using System;
using System.Drawing;

namespace Tepi
{
    // Side edge: resting views as an upright capsule
    partial class Island
    {
        const float SideW = 40;

        // Activity that lives in the island; toasts open sideways
        static bool Upright(View v)
        {
            switch (v)
            {
                case View.Idle:
                case View.Media:
                case View.Lyric:
                case View.Agent:
                case View.Timer:
                case View.Task:
                case View.Class:
                case View.Download:
                case View.Run:
                case View.Meet:
                case View.Record: return true;
            }
            return false;
        }

        bool UprightNow(View v) { return OnSide && Upright(v); }

        SizeF SideSize(View v)
        {
            switch (v)
            {
                case View.Idle: return new SizeF(SideW, (IdleEyes ? 58 : 66) + (unread > 0 || PrivacyOn ? 22 : 0));
                case View.Media:
                case View.Lyric: return new SizeF(SideW, 86);
                case View.Timer: return new SizeF(SideW, 80);
                case View.Agent: return new SizeF(SideW, 78);
            }
            return new SizeF(SideW, 68);
        }

        void DrawSide(View v, float w, float h, DateTime now)
        {
            switch (v)
            {
                case View.Idle: SideIdle(w, h, now); break;
                case View.Media:
                case View.Lyric: SideMedia(w, h); break;
                case View.Timer: SideTimer(w, h); break;
                case View.Agent: SideAgent(w, h); break;
                case View.Download:
                    var d = ActiveDownload();
                    SideGlyph(w, IcoDownload, d != null && d.Percent >= 0 ? Ink.Percent(d.Percent) : "");
                    break;
                case View.Task:
                    var task = SoonTask();
                    SideGlyph(w, '', task != null ? ShortLeft(task.DeadlineUtc - DateTime.UtcNow) : "");
                    break;
                case View.Class:
                    var c = ClassNow();
                    SideGlyph(w, Ink.IcoCalendar, c != null ? Fmt.Hm(c.End) : "");
                    break;
                case View.Run:
                    p.Orbit(w / 2, 22, 8f, orbitPhase, p.T.Text);
                    p.Icon(Ink.IcoTerminal, 12f, p.T.Sub, w / 2, 48);
                    break;
                case View.Meet: SideMeet(w, h); break;
                case View.Record: SideRecord(w, h); break;
            }
        }

        // Eyes, or the time stacked hours over minutes
        void SideIdle(float w, float h, DateTime now)
        {
            float cx = w / 2, face = IdleEyes ? 58 : 66;
            if (IdleEyes) DrawEyes(cx, face / 2, IdleEyeK);
            else
            {
                string clock = Fmt.Clock(DateTime.Now);
                int cut = clock.IndexOfAny(new[] { '.', ':' });
                string hh = cut > 0 ? clock.Substring(0, cut) : clock, mm = cut > 0 ? clock.Substring(cut + 1) : "";
                p.TextCenter(hh, Ink.Display(15.5f), p.T.Text, cx, face / 2 - 9);
                p.TextCenter(mm, Ink.Display(15.5f), p.T.Sub, cx, face / 2 + 10);
            }
            // Unread first; else the camera or mic in use
            if (unread > 0) DrawCount(cx, face + 6, unread);
            else if (PrivacyOn)
            {
                bool off = MicSilenced && camApps.Length == 0;
                p.Icon(camApps.Length > 0 ? Ink.IcoCam : off ? IcoMicOff : Ink.IcoMic, 12f, off ? p.T.Negative : p.T.Sub, cx, face + 6);
            }
        }

        // Cover on top, the waveform under it, progress last
        void SideMedia(float w, float h)
        {
            float cx = w / 2;
            DrawCover(new RectangleF(cx - 13, 8, 26, 26), 7);
            if (mediaInfo.Playing) DrawBars(cx + 6.9f, 50, 3, 14, 3f, 2.4f);
            else p.Pause(cx, 50, 9f, p.T.Sub);
            if (mediaInfo.DurationMs > 0)
            {
                var line = new RectangleF(cx - 11, h - 14, 22, 2);
                LiveRect(line, ProgressFps(line.Width), t =>
                {
                    float f = Ease.Clamp01((float)(mediaInfo.Position(t) / mediaInfo.DurationMs));
                    p.FillRound(line, 1, Ink.A(p.T.Text, 0.12f));
                    p.FillRound(new RectangleF(line.X, line.Y, Math.Max(2, line.Width * f), 2), 1, Ink.A(p.T.Text, 0.45f));
                });
            }
        }

