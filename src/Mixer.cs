using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Tepi
{
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr group, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr group, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator list);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // One app in the mixer
    class AppVolume
    {
        public string Name = "";
        public float Level;
        public bool Muted;
    }

    // Per-app volume, default output only
    partial class VolumeWatcher
    {
        public event Action<List<AppVolume>> AppsChanged;
        // Name to its sessions; worker thread only
        readonly Dictionary<string, List<ISimpleAudioVolume>> appRefs = new Dictionary<string, List<ISimpleAudioVolume>>();

        public void RefreshApps() { Do(ReadApps); }

        void ReadApps()
        {
            foreach (var list in appRefs.Values) foreach (var v in list) Marshal.ReleaseComObject(v);
            appRefs.Clear();
            var apps = new List<AppVolume>();
            IMMDevice dev;
            if (enumerator == null || enumerator.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) { Publish(apps); return; }
            var iid = typeof(IAudioSessionManager2).GUID;
            object o;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) { Publish(apps); return; }
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator list2;
            int count;
            if (mgr.GetSessionEnumerator(out list2) == 0 && list2.GetCount(out count) == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 s;
                    if (list2.GetSession(i, out s) != 0 || s == null) continue;
                    int state;
                    uint pid;
                    // Expired sessions and system sounds stay out
                    if (s.GetState(out state) != 0 || state == 2 || s.IsSystemSoundsSession() == 0 || s.GetProcessId(out pid) != 0 || pid == 0) { Marshal.ReleaseComObject(s); continue; }
                    var vol = (ISimpleAudioVolume)s;
                    string name = AppName(pid);
                    List<ISimpleAudioVolume> refs;
                    if (!appRefs.TryGetValue(name, out refs))
                    {
                        refs = new List<ISimpleAudioVolume>();
                        appRefs[name] = refs;
                        float level; bool mute;
                        vol.GetMasterVolume(out level);
                        vol.GetMute(out mute);
                        apps.Add(new AppVolume { Name = name, Level = level, Muted = mute });
                    }
                    refs.Add(vol);
                }
                Marshal.ReleaseComObject(list2);
            }
            Marshal.ReleaseComObject(mgr);
            Publish(apps);
        }

        void Publish(List<AppVolume> apps)
        {
            apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            var h = AppsChanged;
            if (h != null) h(apps);
        }

        // Friendly name, else the process name
        static string AppName(uint pid)
        {
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    string d = null;
                    try { d = p.MainModule.FileVersionInfo.FileDescription; } catch (Exception) { }
                    return string.IsNullOrWhiteSpace(d) ? p.ProcessName : d.Trim();
                }
            }
            catch (Exception) { return "PID " + pid; }
        }

        // Ducked sessions and their levels; worker only
        readonly List<KeyValuePair<ISimpleAudioVolume, float>> ducked = new List<KeyValuePair<ISimpleAudioVolume, float>>();
        const float DuckTo = 0.25f;

        // Lower other apps while a reminder speaks
        public void Duck(bool on)
        {
            Do(delegate
            {
                if (on) DuckAll(); else Unduck();
            });
        }

        void DuckAll()
        {
            if (ducked.Count > 0) return;
            IMMDevice dev;
            if (enumerator == null || enumerator.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return;
            var iid = typeof(IAudioSessionManager2).GUID;
            object o;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return;
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator list;
            int count;
            uint self = (uint)Process.GetCurrentProcess().Id;
            if (mgr.GetSessionEnumerator(out list) == 0 && list.GetCount(out count) == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 s;
                    if (list.GetSession(i, out s) != 0 || s == null) continue;
                    int state;
                    uint pid;
                    // Only sessions playing now, never Tepi
                    if (s.GetState(out state) != 0 || state != 1 || s.IsSystemSoundsSession() == 0 || s.GetProcessId(out pid) != 0 || pid == self) { Marshal.ReleaseComObject(s); continue; }
                    var vol = (ISimpleAudioVolume)s;
                    float level;
                    if (vol.GetMasterVolume(out level) != 0) { Marshal.ReleaseComObject(s); continue; }
                    vol.SetMasterVolume(level * DuckTo, ref ctx);
                    ducked.Add(new KeyValuePair<ISimpleAudioVolume, float>(vol, level));
                }
                Marshal.ReleaseComObject(list);
            }
            Marshal.ReleaseComObject(mgr);
        }

        void Unduck()
        {
            foreach (var d in ducked)
            {
                float now;
                // User moved it meanwhile: leave theirs
                if (d.Key.GetMasterVolume(out now) == 0 && Math.Abs(now - d.Value * DuckTo) < 0.02f) d.Key.SetMasterVolume(d.Value, ref ctx);
                Marshal.ReleaseComObject(d.Key);
            }
            ducked.Clear();
        }

        public void SetApp(string name, float level, bool mute)
        {
            Do(delegate
            {
                List<ISimpleAudioVolume> refs;
                if (!appRefs.TryGetValue(name, out refs)) return;
                foreach (var v in refs) { v.SetMasterVolume(Ease.Clamp01(level), ref ctx); v.SetMute(mute, ref ctx); }
            });
        }
    }

    partial class Island
    {
        const float MixRowH = 44, MixGap = 6;
        List<AppVolume> apps = new List<AppVolume>();
        readonly List<string> mixIds = new List<string>(), muteIds = new List<string>();
        readonly List<Action<float>> mixSets = new List<Action<float>>();
        readonly List<Action> mixMutes = new List<Action>();
        int appsAskedTick;

        void OnApps(List<AppVolume> list)
        {
            apps = list;
            // Delegates once per list, not per frame
            mixIds.Clear(); muteIds.Clear(); mixSets.Clear(); mixMutes.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                mixIds.Add("mix" + i);
                muteIds.Add("mixMute" + i);
                mixSets.Add(f => { a.Level = f; a.Muted = false; volume.SetApp(a.Name, f, false); Kick(); });
                mixMutes.Add(delegate { a.Muted = !a.Muted; volume.SetApp(a.Name, a.Level, a.Muted); Kick(); });
            }
            if (expanded && page == PageControl) Kick();
        }

        // Live while the sound list is open
        void TickApps()
        {
            if (!devicesOpen || !expanded || page != PageControl) return;
            if (Environment.TickCount - appsAskedTick < 2000) return;
            appsAskedTick = Environment.TickCount;
            volume.RefreshApps();
        }

        // Rows that fit under the device list
        int MixRows()
        {
            float used = PanelTop + 2 + CtlRowH + Ink.ModuleGap + DeviceListH() + Ink.ModuleGap + Ink.ModuleInset;
            return Math.Max(0, Math.Min(apps.Count, (int)((MaxH - used + MixGap) / (MixRowH + MixGap))));
        }

        float MixerH() { int n = MixRows(); return n == 0 ? 0 : Ink.ModuleGap + n * MixRowH + (n - 1) * MixGap; }

        void DrawMixer(float x0, float y, float full)
        {
            int n = MixRows();
            for (int i = 0; i < n && i < mixIds.Count; i++)
            {
                var a = apps[i];
                var r = new RectangleF(x0, y + i * (MixRowH + MixGap), full, MixRowH);
                BigSlider(r, mixIds[i], a.Muted ? 0 : a.Level, a.Muted ? Ink.IcoMute : Ink.IcoVol3, a.Name,
                          a.Muted ? Lang.T("Bisu") : Ink.Percent((int)Math.Round(a.Level * 100)), mixSets[i], null, muteIds[i], mixMutes[i]);
            }
        }
    }
}
