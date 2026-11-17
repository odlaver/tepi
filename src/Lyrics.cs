using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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
        // User shift already added to AtMs
        public double Shift;

        // Line sung at posMs, or -1
        public int IndexAt(double posMs)
        {
            // Upper bound: first line starting later
            int first = 0, count = Lines.Count;
            while (count > 0)
            {
                int half = count >> 1;
                if (posMs >= Lines[first + half].AtMs)
                {
                    first += half + 1;
                    count -= half + 1;
                }
                else count = half;
            }
            return first - 1;
        }

        // LRC lacks end times; estimate from text
        public double SungUntil(int i)
        {
            var line = Lines[i];
            double room = i + 1 < Lines.Count ? Lines[i + 1].AtMs - line.AtMs : 6000;
            double guess = 800 + 115 * line.Text.Length;
            if (guess < 1400) guess = 1400;
            return line.AtMs + (room < guess ? room : guess);
        }

        public double NextAt(int i)
        {
            int n = i + 1;
            return n < Lines.Count ? Lines[n].AtMs : double.MaxValue;
        }
    }

    // LRCLIB, then NetEase; title, artist, duration only
    class LyricsService
    {
        public event Action<Lyrics> Ready;

        // Recent tracks only; disk holds the rest
        const int KeepInRam = 24;
        readonly Dictionary<string, Lyrics> byTrack = new Dictionary<string, Lyrics>();
        static readonly string Folder = Path.Combine(Settings.Dir, "lyrics");

        public Lyrics Get(MediaInfo m)
        {
            if (!m.Present || m.Title.Length == 0) return null;
            int priorFails = 0;
            Lyrics pending;
            lock (byTrack)
            {
                Lyrics known;
                if (byTrack.TryGetValue(m.Key, out known))
                {
                    if (!RetryDue(known)) return known;
                    priorFails = known.Fails;
                }
                if (!SongLength(m.DurationMs))
                {
                    if (m.DurationMs <= 0) return null;
                    return byTrack[m.Key] = new Lyrics { Key = m.Key, State = LyricState.Missing };
                }
                if (byTrack.Count > KeepInRam) byTrack.Clear();
                pending = byTrack[m.Key] = new Lyrics { Key = m.Key, State = LyricState.Loading };
            }
            var track = new[] { m.Key, m.Title, m.Artist };
            double length = m.DurationMs;
            Task.Run(() => Deliver(Fetch(track[0], track[1], track[2], length), priorFails));
            return pending;
        }

        // Failures back off, 2 min max
        static bool RetryDue(Lyrics l)
        {
            if (l.State != LyricState.Failed) return false;
            double backoff = 10 * Math.Pow(2, Math.Max(0, l.Fails - 1));
            return (DateTime.UtcNow - l.FailedUtc).TotalSeconds > Math.Min(120, backoff);
        }

        // Ads, podcasts, unknown length: skip
        static bool SongLength(double ms) { return ms >= 31000 && ms <= 15 * 60000; }

        void Deliver(Lyrics result, int priorFails)
        {
            if (result.State == LyricState.Failed) result.Fails = priorFails + 1;
            lock (byTrack) byTrack[result.Key] = result;
            var ready = Ready;
            if (ready != null) ready(result);
        }

        Lyrics Fetch(string key, string rawTitle, string rawArtist, double durationMs)
        {
            var song = TrackName.From(rawTitle, rawArtist);
            var outcome = new Lyrics { Key = key, State = LyricState.Missing };
            // No artist: a clip or recording, not a song
            if (song.Artist.Length == 0 || song.Title.Length == 0) return outcome;
            string label = song.Artist + " - " + song.Title;
            string stem = Path.Combine(Folder, CacheName((song.Artist + "|" + song.Title).ToLowerInvariant()));
            try
            {
                string synced = ReadIfThere(stem + ".lrc");
                string plain = synced == null ? ReadIfThere(stem + ".txt") : null;
                if (synced == null && plain == null)
                {
                    Download(song, (int)Math.Round(durationMs / 1000), label, out synced, out plain);
                    Directory.CreateDirectory(Folder);
                    if (synced != null) WriteUtf8(stem + ".lrc", synced);
                    else if (plain != null) WriteUtf8(stem + ".txt", plain);
                }
                Fill(outcome, synced, plain);
            }
            catch (Exception ex)
            {
                Log.Write("lyrics '" + label + "': " + ex.Message);
                outcome.State = LyricState.Failed;
                outcome.FailedUtc = DateTime.UtcNow;
            }
            return outcome;
        }

        static string ReadIfThere(string path) { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }

        static void WriteUtf8(string path, string text) { File.WriteAllText(path, text, new UTF8Encoding(false)); }

        static void Fill(Lyrics into, string synced, string plain)
        {
            if (synced != null)
            {
                into.Lines = Parse(synced);
                if (into.Lines.Count > 0) into.State = LyricState.Synced;
                return;
            }
            if (plain == null) return;
            // Untimed: panel only, no karaoke
            foreach (var row in plain.Replace("\r", "").Split('\n'))
                into.Lines.Add(new LyricLine { AtMs = -1, Text = row.Trim() });
            if (into.Lines.Count > 0) into.State = LyricState.Plain;
        }

        static void Download(TrackName song, int seconds, string label, out string synced, out string plain)
        {
            synced = plain = null;
            string query = "track_name=" + Uri.EscapeDataString(song.Title) + "&artist_name=" + Uri.EscapeDataString(song.Artist);
            Exception lrclibError = null;
            try
            {
                var direct = Lrclib("https://lrclib.net/api/get?" + query + "&duration=" + seconds);
                if (direct != null)
                {
                    synced = Field(direct, "syncedLyrics");
                    plain = Field(direct, "plainLyrics");
                }
                string loose = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString((song.Artist + " " + song.Title).Trim());
                foreach (var url in new[] { "https://lrclib.net/api/search?" + query, loose })
                {
                    if (synced != null) break;
                    var hits = Lrclib(url) as object[];
                    synced = Closest(hits, seconds, "syncedLyrics");
                    plain = plain ?? Closest(hits, seconds, "plainLyrics");
                }
            }
            catch (Exception ex) { lrclibError = ex; }
            // LRCLIB busy or unsynced: NetEase
            if (synced == null)
            {
                try { synced = NetEase(song.Title, song.Artist, seconds); }
                catch (Exception ex)
                {
                    if (lrclibError != null) throw new Exception(lrclibError.Message + "; " + ex.Message);
                }
            }
            if (lrclibError == null) return;
            if (synced == null && plain == null) throw lrclibError;
            Log.Write("lyrics '" + label + "': " + lrclibError.Message + (synced != null ? " · NetEase" : ""));
        }

        // Pauses before retrying a busy server
        static readonly int[] LrclibPauses = { 1000 }, NetEasePauses = { 1000, 2500 };

        static WebResult Fetch(string url, int[] pauses)
        {
            int attempt = 0;
            while (true)
            {
                var r = Web.Send("GET", url, null, null, 9000);
                bool transient = r.Status == 0 || r.Status == 429 || r.Status >= 500;
                if (!transient || attempt >= pauses.Length) return r;
                System.Threading.Thread.Sleep(pauses[attempt++]);
            }
        }

        static object Lrclib(string url)
        {
            var r = Fetch(url, LrclibPauses);
            switch (r.Status)
            {
                case 400:
                case 404:
                    return null;
            }
            if (!r.Ok) throw new Exception("LRCLIB " + r.Status + " " + Fmt.OneLine(r.Body, 80));
            return Json.Parse(r.Body);
        }

        // Non-blank text field, else null
        static string Field(object item, string name)
        {
            string s = Json.Str(item, name);
            return s.Trim().Length == 0 ? null : s;
        }

        // Nearest length within 8 s; other edits drift
        static string Closest(object[] hits, int seconds, string field)
        {
            if (hits == null) return null;
            string pick = null;
            double gap = 0;
            for (int i = 0; i < hits.Length; i++)
            {
                string text = Field(hits[i], field);
                if (text == null) continue;
                double d = Math.Abs(Json.Num(hits[i], "duration", seconds) - seconds);
                if (d <= 8 && (pick == null || d < gap)) { gap = d; pick = text; }
            }
            return pick;
        }

        static readonly Regex Credit = new Regex(@"^(作词|作曲|编曲|制作人|监制|混音|母带|录音|和声|吉他|贝斯|鼓|键盘|弦乐|出品|发行|词|曲)\s*[:：]");

        // Same song: artist, title, length agree
        static string NetEase(string title, string artist, int seconds)
        {
            if (artist.Length == 0) return null;
            var r = Fetch("https://music.163.com/api/search/get?s=" + Uri.EscapeDataString(artist + " " + title) + "&type=1&limit=10", NetEasePauses);
            if (!r.Ok) throw new Exception("NetEase " + r.Status);
            var songs = Json.Arr(Json.Obj(Json.Parse(r.Body), "result"), "songs");
            string wantTitle = Bare(title), wantArtist = Letters(artist);
            if (songs == null || wantTitle.Length == 0 || wantArtist.Length == 0) return null;
            double id = PickSong(songs, wantTitle, wantArtist, seconds);
            if (id <= 0) return null;
            r = Fetch("https://music.163.com/api/song/lyric?id=" + ((long)id).ToString(CultureInfo.InvariantCulture) + "&lv=1", NetEasePauses);
            if (!r.Ok) throw new Exception("NetEase " + r.Status);
            string body = Json.Str(Json.Obj(Json.Parse(r.Body), "lrc"), "lyric");
            // Instrumental marker
            if (body.Contains("纯音乐")) return null;
            var kept = new StringBuilder();
            foreach (var row in body.Replace("\r", "").Split('\n'))
            {
                var stamp = Stamp.Match(row);
                if (stamp.Success && !Credit.IsMatch(row.Substring(stamp.Index + stamp.Length).Trim())) kept.Append(row).Append('\n');
            }
            string lrc = kept.ToString();
            return Parse(lrc).Count > 0 ? lrc : null;
        }

        static double PickSong(object[] songs, string wantTitle, string wantArtist, int seconds)
        {
            double closest = double.MaxValue, id = 0;
            foreach (var s in songs)
            {
                double gap = Math.Abs(Json.Num(s, "duration", 0) / 1000 - seconds);
                if (gap > 8 || !(gap < closest) || Bare(Json.Str(s, "name")) != wantTitle) continue;
                if (!SharesArtist(Json.Arr(s, "artists"), wantArtist)) continue;
                closest = gap;
                id = Json.Num(s, "id", 0);
            }
            return id;
        }

        static bool SharesArtist(object[] artists, string want)
        {
            if (artists == null) return false;
            foreach (var a in artists)
            {
                string name = Letters(Json.Str(a, "name"));
                if (name.Length > 0 && (want.Contains(name) || name.Contains(want))) return true;
            }
            return false;
        }

        static readonly Regex Bracketed = new Regex(@"\s*[(\[][^)\]]*[)\]]");

        // Title without (feat.), [Remastered] and the like
        static string Bare(string s) { return Letters(Bracketed.Replace(s ?? "", "")); }

        // Lowercase letters and digits, accents split off
        internal static string Letters(string s)
        {
            string d = (s ?? "").Normalize(NormalizationForm.FormD);
            var keep = new char[d.Length];
            int n = 0;
            for (int i = 0; i < d.Length; i++)
                if (char.IsLetterOrDigit(d[i])) keep[n++] = char.ToLowerInvariant(d[i]);
            return new string(keep, 0, n);
        }

        static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]");

        static List<LyricLine> Parse(string lrc)
        {
            var timed = new List<LyricLine>();
            foreach (var row in lrc.Split('\n'))
            {
                var stamps = Stamp.Matches(row);
                int n = stamps.Count;
                if (n == 0) continue;
                // Text follows the last stamp
                var tail = stamps[n - 1];
                string words = row.Substring(tail.Index + tail.Length).Trim();
                for (int i = 0; i < n; i++)
                    timed.Add(new LyricLine { AtMs = StampMs(stamps[i]), Text = words });
            }
            timed.Sort(Earlier);
            return timed;
        }

        static double StampMs(Match stamp)
        {
            double minutes = double.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
            string secText = stamp.Groups[2].Value.Replace(':', '.');
            return (minutes * 60 + double.Parse(secText, CultureInfo.InvariantCulture)) * 1000;
        }

        static int Earlier(LyricLine x, LyricLine y) { return x.AtMs.CompareTo(y.AtMs); }

        // 64-bit FNV-1a; names the cache files
        static string CacheName(string s)
        {
            const ulong Basis = 0xcbf29ce484222325, Prime = 0x100000001b3;
            ulong h = Basis;
            for (int i = 0; i < s.Length; i++) h = unchecked((h ^ s[i]) * Prime);
            return h.ToString("x16");
        }
    }

    // Player title and channel as song + artist
    struct TrackName
    {
        public string Title, Artist;

        static readonly Regex Junk = new Regex(@"official|video|audio|lyric|lirik|\bmv\b|m/v|\bhd\b|\b4k\b|visuali[sz]er|remaster|explicit|live session", RegexOptions.IgnoreCase);
        static readonly Regex Tag = new Regex(@"\s*[(\[【](?<inner>[^)\]】]*)[)\]】]");
        static readonly Regex PipeTail = new Regex(@"\s+\|.*$");
        static readonly Regex ChannelName = new Regex(@"(vevo|\s-\s*topic|official|records|music|channel|\btv)$|^official", RegexOptions.IgnoreCase);
        static readonly Regex ArtistDashSong = new Regex(@"^(?<who>.+?)\s+[-–—]\s+(?<what>.+)$");
        static readonly Regex ChannelSuffix = new Regex(@"vevo$|\s*-\s*topic$", RegexOptions.IgnoreCase);
        static readonly Regex ArtistNoise = new Regex(@"\s*-\s*topic$|vevo$|^official\s*", RegexOptions.IgnoreCase);
        static readonly Regex FeatTag = new Regex(@"\s*[(\[](ft\.?|feat\.?|featuring)\s[^)\]]*[)\]]", RegexOptions.IgnoreCase);
        static readonly Regex FeatTail = new Regex(@"\s+(ft\.?|feat\.?|featuring)\s+.*$", RegexOptions.IgnoreCase);

        public static TrackName From(string rawTitle, string rawArtist)
        {
            string title = Tag.Replace(rawTitle ?? "", m => Junk.IsMatch(m.Groups["inner"].Value) ? "" : m.Value);
            title = PipeTail.Replace(title, "");
            string artist = (rawArtist ?? "").Trim();

            // "Artist - Song" on a channel upload
            var split = ArtistDashSong.Match(title);
            if (split.Success && OwnsDash(artist, split.Groups["who"].Value))
            {
                artist = split.Groups["who"].Value.Trim();
                title = split.Groups["what"].Value.Trim();
            }
            artist = ArtistNoise.Replace(artist, "");
            title = FeatTail.Replace(FeatTag.Replace(title, ""), "");
            return new TrackName { Title = title.Trim().Trim('"', '\'', '-', ' '), Artist = artist.Trim() };
        }

        // Uploader channel, or glued artist name
        static bool OwnsDash(string uploader, string before)
        {
            if (uploader.Length == 0 || ChannelName.IsMatch(uploader)) return true;
            string a = LyricsService.Letters(ChannelSuffix.Replace(uploader, "")), b = LyricsService.Letters(before);
            return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
        }
    }
}
