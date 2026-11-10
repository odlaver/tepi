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
    // Edit a picture before sharing: blur, crop, turn, mirror
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
        enum EditKind { Blur, Crop, Turn, MirrorX, MirrorY }

        struct Edit
        {
            public EditKind Kind;
            public Rectangle Box;
        }

        readonly Bitmap original;
        Bitmap work;
        // Replayed on the original: undo is one step fewer
        readonly List<Edit> edits = new List<Edit>();
        readonly Label hint = new Label();
        readonly KitSegmented mode = new KitSegmented();
        readonly KitButton copy = new KitButton(), save = new KitButton(), undo = new KitButton(), cancel = new KitButton(), turn = new KitButton(), mirrorX = new KitButton(), mirrorY = new KitButton();
        RectangleF canvas;
        float fit = 1;
        Point dragFrom;
        Rectangle dragBox;
        bool dragging;

        public event Action Copied;

        bool Cropping { get { return mode.Selected == 1; } }

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
            mode.Items = new[] { Lang.T("Samarkan"), Lang.T("Potong") };
            mode.AccessibleName = Lang.T("Alat");
            mode.Changed += i => { hint.Text = HintText(); Invalidate(); };
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
            foreach (Control c in new Control[] { hint, mode, turn, mirrorX, mirrorY, cancel, undo, save, copy }) { c.Cursor = Cursors.Default; Controls.Add(c); }
        }

        string HintText() { return Cropping ? Lang.T("Seret untuk memilih bagian yang disimpan.") : Lang.T("Seret di atas bagian yang mau disamarkan."); }

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
            foreach (var b in new[] { copy, save, undo, cancel, turn, mirrorX, mirrorY })
            {
                b.Font = UiFont(12.5f);
                b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + u(28), btnH);
            }
            mode.Size = new Size(u(190), btnH);
            // Wide enough for both button rows
            int tools = mode.Width + turn.Width + mirrorX.Width + mirrorY.Width + 3 * gap;
            int actions = cancel.Width + undo.Width + save.Width + copy.Width + 3 * gap;
            int cw = Math.Max(Math.Max(tools, actions), (int)(work.Width * fit)), ch = (int)(work.Height * fit);
            canvas = new RectangleF(m + (cw - work.Width * fit) / 2, m, work.Width * fit, ch);
            hint.Text = HintText();
            hint.Font = UiFont(12.5f);
            hint.ForeColor = KitInk.Sub;
            hint.BackColor = KitInk.Window;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            int y1 = m + ch + u(14);
            hint.SetBounds(m, y1, cw, u(22));
            // Tools on the left, actions on the right
            int y2 = y1 + u(30), x = m;
            foreach (Control c in new Control[] { mode, turn, mirrorX, mirrorY }) { c.Location = new Point(x, y2); x += c.Width + gap; }
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
            if (!dragging || dragBox.Width < 1 || dragBox.Height < 1) return;
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !canvas.Contains(e.Location)) return;
            dragging = true;
            dragFrom = ToImage(e.Location);
            dragBox = Rectangle.Empty;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
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
            int least = Cropping ? 8 : 4;
            if (dragBox.Width >= least && dragBox.Height >= least) Add(new Edit { Kind = Cropping ? EditKind.Crop : EditKind.Blur, Box = dragBox });
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keys == (Keys.Control | Keys.C) || keys == Keys.Enter) { CopyOut(); return true; }
            if (keys == (Keys.Control | Keys.S)) { SaveOut(); return true; }
            if (keys == Keys.Escape) { Close(); return true; }
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
