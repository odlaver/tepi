using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Tepi
{
    // Removed item's last picture, flying out
    class Flyer
    {
        public Bitmap Img;
        // Img part this flyer carries; tiles share
        public Rectangle Src;
        // Screen device pixels at capture
        public RectangleF From;
        public bool Suck;
        public float Delay, Vx, Vy, Spin;
    }

    // Exit animations: into the trash, or scattered
    partial class Island
    {
        readonly List<Flyer> flyers = new List<Flyer>();
        readonly List<Bitmap> flyerImages = new List<Bitmap>();
        readonly Random flyRandom = new Random();
        DateTime flyUtc, trashGulpUtc = DateTime.MinValue;
        // Trash button centre, canvas units
        PointF trashPt;
        const double SuckSeconds = 0.55, ScatterSeconds = 0.75;

        // Local rect to canvas units
        RectangleF CanvasRect(RectangleF local)
        {
            local.Offset(originX, originY + hitDy);
            return local;
        }

        // Drawn row rects by hit id prefix
        List<RectangleF> RowRects(string prefix, string exclude)
        {
            var list = new List<RectangleF>();
            foreach (var h in hits)
                if (h.Id.StartsWith(prefix, StringComparison.Ordinal) && h.Id.Length > prefix.Length && char.IsDigit(h.Id[prefix.Length]) &&
                    (exclude == null || !h.Id.StartsWith(exclude, StringComparison.Ordinal)))
                    list.Add(h.Rect);
            list.Sort((a, b) => a.Y.CompareTo(b.Y));
            return list;
        }

        // Capture rows from last frame, send off
        void Vanish(IEnumerable<RectangleF> rects, bool toTrash)
        {
            if (Anim.Reduced || canvas == null) return;
            int i = 0;
            foreach (var r in rects)
            {
                if (r.IsEmpty) continue;
                var px = Rectangle.Intersect(Rectangle.Round(new RectangleF(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale)), new Rectangle(0, 0, canvasW, canvasH));
                if (px.Width < 4 || px.Height < 4) continue;
                var img = new Bitmap(px.Width, px.Height, PixelFormat.Format32bppPArgb);
                using (var fg = Graphics.FromImage(img))
                {
                    fg.CompositingMode = CompositingMode.SourceCopy;
                    fg.DrawImage(canvas, new Rectangle(0, 0, px.Width, px.Height), px, GraphicsUnit.Pixel);
                }
                flyerImages.Add(img);
                var screen = new RectangleF(lastLeft + px.X, px.Y, px.Width, px.Height);
                if (toTrash) flyers.Add(new Flyer { Img = img, Src = new Rectangle(0, 0, px.Width, px.Height), From = screen, Suck = true, Delay = Math.Min(i, 8) * 0.045f });
                else Shatter(img, screen);
                i++;
            }
            if (flyers.Count == 0) return;
            flyUtc = DateTime.UtcNow;
            Kick();
        }

        // Tiles that burst outward and fall
        void Shatter(Bitmap img, RectangleF screen)
        {
            int cols = Math.Max(3, Math.Min(10, img.Width / 28)), rows = Math.Max(2, Math.Min(4, img.Height / 18));
            float tw = (float)img.Width / cols, th = (float)img.Height / rows;
            float cx = screen.X + screen.Width / 2, cy = screen.Y + screen.Height / 2;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    var src = Rectangle.Round(new RectangleF(x * tw, y * th, tw, th));
                    var from = new RectangleF(screen.X + src.X, screen.Y + src.Y, src.Width, src.Height);
                    float dx = from.X + from.Width / 2 - cx, dy = from.Y + from.Height / 2 - cy;
                    float len = Math.Max(1, (float)Math.Sqrt(dx * dx + dy * dy));
                    float speed = (90 + (float)flyRandom.NextDouble() * 160) * scale;
                    flyers.Add(new Flyer
                    {
                        Img = img, Src = src, From = from,
                        Vx = dx / len * speed + ((float)flyRandom.NextDouble() - 0.5f) * 60 * scale,
                        Vy = dy / len * speed * 0.6f - (80 + (float)flyRandom.NextDouble() * 120) * scale,
                        Spin = ((float)flyRandom.NextDouble() - 0.5f) * 540,
                        Delay = (float)flyRandom.NextDouble() * 0.06f
                    });
                }
        }

        bool FlyersMoving(DateTime now) { return flyers.Count > 0 || (now - trashGulpUtc).TotalSeconds < 0.4; }

        void DrawFlyers(DateTime now, int pxW)
        {
            if (flyers.Count == 0) return;
            double t0 = (now - flyUtc).TotalSeconds;
            int left = WindowLeft(pxW);
            var trash = new PointF(lastLeft + trashPt.X * scale, trashPt.Y * scale);
            bool alive = false, gulp = false;
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var ia = new ImageAttributes())
            {
                var cm = new ColorMatrix();
                foreach (var f in flyers)
                {
                    float t = (float)((t0 - f.Delay) / (f.Suck ? SuckSeconds : ScatterSeconds));
                    if (t >= 1) continue;
                    alive = true;
                    t = Math.Max(0, t);
                    float alpha;
                    RectangleF dst;
                    if (f.Suck)
                    {
                        // Genie: width shrinks first, then height
                        float e = t * t * (3 - 2 * t);
                        float sx = 1 - 0.94f * Ease.OutCubic(Math.Min(1, t * 1.4f)), sy = 1 - 0.88f * e;
                        float fx = f.From.X + f.From.Width / 2, fy = f.From.Y + f.From.Height / 2;
                        float ccx = fx, ccy = Math.Min(fy, trash.Y) - 30 * scale;
                        float u = 1 - e;
                        float x = u * u * fx + 2 * u * e * ccx + e * e * trash.X, y = u * u * fy + 2 * u * e * ccy + e * e * trash.Y;
                        dst = new RectangleF(x - f.From.Width * sx / 2 - left, y - f.From.Height * sy / 2, f.From.Width * sx, f.From.Height * sy);
                        alpha = 1 - Ease.Clamp01((t - 0.7f) / 0.3f);
                        if (t > 0.82f) gulp = true;
                    }
                    else
                    {
                        float x = f.From.X - left + f.Vx * t * (float)ScatterSeconds, y = f.From.Y + (f.Vy * t + 520 * scale * t * t) * (float)ScatterSeconds;
                        dst = new RectangleF(x, y, f.From.Width, f.From.Height);
                        alpha = (1 - t) * (1 - t);
                    }
                    cm.Matrix33 = alpha;
                    ia.SetColorMatrix(cm);
                    if (f.Suck || Math.Abs(f.Spin) < 1) g.DrawImage(f.Img, Rectangle.Round(dst), f.Src.X, f.Src.Y, f.Src.Width, f.Src.Height, GraphicsUnit.Pixel, ia);
                    else
                    {
                        var s2 = g.Save();
                        g.TranslateTransform(dst.X + dst.Width / 2, dst.Y + dst.Height / 2);
                        g.RotateTransform(f.Spin * t);
                        g.DrawImage(f.Img, new Rectangle((int)(-dst.Width / 2), (int)(-dst.Height / 2), (int)dst.Width, (int)dst.Height), f.Src.X, f.Src.Y, f.Src.Width, f.Src.Height, GraphicsUnit.Pixel, ia);
                        g.Restore(s2);
                    }
                }
            }
            g.Restore(st);
            if (gulp && (now - trashGulpUtc).TotalSeconds > 0.5) trashGulpUtc = now;
            if (!alive)
            {
                flyers.Clear();
                foreach (var b in flyerImages) b.Dispose();
                flyerImages.Clear();
            }
        }

        // Trash gulp: quick swell, then settle
        float TrashScale(DateTime now)
        {
            double t = (now - trashGulpUtc).TotalSeconds / 0.38;
            if (t >= 1 || Anim.Reduced) return 1;
            return 1 + 0.32f * (float)Math.Sin(Math.PI * t) * (1 - (float)t * 0.4f);
        }

        // Rows below a removed one glide up
        readonly Spring collapse = new Spring(0, 260, 26, 0.05f);
        string collapseList;
        int collapseFrom;

        void Collapse(string list, int index, float rowH)
        {
            if (Anim.Reduced) return;
            collapseList = list;
            collapseFrom = index;
            collapse.Snap(rowH);
            collapse.Target = 0;
        }

        float CollapseAt(string list, int index)
        {
            return list == collapseList && index >= collapseFrom && !collapse.Settled ? collapse.Value : 0;
        }

        // Menus share the island's look
        void StyleMenu(ToolStripDropDownMenu m) { MenuLook.Apply(m); }
    }
}
