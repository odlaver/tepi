using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Screen colour picker: click copies the hex
    partial class Island
    {
        const int PickerHotkey = 15;
        PickerOverlay picker;

        void OpenPicker()
        {
            if (picker != null && !picker.IsDisposed) return;
            picker = new PickerOverlay();
            picker.Picked += c =>
            {
                string hex = HexOf(c);
                try { Clipboard.SetText(hex); }
                catch (Exception ex) { Log.Write("picker copy: " + ex.Message); return; }
                ShowInfo('\uE790', Lang.T("Warna disalin"), hex, true);
            };
            picker.Show();
            picker.Activate();
        }
    }

    // Near-invisible layer over every screen; takes the click
    class PickerOverlay : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        const uint WdaExclude = 0x11;
        const int Grab = 11, Zoom = 10;

        public event Action<Color> Picked;
        readonly Loupe loupe = new Loupe();
        readonly Bitmap sample = new Bitmap(Grab, Grab);

        public PickerOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Black;
            // Fully clear windows miss clicks
            Opacity = 0.01;
            Cursor = Cursors.Cross;
            KeyPreview = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Our own layers stay out of the sample
            SetWindowDisplayAffinity(Handle, WdaExclude);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            loupe.Show(this);
            Follow(Cursor.Position);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Follow(Cursor.Position);
        }

        // One small screen copy per move
        void Follow(Point at)
        {
            try
            {
                using (var g = Graphics.FromImage(sample)) g.CopyFromScreen(at.X - Grab / 2, at.Y - Grab / 2, 0, 0, sample.Size);
            }
            catch (Exception) { return; }
            loupe.Show(sample, sample.GetPixel(Grab / 2, Grab / 2), at, Zoom);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                Follow(Cursor.Position);
                var c = sample.GetPixel(Grab / 2, Grab / 2);
                Close();
                var h = Picked;
                if (h != null) h(Color.FromArgb(255, c));
                return;
            }
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        // Forgotten picker gives the screen back
        readonly Timer giveUp = new Timer { Interval = 60000 };

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            giveUp.Tick += delegate { Close(); };
            giveUp.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { giveUp.Dispose(); loupe.Dispose(); sample.Dispose(); }
            base.Dispose(disposing);
        }

        // Zoomed pixels and the hex, beside the cursor
        class Loupe : Form
        {
            Bitmap view;
            Color color;
            const int Pad = 6, TextH = 26;

            public Loupe()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(18, 18, 18);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Size = new Size(Grab * Zoom + Pad * 2, Grab * Zoom + Pad * 2 + TextH);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    // Never focused, no taskbar button
                    cp.ExStyle |= 0x08000000 | 0x80;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                SetWindowDisplayAffinity(Handle, WdaExclude);
            }

            public void Show(Bitmap src, Color c, Point at, int zoom)
            {
                if (view != null) view.Dispose();
                view = (Bitmap)src.Clone();
                color = c;
                // Flip near screen edges
                var area = Screen.FromPoint(at).Bounds;
                int x = at.X + 24, y = at.Y + 24;
                if (x + Width > area.Right) x = at.X - 24 - Width;
                if (y + Height > area.Bottom) y = at.Y - 24 - Height;
                Location = new Point(x, y);
                Invalidate();
                Update();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                if (view == null) return;
                int side = Grab * Zoom;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(view, new Rectangle(Pad, Pad, side, side));
                int mid = Pad + (Grab / 2) * Zoom;
                using (var pen = new Pen(Color.White, 2)) g.DrawRectangle(pen, mid, mid, Zoom, Zoom);
                using (var sw = new SolidBrush(color)) g.FillRectangle(sw, Pad, Pad + side + 7, 14, 14);
                // Dark picks stay visible on the dark loupe
                using (var edge = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawRectangle(edge, Pad, Pad + side + 7, 14, 14);
                using (var f = new Font("Segoe UI Semibold", 10f, GraphicsUnit.Point))
                    TextRenderer.DrawText(g, Island.HexOf(color), f, new Point(Pad + 20, Pad + side + 5), Color.White);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && view != null) view.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
