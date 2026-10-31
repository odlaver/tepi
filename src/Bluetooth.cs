using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Battery that Bluetooth devices report to Windows
    static class BtBattery
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DevInfo { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropKey { public Guid fmtid; public int pid; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(IntPtr cls, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref DevInfo data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceProperty(IntPtr set, ref DevInfo data, ref PropKey key, out int type, byte[] buffer, int size, out int required, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static readonly Guid BatteryKey = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
        static readonly Guid NameKey = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        // Friendly name to percent
        public static Dictionary<string, int> Read()
        {
            var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return found;
            try
            {
                var buf = new byte[512];
                for (int i = 0; ; i++)
                {
                    var d = new DevInfo { cbSize = Marshal.SizeOf(typeof(DevInfo)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref d)) break;
                    var key = new PropKey { fmtid = BatteryKey, pid = 2 };
                    int type, need;
                    if (!SetupDiGetDeviceProperty(set, ref d, ref key, out type, buf, buf.Length, out need, 0) || type != 0x3) continue;
                    int pct = buf[0];
                    key = new PropKey { fmtid = NameKey, pid = 14 };
                    if (!SetupDiGetDeviceProperty(set, ref d, ref key, out type, buf, buf.Length, out need, 0) || type != 0x12) continue;
                    string name = System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, need - 2)).Trim();
                    if (name.Length > 0 && pct <= 100) found[name] = pct;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return found;
        }
    }

    partial class Island
    {
        Dictionary<string, int> btBattery = new Dictionary<string, int>();
        DateTime btAskedUtc = DateTime.MinValue;
        string btWarned;
        bool btBusy;

        void TickBluetooth(bool now)
        {
            if (btBusy || (!now && (DateTime.UtcNow - btAskedUtc).TotalMinutes < 5)) return;
            btAskedUtc = DateTime.UtcNow;
            btBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, int> got;
                try { got = BtBattery.Read(); }
                catch (Exception ex) { Log.Write("bt battery: " + ex.GetType().Name); got = new Dictionary<string, int>(); }
                Post(() => { btBusy = false; btBattery = got; OnBluetooth(); });
            });
        }

        // Endpoint "Headphones (Buds2)" holds the device name
        int BatteryFor(string endpoint, out string device)
        {
            device = null;
            if (string.IsNullOrEmpty(endpoint)) return -1;
            foreach (var kv in btBattery)
                if (endpoint.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) { device = kv.Key; return kv.Value; }
            return -1;
        }

        void OnBluetooth()
        {
            var dev = DefaultDevice();
            string name;
            int pct = BatteryFor(dev != null ? dev.Name : null, out name);
            if (pct < 0) return;
            if (pct > 20) { btWarned = null; return; }
            if (pct > 15 || btWarned == name) return;
            btWarned = name;
            ShowInfo(Ink.IcoBluetooth, Lang.F("Baterai {0} {1}%", name, pct), "", true);
        }

        // "· 70%" after an output, when known
        string BatteryTail(string endpoint)
        {
            string name;
            int pct = BatteryFor(endpoint, out name);
            return pct < 0 ? "" : "  ·  " + Ink.Percent(pct);
        }
    }
}
