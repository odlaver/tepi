using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    // Toasts: swipe away, pause under the pointer, show time left
    partial class Island
    {
        DateTime toastDue;
        double toastSpanMs = 1, toastLeftMs = -1;
        float swipeFrom = float.NaN;
        bool swiping;
        int swipeOut;
        readonly Spring swipe = new Spring(0, 1, 1, 0.5f);

        static bool Swipeable(View v)
        {
            return v == View.Notif || v == View.Info || v == View.Done || v == View.Track || v == View.Network || v == View.Download;
        }

        void ArmToast(double ms)
        {
            ms = Math.Max(1, ms);
            toastTimer.Stop();
            toastTimer.Interval = (int)ms;
            toastTimer.Start();
            toastDue = DateTime.UtcNow.AddMilliseconds(ms);
            toastSpanMs = ms;
            toastLeftMs = -1;
        }

        // Reading under the pointer: the clock stops
        void PauseToast()
        {
            if (toast == null || toast.Hold || !toastTimer.Enabled || toastLeftMs >= 0 || !Swipeable(toast.View)) return;
            toastLeftMs = Math.Max(0, (toastDue - DateTime.UtcNow).TotalMilliseconds);
            toastTimer.Stop();
            Kick();
        }

        void ResumeToast()
        {
            if (toastLeftMs < 0) return;
            double ms = Math.Max(1200, toastLeftMs);
            toastLeftMs = -1;
            if (toast == null) return;
            toastTimer.Interval = (int)ms;
            toastTimer.Start();
            toastDue = DateTime.UtcNow.AddMilliseconds(ms);
            toastSpanMs = Math.Max(toastSpanMs, ms);
            Kick();
        }

        float ToastLeft()
        {
            double left = toastLeftMs >= 0 ? toastLeftMs : (toastDue - DateTime.UtcNow).TotalMilliseconds;
            return Ease.Clamp01((float)(left / toastSpanMs));
        }

        // Hairline under the toast that runs out with it
        void DrawToastClock(View v, float w, float h)
        {
            if (toast == null || toast.View != v || toast.Hold || !Swipeable(v) || v == View.Track || toastSpanMs < 2500) return;
            if (!toastTimer.Enabled && toastLeftMs < 0) return;
            float inset = Math.Min(h / 2, 22), full = w - inset * 2;
            var area = new RectangleF(inset, h - 4, full, 2);
            var item = toast.Data as NotifItem;
            var brand = v == View.Notif ? BrandInk(item) : Color.Empty;
            var ink = brand.IsEmpty ? p.T.Faint : brand;
            LiveRect(area, 6, () => (int)(ToastLeft() * full * scale), t => p.FillRound(new RectangleF(area.X, area.Y, Math.Max(2, full * ToastLeft()), 2), 1, Ink.A(ink, 0.7f)));
        }

        // Down on a toast: maybe the start of a swipe
        void SwipeDown(PointF pt)
        {
            swipeFrom = !expanded && toast != null && Swipeable(current) && current == toast.View && dragId == null ? pt.X : float.NaN;
            swiping = false;
        }

        bool SwipeMove()
        {
            if (float.IsNaN(swipeFrom) || (Control.MouseButtons & MouseButtons.Left) == 0) return false;
            float dx = mouse.X - swipeFrom;
            if (!swiping && Math.Abs(dx) < 6) return false;
            swiping = true;
            pressKey = null;
            swipe.Snap(dx);
            Kick();
            return true;
        }

        bool SwipeUp()
        {
            swipeFrom = float.NaN;
            if (!swiping) return false;
            swiping = false;
            pressKey = null;
            float dx = swipe.Value;
            if (Math.Abs(dx) > Math.Max(60, shell.Width * 0.25f)) Fling(Math.Sign(dx));
            else { Springs.Tune(swipe, 0.3f, 0.8f); swipe.Target = 0; }
            Kick();
            return true;
        }

        void Fling(int dir)
        {
            if (Anim.Reduced) { swipe.Snap(0); DismissToast(); return; }
            Springs.Tune(swipe, 0.22f, 1f);
            swipe.Target = dir * (shell.Width + 40);
            swipeOut = dir;
            Kick();
        }

        // Touchpad sideways flick
        float hwheel;
        void SwipeWheel(int delta)
        {
            if (expanded || toast == null || !Swipeable(current) || swipeOut != 0) return;
            hwheel += delta;
            if (Math.Abs(hwheel) < 120) return;
            Fling(Math.Sign(hwheel));
            hwheel = 0;
        }

        void StepSwipe(float dt)
        {
            swipe.Step(dt);
            if (swipeOut != 0 && Math.Abs(swipe.Value) >= shell.Width) { swipeOut = 0; swipe.Snap(0); DismissToast(); }
        }

        void ResetSwipe()
        {
            swiping = false;
            swipeFrom = float.NaN;
            swipeOut = 0;
            swipe.Snap(0);
            toastLeftMs = -1;
            hwheel = 0;
        }

        float SwipeFade(View v) { return v == current && swipe.Value != 0 ? 1 - Math.Min(1, Math.Abs(swipe.Value) / (shell.Width * 0.9f)) : 1; }
    }
}
