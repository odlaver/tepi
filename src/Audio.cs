using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Default output volume with change callbacks
    partial class VolumeWatcher : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public event Action<float, bool, bool> Changed; // level, muted, first reading
        public event Action DeviceChanged;

        readonly Queue<Action> work = new Queue<Action>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        IMMDeviceEnumerator enumerator;
        IAudioEndpointVolume endpoint;
        Guid ctx = Guid.NewGuid();

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "volume" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void Set(float level) { Do(delegate { if (endpoint != null) endpoint.SetMasterVolumeLevelScalar(Ease.Clamp01(level), ref ctx); }); }

        void Do(Action a)
        {
            lock (work) work.Enqueue(a);
            signal.Set();
        }

        void Run()
        {
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                enumerator.RegisterEndpointNotificationCallback(this);
                Attach();
                PublishDevices();
            }
            catch (Exception ex) { Log.Write("volume: " + ex.Message); return; }
            while (true)
            {
                signal.WaitOne();
                while (true)
                {
                    Action a;
                    lock (work)
                    {
                        if (work.Count == 0) break;
                        a = work.Dequeue();
                    }
                    try { a(); } catch (Exception ex) { Log.Write("volume work: " + ex.Message); }
                }
            }
        }

        void Attach()
        {
            if (endpoint != null)
            {
                try { endpoint.UnregisterControlChangeNotify(this); } catch (Exception) { }
                Marshal.ReleaseComObject(endpoint);
                endpoint = null;
            }
            IMMDevice dev;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return;
            endpoint = (IAudioEndpointVolume)o;
            endpoint.RegisterControlChangeNotify(this);
            float level; bool mute;
            endpoint.GetMasterVolumeLevelScalar(out level);
            endpoint.GetMute(out mute);
            var h = Changed;
            if (h != null) h(level, mute, true);
        }

        public int OnNotify(IntPtr data)
        {
            bool muted = Marshal.ReadInt32(data, 16) != 0;
            float level = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(data, 20)), 0);
            var h = Changed;
            if (h != null) h(level, muted, false);
            return 0;
        }

        // Multimedia role: the one Attach reads
        public int OnDefaultDeviceChanged(int flow, int role, string id)
        {
            if (flow != 0 || role != 1) return 0;
            Do(delegate
            {
                Attach();
                var h = DeviceChanged;
                if (h != null) h();
                AnnounceDefault(id);
                PublishDevices();
            });
            return 0;
        }

        public int OnDeviceStateChanged(string id, int state) { Do(PublishDevices); return 0; }
        public int OnDeviceAdded(string id) { Do(PublishDevices); return 0; }
        public int OnDeviceRemoved(string id) { Do(PublishDevices); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }
    }

    // Live spectrum: WASAPI loopback + FFT
    class Spectrum
    {
        public const int Bands = 16;
        const int N = 2048;

        readonly float[] bands = new float[Bands];
        readonly float[] ring = new float[N];
        int ringPos;
        volatile bool wanted, restart;
        Thread thread;
        long failedTicks;

        // Failure pauses capture one minute
        public bool Failed { get { long t = Interlocked.Read(ref failedTicks); return t != 0 && DateTime.UtcNow.Ticks - t < TimeSpan.TicksPerMinute; } }

        void Fail() { Interlocked.Exchange(ref failedTicks, DateTime.UtcNow.Ticks); }

        readonly object gate = new object();

        public void SetActive(bool on)
        {
            lock (gate)
            {
                wanted = on;
                if (on && thread == null) Spawn();
            }
        }

        void Spawn()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "spectrum" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public bool Active { get { return thread != null; } }

        public void Restart() { restart = true; Interlocked.Exchange(ref failedTicks, 0); }

        public void CopyBands(float[] dst)
        {
            lock (bands) Array.Copy(bands, dst, Math.Min(dst.Length, Bands));
        }

        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

        void Run()
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            int channels = 2, rate = 48000;
            bool isFloat = true;
            var re = new float[N];
            var im = new float[N];
            DateTime nextFft = DateTime.UtcNow, heardUtc = DateTime.MinValue;
            bool cleared = false;
            int failures = 0;
            try
            {
                while (wanted)
                {
                    if (client == null || restart)
                    {
                        restart = false;
                        Release(ref client, ref capture);
                        if (!Open(out client, out capture, out channels, out rate, out isFloat))
                        {
                            if (++failures > 3) { Fail(); break; }
                            Thread.Sleep(1000);
                            continue;
                        }
                        failures = 0;
                    }
                    uint packet;
                    if (capture.GetNextPacketSize(out packet) != 0) { restart = true; continue; }
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong dp, qp;
                        if (capture.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;
                        bool silent = (flags & 2) != 0;
                        Feed(data, (int)frames, channels, isFloat, silent);
                        if (!silent && frames > 0) heardUtc = DateTime.UtcNow;
                        capture.ReleaseBuffer(frames);
                        if (capture.GetNextPacketSize(out packet) != 0) break;
                    }
                    var now = DateTime.UtcNow;
                    if (now >= nextFft)
                    {
                        nextFft = now.AddMilliseconds(15);
                        // Silent window: zero bands, no FFT
                        if ((now - heardUtc).TotalMilliseconds < 80) { Analyse(re, im, rate); cleared = false; }
                        else if (!cleared) { lock (bands) Array.Clear(bands, 0, Bands); cleared = true; }
                    }
                    // One pass per 60 fps frame
                    Thread.Sleep(15);
                }
            }
            catch (Exception ex) { Log.Write("spectrum: " + ex.Message); Fail(); }
            finally
            {
                Release(ref client, ref capture);
                lock (bands) Array.Clear(bands, 0, Bands);
                // Switched back on while stopping: run again
                lock (gate)
                {
                    thread = null;
                    if (wanted && !Failed) Spawn();
                }
            }
        }

        bool Open(out IAudioClient client, out IAudioCaptureClient capture, out int channels, out int rate, out bool isFloat)
        {
            client = null; capture = null; channels = 2; rate = 48000; isFloat = true;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return false;
                var iid = typeof(IAudioClient).GUID;
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
                client = (IAudioClient)o;
                IntPtr fmt;
                if (client.GetMixFormat(out fmt) != 0) return false;
                try
                {
                    int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
                    channels = Marshal.ReadInt16(fmt, 2);
                    rate = Marshal.ReadInt32(fmt, 4);
                    int bits = Marshal.ReadInt16(fmt, 14);
                    isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(fmt, 24) == 3);
                    if (!isFloat && bits != 16) return false;
                    if (client.Initialize(0, 0x00020000, 2000000, 0, fmt, IntPtr.Zero) != 0) return false;
                }
                finally { CoTaskMemFree(fmt); }
                var cid = typeof(IAudioCaptureClient).GUID;
                object c;
                if (client.GetService(ref cid, out c) != 0) return false;
                capture = (IAudioCaptureClient)c;
                return client.Start() == 0;
            }
            catch (Exception ex) { Log.Write("loopback open: " + ex.Message); return false; }
        }

        static void Release(ref IAudioClient client, ref IAudioCaptureClient capture)
        {
            try { if (client != null) client.Stop(); } catch (Exception) { }
            if (capture != null) { Marshal.ReleaseComObject(capture); capture = null; }
            if (client != null) { Marshal.ReleaseComObject(client); client = null; }
        }

        float[] floats = new float[0];
        short[] shorts = new short[0];

        // Bulk copy, then mono downmix
        void Feed(IntPtr data, int frames, int channels, bool isFloat, bool silent)
        {
            int n = frames * channels, used = Math.Min(2, channels);
            if (!silent)
            {
                if (isFloat)
                {
                    if (floats.Length < n) floats = new float[n];
                    Marshal.Copy(data, floats, 0, n);
                }
                else
                {
                    if (shorts.Length < n) shorts = new short[n];
                    Marshal.Copy(data, shorts, 0, n);
                }
            }
            for (int f = 0; f < frames; f++)
            {
                float v = 0;
                if (!silent)
                {
                    int i = f * channels;
                    for (int ch = 0; ch < used; ch++) v += isFloat ? floats[i + ch] : shorts[i + ch] / 32768f;
                    v /= used;
                }
                ring[ringPos] = v;
                ringPos = (ringPos + 1) & (N - 1);
            }
        }

        readonly float[] window = Hann();
        readonly float[] next = new float[Bands];
        int[] binLo, binHi;
        int binRate;

        static float[] Hann()
        {
            var w = new float[N];
            for (int i = 0; i < N; i++) w[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (N - 1));
            return w;
        }

        void Analyse(float[] re, float[] im, int rate)
        {
            for (int i = 0; i < N; i++)
            {
                re[i] = ring[(ringPos + i) & (N - 1)] * window[i];
                im[i] = 0;
            }
            Fft(re, im);
            if (binRate != rate)
            {
                binRate = rate;
                binLo = new int[Bands];
                binHi = new int[Bands];
                double lo = Math.Log(55), hi = Math.Log(13000);
                for (int b = 0; b < Bands; b++)
                {
                    double f0 = Math.Exp(lo + (hi - lo) * b / Bands), f1 = Math.Exp(lo + (hi - lo) * (b + 1) / Bands);
                    binLo[b] = Math.Max(1, (int)(f0 * N / rate));
                    binHi[b] = Math.Min(N / 2, Math.Max(binLo[b] + 1, (int)(f1 * N / rate)));
                }
            }
            for (int b = 0; b < Bands; b++)
            {
                float peak = 0;
                for (int i = binLo[b]; i < binHi[b]; i++) peak = Math.Max(peak, re[i] * re[i] + im[i] * im[i]);
                double db = 10 * Math.Log10(peak * 16.0 / ((double)N * N) + 1e-18);
                next[b] = Ease.Clamp01((float)((db + 66) / 54)) * (0.82f + 0.18f * b / Bands);
            }
            lock (bands) Array.Copy(next, bands, Bands);
        }

        static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
