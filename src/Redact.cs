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
    // Blur parts of a picture before sharing
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
            w.Copied += delegate { if (!ShotsOn) ShowInfo('', Lang.T("Gambar tersamar disalin"), "", true); };
            w.Show();
            w.Activate();
        }
    }

    class RedactWindow : KitWindow
    {
        readonly Bitmap original, work;
        readonly List<Rectangle> boxes = new List<Rectangle>();
        readonly Label hint = new Label();
        readonly KitButton copy = new KitButton(), undo = new KitButton(), cancel = new KitButton();
        RectangleF canvas;
        float fit = 1;
        Point dragFrom;
        Rectangle dragBox;
        bool dragging;

        public event Action Copied;

        public RedactWindow(Bitmap pic)
        {
            original = pic;
            work = new Bitmap(pic);
            Text = Lang.T("Samarkan");
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Cross;
            hint.UseMnemonic = false;
            hint.AutoSize = false;
            copy.Text = Lang.T("Salin");
            copy.Primary = true;
            copy.Click += delegate { CopyOut(); };
            undo.Text = Lang.T("Urungkan");
            undo.Click += delegate { Undo(); };
            cancel.Text = Lang.T("Batal");
            cancel.Click += delegate { Close(); };
            foreach (Control c in new Control[] { hint, cancel, undo, copy }) { c.Cursor = Cursors.Default; Controls.Add(c); }
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
            float maxW = Math.Min(area.Width * 0.8f, u(1100)), maxH = Math.Min(area.Height * 0.72f, u(720));
            fit = Math.Min(1f, Math.Min(maxW / work.Width, maxH / work.Height));
            int m = u(20), cw = Math.Max(u(360), (int)(work.Width * fit)), ch = (int)(work.Height * fit);
            canvas = new RectangleF(m + (cw - work.Width * fit) / 2, m, work.Width * fit, ch);
            hint.Text = Lang.T("Seret di atas bagian yang mau disamarkan.");
            hint.Font = UiFont(12.5f);
            hint.ForeColor = KitInk.Sub;
            hint.BackColor = KitInk.Window;
            int btnH = u(30), by = m + ch + u(16);
            foreach (var b in new[] { copy, undo, cancel })
            {
                b.Font = UiFont(12.5f);
                b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + u(32), btnH);
                b.Top = by;
            }
            copy.Left = m + cw - copy.Width;
            undo.Left = copy.Left - u(8) - undo.Width;
            cancel.Left = undo.Left - u(8) - cancel.Width;
            undo.Enabled = boxes.Count > 0;
            hint.SetBounds(m, by, Math.Max(0, cancel.Left - m - u(12)), btnH);
            hint.TextAlign = ContentAlignment.MiddleLeft;
            ClientSize = new Size(cw + 2 * m, by + btnH + u(18));
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
            if (dragBox.Width >= 4 && dragBox.Height >= 4)
            {
                boxes.Add(dragBox);
                Pixelate(work, dragBox);
                undo.Enabled = true;
            }
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keys == (Keys.Control | Keys.C) || keys == Keys.Enter) { CopyOut(); return true; }
            return base.ProcessCmdKey(ref msg, keys);
        }

        // Rebuilt from the original, no history copies
        void Undo()
        {
            if (boxes.Count == 0) return;
            boxes.RemoveAt(boxes.Count - 1);
            using (var g = Graphics.FromImage(work))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(original, 0, 0);
            }
            foreach (var b in boxes) Pixelate(work, b);
            undo.Enabled = boxes.Count > 0;
            Invalidate();
        }

        void CopyOut()
        {
            try { Clipboard.SetImage(work); }
            catch (Exception ex) { Log.Write("redact copy: " + ex.Message); return; }
            var h = Copied;
            if (h != null) h();
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
