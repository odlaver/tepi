using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Snapshot layer: the old page fades out, the new one settles in
    sealed class KitVeil : Control
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        readonly Control source;
        readonly Func<float, bool> step;
        Bitmap from, to;
        float t, dur = 0.2f, rise;
        bool playing;

        KitVeil(Control source)
        {
            this.source = source;
            step = Step;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
        }

        // Clicks pass through to the page below
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }
            base.WndProc(ref m);
        }

        static void Reach(Control c)
        {
            if (!c.IsHandleCreated) { var h = c.Handle; }
            foreach (Control k in c.Controls) Reach(k);
        }

        static Bitmap Snap(Control c)
        {
            int w = Math.Max(1, c.ClientSize.Width), h = Math.Max(1, c.ClientSize.Height);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var dc = g.GetHdc();
                try { SendMessage(c.Handle, 0x317, dc, (IntPtr)(4 | 8 | 0x10)); }
                finally { g.ReleaseHdc(dc); }
            }
            return bmp;
        }

        // Covers the page as it is now; null when motion is off
        public static KitVeil Begin(Control source, KitVeil running)
        {
            if (Anim.Reduced || !source.IsHandleCreated || !source.Visible || source.Parent == null || source.ClientSize.Width < 8 || source.ClientSize.Height < 8) { if (running != null) running.Finish(); return null; }
            Bitmap old;
            if (running != null && !running.IsDisposed) { old = running.Flatten(); running.Finish(); }
            else
            {
                try { old = Snap(source); }
                catch (Exception) { return null; }
            }
            var v = new KitVeil(source) { from = old };
            v.Bounds = new Rectangle(source.Location, source.ClientSize);
            source.Parent.Controls.Add(v);
            v.BringToFront();
            return v;
        }

        // Page rebuilt: settle the new one over the old
        public void Play(float seconds, float riseBy)
        {
            if (IsDisposed) return;
            try
            {
                Reach(source);
                source.Update();
                to = Snap(source);
            }
            catch (Exception) { Finish(); return; }
            dur = Math.Max(0.05f, seconds * Anim.Pace);
            rise = riseBy;
            t = 0;
            playing = true;
            KitMotion.Run(step);
        }

        bool Step(float dt)
        {
            if (IsDisposed) return false;
            t += dt / dur;
            if (t >= 1) { Finish(); return false; }
            Invalidate();
            return true;
        }

        public void Finish()
        {
            if (IsDisposed) return;
            playing = false;
            var p = Parent;
            if (p != null) p.Controls.Remove(this);
            Dispose();
        }

        Bitmap Flatten()
        {
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) Draw(g);
            return bmp;
        }

        void Draw(Graphics g)
        {
            g.Clear(source.BackColor);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float tt = Ease.Clamp01(t);
            if (to != null && playing && rise == 0)
            {
                // Same page rebuilt: straight crossfade, no dip
                Blit(g, to, 1, 0);
                if (from != null) Blit(g, from, 1 - Ease.OutCubic(tt), 0);
                return;
            }
            // New page: the old one clears first, then the new one rises in
            if (from != null) Blit(g, from, playing ? 1 - Ease.Clamp01(tt / 0.3f) : 1, 0);
            if (to != null && playing)
            {
                float e = Ease.OutCubic((tt - 0.22f) / 0.78f);
                Blit(g, to, e, rise * (1 - e));
            }
        }

        static void Blit(Graphics g, Bitmap b, float alpha, float dy)
        {
            if (alpha <= 0.004f) return;
            var dest = new Rectangle(0, (int)Math.Round(dy), b.Width, b.Height);
            if (alpha >= 0.996f) { g.DrawImage(b, dest, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel); return; }
            var m = new ColorMatrix { Matrix33 = alpha };
            using (var at = new ImageAttributes())
            {
                at.SetColorMatrix(m, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(b, dest, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, at);
            }
        }

        protected override void OnPaint(PaintEventArgs e) { Draw(e.Graphics); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (from != null) { from.Dispose(); from = null; }
                if (to != null) { to.Dispose(); to = null; }
            }
            base.Dispose(disposing);
        }
    }

    // Panel whose wheel glides instead of jumping
    class SmoothPanel : Panel
    {
        readonly Func<float, bool> step;
        float goal, shown;
        bool gliding;

        public float Notch = 96;

        public SmoothPanel() { step = Step; }

        int Top0 { get { return -AutoScrollPosition.Y; } }

        int Range { get { return Math.Max(0, DisplayRectangle.Height - ClientSize.Height); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var h = e as HandledMouseEventArgs;
            if (Anim.Reduced || !AutoScroll || Range <= 0) { base.OnMouseWheel(e); return; }
            if (h != null) h.Handled = true;
            if (!gliding) goal = shown = Top0;
            goal = Math.Max(0, Math.Min(Range, goal - e.Delta / 120f * Notch));
            gliding = true;
            KitMotion.Run(step);
        }

        bool Step(float dt)
        {
            if (IsDisposed || !IsHandleCreated) { gliding = false; return false; }
            // Someone else moved the page: follow it
            if (Math.Abs(Top0 - shown) > 2) { goal = shown = Top0; gliding = false; return false; }
            shown += (goal - shown) * (1 - (float)Math.Exp(-dt / (0.07f * Anim.Pace)));
            bool more = Math.Abs(goal - shown) > 0.6f;
            if (!more) shown = goal;
            AutoScrollPosition = new Point(0, (int)Math.Round(shown));
            if (!more) gliding = false;
            return more;
        }

        protected override void Dispose(bool disposing) { gliding = false; base.Dispose(disposing); }
    }

    // Rows that moved glide from their old place
    static class KitSlide
    {
        public static void Rows(List<KitRow> rows, List<int> was)
        {
            if (rows.Count == 0 || Anim.Reduced) return;
            var to = new List<int>();
            for (int i = 0; i < rows.Count; i++) { to.Add(rows[i].Top); rows[i].Top = was[i]; }
            KitMotion.Run(dt =>
            {
                float k = 1 - (float)Math.Exp(-dt / (0.06f * Anim.Pace));
                bool more = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (r.IsDisposed) continue;
                    int d = to[i] - r.Top;
                    if (d == 0) continue;
                    int mv = (int)Math.Round(d * k);
                    if (mv == 0) mv = Math.Sign(d);
                    r.Top += mv;
                    more = true;
                    if (r.Parent != null) r.Parent.Invalidate();
                }
                return more;
            });
        }
    }
}
