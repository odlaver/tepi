using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinTimer = System.Windows.Forms.Timer;

namespace Tepi
{
    // Frosted glass from the screen behind
    partial class Island
    {
        const int FrostDiv = 5;
        const float BaseTint = 0.42f;
        volatile bool frostDirty, inMotion;
        volatile int lastPresentTick;
        readonly System.Threading.AutoResetEvent presented = new System.Threading.AutoResetEvent(false);

        // Theme changed: rebake the tint
        void ResetFrost()
        {
            frostDirty = true;
            frostWake.Set();
        }
        readonly System.Threading.AutoResetEvent frostWake = new System.Threading.AutoResetEvent(false);
        System.Threading.Thread frostThread;
        volatile bool frostRun;
        volatile int frostNeedH;
        Bitmap frost;
        int frostH;
        bool excluded;

        bool GlassOn { get { return settings.GetBool("glass", true); } }

        void StartGlass() { ApplyGlass(); }

        void ApplyGlass()
        {
            bool on = GlassOn;
            // Island must not capture itself
            if (on != excluded)
            {
                if (Native.SetWindowDisplayAffinity(Handle, on ? Native.WDA_EXCLUDEFROMCAPTURE : 0)) excluded = on;
                else Log.Write("display affinity failed: " + Marshal.GetLastWin32Error());
            }
            if (on && frostThread == null)
            {
                frostRun = true;
                frostThread = new System.Threading.Thread(FrostLoop) { IsBackground = true, Name = "frost" };
                frostThread.Start();
            }
            else if (!on && frostThread != null)
            {
                frostRun = false;
                frostWake.Set();
                frostThread = null;
                if (frost != null) { frost.Dispose(); frost = null; }
            }
            Kick();
        }

        void ToggleGlass()
        {
            settings.Toggle("glass", true);
            ApplyGlass();
        }

        volatile int frostNeedW;
        int frostLeft, frostW;

        DateTime prefetchUntilUtc;

        // Hover: grab panel-sized frost before the panel opens, not mid-animation
        void PrefetchFrost()
        {
            if (!GlassOn) return;
            prefetchUntilUtc = DateTime.UtcNow.AddSeconds(2);
            NeedFrost(PanelHeight());
        }

        // Cover where the shell is heading
        void NeedFrost(float targetH)
        {
            // Panel surface is baked at full height, so frost must reach it
            if (current == View.Panel || DateTime.UtcNow < prefetchUntilUtc) targetH = Math.Max(targetH, MaxH + 30);
            float wide = DateTime.UtcNow < prefetchUntilUtc ? PanelW : 0;
            float half = Math.Max(Math.Max(sw.Value, sw.Target), wide) / 2 + (bubble.Target > 0 || bubble.Value > 0.01f ? BubbleGap + BubbleR * 2 : 0);
            int w = Math.Min(canvasW, 2 * (int)Math.Ceiling((Math.Min(half, MaxHalf) + Pad) * scale));
            int h = Math.Min(canvasH, (int)Math.Ceiling((Gap + Math.Max(Math.Max(targetH, sh.Value), BubbleR * 2) + Pad) * scale));
            frostNeedW = w;
            frostNeedH = h;
            if (frost == null || w > frostW || h > frostH) frostWake.Set();
        }

        // Slow readback: own thread, adaptive rate
        void FrostLoop()
        {
            Bitmap tiny = null;
            byte[] sig = null;
            int lastW = 0, lastH = 0, wait = 180;
            double spent = 0;
            int runs = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (frostRun)
            {
                bool woken = frostWake.WaitOne(wait);
                if (!frostRun || !windowShown || canvasW == 0) continue;
                // Skip readback mid-motion unless outgrown
                bool outgrown = frostNeedW > frostW || frostNeedH > frostH;
                if (inMotion && !outgrown) { wait = 150; continue; }
                // Capture right after a present
                if (Environment.TickCount - lastPresentTick < 120) presented.WaitOne(60);
                double t0 = watch.Elapsed.TotalMilliseconds;
                try
                {
                    var mon = monitorPx;
                    int w = Math.Max(64, Math.Min(canvasW, frostNeedW)) & ~1, h = Math.Max(40, Math.Min(canvasH, frostNeedH));
                    int left = mon.Left + mon.Width / 2 - w / 2;
                    int tw = Math.Max(4, w / FrostDiv), th = Math.Max(4, h / FrostDiv);
                    if (tiny == null || tiny.Width != tw || tiny.Height != th)
                    {
                        if (tiny != null) tiny.Dispose();
                        tiny = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
                    }
                    using (var tg = Graphics.FromImage(tiny))
                    {
                        IntPtr hdc = tg.GetHdc(), screen = Native.GetDC(IntPtr.Zero);
                        Native.SetStretchBltMode(hdc, 4);
                        Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                        Native.StretchBlt(hdc, 0, 0, tw, th, screen, left, mon.Top, w, h, 0x00CC0020);
                        Native.ReleaseDC(IntPtr.Zero, screen);
                        tg.ReleaseHdc(hdc);
                    }
                    var data = tiny.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    var next = new byte[data.Stride * th];
                    Marshal.Copy(data.Scan0, next, 0, next.Length);
                    tiny.UnlockBits(data);
                    // Unchanged backdrop: keep frost, back off
                    if (frostDirty) { frostDirty = false; sig = null; }
                    if (w == lastW && h == lastH && sig != null && Same(next, sig))
                    {
                        wait = Math.Min(900, wait + 120);
                        continue;
                    }
                    wait = 180;
                    sig = next;
                    lastW = w;
                    lastH = h;
                    // Full size, base tint baked in
                    var frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (var fg = Graphics.FromImage(frame))
                    {
                        fg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        fg.PixelOffsetMode = PixelOffsetMode.Half;
                        float f = FrostDiv / 2f;
                        fg.DrawImage(tiny, new RectangleF(-f, -f, w + f * 2, h + f * 2));
                        using (var tintBrush = new SolidBrush(Ink.A(Ink.Base.Bg, BaseTint))) fg.FillRectangle(tintBrush, 0, 0, w, h);
                    }
                    int fl = left, fw = w, fh = h;
                    Post(() =>
                    {
                        var old = frost;
                        frost = frame;
                        frostLeft = fl;
                        frostW = fw;
                        frostH = fh;
                        if (old != null) old.Dispose();
                        Kick();
                    });
                }
                catch (Exception ex) { Log.Write("frost: " + ex.Message); }
                finally
                {
                    spent += watch.Elapsed.TotalMilliseconds - t0;
                    if (fpsLog && ++runs % 40 == 0) { Log.Write("frost avg " + (spent / 40).ToString("0.00") + "ms wait " + wait); spent = 0; }
                }
            }
            if (tiny != null) tiny.Dispose();
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 3) return false;
            return true;
        }

