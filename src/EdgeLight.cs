using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Tepi
{
    // Progress traced along the island's edge
    partial class Island
    {
        PointF[] edgePts;
        float[] edgeLen;
        int edgeStart;
        string edgeKey;
        int edgeSig = -1;
        DateTime celebrateUtc = DateTime.MinValue;

        bool EdgeOn { get { return settings.GetBool("edge.light", true); } }

        // 0..1 for timer, download, plan, charge; else -1
        float EdgeFraction(DateTime now)
        {
            if (!EdgeOn || expanded) return -1;
            switch (current)
            {
                case View.Timer:
                    if (!Counting) return -1;
                    double total = (timerEndUtc - timerStartUtc).TotalSeconds;
                    return total <= 0 ? -1 : Ease.Clamp01((float)(1 - Remaining().TotalSeconds / total));
                case View.Download:
                    var d = ActiveDownload();
                    return d != null && d.Percent >= 0 ? d.Percent / 100f : -1;
                case View.Agent:
                    var s = BusySession();
                    return s != null && s.Plan != null && s.Plan.Count > 0 ? (float)PlanDone(s) / s.Plan.Count : -1;
                case View.Idle:
                    return hasBattery && !onAc ? -1 : hasBattery && battery < 100 ? battery / 100f : -1;
            }
            return -1;
        }

        // Clock tick: redraw once the line has moved a pixel
        bool EdgeTick()
        {
            float f = EdgeFraction(DateTime.UtcNow);
            int sig = f < 0 ? -1 : (int)(f * (edgeLen != null && edgeLen.Length > 0 ? edgeLen[edgeLen.Length - 1] : 600) * scale);
            if (sig == edgeSig) return false;
            edgeSig = sig;
            return true;
        }

        // Outline flattened once per shape
        void EdgeShape(GraphicsPath path, RectangleF r)
        {
            int at = PlaceNow;
            string key = r.Width.ToString("0") + "x" + r.Height.ToString("0") + (Docked ? "d" : "f") + at;
            if (key == edgeKey) return;
            edgeKey = key;
            using (var flat = (GraphicsPath)path.Clone())
            {
                flat.Flatten(null, 0.3f);
                edgePts = flat.PathPoints;
            }
            if (edgePts.Length < 3) { edgePts = null; return; }
            // Far edge is one long segment: split it at the middle
            bool side = at == PlaceLeft || at == PlaceRight;
            // Top: lowest crossing; sides: the one farthest from the edge
            float sign = at == PlaceRight ? -1 : 1;
            float mid = side ? r.Y + r.Height / 2 : r.X + r.Width / 2, far = float.MinValue;
            int seg = -1;
            for (int i = 0; i < edgePts.Length; i++)
            {
                PointF a = edgePts[i], b = edgePts[(i + 1) % edgePts.Length];
                float au = side ? a.Y : a.X, bu = side ? b.Y : b.X;
                if ((au - mid) * (bu - mid) > 0 || au == bu) continue;
                float av = side ? a.X : a.Y, bv = side ? b.X : b.Y;
                float v = (av + (bv - av) * (mid - au) / (bu - au)) * (side ? sign : 1);
                if (v > far) { far = v; seg = i; }
            }
            if (seg < 0) { edgePts = null; return; }
            var list = new List<PointF>(edgePts);
            float farV = side ? far * sign : far;
            list.Insert(seg + 1, side ? new PointF(farV, mid) : new PointF(mid, farV));
            edgePts = list.ToArray();
            edgeStart = seg + 1;
            edgeLen = new float[edgePts.Length + 1];
            for (int i = 1; i <= edgePts.Length; i++)
            {
                var a = edgePts[(edgeStart + i - 1) % edgePts.Length];
                var b = edgePts[(edgeStart + i) % edgePts.Length];
                edgeLen[i] = edgeLen[i - 1] + (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }
        }

        // Point at a distance along the loop
        PointF EdgeAt(float d, bool forward)
        {
            int n = edgePts.Length;
            float total = edgeLen[n];
            float along = forward ? d : total - d;
            int i = Array.BinarySearch(edgeLen, along);
            if (i < 0) i = ~i;
            i = Math.Max(1, Math.Min(n, i));
            float span = edgeLen[i] - edgeLen[i - 1], k = span <= 0 ? 0 : (along - edgeLen[i - 1]) / span;
            PointF a = edgePts[(edgeStart + i - 1) % n], b = edgePts[(edgeStart + i) % n];
            return new PointF(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
        }

        // Exact ends, corners between
        List<PointF> EdgeRun(float from, float to, bool forward)
        {
            var pts = new List<PointF>();
            if (to <= from) return pts;
            int n = edgePts.Length;
            float total = edgeLen[n];
            pts.Add(EdgeAt(from, forward));
            for (int i = 0; i <= n; i++)
            {
                float d = forward ? edgeLen[i] : total - edgeLen[n - i];
                if (d <= from) continue;
                if (d >= to) break;
                pts.Add(edgePts[(edgeStart + (forward ? i : n - i)) % n]);
            }
            pts.Add(EdgeAt(to, forward));
            return pts;
        }

        void Stroke(List<PointF> pts, Color c, float width)
        {
            if (pts.Count < 2) return;
            using (var pen = new Pen(c, width) { StartCap = LineCap.Flat, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts.ToArray());
        }

        void Glow(List<PointF> a, List<PointF> b, float strength)
        {
            Color c = p.T.Accent;
            foreach (var pts in new[] { a, b })
            {
                Stroke(pts, Ink.A(c, 0.16f * strength * p.Alpha), 9f);
                Stroke(pts, Ink.A(c, 0.9f * strength * p.Alpha), 3.4f);
            }
        }

        // Inside the shell clip, after content
        void DrawEdgeLight(DateTime now, GraphicsPath path, RectangleF r)
        {
            if (!EdgeOn) return;
            float f = EdgeFraction(now);
            double since = toast != null && toast.View == View.Notif ? (now - toast.ShownUtc).TotalSeconds : 9;
            bool sweep = since < 0.9 && !Anim.Reduced;
            if (f < 0 && !sweep) { DrawConfetti(now, r); return; }
            EdgeShape(path, r);
            if (edgePts == null) return;
            float half = edgeLen[edgeLen.Length - 1] / 2;
            if (f >= 0) Glow(EdgeRun(0, half * f, true), EdgeRun(0, half * f, false), 1);
            // New message: light runs up both sides
            if (sweep)
            {
                float k = Ease.OutCubic((float)(since / 0.9)), head = half * k, tail = Math.Max(0, head - half * 0.35f);
                Glow(EdgeRun(tail, head, true), EdgeRun(tail, head, false), 1 - k * 0.6f);
            }
            DrawConfetti(now, r);
        }

        bool EdgeMoving(DateTime now)
        {
            bool sweep = EdgeOn && !Anim.Reduced && toast != null && toast.View == View.Notif && (now - toast.ShownUtc).TotalSeconds < 0.9;
            return sweep || (now - celebrateUtc).TotalSeconds < ConfettiSec;
        }

        // Keeps maximized windows below the island
        int reservedPx;

        public void ApplyReserve()
        {
            if (!IsHandleCreated) return;
            // Only a top island takes a strip
            int want = settings.GetBool("reserve.top", false) && !hiddenByUser && !OnSide ? (int)Math.Ceiling((Gap + 38 + 6) * scale) : 0;
            if (want == reservedPx && want == 0) return;
            var abd = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle, uEdge = 1 };
            abd.rc = new Native.RECT { Left = monitorPx.Left, Top = monitorPx.Top, Right = monitorPx.Right, Bottom = monitorPx.Top + want };
            // ABM_QUERYPOS then ABM_SETPOS
            Native.SHAppBarMessage(2, ref abd);
            abd.rc.Bottom = abd.rc.Top + want;
            Native.SHAppBarMessage(3, ref abd);
            reservedPx = want;
        }

        // Last task due today: a small party
        const double ConfettiSec = 1.8;

        void TaskFinished(KuliaTask task)
        {
            bool today = task.DeadlineUtc.ToLocalTime().Date == DateTime.Today;
            bool left = tasks != null && tasks.Exists(t => t != task && t.DeadlineUtc.ToLocalTime().Date == DateTime.Today);
            if (!today || left) { ShowInfo('\0', Lang.T("Ditandai selesai"), task.Title, true); return; }
            var t2 = ShowToast(View.Info, 4, 3, false, true);
            if (t2 != null)
            {
                t2.Glyph = '';
                t2.Title = Lang.T("Tugas hari ini beres");
                t2.Detail = task.Title;
            }
            EyesReact(EyeMood.Happy, 0);
            if (!Anim.Reduced) celebrateUtc = DateTime.UtcNow;
            Kick();
        }

        void DrawConfetti(DateTime now, RectangleF r)
        {
            double t = (now - celebrateUtc).TotalSeconds;
            if (t >= ConfettiSec || t < 0) return;
            Color[] ink = { p.T.Accent, p.T.Text, p.T.Sub };
            float fade = 1 - Ease.Clamp01((float)((t - 1.2) / 0.6));
            for (int i = 0; i < 22; i++)
            {
                // Fixed seeds: same burst each time
                double a = i * 2.39996, speed = 60 + (i * 37 % 50);
                float x = r.X + r.Width / 2 + (float)(Math.Cos(a) * speed * t * 1.6);
                float y = r.Y + r.Height * 0.35f + (float)(Math.Sin(a) * speed * 0.5 * t - 10 * t + 120 * t * t);
                var st = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform((float)(i * 47 + t * 360 * (i % 2 == 0 ? 1 : -1)));
                using (var b = new SolidBrush(Ink.A(ink[i % 3], fade * p.Alpha))) g.FillRectangle(b, -3f, -1.5f, 6f, 3f);
                g.Restore(st);
            }
        }
    }
}
