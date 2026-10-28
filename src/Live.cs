using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Tepi
{
    // One live area: place, state, schedule
    sealed class LiveSlot
    {
        public Rectangle Px;
        public float Ox, Oy, Sx, Sy, Alpha;
        public Tone Tone;
        public bool Shadow, Clip, Hot;
        public SmoothingMode Smooth;
        public InterpolationMode Interp;
        public PixelOffsetMode Offset;
        public TextRenderingHint TextHint;
        public double Period, Slack, Due;
        public Action<DateTime> Draw;
        public Func<DateTime, double> Next;
        // Optional: polled at fps, redraw on change
        public Func<int> Sig;
        public int LastSig;
        public byte[] Under;
    }

    // Small parts redrawn alone between full frames
    partial class Island
    {
        // Motion at 30 fps; times each second
        const float LiveFps = 30, TickFps = 1;
        readonly List<LiveSlot> slots = new List<LiveSlot>();
        int slotCount, liveInline, framePxW, framePxH;
        // Steady frames record; partial frames replay
        bool liveRecord, liveReady, liveInShell, inRender, inSlot;
        // Drawn in place: whole frames this often
        double wholePeriod, livePower = 1, frameT;
        DateTime frameNow;
        Rectangle cornerPx;
        IntPtr dibBits;
        GraphicsPath liveShell;
        RectangleF liveShellRect;
        float liveShellR;
        readonly PointF[] livePts = new PointF[3];
        readonly int[] dirtyBuf = new int[8];
        GCHandle dirtyPin;
        static readonly int UlwSize = Marshal.SizeOf(typeof(Native.ULWINFO));

        // Draws now, then alone at fps
        void LiveRect(RectangleF local, float fps, Action<DateTime> draw) { LiveRect(local, fps, null, draw); }

        // Polls sig at fps, redraws on change
        void LiveRect(RectangleF local, float fps, Func<int> sig, Action<DateTime> draw)
        {
            if (fps <= 0 || !inRender || inSlot) { draw(frameNow); return; }
            Declare(local, 1.0 / fps, draw, null, sig);
        }

        // Draw returns seconds to next redraw
        void LiveRect(RectangleF local, Func<DateTime, double> draw)
        {
            if (!inRender || inSlot) { draw(frameNow); return; }
            Declare(local, 0, null, draw, null);
        }

        void Declare(RectangleF local, double period, Action<DateTime> draw, Func<DateTime, double> next, Func<int> sig)
        {
            int placed = liveRecord ? Place(local) : -1;
            if (placed > 0)
            {
                var s = slots[slotCount - 1];
                s.Period = period;
                s.Draw = draw;
                s.Next = next;
                s.Sig = sig;
                return;
            }
            if (placed == 0) return;
            double wait = period;
            if (draw != null) draw(frameNow); else wait = next(frameNow);
            if (wait <= 0) return;
            liveInline++;
            wholePeriod = wholePeriod > 0 ? Math.Min(wholePeriod, wait) : wait;
        }

        // 1 recorded, 0 hidden, -1 in place
        int Place(RectangleF local)
        {
            livePts[0] = new PointF(0, 0);
            livePts[1] = new PointF(1, 0);
            livePts[2] = new PointF(0, 1);
            g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, livePts);
            float ox = livePts[0].X, oy = livePts[0].Y, sx = livePts[1].X - ox, sy = livePts[2].Y - oy;
            // Rotated or mirrored: drawn in place
            if (Math.Abs(livePts[1].Y - oy) > 0.001f || Math.Abs(livePts[2].X - ox) > 0.001f || sx <= 0 || sy <= 0) return -1;
            var cb = g.ClipBounds;
            int x0 = Math.Max(0, Math.Max((int)Math.Floor(ox + local.Left * sx) - 1, (int)Math.Floor(ox + cb.Left * sx)));
            int y0 = Math.Max(0, Math.Max((int)Math.Floor(oy + local.Top * sy) - 1, (int)Math.Floor(oy + cb.Top * sy)));
            int x1 = Math.Min(framePxW, Math.Min((int)Math.Ceiling(ox + local.Right * sx) + 1, (int)Math.Ceiling(ox + cb.Right * sx)));
            int y1 = Math.Min(framePxH, Math.Min((int)Math.Ceiling(oy + local.Bottom * sy) + 1, (int)Math.Ceiling(oy + cb.Bottom * sy)));
            if (x1 <= x0 || y1 <= y0) return 0;
            var px = Rectangle.FromLTRB(x0, y0, x1, y1);
            bool clip = false;
            if (liveInShell && liveShell != null)
            {
                float k = 1f / scale;
                clip = !(liveShell.IsVisible(x0 * k, y0 * k) && liveShell.IsVisible(x1 * k, y0 * k) && liveShell.IsVisible(x0 * k, y1 * k) && liveShell.IsVisible(x1 * k, y1 * k));
            }
            LiveSlot s;
            if (slotCount < slots.Count) s = slots[slotCount];
            else { s = new LiveSlot(); slots.Add(s); }
            slotCount++;
            s.Px = px;
            s.Ox = ox; s.Oy = oy; s.Sx = sx; s.Sy = sy;
            s.Alpha = p.Alpha;
            s.Tone = p.T;
            s.Shadow = p.Shadow;
            s.Clip = clip;
            s.Smooth = g.SmoothingMode;
            s.Interp = g.InterpolationMode;
            s.Offset = g.PixelOffsetMode;
            s.TextHint = g.TextRenderingHint;
            return 1;
        }

        // No recording while dragging, focusing or flipping
        bool LiveAllowed()
        {
            return dibBits != IntPtr.Zero && flip <= 0.001f && !keyboardMode && dragId == null && snapshot == null && opacity.Settled && current != View.Prompter;
        }

        // Shell outline for clipping, kept while unchanged
        void KeepShell(GraphicsPath path, float r)
        {
            if (liveShell != null && liveShellRect == shell && liveShellR == r) return;
            if (liveShell != null) liveShell.Dispose();
            liveShell = (GraphicsPath)path.Clone();
            liveShellRect = shell;
            liveShellR = r;
        }

        // Full frame start: fresh records
        void BeginLive(DateTime now, int pxW, int pxH)
        {
            frameNow = now;
            framePxW = pxW;
            framePxH = pxH;
            slotCount = 0;
            liveInline = 0;
            wholePeriod = 0;
            if (!liveRecord) return;
            Native.SYSTEM_POWER_STATUS ps;
            livePower = Native.GetSystemPowerStatus(out ps) && ps.SystemStatusFlag == 1 ? 1.5 : 1;
        }

        // Save pixels under each area, then draw
        void FinishLive(DateTime now)
        {
            if (slotCount == 0) return;
            g.Flush(FlushIntention.Sync);
            for (int i = 0; i < slotCount; i++) SaveUnder(slots[i]);
            for (int i = 0; i < slotCount; i++) DrawSlot(slots[i], now, frameT);
        }

        // Partial frame: due areas, dirty rect only
        void LiveFrame(DateTime now, double t)
        {
            frameNow = now;
            int hot = 0;
            for (int i = 0; i < slotCount; i++)
            {
                var s = slots[i];
                s.Hot = s.Due > 0 && s.Due - t <= s.Slack;
                // Unchanged picture: skip, poll again later
                if (s.Hot && s.Sig != null && s.Sig() == s.LastSig) { s.Hot = false; s.Due = t + s.Period * livePower; }
                if (s.Hot) hot++;
            }
            if (hot == 0) return;
            // Overlapping neighbours redraw too, in order
            for (bool grew = true; grew; )
            {
                grew = false;
                for (int i = 0; i < slotCount; i++)
                {
                    if (slots[i].Hot) continue;
                    for (int j = 0; j < slotCount; j++)
                        if (slots[j].Hot && slots[i].Px.IntersectsWith(slots[j].Px)) { slots[i].Hot = true; grew = true; break; }
                }
            }
            var dirty = Rectangle.Empty;
            double p0 = Prof.T();
            for (int i = 0; i < slotCount; i++)
            {
                if (!slots[i].Hot) continue;
                Restore(slots[i]);
                dirty = dirty.IsEmpty ? slots[i].Px : Rectangle.Union(dirty, slots[i].Px);
            }
            double p1 = Prof.T();
            for (int i = 0; i < slotCount; i++) if (slots[i].Hot) DrawSlot(slots[i], now, t);
            g.Flush(FlushIntention.Sync);
            double p2 = Prof.T();
            PresentDirty(dirty);
            if (fpsLog) { liveRestoreMs += p1 - p0; liveDrawMs += p2 - p1; livePresentMs += Prof.T() - p2; }
            lastPresentTick = Environment.TickCount;
            presented.Set();
            CountLive(t, (clock.Elapsed.TotalSeconds - t) * 1000, dirty);
        }

        void DrawSlot(LiveSlot s, DateTime now, double t)
        {
            g.ResetTransform();
            g.SetClip(s.Px);
            if (s.Clip && liveShell != null)
            {
                g.ScaleTransform(scale, scale);
                g.SetClip(liveShell, CombineMode.Intersect);
                g.ResetTransform();
            }
            g.TranslateTransform(s.Ox, s.Oy);
            g.ScaleTransform(s.Sx, s.Sy);
            SmoothingMode smooth = g.SmoothingMode;
            InterpolationMode interp = g.InterpolationMode;
            PixelOffsetMode offset = g.PixelOffsetMode;
            TextRenderingHint hint = g.TextRenderingHint;
            g.SmoothingMode = s.Smooth;
            g.InterpolationMode = s.Interp;
            g.PixelOffsetMode = s.Offset;
            g.TextRenderingHint = s.TextHint;
            p.Alpha = s.Alpha;
            p.T = s.Tone;
            p.Shadow = s.Shadow;
            bool keepHits = hitsOn;
            hitsOn = false;
            inSlot = true;
            double wait = s.Period;
            try
            {
                if (s.Draw != null) s.Draw(now);
                else wait = s.Next(now);
            }
            catch (Exception ex) { Log.Write("live: " + ex.Message); wait = 0; }
            if (s.Sig != null) s.LastSig = s.Sig();
            g.SmoothingMode = smooth;
            g.InterpolationMode = interp;
            g.PixelOffsetMode = offset;
            g.TextRenderingHint = hint;
            // Mascot sits above content: redraw it
            if (!cornerPx.IsEmpty && s.Px.IntersectsWith(cornerPx))
            {
                g.ResetTransform();
                g.SetClip(s.Px);
                DrawCorner(now);
            }
            inSlot = false;
            hitsOn = keepHits;
            wait *= livePower;
            s.Due = wait > 0 ? t + wait : 0;
            s.Slack = Math.Min(0.012, wait * 0.4);
            g.ResetClip();
            g.ResetTransform();
            p.Alpha = 1f;
            p.T = Ink.Base;
            p.Shadow = false;
        }

        // Rows copied by hand, no GDI+ objects
        void SaveUnder(LiveSlot s)
        {
            int row = s.Px.Width * 4, n = row * s.Px.Height;
            if (s.Under == null || s.Under.Length < n || s.Under.Length > n * 4) s.Under = new byte[n];
            long at = dibBits.ToInt64() + ((long)s.Px.Y * canvasW + s.Px.X) * 4, stride = (long)canvasW * 4;
            for (int y = 0; y < s.Px.Height; y++) Marshal.Copy(new IntPtr(at + y * stride), s.Under, y * row, row);
        }

        void Restore(LiveSlot s)
        {
            int row = s.Px.Width * 4;
            long at = dibBits.ToInt64() + ((long)s.Px.Y * canvasW + s.Px.X) * 4, stride = (long)canvasW * 4;
            for (int y = 0; y < s.Px.Height; y++) Marshal.Copy(s.Under, y * row, new IntPtr(at + y * stride), row);
        }

        // Same window, only the dirty rect copied
        void PresentDirty(Rectangle r)
        {
            if (!dirtyPin.IsAllocated) dirtyPin = GCHandle.Alloc(dirtyBuf, GCHandleType.Pinned);
            IntPtr at = dirtyPin.AddrOfPinnedObject();
            dirtyBuf[0] = 0;
            dirtyBuf[1] = 0;
            dirtyBuf[2] = (int)((uint)(byte)(255 * Ease.Clamp01(opacity.Value)) << 16 | 1u << 24);
            dirtyBuf[4] = r.Left;
            dirtyBuf[5] = r.Top;
            dirtyBuf[6] = r.Right;
            dirtyBuf[7] = r.Bottom;
            var info = new Native.ULWINFO { cbSize = UlwSize, hdcSrc = memDC, pptSrc = at, pblend = at + 8, dwFlags = Native.ULW_ALPHA, prcDirty = at + 16 };
            if (Native.UpdateLayeredWindowIndirect(Handle, ref info)) return;
            Log.Write("live present: " + Marshal.GetLastWin32Error());
            liveReady = false;
            frameDirty = true;
        }

        // Compile panel chrome before its first frame
        static readonly string[] PanelChrome = { "DrawPanel", "DrawTabs", "DrawDots", "TabLayout", "DrawTabAction", "DrawHideButton", "TabButton", "DrawBackdrop", "BackdropShown", "DrawPlate", "PlateFits", "DrawSegments", "DrawSegmentPage", "DrawCorner" };
        bool chromeReady;

        void WarmChrome()
        {
            if (chromeReady) return;
            chromeReady = true;
            try
            {
                foreach (var m in typeof(Island).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    if (Array.IndexOf(PanelChrome, m.Name) >= 0) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
            catch (Exception ex) { Log.Write("warm chrome: " + ex.Message); }
        }

        // Bake tab icons before the panel opens
        void WarmTabs()
        {
            WarmChrome();
            if (g == null || current == View.Panel || (tabIcons.Count > 0 && tabIconEpoch == Ink.Epoch && tabIconScale == scale)) return;
            var keepG = g;
            var keepT = p.T;
            try
            {
                using (var bmp = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                using (var wg = Graphics.FromImage(bmp))
                {
                    g = wg;
                    p.G = wg;
                    p.T = Ink.Base;
                    foreach (int pg in tabs)
                        foreach (var c in new[] { Ink.Base.Sub, Ink.Base.Text, Ink.Base.OnAccent })
                            DrawTabIconFast(pg, 20, 20, c, false);
                }
            }
            catch (Exception ex) { Log.Write("warm tabs: " + ex.Message); }
            finally
            {
                g = keepG;
                p.G = keepG;
                p.T = keepT;
            }
        }

        long restHeap;

        // Collect at rest, never mid-slide
        void CollectAtRest()
        {
            long heap = GC.GetTotalMemory(false);
            if (heap < restHeap + 1024 * 1024) { if (heap < restHeap) restHeap = heap; return; }
            GC.Collect(0, GCCollectionMode.Forced, true);
            restHeap = GC.GetTotalMemory(false);
        }

        // Event says redraw: wake that area now
        void LiveWake(Func<DateTime, double> draw)
        {
            if (!liveReady || animating) return;
            bool found = false;
            double t = clock.Elapsed.TotalSeconds;
            for (int i = 0; i < slotCount; i++)
                if (slots[i].Next == draw) { slots[i].Due = t; found = true; }
            if (!found) return;
            ambientTimer.Stop();
            ambientTimer.Interval = 1;
            ambientTimer.Start();
        }

        bool LiveDue()
        {
            if (!liveReady) return false;
            for (int i = 0; i < slotCount; i++) if (slots[i].Due > 0) return true;
            return false;
        }

        // Next wake: due areas or whole frames
        int AmbientWait(bool whole)
        {
            double t = clock.Elapsed.TotalSeconds, wait = double.MaxValue;
            if (whole) wait = AmbientMs() / 1000.0;
            if (wholePeriod > 0) wait = Math.Min(wait, wholePeriod * livePower);
            if (liveReady)
                for (int i = 0; i < slotCount; i++)
                    if (slots[i].Due > 0) wait = Math.Min(wait, slots[i].Due - t);
            if (wait == double.MaxValue) wait = 0.05;
            return Math.Max(1, Math.Min(60000, (int)Math.Ceiling(wait * 1000)));
        }

        // TEPI_FPS: partial frames, CPU split, kick sources

        int liveFrames;
        double liveMs, liveWorst, liveRestoreMs, liveDrawMs, livePresentMs;
        long liveArea;
        readonly Dictionary<string, int> kickFrom = new Dictionary<string, int>();
        readonly int[] fullWhy = new int[5];
        static readonly string[] WhyNames = { "kick", "move", "record", "whole", "ambient" };
        static long frostCpu;
        long profCpu, profUi, profFrost;
        double profAt;

        void CountLive(double t, double ms, Rectangle dirty)
        {
            if (!fpsLog) return;
            liveFrames++;
            liveMs += ms;
            liveWorst = Math.Max(liveWorst, ms);
            liveArea += (long)dirty.Width * dirty.Height;
            StatsLine(t);
        }

        string LiveStats(double span)
        {
            if (liveFrames == 0) return "";
            string s = " | live " + (liveFrames / span).ToString("0.0") + "/s " + (liveMs / liveFrames).ToString("0.00") + "ms worst " + liveWorst.ToString("0.0") + "ms px " + (liveArea / liveFrames) +
                       " restore=" + (liveRestoreMs / liveFrames).ToString("0.00") + " draw=" + (liveDrawMs / liveFrames).ToString("0.00") + " present=" + (livePresentMs / liveFrames).ToString("0.00");
            liveFrames = 0;
            liveMs = liveWorst = liveRestoreMs = liveDrawMs = livePresentMs = 0;
            liveArea = 0;
            return s;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void CountKick()
        {
            if (!fpsLog) return;
            var m = new System.Diagnostics.StackFrame(2, false).GetMethod();
            string key = m == null ? "?" : m.Name;
            int n;
            kickFrom.TryGetValue(key, out n);
            kickFrom[key] = n + 1;
        }

        void CountWhy(int why) { if (fpsLog) fullWhy[why]++; }

        // Every 10 s: CPU split, frame reasons
        void ProfTick()
        {
            if (!fpsLog) return;
            double t = clock.Elapsed.TotalSeconds;
            long all = Native.ProcessCpu(), ui = Native.ThreadCpu(), frost = System.Threading.Interlocked.Read(ref frostCpu);
            if (profAt > 0 && t - profAt >= 9.5)
            {
                double span = (t - profAt) * 1e5;
                var sb = new System.Text.StringBuilder("cpu all=");
                sb.Append(((all - profCpu) / span).ToString("0.00")).Append("% ui=").Append(((ui - profUi) / span).ToString("0.00"));
                sb.Append("% frost=").Append(((frost - profFrost) / span).ToString("0.00")).Append("% priv=").Append(PrivateMb().ToString("0.0"));
                sb.Append("MB gc=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("0.0")).Append("MB full");
                for (int i = 0; i < fullWhy.Length; i++) { sb.Append(' ').Append(WhyNames[i]).Append('=').Append(fullWhy[i]); fullWhy[i] = 0; }
                sb.Append(" kicks");
                foreach (var kv in kickFrom) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                kickFrom.Clear();
                Log.Write(sb.ToString());
            }
            else if (profAt > 0) return;
            profAt = t;
            profCpu = all;
            profUi = ui;
            profFrost = frost;
        }
    }
}
