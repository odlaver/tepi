using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Edit a picture before sharing: blur, draw, crop, turn, mirror
    partial class Island
    {
        void OpenRedact(string path)
        {
            Bitmap pic;
            try
            {
                // Own copy: the file stays unlocked
                using (var fs = File.OpenRead(path))
                using (var img = Image.FromStream(fs)) pic = new Bitmap(img);
            }
            catch (Exception ex) { Log.Write("redact open: " + ex.Message); ShowInfo('', Lang.T("Gambar tidak bisa dibuka"), "", true); return; }
            DismissToast();
            var w = new RedactWindow(pic);
            // Shot preview already says it
            w.Copied += delegate { if (!ShotsOn) ShowInfo('', Lang.T("Gambar disalin"), "", true); };
            w.Show();
            w.Activate();
        }
    }

    class RedactWindow : KitWindow
    {
        enum EditKind { Blur, Draw, Crop, Turn, MirrorX, MirrorY }

        struct Edit
        {
            public EditKind Kind;
            public Rectangle Box;
            public PointF[] Points;
            public Color StrokeColor;
            public float StrokeWidth;
        }

        static readonly int[] StrokeWidths = { 3, 6, 12 };
        static readonly string[][] StrokeColors =
        {
            new[] { "Merah", "#FF3B30" }, new[] { "Kuning", "#FFD24A" }, new[] { "Hijau", "#3DD68C" }, new[] { "Biru", "#4C8DFF" },
            new[] { "Oranye", "#FF9F43" }, new[] { "Ungu", "#A78BFA" }, new[] { "Putih", "#FFFFFF" }, new[] { "Hitam", "#111111" },
        };

        readonly Bitmap original;
        Bitmap work;
        // Replayed on the original: undo is one step fewer
        readonly List<Edit> edits = new List<Edit>();
        readonly List<PointF> stroke = new List<PointF>();
        readonly Label hint = new Label();
        readonly KitSegmented mode = new KitSegmented();
        readonly KitSwatch inkColor = new KitSwatch();
        readonly KitPopup inkWidth = new KitPopup();
        readonly KitButton copy = new KitButton(), save = new KitButton(), undo = new KitButton(), cancel = new KitButton(), turn = new KitButton(), mirrorX = new KitButton(), mirrorY = new KitButton();
        RectangleF canvas;
        float fit = 1;
        Point dragFrom;
        Rectangle dragBox;
        bool dragging;

        public event Action Copied;

        bool Drawing { get { return mode.Selected == 1; } }
        bool Cropping { get { return mode.Selected == 2; } }

        public RedactWindow(Bitmap pic)
        {
            original = pic;
            work = new Bitmap(pic);
            Text = Lang.T("Edit gambar");
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Cross;
            hint.UseMnemonic = false;
            hint.AutoSize = false;
            mode.Items = new[] { Lang.T("Samarkan"), Lang.T("Coret"), Lang.T("Potong") };
            mode.AccessibleName = Lang.T("Alat");
            mode.Changed += i =>
            {
                inkColor.Visible = inkWidth.Visible = Drawing;
                hint.Text = HintText();
                Invalidate();
            };
            inkColor.Presets = StrokeColors;
            inkColor.Value = ColorTranslator.FromHtml("#FF3B30");
            inkColor.AccessibleName = Lang.T("Warna coretan");
            inkColor.Visible = false;
            inkWidth.Items = new[] { Lang.T("Tipis"), Lang.T("Sedang"), Lang.T("Tebal") };
            inkWidth.Selected = 1;
            inkWidth.AccessibleName = Lang.T("Ketebalan");
            inkWidth.Visible = false;
            turn.Text = Lang.T("Putar 90°");
            turn.AccessibleDescription = Lang.T("Putar 90 derajat searah jarum jam");
            turn.Click += delegate { Add(new Edit { Kind = EditKind.Turn }); };
            mirrorX.Text = Lang.T("Balik ⇆");
            mirrorX.AccessibleName = Lang.T("Balik kiri-kanan");
            mirrorX.Click += delegate { Add(new Edit { Kind = EditKind.MirrorX }); };
            mirrorY.Text = Lang.T("Balik ⇅");
            mirrorY.AccessibleName = Lang.T("Balik atas-bawah");
            mirrorY.Click += delegate { Add(new Edit { Kind = EditKind.MirrorY }); };
            copy.Text = Lang.T("Salin");
            copy.Primary = true;
            copy.Click += delegate { CopyOut(); };
            save.Text = Lang.T("Simpan…");
            save.Click += delegate { SaveOut(); };
            undo.Text = Lang.T("Urungkan");
            undo.Click += delegate { Undo(); };
            cancel.Text = Lang.T("Batal");
            cancel.Click += delegate { Close(); };
            foreach (Control c in new Control[] { hint, mode, inkColor, inkWidth, turn, mirrorX, mirrorY, cancel, undo, save, copy }) { c.Cursor = Cursors.Default; Controls.Add(c); }
        }

        string HintText()
        {
            if (Drawing) return Lang.T("Seret pada gambar untuk mencoret.");
            return Cropping ? Lang.T("Seret untuk memilih bagian yang disimpan.") : Lang.T("Seret di atas bagian yang mau disamarkan.");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Relayout();
        }

        public override void Relayout()
        {
            float s = Ui;
            Func<float, int> u = v => (int)Math.Round(v * s);
            BackColor = KitInk.Window;
            Native.DarkFrame(Handle, !Ink.Light);
            // Fit the picture, never enlarge it
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            float maxW = Math.Min(area.Width * 0.8f, u(1100)), maxH = Math.Min(area.Height * 0.66f, u(680));
            fit = Math.Min(1f, Math.Min(maxW / work.Width, maxH / work.Height));
            int m = u(20), btnH = u(30), gap = u(8);
            var font = UiFont(12.5f);
            foreach (var b in new[] { copy, save, undo, cancel, turn, mirrorX, mirrorY })
            {
                b.Font = font;
                b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + u(28), btnH);
            }
            inkColor.Font = font;
            inkColor.Size = new Size(TextRenderer.MeasureText("      #DDDDDD", font).Width + u(28), btnH);
            inkWidth.Font = font;
            int ww = 0;
            foreach (var it in inkWidth.Items) ww = Math.Max(ww, TextRenderer.MeasureText(it + "  ▾", font).Width);
            inkWidth.Size = new Size(ww + u(28), btnH);
            int sw = 0;
            foreach (var it in mode.Items) sw = Math.Max(sw, TextRenderer.MeasureText(it, font).Width);
            mode.Size = new Size(Math.Max(u(234), (sw + u(22)) * mode.Items.Length), btnH);
            inkColor.Visible = inkWidth.Visible = Drawing;
            // Wide enough for the fullest tool row so switching modes never jumps the window
            int leftTools = mode.Width + inkColor.Width + inkWidth.Width + 2 * gap;
            int rightTools = turn.Width + mirrorX.Width + mirrorY.Width + 2 * gap;
            int tools = leftTools + gap + rightTools;
            int actions = cancel.Width + undo.Width + save.Width + copy.Width + 3 * gap;
            int cw = Math.Max(Math.Max(tools, actions), (int)(work.Width * fit)), ch = (int)(work.Height * fit);
            canvas = new RectangleF(m + (cw - work.Width * fit) / 2, m, work.Width * fit, ch);
            hint.Text = HintText();
            hint.Font = font;
            hint.ForeColor = KitInk.Sub;
            hint.BackColor = KitInk.Window;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            int y1 = m + ch + u(14);
            hint.SetBounds(m, y1, cw, u(22));
            // Tools on the left, transforms and actions anchored right
            int y2 = y1 + u(30), x = m;
            foreach (Control c in new Control[] { mode, inkColor, inkWidth }) { c.Location = new Point(x, y2); x += c.Width + gap; }
            int tx = m + cw;
            foreach (var b in new[] { mirrorY, mirrorX, turn }) { tx -= b.Width; b.Location = new Point(tx, y2); tx -= gap; }
            int y3 = y2 + btnH + u(12), xr = m + cw;
            foreach (var b in new[] { copy, save, undo, cancel }) { xr -= b.Width; b.Location = new Point(xr, y3); xr -= gap; }
            undo.Enabled = edits.Count > 0;
            ClientSize = new Size(cw + 2 * m, y3 + btnH + u(18));
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(KitInk.Window);
            g.InterpolationMode = fit < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(work, canvas);
            if (!dragging) return;
            if (Drawing)
            {
                if (stroke.Count > 0)
                {
                    var clip = g.Clip;
                    g.SetClip(canvas);
                    DrawStroke(g, stroke.ToArray(), inkColor.Value, CurrentStrokeWidth(), fit, canvas.Location);
                    g.Clip = clip;
                }
                return;
            }
            if (dragBox.Width < 1 || dragBox.Height < 1) return;
            var r = ToScreen(dragBox);
            KitInk.Smooth(g);
            if (Cropping)
            {
                // Outside the pick dims: what stays is plain
                using (var dim = new Region(canvas))
                using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                {
                    dim.Exclude(r);
                    g.FillRegion(shade, dim);
                }
                using (var pen = new Pen(Color.White, 1.5f * Ui)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                string size = dragBox.Width + " × " + dragBox.Height;
                using (var f = UiFont(11.5f)) TextRenderer.DrawText(g, size, f, new Point((int)r.X + 6, (int)r.Y + 6), Color.White);
                return;
            }
            using (var fill = new SolidBrush(Color.FromArgb(40, KitInk.Text))) g.FillRectangle(fill, r);
            using (var pen = new Pen(KitInk.Text, 1.5f * Ui) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        RectangleF ToScreen(Rectangle r) { return new RectangleF(canvas.X + r.X * fit, canvas.Y + r.Y * fit, r.Width * fit, r.Height * fit); }

        Point ToImage(Point p)
        {
            int x = (int)Math.Round((p.X - canvas.X) / fit), y = (int)Math.Round((p.Y - canvas.Y) / fit);
            return new Point(Math.Max(0, Math.Min(work.Width, x)), Math.Max(0, Math.Min(work.Height, y)));
        }

        PointF ToImageF(Point p)
        {
            float x = (p.X - canvas.X) / fit, y = (p.Y - canvas.Y) / fit;
            return new PointF(Math.Max(0f, Math.Min(work.Width, x)), Math.Max(0f, Math.Min(work.Height, y)));
        }

        float CurrentStrokeWidth()
        {
            int idx = Math.Max(0, Math.Min(StrokeWidths.Length - 1, inkWidth.Selected));
            return Math.Max(1.5f, StrokeWidths[idx] * Ui / Math.Max(0.05f, fit));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !canvas.Contains(e.Location)) return;
            dragging = true;
            Capture = true;
            if (Drawing)
            {
                stroke.Clear();
                stroke.Add(ToImageF(e.Location));
                Invalidate();
                return;
            }
            dragFrom = ToImage(e.Location);
            dragBox = Rectangle.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            if (Drawing)
            {
                var pt = ToImageF(e.Location);
                if (stroke.Count == 0) stroke.Add(pt);
                else
                {
                    var last = stroke[stroke.Count - 1];
                    float dx = pt.X - last.X, dy = pt.Y - last.Y, minStep = 0.75f / Math.Max(0.05f, fit);
                    if (dx * dx + dy * dy >= minStep * minStep) stroke.Add(pt);
                }
                Invalidate();
                return;
            }
            var p = ToImage(e.Location);
            dragBox = Rectangle.FromLTRB(Math.Min(dragFrom.X, p.X), Math.Min(dragFrom.Y, p.Y), Math.Max(dragFrom.X, p.X), Math.Max(dragFrom.Y, p.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Capture = false;
            if (Drawing)
            {
                var pt = ToImageF(e.Location);
                if (stroke.Count == 0) stroke.Add(pt);
                else
                {
                    var last = stroke[stroke.Count - 1];
                    if (Math.Abs(pt.X - last.X) > 0.05f || Math.Abs(pt.Y - last.Y) > 0.05f) stroke.Add(pt);
                }
                var pts = stroke.ToArray();
                stroke.Clear();
                if (pts.Length > 0) Add(new Edit { Kind = EditKind.Draw, Points = pts, StrokeColor = inkColor.Value, StrokeWidth = CurrentStrokeWidth() });
                else Invalidate();
                return;
            }
            int least = Cropping ? 8 : 4;
            if (dragBox.Width >= least && dragBox.Height >= least) Add(new Edit { Kind = Cropping ? EditKind.Crop : EditKind.Blur, Box = dragBox });
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keys == (Keys.Control | Keys.C) || keys == Keys.Enter) { CopyOut(); return true; }
            if (keys == (Keys.Control | Keys.S)) { SaveOut(); return true; }
            if (keys == Keys.Escape)
            {
                if (dragging) { dragging = false; Capture = false; stroke.Clear(); dragBox = Rectangle.Empty; Invalidate(); return true; }
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keys);
        }

        // New step on the current picture; size may change
        void Add(Edit e)
        {
            edits.Add(e);
            var before = work.Size;
            work = Apply(work, e);
            undo.Enabled = true;
            if (work.Size != before) Relayout();
            else Invalidate();
        }

        static Bitmap Apply(Bitmap b, Edit e)
        {
            switch (e.Kind)
            {
                case EditKind.Blur: Pixelate(b, e.Box); return b;
                case EditKind.Draw:
                    using (var g = Graphics.FromImage(b)) DrawStroke(g, e.Points, e.StrokeColor, e.StrokeWidth, 1f, PointF.Empty);
                    return b;
                case EditKind.Crop:
                    var box = Rectangle.Intersect(e.Box, new Rectangle(0, 0, b.Width, b.Height));
                    if (box.Width < 2 || box.Height < 2) return b;
                    var cut = b.Clone(box, PixelFormat.Format32bppArgb);
                    b.Dispose();
                    return cut;
                case EditKind.Turn: b.RotateFlip(RotateFlipType.Rotate90FlipNone); return b;
                case EditKind.MirrorX: b.RotateFlip(RotateFlipType.RotateNoneFlipX); return b;
                case EditKind.MirrorY: b.RotateFlip(RotateFlipType.RotateNoneFlipY); return b;
            }
            return b;
        }

        internal static void DrawStroke(Graphics g, PointF[] pts, Color color, float width, float scale, PointF origin)
        {
            if (pts == null || pts.Length == 0) return;
            float w = Math.Max(1f, width * scale);
            bool moved = false;
            for (int i = 1; i < pts.Length; i++)
            {
                float dx = pts[i].X - pts[0].X, dy = pts[i].Y - pts[0].Y;
                if (dx * dx + dy * dy > 0.04f) { moved = true; break; }
            }
            var prevSmooth = g.SmoothingMode;
            var prevOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                if (!moved)
                {
                    float cx = origin.X + pts[0].X * scale, cy = origin.Y + pts[0].Y * scale;
                    using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - w / 2f, cy - w / 2f, w, w);
                    return;
                }
                var mapped = new PointF[pts.Length];
                for (int i = 0; i < pts.Length; i++) mapped[i] = new PointF(origin.X + pts[i].X * scale, origin.Y + pts[i].Y * scale);
                using (var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, mapped);
            }
            finally
            {
                g.SmoothingMode = prevSmooth;
                g.PixelOffsetMode = prevOffset;
            }
        }

        // Rebuilt from the original, no history copies
        void Undo()
        {
            if (edits.Count == 0) return;
            edits.RemoveAt(edits.Count - 1);
            var before = work.Size;
            work.Dispose();
            work = new Bitmap(original);
            foreach (var e in edits) work = Apply(work, e);
            undo.Enabled = edits.Count > 0;
            if (work.Size != before) Relayout();
            else Invalidate();
        }

        void CopyOut()
        {
            try { Clipboard.SetImage(work); }
            catch (Exception ex) { Log.Write("redact copy: " + ex.Message); return; }
            var h = Copied;
            if (h != null) h();
            Close();
        }

        void SaveOut()
        {
            using (var d = new SaveFileDialog { Filter = "PNG|*.png|JPEG|*.jpg", FileName = "gambar-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", RestoreDirectory = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { work.Save(d.FileName, d.FilterIndex == 2 ? ImageFormat.Jpeg : ImageFormat.Png); }
                catch (Exception ex) { Log.Write("redact save: " + ex.Message); return; }
            }
            Close();
        }

        // Blocks too big to read text back
        internal static void Pixelate(Bitmap bmp, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (r.Width < 2 || r.Height < 2) return;
            int block = Math.Max(10, Math.Min(48, Math.Min(r.Width, r.Height) / 3));
            var data = bmp.LockBits(r, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * r.Height];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int by = 0; by < r.Height; by += block)
                    for (int bx = 0; bx < r.Width; bx += block)
                    {
                        int ex = Math.Min(r.Width, bx + block), ey = Math.Min(r.Height, by + block);
                        long sb = 0, sg = 0, sr = 0, sa = 0, n = 0;
                        for (int y = by; y < ey; y++)
                            for (int x = bx, i = y * stride + bx * 4; x < ex; x++, i += 4) { sb += buf[i]; sg += buf[i + 1]; sr += buf[i + 2]; sa += buf[i + 3]; n++; }
                        byte b = (byte)(sb / n), gr = (byte)(sg / n), rd = (byte)(sr / n), a = (byte)(sa / n);
                        for (int y = by; y < ey; y++)
                            for (int x = bx, i = y * stride + bx * 4; x < ex; x++, i += 4) { buf[i] = b; buf[i + 1] = gr; buf[i + 2] = rd; buf[i + 3] = a; }
                    }
                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally { bmp.UnlockBits(data); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { original.Dispose(); work.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
