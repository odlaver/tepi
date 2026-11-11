using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Glass dock along the bottom edge; can stand in for the taskbar
        sealed partial class DockWindow : Form
        {
            readonly Island isle;
            readonly Painter p = new Painter();
            readonly AppWatch watch = new AppWatch();
            List<DockApp> apps = new List<DockApp>();
            readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            float scale = 1;
            IntPtr memDC, dib, oldBmp;
            Bitmap canvas;
            Graphics g;
            int cw, ch;
            // Monitor, and the strip the dock stands on, in px
            Rectangle mon, area;
            bool registered, shown = true, closing;
            const int AppBarMsg = Native.WM_APP + 0x32;
            static readonly int ShellMsg = Taskbar.RegisterWindowMessage("SHELLHOOK"), FrameMsg = Taskbar.RegisterWindowMessage("TepiDockFrame");
            readonly Timer tick = new Timer { Interval = 1000 }, dwell = new Timer { Interval = 420 }, hideLater = new Timer { Interval = 700 };
            readonly Stopwatch clock = Stopwatch.StartNew();
            double lastT;
            DockPacer pacer;
            DockFrost frost;
            DockPlate plate;
            TrayHost tray;
            List<TrayIcon> trayIcons = new List<TrayIcon>();
            int trayPosted;
            DockCard card;
            IntPtr front;
            string drawnSig = "";
            int tickN;
            // Dock slides in on open and for auto-hide
            readonly Spring reveal = new Spring(0, 1, 1, 0.001f), hover = new Spring(0, 1, 1, 0.002f);
            bool wantShown = true;

            [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
            [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);
            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("kernel32.dll")] static extern IntPtr VirtualQuery(IntPtr address, out MemInfo info, IntPtr size);

            [StructLayout(LayoutKind.Sequential)]
            struct MemInfo { public IntPtr Base, AllocBase; public uint AllocProtect; public IntPtr Size; public uint State, Protect, Type; }

            Settings S { get { return isle.settings; } }

            // Pilot runs: no taskbar or tray changes, drawn off screen
            public static readonly bool Dry = Environment.GetEnvironmentVariable("TEPI_DOCK_DRY") == "1";

            // Stands in for the Windows taskbar
            bool Replacing { get { return S.GetBool("dockbar.replace", false); } }
            bool Full { get { return S.Get("dockbar.style", "float") == "full"; } }

            string HideMode
            {
                get
                {
                    string m = S.Get("dockbar.hide", "never");
                    return m == "auto" || m == "smart" ? m : "never";
                }
            }

            public DockWindow(Island isle)
            {
                this.isle = isle;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                AllowDrop = true;
                Text = "Tepi Dock";
                Bounds = new Rectangle(-32000, -32000, 1, 1);
                Tune(reveal, 170, 0.86f);
                Tune(hover, 260, 0.9f);
                tick.Tick += delegate { Tick(); };
                dwell.Tick += delegate { dwell.Stop(); Dwell(); };
                hideLater.Tick += delegate { hideLater.Stop(); AutoHide(); };
                trayPop.Tick += delegate { TrayPopup(); };
                Tune(labelShow, 520, 0.9f);
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
                pacer = new DockPacer(Handle, FrameMsg);
                frost = new DockFrost(pl => { try { BeginInvoke(new Action(() => TakePlate(pl))); } catch (Exception) { pl.Dispose(); } });
                RegisterShellHookWindow(Handle);
                watch.Rescan();
                Changed();
                Refresh(true);
                tick.Start();
                // Entrance: the dock rises, then icons arrive one after another
                reveal.Snap(0);
                reveal.Target = 1;
                float delay = 0.12f;
                foreach (var a in apps) { a.Show.Snap(0); a.Show.Target = 1; a.Delay = delay; delay += 0.035f; }
                Animate();
            }

            bool allowClose;

            // Only the island closes the dock; outside WM_CLOSE is ignored
            public void Shutdown()
            {
                allowClose = true;
                Close();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
                base.OnFormClosing(e);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                closing = true;
                tick.Stop();
                dwell.Stop();
                hideLater.Stop();
                DeregisterShellHookWindow(Handle);
                if (pacer != null) pacer.Stop();
                if (frost != null) frost.Stop();
                CloseCard();
                if (sheet != null) SheetDone(sheet);
                StopKeys();
                StopTray();
                Reserve(false);
                if (!Dry) Taskbar.Restore(S);
                FreeCanvas();
                foreach (var b in icons.Values) if (b != null) b.Dispose();
                icons.Clear();
                FreeCaches();
                if (plate != null) plate.Dispose();
                if (shadowTile != null) shadowTile.Dispose();
                base.OnFormClosed(e);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
                if (m.Msg == FrameMsg) { OnFrame(); return; }
                if (m.Msg == ShellMsg && ShellMsg != 0) { OnShell(ref m); return; }
                // The Windows taskbar moved: claim the strip again
                if (m.Msg == AppBarMsg) { if (m.WParam.ToInt64() == 1) { Reserve(Reserving && !Dry); Place(); Render(); } return; }
                // Explorer restarted (not our own tray echo)
                if (m.Msg == Taskbar.Created && Taskbar.Created != 0 && (tray == null || !tray.JustBroadcast)) BeginInvoke(new Action(ExplorerBack));
                base.WndProc(ref m);
            }

            void ExplorerBack()
            {
                if (closing) return;
                if (Replacing) Taskbar.Hide(S, isle.monitorHandle);
                if (tray != null) tray.Resync();
                Changed();
            }

            bool Reserving { get { return HideMode == "never" && (Replacing || S.GetBool("dockbar.reserve", true)); } }

            // Glass reads the screen behind, so it cannot also appear in captures
            public bool Glass { get { return isle.GlassOn && !S.GetBool("dockbar.capture", false); } }

            // Settings, pins or monitor changed
            public void Changed()
            {
                if (!IsHandleCreated || closing) return;
                scale = isle.scale;
                mon = isle.monitorPx;
                Native.SetWindowDisplayAffinity(Handle, Glass ? Native.WDA_EXCLUDEFROMCAPTURE : 0);
                // Taskbar first: it changes the strip the dock gets
                if (Replacing && !Dry) Taskbar.Hide(S, isle.monitorHandle);
                else if (!Dry) Taskbar.Restore(S);
                Reserve(Reserving && !Dry);
                if (Replacing && !Dry && S.GetBool("dockbar.tray", true)) StartTray();
                else StopTray();
                // Yields bare Win automatically if an external Win-key launcher is active
                if (Replacing && !Dry && S.GetBool("dockbar.winkey", true)) StartKeys();
                else StopKeys();
                if (Glass) frost.Start(); else { frost.Stop(); if (plate != null) { plate.Dispose(); plate = null; } }
                foreach (var a in apps) TuneApp(a);
                Refresh(false);
                Place();
                drawnSig = "";
                Render();
            }

            // Strip along the bottom; reserved, maximized windows stop above it
            void Reserve(bool on)
            {
                int want = (int)Math.Ceiling((BarH + Edge) * scale);
                var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle, uCallbackMessage = AppBarMsg };
                if (!on)
                {
                    if (registered) { Native.SHAppBarMessage(1, ref abd); registered = false; }
                    var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
                    Native.GetMonitorInfo(isle.monitorHandle, ref mi);
                    var work = Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
                    // Auto-hide sits on the monitor edge, over everything
                    int bottom = HideMode == "never" ? work.Bottom : mon.Bottom;
                    area = new Rectangle(mon.Left, bottom - want, mon.Width, want);
                    return;
                }
                if (!registered) { Native.SHAppBarMessage(0, ref abd); registered = true; }
                abd.uEdge = 3;
                abd.rc = new Native.RECT { Left = mon.Left, Right = mon.Right, Top = mon.Bottom - want, Bottom = mon.Bottom };
                // Windows moves the strip above its own taskbar
                Native.SHAppBarMessage(2, ref abd);
                abd.rc.Top = abd.rc.Bottom - want;
                Native.SHAppBarMessage(3, ref abd);
                area = Rectangle.FromLTRB(abd.rc.Left, abd.rc.Top, abd.rc.Right, abd.rc.Bottom);
            }

            void StartTray()
            {
                if (tray != null) return;
                tray = new TrayHost();
                tray.Changed += delegate
                {
                    if (System.Threading.Interlocked.Exchange(ref trayPosted, 1) != 0) return;
                    try { BeginInvoke(new Action(TrayChanged)); } catch (Exception) { trayPosted = 0; }
                };
                tray.Start(TrayArea());
            }

            void StopTray()
            {
                if (tray == null) return;
                var t = tray;
                tray = null;
                t.Stop();
                trayIcons = new List<TrayIcon>();
            }

            void TrayChanged()
            {
                trayPosted = 0;
                if (tray == null || closing) return;
                trayIcons = tray.Snapshot();
                if (card != null && card.Kind == DockCard.TrayKind) card.Refill();
                // Hidden icons change often (sync spinners); redraw only for what shows
                string sig = TraySig();
                if (sig == traySig) return;
                traySig = sig;
                Render();
            }

            string traySig = "";

            string TraySig()
            {
                var sb = new StringBuilder();
                foreach (var ic in InlineTray()) sb.Append(ic.Key).Append(ic.Stamp).Append(',');
                sb.Append(OverflowTray().Count > 0);
                return sb.ToString();
            }

            public List<TrayIcon> TrayOverflow() { return OverflowTray(); }

            // Windows came, went, flashed or took focus
            void OnShell(ref Message m)
            {
                int code = (int)(m.WParam.ToInt64() & 0xFFFF);
                IntPtr h = m.LParam;
                bool changed = false;
                switch (code)
                {
                    case 1: changed = watch.Track(h); break;
                    case 2: changed = watch.Drop(h); break;
                    case 4:
                    case 0x8004:
                        changed = watch.Track(h);
                        watch.Activated(h);
                        front = h;
                        changed = true;
                        if (HideMode == "auto" && IsStartOrSearch(h)) Reveal(true);
                        // An app took over from the desktop: hide again
                        else if (HideMode == "auto" && !inside && wantShown && sheet == null && !keyMode) { hideLater.Stop(); hideLater.Start(); }
                        break;
                    case 6:
                        watch.Track(h);
                        if (card != null && card.Kind == DockCard.WindowsKind) card.Refill();
                        break;
                    case 0x8006: changed = watch.Flashed(h); break;
                    case 5:
                        if (MinRect(m.LParam)) { m.Result = (IntPtr)1; return; }
                        break;
                    case 13: watch.Drop(h); changed = watch.Rescan() || true; break;
                }
                if (changed) Refresh(false);
                m.Result = IntPtr.Zero;
            }

            // Minimize flies into the app's icon
            bool MinRect(IntPtr info)
            {
                MemInfo mi;
                if (info == IntPtr.Zero || VirtualQuery(info, out mi, (IntPtr)Marshal.SizeOf(typeof(MemInfo))) == IntPtr.Zero) return false;
                // Must be ours and writable
                if (mi.State != 0x1000 || (mi.Protect & 0x04) == 0) return false;
                IntPtr h = Marshal.ReadIntPtr(info);
                var app = apps.Find(a => a.Wins.Exists(w => w.H == h));
                if (app == null || !Visible) return false;
                var r = IconScreenRect(app);
                if (r.IsEmpty) return false;
                int off = IntPtr.Size;
                Marshal.WriteInt32(info, off, r.Left);
                Marshal.WriteInt32(info, off + 4, r.Top);
                Marshal.WriteInt32(info, off + 8, r.Right);
                Marshal.WriteInt32(info, off + 12, r.Bottom);
                return true;
            }

            static bool IsStartOrSearch(IntPtr h)
            {
                var sb = new StringBuilder(64);
                Native.GetClassName(h, sb, 64);
                return sb.ToString() == "Windows.UI.Core.CoreWindow";
            }

            // Pins and windows into the dock's list; leavers shrink away
            public void Refresh(bool first)
            {
                var keep = new Dictionary<string, DockApp>();
                foreach (var a in apps) if (!a.Leaving) keep[a.Key] = a;
                var next = watch.Arrange(isle.DockPins(), keep);
                var result = new List<DockApp>(next);
                var inNext = new HashSet<DockApp>(next);
                for (int i = 0; i < apps.Count; i++)
                {
                    var a = apps[i];
                    if (inNext.Contains(a)) continue;
                    if (!a.Leaving) { a.Leaving = true; a.Show.Target = 0; CloseCardFor(a); }
                    if (a.Show.Value < 0.02f) continue;
                    int at = 0;
                    for (int k = i - 1; k >= 0; k--)
                    {
                        int idx = result.IndexOf(apps[k]);
                        if (idx >= 0) { at = idx + 1; break; }
                    }
                    result.Insert(at, a);
                }
                foreach (var a in next)
                {
                    if (!a.Placed)
                    {
                        a.Placed = true;
                        TuneApp(a);
                        a.Show.Snap(first ? 1 : 0);
                        a.Grow.Snap(1);
                        a.Dot.Snap(0);
                    }
                    a.Show.Target = 1;
                    LoadIcon(a);
                    // Wants attention: a few hops, then the accent stays
                    if (a.Attention && a.Hops == 0 && a.Hop < 0) { a.Hop = 0; a.Hops = 3; }
                }
                apps = result;
                ApplyBadges();
                // Focus moving between windows the dock does not show changes nothing here
                string look = LookSig();
                if (look != lookSig || Moving()) { lookSig = look; Animate(); }
            }

            string lookSig = "";

            // What the centre shows: apps, their windows, focus, badges, calls for attention
            string LookSig()
            {
                var sb = new StringBuilder();
                IntPtr fg = GetForegroundWindow();
                foreach (var a in apps)
                    sb.Append(a.Key).Append(a.Pinned ? 'p' : 'r').Append(a.Wins.Count).Append(a.Attention ? '!' : '.').Append(a.Badge).Append(a.Leaving ? 'x' : 'o').Append(a.Wins.Exists(w => w.H == fg) ? 'f' : '-').Append('|');
                return sb.ToString();
            }

            bool Moving()
            {
                if (!reveal.Settled || !hover.Settled || WidgetsMoving()) return true;
                foreach (var a in apps) if (!a.Show.Settled || !a.Grow.Settled || !a.Dot.Settled || a.Hop >= 0) return true;
                return false;
            }

            // Key of the app whose window has focus
            string FocusKey()
            {
                IntPtr fg = GetForegroundWindow();
                var app = apps.Find(a => a.Wins.Exists(w => w.H == fg));
                return app != null ? app.Key : "";
            }

            void TuneApp(DockApp a)
            {
                Tune(a.Grow, 900, 0.82f);
                Tune(a.Show, 260, 0.8f);
                Tune(a.Dot, 340, 0.78f);
                Tune(a.X, 300, 0.82f);
            }

            void ApplyBadges()
            {
                var counts = S.GetBool("dockbar.badges", true) ? isle.DockBadgeCounts() : null;
                foreach (var a in apps)
                {
                    int n = 0;
                    if (counts != null && a.AppId.Length > 0) counts.TryGetValue(a.AppId, out n);
                    if (counts != null && n == 0 && a.Name.Length > 0) counts.TryGetValue("name:" + a.Name, out n);
                    a.Badge = n;
                }
            }

            // Icon at the biggest size it will be drawn
            void LoadIcon(DockApp a)
            {
                if (icons.ContainsKey(a.Key)) return;
                int px = IconSourcePx();
                Bitmap bmp = null;
                string item = a.Pinned ? a.Target : a.Packaged && a.AppId.Length > 0 ? ShellApp.AppsFolderOf(a.AppId) : a.Exe;
                if (item.Length > 0) bmp = ShellApp.Icon(item, px);
                if (bmp == null && a.Exe.Length > 0 && !a.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) bmp = ShellApp.Icon(a.Exe, px);
                if (bmp == null && a.Wins.Count > 0) bmp = WindowIcon(a.Wins[0].H);
                icons[a.Key] = bmp;
            }

            [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int ms, out IntPtr result);
            [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr h, int index);

            static Bitmap WindowIcon(IntPtr h)
            {
                IntPtr r;
                SendMessageTimeout(h, 0x7F, (IntPtr)1, IntPtr.Zero, 2, 60, out r);
                if (r == IntPtr.Zero) r = GetClassLongPtr(h, -14);
                if (r == IntPtr.Zero) return null;
                try { using (var ic = Icon.FromHandle(r)) return ic.ToBitmap(); }
                catch (Exception) { return null; }
            }

            int IconSourcePx()
            {
                float px = IconRest * Math.Max(1, Mag) * scale;
                foreach (int s in new[] { 48, 64, 96, 128, 256 }) if (px <= s) return s;
                return 256;
            }

            void TakePlate(DockPlate pl)
            {
                if (closing || !Glass) { pl.Dispose(); return; }
                var old = plate;
                plate = pl;
                if (old != null) old.Dispose();
                if (card != null) card.Invalidate();
                Render();
            }

            // Windows moved behind: the glass looks again
            public void Nudge()
            {
                if (frost != null) frost.Nudge();
                if (HideMode == "smart") SmartHide();
                // Focus moved elsewhere: the tray grid has done its job
                if (card != null && card.Kind == DockCard.TrayKind && !card.Bounds.Contains(Cursor.Position)) CloseCard();
            }

            void Tick()
            {
                if (closing) return;
                tickN++;
                // Fullscreen or locked: out of the way
                bool want = !isle.fullscreen && !isle.dormant && !isle.hiddenByUser;
                if (want != shown)
                {
                    shown = want;
                    Native.ShowWindow(Handle, want ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
                    if (want) { drawnSig = ""; Render(); }
                    else CloseCard();
                }
                if (!shown) return;
                if (HideMode == "auto" && StayOnDesktop && !wantShown && DesktopFront()) Reveal(true);
                if (Replacing && !Dry && Taskbar.Showing()) Taskbar.Rehide();
                if (tickN % 3 == 0) ReadSystem();
                // Shell hooks cover most changes; this catches the rest
                if (tickN % 10 == 0 && watch.Rescan()) Refresh(false);
                if (tickN % 5 == 0) ApplyBadges();
                if (Signature() != drawnSig) Render();
            }

            // Whatever a still frame shows, as text
            string Signature()
            {
                var sb = new StringBuilder();
                sb.Append(Fmt.Clock(DateTime.Now)).Append('|').Append(hoverId).Append('|').Append(pressId).Append('|').Append(FocusKey());
                foreach (var a in apps) sb.Append(a.Key.Length).Append(a.Wins.Count).Append(a.Badge).Append(a.Attention ? '!' : '.');
                var mi = isle.mediaInfo;
                if (mi.Present) sb.Append(mi.Title).Append(mi.Playing);
                if (isle.TimerOn) sb.Append(isle.TimerClock());
                sb.Append(isle.battery).Append(isle.onAc).Append(cpuPct).Append(ramPct).Append(isle.BusyCount()).Append(isle.quotaVersion);
                sb.Append(isle.vol).Append(isle.muted).Append(isle.unread).Append(Ink.Epoch).Append(Lang.English);
                if (isle.weather != null) sb.Append(isle.weather.Temp);
                return sb.ToString();
            }

            // Frames run only while something moves
            void Animate()
            {
                if (closing || pacer == null) return;
                if (pacer.Want()) lastT = clock.Elapsed.TotalSeconds - 1 / 60.0;
            }

            void OnFrame()
            {
                if (closing) return;
                double t = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(0.05, Math.Max(0.001, t - lastT));
                lastT = t;
                bool more = false;
                // A failed frame must not leave the pacer waiting forever
                try
                {
                    more = Step(dt);
                    Render();
                    // A widget set off during this render
                    more |= WidgetsMoving();
                }
                finally { pacer.Done(more); }
            }

            // Auto-hide: the edge brings it back
            void Reveal(bool on)
            {
                if (on) hideLater.Stop();
                wantShown = on;
                reveal.Target = on ? 1 : 0;
                Animate();
            }

            void AutoHide()
            {
                if (HideMode == "never" || inside || card != null || sheet != null || keyMode || dragging) return;
                if (HideMode == "auto" && StayOnDesktop && DesktopFront()) return;
                if (HideMode == "smart" && !Covered()) return;
                Reveal(false);
            }

            void SmartHide()
            {
                if (inside || card != null) return;
                bool covered = Covered();
                if (covered && wantShown) { hideLater.Stop(); hideLater.Start(); }
                else if (!covered && !wantShown) Reveal(true);
            }

            // A window sits where the dock is
            bool Covered()
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Handle || watch.Find(fg) == null) return false;
                Native.RECT r;
                if (!Native.GetWindowRect(fg, out r)) return false;
                var bar = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                return bar.IntersectsWith(new Rectangle(mon.Left, area.Bottom - (int)(BarH * scale), mon.Width, (int)(BarH * scale)));
            }

            // CPU from system times; RAM load
            [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
            [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

            [StructLayout(LayoutKind.Sequential)]
            struct MemStatus { public uint Length, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }

            long idle0, kern0, user0;
            int cpuPct = -1, ramPct = -1;

            void ReadSystem()
            {
                if (!isle.DockWidget("system")) { cpuPct = -1; return; }
                long idle, kern, user;
                if (GetSystemTimes(out idle, out kern, out user))
                {
                    long di = idle - idle0, total = (kern - kern0) + (user - user0);
                    if (idle0 != 0 && total > 0) cpuPct = (int)Math.Round(100.0 * (total - di) / total);
                    idle0 = idle; kern0 = kern; user0 = user;
                    if (cpuPct < 0) cpuPct = 0;
                }
                var m = new MemStatus { Length = (uint)Marshal.SizeOf(typeof(MemStatus)) };
                if (GlobalMemoryStatusEx(ref m)) ramPct = (int)m.Load;
            }
        }
    }
}
