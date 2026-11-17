using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    enum DictState { Off, Listening, Working }

    // Hold, speak, release: text gets pasted
    partial class Island
    {
        const int DictPasteHotkey = 16;
        const double HoldStartSec = 0.22, TapSec = 0.25, DoubleTapSec = 0.45, MaxDictSec = 300;

        DictState dictState = DictState.Off;
        Recorder dictRec;
        MemoryStream dictPcm;
        IntPtr dictTarget;
        string dictApp = "", dictLast = "";
        bool dictHandsFree, keyDown, keyCombo, finishOnUp, dictCommand;
        DateTime keyDownUtc, lastTapUtc, dictStartUtc;
        Words words;
        readonly System.Windows.Forms.Timer holdTimer = new System.Windows.Forms.Timer { Interval = (int)(HoldStartSec * 1000) };
        readonly System.Windows.Forms.Timer dictCap = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly float[] dictLevels = new float[30];
        int dictLevelAt;
        double dictLevelT;

        bool DictOn { get { return settings.GetBool("dictate", false); } }
        string DictEngine { get { return Ai.SttId; } }
        Speech.LocalModel DictModel { get { return Speech.Model(settings.Get("dictate.model", "seimbang")); } }
        int holdVk = 0xA3;
        int HoldVk { get { return holdVk; } }

        // Empty when ready, else what's missing
        string DictMissing()
        {
            if (DictEngine == "lokal") return Speech.LocalReady(DictModel) ? null : Lang.T("Model offline belum diunduh");
            return Ai.SttMissing();
        }

        // User-triggered: shows even in calls
        void DictSay(string title, string detail, bool force = true, Action fix = null)
        {
            // Fixable: stays long enough to click
            var t = ShowToast(View.Info, fix != null ? 5 : 2.8, 3, false, force);
            if (t == null) return;
            t.Glyph = Ink.IcoMic;
            t.Title = title;
            t.Detail = detail;
            t.Page = PageVoice;
            t.Data = fix;
        }

        string DictLang()
        {
            string l = settings.Get("dictate.lang", Lang.English ? "en" : "id");
            return l == "auto" ? null : l;
        }

        void StartDictate()
        {
            // A mouse button down means Ctrl+click
            holdTimer.Tick += delegate { holdTimer.Stop(); if (keyDown && !keyCombo && dictState == DictState.Off && !AnyHeld(0x01, 0x02, 0x04)) BeginDictate(false); };
            dictCap.Tick += delegate
            {
                if (dictState == DictState.Listening && (DateTime.UtcNow - dictStartUtc).TotalSeconds >= MaxDictSec) FinishDictate();
            };
            keyWatch.Tick += delegate { WatchKey(); };
            ApplyDictate();
        }

        public void ApplyDictate()
        {
            bool want = DictOn;
            holdVk = settings.Get("dictate.key", "rctrl") == "ralt" ? 0xA5 : 0xA3;
            if (want && dictKeys == null)
            {
                dictKeys = new KeyThread("dikte key", OnHookKey);
                dictKeys.Start();
                dictPark = new DesktopPark(delegate { var d = dockWin; return d != null && d.WinKeyOn; });
                dictPark.Start();
                keyWatch.Start();
            }
            else if (!want && dictKeys != null)
            {
                keyWatch.Stop();
                dictKeys.Stop();
                dictKeys = null;
                if (dictPark != null) { dictPark.Stop(); dictPark = null; }
                keyDown = false;
                CancelDictate();
            }
            words = Words.Load();
        }

        void StopDictate()
        {
            keyWatch.Stop();
            if (dictKeys != null) { dictKeys.Stop(); dictKeys = null; }
            if (dictPark != null) { dictPark.Stop(); dictPark = null; }
            CancelDictate();
        }

        // Key events: hook thread stays tiny, the UI thread decides

        KeyThread dictKeys;
        DesktopPark dictPark;
        readonly System.Windows.Forms.Timer keyWatch = new System.Windows.Forms.Timer { Interval = 50 };
        volatile bool holdSeen;
        long otherTicks;
        DateTime physSince = DateTime.MinValue;
        bool hookWarned;

        static bool IsModifier(uint vk) { return (vk >= 0xA0 && vk <= 0xA5) || vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C || vk == 0x14; }

        // Hook thread; true swallows the key
        bool OnHookKey(uint vk, bool up)
        {
            var now = DateTime.UtcNow;
            if (vk == (uint)holdVk)
            {
                holdSeen = !up;
                Post(delegate { if (up) HoldUp(now); else HoldDown(now); });
                return false;
            }
            if (up) return false;
            // Esc cancels and stays out of the app
            if (vk == 0x1B && dictState == DictState.Listening) { Post(CancelByEsc); return true; }
            if (IsModifier(vk)) return false;
            Interlocked.Exchange(ref otherTicks, now.Ticks);
            if (holdSeen) Post(delegate { OtherKey(now); });
            return false;
        }

        void HoldDown(DateTime now)
        {
            if (keyDown) return;
            keyDown = true;
            keyCombo = false;
            keyDownUtc = now;
            if (dictState == DictState.Listening && dictHandsFree) finishOnUp = true;
            else if (dictState == DictState.Off) { holdTimer.Stop(); holdTimer.Start(); }
        }

        void HoldUp(DateTime now)
        {
            if (!keyDown) return;
            keyDown = false;
            holdTimer.Stop();
            double held = (now - keyDownUtc).TotalSeconds;
            if (finishOnUp) { finishOnUp = false; FinishDictate(); }
            else if (dictState == DictState.Listening && !dictHandsFree) FinishDictate();
            else if (dictState == DictState.Off && !keyCombo && held < TapSec)
            {
                // Two quick taps with nothing typed between: hands-free
                bool typed = Interlocked.Read(ref otherTicks) > lastTapUtc.Ticks;
                if (!typed && (now - lastTapUtc).TotalSeconds < DoubleTapSec) { lastTapUtc = DateTime.MinValue; BeginDictate(true); }
                else lastTapUtc = now;
            }
        }

        void OtherKey(DateTime now)
        {
            lastTapUtc = DateTime.MinValue;
            if (!keyDown) return;
            keyCombo = true;
            holdTimer.Stop();
            // Ctrl+C and friends, not speech
            if (dictState == DictState.Listening && !dictHandsFree && (now - dictStartUtc).TotalSeconds < 1.0) CancelDictate();
        }

        // The invisible window that holds the focus on an empty desktop
        bool IsParkKeeper(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            var park = dictPark;
            var dock = dockWin;
            return (park != null && park.KeeperHandle == h) || (dock != null && dock.WinKeeper == h);
        }

        void CancelByEsc()
        {
            if (dictState != DictState.Listening) return;
            string said = hearTurn ? Lang.T("Perintah dibatalkan") : Lang.T("Dikte dibatalkan");
            CancelDictate();
            DictSay(said, "", true);
        }

        // Asks the keyboard itself, so a hook Windows dropped cannot leave the key stuck
        void WatchKey()
        {
            holdVk = settings.Get("dictate.key", "rctrl") == "ralt" ? 0xA5 : 0xA3;
            bool down = AnyHeld(holdVk);
            var now = DateTime.UtcNow;
            if (keyDown && !down) { physSince = DateTime.MinValue; HoldUp(now); return; }
            if (keyDown || !down) { physSince = DateTime.MinValue; return; }
            if (physSince == DateTime.MinValue) { physSince = now; return; }
            if ((now - physSince).TotalSeconds < 0.12) return;
            // Held, yet the hook never said so
            if (!hookWarned) { hookWarned = true; Log.Write("dikte: tombol tertekan tapi hook diam, dipasang ulang"); }
            if (dictKeys != null) dictKeys.Rehook();
            HoldDown(physSince);
            physSince = DateTime.MinValue;
        }

        // Recording

        static string ProcessOf(IntPtr hwnd)
        {
            try
            {
                uint pid;
                WindowPid(hwnd, out pid);
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch (Exception) { return ""; }
        }

        void BeginDictate(bool handsFree)
        {
            string missing = DictMissing();
            if (missing != null) { DictSay(missing, Lang.T("Klik untuk menyiapkan"), true, OpenSiap); return; }
            if (!micPresent) { DictSay(Lang.T("Tidak ada mikrofon"), "", true); return; }
            if (micMuted) { DictSay(Lang.T("Mikrofon mati"), Lang.T("Nyalakan dulu untuk dikte"), true); return; }
            if (Needs.MicDenied()) { MicFailSay(); return; }
            dictTarget = GetAncestor(GetForegroundWindow(), 3);
            dictApp = ProcessOf(dictTarget);
            dictHandsFree = handsFree;
            // Shift held too: a command, not text
            dictCommand = !handsFree && AnyHeld(0x10);
            dictStartUtc = DateTime.UtcNow;
            Array.Clear(dictLevels, 0, dictLevels.Length);
            dictState = DictState.Listening;
            expanded = false;
            // Alt released alone opens app menus
            if (HoldVk == 0xA5 && !handsFree) SendInput(2, new[] { Key(0xFF, '\0', 0), Key(0xFF, '\0', 2) }, Marshal.SizeOf(typeof(INPUT)));
            if (settings.GetBool("dictate.duck", true)) volume.Duck(true);
            var pcm = new MemoryStream();
            var rec = new Recorder(false, pcm);
            dictPcm = pcm;
            dictRec = rec;
            dictCap.Start();
            if (toast != null && toast.View == View.Dictate) DismissToast();
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = rec.Start();
                if (!ok) Post(delegate
                {
                    if (dictRec != rec) return;
                    CancelDictate();
                    MicFailSay();
                });
            });
        }

        void CancelDictate()
        {
            if (hearTurn) { EndHearTurn(); return; }
            dictCap.Stop();
            finishOnUp = false;
            var rec = dictRec;
            dictRec = null;
            dictPcm = null;
            if (rec != null) ThreadPool.QueueUserWorkItem(delegate { rec.Dispose(); });
            if (dictState == DictState.Listening && settings.GetBool("dictate.duck", true)) volume.Duck(false);
            dictState = DictState.Off;
            Kick();
        }

        void FinishDictate()
        {
            // "Tepi, …": click ends it early
            if (hearTurn)
            {
                if (dictState == DictState.Listening && hear != null) hear.EndCapture();
                return;
            }
            var rec = dictRec;
            var pcm = dictPcm;
            if (dictState != DictState.Listening || rec == null) return;
            dictCap.Stop();
            dictRec = null;
            dictPcm = null;
            if (settings.GetBool("dictate.duck", true)) volume.Duck(false);
            dictState = DictState.Working;
            Kick();
            string engine = DictEngine, lang = DictLang();
            var model = DictModel;
            var vocab = words;
            var style = !dictCommand && settings.GetBool("dictate.style", true) ? Words.StyleFor(dictApp) : Words.Style.Plain;
            bool fillers = settings.GetBool("dictate.fillers", true), commands = settings.GetBool("dictate.commands", true);
            ThreadPool.QueueUserWorkItem(delegate
            {
                rec.Dispose();
                byte[] raw = pcm.ToArray();
                string text = null, error = null;
                bool fallback = false;
                // Short and silent: Ctrl+click, not speech
                double secs = raw.Length / 2.0 / Recorder.Rate;
                if (rec.Peak < 0.18f || secs < 0.5) error = secs < 2 ? "" : Lang.T("Tidak ada suara terdengar");
                else text = SpeechText(raw, engine, lang, model, vocab, "", style, fillers, commands, out error, out fallback);
                Post(delegate { DeliverDictate(text, error, fallback); });
            });
        }

        // Dictation and "Tepi, …" share the engine
        static string SpeechText(byte[] raw, string engine, string lang, Speech.LocalModel model, Words vocab, string hint, Words.Style style, bool fillers, bool commands, out string error, out bool fallback)
        {
            error = null;
            fallback = false;
            var wav = Wav.FromPcm(raw, 0, raw.Length);
            string prompt = hint + vocab.Prompt();
            SpeechResult res;
            if (engine == "lokal") res = Speech.Local(model, wav, lang, prompt);
            else
            {
                res = Ai.Transcribe(wav, lang, prompt, false);
                // No internet: offline model stands in
                if (!res.Ok && res.NoNet && Speech.LocalReady(model)) { res = Speech.Local(model, wav, lang, prompt); fallback = res.Ok; }
            }
            if (!res.Ok) { error = res.Error; return null; }
            return vocab.Apply(res.Text, style, fillers, commands);
        }

        void DeliverDictate(string text, string error, bool fallback)
        {
            dictState = DictState.Off;
            Kick();
            if (error == "") return;
            if (error != null) { var fix = SpeechFix(error); DictSay(error, fix != null ? Lang.T("Klik untuk menyiapkan") : "", true, fix); return; }
            if (dictCommand && !string.IsNullOrEmpty(text)) { RunSuruh(text); return; }
            if (string.IsNullOrEmpty(text)) { DictSay(Lang.T("Tidak ada kata yang terdengar"), "", true); return; }
            // Next dictation continues the sentence
            if (!text.EndsWith("\n")) text += " ";
            dictLast = text;
            CountDictated(text);
            LogVoice("dikte", text.Trim(), dictApp);
            PasteText(text, dictTarget, fallback ? Lang.T("Offline, server suara tidak terjangkau") : null);
        }

        void CountDictated(string text)
        {
            int n = Words.CountWords(text);
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (settings.Get("dictate.day", "") != today) { settings.Set("dictate.day", today); settings.Set("dictate.today", 0); }
            settings.Set("dictate.today", settings.GetInt("dictate.today", 0) + n);
            settings.Set("dictate.words", settings.GetInt("dictate.words", 0) + n);
            settings.Save();
        }

        void PasteLastDictation()
        {
            if (dictLast.Length == 0) { DictSay(Lang.T("Belum ada dikte"), "", true); return; }
            PasteText(dictLast, GetAncestor(GetForegroundWindow(), 3), null);
        }

        // Pasting through the clipboard

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int got);

        // Admin windows ignore our keystrokes
        static bool Elevated(IntPtr hwnd)
        {
            uint pid;
            WindowPid(hwnd, out pid);
            IntPtr proc = OpenProcess(0x1000, false, pid);
            if (proc == IntPtr.Zero) return false;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(proc, 0x0008, out token)) return true;
                try { int e, got; return GetTokenInformation(token, 20, out e, 4, out got) && e != 0; }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(proc); }
        }

        static readonly bool selfElevated = IsAdmin();

        static bool IsAdmin()
        {
            try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        static DataObject Marked(string text)
        {
            var d = new DataObject();
            d.SetText(text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"), TextDataFormat.UnicodeText);
            // Hidden from clipboard history
            d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
            d.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            return d;
        }

        static DataObject Snapshot()
        {
            try
            {
                var old = Clipboard.GetDataObject();
                if (old == null) return null;
                var keep = new DataObject();
                foreach (var f in old.GetFormats(false))
                {
                    try { var v = old.GetData(f, false); if (v != null) keep.SetData(f, false, v); }
                    catch (Exception) { }
                }
                return keep;
            }
            catch (Exception) { return null; }
        }

        void PasteText(string text, IntPtr target, string note, DataObject restore = null)
        {
            string preview = text.Trim().Replace("\r", "").Replace('\n', ' ');
            bool gone = target == IntPtr.Zero || !IsWindow(target);
            bool blocked = !gone && !selfElevated && Elevated(target);
            // Empty desktop: no text field to type into
            if (!gone && IsParkKeeper(target))
            {
                try { Clipboard.SetText(text); } catch (Exception) { }
                DictSay(Lang.T("Teks disalin"), Lang.T("Tidak ada kolom teks, tempel dengan Ctrl+V"), true);
                return;
            }
            if (gone || blocked || GetAncestor(GetForegroundWindow(), 3) != target)
            {
                // Can't type there: clipboard only
                try { Clipboard.SetText(text); } catch (Exception) { }
                DictSay(Lang.T("Teks disalin"), blocked ? Lang.T("Jendela admin, tempel sendiri dengan Ctrl+V") : Lang.T("Jendela berganti, tempel dengan Ctrl+V"), true);
                return;
            }
            var saved = restore ?? Snapshot();
            try { Clipboard.SetDataObject(Marked(text), false, 5, 40); }
            catch (Exception ex) { Log.Write("dikte clipboard: " + ex.Message); DictSay(Lang.T("Clipboard sedang dipakai aplikasi lain"), "", true); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                var keys = Chord(0x56);
                bool sent = false;
                if (GetAncestor(GetForegroundWindow(), 3) == target)
                    sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT))) == keys.Length;
                // App reads clipboard after the keys
                Thread.Sleep(450);
                Post(delegate
                {
                    if (!sent) { DictSay(Lang.T("Teks disalin"), Lang.T("Tempel dengan Ctrl+V"), true); return; }
                    Restore(saved);
                    var t = ShowToast(View.Dictate, 2.6, 2, false, true);
                    if (t == null) return;
                    t.Title = preview;
                    t.Detail = note ?? "";
                });
            });
        }

        // Island

        string WorkingLabel { get { return suruhBusy || dictCommand ? Lang.T("Mengerjakan…") : Lang.T("Menulis…"); } }

        float DictWidth()
        {
            if (dictState == DictState.Listening) return 236;
            if (dictState == DictState.Working) return p.G == null ? 180 : Clamp(p.Measure(WorkingLabel, Ink.Semi(13.5f)) + 84, 160, 260);
            if (toast == null || p.G == null) return 240;
            float w = p.Measure(toast.Title, Ink.Semi(13.5f));
            if (toast.Detail.Length > 0) w = Math.Max(w, p.Measure(toast.Detail, Ink.Text(11.5f)));
            return Clamp(w + 70, 200, 440);
        }

        float DictHeight() { return dictState == DictState.Off && toast != null && toast.Detail.Length > 0 ? 52 : 38; }

        void DrawDictate(float w, float h, DateTime now)
        {
            float cy = h / 2;
            if (dictState == DictState.Listening)
            {
                // Pulsing dot: the mic is live
                LiveRect(new RectangleF(12, cy - 8, 16, 16), LiveFps, t =>
                {
                    float pulse = Anim.Reduced ? 1 : 0.55f + 0.45f * (float)Math.Abs(Math.Sin((t - dictStartUtc).TotalSeconds * Math.PI * 1.2));
                    p.FillRound(new RectangleF(20 - 4.5f, cy - 4.5f, 9, 9), 4.5f, Ink.A(p.T.Negative, pulse));
                });
                if (dictHandsFree) p.Icon(Ink.IcoLock, 11f, p.T.Sub, 38, cy);
                float x0 = dictHandsFree ? 50 : 36, x1 = w - 58;
                // Command mode says so before the bars
                if (dictCommand) { p.Text(Lang.T("Suruh"), Ink.Semi(12.5f), p.T.Text, x0, cy, 60, StringAlignment.Near); x0 += p.Measure(Lang.T("Suruh"), Ink.Semi(12.5f)) + 10; }
                LiveRect(new RectangleF(x0, cy - 12, x1 - x0, 24), LiveFps, t => DictBars(x0, x1, cy, t));
                var clockAt = new RectangleF(w - 54, cy - 10, 40, 20);
                LiveRect(clockAt, 2, t =>
                {
                    var d = t - dictStartUtc;
                    p.TextRight(((int)d.TotalMinutes) + ":" + d.Seconds.ToString("00"), Ink.Semi(12.5f), p.T.Sub, clockAt.Right, cy);
                });
                AddHit(new RectangleF(0, 0, w, h), "dictStop", FinishDictate);
                return;
            }
            if (dictState == DictState.Working)
            {
                LiveRect(new RectangleF(11, cy - 9, 18, 18), LiveFps, t => p.Orbit(20, cy, 7.5f, orbitPhase, p.T.Text));
                p.Text(WorkingLabel, Ink.Semi(13.5f), p.T.Text, 38, cy, w - 52, StringAlignment.Near);
                return;
            }
            var tt = toast;
            if (tt == null) return;
            bool two = tt.Detail.Length > 0;
            float ty = two ? cy - 8 : cy;
            p.Icon('\uE73E', 14f, p.T.Text, 21, cy);
            // Words sweep in left to right
            var area = new RectangleF(38, ty - 11, w - 52, 22);
            LiveRect(area, t =>
            {
                float k = Anim.Reduced ? 1 : Ease.OutCubic((float)((t - tt.ShownUtc).TotalSeconds / 0.5));
                DictSweep(tt.Title, area, ty, k);
                return k < 1 ? 1.0 / LiveFps : 0;
            });
            if (two) p.Text(tt.Detail, Ink.Text(11.5f), p.T.Sub, 38, cy + 10, w - 52, StringAlignment.Near);
        }

        void DictSweep(string text, RectangleF area, float cy, float k)
        {
            if (k >= 1) { p.Text(text, Ink.Semi(13.5f), p.T.Text, area.X, cy, area.Width, StringAlignment.Near); return; }
            float edge = 36, front = area.X + (area.Width + edge) * k;
            using (var b = new LinearGradientBrush(new PointF(front - edge, 0), new PointF(front + 0.01f, 0), p.T.Text, Color.FromArgb(0, p.T.Text)))
            {
                b.WrapMode = WrapMode.TileFlipX;
                var st = g.Save();
                g.SetClip(new RectangleF(area.X - 1, area.Y, front - area.X + 1, area.Height), CombineMode.Intersect);
                p.Text(text, Ink.Semi(13.5f), b, area.X, cy, area.Width, StringAlignment.Near);
                g.Restore(st);
            }
        }

        // Voice level as a rolling strip
        void DictBars(float x0, float x1, float cy, DateTime t)
        {
            var rec = dictRec;
            double secs = (t - dictStartUtc).TotalSeconds;
            if (secs - dictLevelT >= 1.0 / 20)
            {
                dictLevelT = secs;
                var h = hear;
                dictLevels[dictLevelAt] = rec != null ? rec.Level : hearTurn && h != null ? h.Level : 0;
                dictLevelAt = (dictLevelAt + 1) % dictLevels.Length;
            }
            int n = dictLevels.Length;
            float gap = (x1 - x0) / n, bw = Math.Min(2.6f, gap * 0.55f);
            for (int i = 0; i < n; i++)
            {
                float v = dictLevels[(dictLevelAt + i) % n];
                float bh = 3 + v * 17;
                float x = x0 + i * gap + (gap - bw) / 2;
                // Older bars fade toward the left
                p.FillRound(new RectangleF(x, cy - bh / 2, bw, bh), bw / 2, Ink.A(p.T.Text, 0.35f + 0.65f * i / (n - 1)));
            }
        }

        string DescribeDictate()
        {
            if (dictState == DictState.Listening && hearTurn) return Lang.T("Mendengarkan perintah setelah \"Tepi\". Diam sebentar untuk selesai.");
            if (dictState == DictState.Listening) return dictHandsFree ? Lang.T("Mendengarkan tanpa tangan. Tekan tombol dikte lagi untuk selesai, Esc untuk batal.") : Lang.T("Mendengarkan. Lepas tombol untuk selesai, Esc untuk batal.");
            if (dictState == DictState.Working) return WorkingLabel;
            return toast == null ? "" : Lang.F("Ditempel: {0}", toast.Title);
        }
    }
}
