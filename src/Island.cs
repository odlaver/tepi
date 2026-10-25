using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace Tepi
{
    enum View { Idle, Media, Lyric, Track, Agent, Approve, Done, Notif, Volume, Power, Network, Privacy, Info, Task, Deadline, Panel }

    class Toast
    {
        public View View;
        public int Priority;
        public bool Interactive;
        public DateTime ShownUtc;
        public string Title = "", Detail = "";
        public char Glyph;
        public object Data;
        public bool Flag;
    }

    class Hit
    {
        public RectangleF Rect;
        public string Id;
        public Action Act;
    }

    partial class Island : Form
    {
        const float Gap = 9f, Pad = 34f, MaxHalf = 300f, MaxH = 380f, BubbleR = 17f, BubbleGap = 10f;
        const int AppBarMsg = Native.WM_APP + 0x31;
        static readonly Guid PowerSourceGuid = new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
        static readonly Guid BatteryGuid = new Guid("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");

        readonly Settings settings = new Settings();
        readonly Painter p = new Painter();
        readonly Stopwatch clock = Stopwatch.StartNew();

        // Springs: shell size, radius, extras
        readonly Spring sw = new Spring(120, 300, 26, 0.04f), sh = new Spring(34, 240, 23, 0.04f), sr = new Spring(17, 300, 26, 0.04f);
        readonly Spring bubble = new Spring(0, 170, 15, 0.002f), opacity = new Spring(0, 140, 24, 0.002f);
        readonly Spring topGap = new Spring(Gap, 380, 24, 0.02f);
        float flipLinear, flip;
        View current = View.Idle, previous = View.Idle;
        DateTime switchedUtc = DateTime.UtcNow;
        double lastT;
        bool animating;
        int quickFlushes;

        // Window and canvas
        float scale = 1f;
        Rectangle monitorPx;
        IntPtr memDC, dib, oldBitmap;
        Bitmap canvas;
        Graphics g;
        int canvasW, canvasH;
        bool windowShown = true, fullscreen;
        IntPtr winHook, powerSrc, powerPct;
        Native.WinEventProc winProc;
        RectangleF shell, bubbleRect;
        float originX, originY;
        bool hitsOn;

        // Interaction
        readonly List<Hit> hits = new List<Hit>();
        bool expanded, pointerInside;
        int page;
        PointF mouse = new PointF(-100, -100);
        string hoverId, pressedId;
        readonly WinTimer clockTimer = new WinTimer { Interval = 1000 }, toastTimer = new WinTimer();
        readonly WinTimer hoverTimer = new WinTimer { Interval = 330 }, leaveTimer = new WinTimer { Interval = 420 }, fsTimer = new WinTimer { Interval = 350 };
        Toast toast;
        NotifyIcon tray;
        ContextMenuStrip menu;
        Icon trayIcon;
        readonly Roller clockRoller = new Roller();

        public Island()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Tepi";
            Bounds = new Rectangle(-32000, -32000, 1, 1);

            Ink.Init();
            Ink.Apply(settings.Get("theme", "gelap") == "terang");
            clockTimer.Tick += delegate { ClockTick(); };
            toastTimer.Tick += delegate { toastTimer.Stop(); DismissToast(); };
            hoverTimer.Tick += delegate
            {
                hoverTimer.Stop();
                if (pointerInside && !expanded && CanExpand()) Expand(DefaultPage(true));
            };
            leaveTimer.Tick += delegate
            {
                leaveTimer.Stop();
                if (!pointerInside && expanded) { expanded = false; Kick(); }
            };
            fsTimer.Tick += delegate { fsTimer.Stop(); CheckFullscreen(); };
            ambientTimer.Tick += delegate { AmbientTick(); };
            trimTimer.Tick += delegate { Trim(); };
            Application.Idle += OnIdle;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = Native.WS_POPUP;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateMonitor();
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle, uCallbackMessage = AppBarMsg };
            Native.SHAppBarMessage(0, ref abd);
            winProc = delegate { fsTimer.Stop(); fsTimer.Start(); };
            winHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, winProc, 0, 0, 0);
            Guid a = PowerSourceGuid, b = BatteryGuid;
            powerSrc = Native.RegisterPowerSettingNotification(Handle, ref a, 0);
            powerPct = Native.RegisterPowerSettingNotification(Handle, ref b, 0);
            SystemEvents.DisplaySettingsChanged += OnDisplaySettings;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            BuildTray();
            if (!RegisterHotKey(Handle, HideHotkey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.H)) Log.Write("hotkey Ctrl+Alt+H taken");
            StartGlass();
            StartFeeds();
            EnsureDisc(64);
            var warm = new WinTimer { Interval = 2500 };
            warm.Tick += delegate { warm.Stop(); warm.Dispose(); WarmUp(); };
            warm.Start();
            ReadPower();
            CheckFullscreen();
            clockRoller.Set(Fmt.Hm(DateTime.Now));
            int hour = DateTime.Now.Hour;
            ShowInfo('\0', hour < 11 ? "Pagi" : hour < 15 ? "Siang" : hour < 18 ? "Sore" : "Malam", Fmt.Hm(DateTime.Now), true);
            opacity.Target = 1;
            clockTimer.Start();
            Kick();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.Idle -= OnIdle;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettings;
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle };
            Native.SHAppBarMessage(1, ref abd);
            if (winHook != IntPtr.Zero) Native.UnhookWinEvent(winHook);
            if (powerSrc != IntPtr.Zero) Native.UnregisterPowerSettingNotification(powerSrc);
            if (powerPct != IntPtr.Zero) Native.UnregisterPowerSettingNotification(powerPct);
            ReleaseApprovals();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;
                case Native.WM_POWERBROADCAST:
                    ReadPower();
                    m.Result = (IntPtr)1;
                    return;
                case AppBarMsg:
                    if (m.WParam.ToInt64() == 2) { fsTimer.Stop(); fsTimer.Start(); }
                    return;
                case 0x0312: // WM_HOTKEY
                    if (m.WParam.ToInt32() == HideHotkey) ToggleHidden();
                    return;
                case Native.WM_DPICHANGED:
                case Native.WM_DISPLAYCHANGE:
                    BeginInvoke(new Action(OnDisplayChanged));
                    if (m.Msg == Native.WM_DPICHANGED) return;
                    break;
            }
            base.WndProc(ref m);
        }

        void Post(Action action)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        void OnDisplaySettings(object sender, EventArgs e) { Post(OnDisplayChanged); }

        void OnDisplayChanged()
        {
            UpdateMonitor();
            Kick();
        }

        void UpdateMonitor()
        {
            IntPtr mon = Native.MonitorFromPoint(new Native.POINT(0, 0), 1);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            Native.GetMonitorInfo(mon, ref mi);
            monitorPx = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            float s = 1f;
            try
            {
                uint dx, dy;
                if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0) s = dx / 96f;
            }
            catch (Exception ex) { Log.Write("dpi: " + ex.Message); }
            float forced;
            if (float.TryParse(Environment.GetEnvironmentVariable("TEPI_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out forced) && forced > 0.5f) s = forced;
            if (canvas == null || Math.Abs(s - scale) > 0.001f)
            {
                scale = s;
                CreateCanvas();
            }
        }

        void CreateCanvas()
        {
            if (g != null) { g.Dispose(); canvas.Dispose(); Native.SelectObject(memDC, oldBitmap); Native.DeleteObject(dib); Native.DeleteDC(memDC); }
            canvasW = (int)Math.Ceiling(2 * (MaxHalf + Pad) * scale) + 4;
            canvasH = (int)Math.Ceiling((Gap + MaxH + Pad) * scale) + 4;
            var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = canvasW, biHeight = -canvasH, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            IntPtr bits;
            dib = Native.CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
            oldBitmap = Native.SelectObject(memDC, dib);
            canvas = new Bitmap(canvasW, canvasH, canvasW * 4, PixelFormat.Format32bppPArgb, bits);
            g = Graphics.FromImage(canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            p.G = g;
        }

        // State

        View DesiredView()
        {
            if (approvals.Count > 0) return View.Approve;
            if (expanded) return View.Panel;
            if (toast != null) return toast.View;
            if (MediaVisible()) return LyricOnIsland() ? View.Lyric : View.Media;
            if (BusySession() != null) return View.Agent;
            if (SoonTask() != null) return View.Task;
            return View.Idle;
        }

        // Side bubble: agent or deadline
        View BubbleKind()
        {
            if (current != View.Media && current != View.Lyric) return View.Idle;
            if (BusySession() != null) return View.Agent;
            if (SoonTask() != null) return View.Task;
            return View.Idle;
        }

        void Kick()
        {
            if (g == null) return;
            View want = DesiredView();
            if (want != current)
            {
                previous = current;
                current = want;
                switchedUtc = DateTime.UtcNow;
                if (want == View.Panel)
                {
                    panelOpenedUtc = switchedUtc;
                    if (mediaInfo.Present) BackdropFor(); // ready before the music tab
                }
            }
            SizeF size = ViewSize(current);
            // Jelly: leading axis moves first
            bool growing = size.Width > sw.Value + 1;
            Tune(sw, growing ? 380 : 230, 0.70f);
            Tune(sh, growing ? 210 : 340, 0.72f);
            sw.Target = size.Width;
            sh.Target = size.Height;
            if (GlassOn) NeedFrost(size.Height);
            if (current != View.Panel) DropBackdrop();
            sr.Target = ViewRadius(current, size);
            bubble.Target = BubbleKind() != View.Idle ? 1 : 0;
            opacity.Target = (fullscreen || hiddenByUser) && current != View.Approve ? 0 : 1;
            spectrum.SetActive(VisualizerVisible() && !spectrum.Failed);
            if (!animating)
            {
                if (!ambientTimer.Enabled) lastT = clock.Elapsed.TotalSeconds;
                ambientTimer.Stop();
                animating = true;
            }
        }

        // TEPI_FPS=1 logs rendered frames per second
        static readonly bool fpsLog = Environment.GetEnvironmentVariable("TEPI_FPS") == "1";
        int fpsFrames, gcSeen;
        double fpsStart, renderMs, worstMs;

        void CountFrame(double t, double ms)
        {
            if (!fpsLog) return;
            fpsFrames++;
            renderMs += ms;
            worstMs = Math.Max(worstMs, ms);
            string slow = Prof.EndFrame(ms);
            int gcNow = GC.CollectionCount(0);
            if (slow != null) Log.Write("slow " + ms.ToString("0.0") + "ms " + current + (current == View.Panel ? "/" + PageNames[page] : "") + (gcNow != gcSeen ? " [GC]" : "") + ": " + slow);
            gcSeen = gcNow;
            if (t - fpsStart < 2) return;
            if (fpsStart > 0)
                Log.Write("fps " + (fpsFrames / (t - fpsStart)).ToString("0.0") + " render " + (renderMs / fpsFrames).ToString("0.00") + "ms worst " +
                          worstMs.ToString("0.0") + "ms " + current + (Moving(DateTime.UtcNow) ? " moving " : " ") + Prof.Dump(fpsFrames));
            fpsFrames = 0;
            renderMs = 0;
            worstMs = 0;
            fpsStart = t;
        }

        static void Tune(Spring s, float k, float zeta)
        {
            s.K = k;
            s.D = 2 * zeta * (float)Math.Sqrt(k);
        }

        // One frame per vsync while moving

        const int Stopped = 0, Ambient = 1, Motion = 2;
        readonly WinTimer ambientTimer = new WinTimer { Interval = 30 }, trimTimer = new WinTimer { Interval = 4000 };
        DateTime lastTrimUtc = DateTime.MinValue;

        void OnIdle(object sender, EventArgs e)
        {
            while (animating && !IsDisposed && !Native.HasPendingMessage())
            {
                int mode = Frame();
                inMotion = mode == Motion;
                if (mode != Motion)
                {
                    animating = false;
                    if (mode == Ambient) ambientTimer.Start();
                    else { trimTimer.Stop(); trimTimer.Start(); }
                    break;
                }
                double before = clock.Elapsed.TotalMilliseconds;
                bool failed = Native.DwmFlush() != 0;
                // Compositor stopped blocking: avoid spinning
                quickFlushes = clock.Elapsed.TotalMilliseconds - before < 0.3 ? quickFlushes + 1 : 0;
                if (failed || quickFlushes > 3) Thread.Sleep(8);
            }
        }

        // Ambient loops at ~30 fps
        void AmbientTick()
        {
            if (animating) { ambientTimer.Stop(); return; }
            int mode = Frame();
            if (mode == Motion) { ambientTimer.Stop(); animating = true; }
            else if (mode == Stopped) { ambientTimer.Stop(); trimTimer.Stop(); trimTimer.Start(); }
            if ((DateTime.UtcNow - lastTrimUtc).TotalSeconds > 90) Trim();
        }

        // Return idle memory to Windows
        void Trim()
        {
            trimTimer.Stop();
            if ((DateTime.UtcNow - lastTrimUtc).TotalSeconds < 15) return;
            lastTrimUtc = DateTime.UtcNow;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        int Frame()
        {
            var now = DateTime.UtcNow;
            double t = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, t - lastT));
            lastT = t;
            sw.Step(dt); sh.Step(dt); sr.Step(dt); bubble.Step(dt); opacity.Step(dt); topGap.Step(dt);
            StepPanel(dt);
            StepViews(dt, now);
            float flipTarget = current == View.Approve ? 1 : 0;
            if (flipLinear != flipTarget) flipLinear = flipTarget > flipLinear ? Math.Min(1, flipLinear + dt / 0.55f) : Math.Max(0, flipLinear - dt / 0.45f);
            flip = Ease.InOutCubic(flipLinear);

            if (opacity.Target == 0 && opacity.Value < 0.01f)
            {
                if (windowShown) { Native.ShowWindow(Handle, Native.SW_HIDE); windowShown = false; }
                return Stopped;
            }
            if (!windowShown)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                windowShown = true;
            }

            try { Render(now); }
            catch (Exception ex) { Log.Write("render: " + ex); }
            CountFrame(t, (clock.Elapsed.TotalSeconds - t) * 1000);
            return Moving(now) ? Motion : Live(now) ? Ambient : Stopped;
        }

        bool Moving(DateTime now)
        {
            return !sw.Settled || !sh.Settled || !sr.Settled || !bubble.Settled || !opacity.Settled || !topGap.Settled ||
                   (flipLinear != 0 && flipLinear != 1) || (now - switchedUtc).TotalSeconds < 0.5 ||
                   (toast != null && (now - toast.ShownUtc).TotalSeconds < 1.2) || PanelMoving() || ViewsMoving(now) || clockRoller.Moving(now) || ApproveArming(now);
        }

        // Rendering

        void Render(DateTime now)
        {
            float W = Math.Max(10, sw.Value - Clamp(sh.Velocity * 0.012f, -6, 6));
            float H = Math.Max(6, sh.Value - Clamp(sw.Velocity * 0.005f, -3, 3));
            float R = Math.Max(0, Math.Min(sr.Value, Math.Min(W, H) / 2));
            float pb = Math.Max(0, bubble.Value);
            float half = W / 2 + (pb > 0.001f ? BubbleGap + BubbleR * 2 : 0);
            int pxW = Math.Min(canvasW - (canvasW & 1), 2 * (int)Math.Ceiling((Math.Min(half, MaxHalf) + Pad) * scale));
            float gap = Math.Max(0, topGap.Value);
            int pxH = Math.Min(canvasH, (int)Math.Ceiling((gap + Math.Max(H, BubbleR * 2) + Pad) * scale));
            float cx = pxW / scale / 2f;

            g.ResetTransform();
            g.SetClip(new Rectangle(0, 0, pxW, pxH));
            g.Clear(Color.Transparent);
            g.ResetClip();
            g.ScaleTransform(scale, scale);
            p.Alpha = 1f;
            p.T = Ink.Base;
            backdropInSurface = false;

            shell = new RectangleF(cx - W / 2, gap, W, H);
            float rowCy = gap + Math.Min(H, 38f) / 2f;
            float br = BubbleR * (0.4f + 0.6f * Ease.Clamp01(pb));
            float bx = shell.Right - BubbleR + pb * (BubbleGap + BubbleR * 2);
            bubbleRect = pb > 0.02f ? new RectangleF(bx - br, rowCy - br, br * 2, br * 2) : RectangleF.Empty;

            double t0 = Prof.T();
            // Glass pills skip the drop shadow
            if (!GlassOn || H > 60)
            {
                // Target radius while morphing: one bake
                DrawShadow(shell, sr.Settled ? R : Math.Min(sr.Target, Math.Min(W, H) / 2), 1f);
                if (!bubbleRect.IsEmpty) DrawShadow(bubbleRect, br, 0.7f);
            }
            DrawGlow(now, shell, R);
            Prof.Add("shadow", t0);

            hits.Clear();
            using (var path = Ink.Round(shell, R))
            {
                if (!bubbleRect.IsEmpty)
                {
                    var sb = g.Save();
                    g.SetClip(path, CombineMode.Exclude);
                    DrawBubbleShape(rowCy, bx, br, pxW);
                    g.Restore(sb);
                }
                if (flip < 0.999f)
                {
                    t0 = Prof.T();
                    FillSurface(path, shell, Ink.Base, current == View.Panel ? 0.55f : 0.42f, pxW);
                    Prof.Add("surface", t0);
                    t0 = Prof.T();
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    DrawContent(now, Ink.Base, flip < 0.5f);
                    g.Restore(st);
                    Prof.Add("content", t0);
                }
                if (flip > 0.001f)
                {
                    // Inversion spreads from where the island hangs
                    float ox = shell.X + W / 2, oy = shell.Y;
                    float rad = flip * (float)Math.Sqrt(W * W / 4 + H * H) + 1;
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    using (var circle = new GraphicsPath())
                    {
                        circle.AddEllipse(ox - rad, oy - rad, rad * 2, rad * 2);
                        g.SetClip(circle, CombineMode.Intersect);
                    }
                    FillSurface(path, shell, Ink.Flip, 0.8f, pxW);
                    DrawContent(now, Ink.Flip, flip >= 0.5f);
                    g.Restore(st);
                }
            }
            p.T = Ink.Base;
            p.Alpha = 1f;
            if (!bubbleRect.IsEmpty) DrawBubbleContent(now, rowCy, bx, br, Ease.Clamp01((pb - 0.5f) / 0.5f));

            t0 = Prof.T();
            g.Flush(FlushIntention.Sync);
            var dst = new Native.POINT(monitorPx.Left + monitorPx.Width / 2 - pxW / 2, monitorPx.Top);
            var size = new Native.SIZE(pxW, pxH);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)(255 * Ease.Clamp01(opacity.Value)), AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
            lastPresentTick = Environment.TickCount;
            presented.Set();
            Prof.Add("present", t0);
            DumpFrame(dst, pxW, pxH);
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

        static readonly int[] ShadowAlpha = { 26, 18, 12, 7, 4 };
        const float ShadowMargin = 18f;
        readonly Dictionary<string, Bitmap> shadows = new Dictionary<string, Bitmap>();
        Bitmap shadowCache;

        const float ShadowCore = 16f;

        // 9-slice elevation shadow
        void DrawShadow(RectangleF r, float radius, float strength)
        {
            float R = (float)Math.Round(radius * 2) / 2, M = ShadowMargin;
            if (r.Width < 2 * R + ShadowCore || r.Height < 2 * R + ShadowCore) { ShadowLayers(p, r, radius, strength); return; }
            // Small cards: whole bitmap, stretched mid-morph
            if (r.Height <= 90 && strength >= 1f)
            {
                if (sw.Settled && sh.Settled && sr.Settled) WholeShadow(r, R, strength);
                else if (wholeShadow != null) StretchShadow(r);
                return;
            }
            shadowCache = SliceShadow(R, strength);
            // Corners whole, edges as thin ring
            int n = shadowCache.Width, k = (int)Math.Ceiling((M + R) * scale), m = (int)Math.Ceiling(M * scale) + 1, mid = n / 2 - 1;
            int x0 = (int)Math.Round((r.X - M) * scale), y0 = (int)Math.Round((r.Y - M) * scale);
            int x3 = (int)Math.Round((r.Right + M) * scale), y3 = (int)Math.Round((r.Bottom + M) * scale);
            int ew = x3 - x0 - 2 * k, eh = y3 - y0 - 2 * k;
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Slice(0, 0, k, k, x0, y0, k, k);
            Slice(n - k, 0, k, k, x3 - k, y0, k, k);
            Slice(0, n - k, k, k, x0, y3 - k, k, k);
            Slice(n - k, n - k, k, k, x3 - k, y3 - k, k, k);
            if (ew > 0)
            {
                Slice(mid, 0, 2, m, x0 + k, y0, ew, m);
                Slice(mid, n - m, 2, m, x0 + k, y3 - m, ew, m);
            }
            if (eh > 0)
            {
                Slice(0, mid, m, 2, x0, y0 + k, m, eh);
                Slice(n - m, mid, m, 2, x3 - m, y0 + k, m, eh);
            }
            g.Restore(st);
        }

        Bitmap SliceShadow(float R, float strength)
        {
            float M = ShadowMargin;
            string key = R + "|" + strength + "|" + scale;
            Bitmap img;
            if (shadows.TryGetValue(key, out img)) return img;
            if (shadows.Count > 6) { foreach (var b in shadows.Values) b.Dispose(); shadows.Clear(); }
            int side = (int)Math.Ceiling((2 * (M + R) + ShadowCore) * scale);
            img = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(img))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.ScaleTransform(scale, scale);
                ShadowLayers(new Painter { G = sg }, new RectangleF(M, M, 2 * R + ShadowCore, 2 * R + ShadowCore), R, strength);
            }
            shadows[key] = img;
            return img;
        }

        Bitmap wholeShadow;
        string wholeKey;

        void WholeShadow(RectangleF r, float R, float strength)
        {
            float M = ShadowMargin;
            string key = r.Width.ToString("0") + "x" + r.Height.ToString("0") + "|" + R + "|" + strength + "|" + scale;
            if (key != wholeKey)
            {
                if (wholeShadow != null) wholeShadow.Dispose();
                wholeShadow = new Bitmap((int)Math.Ceiling((r.Width + 2 * M) * scale), (int)Math.Ceiling((r.Height + 2 * M) * scale), PixelFormat.Format32bppPArgb);
                using (var sg = Graphics.FromImage(wholeShadow))
                {
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    sg.ScaleTransform(scale, scale);
                    ShadowLayers(new Painter { G = sg }, new RectangleF(M, M, (float)Math.Round(r.Width), (float)Math.Round(r.Height)), R, strength);
                }
                wholeKey = key;
            }
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(wholeShadow, new Rectangle((int)Math.Round((r.X - M) * scale), (int)Math.Round((r.Y - M) * scale), wholeShadow.Width, wholeShadow.Height),
                0, 0, wholeShadow.Width, wholeShadow.Height, GraphicsUnit.Pixel);
            g.Restore(st);
        }

        void StretchShadow(RectangleF r)
        {
            float M = ShadowMargin;
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(wholeShadow, new Rectangle((int)Math.Round((r.X - M) * scale), (int)Math.Round((r.Y - M) * scale),
                (int)Math.Round((r.Width + 2 * M) * scale), (int)Math.Round((r.Height + 2 * M) * scale)), 0, 0, wholeShadow.Width, wholeShadow.Height, GraphicsUnit.Pixel);
            g.Restore(st);
        }

        void Slice(int sx, int sy, int sw2, int sh2, int dx, int dy, int dw, int dh)
        {
            g.DrawImage(shadowCache, new Rectangle(dx, dy, dw, dh), sx, sy, sw2, sh2, GraphicsUnit.Pixel);
        }

        static void ShadowLayers(Painter sp, RectangleF r, float radius, float strength)
        {
            for (int i = 0; i < ShadowAlpha.Length; i++)
            {
                float grow = 1f + i * 2.6f;
                var rr = new RectangleF(r.X - grow, r.Y - grow + 2f + i * 1.2f, r.Width + grow * 2, r.Height + grow * 2);
                sp.FillRound(rr, radius + grow, Color.FromArgb((int)(ShadowAlpha[i] * strength), 0, 0, 0));
            }
        }

        // Halo while an approval waits
        void DrawGlow(DateTime now, RectangleF r, float radius)
        {
            if (flip < 0.05f) return;
            float breath = 0.55f + 0.45f * (float)Math.Sin(clock.Elapsed.TotalSeconds * Math.PI * 1.3);
            Color c = Ink.Flip.Bg;
            for (int i = 0; i < 6; i++)
            {
                float grow = 1.5f + i * 2.6f;
                var rr = new RectangleF(r.X - grow, r.Y - grow, r.Width + grow * 2, r.Height + grow * 2);
                p.FillRound(rr, radius + grow, Color.FromArgb((int)(flip * breath * (70 - i * 11)), c.R, c.G, c.B));
            }
        }

        // Bubble with a gooey neck
        void DrawBubbleShape(float cy, float bx, float br, int pxW)
        {
            using (var shape = new GraphicsPath(FillMode.Winding))
            {
                float right = shell.Right;
                float space = (bx - br) - right;
                if (space < 8f)
                {
                    float t = Ease.Clamp01((8f - space) / 11f);
                    float waist = br * 0.72f * t;
                    float x0 = right - 7, x1 = bx - br * 0.45f;
                    if (x1 > x0 + 1 && waist > 0.8f)
                    {
                        float hA = Math.Min(shell.Height / 2, 18f) * 0.9f, hB = br * 0.92f, mid = (x0 + x1) / 2;
                        shape.StartFigure();
                        shape.AddBezier(x0, cy - hA, mid, cy - waist, mid, cy - waist, x1, cy - hB);
                        shape.AddLine(x1, cy - hB, x1, cy + hB);
                        shape.AddBezier(x1, cy + hB, mid, cy + waist, mid, cy + waist, x0, cy + hA);
                        shape.CloseFigure();
                    }
                }
                shape.AddEllipse(bx - br, cy - br, br * 2, br * 2);
                FillSurface(shape, new RectangleF(bx - br, cy - br, br * 2, br * 2), Ink.Base, 0.42f, pxW);
            }
        }

        void DrawContent(DateTime now, Tone tone, bool registerHits)
        {
            p.T = tone;
            // Text shadow on the pill only
            p.Shadow = GlassOn && tone.Bg.R < 128 && current != View.Panel;
            float t = (float)(now - switchedUtc).TotalSeconds;
            float outA = 1 - Ease.OutCubic(t / 0.16f);
            float inA = Ease.OutCubic((t - 0.07f) / 0.3f);
            // Fade/slide only; scaling breaks glyph cache
            if (previous != current && outA > 0.01f) DrawViewAt(previous, outA, -(1 - outA) * 6, false, now);
            DrawViewAt(current, inA, (1 - Ease.OutBack(inA)) * 8, registerHits && inA > 0.8f, now);
        }

        void DrawViewAt(View v, float alpha, float dy, bool registerHits, DateTime now)
        {
            if (alpha <= 0.01f) return;
            var st = g.Save();
            float w = shell.Width, h = shell.Height;
            g.TranslateTransform(shell.X, shell.Y + (float)Math.Round(dy * scale) / scale);
            p.Alpha = alpha;
            originX = shell.X;
            originY = shell.Y;
            hitsOn = registerHits;
            try { DrawView(v, w, h, now); }
            finally
            {
                g.Restore(st);
                p.Alpha = 1f;
                hitsOn = false;
            }
        }

        void AddHit(RectangleF local, string id, Action act)
        {
            // No pointer, no hits to allocate
            if (!hitsOn || !pointerInside) return;
            local.Offset(originX, originY);
            hits.Add(new Hit { Rect = local, Id = id, Act = act });
        }

        bool Hover(string id) { return hoverId == id; }

        // Input

        bool InShape(PointF pt)
        {
            var s = shell;
            s.Inflate(2, 2);
            return s.Contains(pt) || (!bubbleRect.IsEmpty && bubbleRect.Contains(pt));
        }

        bool CanExpand() { return !hiddenByUser && approvals.Count == 0 && (toast == null || !toast.Interactive); }

        void Expand(int pg)
        {
            if (!CanExpand()) return;
            toast = null;
            toastTimer.Stop();
            expanded = true;
            SetPage(pg, true);
            Kick();
        }

        string HitAt(PointF pt)
        {
            for (int i = hits.Count - 1; i >= 0; i--)
                if (hits[i].Rect.Contains(pt)) return hits[i].Id;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = new PointF(e.X / scale, e.Y / scale);
            bool inside = InShape(mouse);
            if (inside && !pointerInside)
            {
                pointerInside = true;
                Kick();
                leaveTimer.Stop();
                if (!expanded) { hoverTimer.Stop(); hoverTimer.Start(); }
            }
            else if (!inside && pointerInside) { PointerLeave(); return; }
            string id = inside ? HitAt(mouse) : null;
            // Redraw only when the hovered control changes
            if (id != hoverId) { hoverId = id; Kick(); }
        }

        void PointerLeave()
        {
            pointerInside = false;
            hoverTimer.Stop();
            leaveTimer.Stop();
            leaveTimer.Start();
            if (hoverId != null) { hoverId = null; Kick(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (pointerInside) PointerLeave();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) pressedId = HitAt(new PointF(e.X / scale, e.Y / scale));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pt = new PointF(e.X / scale, e.Y / scale);
            if (e.Button == MouseButtons.Right) { menu.Show(Control.MousePosition); return; }
            if (e.Button != MouseButtons.Left) return;
            string id = HitAt(pt);
            if (id != null && id == pressedId)
            {
                foreach (var h in hits)
                {
                    if (h.Id != id) continue;
                    try { h.Act(); } catch (Exception ex) { Log.Write("action " + id + ": " + ex.Message); }
                    break;
                }
            }
            else if (id == null && !expanded && InShape(pt) && CanExpand())
            {
                hoverTimer.Stop();
                bool onBubble = !bubbleRect.IsEmpty && bubbleRect.Contains(pt);
                Expand(onBubble ? (BubbleKind() == View.Agent ? PageAgents : PageTasks) : DefaultPage(false));
            }
            pressedId = null;
            Kick();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (expanded && ScrollPanel(e.Delta)) return;
            vol = Ease.Clamp01(vol + e.Delta / 120f * 0.04f);
            volBar.Target = vol;
            volume.Set(vol);
        }

        // Hide on demand; permission cards still show
        const int HideHotkey = 1;
        bool hiddenByUser;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        void ToggleHidden()
        {
            hiddenByUser = !hiddenByUser;
            if (hiddenByUser) expanded = false;
            Kick();
        }

        // Toasts

        Toast ShowToast(View v, double seconds, int priority, bool interactive, bool force = false)
        {
            if (toast != null && toast.Priority > priority) return null;
            if (expanded && !force)
            {
                if (priority < 3) return null;
                expanded = false;
            }
            bool same = toast != null && toast.View == v;
            var t = same ? toast : new Toast();
            t.View = v;
            t.Priority = priority;
            t.Interactive = interactive;
            if (!same)
            {
                t.ShownUtc = DateTime.UtcNow;
                topGap.Velocity += 70; // arrival dip
            }
            toast = t;
            toastTimer.Stop();
            toastTimer.Interval = Math.Max(1, (int)(seconds * 1000));
            toastTimer.Start();
            Kick();
            return t;
        }

        void ShowInfo(char glyph, string title, string detail, bool force = false)
        {
            var t = ShowToast(View.Info, 2.8, 1, false, force);
            if (t == null) return;
            t.Glyph = glyph;
            t.Title = title;
            t.Detail = detail;
        }

        void DismissToast()
        {
            toastTimer.Stop();
            toast = null;
            Kick();
        }

        // System

        void CheckFullscreen()
        {
            bool fs = Native.IsFullscreenOnPrimary();
            if (!fs && windowShown) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (fs == fullscreen) return;
            fullscreen = fs;
            if (fs) expanded = false;
            Kick();
        }

        void ClockTick()
        {
            string hm = Fmt.Hm(DateTime.Now);
            bool redraw = hm != clockRoller.Value;
            clockRoller.Set(hm);
            redraw |= LifeTick();
            if (redraw || expanded || DesiredView() != current) Kick();
        }

        void ReadPower()
        {
            bool hb, ac; int level;
            Native.ReadPower(out hb, out ac, out level);
            if (!powerKnown)
            {
                powerKnown = true;
                hasBattery = hb; onAc = ac; battery = level;
                return;
            }
            bool acChanged = ac != onAc;
            int prev = battery;
            hasBattery = hb; onAc = ac; battery = level;
            if (!hb) return;
            Toast t = null;
            if (acChanged) t = ShowToast(View.Power, 3.2, 2, false);
            else if (ac && level >= 100 && prev < 100) t = ShowToast(View.Power, 4, 2, false);
            else if (!ac && ((prev > 20 && level <= 20) || (prev > 10 && level <= 10) || (prev > 5 && level <= 5))) t = ShowToast(View.Power, 5, 2, false);
            if (t != null)
            {
                t.Flag = ac;
                t.Data = acChanged ? (ac ? "plug" : "unplug") : ac ? "full" : "low";
                batteryFill.Snap(0);
                batteryFill.Target = battery / 100f;
            }
            Kick();
        }

        // Tray

        void BuildTray()
        {
            menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            var title = new ToolStripMenuItem("Tepi") { Enabled = false };
            var autoItem = new ToolStripMenuItem("Mulai bersama Windows", null, delegate
            {
                try { AutoStart.Enabled = !AutoStart.Enabled; } catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
            });
            var lightItem = new ToolStripMenuItem("Island terang", null, delegate
            {
                settings.Set("theme", Ink.Light ? "gelap" : "terang");
                settings.Save();
                Ink.Apply(!Ink.Light);
                ResetFrost();
                Kick();
            });
            var lyricItem = new ToolStripMenuItem("Lirik di island", null, delegate { settings.Toggle("lyrics", true); Kick(); });
            var vizItem = new ToolStripMenuItem("Visualizer", null, delegate { settings.Toggle("visualizer", true); Kick(); });
            var notifItem = new ToolStripMenuItem("Notifikasi pesan", null, delegate { settings.Toggle("notif", true); notifWatcher.Enabled = NotifOn; Kick(); });
            var contentItem = new ToolStripMenuItem("Tampilkan isi pesan", null, delegate { settings.Toggle("notif.content", true); Kick(); });
            var kuliaItem = new ToolStripMenuItem("Kulia");
            var glassItem = new ToolStripMenuItem("Efek kaca", null, delegate { ToggleGlass(); });
            var hookItem = new ToolStripMenuItem("Hubungkan agen");
            var dataItem = new ToolStripMenuItem("Buka folder data", null, delegate
            {
                try { Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); } catch (Exception ex) { Log.Write("open dir: " + ex.Message); }
            });
            var updateItem = new ToolStripMenuItem("Unduh pembaruan", null, delegate { if (newUrl != null) OpenUrl(newUrl); }) { Visible = false };
            var hideItem = new ToolStripMenuItem("Sembunyikan island", null, delegate { ToggleHidden(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+H" };
            var exit = new ToolStripMenuItem("Keluar", null, delegate { Close(); });
            menu.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), updateItem, hideItem, hookItem, kuliaItem, new ToolStripSeparator(), glassItem, lightItem, lyricItem, vizItem, notifItem, contentItem, autoItem, new ToolStripSeparator(), dataItem, exit });
            menu.Opening += delegate
            {
                try { autoItem.Checked = AutoStart.Enabled; } catch (Exception) { }
                lightItem.Checked = Ink.Light;
                hideItem.Checked = hiddenByUser;
                updateItem.Visible = newVersion != null;
                if (newVersion != null) updateItem.Text = "Unduh Tepi " + newVersion;
                glassItem.Checked = GlassOn;
                lyricItem.Checked = settings.GetBool("lyrics", true);
                vizItem.Checked = settings.GetBool("visualizer", true);
                notifItem.Checked = NotifOn;
                contentItem.Checked = settings.GetBool("notif.content", true);
                contentItem.Enabled = NotifOn;
                CheckLinks();
                hookItem.DropDownItems.Clear();
                hookItem.DropDownItems.Add(new ToolStripMenuItem("Claude Code" + (hookProfiles > 0 ? "" : " · tidak ditemukan"), null, delegate { ToggleHooks(); })
                {
                    Checked = hooksOn,
                    Enabled = hookProfiles > 0
                });
                foreach (var agentId in AgentSetup.Ids)
                {
                    string id = agentId;
                    bool present = AgentSetup.Present(id);
                    hookItem.DropDownItems.Add(new ToolStripMenuItem(AgentSetup.Label(id) + (present ? "" : " · tidak ditemukan"), null, delegate { ToggleAgent(id); })
                    {
                        Checked = present && linked.Contains(AgentSetup.Label(id)),
                        Enabled = present
                    });
                }
                kuliaItem.DropDownItems.Clear();
                if (kulia != null && kulia.SignedIn)
                {
                    kuliaItem.DropDownItems.Add(new ToolStripMenuItem("Masuk sebagai " + kulia.Email) { Enabled = false });
                    kuliaItem.DropDownItems.Add("Perbarui tugas", null, delegate { RefreshTasks(true); });
                    kuliaItem.DropDownItems.Add("Keluar dari Kulia", null, delegate { SignOutKulia(); });
                }
                else kuliaItem.DropDownItems.Add("Masuk ke Kulia…", null, delegate { OpenLogin(); });
            };
            trayIcon = MakeTrayIcon();
            tray = new NotifyIcon { Icon = trayIcon, Text = "Tepi", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Expand(DefaultPage(false)); };
        }

        // Placeholder mark: the island's own pill shape
        static Icon MakeTrayIcon()
        {
            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (var gr = Graphics.FromImage(bmp))
                {
                    gr.SmoothingMode = SmoothingMode.AntiAlias;
                    var pill = new RectangleF(2.5f, 9.5f, 27, 13);
                    using (var path = Ink.Round(pill, 6.5f))
                    {
                        gr.FillPath(Brushes.Black, path);
                        using (var pen = new Pen(Color.FromArgb(255, 235, 235, 235), 1.4f)) gr.DrawPath(pen, path);
                    }
                    gr.FillEllipse(Brushes.White, 20f, 13f, 6, 6);
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }
    }
}
