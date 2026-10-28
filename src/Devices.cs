using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Radios;

namespace Tepi
{
    // One output endpoint
    class AudioDevice
    {
        public string Id = "", Name = "";
        // EndpointFormFactor: 1 speakers, 3/5 headphones, 9 HDMI
        public int Form;
    }

    // Output list and switching, volume thread
    partial class VolumeWatcher
    {
        public event Action<List<AudioDevice>, string> DevicesChanged; // list, default id
        public event Action<AudioDevice> DefaultChanged;
        int pendingLevel = -1;

        // Slider drags: latest value wins
        public void SetLatest(float level)
        {
            bool queue = Interlocked.Exchange(ref pendingLevel, (int)Math.Round(Ease.Clamp01(level) * 1000)) < 0;
            if (!queue) return;
            Do(delegate
            {
                int v = Interlocked.Exchange(ref pendingLevel, -1);
                if (v >= 0 && endpoint != null) endpoint.SetMasterVolumeLevelScalar(v / 1000f, ref ctx);
            });
        }

        public void SetMute(bool mute) { Do(delegate { if (endpoint != null) endpoint.SetMute(mute, ref ctx); }); }

        public void RefreshDevices() { Do(PublishDevices); }

        // Console and multimedia roles, like Windows
        public void SetDefault(string id)
        {
            Do(delegate
            {
                try
                {
                    var policy = (IPolicyConfig)new PolicyConfigClient();
                    policy.SetDefaultEndpoint(id, 0);
                    policy.SetDefaultEndpoint(id, 1);
                    Marshal.ReleaseComObject(policy);
                }
                catch (Exception ex) { Log.Write("audio default: " + ex.Message); }
            });
        }