        // Ring on top, minutes under it
        void SideTimer(float w, float h)
        {
            float cx = w / 2;
            LiveRect(new RectangleF(cx - 13, 9, 26, 26), timerPaused ? 0 : TickFps, t => DrawTimerRing(cx, 22, 11f));
            LiveRect(new RectangleF(2, 38, w - 4, 34), timerPaused ? 0 : TickFps, t =>
            {
                var left = timerKind == TimerKind.Stopwatch ? Elapsed() : Remaining();
                bool minutes = left.TotalSeconds >= 60;
                int n = Math.Max(0, minutes ? (int)Math.Floor(left.TotalMinutes) : (int)Math.Ceiling(left.TotalSeconds));
                p.TextCenter(n.ToString(), Ink.Display(15.5f), timerPaused ? p.T.Sub : p.T.Text, cx, 50);
                p.TextCenter(minutes ? Lang.T("mnt") : Lang.T("dtk"), Ink.Small(10f), p.T.Sub, cx, 65);
            });
        }

        // Working mark, then the plan filling downward
        void SideAgent(float w, float h)
        {
            float cx = w / 2;
            var s = BusySession();
            if (s == null) return;
            AddHit(new RectangleF(0, 0, w, h), "agentView", delegate { ShowAgent(s); });
            DrawStateMark(s, cx, 22);
            int busy = BusyCount();
            if (busy > 1) p.TextCenter(busy.ToString(), Ink.SmallSemi(11f), p.T.Sub, cx, 44);
            if (s.Plan == null || s.Plan.Count == 0) return;
            var track = new RectangleF(cx - 1.5f, busy > 1 ? 54 : 42, 3, h - (busy > 1 ? 54 : 42) - 12);
            p.FillRound(track, 1.5f, p.T.Line);
            float f = (float)PlanDone(s) / s.Plan.Count;
            if (f > 0.001f) p.FillRound(new RectangleF(track.X, track.Y, 3, Math.Max(3, track.Height * f)), 1.5f, p.T.Progress);
        }

        // One unit only: fits a 40 px capsule
        static string ShortLeft(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 24) return (int)t.TotalHours + (Lang.English ? "h" : "j");
            return (int)t.TotalDays + (Lang.English ? "d" : "h");
        }

        void SideGlyph(float w, char glyph, string text)
        {
            p.Icon(glyph, 14f, p.T.Text, w / 2, 22);
            if (text.Length > 0) p.TextCenter(text, Ink.SmallSemi(10.5f), p.T.Sub, w / 2, 46);
        }

        // Tap mutes, like the wide call view
        void SideMeet(float w, float h)
        {
            float cx = w / 2;
            p.Icon(micMuted ? IcoMicOff : Ink.IcoMic, 14f, micMuted ? p.T.Negative : p.T.Text, cx, 22);
            LiveRect(new RectangleF(2, 36, w - 4, 20), TickFps, t =>
            {
                if (micSinceUtc == DateTime.MinValue) return;
                p.TextCenter(((int)(DateTime.UtcNow - micSinceUtc).TotalMinutes) + Lang.T("m"), Ink.SmallSemi(10.5f), p.T.Sub, cx, 46);
            });
            AddHit(new RectangleF(0, 0, w, h), "meetMute", actMic);
        }

        void SideRecord(float w, float h)
        {
            float cx = w / 2;
            p.Circle(cx, 22, 5f, p.T.Negative);
            LiveRect(new RectangleF(2, 36, w - 4, 20), TickFps, t => p.TextCenter(((int)(t - rapatStartUtc).TotalMinutes) + Lang.T("m"), Ink.SmallSemi(10.5f), p.T.Sub, cx, 46));
        }
    }
}
