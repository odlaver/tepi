using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace Tepi
{
    // Call transcript: mic you, speakers them
    partial class Island
    {
        const int RapatHotkey = 17;
        const double RapatMaxHours = 3, ChunkSec = 600;

        Recorder rapatMic, rapatSys;
        FileStream rapatMicFile, rapatSysFile;
        DateTime rapatStartUtc;
        string rapatApp = "";
        // Writing the transcript: done of total pieces
        volatile int rapatDone, rapatTotal;
        bool rapatWriting;

        bool RapatOn { get { return rapatMic != null; } }

        static string RapatDir { get { return Path.Combine(Settings.Dir, "rapat"); } }
        static string RapatTmp { get { return Path.Combine(RapatDir, "sementara"); } }

        void ToggleRapat()
        {
            if (RapatOn) StopRapat();
            else StartRapat();
        }

        void StartRapat()
        {
            if (RapatOn || rapatWriting) { if (rapatWriting) DictSay(Lang.T("Transkrip sebelumnya masih ditulis"), "", true); return; }
            string missing = DictMissing();
            if (missing != null) { DictSay(missing, Lang.T("Atur di Pengaturan › Dikte"), true); return; }
            if (!micPresent) { DictSay(Lang.T("Tidak ada mikrofon"), "", true); return; }
            try
            {
                // Leftovers from a crash
                if (Directory.Exists(RapatTmp)) Directory.Delete(RapatTmp, true);
                Directory.CreateDirectory(RapatTmp);
                rapatMicFile = File.Create(Path.Combine(RapatTmp, "kamu.pcm"));
                rapatSysFile = File.Create(Path.Combine(RapatTmp, "lawan.pcm"));
            }
            catch (Exception ex) { Log.Write("rapat mulai: " + ex.Message); DictSay(Lang.T("Folder rapat tidak bisa ditulis"), "", true); return; }
            rapatMic = new Recorder(false, rapatMicFile);
            rapatSys = new Recorder(true, rapatSysFile);
            rapatStartUtc = DateTime.UtcNow;
            rapatApp = micApps.Length > 0 ? micApps[0] : "";
            var mic = rapatMic;
            var sys = rapatSys;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool a = mic.Start(), b = sys.Start();
                Post(delegate
                {
                    if (rapatMic != mic) return;
                    if (!a) { AbortRapat(); DictSay(Lang.T("Mikrofon tidak bisa dibuka"), Lang.T("Cek izin mikrofon di Windows"), true); return; }
                    if (!b) Log.Write("rapat: audio sistem tidak terbuka");
                });
            });
            var t = ShowToast(View.Info, 5, 3, false, true);
            if (t != null)
            {
                t.Glyph = Ink.IcoMic;
                t.Title = Lang.T("Rapat direkam");
                t.Detail = Lang.T("Beri tahu peserta lain");
            }
            Kick();
        }

        void AbortRapat()
        {
            var mic = rapatMic;
            var sys = rapatSys;
            var files = TakeRapatFiles();
            rapatMic = rapatSys = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (mic != null) mic.Dispose();
                if (sys != null) sys.Dispose();
                foreach (var f in files)
                {
                    if (f == null) continue;
                    string path = f.Name;
                    try { f.Dispose(); File.Delete(path); } catch (Exception) { }
                }
            });
            Kick();
        }

        // Worker closes these, not the next
        FileStream[] TakeRapatFiles()
        {
            var files = new[] { rapatMicFile, rapatSysFile };
            rapatMicFile = rapatSysFile = null;
            return files;
        }

        // Once a second while recording
        void TickRapat()
        {
            if (RapatOn && (DateTime.UtcNow - rapatStartUtc).TotalHours >= RapatMaxHours) StopRapat();
        }

        void StopRapat()
        {
            if (!RapatOn) return;
            var mic = rapatMic;
            var sys = rapatSys;
            var files = TakeRapatFiles();
            rapatMic = rapatSys = null;
            rapatWriting = true;
            rapatDone = 0;
            rapatTotal = 0;
            var started = rapatStartUtc.ToLocalTime();
            string app = rapatApp, engine = DictEngine, lang = DictLang();
            var model = DictModel;
            var vocab = words ?? Words.Load();
            bool summary = settings.GetBool("rapat.summary", true);
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                mic.Dispose();
                sys.Dispose();
                foreach (var f in files) if (f != null) f.Dispose();
                string path = null, error = null;
                try { path = WriteRapat(started, app, engine, lang, model, vocab, summary, out error); }
                catch (Exception ex) { Log.Write("rapat tulis: " + ex); error = Lang.T("Transkrip gagal ditulis"); }
                try { Directory.Delete(RapatTmp, true); } catch (Exception) { }
                Post(delegate { RapatWritten(path, error); });
            });
        }

        void RapatWritten(string path, string error)
        {
            rapatWriting = false;
            transcriptsReadUtc = DateTime.MinValue;
            Kick();
            if (path == null) { DictSay(error ?? Lang.T("Transkrip gagal ditulis"), "", true); return; }
            if (settings.GetBool("shelf", true)) AddToShelf(new[] { path });
            var t = ShowToast(View.Info, 8, 3, true, true);
            if (t == null) return;
            t.Glyph = '\uE8A5';
            t.Title = Lang.T("Transkrip rapat siap");
            t.Detail = error ?? Lang.T("Klik untuk membuka");
            t.Data = new Action(delegate { try { Process.Start(path); } catch (Exception ex) { Log.Write("buka transkrip: " + ex.Message); } });
        }

        class Line
        {
            public double Start, End;
            public bool Mine;
            public string Text;
        }

        // Worker: both tracks to one Markdown file
        string WriteRapat(DateTime started, string app, string engine, string lang, Speech.LocalModel model, Words vocab, bool wantSummary, out string note)
        {
            note = null;
            string micPath = Path.Combine(RapatTmp, "kamu.pcm"), sysPath = Path.Combine(RapatTmp, "lawan.pcm");
            long micBytes = File.Exists(micPath) ? new FileInfo(micPath).Length : 0, sysBytes = File.Exists(sysPath) ? new FileInfo(sysPath).Length : 0;
            double seconds = Math.Max(micBytes, sysBytes) / 2.0 / Recorder.Rate;
            if (seconds < 3) { note = Lang.T("Rekaman terlalu pendek"); return null; }
            int chunkBytes = (int)(ChunkSec * Recorder.Rate * 2);
            rapatTotal = (int)Math.Ceiling(micBytes / (double)chunkBytes) + (int)Math.Ceiling(sysBytes / (double)chunkBytes);
            string key = engine == "lokal" ? null : Speech.GroqKey();
            var lines = new List<Line>();
            string fail = null;
            foreach (var track in new[] { new { Path = micPath, Mine = true }, new { Path = sysPath, Mine = false } })
            {
                if (!File.Exists(track.Path)) continue;
                using (var f = File.OpenRead(track.Path))
                {
                    var buf = new byte[chunkBytes];
                    for (int i = 0; ; i++)
                    {
                        int n = ReadFull(f, buf);
                        if (n <= 0) break;
                        rapatDone++;
                        Post(Kick);
                        // Quiet stretches cost nothing
                        if (Loudness(buf, n) < 0.004) continue;
                        var wav = Wav.FromPcm(buf, 0, n);
                        var res = engine == "lokal" ? Speech.Local(model, wav, lang, vocab.Prompt()) : Speech.Groq(key, wav, lang, vocab.Prompt(), true);
                        if (!res.Ok && res.NoNet && Speech.LocalReady(model)) res = Speech.Local(model, wav, lang, vocab.Prompt());
                        if (!res.Ok) { fail = res.Error; continue; }
                        double at = i * ChunkSec;
                        foreach (var s in res.Segments)
                        {
                            if (Words.Phantom(s.Text)) continue;
                            lines.Add(new Line { Start = at + s.Start, End = at + s.End, Mine = track.Mine, Text = s.Text });
                        }
                    }
                }
            }
            DropEcho(lines);
            lines.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (lines.Count == 0) { note = fail ?? Lang.T("Tidak ada percakapan yang terdengar"); return null; }

            var transcript = new StringBuilder();
            Line prev = null;
            foreach (var l in lines)
            {
                // Same speaker, short pause: one paragraph
                if (prev != null && prev.Mine == l.Mine && l.Start - prev.End < 4) { transcript.Append(' ').Append(l.Text); prev = l; continue; }
                if (prev != null) transcript.Append("\n\n");
                transcript.Append("**").Append(l.Mine ? Lang.T("Kamu") : Lang.T("Lawan bicara")).Append("** · ").Append(Clock(l.Start)).Append("  \n").Append(l.Text);
                prev = l;
            }

            string summary = null;
            if (wantSummary)
            {
                string k = Speech.GroqKey();
                if (string.IsNullOrEmpty(k)) note = Lang.T("Tanpa ringkasan: key Groq belum diisi");
                else
                {
                    string err;
                    summary = Summarize(k, transcript.ToString(), out err);
                    if (summary == null) note = Lang.F("Tanpa ringkasan: {0}", err);
                }
            }
            if (fail != null && note == null) note = Lang.F("Sebagian tidak tertulis: {0}", fail);

            var md = new StringBuilder();
            string title = (app.Length > 0 ? Lang.F("Rapat {0}", app) : Lang.T("Rapat")) + ", " + started.ToString(Lang.English ? "MMM d, yyyy HH:mm" : "d MMM yyyy HH.mm", Lang.English ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo("id-ID"));
            md.Append("# ").Append(title).Append("\n\n");
            md.Append(Lang.F("Durasi {0}. Kamu = mikrofon laptop ini, Lawan bicara = suara dari speaker atau headset.", Clock(seconds))).Append("\n\n");
            if (summary != null) md.Append(summary.Trim()).Append("\n\n");
            md.Append("## ").Append(Lang.T("Transkrip")).Append("\n\n").Append(transcript).Append('\n');
            Directory.CreateDirectory(RapatDir);
            string name = started.ToString("yyyy-MM-dd HH.mm") + (app.Length > 0 ? " " + SafeName(app) : "") + ".md";
            string path = Path.Combine(RapatDir, name);
            File.WriteAllText(path, md.ToString(), new UTF8Encoding(false));
            return path;
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            return s.Trim();
        }

        static int ReadFull(Stream s, byte[] buf)
        {
            int got = 0, n;
            while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
            return got - (got & 1);
        }

        // Mean absolute level, 0..1
        static double Loudness(byte[] pcm, int n)
        {
            long sum = 0;
            int count = 0;
            for (int i = 0; i + 1 < n; i += 64) { sum += Math.Abs((short)(pcm[i] | pcm[i + 1] << 8)); count++; }
            return count == 0 ? 0 : sum / (double)count / 32768;
        }

        static string Clock(double s)
        {
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }

        // Drop speaker echo heard by mic
        static void DropEcho(List<Line> lines)
        {
            var theirs = lines.FindAll(l => !l.Mine);
            lines.RemoveAll(m => m.Mine && theirs.Exists(o => o.Start < m.End + 1 && m.Start < o.End + 1 && Overlap(m.Text, o.Text) >= 0.5));
        }

        static double Overlap(string a, string b)
        {
            var wa = new HashSet<string>(WordsOf(a));
            var wb = new HashSet<string>(WordsOf(b));
            if (wa.Count == 0 || wb.Count == 0) return 0;
            int both = 0;
            foreach (var w in wa) if (wb.Contains(w)) both++;
            return both / (double)Math.Min(wa.Count, wb.Count);
        }

        static IEnumerable<string> WordsOf(string s)
        {
            foreach (var w in System.Text.RegularExpressions.Regex.Split((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+"))
                if (w.Length > 1) yield return w;
        }

        static string Summarize(string key, string transcript, out string error)
        {
            // Long calls: keep head and tail
            const int Max = 60000;
            if (transcript.Length > Max) transcript = transcript.Substring(0, Max / 2) + "\n\n[…]\n\n" + transcript.Substring(transcript.Length - Max / 2);
            string system = Lang.English
                ? "You turn call transcripts into notes. Write in the transcript's language. Use exactly these Markdown sections: \"## Summary\" (3 to 5 sentences), \"## Key points\" (bullets), \"## To-do\" (bullets as \"- [ ] who: what\", or \"- none\"). \"Kamu\" is the note taker, \"Lawan bicara\" is everyone else. Only state what the transcript says."
                : "Kamu mengubah transkrip rapat menjadi catatan. Tulis dalam bahasa transkrip. Pakai persis bagian Markdown ini: \"## Ringkasan\" (3 sampai 5 kalimat), \"## Poin penting\" (butir), \"## To-do\" (butir \"- [ ] siapa: apa\", atau \"- tidak ada\"). \"Kamu\" adalah pencatat, \"Lawan bicara\" adalah peserta lain. Hanya tulis yang ada di transkrip.";
            return Speech.GroqChat(key, Speech.ChatModel(key), system, transcript, out error);
        }

        // Island: recording, then writing

        float RapatWidth() { return rapatWriting ? 230 : (p.G == null ? 220 : Clamp(p.Measure(RapatTitle(), Ink.Semi(12.5f)) + 120, 200, 340)); }

        string RapatTitle() { return rapatApp.Length > 0 ? Lang.F("Merekam {0}", rapatApp) : Lang.T("Merekam rapat"); }

        void DrawRapat(float w, float h)
        {
            float cy = h / 2;
            if (rapatWriting)
            {
                LiveRect(new RectangleF(11, cy - 9, 18, 18), LiveFps, t => p.Orbit(20, cy, 7.5f, orbitPhase, p.T.Text));
                string s = rapatTotal > 0 ? Lang.F("Menulis transkrip {0}/{1}", Math.Min(rapatDone, rapatTotal), rapatTotal) : Lang.T("Menulis transkrip…");
                p.Text(s, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 52, StringAlignment.Near);
                return;
            }
            // Slower pulse than dictation
            LiveRect(new RectangleF(12, cy - 8, 16, 16), LiveFps, t =>
            {
                float pulse = Anim.Reduced ? 1 : 0.6f + 0.4f * (float)Math.Abs(Math.Sin((t - rapatStartUtc).TotalSeconds * Math.PI * 0.5));
                p.FillRound(new RectangleF(20 - 4.5f, cy - 4.5f, 9, 9), 4.5f, Ink.A(p.T.Negative, pulse));
            });
            p.Text(RapatTitle(), Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - 74, StringAlignment.Near);
            var at = new RectangleF(w - 78, cy - 10, 60, 20);
            LiveRect(at, TickFps, t => p.TextRight(Clock((t - rapatStartUtc).TotalSeconds), Ink.Semi(12f), p.T.Sub, at.Right, cy));
            // The dot stops it
            AddHit(new RectangleF(0, 0, 38, h), "rapatStop", StopRapat);
        }
    }
}
