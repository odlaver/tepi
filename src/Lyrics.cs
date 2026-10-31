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
        public int Fails;

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

    // LRCLIB, then NetEase; title, artist, duration only
    class LyricsService
    {
        public event Action<Lyrics> Ready;

        readonly Dictionary<string, Lyrics> memory = new Dictionary<string, Lyrics>();
        static readonly string CacheDir = Path.Combine(Settings.Dir, "lyrics");

        public Lyrics Get(MediaInfo m)
        {
            if (!m.Present || m.Title.Length == 0) return null;
            Lyrics l;
            int fails;
            lock (memory)
            {
                if (memory.TryGetValue(m.Key, out l))
                {
                    // Busy server: 10, 20, 40 s, up to 2 min
                    double wait = Math.Min(120, 10 * Math.Pow(2, Math.Max(0, l.Fails - 1)));
                    bool retry = l.State == LyricState.Failed && (DateTime.UtcNow - l.FailedUtc).TotalSeconds > wait;
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
                fails = l != null && l.State == LyricState.Failed ? l.Fails : 0;
                if (memory.Count > 24) memory.Clear(); // disk cache keeps the rest
                l = new Lyrics { Key = m.Key, State = LyricState.Loading };
                memory[m.Key] = l;
            }
            string key = m.Key, title = m.Title, artist = m.Artist;
            double duration = m.DurationMs;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var result = Fetch(key, title, artist, duration);
                if (result.State == LyricState.Failed) result.Fails = fails + 1;
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
                    Exception down = null;
                    try
                    {
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
                    }
                    catch (Exception ex) { down = ex; }
                    // LRCLIB busy or unsynced: NetEase
                    if (lrc == null)
                    {
                        try { lrc = NetEase(title, artist, dur); }
                        catch (Exception ex) { if (down != null) throw new Exception(down.Message + "; " + ex.Message); }
                    }
                    if (down != null)
                    {
                        if (lrc == null && plain == null) throw down;
                        Log.Write("lyrics '" + artist + " - " + title + "': " + down.Message + (lrc != null ? " · NetEase" : ""));
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

        // LRCLIB short: NetEase waits behind it
        static readonly int[] LrclibRetryMs = { 1000 }, NetEaseRetryMs = { 1000, 2500 };

        // Overload bursts pass in seconds: retry
        static WebResult Get(string url, int[] retryMs)
        {
            var r = Web.Send("GET", url, null, null, 9000);
            for (int i = 0; i < retryMs.Length && (r.Status == 0 || r.Status == 429 || r.Status >= 500); i++)
            {
                Thread.Sleep(retryMs[i]);
                r = Web.Send("GET", url, null, null, 9000);
            }
            return r;
        }

        static object Ask(string url)
        {
            var r = Get(url, LrclibRetryMs);
            if (r.Status == 404 || r.Status == 400) return null;
            if (!r.Ok) throw new Exception("LRCLIB " + r.Status + " " + Fmt.OneLine(r.Body, 80));
            return Json.Parse(r.Body);
        }

        static readonly Regex Credit = new Regex(@"^(作词|作曲|编曲|制作人|监制|混音|母带|录音|和声|吉他|贝斯|鼓|键盘|弦乐|出品|发行|词|曲)\s*[:：]");

        // Same song: artist, title, length agree
        static string NetEase(string title, string artist, int durationSec)
        {
            if (artist.Length == 0) return null;
            var r = Get("https://music.163.com/api/search/get?s=" + Uri.EscapeDataString(artist + " " + title) + "&type=1&limit=10", NetEaseRetryMs);
            if (!r.Ok) throw new Exception("NetEase " + r.Status);
            var songs = Json.Arr(Json.Obj(Json.Parse(r.Body), "result"), "songs");
            if (songs == null) return null;
            string want = Bare(title), by = Norm(artist);
            if (want.Length == 0 || by.Length == 0) return null;
            double best = double.MaxValue, id = 0;
            foreach (var s in songs)
            {
                double gap = Math.Abs(Json.Num(s, "duration", 0) / 1000 - durationSec);
                if (gap > 8 || Bare(Json.Str(s, "name")) != want) continue;
                bool same = false;
                var artists = Json.Arr(s, "artists");
                if (artists != null) foreach (var a in artists) { string n = Norm(Json.Str(a, "name")); if (n.Length > 0 && (by.Contains(n) || n.Contains(by))) same = true; }
                if (same && gap < best) { best = gap; id = Json.Num(s, "id", 0); }
            }
            if (id <= 0) return null;
            r = Get("https://music.163.com/api/song/lyric?id=" + ((long)id).ToString(CultureInfo.InvariantCulture) + "&lv=1", NetEaseRetryMs);
            if (!r.Ok) throw new Exception("NetEase " + r.Status);
            string lrc = Json.Str(Json.Obj(Json.Parse(r.Body), "lrc"), "lyric");
            if (lrc.IndexOf("纯音乐", StringComparison.Ordinal) >= 0) return null;
            var sb = new StringBuilder();
            foreach (var raw in lrc.Replace("\r", "").Split('\n'))
            {
                var m = Stamp.Match(raw);
                if (!m.Success || Credit.IsMatch(raw.Substring(m.Index + m.Length).Trim())) continue;
                sb.Append(raw).Append('\n');
            }
            return Parse(sb.ToString()).Count > 0 ? sb.ToString() : null;
        }

        // Title without (feat.), [Remastered] and the like
        static string Bare(string s) { return Norm(Regex.Replace(s ?? "", @"\s*[\(\[][^\)\]]*[\)\]]", "")); }

        // Case, accents, punctuation aside
        static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Normalize(NormalizationForm.FormD))
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
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
