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
        const string Base = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "privacy" };
            t.Start();
        }

        // Restart after a failure instead of going quiet
        void Run()
        {
            while (true)
            {
                Watch();
                Thread.Sleep(5000);
            }
        }

        void Watch()
        {
            try
            {
                using (var mic = Registry.CurrentUser.OpenSubKey(Base + "microphone"))
                using (var cam = Registry.CurrentUser.OpenSubKey(Base + "webcam"))
                using (var micEvt = new AutoResetEvent(false))
                using (var camEvt = new AutoResetEvent(false))
                {
                    string last = null;
                    bool armMic = true, armCam = true;
                    var waits = new WaitHandle[] { micEvt, camEvt };
                    while (true)
                    {
                        if (armMic && mic != null) Native.RegNotifyChangeKeyValue(mic.Handle, true, 0x5, micEvt.SafeWaitHandle, true);
                        if (armCam && cam != null) Native.RegNotifyChangeKeyValue(cam.Handle, true, 0x5, camEvt.SafeWaitHandle, true);
                        armMic = armCam = false;
                        Procs procs = null;
                        var m = Users("microphone", ref procs);
                        var c = Users("webcam", ref procs);
                        string sig = string.Join("|", m) + "#" + string.Join("|", c);
                        if (sig != last)
                        {
                            last = sig;
                            var h = Changed;
                            if (h != null) h(m, c);
                        }
                        // Crashed apps leave no registry trace
                        int hit = WaitHandle.WaitAny(waits, m.Length + c.Length > 0 ? 3000 : Timeout.Infinite);
                        if (hit == WaitHandle.WaitTimeout) continue;
                        if (hit == 0) armMic = true; else armCam = true;
                        Thread.Sleep(250);
                    }
                }
            }
            catch (Exception ex) { Log.Write("privacy: " + ex.Message); }
        }

        static string[] Users(string capability, ref Procs procs)
        {
            var names = new List<string>();
            Collect(Base + capability, true, names, ref procs);
            Collect(Base + capability + @"\NonPackaged", false, names, ref procs);
            return names.ToArray();
        }

        static void Collect(string path, bool packaged, List<string> names, ref Procs procs)
        {
            using (var root = Registry.CurrentUser.OpenSubKey(path))
            {
                if (root == null) return;
                foreach (string sub in root.GetSubKeyNames())
                {
                    if (sub == "NonPackaged") continue;
                    // Dictation is Tepi itself: not a call
                    if (!packaged && string.Equals(sub.Replace('#', '\\'), SelfPath, StringComparison.OrdinalIgnoreCase)) continue;
                    using (var k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        object start = k.GetValue("LastUsedTimeStart"), stop = k.GetValue("LastUsedTimeStop");
                        if (start == null || stop == null) continue;
                        try
                        {
                            long since = Convert.ToInt64(start);
                            if (since > 0 && Convert.ToInt64(stop) == 0)
                            {
                                if (procs == null) procs = Procs.Take();
                                if (!procs.Holds(sub, packaged, since)) continue;
                                string n = Friendly(sub);
                                if (!names.Contains(n)) names.Add(n);
                            }
                        }
                        catch (InvalidCastException) { }
                    }
                }
            }
        }

        static readonly string SelfPath = System.Reflection.Assembly.GetEntryAssembly().Location;

        static string Friendly(string key)
        {
            string n = key;
            int hash = n.LastIndexOf('#');
            if (hash >= 0) n = n.Substring(hash + 1);
            else
            {
                int us = n.IndexOf('_');
                if (us > 0) n = n.Substring(0, us);
                n = n.Substring(n.LastIndexOf('.') + 1);
            }
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n.Substring(1) : n;
        }
    }
}
