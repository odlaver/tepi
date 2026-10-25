using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tepi
{
    class LyricLine
    {
        public double AtMs;
        public string Text;
    }

    enum LyricState { Loading, Synced, Plain, Missing, Failed }

    class Lyrics
    {
        public string Key;
        public LyricState State;
        public List<LyricLine> Lines = new List<LyricLine>();
        public DateTime FailedUtc;

        // Line sung at posMs, or -1
        public int IndexAt(double posMs)
        {
            int lo = 0, hi = Lines.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Lines[mid].AtMs <= posMs) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        // LRC lacks end times; estimate from text
        public double SungUntil(int i)
        {
            double start = Lines[i].AtMs;
            double next = i + 1 < Lines.Count ? Lines[i + 1].AtMs : start + 6000;
            double estimate = Math.Max(1400, Lines[i].Text.Length * 115 + 800);
            return start + Math.Min(next - start, estimate);
        }

        public double NextAt(int i) { return i + 1 < Lines.Count ? Lines[i + 1].AtMs : double.MaxValue; }
    }

    // LRCLIB; sends only title, artist, duration
    class LyricsService
    {
        public event Action<Lyrics> Ready;

        readonly Dictionary<string, Lyrics> memory = new Dictionary<string, Lyrics>();
        static readonly string CacheDir = Path.Combine(Settings.Dir, "lyrics");

        public Lyrics Get(MediaInfo m)
        {
            if (!m.Present || m.Title.Length == 0) return null;
            Lyrics l;
            lock (memory)
            {
                if (memory.TryGetValue(m.Key, out l))
                {
                    bool retry = l.State == LyricState.Failed && (DateTime.UtcNow - l.FailedUtc).TotalSeconds > 30;
                    if (!retry) return l;
                }
                // Ads and podcasts: no point asking
                if (m.DurationMs > 0 && (m.DurationMs < 31000 || m.DurationMs > 15 * 60000))
                {
                    l = new Lyrics { Key = m.Key, State = LyricState.Missing };
                    memory[m.Key] = l;
                    return l;
                }
                if (m.DurationMs <= 0) return null;
                if (memory.Count > 24) memory.Clear(); // disk cache keeps the rest
                l = new Lyrics { Key = m.Key, State = LyricState.Loading };
                memory[m.Key] = l;
            }
            string key = m.Key, title = m.Title, artist = m.Artist;
            double duration = m.DurationMs;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var result = Fetch(key, title, artist, duration);
                lock (memory) memory[key] = result;
                var h = Ready;
                if (h != null) h(result);
            });
            return l;
        }

        Lyrics Fetch(string key, string rawTitle, string rawArtist, double durationMs)
        {
            var result = new Lyrics { Key = key, State = LyricState.Missing };
            string title, artist;
            Clean(rawTitle, rawArtist, out title, out artist);
            string stem = Path.Combine(CacheDir, Hash((artist + "|" + title).ToLowerInvariant()));
            string file = stem + ".lrc", plainFile = stem + ".txt";
            try
            {
                string lrc = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
                string plain = lrc == null && File.Exists(plainFile) ? File.ReadAllText(plainFile, Encoding.UTF8) : null;
                if (lrc == null && plain == null)
                {
                    int dur = (int)Math.Round(durationMs / 1000);
                    string q = "track_name=" + Uri.EscapeDataString(title) + (artist.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(artist) : "");
                    if (artist.Length > 0)
                    {
                        var exact = Ask("https://lrclib.net/api/get?" + q + "&duration=" + dur);
                        if (exact != null) { lrc = Synced(exact); plain = Plain(exact); }
                    }
                    foreach (var url in new[] { "https://lrclib.net/api/search?" + q, "https://lrclib.net/api/search?q=" + Uri.EscapeDataString((artist + " " + title).Trim()) })
                    {
                        if (lrc != null) break;
                        var items = Ask(url) as object[];
                        lrc = Best(items, dur, true);
                        if (plain == null) plain = Best(items, dur, false);
                    }
                    Directory.CreateDirectory(CacheDir);
                    if (lrc != null) File.WriteAllText(file, lrc, new UTF8Encoding(false));
                    else if (plain != null) File.WriteAllText(plainFile, plain, new UTF8Encoding(false));
                }
                if (lrc != null)
                {
                    result.Lines = Parse(lrc);
                    if (result.Lines.Count > 0) result.State = LyricState.Synced;
                }
                else if (plain != null)
                {
                    // Untimed: panel only, no karaoke
                    foreach (var line in plain.Replace("\r", "").Split('\n')) result.Lines.Add(new LyricLine { AtMs = -1, Text = line.Trim() });
                    if (result.Lines.Count > 0) result.State = LyricState.Plain;
                }
            }
            catch (Exception ex)
            {
                Log.Write("lyrics '" + artist + " - " + title + "': " + ex.Message);
                result.State = LyricState.Failed;
                result.FailedUtc = DateTime.UtcNow;
            }
            return result;
        }

        static object Ask(string url)
        {
            var r = Web.Send("GET", url, null, null, 9000);
            if (r.Status == 404 || r.Status == 400) return null;
            if (!r.Ok) throw new Exception("LRCLIB " + r.Status + " " + Fmt.OneLine(r.Body, 80));
            return Json.Parse(r.Body);
        }

        static string Synced(object item)
        {
            string s = Json.Str(item, "syncedLyrics");
            return s.Trim().Length > 0 ? s : null;
        }

        static string Plain(object item)
        {
            string s = Json.Str(item, "plainLyrics");
            return s.Trim().Length > 0 ? s : null;
        }

        // Closest duration wins; other edits drift
        static string Best(object[] items, int durationSec, bool synced)
        {
            if (items == null) return null;
            string best = null;
            double bestScore = double.MaxValue;
            foreach (var item in items)
            {
                string lrc = synced ? Synced(item) : Plain(item);
                if (lrc == null) continue;
                double score = Math.Abs(Json.Num(item, "duration", durationSec) - durationSec);
                if (score > 8) continue;
                if (score < bestScore) { bestScore = score; best = lrc; }
            }
            return best;
        }

        static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]");

        static List<LyricLine> Parse(string lrc)
        {
            var lines = new List<LyricLine>();
            foreach (var raw in lrc.Split('\n'))
            {
                var ms = Stamp.Matches(raw);
                if (ms.Count == 0) continue;
                var last = ms[ms.Count - 1];
                string text = raw.Substring(last.Index + last.Length).Trim();
                foreach (Match m in ms)
                {
                    double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                    lines.Add(new LyricLine { AtMs = (min * 60 + sec) * 1000, Text = text });
                }
            }
            lines.Sort((a, b) => a.AtMs.CompareTo(b.AtMs));
            return lines;
        }

        static readonly Regex Noise = new Regex(@"official|video|audio|lyric|lirik|\bmv\b|m/v|\bhd\b|\b4k\b|visuali[sz]er|remaster|explicit|live session", RegexOptions.IgnoreCase);

        // YouTube-style titles into artist + title
        internal static void Clean(string rawTitle, string rawArtist, out string title, out string artist)
        {
            string t = rawTitle ?? "", a = (rawArtist ?? "").Trim();
            t = Regex.Replace(t, @"\s*[\(\[【]([^\)\]】]*)[\)\]】]", m => Noise.IsMatch(m.Groups[1].Value) ? "" : m.Value);
            t = Regex.Replace(t, @"\s+\|.*$", "");
            bool channel = a.Length == 0 || Regex.IsMatch(a, @"(?i)(vevo|\s-\s*topic|official|records|music|channel|\btv)$|^official");
            var dash = Regex.Match(t, @"^(.+?)\s+[-–—]\s+(.+)$");
            if (dash.Success && (channel || dash.Groups[1].Value.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                a = dash.Groups[1].Value.Trim();
                t = dash.Groups[2].Value.Trim();
            }
            a = Regex.Replace(a, @"(?i)\s*-\s*topic$|vevo$|^official\s*", "");
            t = Regex.Replace(t, @"(?i)\s*[\(\[](ft\.?|feat\.?|featuring)\s[^\)\]]*[\)\]]", "");
            t = Regex.Replace(t, @"(?i)\s+(ft\.?|feat\.?|featuring)\s+.*$", "");
            title = t.Trim().Trim('"', '\'', '-', ' ');
            artist = a.Trim();
        }

        static string Hash(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in s) h = (h ^ c) * 1099511628211UL;
                return h.ToString("x16");
            }
        }
    }
}
