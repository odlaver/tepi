using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Tepi
{
    // Webcam frames, mirrored, newest only
    class CameraMirror
    {
        MediaCapture cap;
        MediaFrameReader reader;
        readonly object gate = new object();
        Bitmap latest;
        DateTime lastUtc;
        public volatile bool Failed;
        public volatile int Count;
        float radius;
        public event Action Frame;

        // Blocking; call off the UI thread
        public void Start(int width, int height, float cornerPx)
        {
            radius = cornerPx;
            try
            {
                MediaFrameSourceGroup group = null;
                MediaFrameSourceInfo info = null;
                foreach (var g in MediaService.Await(MediaFrameSourceGroup.FindAllAsync()))
                {
                    foreach (var s in g.SourceInfos)
                        if (s.SourceKind == MediaFrameSourceKind.Color) { group = g; info = s; break; }
                    if (group != null) break;
                }
                if (group == null) { Failed = true; return; }
                // Own the camera for a small raw format; share if a call has it
                cap = Open(group, MediaCaptureSharingMode.ExclusiveControl) ?? Open(group, MediaCaptureSharingMode.SharedReadOnly);
                if (cap == null) { Failed = true; return; }
                var source = cap.FrameSources[info.Id];
                PickSmall(source, width);
                // Windows scales in the pipeline, not us
                reader = MediaService.Await(cap.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, new BitmapSize { Width = (uint)width, Height = (uint)height }));
                reader.FrameArrived += OnFrame;
                if (MediaService.Await(reader.StartAsync()) != MediaFrameReaderStartStatus.Success) { Failed = true; Stop(); }
            }
            catch (Exception ex) { Log.Write("mirror: " + ex.Message); Failed = true; Stop(); }
        }

        static MediaCapture Open(MediaFrameSourceGroup group, MediaCaptureSharingMode mode)
        {
            var c = new MediaCapture();
            try
            {
                Wait(c.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = group,
                    SharingMode = mode,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video
                }));
                return c;
            }
            catch (Exception) { c.Dispose(); return null; }
        }

        // Smallest uncompressed format at least this wide
        static void PickSmall(MediaFrameSource source, int width)
        {
            MediaFrameFormat best = null;
            foreach (var f in source.SupportedFormats)
            {
                string sub = f.Subtype.ToUpperInvariant();
                if (f.VideoFormat == null || (sub != "NV12" && sub != "YUY2")) continue;
                uint w = f.VideoFormat.Width;
                if (w < width) continue;
                if (best == null || w < best.VideoFormat.Width) best = f;
            }
            if (best == null) return;
            try { Wait(source.SetFormatAsync(best)); } catch (Exception) { }
        }

        static void Wait(IAsyncAction op)
        {
            using (var done = new ManualResetEvent(false))
            {
                op.Completed = delegate { try { done.Set(); } catch (ObjectDisposedException) { } };
                if (op.Status == AsyncStatus.Started && !done.WaitOne(10000)) throw new TimeoutException("camera timeout");
            }
            if (op.Status != AsyncStatus.Completed) throw new InvalidOperationException("camera " + op.Status);
        }

        void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            // 30 fps is plenty for a mirror
            if ((DateTime.UtcNow - lastUtc).TotalMilliseconds < 30) return;
            lastUtc = DateTime.UtcNow;
            try
            {
                using (var f = sender.TryAcquireLatestFrame())
                {
                    if (f == null || f.VideoMediaFrame == null || f.VideoMediaFrame.SoftwareBitmap == null) return;
                    var sb = f.VideoMediaFrame.SoftwareBitmap;
                    SoftwareBitmap bgra = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? sb : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    var bmp = ToBitmap(bgra, radius);
                    if (!ReferenceEquals(bgra, sb)) bgra.Dispose();
                    Bitmap old;
                    lock (gate) { old = latest; latest = bmp; }
                    if (old != null) old.Dispose();
                    Count++;
                }
                var h = Frame;
                if (h != null) h();
            }
            catch (Exception ex) { Log.Write("mirror frame: " + ex.Message); }
        }

        // Display-sized: flip and round once, here
        static Bitmap ToBitmap(SoftwareBitmap sb, float radius)
        {
            int w = sb.PixelWidth, h = sb.PixelHeight;
            var buf = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
            sb.CopyToBuffer(buf);
            var px = new byte[buf.Length];
            using (var rd = DataReader.FromBuffer(buf)) rd.ReadBytes(px);
            using (var full = new Bitmap(w, h, PixelFormat.Format32bppRgb))
            {
                var data = full.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
                full.UnlockBits(data);
                int tw = w, th = h;
                var small = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(small))
                using (var tb = new TextureBrush(full, System.Drawing.Drawing2D.WrapMode.Clamp))
                using (var path = Ink.Round(new RectangleF(0, 0, tw, th), radius))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    tb.TranslateTransform(tw, 0);
                    tb.ScaleTransform(-1, 1);
                    g.FillPath(tb, path);
                }
                return small;
            }
        }

        // Caller holds nothing; copy-free peek
        public void Use(Action<Bitmap> draw)
        {
            lock (gate) { if (latest != null) draw(latest); }
        }

        public void Stop()
        {
            var r = reader;
            var c = cap;
            reader = null;
            cap = null;
            try
            {
                if (r != null) { r.FrameArrived -= OnFrame; Wait(r.StopAsync()); r.Dispose(); }
                if (c != null) c.Dispose();
            }
            catch (Exception ex) { Log.Write("mirror stop: " + ex.Message); }
            lock (gate) { if (latest != null) { latest.Dispose(); latest = null; } }
        }
    }

    partial class Island
    {
        const int MirrorHotkey = 14;
        // Inner picture stays 16:9
        const float MirrorW = 384, MirrorH = 223;
        CameraMirror mirror;
        DateTime mirrorUntilUtc;
        Action<DateTime> mirrorPaint;
        Func<int> mirrorSig;
        RectangleF mirrorAt;

        bool MirrorOn { get { return mirror != null; } }

        void ToggleMirror()
        {
            if (mirror != null) { CloseMirror(); return; }
            mirror = new CameraMirror();
            mirror.Frame += () => { if (mirror != null && mirror.Failed) Post(CloseMirror); };
            mirrorUntilUtc = DateTime.UtcNow.AddMinutes(2);
            expanded = false;
            var m = mirror;
            // Camera setup blocks; keep frames smooth
            ThreadPool.QueueUserWorkItem(delegate
            {
                m.Start((int)Math.Round((MirrorW - 16) * scale), (int)Math.Round((MirrorH - 16) * scale), 20 * scale);
                if (m.Failed) Post(() => { if (ReferenceEquals(m, mirror)) { CloseMirror(); ShowInfo(Ink.IcoCam, Lang.T("Kamera tidak bisa dibuka"), "", true); } });
            });
            Kick();
        }

        void CloseMirror()
        {
            if (mirror == null) return;
            var m = mirror;
            mirror = null;
            ThreadPool.QueueUserWorkItem(delegate { m.Stop(); });
            Kick();
        }

        // Two minutes, then the camera goes back
        void TickMirror() { if (mirror != null && DateTime.UtcNow > mirrorUntilUtc) CloseMirror(); }

        void DrawMirror(float w, float h)
        {
            mirrorAt = new RectangleF(8, 8, w - 16, h - 16);
            if (mirrorPaint == null)
            {
                // Pre-rounded and pixel-sized: a plain blit
                mirrorPaint = t =>
                {
                    var m = mirror;
                    if (m == null) return;
                    var mode = p.G.InterpolationMode;
                    p.G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    m.Use(b => p.G.DrawImage(b, mirrorAt));
                    p.G.InterpolationMode = mode;
                };
                mirrorSig = () => mirror == null ? -1 : mirror.Count;
            }
            p.FillRound(mirrorAt, 20, p.T.Fill);
            p.TextCenter(Lang.T("Membuka kamera…"), Ink.Text(12.5f), p.T.Sub, w / 2, h / 2);
            // Redraw only when a frame came
            LiveRect(mirrorAt, 30, mirrorSig, mirrorPaint);
            AddHit(new RectangleF(0, 0, w, h), "mirrorClose", CloseMirror);
        }
    }
}
