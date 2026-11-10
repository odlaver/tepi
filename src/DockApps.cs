using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tepi
{
    // One window the taskbar would list
    sealed class DockWin
    {
        public IntPtr H;
        public int Pid;
        public string Exe = "", AppId = "", Title = "";
        public bool Packaged, Flash;
        public long Seen, Used;
        public DateTime BornUtc;
    }

    // One app on the dock: pinned, running, or both
    sealed class DockApp
    {
        public string Key = "", Target = "", Exe = "", AppId = "", Name = "";
        public bool Pinned, Packaged, Attention, Leaving;
        public readonly List<DockWin> Wins = new List<DockWin>();
        public DateTime LaunchUtc = DateTime.MinValue;
        public int Badge;
        // Slot, size, presence, indicator: kept while on the dock
        public readonly Spring X = new Spring(0, 1, 1, 0.05f), Grow = new Spring(1, 1, 1, 0.0005f), Show = new Spring(0, 1, 1, 0.002f), Dot = new Spring(0, 1, 1, 0.003f);
        // Bounce clock in seconds; below zero means still
        public float Hop = -1, Delay;
        public int Hops;
        public bool Placed;

        public bool Running { get { return Wins.Count > 0; } }

        public DockWin Recent()
        {
            DockWin best = null;
            foreach (var w in Wins) if (best == null || w.Used > best.Used) best = w;
            return best;
        }
    }

    // Taskbar-eligible windows, grouped into apps
    sealed class AppWatch
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        const int ExAppWindow = 0x40000, ExToolWindow = 0x80, ExNoActivate = 0x8000000;

        readonly Dictionary<IntPtr, DockWin> wins = new Dictionary<IntPtr, DockWin>();
        readonly Dictionary<int, string> exeOf = new Dictionary<int, string>(), packOf = new Dictionary<int, string>();
        readonly Dictionary<int, long> bornOf = new Dictionary<int, long>();
        readonly Dictionary<string, PinInfo> pinInfo = new Dictionary<string, PinInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly StringBuilder sb = new StringBuilder(256);
        readonly List<IntPtr> found = new List<IntPtr>();
        readonly HashSet<IntPtr> foundSet = new HashSet<IntPtr>();
        EnumProc enumProc;
        long seq, useSeq;

        sealed class PinInfo { public string Exe = "", AppId = "", Name = ""; public bool Packaged, Missing; }

        public int Count { get { return wins.Count; } }

        public DockWin Find(IntPtr h)
        {
            DockWin w;
            return wins.TryGetValue(h, out w) ? w : null;
        }

        bool Eligible(IntPtr h)
        {
            if (!IsWindowVisible(h)) return false;
            int ex = Native.GetWindowLong(h, -20);
            if ((ex & ExAppWindow) == 0)
            {
                if ((ex & (ExToolWindow | ExNoActivate)) != 0) return false;
                if (GetWindow(h, 4) != IntPtr.Zero) return false;
            }
            // Other virtual desktops and parked store apps are cloaked
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false;
            if (GetWindowTextLength(h) == 0) return false;
            sb.Length = 0;
            Native.GetClassName(h, sb, 64);
            string c = sb.ToString();
            return c != "Progman" && c != "WorkerW" && c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd";
        }

        // Full pass; true when the window set changed
        public bool Rescan()
        {
            found.Clear();
            foundSet.Clear();
            if (enumProc == null) enumProc = delegate (IntPtr h, IntPtr l) { if (Eligible(h)) { found.Add(h); foundSet.Add(h); } return true; };
            EnumWindows(enumProc, IntPtr.Zero);
            bool changed = false;
            var gone = new List<IntPtr>();
            foreach (var h in wins.Keys) if (!foundSet.Contains(h)) gone.Add(h);
            foreach (var h in gone) { wins.Remove(h); changed = true; }
            // First sight: older processes first, like the taskbar's own order
            var fresh = found.FindAll(h => !wins.ContainsKey(h));
            if (fresh.Count > 1)
            {
                var born = new Dictionary<IntPtr, long>();
                foreach (var h in fresh) born[h] = ProcessBorn(PidOf(h));
                fresh.Sort((a, b) => born[a].CompareTo(born[b]));
            }
            foreach (var h in fresh) if (Add(h)) changed = true;
            foreach (var w in wins.Values) if (Refresh(w)) changed = true;
            if (exeOf.Count > 300) { exeOf.Clear(); packOf.Clear(); bornOf.Clear(); }
            return changed;
        }

        // One window appeared, changed style or went away
        public bool Track(IntPtr h)
        {
            bool known = wins.ContainsKey(h), ok = IsWindow(h) && Eligible(h);
            if (known && !ok) { wins.Remove(h); return true; }
            if (!known && ok) return Add(h);
            return known && Refresh(wins[h]);
        }

        public bool Drop(IntPtr h) { return wins.Remove(h); }

        public void Activated(IntPtr h)
        {
            DockWin w;
            if (!wins.TryGetValue(h, out w)) return;
            w.Used = ++useSeq;
            w.Flash = false;
        }

        public bool Flashed(IntPtr h)
        {
            DockWin w;
            if (!wins.TryGetValue(h, out w)) { if (!Track(h) || !wins.TryGetValue(h, out w)) return false; }
            if (w.Flash) return false;
            w.Flash = true;
            return true;
        }

        public string Title(IntPtr h)
        {
            sb.Length = 0;
            GetWindowText(h, sb, 256);
            return sb.ToString();
        }

        int PidOf(IntPtr h)
        {
            int pid;
            GetWindowThreadProcessId(h, out pid);
            return pid;
        }

        bool Add(IntPtr h)
        {
            int pid = PidOf(h);
            var w = new DockWin { H = h, Pid = pid, Seen = ++seq, BornUtc = DateTime.UtcNow, Title = Title(h) };
            w.Exe = ExeOf(pid);
            ResolveId(w);
            wins[h] = w;
            return true;
        }

        // Titles always; app ids settle in the first seconds
        bool Refresh(DockWin w)
        {
            w.Title = Title(w.H);
            if ((DateTime.UtcNow - w.BornUtc).TotalSeconds > 6) return false;
            string was = w.AppId;
            ResolveId(w);
            return was != w.AppId;
        }

        void ResolveId(DockWin w)
        {
            string id = ShellApp.WindowAppId(w.H);
            string pack = PackOf(w.Pid);
            if (id.Length == 0) id = pack;
            // Store app frames: the app lives in a child window
            if (w.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr core = FindWindowEx(w.H, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero)
                {
                    string inner = PackOf(PidOf(core));
                    if (inner.Length > 0) { pack = inner; if (id.Length == 0) id = inner; }
                }
                if (pack.Length == 0 && id.Contains("!")) pack = id;
            }
            // Folder windows carry no id; the taskbar knows them by this one
            if (id.Length == 0 && string.Equals(Path.GetFileName(w.Exe), "explorer.exe", StringComparison.OrdinalIgnoreCase)) id = "Microsoft.Windows.Explorer";
            w.AppId = id;
            w.Packaged = pack.Length > 0;
        }

        string ExeOf(int pid)
        {
            string s;
            if (!exeOf.TryGetValue(pid, out s)) exeOf[pid] = s = ShellApp.ProcessPath(pid);
            return s;
        }

        string PackOf(int pid)
        {
            string s;
            if (!packOf.TryGetValue(pid, out s)) packOf[pid] = s = ShellApp.ProcessAppId(pid);
            return s;
        }

        long ProcessBorn(int pid)
        {
            long t;
            if (bornOf.TryGetValue(pid, out t)) return t;
            t = long.MaxValue;
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h != IntPtr.Zero)
            {
                long create, exit, kernel, user;
                if (Native.GetProcessTimes(h, out create, out exit, out kernel, out user)) t = create;
                CloseHandle(h);
            }
            bornOf[pid] = t;
            return t;
        }

        static string KeyOf(DockWin w)
        {
            if (w.AppId.Length > 0) return "id:" + w.AppId.ToLowerInvariant();
            if (w.Exe.Length > 0) return "exe:" + w.Exe.ToLowerInvariant();
            return "win:" + w.H.ToInt64();
        }

        // What a pin or a new window of this app launches
        static string TargetOf(DockWin w)
        {
            if (w.Packaged && w.AppId.Length > 0) return ShellApp.AppsFolderOf(w.AppId);
            if (w.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return "";
            return w.Exe;
        }

        string NameOf(DockWin w)
        {
            string key = KeyOf(w), name;
            if (names.TryGetValue(key, out name)) return name;
            name = "";
            if (w.Packaged && w.AppId.Length > 0) name = ShellApp.DisplayName(ShellApp.AppsFolderOf(w.AppId));
            if (name.Length == 0 && w.Exe.Length > 0 && !w.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) name = ExeName(w.Exe);
            if (name.Length == 0) return w.Title;
            names[key] = name;
            return name;
        }

        public static string ExeName(string exe)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrEmpty(v.FileDescription) && v.FileDescription.Trim().Length > 1) return v.FileDescription.Trim();
            }
            catch (Exception) { }
            return Path.GetFileNameWithoutExtension(exe);
        }

        PinInfo PinOf(string target)
        {
            PinInfo p;
            if (pinInfo.TryGetValue(target, out p)) return p.Missing ? null : p;
            p = new PinInfo();
            if (ShellApp.IsAppsFolder(target))
            {
                p.AppId = ShellApp.AumidOf(target);
                p.Packaged = p.AppId.Contains("!");
                p.Name = ShellApp.DisplayName(target);
            }
            else if (!File.Exists(target)) p.Missing = true;
            else if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var link = ShellApp.ReadLink(target);
                p.Exe = link.Target;
                p.AppId = link.AppId;
                p.Name = Path.GetFileNameWithoutExtension(target);
            }
            else { p.Exe = target; p.Name = ExeName(target); }
            if (p.Name.Length == 0) p.Name = Path.GetFileNameWithoutExtension(target);
            pinInfo[target] = p;
            return p.Missing ? null : p;
        }

        // A pin reads again next time, after its file changed
        public void ForgetPin(string target) { pinInfo.Remove(target); }

        static bool Matches(PinInfo p, DockWin w)
        {
            if (p.AppId.Length > 0 && w.AppId.Length > 0) return string.Equals(p.AppId, w.AppId, StringComparison.OrdinalIgnoreCase);
            return p.Exe.Length > 0 && string.Equals(p.Exe, w.Exe, StringComparison.OrdinalIgnoreCase);
        }

        // Pins in their order, then running apps by first sight
        public List<DockApp> Arrange(IList<string> pins, Dictionary<string, DockApp> old)
        {
            var list = new List<DockApp>();
            var taken = new HashSet<IntPtr>();
            var ordered = new List<DockWin>(wins.Values);
            ordered.Sort((a, b) => a.Seen.CompareTo(b.Seen));
            foreach (var target in pins)
            {
                var pin = PinOf(target);
                if (pin == null) continue;
                var app = Reuse(old, "pin:" + target.ToLowerInvariant());
                app.Pinned = true;
                app.Target = target;
                app.Exe = pin.Exe;
                app.AppId = pin.AppId;
                app.Packaged = pin.Packaged;
                app.Name = pin.Name;
                app.Wins.Clear();
                foreach (var w in ordered)
                    if (!taken.Contains(w.H) && Matches(pin, w)) { app.Wins.Add(w); taken.Add(w.H); }
                list.Add(app);
            }
            var byKey = new Dictionary<string, DockApp>();
            foreach (var w in ordered)
            {
                if (taken.Contains(w.H)) continue;
                string key = KeyOf(w);
                DockApp app;
                if (!byKey.TryGetValue(key, out app))
                {
                    app = Reuse(old, key);
                    app.Pinned = false;
                    app.Wins.Clear();
                    app.AppId = w.AppId;
                    app.Exe = w.Exe;
                    app.Packaged = w.Packaged;
                    app.Target = TargetOf(w);
                    app.Name = NameOf(w);
                    byKey[key] = app;
                    list.Add(app);
                }
                app.Wins.Add(w);
                taken.Add(w.H);
            }
            foreach (var a in list)
            {
                a.Attention = a.Wins.Exists(w => w.Flash);
                if (a.Running) a.LaunchUtc = DateTime.MinValue;
            }
            return list;
        }

        static DockApp Reuse(Dictionary<string, DockApp> old, string key)
        {
            DockApp app;
            if (old != null && old.TryGetValue(key, out app) && !app.Leaving) return app;
            return new DockApp { Key = key };
        }

        public static string PinnedFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"); }
        }

        // The Windows taskbar's pins, in its order
        public static List<string> TaskbarPins()
        {
            var spots = new List<KeyValuePair<long, string>>();
            byte[] fav = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"))
                    if (k != null) fav = k.GetValue("Favorites") as byte[];
            }
            catch (Exception) { }
            var linkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(PinnedFolder))
                    foreach (var f in Directory.GetFiles(PinnedFolder, "*.lnk"))
                    {
                        long at = fav == null ? -1 : IndexOf(fav, Encoding.Unicode.GetBytes(Path.GetFileName(f)));
                        spots.Add(new KeyValuePair<long, string>(at < 0 ? long.MaxValue : at, f));
                        string id = ShellApp.ReadLink(f).AppId;
                        if (id.Length > 0) linkIds.Add(id);
                    }
            }
            catch (Exception ex) { Log.Write("dock pins: " + ex.Message); }
            // Store apps pinned without a shortcut file
            if (fav != null)
                for (int off = 0; off < 2; off++)
                {
                    string s = Encoding.Unicode.GetString(fav, off, fav.Length - off);
                    foreach (Match m in Regex.Matches(s, @"[A-Za-z0-9][A-Za-z0-9.\-]*_[a-z0-9]{13}![A-Za-z0-9.\-]+"))
                    {
                        if (linkIds.Contains(m.Value)) continue;
                        string target = ShellApp.AppsFolderOf(m.Value);
                        if (!spots.Exists(x => x.Value == target)) spots.Add(new KeyValuePair<long, string>(m.Index * 2L + off, target));
                    }
                }
            spots.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
            var list = new List<string>();
            foreach (var s in spots) list.Add(s.Value);
            return list;
        }

        static long IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
