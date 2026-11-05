using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Mic or loopback to 16 kHz PCM
    sealed class Recorder : IDisposable
    {
        public const int Rate = 16000;

        readonly bool loopback;
        readonly Stream sink;
        readonly object gate = new object();
        Thread thread;
        volatile bool stop;
        volatile float level;
        long written;
        readonly ManualResetEvent opened = new ManualResetEvent(false);
        bool openOk;

        public Recorder(bool loopback, Stream sink)
        {
            this.loopback = loopback;
            this.sink = sink;
        }

        // Smoothed loudness 0..1 for the island
        public float Level { get { return level; } }

        // Loudest moment so far: silence check
        public float Peak { get { return peak; } }
        volatile float peak;

        public double Seconds { get { return Interlocked.Read(ref written) / (double)Rate; } }

        // Blocks until the device opens
        public bool Start()
        {
            thread = new Thread(Prof.Wrap(Run)) { IsBackground = true, Name = loopback ? "rekam-sistem" : "rekam-mic" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            opened.WaitOne(3000);
            return openOk;
        }

        public void Stop()
        {
            stop = true;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
            thread = null;
        }

        public void Dispose() { Stop(); }

        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

        void Run()
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            try
            {
                int channels, rate;
                bool isFloat;
                openOk = Open(out client, out capture, out channels, out rate, out isFloat);
                opened.Set();
                if (!openOk) return;
                var clock = Stopwatch.StartNew();
                double step = rate / (double)Rate, pos = 0;
                float prev = 0;
                float[] mono = new float[0];
                var buf = new byte[0];
                while (!stop)
                {
                    uint packet;
                    if (capture.GetNextPacketSize(out packet) != 0) break;
                    while (packet > 0 && !stop)
                    {
                        IntPtr data; uint frames, flags; ulong dp, qp;
                        if (capture.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;
                        int n = (int)frames;
                        if (mono.Length < n) mono = new float[n];
                        Downmix(data, n, channels, isFloat, (flags & 2) != 0, mono);
                        capture.ReleaseBuffer(frames);
                        // Linear resample, phase carried across packets
                        int outN = 0;
                        if (buf.Length < (int)(n / step + 4) * 2) buf = new byte[(int)(n / step + 4) * 2];
                        float sum = 0;
                        while (pos < n)
                        {
                            int i = (int)pos;
                            float a = i == 0 ? prev : mono[i - 1], b = mono[i];
                            float v = a + (b - a) * (float)(pos - i);
                            short s = (short)Math.Max(-32768, Math.Min(32767, v * 32767));
                            buf[outN * 2] = (byte)s;
                            buf[outN * 2 + 1] = (byte)(s >> 8);
                            sum += v * v;
                            outN++;
                            pos += step;
                        }
                        pos -= n;
                        if (n > 0) prev = mono[n - 1];
                        Write(buf, outN);
                        if (outN > 0) Meter(sum / outN);
                        if (capture.GetNextPacketSize(out packet) != 0) break;
                    }
                    // Silent loopback: pad to wall time
                    PadTo(clock.Elapsed.TotalSeconds);
                    Thread.Sleep(15);
                }
            }
            catch (Exception ex) { Log.Write("rekam: " + ex.Message); opened.Set(); }
            finally { Release(ref client, ref capture); }
        }

        void Write(byte[] buf, int samples)
        {
            if (samples <= 0) return;
            lock (gate) sink.Write(buf, 0, samples * 2);
            Interlocked.Add(ref written, samples);
        }

        static readonly byte[] zeros = new byte[Rate / 5 * 2];

        void PadTo(double seconds)
        {
            long want = (long)(seconds * Rate), have = Interlocked.Read(ref written);
            // Under 120 ms behind is normal buffering
            if (want - have < Rate * 12 / 100) return;
            long gap = want - have - Rate / 20;
            while (gap > 0)
            {
                int n = (int)Math.Min(gap, zeros.Length / 2);
                Write(zeros, n);
                gap -= n;
            }
            Meter(0);
        }

        // Fast rise, slow fall, dB scaled
        void Meter(float meanSquare)
        {
            double db = 10 * Math.Log10(meanSquare + 1e-10);
            float target = (float)Math.Max(0, Math.Min(1, (db + 55) / 45));
            float l = level;
            level = target > l ? l + (target - l) * 0.6f : l + (target - l) * 0.15f;
            if (target > peak) peak = target;
        }

        float[] floats = new float[0];
        short[] shorts = new short[0];

        void Downmix(IntPtr data, int frames, int channels, bool isFloat, bool silent, float[] mono)
        {
            if (silent) { Array.Clear(mono, 0, frames); return; }
            int n = frames * channels;
            if (isFloat) { if (floats.Length < n) floats = new float[n]; Marshal.Copy(data, floats, 0, n); }
            else { if (shorts.Length < n) shorts = new short[n]; Marshal.Copy(data, shorts, 0, n); }
            for (int f = 0; f < frames; f++)
            {
                float v = 0;
                int i = f * channels;
                for (int ch = 0; ch < channels; ch++) v += isFloat ? floats[i + ch] : shorts[i + ch] / 32768f;
                mono[f] = v / channels;
            }
        }

        bool Open(out IAudioClient client, out IAudioCaptureClient capture, out int channels, out int rate, out bool isFloat)
        {
            client = null; capture = null; channels = 1; rate = 48000; isFloat = true;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice dev;
                // Loopback: default output; else default mic
                if (en.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out dev) != 0 || dev == null) return false;
                var iid = typeof(IAudioClient).GUID;
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
                client = (IAudioClient)o;
                IntPtr fmt;
                if (client.GetMixFormat(out fmt) != 0) return false;
                try
                {
                    int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
                    channels = Math.Max(1, (int)Marshal.ReadInt16(fmt, 2));
                    rate = Marshal.ReadInt32(fmt, 4);
                    int bits = Marshal.ReadInt16(fmt, 14);
                    isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(fmt, 24) == 3);
                    if (!isFloat && bits != 16) return false;
                    if (client.Initialize(0, loopback ? 0x00020000 : 0, 2000000, 0, fmt, IntPtr.Zero) != 0) return false;
                }
                finally { CoTaskMemFree(fmt); }
                var cid = typeof(IAudioCaptureClient).GUID;
                object c;
                if (client.GetService(ref cid, out c) != 0) return false;
                capture = (IAudioCaptureClient)c;
                return client.Start() == 0;
            }
            catch (Exception ex) { Log.Write("rekam buka: " + ex.Message); return false; }
        }

        static void Release(ref IAudioClient client, ref IAudioCaptureClient capture)
        {
            try { if (client != null) client.Stop(); } catch (Exception) { }
            if (capture != null) { Marshal.ReleaseComObject(capture); capture = null; }
            if (client != null) { Marshal.ReleaseComObject(client); client = null; }
        }
    }

    static class Wav
    {
        // 44-byte header for 16 kHz mono 16-bit
        public static byte[] Header(long dataBytes)
        {
            var h = new byte[44];
            using (var w = new BinaryWriter(new MemoryStream(h)))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write((int)Math.Min(int.MaxValue, 36 + dataBytes));
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(Recorder.Rate);
                w.Write(Recorder.Rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write((int)Math.Min(int.MaxValue, dataBytes));
            }
            return h;
        }

        public static byte[] FromPcm(byte[] pcm, int offset, int count)
        {
            var wav = new byte[44 + count];
            Buffer.BlockCopy(Header(count), 0, wav, 0, 44);
            Buffer.BlockCopy(pcm, offset, wav, 44, count);
            return wav;
        }
    }
}
