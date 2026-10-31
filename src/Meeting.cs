using System;
using System.Drawing;

namespace Tepi
{
    // Calls: mic busy a while, island goes quiet
    partial class Island
    {
        const double MeetAfterSec = 20;
        DateTime micSinceUtc = DateTime.MinValue;
        string meetApp = "";
        bool meetShown;

        void TrackMeeting()
        {
            if (micApps.Length == 0) { micSinceUtc = DateTime.MinValue; meetApp = ""; return; }
            if (micSinceUtc == DateTime.MinValue) micSinceUtc = DateTime.UtcNow;
            if (Array.IndexOf(micApps, meetApp) < 0) meetApp = micApps[0];
        }

        bool MeetingOn
        {
            get { return micSinceUtc != DateTime.MinValue && (DateTime.UtcNow - micSinceUtc).TotalSeconds >= MeetAfterSec && settings.GetBool("meeting", true); }
        }

        // Once a second: the 20 s mark needs a frame
        bool TickMeeting()
        {
            bool on = MeetingOn;
            if (on == meetShown) return false;
            meetShown = on;
            return true;
        }

        float MeetWidth() { return p.G == null ? 230 : Clamp(p.Measure(meetApp, Ink.Semi(12.5f)) + 140, 210, 360); }

        Action<DateTime> meetClock;
        RectangleF meetClockAt;

        void DrawMeet(float w, float h)
        {
            float cy = h / 2;
            p.Icon(micMuted ? IcoMicOff : Ink.IcoMic, 13f, micMuted ? p.T.Negative : p.T.Text, 21, cy);
            p.Text(meetApp, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - 74, StringAlignment.Near);
            if (meetClock == null) meetClock = t =>
            {
                if (micSinceUtc == DateTime.MinValue) return;
                var d = DateTime.UtcNow - micSinceUtc;
                string s = d.TotalHours >= 1 ? ((int)d.TotalHours) + ":" + d.Minutes.ToString("00") + ":" + d.Seconds.ToString("00") : ((int)d.TotalMinutes) + ":" + d.Seconds.ToString("00");
                p.TextRight(s, Ink.Semi(12f), p.T.Sub, meetClockAt.Right, meetClockAt.Y + meetClockAt.Height / 2);
            };
            meetClockAt = new RectangleF(w - 78, cy - 10, 60, 20);
            LiveRect(meetClockAt, TickFps, meetClock);
            // Tap the call to mute
            AddHit(new RectangleF(0, 0, w, h), "meetMute", actMic);
        }
    }
}
