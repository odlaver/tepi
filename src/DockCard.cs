using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Glass card above the dock: live window previews, or the tray grid.
        // Not layered, so DWM can draw live thumbnails on it.
        sealed class DockCard : Form
        {
            public const int WindowsKind = 1, TrayKind = 2;
            public readonly int Kind;
            public DockApp App;
            readonly DockWindow dock;
            readonly Island isle;
            readonly Painter p = new Painter();
            float scale = 1;
            IntPtr memDC, dib, oldBmp;
            Bitmap canvas;
            Graphics g;
            int cw, ch;
            Rectangle anchor, mon;
            int barTop, baseY;
            DockFrost frost;
            DockPlate plate;
            readonly Timer closeSoon = new Timer { Interval = 320 }, frames = new Timer { Interval = 15 };
            readonly Spring open = new Spring(0, 1, 1, 0.003f);
            DateTime lastStep = DateTime.UtcNow;
            string hot, press;
            bool over, leaving;

            sealed class Thumb
            {
                public DockWin Win;
                public IntPtr Id;
                public RectangleF Box, Pic, Close;
            }

            readonly List<Thumb> thumbs = new List<Thumb>();
            List<TrayIcon> icons = new List<TrayIcon>();
            readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
            const float Pad = 10, ThumbH = 112, TitleH = 24, Cell = 34;

            [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }

            [StructLayout(LayoutKind.Sequential)]
            struct ThumbProps
            {
                public int Flags;
                public Native.RECT Dest, Source;
                public byte Opacity;
                public bool Visible, ClientOnly;
            }

            [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
            [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr id);
            [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr id);
            [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr id, ref ThumbProps props);
            [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr id, out Native.SIZE size);
            [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

            public DockCard(Island isle, DockWindow dock, int kind)
            {
                this.isle = isle;
                this.dock = dock;
                Kind = kind;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                Text = "Tepi Dock";
                Bounds = new Rectangle(-32000, -32000, 10, 10);
                Tune(open, 420, 0.86f);
                closeSoon.Tick += delegate { closeSoon.Stop(); if (!over) dock.CardDone(this); };
                frames.Tick += delegate { Step(); };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = Native.WS_POPUP;
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                    return cp;
                }
            }

            protected override bool ShowWithoutActivation { get { return true; } }
            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Whole client is frame: per-pixel alpha, thumbnails on top
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(Handle, ref m);
                int none = unchecked((int)0xFFFFFFFE), square = 1;
                DwmSetWindowAttribute(Handle, 34, ref none, 4);
                DwmSetWindowAttribute(Handle, 33, ref square, 4);
                if (dock.Glass) Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
                base.WndProc(ref m);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                closeSoon.Stop();
                frames.Stop();
                if (frost != null) frost.Stop();
                DropThumbs();
                Free();
                if (plate != null) plate.Dispose();
                base.OnFormClosed(e);
            }

            public bool Covers(DockApp a) { return Kind == WindowsKind && App == a; }

            bool allowClose;

            // Sinks and fades, then closes
            public void Dismiss()
            {
                if (leaving) return;
                closeSoon.Stop();
                if (!Visible || Anim.Reduced || g == null || open.Value < 0.05f) { Finish(); return; }
                leaving = true;
                Tune(open, 2600, 1f);
                open.Target = 0;
                lastStep = DateTime.UtcNow;
                frames.Start();
            }

            void Finish()
            {
                allowClose = true;
                Close();
                Dispose();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
                base.OnFormClosing(e);
            }

            // Previews go quickly; the tray grid waits a little longer
            public void CloseSoon()
            {
                closeSoon.Stop();
                closeSoon.Interval = Kind == TrayKind ? 1200 : 320;
                closeSoon.Start();
            }

            public void Keep() { closeSoon.Stop(); }

            // Windows of one app; switching apps keeps the card up
            public void ShowApp(DockApp app, Rectangle at, Rectangle monitor, int top, float sc)
            {
                // Thumbnails need a window to land on before the first paint
                if (!IsHandleCreated) CreateHandle();
                closeSoon.Stop();
                bool same = App == app;
                App = app;
                anchor = at;
                mon = monitor;
                barTop = top;
                scale = sc;
                if (!same) DropThumbs();
                Refill();
                if (!Visible) Appear();
            }

            public void ShowTray(List<TrayIcon> list, Rectangle at, Rectangle monitor, int top, float sc)
            {
                if (!IsHandleCreated) CreateHandle();
                icons = list;
                anchor = at;
                mon = monitor;
                barTop = top;
                scale = sc;
                Refill();
                if (!Visible) Appear();
            }

            void Appear()
            {
                open.Snap(0);
                open.Target = 1;
                lastStep = DateTime.UtcNow;
                Show();
                frames.Start();
            }

            // Content changed: titles, windows, icons
            public void Refill()
            {
                if (IsDisposed || leaving) return;
                if (Kind == TrayKind) icons = dock.TrayOverflow();
                if (Kind == WindowsKind)
                {
                    if (App == null || App.Wins.Count == 0) { dock.CardDone(this); return; }
                    var keep = new List<Thumb>();
                    foreach (var w in App.Wins)
                    {
                        if (keep.Count >= 6) break;
                        var t = thumbs.Find(x => x.Win.H == w.H) ?? new Thumb { Win = w };
                        keep.Add(t);
                    }
                    foreach (var t in thumbs) if (!keep.Contains(t) && t.Id != IntPtr.Zero) DwmUnregisterThumbnail(t.Id);
                    thumbs.Clear();
                    thumbs.AddRange(keep);
                }
                Arrange();
            }

            void DropThumbs()
            {
                foreach (var t in thumbs) if (t.Id != IntPtr.Zero) DwmUnregisterThumbnail(t.Id);
                thumbs.Clear();
            }

            // Size from content, above the anchor, inside the monitor
            void Arrange()
            {
                float w, h;
                if (Kind == WindowsKind)
                {
                    float x = Pad;
                    foreach (var t in thumbs)
                    {
                        if (t.Id == IntPtr.Zero && IsHandleCreated) DwmRegisterThumbnail(Handle, t.Win.H, out t.Id);
                        Native.SIZE src = new Native.SIZE(16, 10);
                        if (t.Id != IntPtr.Zero) DwmQueryThumbnailSourceSize(t.Id, out src);
                        float aspect = src.CY > 0 ? src.CX / (float)src.CY : 1.6f;
                        float pw = Math.Max(96, Math.Min(210, ThumbH * aspect)), ph = Math.Min(ThumbH, pw / Math.Max(0.2f, aspect));
                        float bw = pw + 12;
                        t.Box = new RectangleF(x, Pad, bw, TitleH + ThumbH + 10);
                        t.Pic = new RectangleF(x + 6, Pad + TitleH + 4 + (ThumbH - ph) / 2, pw, ph);
                        t.Close = new RectangleF(t.Box.Right - 26, t.Box.Y + 3, 22, 20);
                        x += bw + 6;
                    }
                    w = x - 6 + Pad;
                    h = Pad * 2 + TitleH + ThumbH + 10;
                }
                else
                {
                    int cols = Math.Max(1, Math.Min(5, icons.Count));
                    int rows = Math.Max(1, (icons.Count + cols - 1) / cols);
                    w = Math.Max(Pad * 2 + cols * Cell, 176);
                    h = Pad * 2 + rows * Cell + 34;
                }
                int pw2 = (int)Math.Ceiling(w * scale), ph2 = (int)Math.Ceiling(h * scale);
                int cx = anchor.X + anchor.Width / 2;
                int left = Math.Max(mon.Left + (int)(8 * scale), Math.Min(mon.Right - (int)(8 * scale) - pw2, cx - pw2 / 2));
                // Above the bar, and above a magnified icon too
                int top = Math.Min(barTop, anchor.Top) - (int)(10 * scale) - ph2;
                baseY = top;
                if (pw2 != cw || ph2 != ch) MakeCanvas(pw2, ph2);
                Bounds = new Rectangle(left, top + Rise(), pw2, ph2);
                if (dock.Glass)
                {
                    if (frost == null)
                    {
                        frost = new DockFrost(pl => { try { BeginInvoke(new Action(() => TakePlate(pl))); } catch (Exception) { pl.Dispose(); } });
                        frost.Start();
                    }
                    frost.Request(new Rectangle(left, top, pw2, ph2), Ink.Veil, Ink.Light, isle.LiquidOn);
                }
                Render();
            }

            int Rise() { return (int)Math.Round((1 - open.Value) * 8 * scale); }

            void TakePlate(DockPlate pl)
            {
                if (IsDisposed) { pl.Dispose(); return; }
                var old = plate;
                plate = pl;
                if (old != null) old.Dispose();
                Render();
            }

            void Step()
            {
                var now = DateTime.UtcNow;
                float dt = (float)Math.Min(0.05, (now - lastStep).TotalSeconds);
                lastStep = now;
                open.Step(dt);
                if (leaving && open.Value < 0.03f) { frames.Stop(); Finish(); return; }
                Location = new Point(Location.X, baseY + Rise());
                Render();
                if (open.Settled) frames.Stop();
            }

            void MakeCanvas(int w, int h)
            {
                Free();
                cw = w;
                ch = h;
                var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                memDC = Native.CreateCompatibleDC(screen);
                Native.ReleaseDC(IntPtr.Zero, screen);
                IntPtr bits;
                dib = Native.CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                oldBmp = Native.SelectObject(memDC, dib);
                canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                g = Graphics.FromImage(canvas);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                p.G = g;
            }

            void Free()
            {
                if (g == null) return;
                g.Dispose();
                canvas.Dispose();
                Native.SelectObject(memDC, oldBmp);
                Native.DeleteObject(dib);
                Native.DeleteDC(memDC);
                g = null;
            }

            void Render()
            {
                if (g == null || IsDisposed) return;
                hits.Clear();
                p.T = dock.Glass ? Ink.Base.OnGlass() : Ink.Base;
                p.HalfPx = 0.5f / scale;
                float fade = Ease.Clamp01(open.Value);
                p.Alpha = fade;
                g.ResetTransform();
                g.Clear(Color.Transparent);
                g.ScaleTransform(scale, scale);
                var r = new RectangleF(0.5f, 0.5f, cw / scale - 1, ch / scale - 1);
                float radius = 16;
                using (var path = Ink.Round(r, radius))
                {
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    if (plate != null && dock.Glass)
                    {
                        var inner = g.Save();
                        g.ResetTransform();
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
                            var o = Location;
                            g.DrawImage(plate.Text, new Rectangle(plate.Rect.X - o.X, plate.Rect.Y - o.Y + Rise(), plate.Rect.Width, plate.Rect.Height), 0, 0, plate.Text.Width, plate.Text.Height, GraphicsUnit.Pixel, ia);
                        }
                        g.Restore(inner);
                    }
                    g.FillPath(p.B(Ink.A(Ink.Veil, plate != null && dock.Glass ? 0.3f : 0.94f)), path);
                    using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, 40), Color.FromArgb((int)(fade * (Ink.Light ? 80 : 22)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        g.FillRectangle(lg, r.X, r.Y, r.Width, 39);
                    g.Restore(st);
                    g.DrawPath(p.P(Color.FromArgb(Ink.Light ? 70 : 40, 255, 255, 255), 1f), path);
                }
                if (Kind == WindowsKind) DrawWindows(fade);
                else DrawTray();
                p.Alpha = 1;
                g.Flush(FlushIntention.Sync);
                if (IsHandleCreated)
                {
                    using (var wg = CreateGraphics())
                    {
                        IntPtr hdc = wg.GetHdc();
                        BitBlt(hdc, 0, 0, cw, ch, memDC, 0, 0, 0x00CC0020);
                        wg.ReleaseHdc(hdc);
                    }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (g == null) return;
                IntPtr hdc = e.Graphics.GetHdc();
                BitBlt(hdc, 0, 0, cw, ch, memDC, 0, 0, 0x00CC0020);
                e.Graphics.ReleaseHdc(hdc);
            }

            void DrawWindows(float fade)
            {
                foreach (var t in thumbs)
                {
                    string id = "w" + t.Win.H.ToInt64();
                    bool on = hot == id || hot == "x" + id;
                    if (on) p.FillRound(t.Box, 11, press == id ? p.T.FillPressed : p.T.FillHover);
                    hits.Add(new KeyValuePair<RectangleF, string>(t.Box, id));
                    float tx = t.Box.X + 8, ty = t.Box.Y + TitleH / 2 + 2;
                    // The app's icon before the title, as the taskbar shows it
                    var mark = dock.IconOf(App);
                    if (mark != null) { g.DrawImage(mark, new RectangleF(tx, ty - 8, 16, 16)); tx += 22; }
                    p.Text(t.Win.Title, Ink.Semi(11.5f), p.T.Text, tx, ty, t.Box.Right - tx - (on ? 32 : 8), StringAlignment.Near);
                    if (on)
                    {
                        hits.Add(new KeyValuePair<RectangleF, string>(t.Close, "x" + id));
                        if (hot == "x" + id) p.FillRound(t.Close, 6, BadgeRedSoft);
                        p.Icon('\uE711', 9.5f, hot == "x" + id ? Color.White : p.T.Sub, t.Close.X + t.Close.Width / 2, t.Close.Y + t.Close.Height / 2);
                    }
                    // Seen only when Windows has no live picture of the window
                    p.FillRound(t.Pic, 8, p.T.Fill);
                    if (mark != null) g.DrawImage(mark, new RectangleF(t.Pic.X + t.Pic.Width / 2 - 20, t.Pic.Y + t.Pic.Height / 2 - 20, 40, 40));
                    if (t.Id == IntPtr.Zero) continue;
                    // DWM paints the live window over this spot
                    var d = t.Pic;
                    var props = new ThumbProps
                    {
                        Flags = 0x1 | 0x4 | 0x8 | 0x10,
                        Dest = new Native.RECT { Left = (int)(d.X * scale), Top = (int)(d.Y * scale), Right = (int)(d.Right * scale), Bottom = (int)(d.Bottom * scale) },
                        Opacity = (byte)(255 * fade),
                        Visible = true,
                        ClientOnly = false
                    };
                    DwmUpdateThumbnailProperties(t.Id, ref props);
                }
            }

            static readonly Color BadgeRedSoft = Color.FromArgb(230, 196, 43, 28);

            void DrawTray()
            {
                int cols = Math.Max(1, Math.Min(5, icons.Count));
                float x0 = (cw / scale - cols * Cell) / 2;
                string tip = "";
                for (int i = 0; i < icons.Count; i++)
                {
                    var ic = icons[i];
                    var cell = new RectangleF(x0 + (i % cols) * Cell, Pad + (i / cols) * Cell, Cell, Cell);
                    string id = "t" + i;
                    hits.Add(new KeyValuePair<RectangleF, string>(cell, id));
                    ic.Spot = ToScreen(cell);
                    if (hot == id) { p.FillRound(Inset(cell, 2), 8, press == id ? p.T.FillPressed : p.T.FillHover); tip = FirstLine(ic.Tip); }
                    var bmp = dock.TrayPicture(ic);
                    if (bmp != null) g.DrawImage(bmp, new RectangleF(cell.X + (Cell - 16) / 2, cell.Y + (Cell - 16) / 2, 16, 16));
                }
                // Footer: the hovered icon's name, and where Windows sorts them
                float fy = ch / scale - Pad - 13;
                var gear = new RectangleF(cw / scale - Pad - 26, fy - 12, 26, 24);
                hits.Add(new KeyValuePair<RectangleF, string>(gear, "gear"));
                if (hot == "gear") p.FillRound(gear, 7, p.T.FillHover);
                p.Icon('\uE713', 12f, hot == "gear" ? p.T.Text : p.T.Sub, gear.X + gear.Width / 2, gear.Y + gear.Height / 2);
                p.Text(tip.Length > 0 ? tip : Lang.T("Ikon lain di tray"), Ink.Small(11f), p.T.Sub, Pad + 2, fy, gear.X - Pad - 6, StringAlignment.Near);
            }

            static RectangleF Inset(RectangleF r, float d) { return new RectangleF(r.X + d, r.Y + d, r.Width - d * 2, r.Height - d * 2); }

            static string FirstLine(string s)
            {
                int n = s.IndexOfAny(new[] { '\r', '\n' });
                return (n >= 0 ? s.Substring(0, n) : s).Trim();
            }

            Rectangle ToScreen(RectangleF r)
            {
                var o = Location;
                return new Rectangle(o.X + (int)(r.X * scale), o.Y + (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale));
            }

            string HitAt(Point pt)
            {
                var d = new PointF(pt.X / scale, pt.Y / scale);
                for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(d)) return hits[i].Value;
                return null;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                if (leaving) return;
                over = true;
                closeSoon.Stop();
                dock.HoldOpen(true);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (leaving) return;
                over = false;
                hot = null;
                press = null;
                dock.HoldOpen(false);
                CloseSoon();
                Render();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (leaving) return;
                string id = HitAt(e.Location);
                if (id == hot) return;
                hot = id;
                Render();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (leaving) return;
                press = HitAt(e.Location);
                var ic = TrayAt(press);
                if (ic != null) dock.TrayDown(ic, e.Button);
                Render();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (leaving) return;
                string id = HitAt(e.Location);
                string was = press;
                press = null;
                if (id == null || id != was) { Render(); return; }
                var ic = TrayAt(id);
                if (ic != null) { dock.TrayUp(ic, e.Button); if (e.Button == MouseButtons.Left) dock.CardDone(this); return; }
                if (id == "gear") { dock.CardDone(this); OpenSettings(); return; }
                var t = thumbs.Find(x => id.EndsWith("w" + x.Win.H.ToInt64(), StringComparison.Ordinal));
                if (t == null) return;
                if (id.StartsWith("x", StringComparison.Ordinal) || e.Button == MouseButtons.Middle) { ShellApp.Close(t.Win.H); return; }
                if (e.Button == MouseButtons.Left) { ShellApp.Activate(t.Win.H); dock.CardDone(this); }
            }

            TrayIcon TrayAt(string id)
            {
                int i;
                if (Kind != TrayKind || id == null || id.Length < 2 || id[0] != 't' || !int.TryParse(id.Substring(1), out i)) return null;
                return i < icons.Count ? icons[i] : null;
            }

            static void OpenSettings()
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("dock tray settings: " + ex.Message); }
            }
        }
    }
}
