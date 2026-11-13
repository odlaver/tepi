using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    // Row that opens a page: chevron, hover, Enter
    class NavRow : KitRow
    {
        public event Action Open;
        // Light and chevron follow the pointer
        readonly Glide hot = new Glide(0.07f), held = new Glide(0.04f);
        readonly Func<float, bool> step;

        public NavRow()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Trail = 34;
            step = Step;
        }

        bool Step(float dt)
        {
            if (IsDisposed) return false;
            bool a = hot.Step(dt), b = held.Step(dt);
            Invalidate();
            return a || b;
        }

        void Go()
        {
            if (Anim.Reduced) { hot.Snap(hot.Target); held.Snap(held.Target); Invalidate(); }
            else KitMotion.Run(step);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot.Target = 1; Go(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot.Target = 0; held.Target = 0; Go(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { KitInk.ByMouse = true; base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { held.Target = 1; Go(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            held.Target = 0;
            Go();
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
            if (hot.Value > 0.01f) using (var path = Ink.Round(box, 9 * s)) using (var b = new SolidBrush(Ink.A(KitInk.Hover, hot.Value))) g.FillPath(b, path);
            base.OnPaint(e);
            KitInk.Smooth(g);
            // Chevron leans toward where the row goes
            float lean = 3 * s * hot.Value + 2 * s * held.Value;
            using (var icons = new Font(Ink.IconFamily, 12f * s, GraphicsUnit.Pixel))
                KitInk.Draw(g, "", icons, Ink.Mix(KitInk.Sub, KitInk.Text, hot.Value), new RectangleF(Width - 40 * s + lean, 0, 24 * s, Height), ContentAlignment.MiddleCenter);
            if (Focused && ShowFocusCues && !KitInk.ByMouse)
                using (var path = Ink.Round(box, 9 * s)) using (var pen = new Pen(KitInk.Text, 2f)) g.DrawPath(pen, path);
        }
    }
}
