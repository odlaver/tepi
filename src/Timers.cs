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

        void StartTimer(TimerKind kind, int minutes) { StartTimer(kind, TimeSpan.FromMinutes(minutes)); }

        void StartTimer(TimerKind kind, TimeSpan span)
        {
            timerKind = kind;
            timerPaused = false;
            laps.Clear();
            timerStartUtc = DateTime.UtcNow;
            timerEndUtc = kind == TimerKind.Stopwatch ? DateTime.MaxValue : timerStartUtc + span;
            if (kind == TimerKind.Focus) ShowInfo('\0', Lang.T("Fokus ") + (int)span.TotalMinutes + Lang.T(" menit"), Lang.T("selesai ") + Fmt.Hm(timerEndUtc.ToLocalTime()), true);
            else if (kind == TimerKind.Countdown) ShowInfo('\0', Lang.F("Timer {0}", SpanText(span)), Lang.T("selesai ") + Fmt.Hm(timerEndUtc.ToLocalTime()), true);
            Kick();
        }

        // "1 jam 30 menit", "45 detik"
        static string SpanText(TimeSpan span)
        {
            var parts = new System.Collections.Generic.List<string>();
            int h = (int)span.TotalHours;
            if (h > 0) parts.Add(Lang.F("{0} jam", h));
            if (span.Minutes > 0) parts.Add(Lang.F("{0} menit", span.Minutes));
            if (span.Seconds > 0 || parts.Count == 0) parts.Add(Lang.F("{0} detik", span.Seconds));
            return string.Join(" ", parts.ToArray());
        }

        void StartFocus(int minutes) { StartTimer(TimerKind.Focus, minutes); }

        // Focus holds plain news like Senyap
        bool FocusHold { get { return timerKind == TimerKind.Focus && !timerPaused && settings.GetBool("focus.hold", true); } }

        // What came in while focused
        void FocusSummary(string title, string detail)
        {
            int count = 0;
            var from = new HashSet<string>();
            if (settings.GetBool("focus.hold", true))
                foreach (var n in notifs)
                    if (n.Time >= timerStartUtc) { count++; from.Add(n.Title.Length > 0 ? n.Title : n.AppName); }
            string held = count == 0 ? "" : from.Count > 1 ? Lang.F("{0} pesan dari {1} orang masuk", count, from.Count) : Lang.F("{0} pesan masuk", count);
            var t = ShowToast(View.Info, count > 0 ? 8 : 3.2, 3, count > 0, true);
            if (t == null) return;
            EyesReact(EyeMood.Happy, 0);
            t.Glyph = '\0';
            t.Title = title;
            t.Detail = held.Length == 0 ? detail : detail.Length == 0 ? held : detail + " · " + held;
            if (count > 0) t.Data = new Action(delegate { Show(PageMessages); });
        }

        void StopTimer()
        {
            if (timerKind == TimerKind.Focus) FocusSummary(Lang.T("Fokus dihentikan"), "");
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
                FocusSummary(Lang.T("Fokus selesai"), Lang.T("istirahat ") + rest + Lang.T(" menit"));
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
