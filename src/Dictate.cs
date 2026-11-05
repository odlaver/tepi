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
        string DictEngine { get { return settings.Get("dictate.engine", "groq"); } }
        Speech.LocalModel DictModel { get { return Speech.Model(settings.Get("dictate.model", "seimbang")); } }
        int HoldVk { get { return settings.Get("dictate.key", "rctrl") == "ralt" ? 0xA5 : 0xA3; } }

        // Empty when ready, else what's missing
        string DictMissing()
        {
            if (DictEngine == "lokal") return Speech.LocalReady(DictModel) ? null : Lang.T("Model offline belum diunduh");
            return string.IsNullOrEmpty(Speech.GroqKey()) ? Lang.T("Key Groq belum diisi") : null;
        }

        // User-triggered: shows even in calls
        void DictSay(string title, string detail, bool force = true)
        {
            var t = ShowToast(View.Info, 2.8, 3, false, force);
            if (t == null) return;
            t.Glyph = Ink.IcoMic;
            t.Title = title;
            t.Detail = detail;
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
            ApplyDictate();
        }

        public void ApplyDictate()
        {
            bool want = DictOn;
            if (want && keyHook == IntPtr.Zero)
            {
                keyProc = OnKey;
                keyHook = SetWindowsHookEx(13, keyProc, GetModuleHandle(null), 0);
                if (keyHook == IntPtr.Zero) Log.Write("dikte hook: " + Marshal.GetLastWin32Error());
            }
            else if (!want && keyHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyHook);
                keyHook = IntPtr.Zero;
                CancelDictate();
            }
            words = Words.Load();
        }

        void StopDictate()
        {
            if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            CancelDictate();
        }

        // Low-level keyboard hook

        [StructLayout(LayoutKind.Sequential)]
        struct KeyInfo { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }

        delegate IntPtr KeyHookProc(int code, IntPtr msg, IntPtr info);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, KeyHookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        IntPtr keyHook;
        KeyHookProc keyProc;

        static bool IsModifier(uint vk) { return (vk >= 0xA0 && vk <= 0xA5) || vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C || vk == 0x14; }

        // UI thread; a throw kills Tepi
        IntPtr OnKey(int code, IntPtr msg, IntPtr info)
        {
            if (code < 0) return CallNextHookEx(keyHook, code, msg, info);
            try { if (KeyEvent(msg, info)) return (IntPtr)1; }
            catch (Exception ex) { Log.Write("dikte kunci: " + ex.Message); }
            return CallNextHookEx(keyHook, code, msg, info);
        }

        // True swallows the key
        bool KeyEvent(IntPtr msg, IntPtr info)
        {
            var k = (KeyInfo)Marshal.PtrToStructure(info, typeof(KeyInfo));
            // Our own Ctrl+V and dummy keys
            if ((k.Flags & 0x10) != 0) return false;
            int m = msg.ToInt32();
            bool up = m == 0x0101 || m == 0x0105;
            var now = DateTime.UtcNow;
            if (k.Vk == HoldVk)
            {
                if (!up)
                {
                    if (keyDown) return false;
                    keyDown = true;
                    keyCombo = false;
                    keyDownUtc = now;
                    if (dictState == DictState.Listening && dictHandsFree) finishOnUp = true;
                    else if (dictState == DictState.Off) { holdTimer.Stop(); holdTimer.Start(); }
                }
                else
                {
                    keyDown = false;
                    holdTimer.Stop();
                    double held = (now - keyDownUtc).TotalSeconds;
                    if (finishOnUp) { finishOnUp = false; FinishDictate(); }
                    else if (dictState == DictState.Listening && !dictHandsFree)
                    {
                        if ((now - dictStartUtc).TotalSeconds < TapSec) CancelDictate();
                        else FinishDictate();
                    }
                    else if (dictState == DictState.Off && !keyCombo && held < TapSec)
                    {
                        // Two quick taps: hands-free
                        if ((now - lastTapUtc).TotalSeconds < DoubleTapSec) { lastTapUtc = DateTime.MinValue; BeginDictate(true); }
                        else lastTapUtc = now;
                    }
                }
                return false;
            }
            if (!up && k.Vk == 0x1B && dictState == DictState.Listening)
            {
                CancelDictate();
                DictSay(Lang.T("Dikte dibatalkan"), "", true);
                return true;
            }
            if (!up && !IsModifier(k.Vk))
            {
                lastTapUtc = DateTime.MinValue;
                if (keyDown)
                {
                    keyCombo = true;
                    holdTimer.Stop();
                    // Ctrl+C and friends, not speech
                    if (dictState == DictState.Listening && !dictHandsFree && (now - dictStartUtc).TotalSeconds < 1.0) CancelDictate();
                }
            }
            return false;
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
            if (missing != null) { DictSay(missing, Lang.T("Atur di Pengaturan › Dikte"), true); return; }
            if (!micPresent) { DictSay(Lang.T("Tidak ada mikrofon"), "", true); return; }
            if (micMuted) { DictSay(Lang.T("Mikrofon mati"), Lang.T("Nyalakan dulu untuk dikte"), true); return; }
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
                    DictSay(Lang.T("Mikrofon tidak bisa dibuka"), Lang.T("Cek izin mikrofon di Windows"), true);
                });
            });
        }

        void CancelDictate()
        {
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
                else
                {
                    var wav = Wav.FromPcm(raw, 0, raw.Length);
                    SpeechResult res;
                    if (engine == "lokal") res = Speech.Local(model, wav, lang, vocab.Prompt());
                    else
                    {
                        string key = Speech.GroqKey();
                        res = string.IsNullOrEmpty(key) ? new SpeechResult { Error = Lang.T("Key Groq belum diisi") } : Speech.Groq(key, wav, lang, vocab.Prompt(), false);
                        // No internet: offline model stands in
                        if (!res.Ok && res.NoNet && Speech.LocalReady(model)) { res = Speech.Local(model, wav, lang, vocab.Prompt()); fallback = res.Ok; }
                    }
                    if (res.Ok) text = vocab.Apply(res.Text, style, fillers, commands);
                    else error = res.Error;
                }
                Post(delegate { DeliverDictate(text, error, fallback); });
            });
        }

        void DeliverDictate(string text, string error, bool fallback)
        {
            dictState = DictState.Off;
            Kick();
            if (error == "") return;
            if (error != null) { DictSay(error, "", true); return; }
            if (dictCommand && !string.IsNullOrEmpty(text)) { RunSuruh(text, true); return; }
            if (string.IsNullOrEmpty(text)) { DictSay(Lang.T("Tidak ada kata yang terdengar"), "", true); return; }
            // Next dictation continues the sentence
            if (!text.EndsWith("\n")) text += " ";
            dictLast = text;
            CountDictated(text);
            LogVoice("dikte", text.Trim(), dictApp);
            PasteText(text, dictTarget, fallback ? Lang.T("Offline, Groq tidak terjangkau") : null);
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

        void PasteText(string text, IntPtr target, string note)
        {
            string preview = text.Trim().Replace("\r", "").Replace('\n', ' ');
            bool gone = target == IntPtr.Zero || !IsWindow(target);
            bool blocked = !gone && !selfElevated && Elevated(target);
            if (gone || blocked || GetAncestor(GetForegroundWindow(), 3) != target)
            {
                // Can't type there: clipboard only
                try { Clipboard.SetText(text); } catch (Exception) { }
                DictSay(Lang.T("Teks disalin"), blocked ? Lang.T("Jendela admin, tempel sendiri dengan Ctrl+V") : Lang.T("Jendela berganti, tempel dengan Ctrl+V"), true);
                return;
            }
            var saved = Snapshot();
            try { Clipboard.SetDataObject(Marked(text), false, 5, 40); }
            catch (Exception ex) { Log.Write("dikte clipboard: " + ex.Message); DictSay(Lang.T("Clipboard sedang dipakai aplikasi lain"), "", true); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < 40 && AnyHeld(0x10, 0x11, 0x12, 0x5B, 0x5C); i++) Thread.Sleep(15);
                bool sent = false;
                if (GetAncestor(GetForegroundWindow(), 3) == target)
                {
                    var keys = new[] { Key(0x11, '\0', 0), Key(0x56, '\0', 0), Key(0x56, '\0', 2), Key(0x11, '\0', 2) };
                    sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT))) == keys.Length;
                }
                // App reads clipboard after the keys
                Thread.Sleep(450);
                Post(delegate
                {
                    if (!sent) { DictSay(Lang.T("Teks disalin"), Lang.T("Tempel dengan Ctrl+V"), true); return; }
                    try { if (saved != null) Clipboard.SetDataObject(saved, false, 5, 40); else Clipboard.Clear(); }
                    catch (Exception ex) { Log.Write("dikte pulihkan clipboard: " + ex.Message); }
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
                dictLevels[dictLevelAt] = rec != null ? rec.Level : 0;
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
            if (dictState == DictState.Listening) return dictHandsFree ? Lang.T("Mendengarkan tanpa tangan. Tekan tombol dikte lagi untuk selesai, Esc untuk batal.") : Lang.T("Mendengarkan. Lepas tombol untuk selesai, Esc untuk batal.");
            if (dictState == DictState.Working) return WorkingLabel;
            return toast == null ? "" : Lang.F("Ditempel: {0}", toast.Title);
        }
    }
}
