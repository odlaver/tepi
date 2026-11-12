using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Menus opened from Tepi's own windows never take focus: the dock, island and
    // sheets are WS_EX_NOACTIVATE and answer WM_MOUSEACTIVATE with MA_NOACTIVATE.
    // WinForms closes a drop-down when it loses activation, so with no activation
    // there is nothing to lose and the menu would sit there forever. While such a
    // menu is open a low-level mouse hook watches for a click landing outside it and
    // closes it. The click is not swallowed, so the window under it still gets it.
    static class MenuGuard
    {
        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_NCLBUTTONDOWN = 0xA1;

        delegate IntPtr HookProc(int code, IntPtr msg, IntPtr info);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT Pt; public int MouseData; public int Flags; public int Time; public IntPtr Extra; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr info);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        static IntPtr hook = IntPtr.Zero;
        static HookProc proc;
        static ToolStripDropDown watched;
        static bool closing;

        // Only one of Tepi's menus is ever up, so a new one replaces the last
        internal static void Watch(ToolStripDropDown menu)
        {
            Stop();
            if (menu == null) return;
            watched = menu;
            menu.Closed += delegate { if (ReferenceEquals(watched, menu)) Stop(); };
            proc = Callback;
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write("menu guard: hook failed, menu may stay open");
        }

        static void Stop()
        {
            watched = null;
            closing = false;
            proc = null;
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        static IntPtr Callback(int code, IntPtr msg, IntPtr info)
        {
            if (code >= 0 && watched != null)
            {
                int id = msg.ToInt32();
                if (id == WM_LBUTTONDOWN || id == WM_RBUTTONDOWN || id == WM_MBUTTONDOWN || id == WM_NCLBUTTONDOWN)
                {
                    var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(info, typeof(MSLLHOOKSTRUCT));
                    Down(new Point(data.Pt.X, data.Pt.Y));
                }
            }
            return CallNextHookEx(hook, code, msg, info);
        }

        // A press outside the menu closes it; a press inside is the menu's own business
        internal static void Down(Point at)
        {
            var m = watched;
            if (m == null || closing || m.IsDisposed || !m.Visible) return;
            if (m.Bounds.Contains(at)) return;
            closing = true;
            try
            {
                m.BeginInvoke(new Action(delegate
                {
                    try { if (!m.IsDisposed) m.Close(ToolStripDropDownCloseReason.AppClicked); }
                    catch (Exception ex) { Log.Write("menu guard close: " + ex.Message); }
                }));
            }
            catch (Exception ex) { closing = false; Log.Write("menu guard: " + ex.Message); }
        }
    }
    // Menus in island colours, rounded corners
    class MenuLook : ToolStripProfessionalRenderer
    {
        static Color Bg { get { return Opaque(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.06f)); } }
        static Color Text { get { return Opaque(Ink.Base.Text); } }
        static Color Sub { get { return Opaque(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.5f)); } }
        static Color Hover { get { return Opaque(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.16f)); } }
        static Color Line { get { return Opaque(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.14f)); } }

        static Color Opaque(Color c) { return Color.FromArgb(255, c.R, c.G, c.B); }

        public MenuLook() : base(new Colors()) { RoundedEdges = false; }

        public static readonly Font MenuFont = new Font(Ink.MenuFamily, 9.5f);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Bg); }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(Line)) e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ink.Round(r, 6))
            using (var b = new SolidBrush(Hover)) e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Sub;
            e.TextFont = MenuFont;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Line)) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Min(r.Width, r.Height) * 0.7f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Text, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                e.Graphics.DrawLines(pen, new[] { new PointF(cx - s * 0.42f, cy), new PointF(cx - s * 0.12f, cy + s * 0.3f), new PointF(cx + s * 0.45f, cy - s * 0.32f) });
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Sub;
            base.OnRenderArrow(e);
        }

        class Colors : ProfessionalColorTable
        {
            public override Color MenuBorder { get { return Line; } }
            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Bg; } }
        }

        // Same look down every submenu
        public static void Apply(ToolStripDropDownMenu menu)
        {
            menu.Renderer = new MenuLook();
            menu.Font = MenuFont;
            menu.Padding = new Padding(2, 6, 2, 6);
            menu.HandleCreated += delegate { Native.RoundCorners(menu.Handle); };
            foreach (ToolStripItem item in menu.Items)
            {
                var sub = item as ToolStripMenuItem;
                if (sub == null) continue;
                item.Padding = new Padding(4, 3, 4, 3);
                if (sub.HasDropDownItems || sub.DropDown != null)
                {
                    var dd = sub.DropDown as ToolStripDropDownMenu;
                    if (dd != null && !(dd.Renderer is MenuLook)) Apply(dd);
                }
            }
        }
    }
}
