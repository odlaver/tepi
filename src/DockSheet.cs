using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Glass card above the dock
        abstract class GlassSheet : Form
        {
            public readonly string Kind;
            protected readonly Island isle;
            protected readonly DockWindow dock;
            protected readonly Painter p = new Painter();
            protected float scale = 1, W = 360, H = 400;
            protected string hotId, pressId;
            string tipText;
            RectangleF tipAt;
            protected bool menuOpen;
            protected Graphics g;
            protected readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
            protected readonly Spring open = new Spring(0, 1, 1, 0.003f);
            IntPtr memDC, dib, oldBmp;
            Bitmap canvas;
            int cw, ch, baseY;
            DockFrost frost;
            DockPlate plate;
            bool allowClose, leaving;
            readonly System.Windows.Forms.Timer frames = new System.Windows.Forms.Timer { Interval = 15 };
            DateTime lastStep;

            // Fading out: no more input
            protected bool Leaving { get { return leaving; } }

            [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
            [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hw, ref Margins m);
            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hw, int attr, ref int value, int size);
            [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int hh, IntPtr src, int sx, int sy, int rop);

            protected GlassSheet(Island isle, DockWindow dock, string kind, string title)
            {
                Kind = kind;
                this.isle = isle;
                this.dock = dock;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                KeyPreview = true;
                Text = title;
                Bounds = new Rectangle(-32000, -32000, 10, 10);
                Tune(open, 420, 0.86f);
                frames.Tick += delegate { Step(); };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = Native.WS_POPUP;
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                    return cp;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override bool ShowWithoutActivation { get { return DockWindow.Dry; } }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(Handle, ref m);
                int none = unchecked((int)0xFFFFFFFE), square = 1;
                DwmSetWindowAttribute(Handle, 34, ref none, 4);
                DwmSetWindowAttribute(Handle, 33, ref square, 4);
                if (dock.Glass) Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            }

            // Sinks and fades, then closes
            public void Dismiss()
            {
                if (leaving) return;
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

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                frames.Stop();
                if (frost != null) frost.Stop();
                Teardown();
                Free();
                if (plate != null) plate.Dispose();
                base.OnFormClosed(e);
            }

            // Focus left: close
            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                if (!menuOpen && !allowClose) BeginInvoke(new Action(Done));
            }

            protected virtual void Opening(Rectangle mon, int barTop) { }
            protected virtual void Teardown() { }
            protected abstract void Draw();
            protected virtual void Backdrop(RectangleF r) { }
            protected virtual void Clicked(string id, MouseButtons button) { }
            protected virtual bool Drag(string id, PointF at) { return false; }
            protected virtual void Wheel(string id, int delta) { }
            protected virtual void HoverMoved(string id) { }
            // Own motion; true while running
            protected virtual bool Animating(float dt) { return false; }

            protected void Animate()
            {
                if (frames.Enabled) return;
                lastStep = DateTime.UtcNow;
                frames.Start();
            }

            // Over its button, within the dock
            public void OpenAt(Rectangle anchor, Rectangle span, Rectangle mon, int barTop, float sc)
            {
                scale = sc;
                Opening(mon, barTop);
                H = Math.Min(H, (barTop - mon.Top) / scale - 40);
                if (!IsHandleCreated) CreateHandle();
                int pw = (int)Math.Ceiling(W * scale), ph = (int)Math.Ceiling(H * scale);
                int want = anchor.X + anchor.Width / 2 - pw / 2;
                // Wider than the dock: centred
                if (!span.IsEmpty) want = pw <= span.Width ? Math.Max(span.Left, Math.Min(span.Right - pw, want)) : span.Left + (span.Width - pw) / 2;
                int left = Math.Max(mon.Left + (int)(8 * scale), Math.Min(mon.Right - (int)(8 * scale) - pw, want));
                baseY = Math.Min(barTop, anchor.Top) - (int)(10 * scale) - ph;
                MakeCanvas(pw, ph);
                Bounds = new Rectangle(left, baseY + Rise(), pw, ph);
                if (dock.Glass)
                {
                    frost = new DockFrost(pl => { try { BeginInvoke(new Action(() => TakePlate(pl))); } catch (Exception) { pl.Dispose(); } });
                    frost.Start();
                    frost.Request(new Rectangle(left, baseY, pw, ph), Ink.Veil, Ink.Light, isle.LiquidOn);
                }
                open.Snap(0);
                open.Target = 1;
                lastStep = DateTime.UtcNow;
                Render();
                Show();
                frames.Start();
                // Test runs leave the user's focus alone
                if (DockWindow.Dry) return;
                // Keyboard opens too: take focus
                ShellApp.Activate(Handle);
                Activate();
            }

            int Rise() { return (int)Math.Round((1 - open.Value) * 8 * scale); }

            void Step()
            {
                var now = DateTime.UtcNow;
                float dt = (float)Math.Min(0.05, (now - lastStep).TotalSeconds);
                lastStep = now;
                open.Step(dt);
                if (leaving && open.Value < 0.03f) { frames.Stop(); Finish(); return; }
                bool more = !leaving && Animating(dt);
                Location = new Point(Location.X, baseY + Rise());
                Render();
                if (open.Settled && !more) frames.Stop();
            }

            void TakePlate(DockPlate pl)
            {
                if (IsDisposed) { pl.Dispose(); return; }
                var old = plate;
                plate = pl;
                if (old != null) old.Dispose();
                Render();
            }

            void MakeCanvas(int w, int hh)
            {
                Free();
                cw = w;
                ch = hh;
                var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -hh, biPlanes = 1, biBitCount = 32 };
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                memDC = Native.CreateCompatibleDC(screen);
                Native.ReleaseDC(IntPtr.Zero, screen);
                IntPtr bits;
                dib = Native.CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                oldBmp = Native.SelectObject(memDC, dib);
                canvas = new Bitmap(w, hh, w * 4, PixelFormat.Format32bppPArgb, bits);
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

            protected void Render()
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
                using (var path = Ink.Round(r, 20))
                {
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    bool glass = plate != null && dock.Glass;
                    if (glass)
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
                    g.FillPath(p.B(Ink.A(Ink.Veil, glass ? 0.34f : 0.95f)), path);
                    using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, 58), Color.FromArgb((int)(fade * (Ink.Light ? 80 : 22)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    {
                        // No wrap: a tiled gradient leaves a bright seam
                        lg.WrapMode = WrapMode.TileFlipXY;
                        g.FillRectangle(lg, r.X, r.Y, r.Width, 54);
                    }
                    Backdrop(r);
                    g.Restore(st);
                    g.DrawPath(p.P(Color.FromArgb(Ink.Light ? 70 : 40, 255, 255, 255), 1f), path);
                }
                tipText = null;
                Draw();
                if (tipText != null) DrawTip();
                p.Alpha = 1;
                g.Flush(FlushIntention.Sync);
                if (!IsHandleCreated) return;
                using (var wg = CreateGraphics())
                {
                    IntPtr hdc = wg.GetHdc();
                    BitBlt(hdc, 0, 0, cw, ch, memDC, 0, 0, 0x00CC0020);
                    wg.ReleaseHdc(hdc);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (g == null) return;
                IntPtr hdc = e.Graphics.GetHdc();
                BitBlt(hdc, 0, 0, cw, ch, memDC, 0, 0, 0x00CC0020);
                e.Graphics.ReleaseHdc(hdc);
            }

            // Darker footer shelf
            protected void Shelf(RectangleF r, float height)
            {
                g.FillRectangle(p.B(Ink.A(Ink.Veil, 0.35f)), r.X, H - height, r.Width, height);
                // Solid: a hairline marks the footer
                if (!dock.Glass) g.DrawLine(p.P(p.T.Line, 1f), r.X, H - height, r.Right, H - height);
            }

            protected void Hit(RectangleF r, string id) { hits.Add(new KeyValuePair<RectangleF, string>(r, id)); }

            protected PointF Local(Point pt) { return new PointF(pt.X / scale, pt.Y / scale); }

            protected string HitAt(Point pt)
            {
                var d = Local(pt);
                for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(d)) return hits[i].Value;
                return null;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (leaving) return;
                if (pressId != null && e.Button == MouseButtons.Left && Drag(pressId, Local(e.Location))) { Render(); return; }
                string id = HitAt(e.Location);
                HoverMoved(id);
                if (id == hotId) return;
                hotId = id;
                Render();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (leaving) return;
                hotId = null;
                HoverMoved(null);
                Render();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (leaving) return;
                pressId = HitAt(e.Location);
                if (pressId != null && e.Button == MouseButtons.Left) Drag(pressId, Local(e.Location));
                Render();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (leaving) return;
                string id = HitAt(e.Location), was = pressId;
                pressId = null;
                if (id == null || id != was) { Render(); return; }
                Clicked(id, e.Button);
                if (!IsDisposed) Render();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (leaving) return;
                Wheel(HitAt(e.Location), e.Delta);
                Render();
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (leaving) return true;
                if (keyData == Keys.Escape) { Done(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            protected void ShowMenu(ContextMenuStrip m)
            {
                MenuLook.Apply(m);
                menuOpen = true;
                m.Closed += delegate
                {
                    menuOpen = false;
                    BeginInvoke(new Action(delegate
                    {
                        m.Dispose();
                        if (!IsDisposed && Form.ActiveForm != this) Done();
                    }));
                };
                m.Show(Cursor.Position);
            }

            protected static void Open(string what, string args)
            {
                try { Process.Start(new ProcessStartInfo(what, args ?? "") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("sheet open " + what + ": " + ex.Message); }
            }

            protected void Done() { dock.SheetDone(this); }

            // Tepi's controls, in the island's look
            // More: the label opens a list
            protected void Toggle(float cx, float cy, float d, char glyph, bool on, bool available, string label, string id, string more = null)
            {
                float r = d / 2;
                if (available) Hit(new RectangleF(cx - 44, cy - r - 6, 88, more != null ? d + 10 : d + 32), id);
                bool hover = available && hotId == id, down = available && pressId == id;
                var circle = new RectangleF(cx - r, cy - r, d, d);
                Color fill = on ? (down ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.22f) : hover ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f) : p.T.Accent)
                                : down ? p.T.FillPressed : hover ? p.T.FillHover : p.T.Fill;
                if (on) p.Circle(cx, cy, r, fill);
                else p.Module(circle, r, fill);
                p.Icon(glyph, 17f, on ? p.T.OnAccent : available ? p.T.Text : p.T.Quaternary, cx, cy);
                if (more == null || !available)
                {
                    p.TextCenter(label, Ink.Caption, available ? p.T.Sub : p.T.Faint, cx, cy + r + 13);
                    return;
                }
                float tw = p.Measure(label, Ink.Caption);
                var chip = new RectangleF(cx - tw / 2 - 10, cy + r + 3, tw + 30, 21);
                Hit(chip, more);
                if (hotId == more) p.FillRound(chip, 10.5f, pressId == more ? p.T.FillPressed : p.T.FillHover);
                p.TextCenter(label, Ink.Caption, hotId == more ? p.T.Text : p.T.Sub, cx - 5, cy + r + 13);
                p.Icon((char)0xE70D, 8f, p.T.Sub, cx + tw / 2 + 7, cy + r + 13.5f);
            }

            protected void Slider(RectangleF r, string id, float value, char glyph, string label, string tail, string iconId)
            {
                value = Ease.Clamp01(value);
                Hit(r, id);
                bool hover = pressId == id || hotId == id || (iconId != null && hotId == iconId);
                float radius = Math.Min(Ink.RadiusModule, r.Height / 2), cy = r.Y + r.Height / 2;
                float at = r.X + r.Width * value, ix = r.X + 25;
                p.Module(r, radius, hover ? p.T.FillHover : p.T.Fill);
                if (value > 0.001f) p.FillRound(r, radius, p.Split(r.X, r.Width, at, p.T.Accent, Color.Transparent));
                if (iconId != null)
                {
                    Hit(new RectangleF(r.X + 3, r.Y + 3, 44, r.Height - 6), iconId);
                    if (hotId == iconId) p.Circle(ix, cy, 16, pressId == iconId ? Ink.A(p.T.Text, 0.1f) : Ink.A(p.T.Text, 0.14f));
                }
                p.Icon(glyph, 17f, p.Split(r.X, r.Width, at, p.T.OnAccent, p.T.Text), ix, cy);
                if (label != null) p.Text(label, Ink.Semi(13.5f), p.Split(r.X, r.Width, at, p.T.OnAccent, p.T.Text), r.X + 46, cy, r.Width - 110, StringAlignment.Near);
                if (tail != null) p.Text(tail, Ink.Semi(12.5f), p.Split(r.X, r.Width, at, Ink.A(p.T.OnAccent, 0.72f), p.T.Sub), r.Right - 16, cy, 60, StringAlignment.Far);
            }

            protected static float SliderValue(RectangleF r, PointF at) { return Ease.Clamp01((at.X - r.X) / r.Width); }

            // Name of an icon button, shown on hover
            protected void Tip(RectangleF r, string text)
            {
                tipText = text;
                tipAt = r;
            }

            void DrawTip()
            {
                var f = Ink.Semi(12f);
                float w = Math.Min(W - 16, p.Measure(tipText, f) + 22), hh = 26;
                float cx = Math.Max(8 + w / 2, Math.Min(W - 8 - w / 2, tipAt.X + tipAt.Width / 2));
                float top = tipAt.Y - 6 - hh;
                if (top < 6) top = tipAt.Bottom + 6;
                var r = new RectangleF(cx - w / 2, top, w, hh);
                p.FillRound(r, 9, Ink.A(Ink.Base.Bg, 0.92f));
                p.StrokeRound(r, 9, p.T.Line, 1f);
                p.Text(tipText, f, p.T.Text, cx, top + hh / 2, w - 22, StringAlignment.Center);
            }

            protected void Button(RectangleF r, string id, char glyph, string tip = null)
            {
                Hit(r, id);
                if (tip != null && hotId == id) Tip(r, tip);
                if (hotId == id) p.FillRound(r, 10, pressId == id ? p.T.FillPressed : p.T.FillHover);
                p.Icon(glyph, 15f, hotId == id ? p.T.Text : p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
            }

            protected void Caption(string text, float x, float cy)
            {
                p.Text(text, Ink.Semi(14f), p.T.Text, x, cy, W - x - 20, StringAlignment.Near);
            }
        }
    }
}
