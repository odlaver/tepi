using System;
using System.Drawing;

namespace Tepi
{
    // Focus timer: work, then a short break
    partial class Island
    {
        enum FocusPhase { Off, Work, Break }

        FocusPhase focus;
        DateTime focusStartUtc, focusEndUtc;
        readonly Roller focusRoller = new Roller();

        bool FocusOn { get { return focus != FocusPhase.Off; } }

        void StartFocus(int minutes)
        {
            focus = FocusPhase.Work;
            focusStartUtc = DateTime.UtcNow;
            focusEndUtc = focusStartUtc.AddMinutes(minutes);
            ShowInfo('\0', "Fokus " + minutes + " menit", "selesai " + Fmt.Hm(focusEndUtc.ToLocalTime()), true);
        }

        void StopFocus()
        {
            focus = FocusPhase.Off;
            Kick();
        }

        // Each clock tick; true while a timer runs
        bool FocusTick(DateTime now)
        {
            if (!FocusOn) return false;
            if (now < focusEndUtc) return true;
            topGap.Velocity += 120;
            if (focus == FocusPhase.Work)
            {
                int rest = Math.Max(1, settings.GetInt("focus.break", 5));
                focus = FocusPhase.Break;
                focusStartUtc = now;
                focusEndUtc = now.AddMinutes(rest);
                ShowInfo('\0', "Fokus selesai", "istirahat " + rest + " menit", true);
            }
            else
            {
                focus = FocusPhase.Off;
                ShowInfo('\0', "Istirahat selesai", "siap fokus lagi", true);
            }
            return true;
        }

        float FocusLeft()
        {
            double total = (focusEndUtc - focusStartUtc).TotalSeconds;
            return total <= 0 ? 0 : Ease.Clamp01((float)((focusEndUtc - DateTime.UtcNow).TotalSeconds / total));
        }

        string FocusClock()
        {
            var left = focusEndUtc - DateTime.UtcNow;
            int s = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        // Remaining time as a shrinking arc
        void DrawFocusRing(float cx, float cy, float r)
        {
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            g.DrawEllipse(p.P(p.T.Line, 2.2f), rect);
            float sweep = 360f * FocusLeft();
            if (sweep > 0.5f) g.DrawArc(p.P(focus == FocusPhase.Work ? p.T.Accent : p.T.Sub, 2.2f), rect, -90, sweep);
        }

        float FocusWidth() { return 196; }

        void DrawFocus(float w, float h, DateTime now)
        {
            float cy = h / 2;
            DrawFocusRing(20, cy, 7.5f);
            p.Text(focus == FocusPhase.Work ? "Fokus" : "Istirahat", Ink.Semi(12.5f), p.T.Text, 38, cy, 90, StringAlignment.Near);
            focusRoller.Set(FocusClock());
            focusRoller.Draw(p, Ink.Semi(12.5f), p.T.Sub, w - 18, cy, StringAlignment.Far, now);
        }
    }
}