        void PublishDevices()
        {
            var list = new List<AudioDevice>();
            string def = "";
            try
            {
                IntPtr raw;
                if (enumerator.EnumAudioEndpoints(0, 1, out raw) == 0 && raw != IntPtr.Zero)
                {
                    var col = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(raw);
                    Marshal.Release(raw);
                    int n;
                    col.GetCount(out n);
                    for (int i = 0; i < n && i < 16; i++)
                    {
                        IMMDevice dev;
                        if (col.Item(i, out dev) != 0 || dev == null) continue;
                        var d = Describe(dev);
                        if (d != null) list.Add(d);
                    }
                    Marshal.ReleaseComObject(col);
                }
                IMMDevice cur;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out cur) == 0 && cur != null) cur.GetId(out def);
            }
            catch (Exception ex) { Log.Write("audio devices: " + ex.Message); }
            var h = DevicesChanged;
            if (h != null) h(list, def ?? "");
        }

        static readonly PropertyKey FriendlyName = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        static readonly PropertyKey FormFactor = new PropertyKey { fmtid = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), pid = 0 };

        static AudioDevice Describe(IMMDevice dev)
        {
            string id;
            if (dev.GetId(out id) != 0) return null;
            var d = new AudioDevice { Id = id ?? "" };
            IntPtr storeRaw;
            if (dev.OpenPropertyStore(0, out storeRaw) != 0 || storeRaw == IntPtr.Zero) return d;
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storeRaw);
            Marshal.Release(storeRaw);
            try
            {
                var key = FriendlyName;
                PropVariant v;
                if (store.GetValue(ref key, out v) == 0)
                {
                    if (v.vt == 31 && v.ptr != IntPtr.Zero) d.Name = Marshal.PtrToStringUni(v.ptr) ?? "";
                    PropVariantClear(ref v);
                }
                key = FormFactor;
                if (store.GetValue(ref key, out v) == 0)
                {
                    if (v.vt == 19) d.Form = v.intValue;
                    PropVariantClear(ref v);
                }
            }
            finally { Marshal.ReleaseComObject(store); }
            return d;
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

        // New default's name for the toast
        void AnnounceDefault(string id)
        {
            var h = DefaultChanged;
            if (h == null || string.IsNullOrEmpty(id)) return;
            IMMDevice dev;
            if (enumerator.GetDevice(id, out dev) != 0 || dev == null) return;
            var d = Describe(dev);
            if (d != null) h(d);
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public int intValue;
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // Undocumented, stable since Windows 7
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr period, IntPtr min);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    // Panel brightness over WMI; latest wins
    class BrightnessService
    {
        public event Action<int, bool> Changed; // level, from Tepi itself
        public volatile bool Supported;
        public volatile int Level = -1;
        int want = -1;
        long ownTicks;
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        ManagementEventWatcher watcher;

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "brightness", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void Set(int level)
        {
            Interlocked.Exchange(ref want, Math.Max(0, Math.Min(100, level)));
            signal.Set();
        }

        void Run()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                    foreach (ManagementObject o in s.Get()) { Level = Convert.ToInt32(o["CurrentBrightness"]); Supported = true; break; }
            }
            catch (Exception ex) { Log.Write("brightness: " + ex.Message); }
            if (!Supported) return;
            Raise(Level, true);
            try
            {
                watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                watcher.EventArrived += (s, e) =>
                {
                    int b = Convert.ToInt32(e.NewEvent["Brightness"]);
                    Level = b;
                    bool own = DateTime.UtcNow.Ticks - Interlocked.Read(ref ownTicks) < TimeSpan.TicksPerSecond;
                    Raise(b, own);
                };
                watcher.Start();
            }
            catch (Exception ex) { Log.Write("brightness events: " + ex.Message); }
            while (true)
            {
                signal.WaitOne();
                int w = Interlocked.Exchange(ref want, -1);
                if (w < 0) continue;
                try
                {
                    Interlocked.Exchange(ref ownTicks, DateTime.UtcNow.Ticks);
                    using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                        foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", new object[] { 1, (byte)w }); break; }
                    Level = w;
                }
                catch (Exception ex) { Log.Write("brightness set: " + ex.Message); }
            }
        }

        void Raise(int level, bool own)
        {
            var h = Changed;
            if (h != null) h(level, own);
        }
    }

    // Wi-Fi and Bluetooth radios, user toggled
    class RadioService
    {
        public event Action Changed;
        public volatile bool HasWifi, HasBluetooth, WifiOn, BluetoothOn, Denied;
        Radio wifi, bluetooth;
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Queue<Action> work = new Queue<Action>();

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "radios", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void Toggle(bool isWifi)
        {
            lock (work) work.Enqueue(delegate
            {
                if (MediaService.Await(Radio.RequestAccessAsync()) != RadioAccessStatus.Allowed) { Denied = true; Raise(); return; }
                var r = isWifi ? wifi : bluetooth;
                if (r == null) return;
                var to = r.State == RadioState.On ? RadioState.Off : RadioState.On;
                if (MediaService.Await(r.SetStateAsync(to)) != RadioAccessStatus.Allowed) Denied = true;
                Read();
            });
            signal.Set();
        }

        void Run()
        {
            try
            {
                foreach (var r in MediaService.Await(Radio.GetRadiosAsync()))
                {
                    if (r.Kind == RadioKind.WiFi && wifi == null) wifi = r;
                    else if (r.Kind == RadioKind.Bluetooth && bluetooth == null) bluetooth = r;
                    else continue;
                    r.StateChanged += delegate { Read(); };
                }
                Read();
            }
            catch (Exception ex) { Log.Write("radios: " + ex.Message); }
            while (true)
            {
                signal.WaitOne();
                while (true)
                {
                    Action a;
                    lock (work) { if (work.Count == 0) break; a = work.Dequeue(); }
                    try { a(); } catch (Exception ex) { Log.Write("radio toggle: " + ex.Message); }
                }
            }
        }

        void Read()
        {
            HasWifi = wifi != null;
            HasBluetooth = bluetooth != null;
            WifiOn = wifi != null && wifi.State == RadioState.On;
            BluetoothOn = bluetooth != null && bluetooth.State == RadioState.On;
            Raise();
        }

        void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }
    }
}
