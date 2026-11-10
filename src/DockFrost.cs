using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    partial class Island
    {
        // Baked glass for one screen area: clear for icons, guarded for text
        sealed class DockPlate
        {
            public Bitmap Image, Text;
            public Rectangle Rect;

            public void Dispose()
            {
                if (Text != null && Text != Image) Text.Dispose();
                if (Image != null) Image.Dispose();
            }
        }

        // Reads the screen behind the dock and its cards off the UI thread
        sealed class DockFrost
        {
            // Pilot: a fixed picture instead of the user's screen
            public static Func<Rectangle, Bitmap> Backdrop { get; set; }

            sealed class Job { public Rectangle Rect; public Color Bg; public bool Light, Liquid; public int Div; }

            readonly Action<DockPlate> deliver;
            readonly AutoResetEvent wake = new AutoResetEvent(false);
            Thread thread;
            volatile bool run;
            Job job;
            int nudged;

            public DockFrost(Action<DockPlate> deliver) { this.deliver = deliver; }

            public void Start()
            {
                if (thread != null) return;
                run = true;
                thread = new Thread(Prof.Wrap(Loop)) { IsBackground = true, Name = "dock frost", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }

            public void Stop()
            {
                run = false;
                wake.Set();
                thread = null;
            }

            public void Request(Rectangle rect, Color bg, bool light, bool liquid)
            {
                var j = new Job { Rect = rect, Bg = bg, Light = light, Liquid = liquid, Div = liquid ? 2 : 5 };
                var last = Volatile.Read(ref job);
                Volatile.Write(ref job, j);
                if (last == null || last.Rect != rect || last.Bg != bg || last.Liquid != liquid || last.Light != light) wake.Set();
            }

            // Windows moved: look again once they settle
            public void Nudge()
            {
                Interlocked.Exchange(ref nudged, 1);
                wake.Set();
            }

            void Loop()
            {
                Bitmap tiny = null;
                byte[] sig = null, cur = null, work = null;
                Job last = null;
                int wait = 250, still = 0;
                while (run)
                {
                    wake.WaitOne(wait);
                    var j = Volatile.Read(ref job);
                    if (!run || j == null || j.Rect.Width < 8 || j.Rect.Height < 8) { wait = 1000; continue; }
                    if (Interlocked.Exchange(ref nudged, 0) != 0) { still = 0; Thread.Sleep(80); }
                    bool same = last != null && last.Rect == j.Rect && last.Bg == j.Bg && last.Liquid == j.Liquid && last.Light == j.Light;
                    try
                    {
                        int tw = Math.Max(4, j.Rect.Width / j.Div), th = Math.Max(4, j.Rect.Height / j.Div);
                        if (tiny == null || tiny.Width != tw || tiny.Height != th)
                        {
                            if (tiny != null) tiny.Dispose();
                            tiny = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
                            sig = null;
                        }
                        Capture(tiny, j.Rect);
                        var data = tiny.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        int stride = data.Stride, len = stride * th;
                        if (cur == null || cur.Length != len) cur = new byte[len];
                        Marshal.Copy(data.Scan0, cur, 0, len);
                        tiny.UnlockBits(data);
                        // Same backdrop: keep the glass, look less often
                        if (same && sig != null && Similar(cur, sig))
                        {
                            still++;
                            wait = Math.Min(3000, 250 << Math.Min(4, still));
                            continue;
                        }
                        still = 0;
                        wait = 250;
                        var swap = sig;
                        sig = cur;
                        cur = swap;
                        var pl = new DockPlate { Rect = j.Rect };
                        if (work == null || work.Length != len) work = new byte[len];
                        if (j.Liquid)
                        {
                            Liquid(sig, stride, tw, th, j.Bg, false, work);
                            pl.Image = Upscale(tiny, work, j);
                            Liquid(sig, stride, tw, th, j.Bg, true, work);
                            pl.Text = Upscale(tiny, work, j);
                        }
                        else
                        {
                            Buffer.BlockCopy(sig, 0, work, 0, len);
                            pl.Image = pl.Text = Upscale(tiny, work, j);
                        }
                        last = j;
                        deliver(pl);
                    }
                    catch (Exception ex) { Log.Write("dock frost: " + ex.Message); wait = 2000; }
                }
                if (tiny != null) tiny.Dispose();
            }

            // Liquid for the dock: icons get vivid, lightly veiled glass;
            // under text the veil follows the backdrop to keep contrast
            static void Liquid(byte[] raw, int stride, int w, int h, Color bg, bool text, byte[] dst)
            {
                float lt = Luma(bg);
                bool light = lt > 0.2f;
                float limit = text ? (light ? 0.62f : 0.09f) : (light ? 0.5f : 0.22f);
                float aMin = text ? (light ? 0.3f : 0.26f) : 0.08f, aMax = text ? 0.9f : 0.7f, sat = text ? 1.25f : 1.45f;
                Buffer.BlockCopy(raw, 0, dst, 0, raw.Length);
                BoxBlur(dst, stride, w, h, text ? 2 : 1);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 4;
                        float b = dst[i], gr = dst[i + 1], r = dst[i + 2];
                        float grey = 0.2126f * r + 0.7152f * gr + 0.0722f * b;
                        r = Sat(grey, r, sat);
                        gr = Sat(grey, gr, sat);
                        b = Sat(grey, b, sat);
                        float lum = 0.2126f * Linear[(int)r] + 0.7152f * Linear[(int)gr] + 0.0722f * Linear[(int)b];
                        float a = light ? (lum < limit ? (limit - lum) / Math.Max(0.001f, lt - lum) : 0)
                                        : (lum > limit ? (lum - limit) / Math.Max(0.001f, lum - lt) : 0);
                        a = a < aMin ? aMin : a > aMax ? aMax : a;
                        dst[i] = (byte)(b + (bg.B - b) * a);
                        dst[i + 1] = (byte)(gr + (bg.G - gr) * a);
                        dst[i + 2] = (byte)(r + (bg.R - r) * a);
                        dst[i + 3] = 255;
                    }
            }

            static Bitmap Upscale(Bitmap tiny, byte[] px, Job j)
            {
                var data = tiny.LockBits(new Rectangle(0, 0, tiny.Width, tiny.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(px, 0, data.Scan0, Math.Min(px.Length, data.Stride * tiny.Height));
                tiny.UnlockBits(data);
                var img = new Bitmap(j.Rect.Width, j.Rect.Height, PixelFormat.Format32bppPArgb);
                using (var pg = Graphics.FromImage(img))
                {
                    pg.CompositingMode = CompositingMode.SourceCopy;
                    pg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    pg.PixelOffsetMode = PixelOffsetMode.Half;
                    float f = j.Div / 2f;
                    pg.DrawImage(tiny, new RectangleF(-f, -f, j.Rect.Width + f * 2, j.Rect.Height + f * 2));
                    pg.CompositingMode = CompositingMode.SourceOver;
                    if (!j.Liquid) using (var tint = new SolidBrush(Ink.A(j.Bg, BaseTint))) pg.FillRectangle(tint, 0, 0, j.Rect.Width, j.Rect.Height);
                }
                return img;
            }

            static void Capture(Bitmap tiny, Rectangle r)
            {
                var hook = Backdrop;
                using (var tg = Graphics.FromImage(tiny))
                {
                    if (hook != null)
                    {
                        using (var b = hook(r))
                        {
                            tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            tg.DrawImage(b, new Rectangle(0, 0, tiny.Width, tiny.Height));
                        }
                        return;
                    }
                    IntPtr hdc = tg.GetHdc(), screen = Native.GetDC(IntPtr.Zero);
                    Native.SetStretchBltMode(hdc, 4);
                    Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                    Native.StretchBlt(hdc, 0, 0, tiny.Width, tiny.Height, screen, r.X, r.Y, r.Width, r.Height, 0x00CC0020);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                    tg.ReleaseHdc(hdc);
                }
            }
        }

        // One frame per vsync, at most ~60 a second, only while asked
        sealed class DockPacer
        {
            [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

            readonly IntPtr hwnd;
            readonly int msg;
            readonly AutoResetEvent wake = new AutoResetEvent(false);
            volatile bool run = true, want;
            int posted;

            public DockPacer(IntPtr hwnd, int msg)
            {
                this.hwnd = hwnd;
                this.msg = msg;
                new Thread(Loop) { IsBackground = true, Name = "dock frames" }.Start();
            }

            // True when frames were idle until now
            public bool Want()
            {
                bool idle = !want;
                want = true;
                wake.Set();
                return idle;
            }

            public void Done(bool more)
            {
                if (!more) want = false;
                Interlocked.Exchange(ref posted, 0);
            }

            public void Stop()
            {
                run = false;
                wake.Set();
            }

            void Loop()
            {
                var sw = Stopwatch.StartNew();
                double last = -100;
                while (run)
                {
                    if (!want) { wake.WaitOne(); continue; }
                    if (Native.DwmFlush() != 0) Thread.Sleep(15);
                    double now = sw.Elapsed.TotalMilliseconds;
                    if (now - last < 12) continue;
                    if (Interlocked.CompareExchange(ref posted, 1, 0) != 0) continue;
                    last = now;
                    if (!PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero)) Interlocked.Exchange(ref posted, 0);
                }
            }
        }
    }
}
