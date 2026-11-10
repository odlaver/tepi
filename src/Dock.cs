using System;
using System.Collections.Generic;
using System.IO;

namespace Tepi
{
    // Dock: opt-in bar of apps and live widgets
    partial class Island
    {
        DockWindow dockWin;
        readonly System.Windows.Forms.Timer dockLeave = new System.Windows.Forms.Timer { Interval = 3500 };
        FileSystemWatcher pinWatch;
        readonly System.Windows.Forms.Timer pinSync = new System.Windows.Forms.Timer { Interval = 600 };

        // Keys apart from "dock", which glues the island to its edge
        bool DockBarOn { get { return settings.GetBool("dockbar.on", false); } }

        static readonly string[] DockWidgetIds = { "clock", "media", "ai", "timer", "weather", "battery", "system" };
        static readonly string[] DockWidgetNames = { "Jam", "Musik", "Kuota AI dan agen", "Timer", "Cuaca", "Baterai", "Sistem" };

        bool DockWidget(string id) { return settings.GetBool("dockbar.w." + id, id != "system"); }

        void ApplyDock()
        {
            if (DockBarOn && dockWin == null)
            {
                ImportTaskbarPins(false);
                WatchPins(true);
                dockWin = new DockWindow(this);
                dockWin.Show();
            }
            else if (!DockBarOn && dockWin != null)
            {
                WatchPins(false);
                dockWin.Shutdown();
                dockWin = null;
            }
            else if (dockWin != null) dockWin.Changed();
        }

        // Something on screen moved: dock glass and apps look again
        void NudgeDock() { if (dockWin != null) dockWin.Nudge(); }

        // Dock click opens the island on a tab; closes if never visited
        void OpenFromDock(int pg)
        {
            if (!CanExpand()) return;
            if (expanded) SetPage(pg, false);
            else Expand(pg);
            dockLeave.Stop();
            dockLeave.Start();
        }

        void DockLeaveTick()
        {
            dockLeave.Stop();
            if (expanded && !pointerInside) { expanded = false; Kick(); }
        }

        List<string> DockPins()
        {
            var list = new List<string>();
            foreach (var part in settings.Get("dockbar.pins", "").Split('|'))
                if (part.Trim().Length > 0 && !list.Exists(x => string.Equals(x, part.Trim(), StringComparison.OrdinalIgnoreCase))) list.Add(part.Trim());
            return list;
        }

        void SetDockPins(List<string> pins)
        {
            settings.Set("dockbar.pins", string.Join("|", pins.ToArray()));
            settings.Save();
            if (dockWin != null) dockWin.Refresh(false);
        }

        // First run takes the Windows taskbar's pins, in its order
        void ImportTaskbarPins(bool again)
        {
            if (!again && settings.GetBool("dockbar.imported", false)) return;
            var pins = DockPins();
            foreach (var t in AppWatch.TaskbarPins())
                if (!pins.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) pins.Add(t);
            settings.Set("dockbar.imported", true);
            SetDockPins(pins);
        }

        // "Pin to taskbar" from Start and Explorer lands on the dock too
        void WatchPins(bool on)
        {
            if (!on)
            {
                if (pinWatch != null) { pinWatch.EnableRaisingEvents = false; pinWatch.Dispose(); pinWatch = null; }
                pinSync.Stop();
                return;
            }
            if (pinWatch != null || !Directory.Exists(AppWatch.PinnedFolder)) return;
            if (!pinSyncWired) { pinSync.Tick += delegate { SyncPins(); }; pinSyncWired = true; }
            try
            {
                lastPinFiles = PinFiles();
                pinWatch = new FileSystemWatcher(AppWatch.PinnedFolder, "*.lnk") { NotifyFilter = NotifyFilters.FileName };
                FileSystemEventHandler h = delegate { Post(delegate { pinSync.Stop(); pinSync.Start(); }); };
                pinWatch.Created += h;
                pinWatch.Deleted += h;
                pinWatch.Renamed += delegate { Post(delegate { pinSync.Stop(); pinSync.Start(); }); };
                pinWatch.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("dock pin watch: " + ex.Message); }
        }

        HashSet<string> lastPinFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool pinSyncWired;

        static HashSet<string> PinFiles()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var f in Directory.GetFiles(AppWatch.PinnedFolder, "*.lnk")) set.Add(f); }
            catch (Exception) { }
            return set;
        }

        // Settled folder: new shortcuts join, removed ones leave
        void SyncPins()
        {
            pinSync.Stop();
            var now = PinFiles();
            var pins = DockPins();
            bool changed = false;
            foreach (var f in now)
                if (!lastPinFiles.Contains(f) && !pins.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) { pins.Add(f); changed = true; }
            foreach (var f in lastPinFiles)
                if (!now.Contains(f) && pins.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)) > 0) changed = true;
            lastPinFiles = now;
            if (changed) SetDockPins(pins);
        }

        // Notification centre count per app id, and per app name as a fallback
        Dictionary<string, int> DockBadgeCounts()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!NotifOn) return map;
            foreach (var n in notifs)
            {
                if (n.AppId.StartsWith("phone:", StringComparison.Ordinal)) continue;
                int c;
                if (n.AppId.Length > 0) { map.TryGetValue(n.AppId, out c); map[n.AppId] = c + 1; }
                if (n.AppName.Length > 0) { string k = "name:" + n.AppName; map.TryGetValue(k, out c); map[k] = c + 1; }
            }
            return map;
        }
    }
}
