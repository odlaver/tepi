using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Tepi
{
    // Lock keys, read without a keyboard hook
    partial class Island
    {
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);

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
        }

        // Lasting cue only while Caps is on
        bool CapsShown { get { return capsOn && capsPref; } }

        void ShowLock(string name, string cap, bool on)
        {
            var t = ShowToast(View.Locks, 1.6, 1, false, true);
            if (t == null) return;
            t.Title = name;
            t.Detail = cap;
            t.Flag = on;
        }

        string LockState() { return toast != null && toast.Flag ? Lang.T("nyala") : Lang.T("mati"); }

        float LocksWidth()
        {
            if (toast == null || p.G == null) return 200;
            return Clamp(p.Measure(toast.Title, Ink.Semi(13.5f)) + 14 + p.Measure(LockState(), Ink.Text(12.5f)) + 70, 170, 300);
        }

        void DrawLocks(float w, float h)
        {
            if (toast == null) return;
            float cy = h / 2;
            Keycap(29, cy, 9f, toast.Detail, toast.Flag);
            var semi = Ink.Semi(13.5f);
            p.Text(toast.Title, semi, p.T.Text, 48, cy, w - 68, StringAlignment.Near);
            float dx = 48 + p.Measure(toast.Title, semi) + 14;
            p.Text(LockState(), Ink.Text(12.5f), p.T.Sub, dx, cy, w - dx - 20, StringAlignment.Near);
        }

        // Keycap: filled while locked on
        void Keycap(float cx, float cy, float r, string label, bool on)
        {
            var box = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            var font = Ink.Semi(r * 1.15f);
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
