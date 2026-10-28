using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tepi
{
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
