using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tepi
{
    // Always-on voice commands, heard offline
    partial class Island
    {
        Hear hear;
        bool hearBusy, hearStale;
        // "Tepi, …": recording, then transcribing
        bool hearTurn;
        DateTime hearRetryUtc, hearTurnUtc;
        readonly System.Windows.Forms.Timer hearTick = new System.Windows.Forms.Timer { Interval = 2000 };

        bool HearOn { get { return settings.GetBool("listen", false); } }

        // Sentences after "Tepi" go to dictation
        bool HearFree { get { return settings.GetBool("listen.free", false) && DictMissing() == null; } }

        // Quiet whenever something else holds the mic
        bool HearWanted { get { return HearOn && micPresent && !micMuted && (dictState == DictState.Off || hearTurn) && !RapatOn && micHolders.Length == 0; } }

        void StartHear()
        {
            hearTick.Tick += delegate { HearCheck(); };
            ApplyHear();
        }

        // Each problem warned once per setting change
        string hearWarned;
        bool hearFreeWarned;

        public void ApplyHear()
        {
            hearStale = true;
            hearWarned = null;
            hearFreeWarned = false;
            if (HearOn) { hearTick.Start(); HearCheck(); }
            else { hearTick.Stop(); StopHear(); }
        }

        void StopHear()
        {
            if (hearTurn && dictState == DictState.Listening) EndHearTurn();
            var h = hear;
            hear = null;
            if (h != null) ThreadPool.QueueUserWorkItem(delegate { h.Dispose(); });
        }

        void HearCheck()
        {
            // Mic vanished mid-capture
            if (hearTurn && dictState == DictState.Listening && (DateTime.UtcNow - hearTurnUtc).TotalSeconds > 15) EndHearTurn();
            if (hearStale) { hearStale = false; StopHear(); }
            if (!HearWanted) { StopHear(); return; }
            if (Needs.MicDenied()) { StopHear(); HearWarn(Hear.NoMic); return; }
            if (hear != null && settings.GetBool("listen.debug", false)) Log.Write("dengar level " + hear.Level.ToString("0.00"));
            if (hear != null || hearBusy || DateTime.UtcNow < hearRetryUtc) return;
            hearBusy = true;
            bool wake = settings.GetBool("listen.wake", true), comms = settings.GetBool("listen.aec", true), trace = settings.GetBool("listen.debug", false), free = HearFree;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var h = new Hear();
                h.Heard += (cmd, conf, secs) => Post(delegate { OnHeard(h, cmd, conf, secs); });
                h.Called += (conf, start, end) => Post(delegate { OnCalled(h, conf, start, end); });
                h.Trace += s => { if (trace || s.StartsWith("perintah") || s.StartsWith("sistem") || s.StartsWith("volume") || s.StartsWith("panggil") || s.StartsWith("aplikasi")) Log.Write("dengar? " + s); };
                List<KeyValuePair<string, string>> apps = null;
                try { apps = SpeakableApps(); }
                catch (Exception ex) { Log.Write("dengar aplikasi: " + ex.Message); }
                string error = h.Start(wake, comms, free, apps);
                Post(delegate
                {
                    hearBusy = false;
                    if (error == null && HearWanted && !hearStale) { hear = h; hearWarned = null; HearFreeCheck(); return; }
                    if (error != null) { Log.Write("dengar: " + error); hearRetryUtc = DateTime.UtcNow.AddSeconds(30); HearWarn(error); }
                    ThreadPool.QueueUserWorkItem(delegate { h.Dispose(); });
                });
            });
        }

        // Retried every 30 s; said once
        void HearWarn(string error)
        {
            if (error == hearWarned) return;
            hearWarned = error;
            if (error == Hear.NoEnglish) DictSay(Lang.T("Pengenal ucapan Inggris belum ada"), Lang.T("Klik untuk memasang"), false, OpenSiap);
            else if (error == Hear.NoMic) { if (Needs.MicDenied()) DictSay(Lang.T("Windows menolak akses mikrofon"), Lang.T("Klik untuk membuka izinnya"), false, OpenMicPrivacy); }
            else DictSay(Lang.T("Dengarkan terus gagal mulai"), Lang.T("Dicoba lagi tiap 30 detik"), false);
        }

        // Switched on, yet nothing can transcribe
        void HearFreeCheck()
        {
            if (hearFreeWarned || !settings.GetBool("listen.free", false)) return;
            string missing = DictMissing();
            if (missing == null) return;
            hearFreeWarned = true;
            DictSay(Lang.T("Perintah bebas belum jalan"), Lang.T("Klik untuk menyiapkan"), false, OpenSiap);
        }

        // Likely commands need less certainty
        float HearNeed(string key)
        {
            bool playing = mediaInfo.Present && mediaInfo.Playing;
            var c = Perintah.Find(key);
            float need = c != null ? c.Need : 0.65f;
            if (key == "pause") need = playing ? 0.55f : 0.65f;
            else if (key == "play") need = playing ? 0.65f : 0.58f;
            else if (key == "next" || key == "prev") need = mediaInfo.Present ? 0.6f : 0.75f;
            else if (key == "unmute") need = muted ? 0.55f : 0.7f;
            else if (key == "volup" || key == "voldown" || key == "vol") need = 0.6f;
            // No wake word: music needs more certainty
            bool alwaysWake = c != null && c.Wake;
            return alwaysWake || settings.GetBool("listen.wake", true) ? need : need + 0.1f;
        }

        string hearLastCmd;
        DateTime hearLastUtc;
        float hearLastSecs, hearTurnSecs;

        void OnHeard(Hear h, string cmd, float conf, float secs)
        {
            if (h != hear || !HearWanted) return;
            string key = cmd, arg = null;
            int eq = cmd.IndexOf('=');
            if (cmd.StartsWith("open:")) { key = "open"; arg = cmd.Substring(5); }
            else if (eq > 0) key = cmd.Substring(0, eq);
            var known = Perintah.Find(key);
            int n = known != null ? known.Default : 1;
            if (eq > 0 && arg == null) int.TryParse(cmd.Substring(eq + 1), out n);
            if (Math.Round(conf, 2) < HearNeed(key)) return;
            // One utterance, sometimes reported twice
            var now = DateTime.UtcNow;
            if (cmd == hearLastCmd && (now - hearLastUtc).TotalSeconds < 1.5) return;
            // Known command beats the sentence path
            if (hearTurn)
            {
                if ((now - hearTurnUtc).TotalSeconds > 1.5 || dictState != DictState.Listening) return;
                // "Tepi, play Evaluasi": more words than "play"
                if (hearTurnSecs > secs + 0.6f) return;
                EndHearTurn();
            }
            hearLastCmd = cmd;
            hearLastUtc = now;
            hearLastSecs = secs;
            Log.Write("dengar: " + cmd + " " + conf.ToString("0.00"));
            var done = RunCmd(key, n, arg);
            if (done == null) return;
            LogVoice("suruh", cmd, done.Text);
            if (!done.Quiet) HearShow(done);
        }

        void HearShow(CmdDone d)
        {
            var t = ShowToast(View.Info, 2.8, 3, false, true);
            if (t == null) return;
            t.Glyph = d.Glyph;
            t.Title = d.Text;
            t.Detail = d.Detail;
            t.Page = PageVoice;
            t.Data = d.Fix;
        }

        // "Tepi" heard: record the sentence
        void OnCalled(Hear h, float conf, long start, long end)
        {
            if (h != hear || !HearWanted || hearTurn || dictState != DictState.Off || suruhBusy || !HearFree) return;
            // The fast path already took these words
            var now = DateTime.UtcNow;
            float said = (end - start) / (float)Recorder.Rate;
            if ((now - hearLastUtc).TotalSeconds < 1.5 && said < hearLastSecs + 0.6f) return;
            if (!h.Capture(start, end, pcm => Post(delegate { OnCaptured(h, pcm); }))) return;
            Log.Write("dengar: panggil " + conf.ToString("0.00"));
            hearTurn = true;
            hearTurnUtc = now;
            hearTurnSecs = said;
            dictCommand = true;
            dictHandsFree = false;
            dictStartUtc = now;
            Array.Clear(dictLevels, 0, dictLevels.Length);
            dictState = DictState.Listening;
            expanded = false;
            if (settings.GetBool("dictate.duck", true)) volume.Duck(true);
            if (toast != null && toast.View == View.Dictate) DismissToast();
            Kick();
        }

        void EndHearTurn()
        {
            var h = hear;
            if (h != null) h.DropCapture();
            if (dictState == DictState.Listening && settings.GetBool("dictate.duck", true)) volume.Duck(false);
            hearTurn = false;
            dictCommand = false;
            dictState = DictState.Off;
            Kick();
        }

        void OnCaptured(Hear h, byte[] pcm)
        {
            if (!hearTurn || dictState != DictState.Listening) return;
            if (settings.GetBool("dictate.duck", true)) volume.Duck(false);
            dictState = DictState.Working;
            Kick();
            string engine = DictEngine, lang = DictLang();
            var model = DictModel;
            var vocab = words ?? Words.Load();
            bool trace = settings.GetBool("listen.debug", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var clock = Stopwatch.StartNew();
                double secs = pcm.Length / 2.0 / Recorder.Rate;
                string error = null, text = "";
                bool fallback;
                if (secs >= 0.4) text = SpeechText(pcm, engine, lang, model, vocab, "Tepi, ", Words.Style.Plain, true, false, out error, out fallback);
                long ms = clock.ElapsedMilliseconds;
                // Transcript text only when debugging
                Log.Write("dengar: transkrip " + secs.ToString("0.0") + " dtk dalam " + ms + " ms" + (error != null ? ", " + error : trace ? " \"" + text + "\"" : ""));
                Post(delegate { OnTranscribed(text, error); });
            });
        }

        void OnTranscribed(string text, string error)
        {
            if (!hearTurn) return;
            hearTurn = false;
            dictCommand = false;
            dictState = DictState.Off;
            Kick();
            if (error != null) { var fix = SpeechFix(error); DictSay(error, fix != null ? Lang.T("Klik untuk menyiapkan") : "", true, fix); return; }
            string rest;
            if (!ForTepi(text, out rest)) { Log.Write("dengar: bukan untuk Tepi"); return; }
            if (rest.Length == 0 || Words.Phantom(rest)) { DictSay(Lang.T("Ucapkan perintahnya sekalian"), Lang.T("Misalnya \"Tepi, buka Spotify\""), true); return; }
            RunSuruh(rest);
        }

        static readonly Regex WakeLead = new Regex(@"^\W*(?:(?:hey|hei|hai|halo|oke|okay|ok)\W+)?(?<w>[\p{L}']+)\W*(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        static readonly string[] WakeSpellings = { "tepi", "tepih", "tepy", "teppy", "tepie", "tepii", "tepee", "teepee", "tipi", "tippy", "tepis" };
        // What a command starts with, Indonesian or English
        static readonly Regex CommandStart = new Regex(@"^(?:buka|bukain|bukakan|open|launch|jalankan|putar|puter|play|pause|jeda|stop|lanjut|lanjutkan|lanjutin|next|skip|previous|ganti|ingetin|ingatin|ingatkan|ingetkan|remind|set|setel|pasang|timer|catat|tulis|note|nyalakan|nyalain|hidupkan|matikan|matiin|turn|naikkan|naikin|turunkan|turunin|kecilkan|kecilin|besarkan|keraskan|kunci|lock|mute|unmute|volume|kecerahan|brightness|mode|fokus|focus|tambah|tambahkan|add|cari|search)\b", RegexOptions.IgnoreCase);

        // "Tapi" means but: needs a command
        internal static bool ForTepi(string text, out string rest)
        {
            rest = "";
            text = (text ?? "").Trim();
            var m = WakeLead.Match(text);
            if (!m.Success) return false;
            string w = m.Groups["w"].Value.ToLowerInvariant();
            string after = m.Groups["rest"].Value.Trim();
            if (Array.IndexOf(WakeSpellings, w) >= 0) { rest = after; return true; }
            if (w == "tapi") { rest = after; return CommandStart.IsMatch(after); }
            // Transcript lost "Tepi", kept a command
            rest = text;
            return CommandStart.IsMatch(text);
        }
    }
}
