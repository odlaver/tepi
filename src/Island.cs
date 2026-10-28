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
    enum View { Idle, Media, Lyric, Track, Agent, Approve, Done, Notif, Volume, Power, Network, Privacy, Info, Task, Deadline, Timer, Prompter, Class, Download, Brightness, AudioDevice, ShelfDrop, Pray, Remind, Mini, Panel }

    class Toast
    {
        public View View;
        public int Priority;
        public bool Interactive;
        public DateTime ShownUtc;
        public string Title = "", Detail = "";
        public char Glyph;
        public bool Emblem;
        public object Data;
        public bool Flag;
    }

    class Hit
    {
        public RectangleF Rect;
        public string Id;
        public Action Act;
        // Sliders: pointer x as 0..1
        public Action<float> Drag;
        // Right-click menu for this item
        public Action Menu;
        // Drag released
        public Action DragEnd;
    }

    partial class Island : Form
    {
        const float Gap = 9f, Pad = 34f, MaxH = 380f, BubbleR = 17f, BubbleGap = 10f, BaseHalf = 300f;
        // Wide teleprompter widens the canvas
        float prompterHalf = BaseHalf, canvasHalf = BaseHalf;
        float WantHalf { get { return PrompterOn ? Math.Max(BaseHalf, prompterHalf) : BaseHalf; } }
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
        // Compact canvas until a tall view
        bool canvasTall;
        const float CompactH = 100;
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
            Anim.Read(settings);
            Ink.Apply(settings.Get("theme", "gelap") == "terang");
            Ink.SetAccent(settings.Get("accent", ""));
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
                if (!pointerInside && expanded) { expanded = false; MiniLeft(); Kick(); }
            };
            fsTimer.Tick += delegate { fsTimer.Stop(); CheckFullscreen(); CheckMiniAuto(); };
            ambientTimer.Tick += delegate { AmbientTick(); };
            spriteTimer.Tick += delegate { SpriteTick(); };
            trimTimer.Tick += delegate { trimTimer.Stop(); TrimAtRest(); };
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
            winProc = delegate { fsTimer.Stop(); fsTimer.Start(); NudgeFrost(); };
            winHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, winProc, 0, 0, 0);
            // Windows moving behind the glass
            moveHook = Native.SetWinEventHook(0x0B, 0x0B, IntPtr.Zero, winProc, 0, 0, 0);
            minHook = Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, winProc, 0, 0, 0);
            Guid a = PowerSourceGuid, b = BatteryGuid, d = DisplayGuid;
            powerSrc = Native.RegisterPowerSettingNotification(Handle, ref a, 0);
            powerPct = Native.RegisterPowerSettingNotification(Handle, ref b, 0);
            powerDisplay = Native.RegisterPowerSettingNotification(Handle, ref d, 0);
            SystemEvents.DisplaySettingsChanged += OnDisplaySettings;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        IntPtr moveHook, minHook, powerDisplay;
        static readonly Guid DisplayGuid = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        bool locked, screenOff;

        // Locked or screen off: everything rests
        void SetDormant(bool isLocked, bool isOff)
        {
            locked = isLocked;
            screenOff = isOff;
            bool now = locked || screenOff;
            if (now == dormant) return;
            dormant = now;
            if (!now) NudgeFrost();
            Kick();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) Post(delegate { SetDormant(true, screenOff); });
            else if (e.Reason == SessionSwitchReason.SessionUnlock) Post(delegate { SetDormant(false, screenOff); });
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CleanUpdate();
            Lang.English = settings.Get("lang", "id") == "en";
            LoadPack();
            LoadLayout();
            BuildTray();
            ApplyClips();
            ApplyDownloads();
            if (!RegisterHotKey(Handle, HideHotkey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.H)) Log.Write("hotkey Ctrl+Alt+H taken");
            if (!RegisterHotKey(Handle, KeysHotkey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.I)) Log.Write("hotkey Ctrl+Alt+I taken");
            MiniInit();
            StartGlass();
            StartFeeds();
            StartControls();
            InitShelfDrop();
            EnsureDisc(64);
            var warm = new WinTimer { Interval = 2500 };
            warm.Tick += delegate { warm.Stop(); warm.Dispose(); WarmUp(); WarmTabs(); TrimSoon(); };
            warm.Start();
            ReadPower();
            CheckFullscreen();
            clockRoller.Set(Fmt.Hm(DateTime.Now));
            int hour = DateTime.Now.Hour;
            ShowInfo('\0', Greeting(hour < 11 ? Lang.T("Pagi") : hour < 15 ? Lang.T("Siang") : hour < 18 ? Lang.T("Sore") : Lang.T("Malam")), Fmt.Hm(DateTime.Now), true);
            if (toast != null) toast.Emblem = emblem != null;
            if (toast != null && toast.Emblem) { PlayEmblem(animSeconds); Ripple(); }
            PlayGreetingVoice();
            opacity.Target = 1;
            clockTimer.Start();
            Kick();
            PilotStart();
        }

        // Test builds attach a pilot here
        partial void PilotStart();

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.Idle -= OnIdle;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettings;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle };
            Native.SHAppBarMessage(1, ref abd);
            foreach (var hook in new[] { winHook, moveHook, minHook })
                if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            foreach (var reg in new[] { powerSrc, powerPct, powerDisplay })
                if (reg != IntPtr.Zero) Native.UnregisterPowerSettingNotification(reg);
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
                    // Display: 0 off, 1 on, 2 dimmed
                    if (m.WParam.ToInt64() == 0x8013 && m.LParam != IntPtr.Zero)
                    {
                        var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                        if (setting == DisplayGuid) { SetDormant(locked, Marshal.ReadInt32(m.LParam, 20) == 0); m.Result = (IntPtr)1; return; }
                    }
                    ReadPower();
                    m.Result = (IntPtr)1;
                    return;
                case AppBarMsg:
                    if (m.WParam.ToInt64() == 2) { fsTimer.Stop(); fsTimer.Start(); }
                    return;
                case 0x031D: // WM_CLIPBOARDUPDATE
                    OnClipboardUpdate();
                    break;
                case 0x0312: // WM_HOTKEY
                    if (m.WParam.ToInt32() == HideHotkey) ToggleHidden();
                    else if (m.WParam.ToInt32() == PrompterHotkey) TogglePrompter();
                    else if (m.WParam.ToInt32() == PrompterFaster) NudgeSpeed(1);
                    else if (m.WParam.ToInt32() == PrompterSlower) NudgeSpeed(-1);
                    else if (m.WParam.ToInt32() == PrompterSetupKey) BeginInvoke(new Action(OpenPrompter));
                    else if (m.WParam.ToInt32() == PrompterRestart) RestartPrompter();
                    else if (m.WParam.ToInt32() == PrompterStop) StopPrompter();
                    else if (m.WParam.ToInt32() == PrompterBack) SeekPrompter(-5);
                    else if (m.WParam.ToInt32() == PrompterAhead) SeekPrompter(5);
                    else if (m.WParam.ToInt32() == KeysHotkey) EnterKeyboard();
                    else if (m.WParam.ToInt32() == MiniHotkey) ToggleMini();
                    return;
                case 0x020E: // WM_MOUSEHWHEEL: sideways swipe changes tab
                    if (expanded) WheelTab((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                    m.Result = IntPtr.Zero;
                    return;
                case 0x001A: // WM_SETTINGCHANGE: animation effects toggled
                    if (m.WParam.ToInt64() == 0x1043) { Anim.Read(settings); Kick(); }
                    break;
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

        IntPtr monitorHandle;

        // Monitor picked in the tray, else the primary
        IntPtr ChosenMonitor()
        {
            string want = settings.Get("monitor", "");
            if (want.Length > 0)
                foreach (var s in Screen.AllScreens)
                    if (s.DeviceName == want)
                        return Native.MonitorFromPoint(new Native.POINT(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2), 2);
            return Native.MonitorFromPoint(new Native.POINT(0, 0), 1);
        }

        void UseMonitor(string device)
        {
            settings.Set("monitor", device);
            settings.Save();
            UpdateMonitor();
            ResetFrost();
            CheckFullscreen();
            Kick();
        }

        void OnDisplayChanged()
        {
            UpdateMonitor();
            Kick();
        }

        void UpdateMonitor()
        {
            IntPtr mon = ChosenMonitor();
            monitorHandle = mon;
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

        int TallPx() { return (int)Math.Ceiling((Gap + MaxH + Pad + 20) * scale) + 4; }

        bool NeedTall()
        {
            float dip = Docked ? Math.Max(0, topGap.Value - Gap) : 0;
            return current == View.Panel || expanded || DateTime.UtcNow < prefetchUntilUtc || Math.Max(sh.Target, sh.Value) + dip > CompactH;
        }

        // Grow before drawing; old view kept
        bool GrowCanvas()
        {
            if (canvasTall || g == null || !NeedTall()) return false;
            if (snapWanted) TakeSnapshot();
            canvasTall = true;
            CreateCanvas();
            frameDirty = true;
            return true;
        }

        void CreateCanvas()
        {
            if (g != null) { g.Dispose(); canvas.Dispose(); Native.SelectObject(memDC, oldBitmap); Native.DeleteObject(dib); Native.DeleteDC(memDC); }
            canvasHalf = WantHalf;
            canvasW = (int)Math.Ceiling(2 * (canvasHalf + Pad) * scale) + 4;
            canvasH = canvasTall ? TallPx() : (int)Math.Ceiling((Gap + CompactH + Pad + 20) * scale) + 4;
            var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = canvasW, biHeight = -canvasH, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            IntPtr bits;
            dib = Native.CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
            dibBits = bits;
            slotCount = 0;
            liveReady = false;
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
            // Files hovering: the island becomes a target
            if (dropActive) return View.ShelfDrop;
            if (PrompterOn) return View.Prompter;
            if (expanded) return View.Panel;
            // Retracted: only urgent toasts pop out
            if (MiniOn() && (toast == null || !MiniLets(toast.View, toast.Priority))) return View.Mini;
            if (toast != null) return toast.View;
            return ActivityView();
        }

        // What runs underneath toasts and panel
        View ActivityView()
        {
            if (TimerOn && !MediaVisible()) return View.Timer;
            if (MediaVisible()) return LyricOnIsland() ? View.Lyric : View.Media;
            if (ActiveDownload() != null) return View.Download;
            if (BusySession() != null) return View.Agent;
            if (SoonTask() != null) return View.Task;
            if (ClassNow() != null) return View.Class;
            return View.Idle;
        }

        // Side bubble: agent or deadline
        View BubbleKind()
        {
            if (current != View.Media && current != View.Lyric) return View.Idle;
            if (BusySession() != null) return View.Agent;
            if (ActiveDownload() != null) return View.Download;
            if (TimerOn) return View.Timer;
            if (SoonTask() != null) return View.Task;
            return View.Idle;
        }

        bool frameDirty;

        void Kick()
        {
            if (g == null) return;
            frameDirty = true;
            if (fpsLog) CountKick();
            View want = DesiredView();
            if (want != current)
            {
                previous = current;
                current = want;
                AnnounceView(want);
                switchedUtc = DateTime.UtcNow;
                // Old view leaves as a picture
                snapWanted = true;
                if (want == View.Panel) panelOpenedUtc = switchedUtc;
                else if (previous == View.Panel || previous == View.Prompter) TrimSoon();
                if (want == View.Mini) EyesShown();
            }
            MiniSync();
            // Music tab chosen early: bake its backdrop
            if (current == View.Panel && page == PageMusic && mediaInfo.Present) BackdropFor();
            SizeF size = ViewSize(current);
            // Jelly: leading axis moves first
            bool growing = size.Width > sw.Value + 1;
            // Into the sliver without a bounce
            float zeta = current == View.Mini ? 0.95f : 0.75f;
            Tune(sw, growing ? 300 : 190, zeta);
            Tune(sh, growing ? 190 : 300, zeta);
            sw.Target = size.Width;
            sh.Target = size.Height;
            GrowCanvas();
            // Script backdrop is plain: no frost jobs
            if (GlassOn && current != View.Prompter) NeedFrost(size.Height);
            if (current != View.Panel) { DropBackdrop(); DropCorner(); }
            sr.Target = ViewRadius(current, size);
            topGap.Target = current == View.Mini && !Docked ? 0 : Gap;
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
            StatsLine(t);
        }

        // One stats line every two seconds
        void StatsLine(double t)
        {
            if (t - fpsStart < 2) return;
            if (fpsStart > 0)
                Log.Write("fps " + (fpsFrames / (t - fpsStart)).ToString("0.0") + " render " + (renderMs / Math.Max(1, fpsFrames)).ToString("0.00") + "ms worst " +
                          worstMs.ToString("0.0") + "ms " + current + (Moving(DateTime.UtcNow) ? " moving " : " ") + Prof.Dump(fpsFrames) + LiveStats(t - fpsStart));
            fpsFrames = 0;
            renderMs = 0;
            worstMs = 0;
            fpsStart = t;
        }

        // Reduced motion: no overshoot anywhere
        static void Tune(Spring s, float k, float zeta)
        {
            s.K = k;
            s.D = 2 * (Anim.Reduced ? Math.Max(1f, zeta) : zeta) * (float)Math.Sqrt(k);
        }

        // One frame per vsync while moving

        const int Stopped = 0, Ambient = 1, Motion = 2;
        readonly WinTimer ambientTimer = new WinTimer { Interval = 40 };

        void OnIdle(object sender, EventArgs e)
        {
            while (animating && !IsDisposed && !Native.HasPendingMessage())
            {
                int mode = Frame();
                inMotion = mode == Motion;
                if (mode == Motion) lastMotionUtc = DateTime.UtcNow;
                if (mode != Motion)
                {
                    animating = false;
                    RestTimer();
                    if (mode == Ambient) { ambientTimer.Interval = AmbientWait(wholeLive); ambientTimer.Start(); }
                    break;
                }
                double before = clock.Elapsed.TotalMilliseconds;
                bool failed = Native.DwmFlush() != 0;
                // Compositor stopped blocking: avoid spinning
                quickFlushes = clock.Elapsed.TotalMilliseconds - before < 0.3 ? quickFlushes + 1 : 0;
                if (failed || quickFlushes > 3) Thread.Sleep(8);
            }
        }

        // Timer for live areas and ambient views
        void AmbientTick()
        {
            if (animating) { ambientTimer.Stop(); return; }
            int mode = Frame();
            if (mode == Motion) { ambientTimer.Stop(); animating = true; }
            else if (mode == Stopped) ambientTimer.Stop();
            else
            {
                int ms = AmbientWait(wholeLive);
                if (ambientTimer.Interval != ms) ambientTimer.Interval = ms;
            }
        }

        // Live content paces; battery saver slows
        int AmbientMs()
        {
            int ms = 50;
            if (VisualizerVisible() || current == View.Lyric || PrompterRunning() || (current == View.Panel && page == PageMusic && mediaInfo.Playing)) ms = 40;
            else if (current == View.Media && mediaInfo.Playing && !settings.GetBool("visualizer", true)) ms = 250;
            Native.SYSTEM_POWER_STATUS ps;
            if (Native.GetSystemPowerStatus(out ps) && ps.SystemStatusFlag == 1) ms = ms * 3 / 2;
            return ms;
        }

        DateTime lastMotionUtc = DateTime.UtcNow;


        int Frame()
        {
            var now = DateTime.UtcNow;
            double t = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, Math.Max(0.001, t - lastT));
            lastT = t;
            sw.Step(dt); sh.Step(dt); sr.Step(dt); bubble.Step(dt); opacity.Step(dt); topGap.Step(dt);
            StepPanel(dt);
            StepPrompter(dt);
            StepViews(dt, now);
            StepSwitches(dt);
            collapse.Step(dt);
            tabNudge.Step(dt);
            float flipTarget = current == View.Approve ? 1 : 0;
            if (flipLinear != flipTarget) flipLinear = flipTarget > flipLinear ? Math.Min(1, flipLinear + dt / 0.55f) : Math.Max(0, flipLinear - dt / 0.45f);
            flip = Ease.InOutCubic(flipLinear);

            if (opacity.Target == 0 && opacity.Value < 0.01f)
            {
                if (windowShown) { Native.ShowWindow(Handle, Native.SW_HIDE); windowShown = false; }
                return Stopped;
            }
            // Locked or screen off: draw nothing
            if (dormant) { frameDirty = true; return Stopped; }
            if (!windowShown)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                windowShown = true;
                NudgeFrost();
            }

            GrowCanvas();
            // Shrink wide canvas once shell fits
            if (!PrompterOn && canvasHalf > BaseHalf && sw.Settled && sw.Value / 2 + (bubble.Value > 0.01f ? BubbleGap + BubbleR * 2 : 0) <= BaseHalf) CreateCanvas();
            else if (canvasTall && !PrompterOn && sw.Settled && sh.Settled && snapshot == null && !FlyersMoving(now) && !NeedTall()) { canvasTall = false; CreateCanvas(); }
            bool moving = Moving(now);
            wholeLive = !moving && Live(now);
            // Prompter redraws only when its picture changes
            if (current == View.Prompter && !frameDirty && !moving && PrompterKey() == prompterDrawn) return wholeLive ? Ambient : Stopped;
            // Otherwise only due live areas redraw
            if (!frameDirty && !moving && liveReady && wholePeriod <= 0 && !wholeLive)
            {
                frameT = t;
                LiveFrame(now, t);
                DumpFrame();
                return LiveDue() ? Ambient : Stopped;
            }
            CountWhy(frameDirty ? 0 : moving ? 1 : !liveReady ? 2 : wholePeriod > 0 ? 3 : 4);
            frameDirty = false;
            liveRecord = !moving && LiveAllowed();
            frameT = t;
            if (current == View.Prompter) prompterDrawn = PrompterKey();
            try { Render(now); }
            catch (Exception ex) { liveRecord = false; slotCount = 0; Log.Write("render: " + ex); }
            inRender = false;
            liveReady = liveRecord;
            if (liveRecord)
            {
                // Panel settled: bake cover backdrop now
                if (current == View.Panel && mediaInfo.Present) BackdropFor();
                CollectAtRest();
            }
            CountFrame(t, (clock.Elapsed.TotalSeconds - t) * 1000);
            DumpFrame();
            if (Moving(now)) return Motion;
            // Just settled: record live areas next
            if (!liveRecord && liveInline > 0 && LiveAllowed()) return Motion;
            wholeLive = Live(now);
            return wholeLive || wholePeriod > 0 || LiveDue() ? Ambient : Stopped;
        }

        bool wholeLive;

        bool Moving(DateTime now)
        {
            return !sw.Settled || !sh.Settled || !sr.Settled || !bubble.Settled || !opacity.Settled || !topGap.Settled ||
                   (flipLinear != 0 && flipLinear != 1) || (now - switchedUtc).TotalSeconds < 0.5 || snapshot != null ||
                   (toast != null && (now - toast.ShownUtc).TotalSeconds < 1.2) || PanelMoving() || ViewsMoving(now) || (current == View.Idle && clockRoller.Moving(now)) || ApproveArming(now) ||
                   ThemeMoving(now) ||
                   FlyersMoving(now) || SwitchesMoving() || prompterSizeMoved || !collapse.Settled || !tabNudge.Settled || brightRoller.Moving(now) || !brightBar.Settled;
        }

        // Rendering

        void Render(DateTime now)
        {
            float jelly = Anim.Reduced ? 0 : 1;
            float W = Math.Max(10, sw.Value - jelly * Clamp(sh.Velocity * 0.012f, -6, 6));
            float H = Math.Max(6, sh.Value - jelly * Clamp(sw.Velocity * 0.005f, -3, 3));
            float R = Math.Max(0, Math.Min(sr.Value, Math.Min(W, H) / 2));
            float pb = Math.Max(0, bubble.Value);
            float half = W / 2 + (pb > 0.001f ? BubbleGap + BubbleR * 2 : 0);
            // Room for corner mascot
            float extra = corner != null && current == View.Panel ? 20 : 0;
            int pxW = Math.Min(canvasW - (canvasW & 1), 2 * (int)Math.Ceiling((Math.Min(half, canvasHalf) + Pad + extra) * scale));
            // Docked: no gap; the arrival dip stretches the island instead
            float gap = Docked ? 0 : Math.Max(0, topGap.Value);
            if (Docked) H += Math.Max(0, topGap.Value - Gap);
            int pxH = Math.Min(canvasH, (int)Math.Ceiling((gap + Math.Max(H, BubbleR * 2) + Pad + extra) * scale));
            float cx = pxW / scale / 2f;
            BeginLive(now, pxW, pxH);
            inRender = true;

            g.ResetTransform();
            if (snapWanted) TakeSnapshot();
            snapPxW = pxW;
            g.SetClip(new Rectangle(0, 0, pxW, pxH));
            g.Clear(Color.Transparent);
            g.ResetClip();
            g.ScaleTransform(scale, scale);
            p.Alpha = 1f;
            p.T = Ink.Base;
            backdropInSurface = false;

            shell = new RectangleF(cx - W / 2, gap, W, H);
            float rowCy = gap + Math.Min(H, 38f) / 2f;
            float br = BubbleRadius(pb);
            float bx = BubbleX(pb);
            bubbleRect = pb > 0.02f ? new RectangleF(bx - br, rowCy - br, br * 2, br * 2) : RectangleF.Empty;

            double t0 = Prof.T();
            // Glass pills skip the drop shadow
            if ((!GlassOn || H > 60) && current != View.Prompter)
            {
                // Target radius while morphing: one bake
                DrawShadow(shell, sr.Settled ? R : Math.Min(sr.Target, Math.Min(W, H) / 2), 1f);
                if (!bubbleRect.IsEmpty) DrawShadow(bubbleRect, br, 0.7f);
            }
            DrawGlow(now, shell, R);
            Prof.Add("shadow", t0);

            ClearHits();
            using (var path = Docked ? Ink.Notch(shell, R, 10) : Ink.Round(shell, R))
            {
                if (liveRecord) KeepShell(path, R);
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
                    if (current == View.Prompter) FillPrompterBg(path);
                    else FillSurface(path, shell, Ink.Base, current == View.Panel ? 0.55f : 0.42f, pxW);
                    Prof.Add("surface", t0);
                    t0 = Prof.T();
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    liveInShell = true;
                    DrawContent(now, Ink.Base, flip < 0.5f);
                    liveInShell = false;
                    DrawWater(now);
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
            DrawCorner(now);
            DrawFlyers(now, pxW);
            t0 = Prof.T();
            FinishLive(now);
            Prof.Add("live", t0);

            t0 = Prof.T();
            g.Flush(FlushIntention.Sync);
            var dst = new Native.POINT(WindowLeft(pxW), monitorPx.Top);
            var size = new Native.SIZE(pxW, pxH);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)(255 * Ease.Clamp01(opacity.Value)), AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
            lastPresentTick = Environment.TickCount;
            presented.Set();
            Prof.Add("present", t0);
            dumpAt = new Rectangle(dst.X, dst.Y, pxW, pxH);
            lastShellPx = Rectangle.Round(new RectangleF(shell.X * scale, shell.Y * scale, shell.Width * scale, shell.Height * scale));
            lastLeft = dst.X;
        }

        // Snapshot morph: last shell, half size
        Bitmap snapshot;
        Rectangle snapScreen, lastShellPx;
        int lastLeft, snapPxW;
        DateTime snapUtc;
        bool snapWanted;
        const double SnapSeconds = 0.14;
        // Snapshot DIB: BitBlt in, AlphaBlend out
        IntPtr snapDC, snapDib, snapOld;
        int snapW, snapH;
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int SelectClipRgn(IntPtr hdc, IntPtr rgn);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int blend);

        void FreeSnapshot()
        {
            if (snapshot != null) { snapshot.Dispose(); snapshot = null; }
            if (snapDC != IntPtr.Zero) { Native.SelectObject(snapDC, snapOld); Native.DeleteObject(snapDib); Native.DeleteDC(snapDC); snapDC = IntPtr.Zero; }
        }

        void TakeSnapshot()
        {
            snapWanted = false;
            var r = Rectangle.Intersect(lastShellPx, new Rectangle(0, 0, canvasW, canvasH));
            if (!windowShown || opacity.Value < 0.5f || r.Width < 8 || r.Height < 8) return;
            int w = r.Width, h = r.Height;
            if (snapDC == IntPtr.Zero || snapW != w || snapH != h)
            {
                FreeSnapshot();
                var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr bits;
                snapDC = Native.CreateCompatibleDC(memDC);
                snapDib = Native.CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                snapOld = Native.SelectObject(snapDC, snapDib);
                snapshot = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                snapW = w;
                snapH = h;
            }
            // Plain memory copy, no resampling
            g.Flush(FlushIntention.Sync);
            BitBlt(snapDC, 0, 0, w, h, memDC, r.X, r.Y, 0x00CC0020);
            snapScreen = new Rectangle(lastLeft + r.X, r.Y, r.Width, r.Height);
            snapUtc = DateTime.UtcNow;
        }

        // Old view shrinks toward 0.92, fading
        void DrawSnapshot(DateTime now)
        {
            if (snapshot == null) return;
            double secs = (now - snapUtc).TotalSeconds, t = secs / SnapSeconds;
            if (t >= 1) { FreeSnapshot(); return; }
            float k = Ease.OutCubic((float)t), sc = Anim.Reduced ? 1 : 1 - 0.08f * Springs.Progress((float)secs, 0.24f, 1f);
            int left = WindowLeft(snapPxW);
            float w = snapScreen.Width * sc, h = snapScreen.Height * sc;
            float cx = snapScreen.X - left + snapScreen.Width / 2f;
            var dst = new RectangleF(cx - w / 2, snapScreen.Y, w, h);
            // GDI AlphaBlend: premultiplied, constant fade
            int sca = (int)(255 * (1 - k) * 0.95f);
            if (sca <= 0) return;
            // Same shell clip as the GDI+ content
            using (var clip = g.Clip)
            {
                IntPtr rgn = clip.IsInfinite(g) ? IntPtr.Zero : clip.GetHrgn(g);
                g.Flush(FlushIntention.Sync);
                SelectClipRgn(memDC, rgn);
                AlphaBlend(memDC, (int)Math.Round(dst.X), (int)Math.Round(dst.Y), Math.Max(1, (int)Math.Round(dst.Width)), Math.Max(1, (int)Math.Round(dst.Height)), snapDC, 0, 0, snapW, snapH, sca << 16 | 1 << 24);
                SelectClipRgn(memDC, IntPtr.Zero);
                if (rgn != IntPtr.Zero) Native.DeleteObject(rgn);
            }
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

        static readonly int[] ShadowAlpha = { 10, 9, 7, 5, 3, 16, 10, 6 };
        const float ShadowMargin = 18f;
        readonly Dictionary<string, Bitmap> shadows = new Dictionary<string, Bitmap>();
        Bitmap shadowCache;

        const float ShadowCore = 16f;

        // 9-slice elevation shadow
        void DrawShadow(RectangleF r, float radius, float strength)
        {
            float R = (float)Math.Round(radius * 2) / 2, M = ShadowMargin;
            // Continuous corners extend past R
            float E = Ink.CornerExtent(R);
            if (r.Width < 2 * E + ShadowCore || r.Height < 2 * E + ShadowCore) { ShadowLayers(p, r, radius, strength); return; }
            // Small cards: whole bitmap, stretched mid-morph
            if (r.Height <= 90 && strength >= 1f)
            {
                if (sw.Settled && sh.Settled && sr.Settled) WholeShadow(r, R, strength);
                else if (wholeShadow != null) StretchShadow(r);
                return;
            }
            shadowCache = SliceShadow(R, strength);
            // Corners whole, edges as thin ring
            int n = shadowCache.Width, k = (int)Math.Ceiling((M + E + 7) * scale), m = (int)Math.Ceiling(M * scale) + 1, mid = n / 2 - 1;
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
            // Room for every view's radius; small bitmaps (~80 KB each)
            if (shadows.Count > 16) { foreach (var b in shadows.Values) b.Dispose(); shadows.Clear(); }
            // Square wide enough for full corner smoothing
            float E = Ink.CornerExtent(R), inner = 2 * (E + 7) + ShadowCore;
            int side = (int)Math.Ceiling((2 * M + inner) * scale);
            img = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(img))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.ScaleTransform(scale, scale);
                using (var sp = new Painter { G = sg }) ShadowLayers(sp, new RectangleF(M, M, inner, inner), R, strength);
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
                    using (var sp = new Painter { G = sg }) ShadowLayers(sp, new RectangleF(M, M, (float)Math.Round(r.Width), (float)Math.Round(r.Height)), R, strength);
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

        // Ambient wide and soft; key tight, lower
        static readonly float[] ShadowGrow = { 3f, 6f, 9f, 11.5f, 13.5f, 0.5f, 1.5f, 2.5f }, ShadowDrop = { 2f, 3f, 4f, 5f, 4.5f, 1.5f, 2f, 2.5f };

        static void ShadowLayers(Painter sp, RectangleF r, float radius, float strength)
        {
            for (int i = 0; i < ShadowAlpha.Length; i++)
            {
                float grow = ShadowGrow[i];
                var rr = new RectangleF(r.X - grow, r.Y - grow + ShadowDrop[i], r.Width + grow * 2, r.Height + grow * 2);
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

        // This frame's bridge to the bubble
        readonly GraphicsPath bridge = new GraphicsPath(FillMode.Winding);
        bool bridgeOn;
        // Neck snaps once the gap passes this
        const float BridgeSnap = 7f;

        float CapR { get { return Math.Min(shell.Height / 2, 19f); } }

        // Born inside the end cap
        float BubbleRadius(float pb) { return Anim.Reduced ? BubbleR : BubbleR * (0.5f + 0.5f * Ease.Clamp01(pb)); }

        float BubbleX(float pb)
        {
            float rest = shell.Right + BubbleGap + BubbleR;
            return Anim.Reduced ? rest : Ease.Lerp(shell.Right - CapR, rest, pb);
        }

        // Bubble joined by a thinning neck
        void DrawBubbleShape(float cy, float bx, float br, int pxW)
        {
            bridge.Reset();
            bridgeOn = false;
            float capR = CapR, capX = shell.Right - capR, gapNow = bx - br - shell.Right;
            if (!Anim.Reduced && gapNow < BridgeSnap)
            {
                float v = 0.5f * (float)Math.Pow(Ease.Clamp01(1 - gapNow / BridgeSnap), 0.6);
                bridgeOn = v > 0.02f && Metaball(bridge, capX, cy, capR, bx, cy, br, v);
            }
            if (!bridgeOn) { bridge.Reset(); bridge.AddEllipse(bx - br, cy - br, br * 2, br * 2); }
            float left = bridgeOn ? capX : bx - br, half = bridgeOn ? Math.Max(br, capR) : br;
            var bounds = new RectangleF(left, cy - half, bx + br - left, half * 2);
            // Reduced motion: a plain fade in place
            if (Anim.Reduced && bubble.Value < 0.98f)
            {
                float a = Ease.Clamp01(bubble.Value);
                g.FillPath(p.B(Ink.A(Ink.Base.Bg, 0.82f * a)), bridge);
                p.Alpha = a;
                DrawRim(bridge, bounds, !Ink.Base.IsDark, !GlassOn);
                p.Alpha = 1f;
                return;
            }
            FillSurface(bridge, bounds, Ink.Base, 0.42f, pxW);
        }

        static PointF On(double x, double y, double a, double r) { return new PointF((float)(x + r * Math.Cos(a)), (float)(y + r * Math.Sin(a))); }

        static double Unit(double v) { return v < -1 ? -1 : v > 1 ? 1 : v; }

        // Metaball: tangent beziers, far side arc
        static bool Metaball(GraphicsPath path, float x1, float y1, float r1, float x2, float y2, float r2, float v)
        {
            double dx = x2 - x1, dy = y2 - y1, d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= Math.Abs(r1 - r2) + 0.5) return false;
            double u1 = 0, u2 = 0;
            if (d < r1 + r2)
            {
                u1 = Math.Acos(Unit((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d)));
                u2 = Math.Acos(Unit((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d)));
            }
            double towards = Math.Atan2(dy, dx), spread = Math.Acos(Unit((r1 - r2) / d));
            double a1 = towards + u1 + (spread - u1) * v, a2 = towards - u1 - (spread - u1) * v;
            double a3 = towards + Math.PI - u2 - (Math.PI - u2 - spread) * v, a4 = towards - Math.PI + u2 + (Math.PI - u2 - spread) * v;
            PointF p1 = On(x1, y1, a1, r1), p2 = On(x1, y1, a2, r1), p3 = On(x2, y2, a3, r2), p4 = On(x2, y2, a4, r2);
            double gx = p3.X - p1.X, gy = p3.Y - p1.Y, total = r1 + r2;
            double k = Math.Min(v * 2.4, Math.Sqrt(gx * gx + gy * gy) / total) * Math.Min(1, d * 2 / total);
            PointF h1 = On(p1.X, p1.Y, a1 - Math.PI / 2, r1 * k), h2 = On(p2.X, p2.Y, a2 + Math.PI / 2, r1 * k);
            PointF h3 = On(p3.X, p3.Y, a3 + Math.PI / 2, r2 * k), h4 = On(p4.X, p4.Y, a4 - Math.PI / 2, r2 * k);
            path.StartFigure();
            path.AddBezier(p1, h1, h3, p3);
            // p3 round the far side to p4
            float start = (float)(a3 * 180 / Math.PI), sweep = (float)((a4 - a3) * 180 / Math.PI);
            while (sweep > 0) sweep -= 360;
            while (sweep <= -360) sweep += 360;
            path.AddArc(x2 - r2, y2 - r2, r2 * 2, r2 * 2, start, sweep);
            path.AddBezier(p4, h4, h2, p2);
            path.CloseFigure();
            return true;
        }

        void DrawContent(DateTime now, Tone tone, bool registerHits)
        {
            p.T = tone;
            // Text shadow on the pill only
            p.Shadow = GlassOn && !LiquidOn && tone.Bg.R < 128 && current != View.Panel;
            TrackPress();
            float t = (float)(now - switchedUtc).TotalSeconds;
            // New view follows 50 ms later
            if (tone == Ink.Base) DrawSnapshot(now);
            float inA = Ease.OutCubic((t - 0.05f) / 0.22f);
            float grow = Anim.Reduced ? 0 : 1 - Springs.Progress(t - 0.05f, 0.34f, 0.86f);
            DrawViewAt(current, inA, 1 + 0.04f * grow, registerHits && inA > 0.8f && grow < 0.15f, now);
            if (registerHits) DrawFocusRing();
        }

        void DrawViewAt(View v, float alpha, float zoom, bool registerHits, DateTime now)
        {
            if (alpha <= 0.01f) return;
            var st = g.Save();
            float w = shell.Width, h = shell.Height;
            g.TranslateTransform(shell.X, shell.Y);
            // Settles from 1.04 about the centre
            if (Math.Abs(zoom - 1) > 0.0005f)
            {
                g.TranslateTransform(w / 2, h / 2);
                g.ScaleTransform(zoom, zoom);
                g.TranslateTransform(-w / 2, -h / 2);
            }
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

        void AddHit(RectangleF local, string id, Action act) { AddHit(local, id, act, null, null); }

        void AddHit(RectangleF local, string id, Action act, Action<float> drag, Action menu) { AddHit(local, id, act, drag, menu, null); }

        void AddHit(RectangleF local, string id, Action act, Action<float> drag, Action menu, Action dragEnd)
        {
            if (inSlot && fpsLog) Log.Write("live: hit " + id + " inside a live area is lost");
            // No pointer, no hits to allocate
            if (!hitsOn || !(pointerInside || keyboardMode || dragId != null)) return;
            local.Offset(originX, originY + hitDy);
            Hit h;
            if (hitPool.Count > 0) { h = hitPool[hitPool.Count - 1]; hitPool.RemoveAt(hitPool.Count - 1); }
            else h = new Hit();
            h.Rect = local;
            h.Id = id;
            h.Act = act;
            h.Drag = drag;
            h.Menu = menu;
            h.DragEnd = dragEnd;
            hits.Add(h);
        }

        // Hits are reused frame to frame
        readonly List<Hit> hitPool = new List<Hit>();

        void ClearHits()
        {
            hitPool.AddRange(hits);
            hits.Clear();
        }

        Hit HitById(string id)
        {
            if (id == null) return null;
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Id == id) return hits[i];
            return null;
        }

        static float Fraction(Hit h, PointF pt) { return Ease.Clamp01((pt.X - h.Rect.X) / Math.Max(1, h.Rect.Width)); }

        // Dragged slider; survives hit rebuilds
        string dragId;

        bool Hover(string id) { return hoverId == id; }

        // Held down under the pointer
        bool Pressed(string id) { return pressedId == id && hoverId == id; }

        // Input

        bool InShape(PointF pt)
        {
            var s = shell;
            s.Inflate(2, 2);
            return s.Contains(pt) || (!bubbleRect.IsEmpty && bubbleRect.Contains(pt));
        }

        bool CanExpand() { return !hiddenByUser && !PrompterOn && approvals.Count == 0 && (toast == null || !toast.Interactive); }

        void Expand(int pg)
        {
            if (!CanExpand()) return;
            toast = null;
            toastTimer.Stop();
            expanded = true;
            PickDecor();
            StartBubbles(12);
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
            if (prompterSizing != 0) { SizePrompterTo(Control.MousePosition); return; }
            if (dragId != null)
            {
                var dh = HitById(dragId);
                if (dh != null && dh.Drag != null) dh.Drag(Fraction(dh, mouse));
                Kick();
                return;
            }
            if (ShelfDragCheck()) return;
            bool inside = InShape(mouse);
            if (current == View.Prompter) PrompterHover(mouse, inside);
            if (inside && !pointerInside)
            {
                pointerInside = true;
                Kick();
                leaveTimer.Stop();
                if (!expanded) { hoverTimer.Stop(); hoverTimer.Start(); PrefetchFrost(); }
            }
            else if (!inside && pointerInside) { PointerLeave(); return; }
            string id = inside ? HitAt(mouse) : null;
            // Redraw only when the hovered control changes
            if (id != hoverId) { hoverId = id; Kick(); }
        }

        void PointerLeave()
        {
            // Dragging slider or file: stay open
            if (dragId != null || draggingOut) return;
            if (prompterSizing != 0) return;
            pointerInside = false;
            hoverTimer.Stop();
            leaveTimer.Stop();
            leaveTimer.Start();
            if (hoverId != null) { hoverId = null; Kick(); }
            PrompterHover(mouse, false);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (pointerInside) PointerLeave();
        }

        // Capture lost mid-resize: keep what's there
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (prompterSizing != 0 && !Capture) EndPrompterSize();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                var pt = new PointF(e.X / scale, e.Y / scale);
                if (current == View.Prompter && PrompterGrab(pt, Control.MousePosition)) return;
                pressedId = HitAt(pt);
                var h = HitById(pressedId);
                if (h != null && h.Drag != null) { dragId = pressedId; Capture = true; h.Drag(Fraction(h, pt)); }
                Kick();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var pt = new PointF(e.X / scale, e.Y / scale);
            if (prompterSizing != 0)
            {
                EndPrompterSize();
                Capture = false;
                if (!InShape(pt)) PointerLeave();
                return;
            }
            if (dragId != null)
            {
                var dh = HitById(dragId);
                if (dh != null && dh.DragEnd != null) dh.DragEnd();
                dragId = null;
                Capture = false;
                pressedId = null;
                Kick();
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                var mh = HitById(HitAt(pt));
                if (mh != null && mh.Menu != null) { mh.Menu(); return; }
                menu.Show(Control.MousePosition);
                return;
            }
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
            else if (id == null && current == View.Prompter && InShape(pt)) TogglePrompter();
            else if (id == null && !expanded && InShape(pt) && CanExpand())
            {
                hoverTimer.Stop();
                bool onBubble = !bubbleRect.IsEmpty && bubbleRect.Contains(pt);
                if (onBubble && BubbleKind() == View.Task) ShowTasks();
                else if (onBubble && BubbleKind() == View.Timer) Expand(PageControl);
                else Expand(onBubble && BubbleKind() == View.Agent ? PageAgents : DefaultPage(false));
            }
            pressedId = null;
            Kick();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (current == View.Prompter) { ScrollPrompter(e.Delta); return; }
            if (current == View.Approve && ScrollApproval(e.Delta)) return;
            // Open panel: wheel changes tabs, lists scroll
            if (expanded)
            {
                bool overTabs = mouse.Y - shell.Y < PanelTop - 2;
                if (overTabs || !ScrollPanel(e.Delta)) WheelTab(-e.Delta);
                return;
            }
            if (current == View.Approve) return;
            vol = Ease.Clamp01(vol + e.Delta / 120f * 0.04f);
            volBar.Target = vol;
            volume.Set(vol);
        }

        bool Docked { get { return settings.GetBool("dock", true); } }

        // Hide on demand; permission cards still show
        const int HideHotkey = 1;
        bool hiddenByUser;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        bool hideTipShown;

        void ToggleHidden()
        {
            hiddenByUser = !hiddenByUser;
            if (hiddenByUser) expanded = false;
            // Hidden island can't say how to return, so the tray does
            if (hiddenByUser && !hideTipShown && tray != null)
            {
                hideTipShown = true;
                tray.ShowBalloonTip(5000, Lang.T("Island disembunyikan"), Lang.T("Klik ikon Tepi di tray atau tekan Ctrl+Alt+H untuk memunculkannya lagi."), ToolTipIcon.None);
            }
            Kick();
        }

        // Toasts

        Toast ShowToast(View v, double seconds, int priority, bool interactive, bool force = false)
        {
            if (toast != null && toast.Priority > priority) return null;
            // Senyap: only what can't wait
            if (Quiet && priority < 3 && (v == View.Notif || v == View.Track || v == View.Network || v == View.Privacy || v == View.Remind || v == View.Download || v == View.Info || v == View.Done)) return null;
            // Retracted: quiet news stays in the sliver
            if (MiniOn() && !MiniLets(v, priority)) return null;
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
                if (!Anim.Reduced) topGap.Velocity += 70; // arrival dip
            }
            toast = t;
            if (!same) EyesToast(v, t);
            toastTimer.Stop();
            toastTimer.Interval = Math.Max(1, (int)(seconds * 1000));
            toastTimer.Start();
            Kick();
            return t;
        }

        // Notices that pass Senyap, like timers
        void ShowNotice(char glyph, string title, string detail)
        {
            var t = ShowToast(View.Info, 3.2, 3, false, true);
            if (t == null) return;
            EyesReact(EyeMood.Happy, 0);
            t.Glyph = glyph;
            t.Title = title;
            t.Detail = detail;
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
            bool fs = Native.IsFullscreenOn(monitorHandle);
            if (!fs && windowShown) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (fs == fullscreen) return;
            fullscreen = fs;
            if (fs) expanded = false;
            Kick();
        }

        void ClockTick()
        {
            var nowUtc = DateTime.UtcNow;
            string hm = Fmt.Hm(DateTime.Now);
            bool minute = hm != clockRoller.Value;
            clockRoller.Set(hm);
            // The clock rolls only where it shows
            bool redraw = minute && current == View.Idle;
            redraw |= LifeTick(minute);
            // Running timers tick in their live areas
            TimerTick(nowUtc);
            redraw |= RemindTick(nowUtc);
            ThemeTick(nowUtc);
            MemoryWatch();
            DropSpareGlass();
            ProfTick();
            MiniClock();
            // Panel texts change by the minute
            if (redraw || (expanded && minute) || DesiredView() != current) Kick();
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
            // Rebuilt when the language changes
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (menu != null) menu.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
            menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            MenuLook.Apply(menu);
            var title = new ToolStripMenuItem("Tepi") { Enabled = false };
            var kuliaItem = new ToolStripMenuItem("Kulia");
            var hookItem = new ToolStripMenuItem(Lang.T("Hubungkan agen"));
            var dataItem = new ToolStripMenuItem(Lang.T("Buka folder data"), null, delegate
            {
                try { Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); } catch (Exception ex) { Log.Write("open dir: " + ex.Message); }
            });
            var updateItem = new ToolStripMenuItem(Lang.T("Perbarui"), null, delegate { InstallUpdate(); }) { Visible = false };
            var hideItem = new ToolStripMenuItem(Lang.T("Sembunyikan island"), null, delegate { ToggleHidden(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+H" };
            var keysItem = new ToolStripMenuItem(Lang.T("Navigasi keyboard"), null, delegate { EnterKeyboard(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+I" };
            var miniItem = new ToolStripMenuItem(Lang.T("Mini"), null, delegate { ToggleMini(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+K" };
            var prompterItem = new ToolStripMenuItem(Lang.T("Teleprompter"), null, delegate { if (!PrompterOn) OpenPrompter(); });
            var focusItem = new ToolStripMenuItem(Lang.T("Timer"));
            var exit = new ToolStripMenuItem(Lang.T("Keluar"), null, delegate { Close(); });
            var prefsItem = new ToolStripMenuItem(Lang.T("Pengaturan…"), null, delegate { OpenPrefs(); });
            menu.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), updateItem, miniItem, hideItem, keysItem, focusItem, prompterItem, hookItem, kuliaItem, new ToolStripSeparator(), prefsItem, dataItem, exit });
            menu.Opening += delegate
            {
                // Style submenus after they're rebuilt
                BeginInvoke(new Action(delegate { MenuLook.Apply(menu); }));
                hideItem.Checked = hiddenByUser;
                miniItem.Checked = MiniWanted();
                prompterItem.Checked = PrompterOn;
                // Running script: controls live here
                prompterItem.DropDownItems.Clear();
                if (PrompterOn)
                    prompterItem.DropDownItems.AddRange(new ToolStripItem[]
                    {
                        new ToolStripMenuItem(Lang.T(prompterPaused ? "Lanjutkan" : "Jeda"), null, delegate { TogglePrompter(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+Space" },
                        new ToolStripMenuItem(Lang.T("Ulang dari awal"), null, delegate { RestartPrompter(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+R" },
                        new ToolStripMenuItem(Lang.T("Mundur 5 detik"), null, delegate { SeekPrompter(-5); }) { ShortcutKeyDisplayString = "Ctrl+Alt+Left" },
                        new ToolStripMenuItem(Lang.T("Maju 5 detik"), null, delegate { SeekPrompter(5); }) { ShortcutKeyDisplayString = "Ctrl+Alt+Right" },
                        new ToolStripMenuItem(Lang.T("Atur…"), null, delegate { BeginInvoke(new Action(OpenPrompter)); }) { ShortcutKeyDisplayString = "Ctrl+Alt+P" },
                        new ToolStripMenuItem(Lang.T("Lebih cepat"), null, delegate { NudgeSpeed(1); }) { ShortcutKeyDisplayString = "Ctrl+Alt+=" },
                        new ToolStripMenuItem(Lang.T("Lebih lambat"), null, delegate { NudgeSpeed(-1); }) { ShortcutKeyDisplayString = "Ctrl+Alt+-" },
                        new ToolStripSeparator(),
                        new ToolStripMenuItem(Lang.T("Hentikan"), null, delegate { StopPrompter(); }) { ShortcutKeyDisplayString = "Ctrl+Alt+X" },
                    });
                FillFocusMenu(focusItem);
                updateItem.Visible = newVersion != null;
                if (newVersion != null) updateItem.Text = Lang.F(newAsset != null ? Lang.T("Perbarui ke Tepi {0}") : Lang.T("Unduh Tepi {0}"), newVersion);
                CheckLinks();
                hookItem.DropDownItems.Clear();
                hookItem.DropDownItems.Add(new ToolStripMenuItem("Claude Code" + (hookProfiles > 0 ? "" : Lang.T(" · tidak ditemukan")), null, delegate { ToggleHooks(); })
                {
                    Checked = hooksOn,
                    Enabled = hookProfiles > 0
                });
                foreach (var agentId in AgentSetup.Ids)
                {
                    string id = agentId;
                    bool present = AgentSetup.Present(id);
                    hookItem.DropDownItems.Add(new ToolStripMenuItem(AgentSetup.Label(id) + (present ? "" : Lang.T(" · tidak ditemukan")), null, delegate { ToggleAgent(id); })
                    {
                        Checked = present && linked.Contains(AgentSetup.Label(id)),
                        Enabled = present
                    });
                }
                kuliaItem.DropDownItems.Clear();
                if (kulia != null && kulia.SignedIn)
                {
                    kuliaItem.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Masuk sebagai ") + kulia.Email) { Enabled = false });
                    kuliaItem.DropDownItems.Add(Lang.T("Perbarui tugas"), null, delegate { RefreshTasks(true); });
                    kuliaItem.DropDownItems.Add(Lang.T("Keluar dari Kulia"), null, delegate { SignOutKulia(); });
                }
                else kuliaItem.DropDownItems.Add(Lang.T("Masuk ke Kulia…"), null, delegate { OpenLogin(); });
            };
            trayIcon = MakeTrayIcon();
            tray = new NotifyIcon { Icon = trayIcon, Text = "Tepi", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button != MouseButtons.Left) return; if (hiddenByUser) ToggleHidden(); else Expand(DefaultPage(false)); };
        }



        // Rebuilt each time the menu opens
        void FillFocusMenu(ToolStripMenuItem focusItem)
        {
            focusItem.DropDownItems.Clear();
            focusItem.Checked = TimerOn;
            foreach (var m in new[] { 25, 50 })
            {
                int minutes = m;
                focusItem.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Fokus ") + minutes + Lang.T(" menit"), null, delegate { StartTimer(TimerKind.Focus, minutes); }));
            }
            focusItem.DropDownItems.Add(new ToolStripSeparator());
            foreach (var m in new[] { 5, 10, 15 })
            {
                int minutes = m;
                focusItem.DropDownItems.Add(new ToolStripMenuItem(Lang.F("Timer {0} menit", minutes), null, delegate { StartTimer(TimerKind.Countdown, minutes); }));
            }
            focusItem.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Stopwatch"), null, delegate { StartTimer(TimerKind.Stopwatch, 0); }));
            focusItem.DropDownItems.Add(new ToolStripSeparator());
            focusItem.DropDownItems.Add(new ToolStripMenuItem(timerPaused ? Lang.T("Lanjutkan") : Lang.T("Jeda"), null, delegate { ToggleTimerPause(); }) { Enabled = TimerOn });
            focusItem.DropDownItems.Add(new ToolStripMenuItem(Lang.T("Hentikan"), null, delegate { StopTimer(); }) { Enabled = TimerOn });
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
