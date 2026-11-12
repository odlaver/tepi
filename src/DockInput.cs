using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        sealed partial class DockWindow
        {
            string hoverId, pressId;
            Point downAt;
            // Reorder: the dragged app, its grab point, and the gaps it opens
            DockApp dragApp;
            PointF dragGrab;
            bool dragOut;
            int dragTo = -1;
            sealed class Gap { public int At; public readonly Spring Show = new Spring(0, 1, 1, 0.002f); }
            readonly List<Gap> gaps = new List<Gap>();
            // Tray pointer state
            TrayIcon trayHot;
            bool trayPopup;
            string lastTrayKey = "";
            DateTime lastTrayUtc = DateTime.MinValue;
            readonly Timer trayPop = new Timer { Interval = 500 };
            // File drag over an app brings its window up
            string dropHover;
            DateTime dropSinceUtc;

            bool dragging { get { return dragApp != null; } }
            bool PreviewsOn { get { return S.GetBool("dockbar.previews", true); } }

            string HitAt(PointF pt)
            {
                for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(pt)) return hits[i].Value;
                return null;
            }

            readonly DockApp workSlotApp = new DockApp { Key = "sys:work", Pinned = true };

            DockApp AppById(string id)
            {
                if (id == null || !id.StartsWith("app:", StringComparison.Ordinal)) return null;
                string key = id.Substring(4);
                return apps.Find(a => a.Key == key && !a.Leaving);
            }

            DockApp PreviewAppById(string id)
            {
                if (id == "sys:work")
                {
                    if (!S.GetBool("dockbar.work", true) || !isle.WorkOpen || isle.WorkHwnd == IntPtr.Zero) return null;
                    workSlotApp.Name = Lang.T("Tugas dan catatan");
                    workSlotApp.Wins.Clear();
                    var w = watch.Find(isle.WorkHwnd) ?? new DockWin { H = isle.WorkHwnd, Title = workSlotApp.Name };
                    workSlotApp.Wins.Add(w);
                    return workSlotApp;
                }
                return AppById(id);
            }

            TrayIcon TrayById(string id)
            {
                if (id == null || !id.StartsWith("tray:", StringComparison.Ordinal)) return null;
                return trayIcons.Find(x => "tray:" + x.Key == id);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var pt = new PointF(e.X / scale, e.Y / scale);
                mouseX = pt.X;
                mouseY = pt.Y;
                if (!inside)
                {
                    inside = true;
                    hover.Target = 1;
                    hideLater.Stop();
                    if (!wantShown) Reveal(true);
                }
                if (pressId != null && e.Button == MouseButtons.Left && dragApp == null && AppById(pressId) != null && Far(e.Location, downAt)) BeginDrag(AppById(pressId), pt);
                if (dragApp != null) { DragMove(pt); Animate(); return; }
                string id = HitAt(pt);
                if (id != hoverId) SetHover(id);
                TrayMove(id);
                Animate();
            }

            bool Far(Point a, Point b) { return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) > 7 * scale; }

            void SetHover(string id)
            {
                hoverId = id;
                bool label = id != null && (id.StartsWith("app:", StringComparison.Ordinal) || id.StartsWith("sys:", StringComparison.Ordinal) || id.StartsWith("tray:", StringComparison.Ordinal));
                var app = PreviewAppById(id);
                bool previewing = card != null && card.Kind == DockCard.WindowsKind && app != null && card.App == app;
                labelShow.Target = label && !previewing ? 1 : 0;
                dwell.Stop();
                if (app != null && app.Running && PreviewsOn)
                {
                    // Once one preview is up, the next app shows at once
                    if (card != null && card.Kind == DockCard.WindowsKind) ShowPreview(app);
                    else dwell.Start();
                }
                else if (card != null && card.Kind == DockCard.WindowsKind) card.CloseSoon();
            }

            void Dwell()
            {
                var app = PreviewAppById(hoverId);
                if (app != null && app.Running && inside && dragApp == null) ShowPreview(app);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (dragApp != null) return;
                inside = false;
                hover.Target = 0;
                pressId = null;
                dwell.Stop();
                SetHover(null);
                TrayLeave();
                if (card != null) card.CloseSoon();
                if (HideMode != "never") { hideLater.Stop(); hideLater.Start(); }
                Animate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                string id = HitAt(new PointF(e.X / scale, e.Y / scale));
                pressId = id;
                downAt = e.Location;
                dwell.Stop();
                if (id != null && id.StartsWith("tray:", StringComparison.Ordinal)) TrayDown(id, e.Button);
                Render();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                var pt = new PointF(e.X / scale, e.Y / scale);
                if (dragApp != null) { EndDrag(); pressId = null; Animate(); return; }
                string id = HitAt(pt);
                Action a;
                if (id != null && id.StartsWith("tray:", StringComparison.Ordinal)) TrayUp(id, e.Button);
                else if (e.Button == MouseButtons.Right && id != null && menus.TryGetValue(id, out a)) Run(a, id);
                else if (e.Button == MouseButtons.Right) DockMenu();
                else if (e.Button == MouseButtons.Left && id != null && id == pressId && acts.TryGetValue(id, out a)) Run(a, id);
                else if (e.Button == MouseButtons.Middle && id != null && id == pressId)
                {
                    var app = AppById(id);
                    if (app != null) Launch(app, false);
                }
                pressId = null;
                Render();
            }

            static void Run(Action a, string id)
            {
                try { a(); } catch (Exception ex) { Log.Write("dock " + id + ": " + ex.Message); }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (hoverId != "status") return;
                float step = e.Delta > 0 ? 0.02f : -0.02f;
                isle.volume.Set(Ease.Clamp01(isle.vol + step));
            }

            // Click always brings the app forward, never minimizes it;
            // a second click on the front app moves to its next window
            void OpenApp(DockApp a, bool fromCard)
            {
                bool shift = (ModifierKeys & Keys.Shift) != 0, ctrl = (ModifierKeys & Keys.Control) != 0;
                if (!a.Running || shift) { Launch(a, shift && ctrl); return; }
                IntPtr fg = GetForegroundWindow();
                int at = a.Wins.FindIndex(w => w.H == fg);
                var next = at >= 0 && a.Wins.Count > 1 ? a.Wins[(at + 1) % a.Wins.Count] : at >= 0 ? a.Wins[at] : a.Recent();
                if (next != null) ShellApp.Activate(next.H);
                if (!fromCard) CloseCard();
            }

            void Launch(DockApp a, bool admin)
            {
                string target = a.Target.Length > 0 ? a.Target : a.Exe;
                if (target.Length == 0) return;
                ShellApp.Launch(target, admin);
                a.LaunchUtc = DateTime.UtcNow;
                if (a.Hop < 0) { a.Hop = 0; a.Hops = 1; }
                Animate();
            }

            void WorkMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(new ToolStripMenuItem(Lang.T("Tugas dan catatan")) { Enabled = false });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Tugas"), null, delegate { isle.OpenWork(0); });
                m.Items.Add(Lang.T("Catatan"), null, delegate { isle.OpenWork(1); });
                m.Items.Add(Lang.T("Lepas dari Dock"), null, delegate { isle.SetFlag("dockbar.work", false); if (isle.prefs != null && !isle.prefs.IsDisposed) isle.prefs.Relayout(); });
                if (isle.WorkOpen && isle.WorkHwnd != IntPtr.Zero)
                {
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(Lang.T("Tutup jendela"), null, delegate { ShellApp.Close(isle.WorkHwnd); });
                }
                ShowMenu(m);
            }

            void AppMenu(DockApp a)
            {
                var m = new ContextMenuStrip();
                m.Items.Add(new ToolStripMenuItem(a.Name) { Enabled = false });
                if (a.Wins.Count > 1)
                {
                    m.Items.Add(new ToolStripSeparator());
                    foreach (var w in a.Wins)
                    {
                        var h = w.H;
                        string title = watch.Title(h);
                        m.Items.Add(title.Length > 60 ? title.Substring(0, 57) + "…" : title, null, delegate { ShellApp.Activate(h); });
                    }
                }
                m.Items.Add(new ToolStripSeparator());
                string target = a.Target.Length > 0 ? a.Target : a.Exe;
                if (target.Length > 0)
                {
                    m.Items.Add(a.Running ? Lang.T("Jendela baru") : Lang.T("Buka"), null, delegate { Launch(a, false); });
                    if (!a.Packaged && !ShellApp.IsAppsFolder(target)) m.Items.Add(Lang.T("Jalankan sebagai administrator"), null, delegate { Launch(a, true); });
                }
                if (a.Pinned) m.Items.Add(Lang.T("Lepas dari Dock"), null, delegate { Unpin(a); });
                else if (target.Length > 0) m.Items.Add(Lang.T("Sematkan di Dock"), null, delegate { Pin(a, -1); });
                string file = a.Exe.Length > 0 ? a.Exe : File.Exists(a.Target) ? a.Target : "";
                if (file.Length > 0 && !a.Packaged) m.Items.Add(Lang.T("Buka lokasi berkas"), null, delegate { ShowInFolder(file); });
                if (a.Running)
                {
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(a.Wins.Count > 1 ? Lang.F("Tutup {0} jendela", a.Wins.Count) : Lang.T("Tutup jendela"), null, delegate
                    {
                        foreach (var w in a.Wins) ShellApp.Close(w.H);
                    });
                }
                ShowMenu(m);
            }

            static void ShowInFolder(string file)
            {
                try { Process.Start("explorer.exe", "/select,\"" + file + "\""); }
                catch (Exception ex) { Log.Write("dock reveal: " + ex.Message); }
            }

            void ClockMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Notifikasi dan kalender"), null, delegate { ToggleSheet("cal"); });
                m.Items.Add(Lang.T("Atur tanggal dan waktu"), null, delegate { Open("ms-settings:dateandtime"); });
                ShowMenu(m);
            }

            void DockMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Pengaturan Dock"), null, delegate { isle.OpenPrefs("dock"); });
                m.Items.Add(Lang.T("Task Manager"), null, delegate { Open("taskmgr.exe"); });
                if (Replacing) m.Items.Add(Lang.T("Ikon di pojok taskbar"), null, delegate { Open("ms-settings:taskbar"); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Tampilkan desktop"), null, delegate { ShowDesktop(); });
                ShowMenu(m);
            }

            static void Open(string what)
            {
                try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("dock open " + what + ": " + ex.Message); }
            }

            void ShowMenu(ContextMenuStrip m)
            {
                CloseCard();
                labelShow.Target = 0;
                MenuLook.Apply(m);
                m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
                MenuGuard.Watch(m);
                m.Show(Cursor.Position);
            }

            void Pin(DockApp a, int at)
            {
                string target = a.Target.Length > 0 ? a.Target : a.Exe;
                if (target.Length == 0) return;
                var pins = isle.DockPins();
                pins.RemoveAll(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
                if (at < 0 || at > pins.Count) pins.Add(target);
                else pins.Insert(at, target);
                isle.SetDockPins(pins);
            }

            void Unpin(DockApp a)
            {
                var pins = isle.DockPins();
                pins.RemoveAll(x => string.Equals(x, a.Target, StringComparison.OrdinalIgnoreCase));
                isle.SetDockPins(pins);
            }

            void ShowPreview(DockApp app)
            {
                if (closing || !shown || dragApp != null) return;
                var at = IconScreenRect(app);
                if (at.IsEmpty) return;
                if (card != null && card.Kind != DockCard.WindowsKind) CloseCard();
                if (card == null) card = new DockCard(isle, this, DockCard.WindowsKind);
                labelShow.Target = 0;
                card.ShowApp(app, at, mon, BarTopScreen(), scale);
            }

            void ToggleTrayCard()
            {
                bool wasTray = card != null && card.Kind == DockCard.TrayKind;
                CloseCard();
                if (wasTray) return;
                TrayPromo.Invalidate();
                card = new DockCard(isle, this, DockCard.TrayKind);
                card.ShowTray(OverflowTray(), HitScreenRect("chev"), mon, BarTopScreen(), scale);
                Render();
            }

            public void CardDone(DockCard c)
            {
                if (c == card) CloseCard();
                else if (!c.IsDisposed) c.Dismiss();
            }

            // Pointer on a card keeps an auto-hidden dock up
            public void HoldOpen(bool on)
            {
                if (on) hideLater.Stop();
                else if (HideMode != "never" && !inside) { hideLater.Stop(); hideLater.Start(); }
            }

            public Bitmap TrayPicture(TrayIcon ic) { return tray != null ? tray.Picture(ic) : null; }

            GlassSheet sheet;

            // One card; its button toggles it
            void ToggleSheet(string kind)
            {
                if (sheet != null)
                {
                    bool same = sheet.Kind == kind;
                    SheetDone(sheet);
                    if (same) return;
                }
                CloseCard();
                labelShow.Target = 0;
                // Auto-hidden: the dock comes up under its card
                Reveal(true);
                sheet = MakeSheet(kind);
                sheet.OpenAt(HitScreenRect(SheetAnchor(kind)), Full ? Rectangle.Empty : ToScreen(centerPill), mon, BarTopShownScreen(), scale);
            }

            GlassSheet MakeSheet(string kind)
            {
                switch (kind)
                {
                    case "quick": return new QuickSheet(isle, this);
                    case "cal": return new CalendarSheet(isle, this);
                    case "weather": return new WeatherSheet(isle, this);
                    default: return new StartCard(isle, this);
                }
            }

            static string SheetAnchor(string kind)
            {
                switch (kind)
                {
                    case "quick": return "status";
                    case "cal": return "clock";
                    case "weather": return "w_weather";
                    default: return "sys:start";
                }
            }

            public void SheetDone(GlassSheet s)
            {
                if (s == sheet) sheet = null;
                if (!s.IsDisposed) s.Dismiss();
                if (sheet == null && !closing && HideMode != "never" && !inside) { hideLater.Stop(); hideLater.Start(); }
            }

            StartKey startKey;

            void StartKeys()
            {
                if (startKey != null) return;
                startKey = new StartKey();
                startKey.Pressed += what =>
                {
                    try { BeginInvoke(new Action(delegate { KeyAction(what); })); } catch (Exception) { }
                };
                startKey.Start();
            }

            void StopKeys()
            {
                if (startKey == null) return;
                startKey.Stop();
                startKey = null;
            }

            // Key from the hook to a card
            void KeyAction(string what)
            {
                if (closing) return;
                if (what == "menu") { QuickMenuAtStart(); return; }
                if (what == "dockkeys") { KeyNav(); return; }
                if (what == "search")
                {
                    if (sheet is StartCard) { ShellApp.Activate(sheet.Handle); return; }
                    if (sheet != null) SheetDone(sheet);
                    what = "start";
                }
                ToggleSheet(what);
            }

            // Win+T: the dock by keyboard, like the taskbar
            bool keyMode;
            string keySel;
            IntPtr keyPrev;

            [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);

            void KeyNav()
            {
                if (keyMode) { ExitKeys(true); return; }
                var ids = NavIds();
                if (ids.Count == 0) return;
                CloseCard();
                if (sheet != null) SheetDone(sheet);
                Reveal(true);
                keyPrev = GetForegroundWindow();
                // Starts on the first app, as the taskbar does
                keySel = ids.Find(x => !x.StartsWith("sys:", StringComparison.Ordinal)) ?? ids[0];
                keyMode = true;
                SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) & ~Native.WS_EX_NOACTIVATE);
                ShellApp.Activate(Handle);
                labelShow.Target = 1;
                Animate();
            }

            void ExitKeys(bool restore)
            {
                if (!keyMode) return;
                keyMode = false;
                keySel = null;
                labelShow.Target = 0;
                SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | Native.WS_EX_NOACTIVATE);
                if (restore && keyPrev != IntPtr.Zero) ShellApp.Activate(keyPrev);
                if (HideMode != "never" && !inside) { hideLater.Stop(); hideLater.Start(); }
                Animate();
            }

            List<string> NavIds()
            {
                var ids = new List<string>();
                foreach (var s in slots)
                {
                    if (s.Sep || s.Id == "gap" || s.Show < 0.5f) continue;
                    ids.Add(s.App == null ? "sys:" + s.Id : s.Id);
                }
                return ids;
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys key)
            {
                if (!keyMode) return base.ProcessCmdKey(ref msg, key);
                var ids = NavIds();
                int at = Math.Max(0, ids.IndexOf(keySel));
                Action a;
                switch (key)
                {
                    case Keys.Left:
                    case Keys.Up:
                    case Keys.Shift | Keys.Tab:
                        at = Math.Max(0, at - 1);
                        break;
                    case Keys.Right:
                    case Keys.Down:
                    case Keys.Tab:
                        at = Math.Min(ids.Count - 1, at + 1);
                        break;
                    case Keys.Home: at = 0; break;
                    case Keys.End: at = ids.Count - 1; break;
                    case Keys.Enter:
                    case Keys.Space:
                        string id = keySel;
                        ExitKeys(false);
                        if (id != null && acts.TryGetValue(id, out a)) Run(a, id);
                        return true;
                    case Keys.Apps:
                    case Keys.Shift | Keys.F10:
                        if (keySel != null && menus.TryGetValue(keySel, out a)) Run(a, keySel);
                        return true;
                    case Keys.Escape:
                        ExitKeys(true);
                        return true;
                    default:
                        return true;
                }
                if (ids.Count > 0) keySel = ids[at];
                labelShow.Target = 1;
                Animate();
                return true;
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                if (keyMode) ExitKeys(false);
            }

            // Empty desktop in front: nothing to make room for
            bool StayOnDesktop { get { return S.GetBool("dockbar.desktop", true); } }

            bool DesktopFront()
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                if (startKey != null && fg == startKey.KeeperHandle) return true;
                var c = new System.Text.StringBuilder(32);
                GetClassName(fg, c, 32);
                string cls = c.ToString();
                return cls == "Progman" || cls == "WorkerW";
            }

            public bool IsPinned(string target) { return isle.DockPins().Exists(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase)); }

            public void SetPinned(string target, bool on)
            {
                var pins = isle.DockPins();
                pins.RemoveAll(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
                if (on) pins.Add(target);
                isle.SetDockPins(pins);
            }

            // Right-click on Start: the places and power actions people reach for
            void QuickMenu() { ShowMenu(QuickItems()); }

            // Win+X: same menu, above Start
            void QuickMenuAtStart()
            {
                var m = QuickItems();
                CloseCard();
                if (sheet != null) SheetDone(sheet);
                MenuLook.Apply(m);
                m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
                var at = HitScreenRect("sys:start");
                MenuGuard.Watch(m);
                m.Show(new Point(at.X, at.Top - (int)(6 * scale)), ToolStripDropDownDirection.AboveRight);
            }

            ContextMenuStrip QuickItems()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Task Manager"), null, delegate { Open("taskmgr.exe"); });
                m.Items.Add(Lang.T("Pengaturan Windows"), null, delegate { Open("ms-settings:"); });
                m.Items.Add(Lang.T("File Explorer"), null, delegate { Open("explorer.exe"); });
                m.Items.Add(Lang.T("Terminal"), null, delegate { Open(File.Exists(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe")) ? "wt.exe" : "powershell.exe"); });
                m.Items.Add(Lang.T("Pengelola Perangkat"), null, delegate { Open("devmgmt.msc"); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Kunci"), null, delegate { SessionPower.Lock(); });
                m.Items.Add(Lang.T("Tidur"), null, delegate { SessionPower.Sleep(); });
                m.Items.Add(Lang.T("Keluar"), null, delegate { SessionPower.SignOut(); });
                m.Items.Add(Lang.T("Mulai ulang"), null, delegate { SessionPower.Restart(); });
                m.Items.Add(Lang.T("Matikan"), null, delegate { SessionPower.ShutDown(); });
                return m;
            }

            public Bitmap IconOf(DockApp a)
            {
                Bitmap b;
                return a != null && icons.TryGetValue(a.Key, out b) ? b : null;
            }

            void CloseCard()
            {
                if (card == null) return;
                var c = card;
                card = null;
                if (!c.IsDisposed) c.Dismiss();
                if (!closing) Render();
            }

            void CloseCardFor(DockApp a)
            {
                if (card != null && card.Covers(a)) CloseCard();
            }

            int BarTopScreen() { return WindowOrigin().Y + (int)Math.Round(barTop * scale); }

            // Where the bar sits when shown, even mid-slide
            int BarTopShownScreen() { return WindowOrigin().Y + (int)Math.Round((ch / scale - Edge - BarH) * scale); }

            Rectangle HitScreenRect(string id)
            {
                foreach (var kv in hits) if (kv.Value == id) return ToScreen(kv.Key);
                return ToScreen(rightPill);
            }

            // Drag to reorder; drag up and off the dock to unpin
            void BeginDrag(DockApp a, PointF pt)
            {
                var s = slots.Find(x => x.App == a);
                if (s == null) return;
                dragApp = a;
                dragGrab = new PointF(pt.X - (s.Icon.X + s.Icon.Width / 2), pt.Y - (s.Icon.Y + s.Icon.Height / 2));
                dragOut = false;
                int index = apps.FindIndex(x => x == a);
                dragTo = index;
                gaps.Clear();
                var gap = new Gap { At = index };
                Tune(gap.Show, 300, 0.85f);
                gap.Show.Snap(1);
                gaps.Add(gap);
                CloseCard();
                labelShow.Target = 0;
                labelShow.Snap(0);
            }

            void DragMove(PointF pt)
            {
                dragOut = pt.Y < barTop - 64;
                int to = -1;
                if (!dragOut)
                {
                    // Count the icons left of the pointer, the dragged one aside
                    to = 0;
                    foreach (var s in slots)
                        if (s.App != null && s.App != dragApp && !s.App.Leaving && s.Icon.X + s.Icon.Width / 2 < pt.X) to++;
                }
                if (to == dragTo) return;
                dragTo = to;
                foreach (var gp in gaps) gp.Show.Target = 0;
                if (to >= 0)
                {
                    var gap = new Gap { At = to };
                    Tune(gap.Show, 300, 0.85f);
                    gap.Show.Snap(0);
                    gap.Show.Target = 1;
                    gaps.Add(gap);
                }
            }

            void EndDrag()
            {
                var a = dragApp;
                dragApp = null;
                gaps.Clear();
                if (a == null) return;
                if (dragOut)
                {
                    if (a.Pinned) Unpin(a);
                    a.Show.Snap(0);
                    Refresh(false);
                    return;
                }
                var others = apps.FindAll(x => x != a && !x.Leaving);
                int pinned = others.FindAll(x => x.Pinned).Count;
                int to = Math.Max(0, Math.Min(dragTo, others.Count));
                if (to <= pinned) Pin(a, PinIndex(others, to));
                else if (a.Pinned) Pin(a, -1);
                // The icon lands where the gap was, then grows back in
                a.Show.Snap(0.6f);
                a.Show.Target = 1;
                Refresh(false);
            }

            // Position among pins for a slot among all apps
            static int PinIndex(List<DockApp> others, int to)
            {
                int n = 0;
                for (int i = 0; i < to && i < others.Count; i++) if (others[i].Pinned) n++;
                return n;
            }

            void InsertDragGap()
            {
                slots.RemoveAll(s => s.App == dragApp);
                int sys = slots.FindIndex(s => s.App != null);
                if (sys < 0) sys = slots.Count;
                // Gaps sorted from the end keep earlier indexes valid
                var order = new List<Gap>(gaps);
                order.Sort((x, y) => y.At.CompareTo(x.At));
                foreach (var gp in order)
                {
                    int appIndex = 0, at = sys;
                    for (int i = sys; i < slots.Count && appIndex < gp.At; i++) { if (slots[i].App != null) appIndex++; at = i + 1; }
                    slots.Insert(Math.Min(at, slots.Count), new Slot { Id = "gap", Show = Ease.Clamp01(gp.Show.Value) });
                }
            }

            bool StepDrag(float dt)
            {
                bool moving = false;
                for (int i = gaps.Count - 1; i >= 0; i--)
                {
                    gaps[i].Show.Step(dt);
                    moving |= !gaps[i].Show.Settled;
                    if (gaps[i].Show.Target == 0 && gaps[i].Show.Value < 0.01f) gaps.RemoveAt(i);
                }
                return moving || dragApp != null;
            }

            void DrawDragGhost()
            {
                float size = iconSize * 1.12f, alpha = dragOut && dragApp.Pinned ? 0.55f : 0.95f;
                var r = new RectangleF(mouseX - dragGrab.X - size / 2, mouseY - dragGrab.Y - size / 2, size, size);
                Tile(r, true, false, alpha);
                DrawIconImage(dragApp, Glyph(r), alpha);
                if (dragOut && dragApp.Pinned)
                {
                    // Removal mark: the pin goes when released here
                    var b = new RectangleF(r.Right - 12, r.Top - 4, 18, 18);
                    p.FillRound(b, 9, Ink.A(Ink.Base.Bg, 0.9f));
                    g.DrawLine(p.P(p.T.Text, 1.8f), b.X + 5, b.Y + 9, b.Right - 5, b.Y + 9);
                }
            }

            // Tray: hover, press and release reach the owning app
            void TrayMove(string id)
            {
                var ic = TrayById(id);
                if (ic != trayHot)
                {
                    TrayLeave();
                    trayHot = ic;
                    if (ic != null) { trayPop.Stop(); trayPop.Start(); }
                }
                if (ic != null && tray != null && !tray.Gone(ic)) TrayHost.Send(ic, 0x200, Cursor.Position);
            }

            void TrayPopup()
            {
                trayPop.Stop();
                if (trayHot == null || trayHot.Version <= 3) return;
                trayPopup = true;
                TrayHost.Send(trayHot, 0x406, Cursor.Position);
            }

            void TrayLeave()
            {
                trayPop.Stop();
                if (trayHot != null && trayPopup) TrayHost.Send(trayHot, 0x407, Cursor.Position);
                trayPopup = false;
                trayHot = null;
            }

            public void TrayDown(TrayIcon ic, MouseButtons button)
            {
                if (ic == null || tray == null || tray.Gone(ic)) return;
                bool dbl = button == MouseButtons.Left && lastTrayKey == ic.Key && (DateTime.UtcNow - lastTrayUtc).TotalMilliseconds < SystemInformation.DoubleClickTime;
                uint msg = button == MouseButtons.Right ? 0x204u : button == MouseButtons.Middle ? 0x207u : dbl ? 0x203u : 0x201u;
                TrayHost.Down(ic, msg, Cursor.Position);
            }

            public void TrayUp(TrayIcon ic, MouseButtons button)
            {
                if (ic == null) return;
                var at = Cursor.Position;
                if (button == MouseButtons.Left)
                {
                    TrayHost.Send(ic, 0x202, at);
                    if (ic.Version >= 3) TrayHost.Send(ic, 0x400, at);
                    lastTrayKey = ic.Key;
                    lastTrayUtc = DateTime.UtcNow;
                }
                else if (button == MouseButtons.Right)
                {
                    TrayHost.Send(ic, 0x205, at);
                    if (ic.Version >= 3) TrayHost.Send(ic, 0x7B, at);
                }
                else if (button == MouseButtons.Middle) TrayHost.Send(ic, 0x208, at);
            }

            void TrayDown(string id, MouseButtons button) { TrayDown(TrayById(id), button); }
            void TrayUp(string id, MouseButtons button) { TrayUp(TrayById(id), button); }

            // Files over an app: its window comes up; dropped files open with it
            protected override void OnDragOver(DragEventArgs e)
            {
                base.OnDragOver(e);
                var c = PointToClient(new Point(e.X, e.Y));
                mouseX = c.X / scale;
                mouseY = c.Y / scale;
                inside = true;
                hover.Target = 1;
                string id = HitAt(new PointF(mouseX, mouseY));
                var app = AppById(id);
                var files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
                e.Effect = DragDropEffects.None;
                if (app != null && files != null && OpensFiles(app)) e.Effect = DragDropEffects.Copy;
                else if (app == null && files != null && Array.TrueForAll(files, Pinnable)) e.Effect = DragDropEffects.Link;
                if (id != dropHover) { dropHover = id; dropSinceUtc = DateTime.UtcNow; }
                else if (app != null && app.Running && (DateTime.UtcNow - dropSinceUtc).TotalMilliseconds > 700)
                {
                    var w = app.Recent();
                    if (w != null && GetForegroundWindow() != w.H) ShellApp.Activate(w.H);
                }
                Animate();
            }

            protected override void OnDragEnter(DragEventArgs e) { OnDragOver(e); }

            protected override void OnDragLeave(EventArgs e)
            {
                base.OnDragLeave(e);
                dropHover = null;
                inside = false;
                hover.Target = 0;
                Animate();
            }

            protected override void OnDragDrop(DragEventArgs e)
            {
                base.OnDragDrop(e);
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                var app = AppById(dropHover);
                dropHover = null;
                inside = false;
                hover.Target = 0;
                if (files == null || files.Length == 0) return;
                if (app != null && OpensFiles(app))
                {
                    var args = new System.Text.StringBuilder();
                    foreach (var f in files) args.Append('"').Append(f).Append("\" ");
                    try { Process.Start(new ProcessStartInfo(app.Exe, args.ToString().Trim()) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Write("dock drop: " + ex.Message); }
                    if (BounceOnce(app)) Animate();
                    return;
                }
                if (app == null)
                {
                    var pins = isle.DockPins();
                    foreach (var f in files) if (Pinnable(f) && !pins.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) pins.Add(f);
                    isle.SetDockPins(pins);
                }
            }

            static bool BounceOnce(DockApp app)
            {
                if (app.Hop >= 0) return false;
                app.Hop = 0;
                app.Hops = 1;
                return true;
            }

            static bool OpensFiles(DockApp a) { return a.Exe.Length > 0 && File.Exists(a.Exe) && !a.Packaged; }

            static bool Pinnable(string f)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                return (ext == ".exe" || ext == ".lnk") && File.Exists(f);
            }
        }
    }
}
