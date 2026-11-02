using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // Per-app rules: hide the island or hold notices
    partial class Island
    {
        // island.except: exe:mode:name, comma separated
        public class ExceptRule
        {
            public string Exe, Mode, Name;
        }

        public const string ExceptHide = "hide", ExceptQuiet = "quiet";

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint WindowPid(IntPtr hwnd, out uint pid);
        delegate bool ChildProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, ChildProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        const string FrameHost = "applicationframehost.exe";

        IntPtr exceptSeen = new IntPtr(-1);
        ExceptRule exceptFront;

        // Front app's rule, looked up once per window
        ExceptRule FrontRule()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == exceptSeen) return exceptFront;
            exceptSeen = fg;
            exceptFront = null;
            if (fg == IntPtr.Zero || settings.Get("island.except", "").Length == 0) return null;
            uint pid;
            WindowPid(fg, out pid);
            if (pid == ownPid) return null;
            string exe = ExeOf(pid);
            if (exe == FrameHost) exe = HostedExe(fg, pid);
            foreach (var r in ExceptRules())
                if (r.Exe == exe) { exceptFront = r; break; }
            return exceptFront;
        }

        // Hide only when the app shares the island's screen
        bool ExceptHidden()
        {
            var r = FrontRule();
            return r != null && r.Mode == ExceptHide && Native.MonitorFromWindow(Native.GetForegroundWindow(), 2) == monitorHandle;
        }

        bool ExceptQuieted()
        {
            var r = FrontRule();
            return r != null && r.Mode == ExceptQuiet;
        }

        static string ExeOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(520);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()).ToLowerInvariant() : "";
            }
            finally { CloseHandle(h); }
        }

        // Store apps live in a child window
        static string HostedExe(IntPtr frame, uint hostPid)
        {
            string found = FrameHost;
            EnumChildWindows(frame, (h, l) =>
            {
                uint p;
                WindowPid(h, out p);
                if (p == hostPid) return true;
                found = ExeOf(p);
                return false;
            }, IntPtr.Zero);
            return found;
        }

        static bool WindowShown(IntPtr hwnd)
        {
            int cloaked;
            return IsWindowVisible(hwnd) && !(DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0);
        }

        public List<ExceptRule> ExceptRules()
        {
            var list = new List<ExceptRule>();
            foreach (var entry in settings.Get("island.except", "").Split(','))
            {
                var p = entry.Split(':');
                if (p[0].Trim().Length == 0) continue;
                list.Add(new ExceptRule
                {
                    Exe = p[0].Trim().ToLowerInvariant(),
                    Mode = p.Length > 1 && p[1] == ExceptQuiet ? ExceptQuiet : ExceptHide,
                    Name = p.Length > 2 && p[2].Trim().Length > 0 ? p[2].Trim() : p[0].Trim(),
                });
            }
            return list;
        }

        public void SaveExceptRules(List<ExceptRule> rules)
        {
            var parts = new List<string>();
            foreach (var r in rules) parts.Add(r.Exe + ":" + r.Mode + ":" + Clean(r.Name));
            settings.Set("island.except", string.Join(",", parts.ToArray()));
            settings.Save();
            exceptSeen = new IntPtr(-1);
            CheckFullscreen();
        }

        static string Clean(string name) { return name.Replace(",", " ").Replace(":", " ").Trim(); }

        // Apps with a window now, for the picker
        public List<ExceptRule> OpenApps()
        {
            var seen = new HashSet<string>();
            var apps = new List<ExceptRule>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == ownPid || p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle.Length == 0 || !WindowShown(p.MainWindowHandle)) continue;
                    string exe = ExeOf((uint)p.Id);
                    string name = p.ProcessName;
                    if (exe == FrameHost)
                    {
                        exe = HostedExe(p.MainWindowHandle, (uint)p.Id);
                        if (exe != FrameHost && seen.Add(exe)) apps.Add(new ExceptRule { Exe = exe, Mode = ExceptHide, Name = Clean(p.MainWindowTitle) });
                        continue;
                    }
                    if (exe.Length == 0 || exe == "explorer.exe" || !seen.Add(exe)) continue;
                    try
                    {
                        string desc = p.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrEmpty(desc)) name = desc.Trim();
                    }
                    catch (Exception) { }
                    apps.Add(new ExceptRule { Exe = exe, Mode = ExceptHide, Name = Clean(name) });
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return apps;
        }
    }
}
