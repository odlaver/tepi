using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Tepi
{
    // Lock keys, read without a keyboard hook
    partial class Island
    {
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, IntPtr[] list);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

        System.Windows.Forms.Timer locksTimer;
        bool capsOn, numOn, scrollOn, locksRead, capsPref = true;

        void StartLocks()
        {
            locksTimer = new System.Windows.Forms.Timer { Interval = 120 };
            locksTimer.Tick += delegate { ReadLocks(); };
            locksTimer.Start();
        }

        static bool Toggled(int vk) { return (GetKeyState(vk) & 1) != 0; }

        void ReadLocks()
        {
            // Asleep: resync quietly on wake
            if (dormant) { locksRead = false; return; }
            bool caps = Toggled(0x14), num = Toggled(0x90), scroll = Toggled(0x91);
            bool pref = settings.GetBool("keys.caps", true), others = settings.GetBool("keys.num", false);
            if (!locksRead || pref != capsPref)
            {
                capsOn = caps; numOn = num; scrollOn = scroll; capsPref = pref; locksRead = true;
                Kick();
                return;
            }
            if (caps != capsOn)
            {
                capsOn = caps;
                if (pref) ShowLock("Caps Lock", "A", caps);
                Kick();
            }
            if (num != numOn) { numOn = num; if (others) ShowLock("Num Lock", "1", num); }
            if (scroll != scrollOn) { scrollOn = scroll; if (others) ShowLock("Scroll Lock", "S", scroll); }
            ReadLayout();
        }

        // Input language of the front window
        IntPtr layoutWin, layoutNow;
        int layoutsCheckedTick;
        bool manyLayouts;
        readonly Dictionary<long, string[]> layoutNames = new Dictionary<long, string[]>();

        void ReadLayout()
        {
            if (Environment.TickCount - layoutsCheckedTick > 60000 || layoutsCheckedTick == 0)
            {
                layoutsCheckedTick = Environment.TickCount;
                manyLayouts = GetKeyboardLayoutList(0, null) > 1;
            }
            if (!manyLayouts || !settings.GetBool("keys.lang", true)) return;
            IntPtr win = Native.GetForegroundWindow();
            IntPtr hkl = GetKeyboardLayout(GetWindowThreadProcessId(win, IntPtr.Zero));
            if (hkl == IntPtr.Zero) return;
            // Switching windows isn't a change
            bool same = win == layoutWin;
            layoutWin = win;
            if (hkl == layoutNow) return;
            bool first = layoutNow == IntPtr.Zero;
            layoutNow = hkl;
            if (first || !same) return;
            var name = LayoutName(hkl);
            var t = ShowToast(View.Locks, 1.6, 1, false, true);
            if (t == null) return;
            t.Title = name[1];
            t.Detail = name[0];
            t.Flag = true;
            t.Data = name;
        }

        // Code like EN, plus language name
        string[] LayoutName(IntPtr hkl)
        {
            long key = hkl.ToInt64();
            string[] n;
            if (layoutNames.TryGetValue(key, out n)) return n;
            int lang = (int)(key & 0xFFFF);
            try
            {
                var c = new CultureInfo(lang);
                string title = c.NativeName;
                int paren = title.IndexOf(" (");
                if (paren > 0) title = title.Substring(0, paren);
                if (title.Length > 0) title = char.ToUpper(title[0], c) + title.Substring(1);
                n = new[] { c.TwoLetterISOLanguageName.ToUpperInvariant(), title };
            }
            catch (Exception) { n = new[] { "?", lang.ToString("X4") }; }
            layoutNames[key] = n;
            return n;
        }

        // Lasting cue only while Caps is on
        bool CapsShown { get { return capsOn && capsPref; } }
        // Num Lock marker only if its HUD is on
        bool NumShown { get { return numOn && settings.GetBool("keys.num", false); } }
        bool LockShown { get { return CapsShown || NumShown; } }
        int LockMarks { get { return (CapsShown ? 1 : 0) + (NumShown ? 1 : 0); } }

        void ShowLock(string name, string cap, bool on)
        {
            var t = ShowToast(View.Locks, 1.6, 1, false, true);
            if (t == null) return;
            t.Title = name;
            t.Detail = cap;
            t.Flag = on;
        }

        string LockState()
        {
            if (toast == null) return "";
            if (toast.Data is string[]) return Lang.T("bahasa input");
            return toast.Flag ? Lang.T("nyala") : Lang.T("mati");
        }

        float LocksWidth()
        {
            if (toast == null || p.G == null) return 200;
            return Clamp(p.Measure(toast.Title, Ink.Semi(13.5f)) + 14 + p.Measure(LockState(), Ink.Text(12.5f)) + 70 + (toast.Detail.Length > 1 ? 8 : 0), 170, 340);
        }

        void DrawLocks(float w, float h)
        {
            if (toast == null) return;
            float cy = h / 2;
            float half = toast.Detail.Length > 1 ? 13f : 9f;
            var semi = Ink.Semi(13.5f);
            float x = CenterStart(w, half * 2 + 11, toast.Title, semi, LockState(), Ink.Text(12.5f), 120);
            Keycap(x + half, cy, 9f, toast.Detail, toast.Flag);
            x += half * 2 + 11;
            p.Text(toast.Title, semi, p.T.Text, x, cy, w - x - 16, StringAlignment.Near);
            float dx = x + p.Measure(toast.Title, semi) + 14;
            p.Text(LockState(), Ink.Text(12.5f), p.T.Sub, dx, cy, w - dx - 8, StringAlignment.Near);
        }

        // Keycap: filled while locked on
        void Keycap(float cx, float cy, float r, string label, bool on)
        {
            float half = label.Length > 1 ? r * 1.45f : r;
            var box = new RectangleF(cx - half, cy - r, half * 2, r * 2);
            var font = Ink.Semi(label.Length > 1 ? r * 1.0f : r * 1.15f);
            if (on)
            {
                p.FillRound(box, r * 0.45f, p.T.Text);
                p.TextCenter(label, font, p.T.Bg, cx, cy);
            }
            else
            {
                p.StrokeRound(box, r * 0.45f, p.T.Sub, 1.3f);
                p.TextCenter(label, font, p.T.Sub, cx, cy);
            }
        }
    }
}
