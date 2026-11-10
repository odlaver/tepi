using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Tepi
{
    // Nearby Wi-Fi networks, through Windows' Native Wifi API
    static class Wifi
    {
        public sealed class Net
        {
            public string Ssid = "", Profile = "";
            public int Signal, Auth, Cipher;
            public bool Secured, Connected, Known;
            public Guid Iface;
            // Needs a password Tepi can save
            public bool Personal { get { return Auth == 4 || Auth == 7 || Auth == 9; } }
            public bool Open { get { return !Secured || Auth == 10; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ConnParams
        {
            public int Mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid, Bssids;
            public int BssType;
            public uint Flags;
        }

        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr handle, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr handle, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr handle, ref Guid iface, ref ConnParams p, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr handle, ref Guid iface, uint flags, string xml, string security, bool overwrite, IntPtr reserved, out uint reason);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

        static IntPtr Open()
        {
            uint version;
            IntPtr h;
            return WlanOpenHandle(2, IntPtr.Zero, out version, out h) == 0 ? h : IntPtr.Zero;
        }

        static List<Guid> Interfaces(IntPtr h)
        {
            var list = new List<Guid>();
            IntPtr p;
            if (WlanEnumInterfaces(h, IntPtr.Zero, out p) != 0) return list;
            try
            {
                int n = Marshal.ReadInt32(p);
                // Items: GUID, 256-char name, state
                for (int i = 0; i < n; i++)
                {
                    var b = new byte[16];
                    Marshal.Copy(new IntPtr(p.ToInt64() + 8 + i * 532), b, 0, 16);
                    list.Add(new Guid(b));
                }
            }
            finally { WlanFreeMemory(p); }
            return list;
        }

        // Ask the adapters for a fresh look; results land a few seconds later
        public static void Scan()
        {
            var h = Open();
            if (h == IntPtr.Zero) return;
            try { foreach (var g in Interfaces(h)) { var id = g; WlanScan(h, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }

        // One row per network name, strongest first, connected on top
        public static List<Net> List()
        {
            var seen = new Dictionary<string, Net>();
            var h = Open();
            if (h == IntPtr.Zero) return new List<Net>();
            try
            {
                foreach (var g in Interfaces(h))
                {
                    var id = g;
                    IntPtr p;
                    if (WlanGetAvailableNetworkList(h, ref id, 0, IntPtr.Zero, out p) != 0) continue;
                    try
                    {
                        int n = Marshal.ReadInt32(p);
                        for (int i = 0; i < n; i++)
                        {
                            var e = new IntPtr(p.ToInt64() + 8 + i * 628);
                            int len = Math.Min(32, Marshal.ReadInt32(e, 512));
                            if (len == 0) continue;
                            var raw = new byte[len];
                            Marshal.Copy(new IntPtr(e.ToInt64() + 516), raw, 0, len);
                            int flags = Marshal.ReadInt32(e, 620);
                            var net = new Net
                            {
                                Ssid = Encoding.UTF8.GetString(raw),
                                Profile = Marshal.PtrToStringUni(e) ?? "",
                                Signal = Marshal.ReadInt32(e, 604),
                                Secured = Marshal.ReadInt32(e, 608) != 0,
                                Auth = Marshal.ReadInt32(e, 612),
                                Cipher = Marshal.ReadInt32(e, 616),
                                Connected = (flags & 1) != 0,
                                Known = (flags & 2) != 0,
                                Iface = g,
                            };
                            Net had;
                            if (!seen.TryGetValue(net.Ssid, out had)) { seen[net.Ssid] = net; continue; }
                            // Keep the saved entry; take the best signal and state
                            if (net.Known && !had.Known) { net.Signal = Math.Max(net.Signal, had.Signal); net.Connected |= had.Connected; seen[net.Ssid] = net; }
                            else { had.Signal = Math.Max(had.Signal, net.Signal); had.Connected |= net.Connected; }
                        }
                    }
                    finally { WlanFreeMemory(p); }
                }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
            var list = new List<Net>(seen.Values);
            list.Sort((a, b) => a.Connected != b.Connected ? (a.Connected ? -1 : 1) : a.Known != b.Known ? (a.Known ? -1 : 1) : b.Signal.CompareTo(a.Signal));
            return list;
        }

        // Saved networks join by profile; new ones get a profile first
        public static string Connect(Net net, string password)
        {
            var h = Open();
            if (h == IntPtr.Zero) return "wlan";
            try
            {
                var id = net.Iface;
                string profile = net.Known && net.Profile.Length > 0 ? net.Profile : null;
                if (profile == null)
                {
                    uint reason;
                    int set = WlanSetProfile(h, ref id, 0, ProfileXml(net, password), null, true, IntPtr.Zero, out reason);
                    if (set != 0) return "profile " + set + "/" + reason;
                    profile = net.Ssid;
                }
                var cp = new ConnParams { Mode = 0, Profile = profile, BssType = 1 };
                int r = WlanConnect(h, ref id, ref cp, IntPtr.Zero);
                return r == 0 ? null : "connect " + r;
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }

        public static void Disconnect(Net net)
        {
            var h = Open();
            if (h == IntPtr.Zero) return;
            try { var id = net.Iface; WlanDisconnect(h, ref id, IntPtr.Zero); }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }

        static string ProfileXml(Net net, string password)
        {
            string auth, enc;
            if (net.Auth == 10) { auth = "OWE"; enc = "AES"; }
            else if (!net.Secured) { auth = "open"; enc = "none"; }
            else if (net.Auth == 9) { auth = "WPA3SAE"; enc = "AES"; }
            else if (net.Auth == 4) { auth = "WPAPSK"; enc = net.Cipher == 2 ? "TKIP" : "AES"; }
            else { auth = "WPA2PSK"; enc = net.Cipher == 2 ? "TKIP" : "AES"; }
            var hex = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(net.Ssid)) hex.Append(b.ToString("X2"));
            string name = SecurityElement.Escape(net.Ssid);
            var x = new StringBuilder();
            x.Append("<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">");
            x.Append("<name>").Append(name).Append("</name><SSIDConfig><SSID><hex>").Append(hex).Append("</hex><name>").Append(name).Append("</name></SSID></SSIDConfig>");
            x.Append("<connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security><authEncryption>");
            x.Append("<authentication>").Append(auth).Append("</authentication><encryption>").Append(enc).Append("</encryption><useOneX>false</useOneX></authEncryption>");
            if (password != null && net.Secured && net.Auth != 10)
                x.Append("<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>").Append(SecurityElement.Escape(password)).Append("</keyMaterial></sharedKey>");
            x.Append("</security></MSM></WLANProfile>");
            return x.ToString();
        }
    }
}
