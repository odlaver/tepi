using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // Per-song lyric shift, kept in lyrics\offsets.txt
    partial class Island
    {
        const int LyricStep = 500, LyricShiftMax = 10000;
        Dictionary<string, int> lyricOffsets;

        static string OffsetsPath { get { return Path.Combine(Settings.Dir, "lyrics", "offsets.txt"); } }

        // Title and artist; the player doesn't matter
        static string SongOf(string mediaKey)
        {
            int cut = mediaKey == null ? -1 : mediaKey.LastIndexOf('|');
            return cut > 0 ? mediaKey.Substring(0, cut).ToLowerInvariant() : (mediaKey ?? "").ToLowerInvariant();
        }

        int LyricOffset(string mediaKey)
        {
            if (lyricOffsets == null)
            {
                lyricOffsets = new Dictionary<string, int>();
                try
                {
                    if (File.Exists(OffsetsPath))
                        foreach (var line in File.ReadAllLines(OffsetsPath, Encoding.UTF8))
                        {
                            int tab = line.LastIndexOf('\t'), ms;
                            if (tab > 0 && int.TryParse(line.Substring(tab + 1), out ms)) lyricOffsets[line.Substring(0, tab)] = Math.Max(-LyricShiftMax, Math.Min(LyricShiftMax, ms));
                        }
                }
                catch (Exception ex) { Log.Write("lyric offsets: " + ex.Message); }
            }
            int v;
            return lyricOffsets.TryGetValue(SongOf(mediaKey), out v) ? v : 0;
        }

        // Cached lyrics remember their shift, so it applies once
        void ApplyLyricOffset(Lyrics l)
        {
            if (l == null || l.Lines.Count == 0) return;
            int want = LyricOffset(l.Key);
            double delta = want - l.Shift;
            if (delta == 0) return;
            foreach (var line in l.Lines) line.AtMs += delta;
            l.Shift = want;
        }

        void NudgeLyrics(int ms)
        {
            if (lyrics == null || lyrics.Key != mediaInfo.Key) return;
            int was = LyricOffset(lyrics.Key);
            int v = ms == 0 ? 0 : Math.Max(-LyricShiftMax, Math.Min(LyricShiftMax, was + ms));
            string song = SongOf(lyrics.Key);
            if (v == 0) lyricOffsets.Remove(song);
            else lyricOffsets[song] = v;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in lyricOffsets) sb.Append(kv.Key.Replace('\t', ' ').Replace('\n', ' ')).Append('\t').Append(kv.Value).Append("\r\n");
                Directory.CreateDirectory(Path.GetDirectoryName(OffsetsPath));
                AtomicFile.Write(OffsetsPath, new UTF8Encoding(false).GetBytes(sb.ToString()));
            }
            catch (Exception ex) { Log.Write("lyric offsets: " + ex.Message); }
            ApplyLyricOffset(lyrics);
            lyricIndex = lyricPrev = -1;
            Kick();
        }

        static string ShiftText(int ms)
        {
            string s = (Math.Abs(ms) / 1000.0).ToString("0.0", Lang.English ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo("id-ID"));
            return (ms < 0 ? "−" : "+") + s + Lang.T(" dtk");
        }

        // Right click on the lyrics
        void LyricMenu()
        {
            if (lyrics == null || lyrics.Key != mediaInfo.Key) return;
            int now = LyricOffset(lyrics.Key);
            var m = new ContextMenuStrip();
            m.Items.Add(new ToolStripMenuItem(now == 0 ? Lang.T("Waktu lirik asli") : Lang.F("Waktu lirik digeser {0}", ShiftText(now))) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Lang.T("Lirik lebih awal 0,5 dtk"), null, delegate { NudgeLyrics(-LyricStep); });
            m.Items.Add(Lang.T("Lirik lebih lambat 0,5 dtk"), null, delegate { NudgeLyrics(LyricStep); });
            if (now != 0) m.Items.Add(Lang.T("Kembalikan waktu asli"), null, delegate { NudgeLyrics(0); });
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(Control.MousePosition);
        }
    }
}
