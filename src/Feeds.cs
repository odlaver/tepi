using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace Tepi
{
    class NetState
    {
        public bool Online, Wifi, Any;
        public string Name = "";

        public string Sig { get { return Any + "|" + Online + "|" + Wifi + "|" + Name; } }
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
        }

        void Check()
        {
            var now = Read();
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
                s.Wifi = p.IsWlanConnectionProfile;
                s.Name = s.Wifi && p.WlanConnectionProfileDetails != null ? p.WlanConnectionProfileDetails.GetConnectedSsid() : (p.ProfileName ?? "");
            }
            catch (Exception ex) { Log.Write("net: " + ex.Message); }
            return s;
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

        void Run()
        {
            try
            {
                using (var mic = Registry.CurrentUser.OpenSubKey(Base + "microphone"))
                using (var cam = Registry.CurrentUser.OpenSubKey(Base + "webcam"))
                using (var micEvt = new AutoResetEvent(false))
                using (var camEvt = new AutoResetEvent(false))
                {
                    string last = null;
                    while (true)
                    {
                        if (mic != null) Native.RegNotifyChangeKeyValue(mic.Handle, true, 0x5, micEvt.SafeWaitHandle, true);
                        if (cam != null) Native.RegNotifyChangeKeyValue(cam.Handle, true, 0x5, camEvt.SafeWaitHandle, true);
                        var m = Users("microphone");
                        var c = Users("webcam");
                        string sig = string.Join("|", m) + "#" + string.Join("|", c);
                        if (sig != last)
                        {
                            last = sig;
                            var h = Changed;
                            if (h != null) h(m, c);
                        }
                        WaitHandle.WaitAny(new WaitHandle[] { micEvt, camEvt });
                        Thread.Sleep(250);
                    }
                }
            }
            catch (Exception ex) { Log.Write("privacy: " + ex.Message); }
        }

        static string[] Users(string capability)
        {
            var names = new List<string>();
            Collect(Base + capability, names);
            Collect(Base + capability + @"\NonPackaged", names);
            return names.ToArray();
        }

        static void Collect(string path, List<string> names)
        {
            using (var root = Registry.CurrentUser.OpenSubKey(path))
            {
                if (root == null) return;
                foreach (string sub in root.GetSubKeyNames())
                {
                    if (sub == "NonPackaged") continue;
                    using (var k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        object start = k.GetValue("LastUsedTimeStart"), stop = k.GetValue("LastUsedTimeStop");
                        if (start == null || stop == null) continue;
                        try
                        {
                            if (Convert.ToInt64(start) > 0 && Convert.ToInt64(stop) == 0)
                            {
                                string n = Friendly(sub);
                                if (!names.Contains(n)) names.Add(n);
                            }
                        }
                        catch (InvalidCastException) { }
                    }
                }
            }
        }

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
