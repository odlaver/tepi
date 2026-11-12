using System;

namespace Tepi
{
    // Music and volume, by voice or chat
    partial class Island
    {
        const char IcoPlaySong = (char)0xE768, IcoPauseSong = (char)0xE769, IcoNextSong = (char)0xE893, IcoPrevSong = (char)0xE892;
        const int PressGapMs = 400;

        // Presses spaced so the player takes each
        void PressLater(Action press, int times)
        {
            if (times <= 0) return;
            int left = times;
            var t = new System.Windows.Forms.Timer { Interval = PressGapMs };
            t.Tick += delegate
            {
                press();
                if (--left > 0) return;
                t.Stop();
                t.Dispose();
            };
            t.Start();
        }

        // Null: no player to talk to
        string DoMedia(string cmd, int count, out char glyph)
        {
            glyph = IcoPlaySong;
            if (!mediaInfo.Present) return null;
            count = Math.Max(1, Math.Min(10, count));
            switch (cmd)
            {
                case "play":
                    media.Play();
                    return Lang.T("Musik diputar");
                case "pause":
                    glyph = IcoPauseSong;
                    media.Pause();
                    return Lang.T("Musik dijeda");
                case "next":
                    glyph = IcoNextSong;
                    media.Next();
                    PressLater(media.Next, count - 1);
                    return count == 1 ? Lang.T("Lagu berikutnya") : Lang.F("Maju {0} lagu", count);
                case "prev":
                    glyph = IcoPrevSong;
                    // Past the opening, the first press only restarts the song
                    int restart = mediaInfo.DurationMs <= 0 || mediaInfo.Position(DateTime.UtcNow) > 3000 ? 1 : 0;
                    media.Previous();
                    PressLater(media.Previous, count - 1 + restart);
                    return count == 1 ? Lang.T("Lagu sebelumnya") : Lang.F("Mundur {0} lagu", count);
            }
            return null;
        }

        string VolumeTo(float level)
        {
            level = Math.Max(0, Math.Min(1, (float)Math.Round(level, 2)));
            if (muted) volume.SetMute(false);
            volume.Set(level);
            return Lang.F("Volume {0}%", (int)Math.Round(level * 100));
        }

        string MuteTo(bool on)
        {
            volume.SetMute(on);
            return on ? Lang.T("Suara dimatikan") : Lang.T("Suara dinyalakan");
        }

        static char VolumeGlyph(float level) { return level <= 0 ? Ink.IcoMute : level < 0.34f ? Ink.IcoVol1 : level < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3; }
    }
}
