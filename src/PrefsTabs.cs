using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    // Row that opens a page: chevron, hover, Enter
    class NavRow : KitRow
    {
        public event Action Open;
        bool hovered;

        public NavRow()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Trail = 34;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { KitInk.ByMouse = true; base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Fire();
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
            KitInk.ByMouse = false;
            e.Handled = true;
            Fire();
        }

        void Fire() { var h = Open; if (h != null) h(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var w = FindForm() as KitWindow;
            float s = w != null ? w.Ui : 1;
            KitInk.Smooth(g);
            var box = new RectangleF(4 * s, 3 * s, Width - 8 * s, Height - 6 * s);
            // The whole row is the target: it lights as one
            if (hovered) using (var path = Ink.Round(box, 9 * s)) using (var b = new SolidBrush(KitInk.Hover)) g.FillPath(b, path);
            base.OnPaint(e);
            KitInk.Smooth(g);
            using (var icons = new Font(Ink.IconFamily, 12f * s, GraphicsUnit.Pixel))
                KitInk.Draw(g, "", icons, hovered ? KitInk.Text : KitInk.Sub, new RectangleF(Width - 40 * s, 0, 24 * s, Height), ContentAlignment.MiddleCenter);
            if (Focused && ShowFocusCues && !KitInk.ByMouse)
                using (var path = Ink.Round(box, 9 * s)) using (var pen = new Pen(KitInk.Text, 2f)) g.DrawPath(pen, path);
        }
    }
}
