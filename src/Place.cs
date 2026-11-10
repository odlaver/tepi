using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Tepi
{
    // Where the island lives: top edge, its corners, or a side
    partial class Island
    {
        const int PlaceTop = 0, PlaceTopLeft = 1, PlaceTopRight = 2, PlaceLeft = 3, PlaceRight = 4;
        static readonly string[] PlaceIds = { "top", "top-left", "top-right", "left", "right" };
        static readonly string[] PlaceNames = { "Atas tengah", "Pojok kiri atas", "Pojok kanan atas", "Sisi kiri", "Sisi kanan" };
        // Floating island's distance from a side edge
        const float SideFloat = 10;
        int placeMode;
        // Corner inset (1x px); side top as share of height
        float placeInset, placeY;
        // Canvas origin this frame; last presented
        int frameLeft, frameTop, lastTop;
        readonly Spring sideGap = new Spring(SideFloat, 380, 24, 0.02f);
        // Old spot minus new, in device px; glides to 0
        readonly Spring placeGlide = new Spring(0, 170, 22, 0.3f);

        void LoadPlace()
        {
            placeMode = Math.Max(0, Array.IndexOf(PlaceIds, settings.Get("place", "top")));
            placeInset = Clamp(settings.GetInt("place.inset", 16), 0, 320);
            placeY = Clamp(settings.GetInt("place.y", 22), 0, 100) / 100f;
        }

        // Script and camera mirror stay under the camera
        int PlaceNow { get { return PrompterOn || MirrorOn ? PlaceTop : placeMode; } }
        bool OnSide { get { int at = PlaceNow; return at == PlaceLeft || at == PlaceRight; } }
        // Right edge near: extras sit on the left
        bool RightHanded { get { int at = PlaceNow; return at == PlaceTopRight || at == PlaceRight; } }

        // Mini hugs the edge even when floating
        float SideGapTarget { get { return Docked || current == View.Mini ? 0 : SideFloat; } }

        // Screen x of the canvas centre line, device px
        float MidX(float shellW)
        {
            var mon = monitorPx;
            float glide = placeGlide.Value;
            switch (PlaceNow)
            {
                case PlaceTopLeft: return mon.Left + (placeInset + shellW / 2) * scale + glide;
                case PlaceTopRight: return mon.Right - (placeInset + shellW / 2) * scale + glide;
                case PlaceLeft: return mon.Left + (sideGap.Value + shellW / 2) * scale;
                case PlaceRight: return mon.Right - (sideGap.Value + shellW / 2) * scale;
            }
            return CenterX(mon) + glide;
        }

        // Screen y of the canvas top, device px; sides stay on screen
        int TopY(float shellH)
        {
            var mon = monitorPx;
            if (!OnSide) return mon.Top;
            float m = 8 * scale, want = mon.Top + placeY * mon.Height;
            float y = Math.Max(mon.Top + m, Math.Min(mon.Bottom - m - shellH * scale, want));
            return (int)Math.Round(y - Gap * scale);
        }

        // Shell top inside the canvas
        float ShellTop { get { return OnSide || !Docked ? Gap : 0; } }

        // Canvas origin for this frame's size
        Point CanvasAt(int pxW, float shellW, float shellH)
        {
            if (PlaceNow == PlaceTop && Math.Abs(placeGlide.Value) < 0.5f) return new Point(WindowLeft(pxW), monitorPx.Top);
            return new Point((int)Math.Round(MidX(shellW)) - pxW / 2, TopY(shellH));
        }

        GraphicsPath ShellPath(RectangleF r, float radius)
        {
            if (!Docked) return Ink.Round(r, radius);
            int at = PlaceNow;
            if (at == PlaceLeft || at == PlaceRight) return Ink.SideNotch(r, radius, 10, at == PlaceRight);
            return Ink.Notch(r, radius, 10);
        }

        void SetPlace(int mode)
        {
            if (mode == placeMode) return;
            bool wasSide = OnSide;
            float before = MidX(sw.Value);
            settings.Set("place", PlaceIds[mode]);
            settings.Save();
            LoadPlace();
            if (wasSide || OnSide)
            {
                // Across edges: fade in at the new spot
                placeGlide.Snap(0);
                sideGap.Snap(SideGapTarget);
                opacity.Snap(0);
            }
            else if (!Anim.Reduced)
            {
                // Along the top: glide over
                placeGlide.Snap(0);
                placeGlide.Snap(before - MidX(sw.Value));
                placeGlide.Target = 0;
            }
            NudgeFrost();
            ResetFrost();
            ApplyReserve();
            Kick();
        }

        bool PlaceMoving() { return !placeGlide.Settled || !sideGap.Settled; }

        void StepPlace(float dt)
        {
            sideGap.Target = SideGapTarget;
            bool moving = PlaceMoving();
            placeGlide.Step(dt);
            sideGap.Step(dt);
            // Glass follows the glide
            if (moving && GlassOn) NeedFrost(sh.Target);
        }

        // Capture box over the shell now and at its target
        FrostJob AnchoredFrost(float targetH, float wide, float bubbleW, out int w, out int h)
        {
            var mon = monitorPx;
            float lo = float.MaxValue, hi = float.MinValue;
            Span(sw.Value, ref lo, ref hi);
            Span(sw.Target, ref lo, ref hi);
            if (wide > 0) Span(wide, ref lo, ref hi);
            if (RightHanded) lo -= bubbleW * scale; else hi += bubbleW * scale;
            int left = Math.Max(mon.Left, (int)Math.Floor(lo - FrostPad * scale));
            int right = Math.Min(mon.Right, (int)Math.Ceiling(hi + FrostPad * scale));
            float hNow = Math.Max(sh.Value, BubbleR * 2), hWant = Math.Max(targetH, BubbleR * 2);
            int t0 = TopY(hNow), t1 = TopY(hWant);
            int top = Math.Max(mon.Top, Math.Min(t0, t1));
            int bottom = Math.Max(t0 + (int)Math.Ceiling((ShellTop + hNow + FrostPad) * scale), t1 + (int)Math.Ceiling((ShellTop + hWant + FrostPad) * scale));
            bottom = Math.Min(mon.Bottom, bottom);
            w = Math.Max(64, right - left);
            h = Math.Max(40, bottom - top);
            return new FrostJob { W = w, H = h, Left = left, Top = top };
        }

        void Span(float shellW, ref float lo, ref float hi)
        {
            float mid = MidX(shellW), half = shellW * scale / 2;
            lo = Math.Min(lo, mid - half);
            hi = Math.Max(hi, mid + half);
        }

        // Canvas past the monitor stays empty: no shadow on the next screen
        void ClearOffMonitor(int left, int top, int pxW, int pxH)
        {
            var mon = monitorPx;
            if (left >= mon.Left && left + pxW <= mon.Right && top >= mon.Top && top + pxH <= mon.Bottom) return;
            var keep = new Rectangle(mon.Left - left, mon.Top - top, mon.Width, mon.Height);
            var st = g.Save();
            g.ResetTransform();
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var off = new Region(new Rectangle(0, 0, pxW, pxH)))
            {
                off.Exclude(keep);
                g.SetClip(off, CombineMode.Replace);
                g.Clear(Color.Transparent);
            }
            g.Restore(st);
        }
    }
}
