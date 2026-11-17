using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace Tepi
{
    class NetState
    {
        public bool Online, Wifi, Any;
        public string Name = "";

        // Behind a login page (hotel, campus)
        public bool Captive;
        public string Sig { get { return Any + "|" + Online + "|" + Captive + "|" + Wifi + "|" + Name; } }
    }

    // Wi-Fi / LAN / internet transitions, debounced
    class NetWatcher
    {
        public event Action<NetState, NetState> Changed;
        Timer debounce;
        NetState last;

        public NetState Current { get { return last ?? new NetState(); } }

        public void Start()
        {
            last = Read();
            debounce = new Timer(delegate { Check(); }, null, Timeout.Infinite, Timeout.Infinite);
            NetworkChange.NetworkAddressChanged += delegate { debounce.Change(1800, Timeout.Infinite); };
            NetworkChange.NetworkAvailabilityChanged += delegate { debounce.Change(1800, Timeout.Infinite); };
            // Login done: address stays, level moves
            NetworkInformation.NetworkStatusChanged += delegate { debounce.Change(1800, Timeout.Infinite); };
        }

        void Check()
        {
            var now = Read();
            // Windows re-probes; look again while captive
            if (now.Captive) debounce.Change(15000, Timeout.Infinite);
            if (now.Sig == last.Sig) return;
            var before = last;
            last = now;
            var h = Changed;
            if (h != null) h(before, now);
        }

        static NetState Read()
        {
            var s = new NetState();
            try
            {
                var p = NetworkInformation.GetInternetConnectionProfile();
                if (p == null) return s;
                s.Any = true;
                var level = p.GetNetworkConnectivityLevel();
                s.Online = level == NetworkConnectivityLevel.InternetAccess;
                s.Captive = level == NetworkConnectivityLevel.ConstrainedInternetAccess;
                s.Wifi = p.IsWlanConnectionProfile;
                s.Name = s.Wifi && p.WlanConnectionProfileDetails != null ? p.WlanConnectionProfileDetails.GetConnectedSsid() : (p.ProfileName ?? "");
            }
            catch (Exception ex) { Log.Write("net: " + ex.Message); }
            return s;
        }
    }

    // Running processes, to drop stale registry entries
    class Procs
    {
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr h, ref int length, StringBuilder name);

        readonly Dictionary<string, long> oldest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> sealedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Procs Take()
        {
            var r = new Procs();
            var sb = new StringBuilder(1024);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    IntPtr h = OpenProcess(0x1000, false, p.Id);
                    if (h == IntPtr.Zero)
                    {
                        try { r.sealedNames.Add(p.ProcessName); } catch (Exception) { }
                        continue;
                    }
                    try
                    {
                        long created, e, k, u;
                        if (!Native.GetProcessTimes(h, out created, out e, out k, out u)) continue;
                        sb.Length = 0;
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref size)) r.Note(sb.ToString(), created);
                        sb.Length = 0;
                        size = sb.Capacity;
                        if (GetPackageFamilyName(h, ref size, sb) == 0) r.Note(sb.ToString(), created);
                    }
                    finally { CloseHandle(h); }
                }
            }
            return r;
        }

        void Note(string key, long created)
        {
            long t;
            if (!oldest.TryGetValue(key, out t) || created < t) oldest[key] = created;
        }

        // Live only if its process predates the session
        public bool Holds(string key, bool packaged, long since)
        {
            string id = packaged ? key : key.Replace('#', '\\');
            long t;
            if (oldest.TryGetValue(id, out t)) return t <= since;
            return !packaged && sealedNames.Contains(Path.GetFileNameWithoutExtension(id));
        }
    }

    // Mic/camera users from privacy registry
    class PrivacyWatcher
    {
        public event Action<string[], string[]> Changed;
        const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        public void Start()
        {
            var t = new Thread(Prof.Wrap(Run)) { IsBackground = true, Name = "privacy" };
            t.Start();
        }

        // Restart after a failure instead of going quiet
        void Run()
        {
            for (;;)
            {
                Watch();
                Thread.Sleep(5000);
            }
        }

        // One capability key and its change event
        sealed class Consent : IDisposable
        {
            public readonly string Name;
            public readonly RegistryKey Key;
            public readonly AutoResetEvent Touched = new AutoResetEvent(false);
            public bool Rearm = true;

            public Consent(string name)
            {
                Name = name;
                Key = Registry.CurrentUser.OpenSubKey(Store + name);
            }

            // Subkeys, name and last-write changes
            public void Arm()
            {
                if (Rearm && Key != null) Native.RegNotifyChangeKeyValue(Key.Handle, true, 0x1 | 0x4, Touched.SafeWaitHandle, true);
                Rearm = false;
            }

            public void Dispose()
            {
                if (Key != null) Key.Dispose();
                Touched.Dispose();
            }
        }

        void Watch()
        {
            try
            {
                using (var mic = new Consent("microphone"))
                using (var cam = new Consent("webcam"))
                {
                    var both = new[] { mic, cam };
                    var signals = new WaitHandle[] { mic.Touched, cam.Touched };
                    string shown = null;
                    for (;;)
                    {
                        mic.Arm();
                        cam.Arm();
                        var running = new RunningApps();
                        var micUsers = InUse(mic.Name, running);
                        var camUsers = InUse(cam.Name, running);
                        string now = string.Join("#", string.Join("|", micUsers), string.Join("|", camUsers));
                        if (now != shown)
                        {
                            shown = now;
                            var handler = Changed;
                            if (handler != null) handler(micUsers, camUsers);
                        }
                        // Crashed apps leave no registry trace
                        bool anyone = micUsers.Length + camUsers.Length > 0;
                        int which = WaitHandle.WaitAny(signals, anyone ? 3000 : Timeout.Infinite);
                        if (which == WaitHandle.WaitTimeout) continue;
                        both[which].Rearm = true;
                        // Writes come in bursts
                        Thread.Sleep(250);
                    }
                }
            }
            catch (Exception ex) { Log.Write("privacy: " + ex.Message); }
        }

        // Process list taken once, only when needed
        sealed class RunningApps
        {
            Procs snapshot;
            public Procs Get() { return snapshot ?? (snapshot = Procs.Take()); }
        }

        static string[] InUse(string capability, RunningApps running)
        {
            var names = new List<string>();
            Scan(Store + capability, true, names, running);
            Scan(Store + capability + @"\NonPackaged", false, names, running);
            return names.ToArray();
        }

        static void Scan(string path, bool packaged, List<string> names, RunningApps running)
        {
            using (var parent = Registry.CurrentUser.OpenSubKey(path))
            {
                if (parent == null) return;
                foreach (var app in parent.GetSubKeyNames())
                {
                    if (app == "NonPackaged" || (!packaged && IsSelf(app))) continue;
                    long since;
                    if (!Recording(parent, app, out since)) continue;
                    if (!running.Get().Holds(app, packaged, since)) continue;
                    string label = Label(app);
                    if (!names.Contains(label)) names.Add(label);
                }
            }
        }

        // Started, not stopped: in use now
        static bool Recording(RegistryKey parent, string app, out long since)
        {
            since = 0;
            using (var entry = parent.OpenSubKey(app))
            {
                if (entry == null) return false;
                object started = entry.GetValue("LastUsedTimeStart"), stopped = entry.GetValue("LastUsedTimeStop");
                if (started == null || stopped == null) return false;
                try
                {
                    since = Convert.ToInt64(started);
                    return since > 0 && Convert.ToInt64(stopped) == 0;
                }
                catch (InvalidCastException) { return false; }
            }
        }

        static readonly string SelfPath = System.Reflection.Assembly.GetEntryAssembly().Location;

        // Dictation is Tepi itself: not a call
        static bool IsSelf(string app) { return string.Equals(app.Replace('#', '\\'), SelfPath, StringComparison.OrdinalIgnoreCase); }

        // "C:#x#Zoom.exe" or "Pub.App_hash" to "Zoom", "App"
        static string Label(string app)
        {
            int slash = app.LastIndexOf('#');
            string name;
            if (slash >= 0) name = app.Substring(slash + 1);
            else
            {
                int cut = app.IndexOf('_');
                name = cut > 0 ? app.Remove(cut) : app;
                name = name.Substring(name.LastIndexOf('.') + 1);
            }
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Remove(name.Length - 4);
            if (name.Length == 0) return name;
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }
}
