using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Frost work order; immutable once published
    sealed class FrostJob
    {
        public int W, H, Left, Top;
        public Color Bg;
        public bool Light, Liquid;
        // Panel plate: capture rect, tint, sheen
        public bool Panel;
        public Rectangle Bake;
        public float Extra;
        public int Sheen, BackdropGen;
        // Cover plate only on the music tab
        public bool Cover;

        public bool SameCapture(FrostJob o) { return o != null && W == o.W && H == o.H && Left == o.Left && Top == o.Top; }

        public bool SameLook(FrostJob o) { return SameBase(o) && Cover == o.Cover && BackdropGen == o.BackdropGen; }

        // Same glass apart from the music wash
        public bool SameBase(FrostJob o)
        {
            return o != null && Bg == o.Bg && Light == o.Light && Liquid == o.Liquid && Panel == o.Panel && Bake == o.Bake && Extra == o.Extra && Sheen == o.Sheen;
        }
    }

    // Baked glass, one owner thread
    sealed class Plate : IDisposable
    {
        public FrostJob Job;
        public Bitmap Base, Plain, Cover;

        public void Dispose()
        {
            Drop(ref Base);
            Drop(ref Plain);
            Drop(ref Cover);
        }

        public static void Drop(ref Bitmap b) { if (b != null) b.Dispose(); b = null; }

        public static void Ensure(ref Bitmap b, int w, int h)
        {
            if (b != null && b.Width == w && b.Height == h) return;
            Drop(ref b);
            b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }
    }

    // Backdrop copy handed to the frost thread
    sealed class BackdropItem { public Bitmap Image; public int Gen; }

    // Frosted glass from the screen behind
    partial class Island
    {
        const int FrostDiv = 5;
        const float BaseTint = 0.42f, FrostPad = 8;
        // Static backdrop: captures slow to this
        const int FrostRestMs = 3000;
        volatile bool frostDirty, inMotion, dormant, frostCopy;
        // Window moved behind: look again soon
        int frostHint;
        DateTime plateUtc;
        volatile int lastPresentTick;
        readonly AutoResetEvent presented = new AutoResetEvent(false), frostWake = new AutoResetEvent(false);
        Thread frostThread;
        volatile bool frostRun;
        // Latest request, read by the frost thread
        FrostJob frostJob;
        // UI-owned glass; previous returns for reuse
        Plate plate, plateSpare;
        BackdropItem backdropSlot;
        int backdropGen;
        bool excluded, backdropInSurface;

        // Theme changed: rebake the tint
        void ResetFrost()
        {
            frostDirty = true;
            frostWake.Set();
        }

        // Something moved behind the island: look now
        void NudgeFrost()
        {
            if (frostThread == null) return;
            Interlocked.Exchange(ref frostHint, 1);
            frostWake.Set();
        }

        // liquid, frost, or solid; glass=0 means solid
        string MaterialName
        {
            get
            {
                string m = settings.Get("material", "");
                if (m == "liquid" || m == "frost" || m == "solid") return m;
                return settings.GetBool("glass", true) ? "liquid" : "solid";
            }
        }

        // Capturable island can't read the screen
        bool GlassOn { get { return MaterialName != "solid" && !ShowInCapture; } }
        bool LiquidOn { get { return GlassOn && MaterialName == "liquid"; } }

        // Island centre; shift is percent of free half-width
        volatile int shiftPct;
        int CenterX(Rectangle mon)
        {
            float free = Math.Max(0, mon.Width / 2f - (BaseHalf + Pad) * scale);
            return mon.Left + mon.Width / 2 + (int)Math.Round(shiftPct / 100f * free);
        }

        // Window left edge, clamped to monitor
        int WindowLeft(int pxW)
        {
            var mon = monitorPx;
            int left = CenterX(mon) - pxW / 2;
            return Math.Max(mon.Left, Math.Min(mon.Right - pxW, left));
        }

        void StartGlass() { ApplyGlass(); }

        void ApplyGlass()
        {
            bool on = GlassOn, hide = on || PrompterOn;
            // Glass and scripts stay out of captures
            if (hide != excluded)
            {
                if (Native.SetWindowDisplayAffinity(Handle, hide ? Native.WDA_EXCLUDEFROMCAPTURE : 0)) excluded = hide;
                else Log.Write("display affinity failed: " + Marshal.GetLastWin32Error());
            }
            if (on && frostThread == null)
            {
                frostRun = true;
                frostThread = new Thread(FrostLoop) { IsBackground = true, Name = "frost", Priority = ThreadPriority.BelowNormal };
                frostThread.Start();
            }
            else if (!on && frostThread != null)
            {
                frostRun = false;
                frostWake.Set();
                frostThread = null;
                if (plate != null) { plate.Dispose(); plate = null; }
                var spare = Interlocked.Exchange(ref plateSpare, null);
                if (spare != null) spare.Dispose();
            }
            Kick();
        }

        DateTime prefetchUntilUtc;

        // Hover: grab panel-sized frost before the panel opens, not mid-animation
        void PrefetchFrost()
        {
            WarmTabs();
            if (!GlassOn) return;
            prefetchUntilUtc = DateTime.UtcNow.AddSeconds(2);
            if (GrowCanvas()) Kick();
            NeedFrost(PanelHeight());
        }

        // Cover the shell's target; plates at rest
        void NeedFrost(float targetH)
        {
            bool panel = current == View.Panel || DateTime.UtcNow < prefetchUntilUtc;
            // Panel surface is baked at full height, so frost must reach it
            if (panel) targetH = Math.Max(targetH, MaxH + 30);
            float wide = DateTime.UtcNow < prefetchUntilUtc ? PanelW : 0;
            float half = Math.Max(Math.Max(sw.Value, sw.Target), wide) / 2 + (bubble.Target > 0 || bubble.Value > 0.01f ? BubbleGap + BubbleR * 2 : 0);
            // Glass covers the shell, not its shadow
            int w = Math.Max(64, Math.Min(canvasW, 2 * (int)Math.Ceiling((Math.Min(half, canvasHalf) + FrostPad) * scale))) & ~1;
            int h = Math.Max(40, Math.Min(TallPx(), (int)Math.Ceiling((Gap + Math.Max(Math.Max(targetH, sh.Value), BubbleR * 2) + FrostPad) * scale)));
            var job = new FrostJob { W = w, H = h, Left = WindowLeft(w), Top = monitorPx.Top, Bg = Ink.Base.Bg, Light = Ink.Light, Liquid = LiquidOn, BackdropGen = backdropGen };
            if (panel)
            {
                int bw = (int)Math.Ceiling(PanelW * scale), bh = Math.Min(h, (int)Math.Ceiling((MaxH + 30) * scale));
                job.Panel = true;
                job.Bake = new Rectangle(w / 2 - bw / 2, (int)Math.Round((Docked ? 0 : Gap) * scale), bw, bh);
                job.Extra = PanelExtra;
                job.Sheen = (int)Math.Ceiling((PanelTop - 8) * scale);
                job.Cover = current == View.Panel && page == PageMusic;
                // Frost freed its copy: send another
                if (job.Cover && !frostCopy && blurCache != null && Volatile.Read(ref backdropSlot) == null) { PublishBackdrop(DeepCopy(blurCache)); return; }
            }
            var last = Volatile.Read(ref frostJob);
            Volatile.Write(ref frostJob, job);
            // Wake only when glass no longer fits
            var pl = plate;
            bool outgrown = pl == null || w > pl.Job.W || h > pl.Job.H || job.Left != pl.Job.Left;
            if (outgrown || !job.SameLook(last)) frostWake.Set();
        }

        // Backdrop copy for frost thread; null clears
        void PublishBackdrop(Bitmap copy)
        {
            backdropGen++;
            var old = Interlocked.Exchange(ref backdropSlot, new BackdropItem { Image = copy, Gen = backdropGen });
            if (old != null && old.Image != null) old.Image.Dispose();
            if (GlassOn) NeedFrost(sh.Target);
        }

        // Readback and bakes: own thread, adaptive rate
        void FrostLoop()
        {
            Bitmap tiny = null, backdrop = null;
            byte[] sig = null, next = null;
            FrostJob last = null;
            int wait = 250, runs = 0, still = 0, caps = 0;
            double capMs = 0, bakeMs = 0;
            long cpuMark = Native.ThreadCpu(), capCpu = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double lastPanelMs = -10000;
            while (frostRun)
            {
                frostWake.WaitOne(wait);
                long cpuNow = Native.ThreadCpu();
                Interlocked.Add(ref frostCpu, cpuNow - cpuMark);
                cpuMark = cpuNow;
                var job = Volatile.Read(ref frostJob);
                if (!frostRun || job == null) continue;
                // Unseen: hidden, locked or screen off
                if (!windowShown || dormant) { wait = 2000; continue; }
                if (Interlocked.Exchange(ref frostHint, 0) != 0) still = 0;
                bool outgrown = last == null || !job.SameCapture(last) && (job.W > last.W || job.H > last.H || job.Left != last.Left);
                // Mid-motion: the music wash waits too
                if (inMotion && !outgrown && job.SameBase(last)) { wait = 150; continue; }
                if (frostDirty) { frostDirty = false; sig = null; }
                bool sameJob = job.SameCapture(last) && job.SameLook(last);
                // Panel plates: at most ~2.5 per second
                double since = watch.Elapsed.TotalMilliseconds - lastPanelMs;
                if (sameJob && job.Panel && sig != null && since < 400) { wait = Math.Max(20, (int)(400 - since)); continue; }
                // Capture right after a present
                if (Environment.TickCount - lastPresentTick < 120) presented.WaitOne(60);
                double t0 = watch.Elapsed.TotalMilliseconds;
                try
                {
                    var item = Interlocked.Exchange(ref backdropSlot, null);
                    if (item != null)
                    {
                        if (backdrop != null) backdrop.Dispose();
                        backdrop = item.Image;
                    }
                    // No music wash: free the copy
                    if (!job.Cover && backdrop != null) { backdrop.Dispose(); backdrop = null; }
                    frostCopy = backdrop != null;
                    int w = job.W, h = job.H;
                    int tw = Math.Max(4, w / FrostDiv), th = Math.Max(4, h / FrostDiv);
                    if (tiny == null || tiny.Width != tw || tiny.Height != th)
                    {
                        if (tiny != null) tiny.Dispose();
                        tiny = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
                    }
                    long c0 = Native.ThreadCpu();
                    using (var tg = Graphics.FromImage(tiny))
                    {
                        IntPtr hdc = tg.GetHdc(), screen = Native.GetDC(IntPtr.Zero);
                        Native.SetStretchBltMode(hdc, 4);
                        Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                        Native.StretchBlt(hdc, 0, 0, tw, th, screen, job.Left, job.Top, w, h, 0x00CC0020);
                        Native.ReleaseDC(IntPtr.Zero, screen);
                        tg.ReleaseHdc(hdc);
                    }
                    var data = tiny.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    int len = data.Stride * th;
                    // Two buffers swap; no garbage per capture
                    if (next == null || next.Length != len) next = new byte[len];
                    Marshal.Copy(data.Scan0, next, 0, len);
                    tiny.UnlockBits(data);
                    capCpu += Native.ThreadCpu() - c0;
                    caps++;
                    double t1 = watch.Elapsed.TotalMilliseconds;
                    capMs += t1 - t0;
                    // Unchanged backdrop: keep glass, look less often
                    if (sameJob && sig != null && Similar(next, sig))
                    {
                        still++;
                        wait = Math.Min(FrostRestMs, 250 << Math.Min(4, still));
                        continue;
                    }
                    still = 0;
                    wait = 250;
                    var swap = sig;
                    sig = next;
                    next = swap != null && swap.Length == len ? swap : null;
                    if (job.Liquid) LiquidPass(tiny, sig, job.Bg, job.Light);
                    var pl = Interlocked.Exchange(ref plateSpare, null) ?? new Plate();
                    pl.Job = job;
                    Plate.Ensure(ref pl.Base, w, h);
                    using (var fg = Graphics.FromImage(pl.Base))
                    {
                        fg.CompositingMode = CompositingMode.SourceCopy;
                        fg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        fg.PixelOffsetMode = PixelOffsetMode.Half;
                        float f = FrostDiv / 2f;
                        fg.DrawImage(tiny, new RectangleF(-f, -f, w + f * 2, h + f * 2));
                        fg.CompositingMode = CompositingMode.SourceOver;
                        // Liquid tints per pixel already
                        if (!job.Liquid)
                            using (var tintBrush = new SolidBrush(Ink.A(job.Bg, BaseTint))) fg.FillRectangle(tintBrush, 0, 0, w, h);
                    }
                    if (job.Panel)
                    {
                        lastPanelMs = t0;
                        Plate.Ensure(ref pl.Plain, job.Bake.Width, job.Bake.Height);
                        BakePlate(pl.Plain, pl.Base, job, null);
                        if (backdrop != null && job.Cover)
                        {
                            Plate.Ensure(ref pl.Cover, job.Bake.Width, job.Bake.Height);
                            BakePlate(pl.Cover, pl.Base, job, backdrop);
                        }
                        else Plate.Drop(ref pl.Cover);
                    }
                    else { Plate.Drop(ref pl.Plain); Plate.Drop(ref pl.Cover); }
                    bakeMs += watch.Elapsed.TotalMilliseconds - t1;
                    last = job;
                    var done = pl;
                    Post(() => AcceptPlate(done));
                }
                catch (Exception ex) { Log.Write("frost: " + ex.Message); }
                finally
                {
                    if (fpsLog && ++runs % 20 == 0)
                    {
                        Log.Write("frost capture " + (capMs / Math.Max(1, caps)).ToString("0.00") + "ms cpu " + (capCpu / 1e4 / Math.Max(1, caps)).ToString("0.00") + "ms bake " +
                                  (bakeMs / 20).ToString("0.00") + "ms wait " + wait + " caps " + caps);
                        capMs = bakeMs = 0;
                        capCpu = 0;
                        caps = 0;
                    }
                }
            }
            if (tiny != null) tiny.Dispose();
            if (backdrop != null) backdrop.Dispose();
        }

        // Panel tint over base; matches live path
        static readonly float PanelExtra = 1 - (1 - 0.55f) / (1 - BaseTint);

        static readonly float[] Linear = BuildLinear();

        static float[] BuildLinear()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        static float Luma(Color c) { return 0.2126f * Linear[c.R] + 0.7152f * Linear[c.G] + 0.0722f * Linear[c.B]; }

        // Liquid: vivid edges, tint where text needs
        static void LiquidPass(Bitmap tiny, byte[] raw, Color bg, bool light)
        {
            int w = tiny.Width, h = tiny.Height;
            float lt = Luma(bg);
            // Tint colour decides the ink
            light = lt > 0.2f;
            // Contrast: light text L<=0.09, dark L>=0.62
            float limit = light ? 0.62f : 0.09f, aMin = light ? 0.3f : 0.26f, aMax = 0.9f;
            // Copy; raw capture stays the signature
            var src = (byte[])raw.Clone();
            int stride = w * 4;
            // Two box passes; edges keep the first
            BoxBlur(src, stride, w, h, 2);
            var soft = (byte[])src.Clone();
            BoxBlur(src, stride, w, h, 2);
            // Vignette: 1 at centre, 0 at edges
            float cx = (w - 1) / 2f, cy = h * 0.4f, rx = Math.Max(1, w * 0.46f), ry = Math.Max(1, h * 0.55f);
            var data = tiny.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var outPx = new byte[data.Stride * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * data.Stride + x * 4;
                        float dx = (x - cx) / rx, dy = (y - cy) / ry;
                        float v = 1 - Smooth(0.3f, 1.05f, (float)Math.Sqrt(dx * dx + dy * dy));
                        float b = soft[i] + (src[i] - soft[i]) * v, gr = soft[i + 1] + (src[i + 1] - soft[i + 1]) * v, r = soft[i + 2] + (src[i + 2] - soft[i + 2]) * v;
                        float grey = 0.2126f * r + 0.7152f * gr + 0.0722f * b, sat = 1.25f + 0.35f * (1 - v);
                        r = Sat(grey, r, sat); gr = Sat(grey, gr, sat); b = Sat(grey, b, sat);
                        float lum = 0.2126f * Linear[(int)r] + 0.7152f * Linear[(int)gr] + 0.0722f * Linear[(int)b];
                        float a = light ? (lum < limit ? (limit - lum) / Math.Max(0.001f, lt - lum) : 0)
                                        : (lum > limit ? (lum - limit) / Math.Max(0.001f, lum - lt) : 0);
                        // Contrast guard; floor deepens toward centre
                        float floor = aMin - 0.06f + 0.14f * v;
                        a = a < floor ? floor : a > aMax ? aMax : a;
                        outPx[i] = (byte)(b + (bg.B - b) * a);
                        outPx[i + 1] = (byte)(gr + (bg.G - gr) * a);
                        outPx[i + 2] = (byte)(r + (bg.R - r) * a);
                        outPx[i + 3] = 255;
                    }
                Marshal.Copy(outPx, 0, data.Scan0, outPx.Length);
            }
            finally { tiny.UnlockBits(data); }
        }

        static float Smooth(float a, float b, float x)
        {
            float t = (x - a) / (b - a);
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        // Running-sum box blur, rows then columns
        static void BoxBlur(byte[] px, int stride, int w, int h, int r)
        {
            var tmp = new byte[px.Length];
            int span = 2 * r + 1;
            for (int y = 0; y < h; y++)
                for (int c = 0; c < 3; c++)
                {
                    int row = y * stride + c, sum = 0;
                    for (int k = -r; k <= r; k++) sum += px[row + Math.Max(0, Math.Min(w - 1, k)) * 4];
                    for (int x = 0; x < w; x++)
                    {
                        tmp[row + x * 4] = (byte)(sum / span);
                        sum += px[row + Math.Min(w - 1, x + r + 1) * 4] - px[row + Math.Max(0, x - r) * 4];
                    }
                }
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                {
                    int col = x * 4 + c, sum = 0;
                    for (int k = -r; k <= r; k++) sum += tmp[Math.Max(0, Math.Min(h - 1, k)) * stride + col];
                    for (int y = 0; y < h; y++)
                    {
                        px[y * stride + col] = (byte)(sum / span);
                        sum += tmp[Math.Min(h - 1, y + r + 1) * stride + col] - tmp[Math.Max(0, y - r) * stride + col];
                    }
                }
        }

        // Vibrancy: push channels away from grey
        static float Sat(float grey, float c, float amount)
        {
            float v = grey + (c - grey) * amount;
            return v < 0 ? 0 : v > 255 ? 255 : v;
        }

        // Frost thread: panel surface, cover optional
        static void BakePlate(Bitmap dst, Bitmap baseFrost, FrostJob job, Bitmap backdrop)
        {
            int bw = dst.Width, bh = dst.Height;
            using (var pg = Graphics.FromImage(dst))
            {
                pg.CompositingMode = CompositingMode.SourceCopy;
                pg.DrawImage(baseFrost, new Rectangle(0, 0, bw, bh), job.Bake.X, job.Bake.Y, bw, bh, GraphicsUnit.Pixel);
                pg.CompositingMode = CompositingMode.SourceOver;
                using (var tb = new SolidBrush(Ink.A(job.Bg, job.Extra))) pg.FillRectangle(tb, 0, 0, bw, bh);
                // Same inner light as the live path
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, bw, job.Sheen), Color.FromArgb(InnerLightAlpha(job.Bg.R > 128), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    lg.WrapMode = WrapMode.TileFlipXY;
                    pg.FillRectangle(lg, 0, 0, bw, job.Sheen);
                }
                if (backdrop != null) pg.DrawImage(backdrop, new Rectangle(0, 0, backdrop.Width, backdrop.Height), 0, 0, backdrop.Width, backdrop.Height, GraphicsUnit.Pixel);
            }
        }

        // UI thread: swap in newest glass
        void AcceptPlate(Plate incoming)
        {
            if (!frostRun) { incoming.Dispose(); return; }
            var old = plate;
            plate = incoming;
            plateUtc = DateTime.UtcNow;
            if (old != null)
            {
                // Panel closed: free its big plate
                if ((long)old.Job.W * old.Job.H > 2L * incoming.Job.W * incoming.Job.H || (old.Plain != null && incoming.Plain == null)) old.Dispose();
                else
                {
                    var prev = Interlocked.Exchange(ref plateSpare, old);
                    if (prev != null) prev.Dispose();
                }
            }
            Kick();
        }

        // Spare plate helps only while glass changes
        void DropSpareGlass()
        {
            if ((DateTime.UtcNow - plateUtc).TotalSeconds < 5) return;
            var spare = Interlocked.Exchange(ref plateSpare, null);
            if (spare != null) spare.Dispose();
        }

        // Caret blinks and clock digits keep glass
        static bool Similar(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = a[i] - b[i];
                if (d < 0) d = -d;
                if (d > 64) return false;
                sum += d;
            }
            return sum <= a.Length;
        }

        // Glass or solid; rim on every material
        void FillSurface(GraphicsPath path, RectangleF bounds, Tone tone, float tint, int pxW)
        {
            bool light = tone.Bg.R > 128;
            // No rim across the bubble's join
            bool joined = bridgeOn && bounds == shell;
            if (joined) bridgeOn = false;
            if (!GlassOn)
            {
                g.FillPath(p.B(tone.Bg), path);
                RimOutside(path, bounds, light, true, joined);
                return;
            }
            var st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            int left = WindowLeft(pxW);
            var pl = plate;
            int fx = pl != null ? pl.Job.Left - left : 0;
            bool covered = pl != null && fx <= Math.Floor(bounds.X * scale) && fx + pl.Job.W >= Math.Ceiling(bounds.Right * scale) &&
                           pl.Job.H >= Math.Ceiling(bounds.Bottom * scale);
            bool baseTone = tone.Bg.R < 128 == Ink.Base.Bg.R < 128;
            // Frost has base tint; add the rest
            float extra = baseTone ? 1 - (1 - tint) / (1 - BaseTint) : tint;
            if (covered && baseTone && current == View.Panel && SurfaceSteady() && PlateFits(pl, fx, bounds, extra)) DrawPlate(pl, fx);
            else
            {
                // Translucent base outside the frost
                if (!covered) g.FillPath(p.B(Ink.A(tone.Bg, 0.62f)), path);
                if (pl != null)
                {
                    BlitPart(pl.Base, fx, bounds);
                    if (extra > 0.01f) g.FillPath(p.B(Ink.A(tone.Bg, extra)), path);
                }
                InnerLight(bounds, light);
            }
            // Thickness: darker line inside the rim
            if (joined) g.SetClip(bridge, CombineMode.Exclude);
            g.DrawPath(p.P(Color.FromArgb(light ? 22 : 54, 0, 0, 0), 3f), path);
            g.Restore(st);
            RimOutside(path, bounds, light, false, joined);
        }

        void RimOutside(GraphicsPath path, RectangleF bounds, bool light, bool solid, bool joined)
        {
            if (!joined) { DrawRim(path, bounds, light, solid); return; }
            var st = g.Save();
            g.SetClip(bridge, CombineMode.Exclude);
            DrawRim(path, bounds, light, solid);
            g.Restore(st);
        }

        static int InnerLightAlpha(bool light) { return light ? 46 : 17; }

        LinearGradientBrush lightBrush, rimBrush;
        Pen rimPen;
        readonly Blend rimBlend = new Blend(4);
        readonly Color[] glassPair = new Color[2];

        static LinearGradientBrush Vertical()
        {
            return new LinearGradientBrush(new RectangleF(0, 0, Ink.GradientSpan, Ink.GradientSpan), Color.White, Color.White, LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipXY };
        }

        // Turned a quarter: reset drops Vertical's rotation
        static void MapDown(LinearGradientBrush b, float y, float h)
        {
            b.ResetTransform();
            b.TranslateTransform(0, y);
            b.ScaleTransform(1, Math.Max(0.01f, h) / Ink.GradientSpan);
            b.RotateTransform(90);
        }

        // Soft light over the top third
        void InnerLight(RectangleF b, bool light)
        {
            if (lightBrush == null) lightBrush = Vertical();
            // Stops above the panel's first row
            float hl = Math.Min(b.Height * 0.35f, PanelTop - 8);
            glassPair[0] = Color.FromArgb((int)(InnerLightAlpha(light) * p.Alpha), 255, 255, 255);
            glassPair[1] = Color.FromArgb(0, 255, 255, 255);
            lightBrush.LinearColors = glassPair;
            MapDown(lightBrush, b.Y, hl);
            g.FillRectangle(lightBrush, b.X, b.Y, b.Width, hl);
        }

        // Rim: bright top, thin sides, lip
        void DrawRim(GraphicsPath path, RectangleF bounds, bool light, bool solid)
        {
            if (solid && light) { g.DrawPath(p.P(Color.FromArgb(30, 0, 0, 0), 1f), path); return; }
            if (rimBrush == null)
            {
                rimBrush = Vertical();
                rimPen = new Pen(Color.White, 1f);
                rimBlend.Positions = new[] { 0f, 0.4f, 0.72f, 1f };
                rimBlend.Factors = new[] { 0f, 1f, 1f, 0f };
            }
            int top = solid ? 46 : light ? 190 : 102, side = solid ? 10 : light ? 56 : 20, lip = solid ? 22 : light ? 120 : 41;
            float y = bounds.Y - 1, h = bounds.Height + 2;
            rimBlend.Positions[1] = Math.Max(0.05f, Math.Min(0.6f, Math.Min(26f, h * 0.4f) / h));
            rimBlend.Factors[3] = (top - lip) / (float)(top - side);
            rimBrush.Blend = rimBlend;
            glassPair[0] = Rim(top);
            glassPair[1] = Rim(side);
            rimBrush.LinearColors = glassPair;
            MapDown(rimBrush, y, h);
            rimPen.Brush = rimBrush;
            g.DrawPath(rimPen, path);
        }

        Color Rim(int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(alpha * p.Alpha))), 255, 255, 255); }

        // Plate matches this panel, tone, backdrop
        bool PlateFits(Plate pl, int fx, RectangleF bounds, float extra)
        {
            if (pl.Plain == null || !pl.Job.Panel || pl.Job.Bg != Ink.Base.Bg || pl.Job.Light != Ink.Light || Math.Abs(pl.Job.Extra - extra) > 0.005f) return false;
            int bx = fx + pl.Job.Bake.X;
            return Math.Abs(bx - bounds.X * scale) <= 1.01f && Math.Abs(pl.Job.Bake.Width - bounds.Width * scale) <= 1.01f &&
                   Math.Abs(pl.Job.Bake.Y - bounds.Y * scale) <= 1.01f;
        }

        void DrawPlate(Plate pl, int fx)
        {
            bool cover = mediaInfo.Present && BackdropShown(DateTime.UtcNow) && pl.Cover != null && pl.Job.BackdropGen == backdropGen;
            Blit(cover ? pl.Cover : pl.Plain, fx + pl.Job.Bake.X, pl.Job.Bake.Y, true);
            backdropInSurface = cover;
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

        // Cached surface is valid; the panel may still be changing height
        bool SurfaceSteady()
        {
            return flip < 0.001f && sw.Settled && sr.Settled && topGap.Settled &&
                   (DateTime.UtcNow - switchedUtc).TotalSeconds > 0.5 && (current == View.Panel || sh.Settled);
        }

        // TEPI_DUMP: frames, since glass skips screenshots
        static readonly string dumpDir = Environment.GetEnvironmentVariable("TEPI_DUMP");
        DateTime nextDumpUtc;
        Rectangle dumpAt;
        int dumpBusy;

        // Outside frame timing; PNG encoding off-thread
        void DumpFrame()
        {
            if (dumpDir == null || dumpAt.Width <= 0 || DateTime.UtcNow < nextDumpUtc) return;
            if (Interlocked.CompareExchange(ref dumpBusy, 1, 0) != 0) return;
            nextDumpUtc = DateTime.UtcNow.AddMilliseconds(250);
            Bitmap bmp = null;
            try
            {
                var r = dumpAt;
                bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                using (var dg = Graphics.FromImage(bmp))
                {
                    dg.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                    dg.DrawImage(canvas, new Rectangle(0, 0, r.Width, r.Height), new Rectangle(0, 0, r.Width, r.Height), GraphicsUnit.Pixel);
                }
            }
            catch (Exception ex)
            {
                Log.Write("dump: " + ex.Message);
                if (bmp != null) bmp.Dispose();
                dumpBusy = 0;
                return;
            }
            string name = current == View.Panel ? "panel-" + PageNames[page] : current.ToString();
            var shot = bmp;
            // The island alone, alpha kept
            var raw = canvas.Clone(new Rectangle(0, 0, dumpAt.Width, dumpAt.Height), PixelFormat.Format32bppArgb);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Directory.CreateDirectory(dumpDir);
                    shot.Save(Path.Combine(dumpDir, "latest.png"), ImageFormat.Png);
                    shot.Save(Path.Combine(dumpDir, name + ".png"), ImageFormat.Png);
                    raw.Save(Path.Combine(dumpDir, "raw-" + name + ".png"), ImageFormat.Png);
                }
                catch (Exception ex) { Log.Write("dump: " + ex.Message); }
                finally { shot.Dispose(); raw.Dispose(); dumpBusy = 0; }
            });
        }
    }
}
