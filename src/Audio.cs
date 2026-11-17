using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Default output volume with change callbacks
    partial class VolumeWatcher : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public event Action<float, bool, bool> Changed; // level, muted, first reading
        public event Action DeviceChanged;

        // Core Audio work stays on one MTA thread
        readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
        IMMDeviceEnumerator deviceEnum;
        IAudioEndpointVolume output;
        Guid ctx = Guid.NewGuid();

        // Render flow, multimedia role
        const int Render = 0, Capture = 1, Multimedia = 1, AllContexts = 23;

        public void Start()
        {
            var worker = new Thread(Prof.Wrap(Serve)) { IsBackground = true, Name = "volume" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public void Set(float level)
        {
            float v = Ease.Clamp01(level);
            Do(delegate { if (output != null) output.SetMasterVolumeLevelScalar(v, ref ctx); });
        }

        void Do(Action job) { jobs.Add(job); }

        void Serve()
        {
            try
            {
                deviceEnum = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                deviceEnum.RegisterEndpointNotificationCallback(this);
                Attach();
                AttachMic();
                PublishDevices();
            }
            catch (Exception ex) { Log.Write("volume: " + ex.Message); return; }
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception ex) { Log.Write("volume work: " + ex.Message); }
            }
        }

        // Follow the default output device
        void Attach()
        {
            Detach();
            IMMDevice dev;
            if (deviceEnum.GetDefaultAudioEndpoint(Render, Multimedia, out dev) != 0 || dev == null) return;
            object activated;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, AllContexts, IntPtr.Zero, out activated) != 0) return;
            output = (IAudioEndpointVolume)activated;
            output.RegisterControlChangeNotify(this);
            float level;
            bool mute;
            output.GetMasterVolumeLevelScalar(out level);
            output.GetMute(out mute);
            Report(level, mute, true);
        }

        void Detach()
        {
            if (output == null) return;
            try { output.UnregisterControlChangeNotify(this); }
            catch (Exception) { }
            Marshal.ReleaseComObject(output);
            output = null;
        }

        void Report(float level, bool mute, bool first)
        {
            var handler = Changed;
            if (handler != null) handler(level, mute, first);
        }

        // AUDIO_VOLUME_NOTIFICATION_DATA head
        [StructLayout(LayoutKind.Sequential)]
        struct VolumeNote
        {
            public Guid EventContext;
            public int Muted;
            public float MasterVolume;
        }

        public int OnNotify(IntPtr data)
        {
            var note = (VolumeNote)Marshal.PtrToStructure(data, typeof(VolumeNote));
            Report(note.MasterVolume, note.Muted != 0, false);
            return 0;
        }

        // Multimedia role: the one Attach reads
        public int OnDefaultDeviceChanged(int flow, int role, string id)
        {
            if (flow == Capture) Do(AttachMic);
            else if (flow == Render && role == Multimedia) Do(() => SwitchOutput(id));
            return 0;
        }

        void SwitchOutput(string id)
        {
            Attach();
            var handler = DeviceChanged;
            if (handler != null) handler();
            AnnounceDefault(id);
            PublishDevices();
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
        const int N = 2048, Mask = N - 1;

        readonly float[] levels = new float[Bands];
        // Last N mono samples, oldest at head
        readonly float[] history = new float[N];
        int head;
        volatile bool want, reopen;
        Thread worker;
        long failedTicks;

        // Failure pauses capture one minute
        public bool Failed
        {
            get
            {
                long at = Interlocked.Read(ref failedTicks);
                return at != 0 && DateTime.UtcNow.Ticks - at < TimeSpan.TicksPerMinute;
            }
        }

        void Fail() { Interlocked.Exchange(ref failedTicks, DateTime.UtcNow.Ticks); }

        readonly object gate = new object();

        public void SetActive(bool on)
        {
            lock (gate)
            {
                want = on;
                if (on && worker == null) Launch();
            }
        }

        void Launch()
        {
            worker = new Thread(Prof.Wrap(Capture)) { IsBackground = true, Name = "spectrum" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public bool Active { get { return worker != null; } }

        public void Restart()
        {
            reopen = true;
            Interlocked.Exchange(ref failedTicks, 0);
        }

        public void CopyBands(float[] dst)
        {
            int n = dst.Length < Bands ? dst.Length : Bands;
            lock (levels)
                for (int i = 0; i < n; i++) dst[i] = levels[i];
        }

        void Silence() { lock (levels) Array.Clear(levels, 0, Bands); }

        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

        // Loopback stream and its mix format
        class Tap
        {
            public IAudioClient Client;
            public IAudioCaptureClient Reader;
            public int Channels = 2, Rate = 48000;
            public bool Float = true;

            public void Close()
            {
                try { if (Client != null) Client.Stop(); }
                catch (Exception) { }
                if (Reader != null) Marshal.ReleaseComObject(Reader);
                if (Client != null) Marshal.ReleaseComObject(Client);
                Reader = null;
                Client = null;
            }
        }

        void Capture()
        {
            Tap tap = null;
            var re = new float[N];
            var im = new float[N];
            DateTime fftDueUtc = DateTime.UtcNow, soundUtc = DateTime.MinValue;
            bool zeroed = false;
            int misses = 0;
            try
            {
                while (want)
                {
                    if (tap == null || reopen)
                    {
                        reopen = false;
                        if (tap != null) tap.Close();
                        tap = new Tap();
                        if (!Open(tap))
                        {
                            // Half-open client is kept, as before
                            if (tap.Client == null) tap = null;
                            if (++misses > 3) { Fail(); break; }
                            Thread.Sleep(1000);
                            continue;
                        }
                        misses = 0;
                    }
                    if (!Pump(tap, ref soundUtc)) { reopen = true; continue; }
                    var now = DateTime.UtcNow;
                    if (now >= fftDueUtc)
                    {
                        fftDueUtc = now.AddMilliseconds(42);
                        // Silent window: zero bands, no FFT
                        bool hearing = (now - soundUtc).TotalMilliseconds < 80;
                        if (hearing) Analyse(re, im, tap.Rate);
                        else if (!zeroed) Silence();
                        zeroed = !hearing;
                    }
                    // Wake once per FFT; buffer holds 200 ms
                    Thread.Sleep(Math.Max(5, (int)(fftDueUtc - DateTime.UtcNow).TotalMilliseconds));
                }
            }
            catch (Exception ex) { Log.Write("spectrum: " + ex.Message); Fail(); }
            finally
            {
                if (tap != null) tap.Close();
                Silence();
                // Switched back on while stopping: run again
                lock (gate)
                {
                    worker = null;
                    if (want && !Failed) Launch();
                }
            }
        }

        // Drain queued packets; false if the stream broke
        bool Pump(Tap tap, ref DateTime soundUtc)
        {
            uint waiting;
            if (tap.Reader.GetNextPacketSize(out waiting) != 0) return false;
            for (; waiting > 0; )
            {
                IntPtr data;
                uint frames, flags;
                ulong devicePos, qpcPos;
                if (tap.Reader.GetBuffer(out data, out frames, out flags, out devicePos, out qpcPos) != 0) break;
                // AUDCLNT_BUFFERFLAGS_SILENT
                bool silent = (flags & 2) != 0;
                Mix(data, (int)frames, tap, silent);
                if (!silent && frames > 0) soundUtc = DateTime.UtcNow;
                tap.Reader.ReleaseBuffer(frames);
                if (tap.Reader.GetNextPacketSize(out waiting) != 0) break;
            }
            return true;
        }

        // Default output in loopback; false on any failure
        bool Open(Tap tap)
        {
            try
            {
                var devs = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice dev;
                if (devs.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return false;
                object activated;
                var clientId = typeof(IAudioClient).GUID;
                if (dev.Activate(ref clientId, 23, IntPtr.Zero, out activated) != 0) return false;
                tap.Client = (IAudioClient)activated;
                IntPtr fmt;
                if (tap.Client.GetMixFormat(out fmt) != 0) return false;
                try
                {
                    if (!ReadFormat(fmt, tap)) return false;
                    // Shared loopback, 200 ms buffer
                    const int Loopback = 0x00020000;
                    if (tap.Client.Initialize(0, Loopback, 2000000, 0, fmt, IntPtr.Zero) != 0) return false;
                }
                finally { CoTaskMemFree(fmt); }
                var readerId = typeof(IAudioCaptureClient).GUID;
                object reader;
                if (tap.Client.GetService(ref readerId, out reader) != 0) return false;
                tap.Reader = (IAudioCaptureClient)reader;
                return tap.Client.Start() == 0;
            }
            catch (Exception ex)
            {
                Log.Write("loopback open: " + ex.Message);
                return false;
            }
        }

        // WAVEFORMATEX(TENSIBLE): float, or 16-bit PCM
        static bool ReadFormat(IntPtr fmt, Tap tap)
        {
            const int IeeeFloat = 3, Extensible = 0xFFFE;
            int tag = (ushort)Marshal.ReadInt16(fmt, 0);
            tap.Channels = Marshal.ReadInt16(fmt, 2);
            tap.Rate = Marshal.ReadInt32(fmt, 4);
            int bits = Marshal.ReadInt16(fmt, 14);
            // Extensible: SubFormat GUID's first field at 24
            tap.Float = tag == IeeeFloat || (tag == Extensible && Marshal.ReadInt32(fmt, 24) == IeeeFloat);
            return tap.Float || bits == 16;
        }

        float[] floats = new float[0];
        short[] shorts = new short[0];

        // Bulk copy, then first two channels averaged
        void Mix(IntPtr data, int frames, Tap tap, bool silent)
        {
            int stride = tap.Channels, total = frames * stride, take = stride < 2 ? stride : 2;
            if (!silent && tap.Float)
            {
                if (floats.Length < total) floats = new float[total];
                Marshal.Copy(data, floats, 0, total);
            }
            else if (!silent)
            {
                if (shorts.Length < total) shorts = new short[total];
                Marshal.Copy(data, shorts, 0, total);
            }
            for (int f = 0, at = 0; f < frames; f++, at += stride)
            {
                float sum = 0;
                if (!silent)
                {
                    for (int c = 0; c < take; c++) sum += tap.Float ? floats[at + c] : shorts[at + c] / 32768f;
                    sum /= take;
                }
                history[head] = sum;
                if (++head == N) head = 0;
            }
        }

        readonly float[] window = Hann();
        readonly float[] fresh = new float[Bands];
        int[] firstBin, endBin;
        int binsForRate;

        static float[] Hann()
        {
            var w = new float[N];
            for (int i = 0; i < N; i++) w[i] = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (N - 1));
            return w;
        }

        // 55 Hz to 13 kHz, log spaced
        void MapBins(int rate)
        {
            binsForRate = rate;
            firstBin = new int[Bands];
            endBin = new int[Bands];
            double lo = Math.Log(55), span = Math.Log(13000) - lo;
            for (int b = 0; b < Bands; b++)
            {
                double from = Math.Exp(lo + span * b / Bands), to = Math.Exp(lo + span * (b + 1) / Bands);
                firstBin[b] = Math.Max(1, (int)(from * N / rate));
                endBin[b] = Math.Min(N / 2, Math.Max(firstBin[b] + 1, (int)(to * N / rate)));
            }
        }

        void Analyse(float[] re, float[] im, int rate)
        {
            for (int i = 0; i < N; i++)
            {
                re[i] = history[(head + i) & Mask] * window[i];
                im[i] = 0;
            }
            Fft(re, im);
            if (binsForRate != rate) MapBins(rate);
            for (int b = 0; b < Bands; b++)
            {
                float power = 0;
                for (int k = firstBin[b]; k < endBin[b]; k++) power = Math.Max(power, re[k] * re[k] + im[k] * im[k]);
                // -66..-12 dB to 0..1, treble lifted
                double db = 10 * Math.Log10(power * 16.0 / ((double)N * N) + 1e-18);
                fresh[b] = Ease.Clamp01((float)((db + 66) / 54)) * (0.82f + 0.18f * b / Bands);
            }
            lock (levels) Array.Copy(fresh, levels, Bands);
        }

        // In-place radix-2 Cooley-Tukey
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
