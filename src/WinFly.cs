using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Window shrinks into its dock icon, and back
    sealed class WinFly : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

        [StructLayout(LayoutKind.Sequential)]
        struct ThumbProps
        {
            public int Flags;
            public Native.RECT Dest, Source;
            public byte Opacity;
            public bool Visible, ClientOnly;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AnimInfo { public int Size, MinAnimate; }

        [StructLayout(LayoutKind.Sequential)]
        struct Placement { public int Length, Flags, ShowCmd; public Native.POINT Min, Max; public Native.RECT Normal; }

        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr id);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr id);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr id, ref ThumbProps props);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr id, out Native.SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref AnimInfo info, int flags);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref Placement p);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

        const int SPI_GETANIMATION = 0x48, SPI_SETANIMATION = 0x49;
        const int DestFlag = 0x1, OpacityFlag = 0x4, VisibleFlag = 0x8, ClientOnlyFlag = 0x10;
        const double MinimizeMs = 300, RestoreMs = 280;

        readonly IntPtr src;
        readonly Rectangle from, to;
        readonly bool restoring;
        readonly Action done;
        readonly Stopwatch clock = new Stopwatch();
        readonly Timer frames = new Timer { Interval = 8 };
        IntPtr thumb;
        Rectangle area;
        float aspect = 1;

        WinFly(IntPtr src, Rectangle from, Rectangle to, bool restoring, Action done)
        {
            this.src = src;
            this.from = from;
            this.to = to;
            this.restoring = restoring;
            this.done = done;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            area = Rectangle.Union(from, to);
            area.Inflate(8, 8);
            Bounds = area;
            frames.Tick += delegate { Step(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = Native.WS_POPUP;
                // Clicks fall through to what is below
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | 0x20;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Fully see-through; only the thumbnail shows
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref m);
            int none = unchecked((int)0xFFFFFFFE), off = 1;
            DwmSetWindowAttribute(Handle, 34, ref none, 4);
            DwmSetWindowAttribute(Handle, 3, ref off, 4);
        }

        static bool active;

        // Off when Windows or Tepi says less motion
        static bool Allowed()
        {
            if (active || Anim.Reduced) return false;
            var a = new AnimInfo { Size = 8 };
            return SystemParametersInfo(SPI_GETANIMATION, 8, ref a, 0) && a.MinAnimate != 0;
        }

        // Native minimize anim paused briefly, so only one plays
        static bool held, exitHooked;

        static void HoldNative()
        {
            var a = new AnimInfo { Size = 8, MinAnimate = 0 };
            SystemParametersInfo(SPI_SETANIMATION, 8, ref a, 0);
            held = true;
            // Tepi closing mid-hold still gives it back
            if (!exitHooked) { exitHooked = true; Application.ApplicationExit += delegate { ReleaseNative(); }; AppDomain.CurrentDomain.ProcessExit += delegate { ReleaseNative(); }; }
            var t = new Timer { Interval = 450 };
            t.Tick += delegate { t.Stop(); t.Dispose(); ReleaseNative(); };
            t.Start();
        }

        internal static void ReleaseNative()
        {
            if (!held) return;
            held = false;
            var a = new AnimInfo { Size = 8, MinAnimate = 1 };
            SystemParametersInfo(SPI_SETANIMATION, 8, ref a, 0);
        }

        // Minimize with the window flying into icon
        public static bool Minimize(IntPtr h, Rectangle icon, Action minimize)
        {
            Native.RECT r;
            if (icon.IsEmpty || !Allowed() || IsIconic(h) || !Native.GetWindowRect(h, out r)) return false;
            var win = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (win.Width < 40 || win.Height < 40) return false;
            var fly = new WinFly(h, win, icon, false, null);
            if (!fly.Start()) { fly.Dispose(); return false; }
            HoldNative();
            minimize();
            return true;
        }

        // The window grows out of its icon, then shows
        public static bool Restore(IntPtr h, Rectangle icon, Action restore)
        {
            if (icon.IsEmpty || !Allowed() || !IsIconic(h)) return false;
            var win = RestoredRect(h);
            if (win.IsEmpty) return false;
            var fly = new WinFly(h, icon, win, true, delegate { HoldNative(); restore(); });
            if (!fly.Start()) { fly.Dispose(); return false; }
            return true;
        }

        // Where a minimized window comes back to
        static Rectangle RestoredRect(IntPtr h)
        {
            var p = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
            if (!GetWindowPlacement(h, ref p)) return Rectangle.Empty;
            var screen = Screen.FromHandle(h);
            // Back to maximized: the whole work area
            if ((p.Flags & 2) != 0) return screen.WorkingArea;
            // Placement is in work-area coordinates
            var n = Rectangle.FromLTRB(p.Normal.Left, p.Normal.Top, p.Normal.Right, p.Normal.Bottom);
            n.Offset(screen.WorkingArea.Left - screen.Bounds.Left, screen.WorkingArea.Top - screen.Bounds.Top);
            return n.Width > 0 && n.Height > 0 ? n : Rectangle.Empty;
        }

        bool Start()
        {
            CreateHandle();
            if (DwmRegisterThumbnail(Handle, src, out thumb) != 0 || thumb == IntPtr.Zero) return false;
            Native.SIZE size;
            if (DwmQueryThumbnailSourceSize(thumb, out size) != 0 || size.CX <= 0 || size.CY <= 0) { DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; return false; }
            aspect = size.CY / (float)size.CX;
            active = true;
            Place(0);
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            clock.Start();
            frames.Start();
            return true;
        }

        void Step()
        {
            double ms = restoring ? RestoreMs : MinimizeMs;
            float t = (float)Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            Place(t);
            if (t < 1) return;
            frames.Stop();
            if (done != null) done();
            // One frame later, so the real window is drawn
            var bye = new Timer { Interval = restoring ? 60 : 16 };
            bye.Tick += delegate { bye.Stop(); bye.Dispose(); Finish(); };
            bye.Start();
        }

        void Finish()
        {
            if (thumb != IntPtr.Zero) { DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; }
            active = false;
            Close();
            Dispose();
        }

        // Accelerates into the dock, decelerates out of it
        void Place(float t)
        {
            float e = restoring ? 1 - (float)Math.Pow(1 - t, 3) : t * t * (3 - 2 * t);
            float cx = Lerp(from.X + from.Width / 2f, to.X + to.Width / 2f, e);
            float w = Lerp(from.Width, to.Width, e);
            float h = Math.Max(1, w * aspect);
            float bottom = Lerp(from.Bottom, to.Bottom, e);
            var dest = new Native.RECT
            {
                Left = (int)(cx - w / 2) - area.X,
                Right = (int)(cx + w / 2) - area.X,
                Bottom = (int)bottom - area.Y,
                Top = (int)(bottom - h) - area.Y,
            };
            float fade = restoring ? Math.Min(1, t / 0.35f) : Math.Min(1, (1 - t) / 0.4f);
            var props = new ThumbProps
            {
                Flags = DestFlag | OpacityFlag | VisibleFlag | ClientOnlyFlag,
                Dest = dest,
                Opacity = (byte)(255 * Math.Max(0, fade)),
                Visible = true,
                ClientOnly = false,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }

        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) frames.Dispose();
            base.Dispose(disposing);
        }
    }
}
