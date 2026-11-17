using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Windows.Media.Control;

namespace Tepi
{
    // Browser windows, for handing focus back
    static class BrowserWin
    {
        public static readonly string[] Names = { "brave", "chrome", "msedge", "firefox", "vivaldi", "opera" };

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc fn, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

        static HashSet<int> Pids()
        {
            var pids = new HashSet<int>();
            foreach (var name in Names)
                foreach (var p in Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }
            return pids;
        }

        // Browser windows and their minimized state
        public static Dictionary<IntPtr, bool> All()
        {
            var pids = Pids();
            var map = new Dictionary<IntPtr, bool>();
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pids.Contains((int)pid) && IsWindowVisible(h) && GetWindowTextLength(h) > 0) map[h] = IsIconic(h);
                return true;
            }, IntPtr.Zero);
            return map;
        }

        public static IntPtr Front() { return GetAncestor(GetForegroundWindow(), 2); }

        // The browser window that just came forward
        public static IntPtr WaitFront(int ms)
        {
            var pids = Pids();
            for (int waited = 0; waited <= ms; waited += 100)
            {
                IntPtr h = Front();
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (h != IntPtr.Zero && (pids.Contains((int)pid) || waited % 1000 == 0 && (pids = Pids()).Contains((int)pid))) return h;
                Thread.Sleep(100);
            }
            return IntPtr.Zero;
        }
    }

    // One music tab in the user's browser
    partial class Island
    {
        static readonly List<string> openedTitles = new List<string>();
        static readonly Regex UnreadCount = new Regex(@"^\(\d+\)\s*");

        // Pause media, play, hand focus back
        internal static void OpenSong(string url, string title)
        {
            IntPtr back = BrowserWin.Front();
            var mayReplace = new List<string>();
            try
            {
                var mgr = WinRt.Get(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                foreach (var s in mgr.GetSessions())
                {
                    bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    string t = "";
                    try { t = WinRt.Get(s.TryGetMediaPropertiesAsync()).Title ?? ""; } catch (Exception) { }
                    // Someone's paused tutorial tab stays untouched
                    if (t.Length > 0 && (playing || OpenedByTepi(t))) mayReplace.Add(t);
                    if (playing) { try { WinRt.Get(s.TryPauseAsync()); } catch (Exception) { } }
                }
            }
            catch (Exception ex) { Log.Write("media jeda: " + ex.Message); }
            var before = BrowserWin.All();
            IntPtr hwnd = IntPtr.Zero;
            bool reused = mayReplace.Count > 0 && ReplaceTab(mayReplace, url, out hwnd);
            if (!reused)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                hwnd = BrowserWin.WaitFront(5000);
            }
            Log.Write("youtube: " + (reused ? "tab ditimpa" : "tab baru"));
            if (!string.IsNullOrEmpty(title)) lock (openedTitles) { openedTitles.Remove(title); openedTitles.Insert(0, title); if (openedTitles.Count > 20) openedTitles.RemoveAt(20); }
            if (hwnd == IntPtr.Zero) return;
            // Focus back; the browser drops behind
            if (back != IntPtr.Zero && back != hwnd && !before.ContainsKey(back)) TerminalFinder.Focus(back);
            bool wasHidden;
            if (!before.TryGetValue(hwnd, out wasHidden)) wasHidden = true;
            if (!wasHidden) return;
            // Minimize early and playback may stall
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (!WaitPlaying(title, 10000)) return;
                // Restored session windows were hidden too
                foreach (var w in BrowserWin.All())
                    if (!w.Value && (w.Key == hwnd || !before.ContainsKey(w.Key))) BrowserWin.ShowWindow(w.Key, 7);
                if (back != IntPtr.Zero && !before.ContainsKey(back) && BrowserWin.All().ContainsKey(BrowserWin.Front())) TerminalFinder.Focus(back);
            });
        }

        static bool WaitPlaying(string title, int ms)
        {
            for (int waited = 0; waited < ms; waited += 250)
            {
                try
                {
                    var mgr = WinRt.Get(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    foreach (var s in mgr.GetSessions())
                    {
                        if (s.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                        string t = "";
                        try { t = WinRt.Get(s.TryGetMediaPropertiesAsync()).Title ?? ""; } catch (Exception) { }
                        // An ad plays first; that's started too
                        if (string.IsNullOrEmpty(title) || t.Length > 0) return true;
                    }
                }
                catch (Exception) { }
                Thread.Sleep(250);
            }
            return false;
        }

        static bool OpenedByTepi(string title) { lock (openedTitles) return openedTitles.Contains(title); }

        // Browser tab matching one of these titles
        static bool ReplaceTab(List<string> titles, string url, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            var pids = new HashSet<int>();
            foreach (var name in BrowserWin.Names)
                foreach (var p in Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }
            if (pids.Count == 0) return false;
            var tabKind = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            foreach (AutomationElement win in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                try
                {
                    if (!pids.Contains(win.Current.ProcessId)) continue;
                    foreach (AutomationElement tab in win.FindAll(TreeScope.Descendants, tabKind))
                    {
                        string name = UnreadCount.Replace(tab.Current.Name ?? "", "");
                        if (!titles.Exists(t => name.StartsWith(t + " - YouTube", StringComparison.OrdinalIgnoreCase))) continue;
                        object sel;
                        if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out sel)) continue;
                        ((SelectionItemPattern)sel).Select();
                        hwnd = new IntPtr(win.Current.NativeWindowHandle);
                        return Navigate(hwnd, win.Current.ProcessId, url);
                    }
                }
                catch (ElementNotAvailableException) { }
            }
            return false;
        }

        // Focused edit box in that browser
        static AutomationElement AddressBar(int pid)
        {
            try
            {
                var f = AutomationElement.FocusedElement;
                return f != null && f.Current.ProcessId == pid && f.Current.ControlType == ControlType.Edit ? f : null;
            }
            catch (Exception) { return null; }
        }

        // Ctrl+L, set URL, Enter; never into page
        static bool Navigate(IntPtr hwnd, int pid, string url)
        {
            TerminalFinder.Focus(hwnd);
            Func<bool> front = () => GetAncestor(GetForegroundWindow(), 2) == hwnd;
            for (int i = 0; i < 20 && !front(); i++) Thread.Sleep(50);
            if (!front()) { Log.Write("youtube: jendela browser tidak bisa didepankan"); return false; }
            Thread.Sleep(120);
            AutomationElement bar = null;
            for (int attempt = 0; attempt < 2 && bar == null; attempt++)
            {
                var focus = Chord(0x4C);
                SendInput((uint)focus.Length, focus, Marshal.SizeOf(typeof(INPUT)));
                for (int i = 0; i < 8 && bar == null; i++) { Thread.Sleep(75); bar = AddressBar(pid); }
            }
            if (bar == null || !front()) { Log.Write("youtube: bilah alamat tidak terfokus"); return false; }
            object value;
            bool set = false;
            if (bar.TryGetCurrentPattern(ValuePattern.Pattern, out value))
            {
                try { ((ValuePattern)value).SetValue(url); set = true; }
                catch (Exception) { }
            }
            var keys = new List<INPUT>();
            if (!set) foreach (char c in url) { keys.Add(Key(0, c, 4)); keys.Add(Key(0, c, 6)); }
            // Delete drops any inline suggestion
            keys.AddRange(new[] { Key(0x2E, '\0', 0), Key(0x2E, '\0', 2), Key(0x0D, '\0', 0), Key(0x0D, '\0', 2) });
            Thread.Sleep(60);
            if (AddressBar(pid) == null || !front()) { Log.Write("youtube: fokus pindah sebelum Enter"); return false; }
            return SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf(typeof(INPUT))) == keys.Count;
        }
    }
}
