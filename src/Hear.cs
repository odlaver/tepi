using System;
using System.Collections.Generic;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading;

namespace Tepi
{
    // Mic samples handed to the recognizer; Read waits for more
    sealed class PcmPipe : Stream
    {
        readonly Queue<byte[]> parts = new Queue<byte[]>();
        readonly object gate = new object();
        byte[] cur;
        int at, queued;
        bool closed;

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        // The recognizer pokes these on a live stream
        public override long Length { get { return long.MaxValue; } }
        public override long Position { get { return 0; } set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { return 0; }
        public override void SetLength(long v) { }

        public override void Write(byte[] buf, int off, int n)
        {
            var copy = new byte[n];
            Buffer.BlockCopy(buf, off, copy, 0, n);
            lock (gate)
            {
                if (closed) return;
                parts.Enqueue(copy);
                queued += n;
                // Recognizer stalled: drop the oldest 10 s
                while (queued > 320000 && parts.Count > 1)
                {
                    int gone = parts.Dequeue().Length;
                    queued -= gone;
                    dropped += gone / 2;
                }
                Monitor.Pulse(gate);
            }
        }

        // Fills the whole ask: a short read looks like the end
        public override int Read(byte[] buf, int off, int n)
        {
            int got = 0;
            lock (gate)
            {
                while (got < n)
                {
                    if (cur == null)
                    {
                        if (parts.Count > 0) { cur = parts.Dequeue(); queued -= cur.Length; at = 0; }
                        else if (closed) break;
                        else { Monitor.Wait(gate, 300); continue; }
                    }
                    int take = Math.Min(n - got, cur.Length - at);
                    Buffer.BlockCopy(cur, at, buf, off + got, take);
                    at += take;
                    got += take;
                    if (at >= cur.Length) cur = null;
                }
            }
            return got;
        }

        public int QueuedMs { get { lock (gate) return queued / 32; } }

        // Samples the recognizer never saw
        long dropped;
        public long Dropped { get { lock (gate) return dropped; } }

        public void Close2()
        {
            lock (gate) { closed = true; Monitor.PulseAll(gate); }
        }
    }


    // Raw mic history; cuts out what follows "Tepi"
    sealed class Tap : Stream
    {
        const int Rate = Recorder.Rate, Frame = Rate / 50, RingLen = Rate * 20;
        // Ending silence and longest command
        const int TailMs = 800, CapMs = 12000, PreMs = 200, PostMs = 300;
        readonly Stream next;
        readonly short[] ring = new short[RingLen];
        readonly object gate = new object();
        long total, framed, capStart, lastSpeech;
        double thr;
        Action<byte[]> done;

