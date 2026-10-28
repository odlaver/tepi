using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Tepi
{
    enum TimerKind { Off, Focus, Break, Countdown, Stopwatch }

    // Focus with break, countdown, stopwatch with laps
    partial class Island
    {
        TimerKind timerKind;
        // Countdown: start and end; stopwatch: start
        DateTime timerStartUtc, timerEndUtc;
        // Paused: time left or elapsed
        TimeSpan timerHeld;
        bool timerPaused;
        readonly List<TimeSpan> laps = new List<TimeSpan>();
        readonly Roller timerRoller = new Roller();

        bool TimerOn { get { return timerKind != TimerKind.Off; } }
        bool Counting { get { return timerKind == TimerKind.Focus || timerKind == TimerKind.Break || timerKind == TimerKind.Countdown; } }

        void StartTimer(TimerKind kind, int minutes)
        {
            timerKind = kind;
            timerPaused = false;
            laps.Clear();
            timerStartUtc = DateTime.UtcNow;
            timerEndUtc = kind == TimerKind.Stopwatch ? DateTime.MaxValue : timerStartUtc.AddMinutes(minutes);
            if (kind == TimerKind.Focus) ShowInfo('\0', Lang.T("Fokus ") + minutes + Lang.T(" menit"), Lang.T("selesai ") + Fmt.Hm(timerEndUtc.ToLocalTime()), true);
            else if (kind == TimerKind.Countdown) ShowInfo('\0', Lang.F("Timer {0} menit", minutes), Lang.T("selesai ") + Fmt.Hm(timerEndUtc.ToLocalTime()), true);
            Kick();
        }

        void StartFocus(int minutes) { StartTimer(TimerKind.Focus, minutes); }

        void StopTimer()
        {
            timerKind = TimerKind.Off;
            timerPaused = false;
            Kick();
        }

        void PauseTimer()
        {
            if (!TimerOn || timerPaused) return;
            timerPaused = true;
            timerHeld = Counting ? timerEndUtc - DateTime.UtcNow : DateTime.UtcNow - timerStartUtc;
            Kick();
        }

        void ResumeTimer()
        {
            if (!timerPaused) return;
            timerPaused = false;
            var now = DateTime.UtcNow;
            if (Counting)
            {
                // Keep the ring's proportion: shift both ends
                var total = timerEndUtc - timerStartUtc;
                timerEndUtc = now + timerHeld;
                timerStartUtc = timerEndUtc - total;
            }
            else timerStartUtc = now - timerHeld;
            Kick();
        }

        void ToggleTimerPause() { if (timerPaused) ResumeTimer(); else PauseTimer(); }

        void AddTimerMinute()
        {
            if (!Counting) return;
            if (timerPaused) timerHeld += TimeSpan.FromMinutes(1);
            else timerEndUtc = timerEndUtc.AddMinutes(1);
            Kick();
        }

        void Lap()
        {
            if (timerKind != TimerKind.Stopwatch) return;
            laps.Insert(0, Elapsed());
            if (laps.Count > 20) laps.RemoveAt(laps.Count - 1);
            Kick();
        }

        TimeSpan Elapsed() { return timerPaused ? timerHeld : DateTime.UtcNow - timerStartUtc; }
        TimeSpan Remaining() { return timerPaused ? timerHeld : timerEndUtc - DateTime.UtcNow; }

        // Each clock tick; true while something runs
        bool TimerTick(DateTime now)
        {
            if (!TimerOn) return false;
            if (!Counting || timerPaused || now < timerEndUtc) return true;
            if (!Anim.Reduced) topGap.Velocity += 120;
            Chime();
            if (timerKind == TimerKind.Focus)
            {
                int rest = Math.Max(1, settings.GetInt("focus.break", 5));
                timerKind = TimerKind.Break;
                timerStartUtc = now;
                timerEndUtc = now.AddMinutes(rest);
                ShowNotice('\0', Lang.T("Fokus selesai"), Lang.T("istirahat ") + rest + Lang.T(" menit"));
            }
            else if (timerKind == TimerKind.Break)
            {
                timerKind = TimerKind.Off;
                ShowNotice('\0', Lang.T("Istirahat selesai"), Lang.T("siap fokus lagi"));
            }
            else
            {
                timerKind = TimerKind.Off;
                ShowNotice('\0', Lang.T("Timer selesai"), Fmt.Hm(now.ToLocalTime()));
            }
            return true;
        }


        // Short chime; silent under DND
        static void Chime()
        {
            if (Native.DoNotDisturb()) return;
            string wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Windows Notify Calendar.wav");
            try { if (File.Exists(wav)) PlaySound(wav, IntPtr.Zero, 0x0001 | 0x0002 | 0x20000); }
            catch (Exception ex) { Log.Write("chime: " + ex.Message); }
        }

        // Ring: time left, or stopwatch minute
        float TimerFraction()
        {
            if (timerKind == TimerKind.Stopwatch) return (float)(Elapsed().TotalSeconds % 60 / 60);
            double total = (timerEndUtc - timerStartUtc).TotalSeconds;
            return total <= 0 ? 0 : Ease.Clamp01((float)(Remaining().TotalSeconds / total));
        }

        string TimerClock()
        {
            var t = timerKind == TimerKind.Stopwatch ? Elapsed() : Remaining();
            int s = Math.Max(0, timerKind == TimerKind.Stopwatch ? (int)t.TotalSeconds : (int)Math.Ceiling(t.TotalSeconds));
            return s >= 3600 ? (s / 3600) + ":" + (s / 60 % 60).ToString("00") + ":" + (s % 60).ToString("00")
                             : (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        string TimerLabel()
        {
            switch (timerKind)
            {
                case TimerKind.Focus: return Lang.T("Fokus");
                case TimerKind.Break: return Lang.T("Istirahat");
                case TimerKind.Stopwatch: return Lang.T("Stopwatch");
                default: return Lang.T("Timer");
            }
        }

        // Remaining time as a shrinking arc
        void DrawTimerRing(float cx, float cy, float r)
        {
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            g.DrawEllipse(p.P(p.T.Line, 2.2f), rect);
            float sweep = 360f * TimerFraction();
            Color c = timerPaused ? p.T.Faint : timerKind == TimerKind.Focus || timerKind == TimerKind.Countdown ? p.T.Accent : p.T.Sub;
            if (sweep > 0.5f) g.DrawArc(p.P(c, 2.2f), rect, -90, sweep);
        }

        float TimerWidth() { return timerPaused ? 214 : 196; }

        // Digits roll fast, then wait a second
        double TimerWait(DateTime now)
        {
            if (timerRoller.Moving(now)) return 1 / 60.0;
            if (!TimerOn || timerPaused) return 0;
            double ms = (timerKind == TimerKind.Stopwatch ? Elapsed() : Remaining()).TotalMilliseconds % 1000;
            if (ms < 0) ms += 1000;
            return ((timerKind == TimerKind.Stopwatch ? 1000 - ms : ms) + 4) / 1000;
        }

        // Roller and ring redraw alone
        void LiveTimer(Font f, Color c, float x, float cy, StringAlignment align, float room)
        {
            float left = align == StringAlignment.Far ? x - room : x - 2;
            LiveRect(new RectangleF(left, cy - f.Size * 0.7f, room + 2, f.Size * 1.4f), t =>
            {
                timerRoller.Set(TimerClock());
                timerRoller.Draw(p, f, c, x, cy, align, t);
                return TimerWait(t);
            });
        }

        void DrawTimer(float w, float h, DateTime now)
        {
            float cy = h / 2;
            LiveRect(new RectangleF(11, cy - 9, 18, 18), timerPaused ? 0 : TickFps, t => DrawTimerRing(20, cy, 7.5f));
            string label = timerPaused ? TimerLabel() + Lang.T(" · dijeda") : TimerLabel();
            p.Text(label, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - 70, StringAlignment.Near);
            LiveTimer(Ink.Semi(13f), timerPaused ? p.T.Faint : p.T.Sub, w - 18, cy, StringAlignment.Far, 60);
            AddHit(new RectangleF(0, 0, w, h), "timerPill", delegate { Expand(PageControl); });
        }
    }
}
