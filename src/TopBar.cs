using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // Bar atas: a menu bar along the top edge, the island as its notch
    partial class Island
    {
        TopBar topBar;

        bool TopBarOn { get { return settings.Get("desk", "biasa") == "bar"; } }

        // Readers the bar shows too
        bool WantsDisk { get { return DockWidget("disk") || (TopBarOn && settings.GetBool("bar.disk", true)); } }
        bool WantsNet { get { return DockWidget("net") || (TopBarOn && settings.GetBool("bar.net", true)); } }

        // Keys the bar look takes over; given back exactly when it ends
        static readonly string[] DeskKeys = { "dockbar.on", "dockbar.replace", "place", "dock" };
        const string NoValue = "\u0001";

        void SetDesk(bool bar)
        {
            if (bar == TopBarOn) return;
            if (bar)
            {
                var sb = new StringBuilder();
                foreach (var k in DeskKeys) sb.Append(k).Append('=').Append(settings.Has(k) ? settings.Get(k, "") : NoValue).Append('\n');
                settings.Set("desk.prev", Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())));
                settings.Set("dockbar.on", true);
                settings.Set("dockbar.replace", true);
                settings.Set("place", "top");
                settings.Set("dock", true);
                settings.Set("desk", "bar");
            }
            else
            {
                try
                {
                    string saved = Encoding.UTF8.GetString(Convert.FromBase64String(settings.Get("desk.prev", "")));
                    foreach (var line in saved.Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        if (Array.IndexOf(DeskKeys, k) < 0) continue;
                        if (v == NoValue) settings.Remove(k);
                        else settings.Set(k, v);
                    }
                }
                catch (Exception ex) { Log.Write("desk restore: " + ex.Message); }
                settings.Remove("desk.prev");
                settings.Set("desk", "biasa");
            }
            settings.Save();
            LoadPlace();
            ResetFrost();
            ApplyDock();
            Kick();
        }

        void SetBarHide(bool on)
        {
            settings.Set("bar.hide", on);
            settings.Save();
            if (topBar != null) topBar.Changed();
        }

        void ApplyTopBar()
        {
            bool want = TopBarOn && DockBarOn && dockWin != null;
            // A new dock: the bar follows it
            if (topBar != null && (!want || topBar.BarDock != dockWin)) { topBar.Shutdown(); topBar = null; }
            if (want && topBar == null)
            {
                topBar = new TopBar(this, dockWin);
                topBar.Show();
                KeepOnTop();
            }
            else if (topBar != null) topBar.Changed();
        }

        sealed class TopBar : Form
        {
            readonly Island isle;
            readonly DockWindow dock;
            readonly Painter p = new Painter();
            DibCanvas surface;
            Bitmap canvas;
            Graphics g;
            int cw, ch;
            float scale = 1;
            Rectangle mon;
            bool registered, closing, shown = true;
            readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
            readonly Dictionary<string, Action> acts = new Dictionary<string, Action>();
            readonly Dictionary<string, Rectangle> spots = new Dictionary<string, Rectangle>();
            string hotId, pressId, drawnSig = "";
            readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
            readonly ToolTip tip = new ToolTip { ShowAlways = true, InitialDelay = 400 };
            Tone tone, toneFrom;
            string appName = "", menuId;
            IntPtr frontH;
            readonly Dictionary<string, string> nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Native.WinEventProc onFront;
            IntPtr frontHook;

            // As tall as the island, so it reads as the notch
            const float BarH = 34, Pad = 10;
            const int AppBarMsg = Native.WM_APP + 0x33;

            [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
            [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hw, ref Margins m);
            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hw, int attr, ref int value, int size);
            [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int hh, IntPtr src, int sx, int sy, int rop);
            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
            [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
            [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
            [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
            [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
            [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
            [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Native.RECT r);
            [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
            [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);
            [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

            // Auto-hide: tucked above the screen, the top edge brings it down
            bool AutoHide { get { return isle.settings.GetBool("bar.hide", false); } }
            bool wasAuto, visible = true;
            float slide = 1, slideFrom = 1, slideTo = 1;
            int edgeHits, awayMs;
            readonly Stopwatch slideClock = new Stopwatch();
            readonly System.Windows.Forms.Timer edge = new System.Windows.Forms.Timer { Interval = 100 };
            readonly System.Windows.Forms.Timer slider = new System.Windows.Forms.Timer { Interval = 15 };
            Native.WinEventProc onMoved;
            IntPtr movedHook;

            [StructLayout(LayoutKind.Sequential)] struct MonInfo { public int Size; public Native.RECT Mon, Work; public int Flags; }
            [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MonInfo i);
            [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, int flags);
            [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int p, ref Native.RECT r, int ini);

            // Explorer takes the reservation but may skip the work area; set it ourselves
            void HoldWork(bool reserve)
            {
                if (DockWindow.Dry || ch <= 0) return;
                var mi = new MonInfo { Size = Marshal.SizeOf(typeof(MonInfo)) };
                if (!GetMonitorInfo(MonitorFromPoint(new Point(mon.Left + 1, mon.Top + 1), 2), ref mi)) return;
                int barBottom = mon.Top + ch, top = mi.Work.Top;
                int want = reserve ? barBottom : mon.Top;
                // Freed: only undo what the bar itself took
                if (top == want || (!reserve && top != barBottom)) return;
                var r = mi.Work;
                r.Top = want;
                SystemParametersInfo(0x2F, 0, ref r, 2);
            }

            public DockWindow BarDock { get { return dock; } }

            public TopBar(Island isle, DockWindow dock)
            {
                this.isle = isle;
                this.dock = dock;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                Text = Lang.T("Bar atas");
                tick.Tick += delegate { Tick(); };
                edge.Tick += delegate { Edge(); };
                slider.Tick += delegate { Slide(); };
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
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(Handle, ref m);
                int none = unchecked((int)0xFFFFFFFE), square = 1;
                DwmSetWindowAttribute(Handle, 34, ref none, 4);
                DwmSetWindowAttribute(Handle, 33, ref square, 4);
                onFront = delegate (IntPtr h, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time) { Front(hwnd); };
                frontHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, onFront, 0, 0, 0);
                // A window dragged under the bar comes back down
                onMoved = delegate (IntPtr h, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time) { if (obj == 0) KeepBelow(hwnd); };
                movedHook = Native.SetWinEventHook(0x0B, 0x0B, IntPtr.Zero, onMoved, 0, 0, 0);
                Place();
                Front(GetForegroundWindow());
                tick.Start();
            }

            // Island moved screens or the display changed
            public void Changed()
            {
                if (IsDisposed || closing) return;
                Place();
            }

            public void Shutdown()
            {
                closing = true;
                tick.Stop();
                edge.Stop();
                slider.Stop();
                if (frontHook != IntPtr.Zero) Native.UnhookWinEvent(frontHook);
                if (movedHook != IntPtr.Zero) Native.UnhookWinEvent(movedHook);
                frontHook = movedHook = IntPtr.Zero;
                Reserve(false);
                HoldWork(false);
                Close();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                tick.Dispose();
                edge.Dispose();
                slider.Dispose();
                tip.Dispose();
                if (wall != null) wall.Dispose();
                if (g != null) { g.Dispose(); canvas.Dispose(); surface.Dispose(); }
                p.Dispose();
                base.OnFormClosed(e);
            }

            // A strip along the top: maximized windows stop under it
            void Place()
            {
                mon = isle.monitorPx;
                scale = DpiScale(isle.monitorHandle);
                bool auto = AutoHide;
                // Turned on: starts tucked away
                if (!auto) slide = slideFrom = slideTo = 1;
                else if (!wasAuto) slide = slideFrom = slideTo = 0;
                wasAuto = auto;
                slider.Stop();
                // Test runs: drawn off screen, nothing reserved
                if (!DockWindow.Dry) Reserve(!auto);
                int h = (int)Math.Ceiling(BarH * scale);
                var rc = new Rectangle(mon.Left, DockWindow.Dry ? -30000 : TopAt(h), mon.Width, h);
                Bounds = rc;
                if (rc.Width != cw || rc.Height != ch) MakeCanvas(rc.Width, rc.Height);
                RefreshWall(true);
                drawnSig = "";
                Render();
                SyncVisible();
                if (auto) edge.Start(); else edge.Stop();
                HoldWork(!auto);
                if (!auto) KeepBelow(GetAncestor(GetForegroundWindow(), 2));
            }

            int TopAt(int h) { return mon.Top - (int)Math.Round((1 - Ease.OutCubic(slide)) * h); }

            void SyncVisible()
            {
                bool want = shown && slide > 0;
                if (want == visible) return;
                visible = want;
                Native.ShowWindow(Handle, want ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
            }

            // Pointer at the top edge brings it down; away for half a second, up it goes
            void Edge()
            {
                if (closing || !AutoHide || !shown || DockWindow.Dry) return;
                var c = Cursor.Position;
                bool across = c.X >= mon.Left && c.X < mon.Right;
                if (slideTo == 0)
                {
                    // A short rest at the edge, so a quick click on a tab stays a click
                    bool atTop = across && c.Y <= mon.Top && Control.MouseButtons == MouseButtons.None;
                    edgeHits = atTop ? edgeHits + 1 : 0;
                    if (edgeHits >= 2) SlideTo(1);
                    return;
                }
                bool near = across && c.Y < mon.Top + ch + (int)(8 * scale) && c.Y >= mon.Top;
                bool busy = menuId != null || pressId != null || dock.Busy;
                awayMs = near || busy ? 0 : awayMs + edge.Interval;
                if (awayMs >= 500) SlideTo(0);
            }

            void SlideTo(float to)
            {
                edgeHits = awayMs = 0;
                if (to == slideTo) return;
                slideFrom = slide;
                slideTo = to;
                if (to > 0 && slide == 0) { drawnSig = ""; Render(); }
                if (Anim.Reduced) { slide = to; Slide(); return; }
                slideClock.Restart();
                slider.Start();
            }

            void Slide()
            {
                if (closing) { slider.Stop(); return; }
                float t = Anim.Reduced ? 1 : Math.Min(1, (float)slideClock.Elapsed.TotalSeconds / 0.18f);
                slide = slideFrom + (slideTo - slideFrom) * t;
                if (t >= 1) { slide = slideTo; slider.Stop(); }
                SetWindowPos(Handle, IntPtr.Zero, mon.Left, TopAt(ch), 0, 0, 0x0001 | 0x0004 | 0x0010);
                SyncVisible();
            }

            // Bar always shown: windows sit under it, like under the Windows taskbar
            void KeepBelow(IntPtr h)
            {
                if (closing || AutoHide || DockWindow.Dry || !shown || h == IntPtr.Zero || h == Handle) return;
                if (GetAncestor(h, 2) != h || !IsWindowVisible(h) || IsIconic(h)) return;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == Process.GetCurrentProcess().Id) return;
                int style = Native.GetWindowLong(h, -16), ex = Native.GetWindowLong(h, -20);
                // Only real windows: framed, not tool or always-on-top overlays
                if ((style & 0x40000000) != 0 || (style & 0x00C40000) == 0 || (ex & 0x88) != 0) return;
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                switch (cls.ToString())
                {
                    case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd": case "Windows.UI.Core.CoreWindow": return;
                }
                Native.RECT f, w;
                if (!GetWindowRect(h, out w)) return;
                if (DwmGetWindowAttribute(h, 9, out f, Marshal.SizeOf(typeof(Native.RECT))) != 0) f = w;
                // Fullscreen apps keep the whole screen; the bar steps aside
                if (f.Left <= mon.Left && f.Top <= mon.Top && f.Right >= mon.Right && f.Bottom >= mon.Bottom) return;
                var mid = new Point((f.Left + f.Right) / 2, (f.Top + f.Bottom) / 2);
                if (!mon.Contains(mid)) return;
                int barBottom = mon.Top + ch, dy = barBottom - f.Top;
                if (dy <= 0 || f.Bottom <= mon.Top) return;
                var work = Screen.FromHandle(h).WorkingArea;
                int top = w.Top + dy, height = w.Bottom - w.Top;
                // Too tall to move whole: the bottom stays, the top comes down
                if (IsZoomed(h) || top + height > work.Bottom) height = Math.Max(200, w.Bottom - top);
                SetWindowPos(h, IntPtr.Zero, w.Left, top, w.Right - w.Left, height, 0x0004 | 0x0010 | 0x4000);
            }

            void Reserve(bool on)
            {
                var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle, uCallbackMessage = AppBarMsg };
                if (!on)
                {
                    if (registered) Native.SHAppBarMessage(1, ref abd);
                    registered = false;
                    return;
                }
                if (!registered) { Native.SHAppBarMessage(0, ref abd); registered = true; }
                int h = (int)Math.Ceiling(BarH * scale);
                abd.uEdge = 1;
                abd.rc = new Native.RECT { Left = mon.Left, Right = mon.Right, Top = mon.Top, Bottom = mon.Top + h };
                Native.SHAppBarMessage(2, ref abd);
                abd.rc.Bottom = abd.rc.Top + h;
                Native.SHAppBarMessage(3, ref abd);
            }

            protected override void WndProc(ref Message m)
            {
                // Wallpaper or theme changed
                if (m.Msg == 0x1A && !closing && cw > 0) { Wallpaper.Forget(); if (RefreshWall(true)) Render(); }
                if (m.Msg == AppBarMsg)
                {
                    if (m.WParam.ToInt64() == 1 && !closing) Place();
                    return;
                }
                base.WndProc(ref m);
            }

            // Glass cut from the wallpaper behind the bar, lightly blurred; kept in screenshots.
            // Always on: the island's material doesn't decide the bar's
            bool GlassLook { get { return isle != null; } }
            Bitmap wall;
            string wallKey;
            float veil = 0.3f;
            int wallTick;

            // True when the picture behind changed
            bool RefreshWall(bool force)
            {
                if (!GlassLook || cw <= 0)
                {
                    bool had = wall != null;
                    if (wall != null) wall.Dispose();
                    wall = null;
                    wallKey = null;
                    return had;
                }
                string k = Wallpaper.Key(mon) + "|" + cw + "x" + ch + "|" + Ink.Light;
                if (!force && k == wallKey) return false;
                wallKey = k;
                if (wall != null) wall.Dispose();
                float lo, hi;
                wall = Wallpaper.Cut(mon, new Rectangle(mon.X, mon.Y, mon.Width, ch), cw, ch, out lo, out hi);
                veil = Ink.Light ? Math.Min(0.72f, 0.22f + 0.45f * (1 - lo)) : Math.Min(0.72f, 0.08f + 0.5f * hi);
                return true;
            }

            void MakeCanvas(int w, int h)
            {
                if (g != null) { g.Dispose(); canvas.Dispose(); surface.Dispose(); }
                cw = w;
                ch = h;
                surface = new DibCanvas(w, h);
                canvas = surface.Wrap();
                g = Graphics.FromImage(canvas);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                p.G = g;
            }

            // Clock, status, tray and readers each second; drawn only when what shows changed
            void Tick()
            {
                if (closing) return;
                bool want = !isle.fullscreen && !isle.dormant;
                if (want != shown)
                {
                    shown = want;
                    SyncVisible();
                    if (shown && AutoHide) edge.Start(); else edge.Stop();
                }
                // Explorer can reset it later, e.g. when the taskbar is hidden again
                if (++wallTick % 3 == 0 && !AutoHide) HoldWork(true);
                if (!shown || slide == 0) return;
                // Slideshows change the picture: look every half minute
                if (wallTick % 30 == 0 && RefreshWall(false)) { Render(); return; }
                if (Signature() != drawnSig) Render();
            }

            string Signature()
            {
                var sb = new StringBuilder();
                sb.Append(Fmt.Clock(DateTime.Now)).Append(appName).Append(hotId).Append(pressId).Append(menuId).Append(isle.vol).Append(isle.muted).Append(isle.battery).Append(isle.onAc).Append(Ink.Epoch);
                var net = isle.net.Current;
                sb.Append(net.Any).Append(net.Wifi).Append(net.Online);
                if (ShowNet) sb.Append(NetText(dock.NetDown)).Append(NetText(dock.NetUp));
                if (ShowDisk) sb.Append(dock.DiskSig);
                foreach (var ic in dock.TrayInline()) sb.Append(ic.Key).Append(ic.BmpStamp);
                sb.Append(dock.TrayOverflow().Count > 0);
                return sb.ToString();
            }

            bool ShowNet { get { return isle.settings.GetBool("bar.net", true) && dock.NetDown >= 0; } }
            bool ShowDisk { get { return isle.settings.GetBool("bar.disk", true) && dock.Disks.Length > 0; } }

            // Name of the app in front, as its file says
            void Front(IntPtr h)
            {
                if (h == IntPtr.Zero || closing) return;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                // Tepi's own windows keep the last name
                if (pid == Process.GetCurrentProcess().Id) return;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                string c = cls.ToString(), name;
                if (c == "Progman" || c == "WorkerW") { name = Lang.T("Desktop"); frontH = IntPtr.Zero; }
                else
                {
                    frontH = GetAncestor(h, 2);
                    KeepBelow(frontH);
                    string exe = ShellApp.ProcessPath(pid), aumid = ShellApp.ProcessAppId(pid);
                    // Store apps: the name Start shows, not the file's
                    if (aumid.Length > 0)
                    {
                        if (!nameOf.TryGetValue(aumid, out name))
                        {
                            name = ShellApp.DisplayName(ShellApp.AppsFolderOf(aumid));
                            if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(exe);
                            nameOf[aumid] = name;
                        }
                    }
                    else if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // Store apps sit in a frame: its title is the app
                        var t = new StringBuilder(256);
                        GetWindowText(h, t, 256);
                        name = t.ToString();
                    }
                    else if (!nameOf.TryGetValue(exe, out name))
                    {
                        name = "";
                        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription ?? ""; } catch (Exception) { }
                        if (name.Trim().Length == 0) name = System.IO.Path.GetFileNameWithoutExtension(exe);
                        nameOf[exe] = name = name.Trim();
                    }
                }
                if (name == appName) return;
                appName = name;
                Render();
            }

            void Render()
            {
                if (g == null || IsDisposed || closing) return;
                drawnSig = Signature();
                hits.Clear();
                acts.Clear();
                if (!ReferenceEquals(toneFrom, Ink.Base)) { toneFrom = Ink.Base; tone = GlassLook ? Ink.Base.OnGlass() : Ink.Base; }
                p.T = tone;
                p.HalfPx = 0.5f / scale;
                p.Alpha = 1;
                g.ResetTransform();
                g.Clear(Color.Transparent);
                // Clear glass: the wallpaper's colours, text with a soft shadow
                if (wall != null)
                {
                    g.DrawImageUnscaledAndClipped(wall, new Rectangle(0, 0, cw, ch));
                    g.FillRectangle(p.B(Ink.A(Ink.Base.Bg, veil)), 0, 0, cw, ch);
                }
                else g.FillRectangle(p.B(Ink.Base.Bg), 0, 0, cw, ch);
                p.Shadow = wall != null && !Ink.Light;
                g.ScaleTransform(scale, scale);
                float W = cw / scale, H = ch / scale, cy = H / 2;
                // Right first: the left side stops where the island begins
                float x = W - Pad;
                x = DrawClock(x, cy, H);
                x = Glyph(x, cy, H, "search", (char)0xE721, delegate { dock.OpenSheetFrom("start", spots["search"]); });
                x = DrawControl(x, cy, H);
                if (isle.hasBattery) x = DrawBattery(x, cy, H);
                x = DrawWifi(x, cy, H);
                x = DrawVolume(x, cy, H);
                if (ShowNet) x = DrawNet(x, cy, H);
                if (ShowDisk) x = DrawDisk(x, cy, H);
                x = DrawTray(x, cy, H);
                // Left: Tepi's eyes, the app in bold, then its menus
                float islandLeft = W / 2 - 110;
                var eyes = new RectangleF(Pad - 2, 4, 32, H - 8);
                Spot(eyes, "menu", ShowMenu);
                float ex = eyes.X + eyes.Width / 2;
                p.FillRound(new RectangleF(ex - 6.4f, cy - 4f, 3.8f, 8f), 1.9f, p.T.Text);
                p.FillRound(new RectangleF(ex + 2.6f, cy - 4f, 3.8f, 8f), 1.9f, p.T.Text);
                float lx = eyes.Right + 4;
                var bold = Ink.Semi(13f);
                string app = appName.Length > 0 ? appName : Lang.T("Desktop");
                float aw = Math.Min(220, p.Measure(app, bold)) + 18;
                if (lx + aw < islandLeft)
                {
                    var ar = new RectangleF(lx, 4, aw, H - 8);
                    Spot(ar, "m:app", delegate { OpenMenu("m:app"); });
                    p.Text(app, bold, p.T.Text, ar.X + 9, cy, aw - 18, StringAlignment.Near);
                    lx = ar.Right;
                }
                var reg = Ink.Text(13f);
                foreach (var m in MenuIds)
                {
                    string label = MenuName(m);
                    float mw = p.Measure(label, reg) + 18;
                    if (lx + mw > islandLeft) break;
                    var mr = new RectangleF(lx, 4, mw, H - 8);
                    string id = m;
                    Spot(mr, id, delegate { OpenMenu(id); });
                    p.Text(label, reg, p.T.Text, mr.X + 9, cy, mw - 10, StringAlignment.Near);
                    lx = mr.Right;
                }
                p.Shadow = false;
                g.Flush(FlushIntention.Sync);
                if (!IsHandleCreated) return;
                IntPtr hdc = Native.GetDC(Handle);
                BitBlt(hdc, 0, 0, cw, ch, surface.Dc, 0, 0, 0x00CC0020);
                Native.ReleaseDC(Handle, hdc);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (g == null) return;
                IntPtr hdc = e.Graphics.GetHdc();
                BitBlt(hdc, 0, 0, cw, ch, surface.Dc, 0, 0, 0x00CC0020);
                e.Graphics.ReleaseHdc(hdc);
            }

            // Hover plate, hit and screen spot for cards that open beneath
            void Spot(RectangleF r, string id, Action act)
            {
                hits.Add(new KeyValuePair<RectangleF, string>(r, id));
                if (act != null) acts[id] = act;
                spots[id] = new Rectangle(Left + (int)(r.X * scale), Top + (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale));
                if (menuId == id || (pressId == id && hotId == id)) p.FillRound(r, 6, Ink.A(p.T.Text, 0.22f));
                else if (hotId == id) p.FillRound(r, 6, Ink.A(p.T.Text, 0.12f));
            }

            float DrawClock(float right, float cy, float H)
            {
                var now = DateTime.Now;
                string s = now.ToString("ddd d MMM", Fmt.Id) + "   " + Fmt.Clock(now);
                var f = Ink.Text(13f);
                float w = p.Measure(s, f) + 18;
                var r = new RectangleF(right - w, 4, w, H - 8);
                Spot(r, "clock", delegate { dock.OpenSheetFrom("cal", spots["clock"]); });
                p.Text(s, f, p.T.Text, r.X + 9, cy, w - 10, StringAlignment.Near);
                return r.X;
            }

            // One icon, its own hover plate
            float Glyph(float right, float cy, float H, string id, char glyph, Action act)
            {
                var r = new RectangleF(right - 30, 4, 30, H - 8);
                Spot(r, id, act);
                p.Icon(glyph, 13.5f, p.T.Text, r.X + 15, cy);
                return r.X;
            }

            // Control Center: two switches, one on
            float DrawControl(float right, float cy, float H)
            {
                var r = new RectangleF(right - 30, 4, 30, H - 8);
                Spot(r, "control", delegate { dock.OpenSheetFrom("quick", spots["control"]); });
                float x = r.X + 8;
                var pen = p.P(p.T.Text, 1.3f);
                using (var a = Ink.Round(new RectangleF(x, cy - 6, 14, 5.2f), 2.6f)) g.DrawPath(pen, a);
                p.Circle(x + 11.3f, cy - 3.4f, 1.6f, p.T.Text);
                p.FillRound(new RectangleF(x, cy + 0.8f, 14, 5.2f), 2.6f, p.T.Text);
                p.Circle(x + 2.7f, cy + 3.4f, 1.6f, Ink.Base.Bg);
                return r.X;
            }

            float DrawBattery(float right, float cy, float H)
            {
                var r = new RectangleF(right - 38, 4, 38, H - 8);
                Spot(r, "battery", delegate { dock.OpenSheetFrom("quick", spots["battery"]); });
                p.Battery(r.X + 8, cy, isle.battery / 100f, isle.battery <= 20 && !isle.onAc ? p.T.Negative : p.T.Text, isle.onAc);
                return r.X;
            }

            float DrawWifi(float right, float cy, float H)
            {
                var net = isle.net.Current;
                char glyph = !net.Any ? Ink.IcoNoNet : net.Wifi ? Ink.IcoWifi : (char)0xE839;
                var r = new RectangleF(right - 30, 4, 30, H - 8);
                Spot(r, "wifi", delegate { dock.OpenSheetFrom("quick", spots["wifi"]); });
                p.Icon(glyph, 13.5f, net.Online || !net.Any ? p.T.Text : p.T.Sub, r.X + 15, cy);
                return r.X;
            }

            // Wheel over it changes the volume
            float DrawVolume(float right, float cy, float H)
            {
                char glyph = isle.muted ? Ink.IcoMute : isle.vol < 0.01f ? Ink.IcoVol0 : isle.vol < 0.34f ? Ink.IcoVol1 : isle.vol < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3;
                return Glyph(right, cy, H, "status", glyph, delegate { dock.OpenSheetFrom("quick", spots["status"]); });
            }

            // Up over down in small type, like a menu bar extra
            float DrawNet(float right, float cy, float H)
            {
                var f = Ink.SmallSemi(9.5f);
                string up = NetText(dock.NetUp), down = NetText(dock.NetDown);
                float tw = Math.Max(p.Measure(up, f), p.Measure(down, f)), w = tw + 26;
                var r = new RectangleF(right - w, 4, w, H - 8);
                Spot(r, "net", delegate { Open("ms-settings:network-status"); });
                float ax = r.X + 9;
                g.FillPolygon(p.B(p.T.Sub), new[] { new PointF(ax - 2.6f, cy - 3.2f), new PointF(ax + 2.6f, cy - 3.2f), new PointF(ax, cy - 7.2f) });
                g.FillPolygon(p.B(p.T.Text), new[] { new PointF(ax - 2.6f, cy + 3.2f), new PointF(ax + 2.6f, cy + 3.2f), new PointF(ax, cy + 7.2f) });
                p.TextRight(up, f, p.T.Sub, r.Right - 8, cy - 5.2f);
                p.TextRight(down, f, p.T.Text, r.Right - 8, cy + 5.2f);
                return r.X;
            }

            static string NetText(double perSec)
            {
                if (perSec < 1024) return "0 KB/s";
                if (perSec < 1048576) return (int)(perSec / 1024) + " KB/s";
                double mb = perSec / 1048576;
                return mb.ToString(mb >= 10 ? "0" : "0.0", Fmt.Id) + " MB/s";
            }

            // Drive glyph over one hairline per drive; free space in the tooltip
            float DrawDisk(float right, float cy, float H)
            {
                var disks = dock.Disks;
                var r = new RectangleF(right - 34, 4, 34, H - 8);
                Spot(r, "disk", delegate { Open("shell:MyComputerFolder"); });
                int n = disks.Length;
                p.Icon((char)0xEDA2, 12f, p.T.Text, r.X + 17, cy - 3.5f);
                float w = 16, x0 = r.X + 9, top = cy + 6 - (n - 1);
                for (int i = 0; i < n; i++)
                {
                    float used = (float)(disks[i].Total - disks[i].Free) / disks[i].Total, y = top + i * 2.2f;
                    g.FillRectangle(p.B(Ink.A(p.T.Text, 0.25f)), x0, y, w, 1.4f);
                    g.FillRectangle(p.B(used >= 0.9f ? p.T.Negative : p.T.Text), x0, y, Math.Max(1.4f, w * used), 1.4f);
                }
                return r.X;
            }

            string DiskTip()
            {
                var sb = new StringBuilder();
                foreach (var d in dock.Disks)
                {
                    sb.Append(sb.Length == 0 ? Lang.T("Ruang kosong") + ": " : "  ·  ");
                    double gb = d.Free / 1073741824.0;
                    sb.Append(d.Name).Append(' ').Append(gb.ToString(gb >= 10 ? "0" : "0.0", Fmt.Id)).Append(" GB");
                }
                return sb.ToString();
            }

            // Tray icons Windows would show, then the arrow for the rest
            float DrawTray(float right, float cy, float H)
            {
                float x = right;
                if (dock.TrayOverflow().Count > 0)
                {
                    var r = new RectangleF(x - 26, 4, 26, H - 8);
                    Spot(r, "chev", delegate { dock.TrayCardFrom(spots["chev"]); });
                    p.Icon((char)0xE70D, 10f, p.T.Text, r.X + 13, cy);
                    x = r.X;
                }
                var inline = dock.TrayInline();
                for (int i = inline.Count - 1; i >= 0; i--)
                {
                    var ic = inline[i];
                    var r = new RectangleF(x - 28, 4, 28, H - 8);
                    string id = "tray:" + ic.Key;
                    Spot(r, id, null);
                    ic.Spot = spots[id];
                    var bmp = dock.TrayPicture(ic);
                    if (bmp != null) g.DrawImage(bmp, new RectangleF(r.X + 6, cy - 8, 16, 16));
                    x = r.X;
                }
                return x;
            }

            // Menus named like a Mac's; every item does something real
            static readonly string[] MenuIds = { "m:file", "m:edit", "m:view", "m:window", "m:help" };

            // Menu bar names of their own: in English they read as a Mac's do
            static string MenuName(string id)
            {
                bool en = Lang.English;
                switch (id)
                {
                    case "m:file": return en ? "File" : "Berkas";
                    case "m:edit": return "Edit";
                    case "m:view": return en ? "View" : "Tampilan";
                    case "m:window": return en ? "Window" : "Jendela";
                    default: return en ? "Help" : "Bantuan";
                }
            }

            static readonly string[] Terminals = { "WindowsTerminal", "conhost", "cmd", "powershell", "pwsh", "OpenConsole", "alacritty", "wezterm-gui", "mintty" };

            void OpenMenu(string id)
            {
                IntPtr target = frontH;
                bool any = target != IntPtr.Zero && IsWindow(target);
                string exe = any ? System.IO.Path.GetFileNameWithoutExtension(ShellApp.ProcessPath(PidOf(target))) : "";
                bool term = Array.Exists(Terminals, t => string.Equals(t, exe, StringComparison.OrdinalIgnoreCase));
                var m = new ContextMenuStrip();
                Action<string, Action, string, bool> add = (text, act, keys, on) =>
                {
                    var it = new ToolStripMenuItem(Lang.T(text), null, delegate { act(); }) { ShortcutKeyDisplayString = keys, Enabled = on && any };
                    m.Items.Add(it);
                };
                switch (id)
                {
                    case "m:app":
                        add("Sembunyikan", delegate { ShellApp.Minimize(target); }, null, true);
                        add("Keluar", delegate { ShellApp.Close(target); }, "Alt+F4", true);
                        break;
                    case "m:file":
                        add("Baru", Press(target, 0x11, 0x4E), "Ctrl+N", true);
                        add("Buka…", Press(target, 0x11, 0x4F), "Ctrl+O", true);
                        m.Items.Add(new ToolStripSeparator());
                        add("Simpan", Press(target, 0x11, 0x53), "Ctrl+S", true);
                        add("Simpan sebagai…", Press(target, 0x11, 0x10, 0x53), "Ctrl+Shift+S", true);
                        add("Cetak…", Press(target, 0x11, 0x50), "Ctrl+P", true);
                        m.Items.Add(new ToolStripSeparator());
                        add("Tutup", Press(target, 0x11, 0x57), "Ctrl+W", true);
                        break;
                    case "m:edit":
                        add("Urungkan", Press(target, 0x11, 0x5A), "Ctrl+Z", !term);
                        add("Ulangi", Press(target, 0x11, 0x59), "Ctrl+Y", !term);
                        m.Items.Add(new ToolStripSeparator());
                        add("Potong", Press(target, 0x11, 0x58), "Ctrl+X", !term);
                        // Terminals: Ctrl+C would stop the running command
                        add("Salin", term ? Press(target, 0x11, 0x10, 0x43) : Press(target, 0x11, 0x43), term ? "Ctrl+Shift+C" : "Ctrl+C", true);
                        add("Tempel", term ? Press(target, 0x11, 0x10, 0x56) : Press(target, 0x11, 0x56), term ? "Ctrl+Shift+V" : "Ctrl+V", true);
                        add("Pilih semua", Press(target, 0x11, 0x41), "Ctrl+A", !term);
                        m.Items.Add(new ToolStripSeparator());
                        add("Cari…", Press(target, 0x11, 0x46), "Ctrl+F", true);
                        break;
                    case "m:view":
                        add("Perbesar", Press(target, 0x11, 0xBB), "Ctrl++", true);
                        add("Perkecil", Press(target, 0x11, 0xBD), "Ctrl+-", true);
                        add("Ukuran asli", Press(target, 0x11, 0x30), "Ctrl+0", true);
                        m.Items.Add(new ToolStripSeparator());
                        add("Layar penuh", Press(target, 0x7A), "F11", true);
                        break;
                    case "m:window":
                        add("Minimalkan", delegate { ShellApp.Minimize(target); }, null, true);
                        add(any && IsZoomed(target) ? "Pulihkan ukuran" : "Maksimalkan", delegate { ShowWindowAsync(target, IsZoomed(target) ? 9 : 3); }, null, true);
                        m.Items.Add(new ToolStripSeparator());
                        add("Rapatkan ke kiri", delegate { Half(target, true); }, null, true);
                        add("Rapatkan ke kanan", delegate { Half(target, false); }, null, true);
                        add("Pindah ke layar lain", delegate { NextScreen(target); }, null, Screen.AllScreens.Length > 1);
                        m.Items.Add(new ToolStripSeparator());
                        bool top = any && (Native.GetWindowLong(target, -20) & 8) != 0;
                        var pin = new ToolStripMenuItem(Lang.T("Selalu di atas"), null, delegate { Native.SetWindowPos(target, top ? new IntPtr(-2) : new IntPtr(-1), 0, 0, 0, 0, 0x13); }) { Checked = top, Enabled = any };
                        m.Items.Add(pin);
                        m.Items.Add(new ToolStripSeparator());
                        add("Tutup jendela", delegate { ShellApp.Close(target); }, null, true);
                        break;
                    default:
                        add("Bantuan aplikasi", Press(target, 0x70), "F1", true);
                        m.Items.Add(new ToolStripMenuItem(Lang.T("Panduan Tepi"), null, delegate { Open("https://github.com/odlaver/tepi/blob/main/docs/fitur.md"); }));
                        break;
                }
                isle.StyleMenu(m);
                menuId = id;
                Render();
                m.Closed += delegate { menuId = null; BeginInvoke(new Action(delegate { m.Dispose(); Render(); })); };
                var at = spots[id];
                m.Show(new Point(at.Left, at.Bottom + 2));
            }

            // The app in front gets the keys, after the menu is gone
            Action Press(IntPtr target, params ushort[] chord)
            {
                return delegate
                {
                    ShellApp.Activate(target);
                    var wait = new System.Windows.Forms.Timer { Interval = 80 };
                    wait.Tick += delegate
                    {
                        wait.Stop();
                        wait.Dispose();
                        var list = new List<INPUT>();
                        foreach (var k in chord) list.Add(Key(k, '\0', 0));
                        for (int i = chord.Length - 1; i >= 0; i--) list.Add(Key(chord[i], '\0', 2));
                        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
                    };
                    wait.Start();
                };
            }

            static int PidOf(IntPtr h)
            {
                int pid;
                GetWindowThreadProcessId(h, out pid);
                return pid;
            }

            // Half of the work area its window is on
            static void Half(IntPtr h, bool left)
            {
                var work = Screen.FromHandle(h).WorkingArea;
                ShowWindowAsync(h, 9);
                int w = work.Width / 2;
                Native.SetWindowPos(h, IntPtr.Zero, left ? work.Left : work.Left + w, work.Top, w, work.Height, 0x14);
            }

            // Same spot, next screen; maximized stays maximized
            static void NextScreen(IntPtr h)
            {
                var all = Screen.AllScreens;
                var here = Screen.FromHandle(h);
                int at = Array.IndexOf(all, Array.Find(all, s => s.DeviceName == here.DeviceName));
                var to = all[(at + 1) % all.Length];
                bool max = IsZoomed(h);
                if (max) ShowWindowAsync(h, 9);
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return;
                int dx = r.Left - here.WorkingArea.Left, dy = r.Top - here.WorkingArea.Top;
                int w = Math.Min(r.Right - r.Left, to.WorkingArea.Width), hh = Math.Min(r.Bottom - r.Top, to.WorkingArea.Height);
                Native.SetWindowPos(h, IntPtr.Zero, to.WorkingArea.Left + Math.Min(dx, to.WorkingArea.Width - w), to.WorkingArea.Top + Math.Min(dy, to.WorkingArea.Height - hh), w, hh, 0x14);
                if (max) ShowWindowAsync(h, 3);
            }

            static void Open(string what)
            {
                try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("bar open " + what + ": " + ex.Message); }
            }

            void ShowMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Pengaturan Tepi"), null, delegate { isle.OpenPrefs(); });
                m.Items.Add(Lang.T("Pengaturan Windows"), null, delegate { Open("ms-settings:"); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Kunci"), null, delegate { SessionPower.Lock(); });
                m.Items.Add(Lang.T("Tidur"), null, delegate { SessionPower.Sleep(); });
                m.Items.Add(Lang.T("Mulai ulang"), null, delegate { SessionPower.Restart(); });
                m.Items.Add(Lang.T("Matikan"), null, delegate { SessionPower.ShutDown(); });
                m.Items.Add(new ToolStripSeparator());
                var hide = new ToolStripMenuItem(Lang.T("Sembunyikan bar atas otomatis")) { Checked = AutoHide };
                hide.Click += delegate { isle.SetBarHide(!AutoHide); };
                m.Items.Add(hide);
                m.Items.Add(Lang.T("Kembali ke tampilan biasa"), null, delegate { isle.SetDesk(false); });
                isle.StyleMenu(m);
                menuId = "menu";
                m.Closed += delegate { menuId = null; BeginInvoke(new Action(delegate { m.Dispose(); Render(); })); };
                var at = spots["menu"];
                m.Show(new Point(at.Left, at.Bottom + 2));
            }

            string HitAt(Point pt)
            {
                var d = new PointF(pt.X / scale, pt.Y / scale);
                for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(d)) return hits[i].Value;
                return null;
            }

            TrayIcon TrayById(string id)
            {
                if (id == null || !id.StartsWith("tray:", StringComparison.Ordinal)) return null;
                return dock.TrayInline().Find(x => "tray:" + x.Key == id);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                string id = HitAt(e.Location);
                if (id == hotId) return;
                hotId = id;
                Cursor = id != null ? Cursors.Hand : Cursors.Default;
                tip.Hide(this);
                var ic = TrayById(id);
                string text = id == "disk" ? DiskTip() : ic != null ? ic.Tip : null;
                if (!string.IsNullOrEmpty(text)) tip.Show(text.Split('\n')[0], this, e.X, ch + 6, 4000);
                Render();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hotId = null;
                tip.Hide(this);
                Render();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                pressId = HitAt(e.Location);
                var ic = TrayById(pressId);
                if (ic != null) dock.TrayDown(ic, e.Button);
                Render();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                string id = HitAt(e.Location), was = pressId;
                pressId = null;
                var ic = TrayById(was);
                if (ic != null) { dock.TrayUp(ic, e.Button); Render(); return; }
                Render();
                Action act;
                if (id != null && id == was && e.Button == MouseButtons.Left && acts.TryGetValue(id, out act))
                {
                    try { act(); } catch (Exception ex) { Log.Write("bar " + id + ": " + ex.Message); }
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (hotId != "status") return;
                isle.volume.Set(Ease.Clamp01(isle.vol + 0.02f * e.Delta / 120f));
            }
        }
    }
}