        public Tap(Stream next) { this.next = next; }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return 0; } }
        public override long Position { get { return 0; } set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { return 0; }
        public override void SetLength(long v) { }
        public override int Read(byte[] b, int o, int n) { return 0; }

        public override void Write(byte[] buf, int off, int n)
        {
            Action<byte[]> fire = null;
            byte[] clip = null;
            lock (gate)
            {
                for (int i = 0; i + 1 < n; i += 2) ring[total++ % RingLen] = (short)(buf[off + i] | buf[off + i + 1] << 8);
                if (done != null) Scan(out fire, out clip);
            }
            next.Write(buf, off, n);
            if (fire != null) fire(clip);
        }

        // From "Tepi" until the speaker stops
        public void Begin(long start, long from, Action<byte[]> whenDone)
        {
            lock (gate)
            {
                long oldest = Math.Max(0, total - RingLen + Frame);
                start = Math.Max(oldest, Math.Min(total, start - PreMs * Rate / 1000));
                from = Math.Max(start, Math.Min(total, from));
                // Noise before "Tepi", voice during it
                double floor = Percentile(start - 2 * Rate, start, 0.2), voice = Percentile(start, from, 0.8);
                thr = Math.Max(40, Math.Max(floor * 2.5, voice * 0.12));
                capStart = start;
                lastSpeech = framed = from;
                done = whenDone;
            }
        }

        public void FinishNow()
        {
            Action<byte[]> fire;
            byte[] clip;
            lock (gate)
            {
                if (done == null) return;
                fire = done;
                done = null;
                clip = Cut(capStart, total);
            }
            fire(clip);
        }

        public void Cancel() { lock (gate) done = null; }

        void Scan(out Action<byte[]> fire, out byte[] clip)
        {
            fire = null;
            clip = null;
            while (framed + Frame <= total)
            {
                double rms = Rms(framed);
                framed += Frame;
                if (rms > thr) lastSpeech = framed;
                bool still = framed - lastSpeech >= TailMs * Rate / 1000, tooLong = framed - capStart >= CapMs * Rate / 1000;
                if (!still && !tooLong) continue;
                fire = done;
                done = null;
                clip = Cut(capStart, Math.Min(total, lastSpeech + PostMs * Rate / 1000));
                return;
            }
        }

        double Rms(long at)
        {
            double sum = 0;
            for (long i = at; i < at + Frame; i++) { double v = ring[i % RingLen]; sum += v * v; }
            return Math.Sqrt(sum / Frame);
        }

        double Percentile(long from, long to, double q)
        {
            from = Math.Max(from, Math.Max(0, total - RingLen + Frame));
            var list = new List<double>();
            for (long at = from; at + Frame <= to; at += Frame) list.Add(Rms(at));
            if (list.Count == 0) return 0;
            list.Sort();
            return list[(int)Math.Min(list.Count - 1, list.Count * q)];
        }

        byte[] Cut(long from, long to)
        {
            from = Math.Max(from, Math.Max(0, total - RingLen));
            int n = (int)Math.Max(0, to - from);
            var b = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                short v = ring[(from + i) % RingLen];
                b[i * 2] = (byte)v;
                b[i * 2 + 1] = (byte)(v >> 8);
            }
            return b;
        }
    }

    // Quiet mics reach the recognizer lifted, like its own input does
    sealed class Agc : Stream
    {
        readonly Stream[] next;
        double gain = 4, env = 3000;
        const double Floor = 500, Target = 12000, MaxGain = 14;

        public Agc(params Stream[] next) { this.next = next; }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return 0; } }
        public override long Position { get { return 0; } set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { return 0; }
        public override void SetLength(long v) { }
        public override int Read(byte[] b, int o, int n) { return 0; }

        public override void Write(byte[] buf, int off, int n)
        {
            var o = new byte[n];
            int peak = 0;
            for (int i = 0; i + 1 < n; i += 2) peak = Math.Max(peak, Math.Abs((short)(buf[off + i] | buf[off + i + 1] << 8)));
            // Speech-sized blocks steer the gain; hiss does not
            if (peak > Floor)
            {
                env = Math.Max(peak, env * 0.995);
                double want = Math.Max(1, Math.Min(MaxGain, Target / env));
                gain += (want - gain) * (want < gain ? 0.5 : 0.02);
            }
            else env = Math.Max(Floor, env * 0.999);
            for (int i = 0; i + 1 < n; i += 2)
            {
                int v = (int)((short)(buf[off + i] | buf[off + i + 1] << 8) * gain);
                v = Math.Max(-32768, Math.Min(32767, v));
                o[i] = (byte)v;
                o[i + 1] = (byte)(v >> 8);
            }
            foreach (var s in next) s.Write(o, 0, n);
        }
    }

    // Offline command spotting on the Windows recognizer
    sealed class Hear : IDisposable
    {
        static readonly string[] Ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        static readonly string[] Wake = { "Tepi", "hey Tepi", "okay Tepi" };
        static readonly SpeechAudioFormatInfo Pcm16k = new SpeechAudioFormatInfo(Recorder.Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

        // Floor only; the island asks more of unlikely commands
        public const float MinConfidence = 0.45f;
        // The word "Tepi" itself must be clear
        const float CallNeed = 0.58f, CallWord = 0.5f;

        // Command, confidence, seconds spoken
        public event Action<string, float, float> Heard;
        // Confidence, sentence start, sentence end
        public event Action<float, long, long> Called;
        public event Action<string> Trace;

        SpeechRecognitionEngine engine, caller;
        Recorder rec;
        PcmPipe pipe, callPipe;
        Tap tap;
        DateTime startUtc;

        // min..max as spoken
        static Choices Numbers(int min, int max)
        {
            var n = new Choices();
            for (int i = min; i <= max; i++)
            {
                string w = i < 20 ? Ones[i] : i == 100 ? "one hundred" : Tens[i / 10] + (i % 10 == 0 ? "" : " " + Ones[i % 10]);
                n.Add(new SemanticResultValue(w, i));
                if (i == 100) n.Add(new SemanticResultValue("a hundred", 100));
            }
            return n;
        }

        static Grammar Phrases(bool wake, bool system)
        {
            var c = new Choices();
            foreach (var cmd in Perintah.All)
                if (cmd.Say != null && cmd.Wake == system)
                    foreach (var s in cmd.Say) c.Add(new SemanticResultValue(s, cmd.Key));
            var gb = new GrammarBuilder();
            if (wake || system) gb.Append(new Choices(Wake));
            gb.Append(new SemanticResultKey("cmd", c));
            return new Grammar(gb) { Name = system ? "sistem" : "perintah" };
        }

        // "Tepi, set volume to fifty"
        static Grammar Number(string name, bool wake, string[] lead, int min, int max, string unit, bool unitNeeded)
        {
            var gb = new GrammarBuilder();
            if (wake) gb.Append(new Choices(Wake));
            gb.Append(new Choices(lead));
            gb.Append(new SemanticResultKey("num", Numbers(min, max)));
            gb.Append(new GrammarBuilder(new Choices(unit.Split('|'))), unitNeeded ? 1 : 0, 1);
            return new Grammar(gb) { Name = name };
        }

        // "Tepi, open Spotify"
        static Grammar Apps(List<KeyValuePair<string, string>> apps)
        {
            var c = new Choices();
            foreach (var a in apps) c.Add(new SemanticResultValue(a.Key, a.Value));
            var gb = new GrammarBuilder(new Choices(Wake));
            gb.Append(new Choices("open", "launch"));
            gb.Append(new SemanticResultKey("app", c));
            return new Grammar(gb) { Name = "aplikasi" };
        }

        // Fast engine grammars, shared with tests
        internal static void LoadCommands(SpeechRecognitionEngine e, bool wake, List<KeyValuePair<string, string>> apps, Action<string> say)
        {
            e.LoadGrammar(Phrases(wake, false));
            e.LoadGrammar(Phrases(true, true));
            e.LoadGrammar(Number("volume", wake, new[] { "volume", "volume to", "set volume to", "set the volume to", "volume at" }, 0, 100, "percent", false));
            e.LoadGrammar(Number("kecerahan", true, new[] { "brightness", "brightness to", "set brightness to", "set the brightness to" }, 0, 100, "percent", false));
            e.LoadGrammar(Number("timer", true, new[] { "timer", "timer for", "set a timer for", "start a timer for" }, 1, 100, "minutes|minute", true));
            if (apps != null && apps.Count > 0)
            {
                try { e.LoadGrammar(Apps(apps)); }
                catch (Exception ex) { say("aplikasi: " + ex.Message); }
            }
            // No wake word: ordinary talk lands here, not on a command
            if (!wake) e.LoadGrammar(new DictationGrammar { Name = "bebas" });
        }

        internal static Grammar CallGrammar() { return Call(); }

        // Any sentence that opens with "Tepi"
        static Grammar Call()
        {
            var gb = new GrammarBuilder(new Choices(Wake));
            gb.AppendWildcard();
            return new Grammar(gb) { Name = "panggil" };
        }

        // Why Start failed, for the island's warnings
        public const string NoEnglish = "no English recognizer", NoMic = "mic tidak bisa dibuka";

        static RecognizerInfo English()
        {
            if (Sim.Has("noeng")) return null;
            RecognizerInfo any = null;
            foreach (var r in SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (r.Culture.TwoLetterISOLanguageName != "en") continue;
                if (r.Culture.Name == "en-US") return r;
                if (any == null) any = r;
            }
            return any;
        }

        public static bool Available
        {
            get
            {
                try { return English() != null; }
                catch (Exception) { return false; }
            }
        }

        void Say(string s)
        {
            var t = Trace;
            if (t != null) t(s);
        }

        // Speech end to now, and audio still waiting
        string Lag(RecognitionResult r, PcmPipe p)
        {
            if (r.Audio == null) return "";
            var end = startUtc + r.Audio.AudioPosition + r.Audio.Duration;
            return " telat " + (int)(DateTime.UtcNow - end).TotalMilliseconds + " ms antre " + (p == null ? 0 : p.QueuedMs) + " ms";
        }

        // Peak of the mic over the last stretch, 0..1
        public float Level { get { var r = rec; return r == null ? 0 : r.Level; } }

        // Null when listening, else why not
        public string Start(bool wake, bool comms, bool call, List<KeyValuePair<string, string>> apps)
        {
            try
            {
                var info = English();
                if (info == null) return NoEnglish;
                var e = new SpeechRecognitionEngine(info);
                engine = e;
                LoadCommands(e, wake, apps, Say);
                e.SpeechRecognized += OnRecognized;
                e.SpeechRecognitionRejected += OnRejected;
                e.RecognizeCompleted += (s, a) => Say("selesai " + (a.Error != null ? a.Error.Message : a.Cancelled ? "batal" : "ok"));
                e.AudioSignalProblemOccurred += (s, a) => Say("masalah " + a.AudioSignalProblem);
                pipe = new PcmPipe();
                // Separate engine: never outbids known commands
                if (call)
                {
                    caller = new SpeechRecognitionEngine(info);
                    caller.LoadGrammar(Call());
                    caller.SpeechRecognized += OnCalled;
                    callPipe = new PcmPipe();
                }
                tap = new Tap(new Agc(callPipe == null ? new Stream[] { pipe } : new Stream[] { pipe, callPipe }));
                rec = new Recorder(false, tap, comms);
                startUtc = DateTime.UtcNow;
                if (!rec.Start()) { Dispose(); return NoMic; }
                e.SetInputToAudioStream(pipe, Pcm16k);
                // "Tepi stop" may go on to "stop it": don't wait long to find out
                e.EndSilenceTimeout = TimeSpan.FromMilliseconds(150);
                e.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(250);
                e.RecognizeAsync(RecognizeMode.Multiple);
                if (caller != null)
                {
                    caller.SetInputToAudioStream(callPipe, Pcm16k);
                    caller.RecognizeAsync(RecognizeMode.Multiple);
                }
                return null;
            }
            catch (Exception ex)
            {
                Dispose();
                return ex.Message;
            }
        }

        void OnRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            var r = e.Result;
            if (r == null || r.Grammar == null) return;
            Say(r.Grammar.Name + " " + r.Confidence.ToString("0.00") + " \"" + r.Text + "\" mic " + Level.ToString("0.00") + Lag(r, pipe));
            if (r.Confidence < MinConfidence) return;
            string cmd = CommandOf(r);
            var h = Heard;
            if (h != null && !string.IsNullOrEmpty(cmd)) h(cmd, r.Confidence, r.Audio == null ? 0 : (float)r.Audio.Duration.TotalSeconds);
        }

        // "pause", "vol=50", "open:Spotify"; null when none
        internal static string CommandOf(RecognitionResult r)
        {
            string name = r.Grammar.Name;
            if ((name == "perintah" || name == "sistem") && r.Semantics.ContainsKey("cmd")) return Convert.ToString(r.Semantics["cmd"].Value);
            if (name == "aplikasi" && r.Semantics.ContainsKey("app")) return "open:" + Convert.ToString(r.Semantics["app"].Value);
            if (!r.Semantics.ContainsKey("num")) return null;
            string key = name == "volume" ? "vol" : name == "kecerahan" ? "bright" : name == "timer" ? "timer" : null;
            return key == null ? null : key + "=" + Convert.ToString(r.Semantics["num"].Value);
        }

        // Clear enough to start recording the sentence
        internal static bool CallHeard(RecognitionResult r, out float word)
        {
            word = 0;
            foreach (var w in r.Words) if (w.Text == "Tepi") word = Math.Max(word, w.Confidence);
            return r.Confidence >= CallNeed && word >= CallWord;
        }

        void OnCalled(object sender, SpeechRecognizedEventArgs e)
        {
            var r = e.Result;
            if (r == null || r.Audio == null) return;
            float word;
            bool heard = CallHeard(r, out word);
            Say("panggil " + r.Confidence.ToString("0.00") + " tepi " + word.ToString("0.00") + " mic " + Level.ToString("0.00") + Lag(r, callPipe));
            if (!heard) return;
            var p = callPipe;
            long start = (long)(r.Audio.AudioPosition.TotalSeconds * Recorder.Rate) + (p == null ? 0 : p.Dropped);
            long end = start + (long)(r.Audio.Duration.TotalSeconds * Recorder.Rate);
            var h = Called;
            if (h != null) h(Math.Min(r.Confidence, word), start, end);
        }

        void OnRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            if (e.Result != null) Say("ditolak " + e.Result.Confidence.ToString("0.00") + " \"" + e.Result.Text + "\" mic " + Level.ToString("0.00"));
        }

        // Record from the "Tepi" sentence onward
        public bool Capture(long start, long end, Action<byte[]> done)
        {
            var t = tap;
            if (t == null) return false;
            t.Begin(start, end, done);
            return true;
        }

        public void EndCapture() { var t = tap; if (t != null) t.FinishNow(); }

        public void DropCapture() { var t = tap; if (t != null) t.Cancel(); }

        public void Dispose()
        {
            var e = engine;
            var c = caller;
            engine = caller = null;
            var r = rec;
            rec = null;
            var t = tap;
            tap = null;
            if (t != null) t.Cancel();
            foreach (var p in new[] { pipe, callPipe }) if (p != null) p.Close2();
            pipe = callPipe = null;
            if (e != null)
            {
                try { e.SpeechRecognized -= OnRecognized; e.SpeechRecognitionRejected -= OnRejected; e.RecognizeAsyncCancel(); } catch (Exception) { }
                try { e.Dispose(); } catch (Exception) { }
            }
            if (c != null)
            {
                try { c.SpeechRecognized -= OnCalled; c.RecognizeAsyncCancel(); } catch (Exception) { }
                try { c.Dispose(); } catch (Exception) { }
            }
            if (r != null) { try { r.Dispose(); } catch (Exception) { } }
        }
    }
}