        // Glass, else a translucent fill
        void FillSurface(GraphicsPath path, RectangleF bounds, Tone tone, float tint, int pxW)
        {
            if (!GlassOn)
            {
                g.FillPath(p.B(tone.Bg), path);
                g.DrawPath(p.P(tone.Line, 1f), path);
                return;
            }
            var st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            int left = monitorPx.Left + monitorPx.Width / 2 - pxW / 2;
            int fx = frostLeft - left;
            bool covered = frost != null && fx <= Math.Floor(bounds.X * scale) && fx + frostW >= Math.Ceiling(bounds.Right * scale) &&
                           frostH >= Math.Ceiling(bounds.Bottom * scale);
            // Panel: one bake at full height serves every page height and the resize
            var bake = current == View.Panel ? new RectangleF(bounds.X, bounds.Y, bounds.Width, MaxH + 30) : bounds;
            bool bakeCovered = covered && frostH >= Math.Ceiling(bake.Bottom * scale);
            bool baseTone = tone.Bg.R < 128 == Ink.Base.Bg.R < 128;
            bool light = tone.Bg.R > 128;
            // Frost has base tint; add the rest
            float extra = baseTone ? 1 - (1 - tint) / (1 - BaseTint) : tint;
            if (bakeCovered && baseTone && SurfaceSteady()) DrawPanelSurface(bake, fx, tone, extra);
            else
            {
                // Translucent base outside the frost
                if (!covered) g.FillPath(p.B(Ink.A(tone.Bg, 0.62f)), path);
                if (frost != null)
                {
                    BlitPart(frost, fx, bounds);
                    if (extra > 0.01f) g.FillPath(p.B(Ink.A(tone.Bg, extra)), path);
                }
                // Light catching the top edge
                var sheen = new RectangleF(bounds.X, bounds.Y - 1, bounds.Width, Math.Min(bounds.Height, 64) + 2);
                using (var lg = new LinearGradientBrush(sheen, Color.FromArgb(light ? 70 : 26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(lg, sheen);
            }
            g.Restore(st);
            g.DrawPath(p.P(light ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(52, 255, 255, 255), 1f), path);
        }

        // Only the island's box of the frost; the rest is clipped anyway
        void BlitPart(Bitmap img, int x, RectangleF bounds)
        {
            int x0 = Math.Max(x, (int)Math.Floor(bounds.X * scale) - 2), y0 = Math.Max(0, (int)Math.Floor(bounds.Y * scale) - 2);
            int x1 = Math.Min(x + img.Width, (int)Math.Ceiling(bounds.Right * scale) + 2), y1 = Math.Min(img.Height, (int)Math.Ceiling(bounds.Bottom * scale) + 2);
            if (x1 <= x0 || y1 <= y0) return;
            var inner = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(img, new Rectangle(x0, y0, x1 - x0, y1 - y0), x0 - x, y0, x1 - x0, y1 - y0, GraphicsUnit.Pixel);
            g.Restore(inner);
        }

        // Opaque 1:1 device-pixel copy
        void Blit(Bitmap img, int x, int y, bool copy)
        {
            var inner = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (copy) g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(img, new Rectangle(x, y, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
            g.Restore(inner);
        }

        // Two cached surfaces: plain and with the cover backdrop, so the
        // mid-switch swap never rebakes during the animation
        class Surface { public Bitmap Image, Frost, Backdrop; public string Key; }
        readonly Surface[] surfaces = { new Surface(), new Surface() };
        bool backdropInSurface;

        // Cached surface is valid; the panel may still be changing height
        bool SurfaceSteady()
        {
            return flip < 0.001f && sw.Settled && sr.Settled && topGap.Settled &&
                   (DateTime.UtcNow - switchedUtc).TotalSeconds > 0.5 && (current == View.Panel || sh.Settled);
        }

        void DropSurfaces()
        {
            foreach (var s in surfaces) { if (s.Image != null) s.Image.Dispose(); s.Image = null; s.Key = null; }
        }

        bool SurfaceFresh(Surface s, Bitmap bd, string key)
        {
            return s.Image != null && ReferenceEquals(frost, s.Frost) && ReferenceEquals(bd, s.Backdrop) && key == s.Key;
        }

        void BakeSurface(Surface s, Bitmap bd, string key, int bx, int by, int bw, int bh, int fx, float boundsH, Tone tone, float extra)
        {
            double tb0 = Prof.T();
            if (s.Image != null) s.Image.Dispose();
            s.Image = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
            using (var pg = Graphics.FromImage(s.Image))
            {
                pg.CompositingMode = CompositingMode.SourceCopy;
                pg.DrawImage(frost, new Rectangle(0, 0, bw, bh), bx - fx, by, bw, bh, GraphicsUnit.Pixel);
                pg.CompositingMode = CompositingMode.SourceOver;
                using (var tb = new SolidBrush(Ink.A(tone.Bg, extra))) pg.FillRectangle(tb, 0, 0, bw, bh);
                // Same layer order as the live path
                int sh2 = (int)Math.Ceiling(Math.Min(boundsH, 64) * scale);
                using (var lg = new LinearGradientBrush(new Rectangle(0, -1, bw, sh2 + 2), Color.FromArgb(tone.Bg.R > 128 ? 70 : 26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    pg.FillRectangle(lg, 0, 0, bw, sh2);
                if (bd != null) pg.DrawImage(bd, new Rectangle(0, 0, bd.Width, bd.Height), 0, 0, bd.Width, bd.Height, GraphicsUnit.Pixel);
            }
            s.Frost = frost;
            s.Backdrop = bd;
            s.Key = key;
            Prof.Add("surfaceBake", tb0);
        }

        // Resting surface baked into one copy
        void DrawPanelSurface(RectangleF bounds, int fx, Tone tone, float extra)
        {
            int bx = (int)Math.Round(bounds.X * scale), by = (int)Math.Round(bounds.Y * scale);
            int bw = (int)Math.Ceiling(bounds.Width * scale), bh = (int)Math.Ceiling(bounds.Height * scale);
            var cover = current == View.Panel && mediaInfo.Present ? BackdropFor() : null;
            var bd = BackdropShown(DateTime.UtcNow) ? cover : null;
            string key = bx + "," + by + "," + bw + "," + bh + "," + extra.ToString("0.00") + Ink.Light;
            var use = surfaces[bd != null ? 1 : 0];
            if (!SurfaceFresh(use, bd, key)) BakeSurface(use, bd, key, bx, by, bw, bh, fx, bounds.Height, tone, extra);
            Blit(use.Image, bx, by, true);
            backdropInSurface = bd != null;
            // Ready the other one while nothing moves
            var other = surfaces[bd != null ? 0 : 1];
            var otherBd = bd != null ? null : cover;
            if ((bd != null || cover != null) && !PanelMoving() && sh.Settled && !SurfaceFresh(other, otherBd, key))
                BakeSurface(other, otherBd, key, bx, by, bw, bh, fx, bounds.Height, tone, extra);
        }

        // TEPI_DUMP: frames, since glass skips screenshots
        static readonly string dumpDir = Environment.GetEnvironmentVariable("TEPI_DUMP");
        DateTime nextDumpUtc;

        void DumpFrame(Native.POINT dst, int pxW, int pxH)
        {
            if (dumpDir == null || DateTime.UtcNow < nextDumpUtc) return;
            nextDumpUtc = DateTime.UtcNow.AddMilliseconds(250);
            try
            {
                Directory.CreateDirectory(dumpDir);
                using (var bmp = new Bitmap(pxW, pxH, PixelFormat.Format32bppArgb))
                using (var dg = Graphics.FromImage(bmp))
                {
                    dg.CopyFromScreen(dst.X, dst.Y, 0, 0, new Size(pxW, pxH));
                    dg.DrawImage(canvas, new Rectangle(0, 0, pxW, pxH), new Rectangle(0, 0, pxW, pxH), GraphicsUnit.Pixel);
                    bmp.Save(Path.Combine(dumpDir, "latest.png"), ImageFormat.Png);
                    string name = current == View.Panel ? "panel-" + PageNames[page] : current.ToString();
                    bmp.Save(Path.Combine(dumpDir, name + ".png"), ImageFormat.Png);
                }
            }
            catch (Exception ex) { Log.Write("dump: " + ex.Message); }
        }
    }
}
