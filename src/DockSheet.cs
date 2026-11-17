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
            // Tips wait a beat, then fade in
            DateTime tipSince = DateTime.MaxValue;
            readonly System.Windows.Forms.Timer tipWait = new System.Windows.Forms.Timer { Interval = 460 };
            const double TipDelay = 0.45;

            float TipK()
            {
                double t = (DateTime.UtcNow - tipSince).TotalSeconds - TipDelay;
                if (t < 0) return 0;
                return Anim.Reduced ? 1 : Ease.OutCubic((float)(t / 0.12));
            }
            protected bool menuOpen;
            protected Graphics g;
            protected readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
            protected readonly Spring open = new Spring(0, 1, 1, 0.003f);
            DibCanvas surface;
            Bitmap canvas;
            int cw, ch, baseY;
            DockFrost frost;
            DockPlate plate;
            bool allowClose, leaving;
            readonly PacedTimer frames = new PacedTimer();
            long lastStep;

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
                SetBounds(-32000, -32000, 10, 10);
                Tune(open, 420, 0.86f);
                frames.Tick += delegate { Step(); };
                tipWait.Tick += delegate { tipWait.Stop(); if (!IsDisposed) Animate(); };
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
                lastStep = System.Diagnostics.Stopwatch.GetTimestamp();
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
                tipWait.Dispose();
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
                lastStep = System.Diagnostics.Stopwatch.GetTimestamp();
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
                    frost.Request(new Rectangle(left, baseY, pw, ph), Ink.Base.Bg, Ink.Light, isle.LiquidOn);
                }
                open.Snap(0);
                open.Target = 1;
                lastStep = System.Diagnostics.Stopwatch.GetTimestamp();
                Render();
                Show();
                frames.Start();
                // Test runs leave the user's focus alone
                if (DockWindow.Dry) return;
                // Keyboard opens too: take focus
                ShellApp.Activate(Handle);
                Activate();
            }

            int Rise() { return Anim.Reduced ? 0 : (int)Math.Round((1 - open.Value) * 8 * scale); }

            void Step()
            {
                float dt = Math.Min(0.05f, Anim.Dt(ref lastStep));
                open.Step(dt);
                if (leaving && open.Value < 0.03f) { frames.Stop(); Finish(); return; }
                bool more = !leaving && (Animating(dt) | StepHot(dt));
                Location = new Point(Location.X, baseY + Rise());
                Render();
                // Content moved under a still cursor
                if (!leaving && more)
                {
                    var at = PointToClient(Cursor.Position);
                    string id = ClientRectangle.Contains(at) ? HitAt(at) : null;
                    if (id != hotId) { hotId = id; HoverMoved(id); Render(); }
                }
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
                surface = new DibCanvas(w, hh);
                canvas = surface.Wrap();
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
                surface.Dispose();
                surface = null;
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
                        var o = Location;
                        var dst = new Rectangle(plate.Rect.X - o.X, plate.Rect.Y - o.Y + Rise(), plate.Rect.Width, plate.Rect.Height);
                        if (fade >= 0.999f && dst.Width == plate.Text.Width && dst.Height == plate.Text.Height)
                        {
                            // Settled: plain copy, no matrix
                            var im = g.InterpolationMode;
                            var po = g.PixelOffsetMode;
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(plate.Text, dst);
                            g.InterpolationMode = im;
                            g.PixelOffsetMode = po;
                        }
                        else
                            using (var ia = new ImageAttributes())
                            {
                                ia.SetColorMatrix(new ColorMatrix { Matrix33 = fade });
                                g.DrawImage(plate.Text, dst, 0, 0, plate.Text.Width, plate.Text.Height, GraphicsUnit.Pixel, ia);
                            }
                        g.Restore(inner);
                    }
                    g.FillPath(p.B(Ink.A(Ink.Base.Bg, glass ? 0.34f : 0.95f)), path);
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
                float tk = tipText != null ? TipK() : 0;
                if (tk > 0) { p.Alpha = tk; DrawTip(); }
                p.Alpha = 1;
                g.Flush(FlushIntention.Sync);
                if (!IsHandleCreated) return;
                using (var wg = CreateGraphics())
                {
                    IntPtr hdc = wg.GetHdc();
                    BitBlt(hdc, 0, 0, cw, ch, surface.Dc, 0, 0, 0x00CC0020);
                    wg.ReleaseHdc(hdc);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (g == null) return;
                IntPtr hdc = e.Graphics.GetHdc();
                BitBlt(hdc, 0, 0, cw, ch, surface.Dc, 0, 0, 0x00CC0020);
                e.Graphics.ReleaseHdc(hdc);
            }

            // Darker footer shelf
            protected void Shelf(RectangleF r, float height)
            {
                g.FillRectangle(p.B(Ink.A(Ink.Base.Bg, 0.35f)), r.X, H - height, r.Width, height);
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
                Animate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (leaving) return;
                hotId = null;
                HoverMoved(null);
                Render();
                Animate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (leaving) return;
                pressId = HitAt(e.Location);
                if (pressId != null && e.Button == MouseButtons.Left) Drag(pressId, Local(e.Location));
                Render();
                Animate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (leaving) return;
                string id = HitAt(e.Location), was = pressId;
                pressId = null;
                Animate();
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
                MenuGuard.Watch(m);
                m.Show(menuAt ?? Cursor.Position);
                menuAt = null;
            }

            // Keyboard menus open at the control
            protected Point? menuAt;

            protected static void Open(string what, string args)
            {
                try { Process.Start(new ProcessStartInfo(what, args ?? "") { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("sheet open " + what + ": " + ex.Message); }
            }

            protected void Done() { dock.SheetDone(this); }

            // Hover fades: current in, previous out
            readonly Spring hotIn = new Spring(0, 1, 1, 0.004f), hotOut = new Spring(0, 1, 1, 0.004f);
            string hotInId, hotOutId;
            // Toggle on/off blends, per id
            readonly Dictionary<string, Spring> onK = new Dictionary<string, Spring>();

            bool StepHot(float dt)
            {
                if (hotId != hotInId)
                {
                    tipSince = DateTime.UtcNow;
                    tipWait.Stop();
                    if (hotId != null) tipWait.Start();
                    float back = hotId != null && hotId == hotOutId ? hotOut.Value : 0;
                    if (hotInId != null)
                    {
                        hotOutId = hotInId;
                        hotOut.Value = hotIn.Value;
                        hotOut.Velocity = 0;
                        Springs.Tune(hotOut, 0.2f, 1f);
                        hotOut.Target = 0;
                    }
                    else if (back > 0) hotOutId = null;
                    hotInId = hotId;
                    hotIn.Value = back;
                    hotIn.Velocity = 0;
                    Springs.Tune(hotIn, 0.1f, 1f);
                    hotIn.Target = hotId != null ? 1 : 0;
                }
                hotIn.Step(dt);
                hotOut.Step(dt);
                if (hotOutId != null && hotOut.Settled) hotOutId = null;
                float tk = tipText != null ? TipK() : 0;
                bool moving = StepSquish(dt) | !hotIn.Settled || hotOutId != null || (tk > 0 && tk < 1);
                foreach (var s in onK.Values) { s.Step(dt); moving |= !s.Settled; }
                foreach (var s in eased.Values) { s.Step(dt); moving |= !s.Settled; }
                return moving;
            }

            // Press squish: held id sinks, release overshoots
            readonly Spring squish = new Spring(0, 1, 1, 0.004f);
            string squishId;

            bool StepSquish(float dt)
            {
                if (Anim.Reduced) { squishId = null; squish.Snap(0); return false; }
                string held = pressId != null && pressId == hotId ? pressId : null;
                if (held != null && held != squishId)
                {
                    squishId = held;
                    Springs.Tune(squish, 0.1f, 1f);
                    squish.Target = 1;
                }
                else if (held == null && squishId != null && squish.Target != 0)
                {
                    Springs.Tune(squish, 0.34f, 0.52f);
                    squish.Target = 0;
                }
                squish.Step(dt);
                if (squishId != null && held == null && squish.Settled) squishId = null;
                return squishId != null;
            }

            // Held control's scale, 1 at rest
            protected float PressScale(string id, float depth)
            {
                if (id == null || id != squishId || Anim.Reduced) return 1;
                return 1 - (1 - depth) * squish.Value;
            }

            protected float Squish(string id, RectangleF r, float depth) { return Squish(r, PressScale(id, depth)); }

            // Scale around r's centre; pair with Unsquish
            protected float Squish(RectangleF r, float s)
            {
                if (Math.Abs(s - 1) < 0.0005f) return 1;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(s, s);
                g.TranslateTransform(-cx, -cy);
                return s;
            }

            protected void Unsquish(RectangleF r, float s)
            {
                if (s == 1) return;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(1 / s, 1 / s);
                g.TranslateTransform(-cx, -cy);
            }

            // Shown value easing to its target
            readonly Dictionary<string, Spring> eased = new Dictionary<string, Spring>();

            protected float Eased(string id, float want, bool fast)
            {
                Spring s;
                if (!eased.TryGetValue(id, out s)) { s = new Spring(want, 1, 1, 0.002f); eased[id] = s; return want; }
                if (s.Target != want)
                {
                    Springs.Tune(s, fast ? 0.07f : Springs.InteractiveResponse, 1f);
                    s.Target = want;
                    if (Anim.Reduced) s.Snap(want); else Animate();
                }
                return s.Value;
            }

            protected float HotK(string id)
            {
                if (id == null) return 0;
                if (Anim.Reduced) return hotId == id ? 1 : 0;
                if (id == hotInId) return Ease.Clamp01(hotIn.Value);
                if (id == hotOutId) return Ease.Clamp01(hotOut.Value);
                return 0;
            }

            protected float OnK(string id, bool on)
            {
                Spring s;
                float want = on ? 1 : 0;
                if (!onK.TryGetValue(id, out s)) { s = new Spring(want, 1, 1, 0.004f); Springs.Tune(s, 0.18f, 1f); onK[id] = s; }
                if (s.Target != want) { s.Target = want; if (Anim.Reduced) s.Snap(want); else Animate(); }
                return Ease.Clamp01(s.Value);
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

            protected void Button(RectangleF r, string id, char glyph, string tip = null, float size = 15f)
            {
                Hit(r, id);
                if (tip != null && hotId == id) Tip(r, tip);
                float hk = HotK(id), s = Squish(id, r, 0.9f);
                if (hk > 0 || pressId == id) p.FillRound(r, 10, pressId == id ? p.T.FillPressed : Ink.A(p.T.FillHover, hk));
                p.Icon(glyph, size, Ink.Mix(p.T.Sub, p.T.Text, hk), r.X + r.Width / 2, r.Y + r.Height / 2);
                Unsquish(r, s);
            }

            protected void Caption(string text, float x, float cy)
            {
                p.Text(text, Ink.Semi(14f), p.T.Text, x, cy, W - x - 20, StringAlignment.Near);
            }
        }
    }
}
