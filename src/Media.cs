using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Tepi
{
    class MediaInfo
    {
        public static readonly MediaInfo None = new MediaInfo();

        public bool Present, Playing, CanSeek;
        // Only players that report them
        public bool CanShuffle, Shuffle, CanRepeat;
        // 0 off, 1 track, 2 list
        public int Repeat;
        public string Title = "", Artist = "", App = "", Key = "", AppId = "";
        // Every player with a session: id, name
        public string[][] Sources = new string[0][];
        public double PositionMs, DurationMs;
        public DateTime PositionAtUtc;
        public Bitmap Cover;

        // Reported position moved forward while playing
        public double Position(DateTime nowUtc)
        {
            double at = PositionMs;
            if (Playing) at += (nowUtc - PositionAtUtc).TotalMilliseconds;
            if (DurationMs > 0 && at > DurationMs) at = DurationMs;
            return at < 0 ? 0 : at;
        }
    }

    // Media sessions from any player
    class MediaService
    {
        public event Action<MediaInfo> Changed;

        readonly AutoResetEvent wake = new AutoResetEvent(true);
        readonly ConcurrentQueue<Func<Session, IAsyncInfo>> orders = new ConcurrentQueue<Func<Session, IAsyncInfo>>();
        readonly HashSet<string> watched = new HashSet<string>();
        GlobalSystemMediaTransportControlsSessionManager hub;
        Session followed;
        string[][] players = new string[0][];
        // Panel choice, for its session
        volatile string preferred;
        MediaInfo sent;
        bool showing;

        // Artwork lands after the title: poll briefly
        static readonly int[] ArtGapsMs = { 450, 450, 1200, 1200 };
        const int ArtRecheck = 4;
        string artTrack;
        Bitmap art;
        int artTries;
        DateTime artDueUtc = DateTime.MaxValue;

        public void Start()
        {
            var worker = new Thread(Prof.Wrap(Loop)) { IsBackground = true, Name = "media" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public void TogglePlay() { Order(s => s.TryTogglePlayPauseAsync()); }
        public void Play() { Order(s => s.TryPlayAsync()); }
        public void Pause() { Order(s => s.TryPauseAsync()); }
        public void Next() { Order(s => s.TrySkipNextAsync()); }
        public void Previous() { Order(s => s.TrySkipPreviousAsync()); }
        public void Seek(double ms) { long ticks = (long)(ms * 10000); Order(s => s.TryChangePlaybackPositionAsync(ticks)); }
        public void SetShuffle(bool on) { Order(s => s.TryChangeShuffleActiveAsync(on)); }
        public void SetRepeat(int mode) { var m = RepeatMode(mode); Order(s => s.TryChangeAutoRepeatModeAsync(m)); }

        public void Prefer(string id)
        {
            preferred = id;
            wake.Set();
        }

        // Fire and forget; session events report back
        void Order(Func<Session, IAsyncInfo> act)
        {
            orders.Enqueue(act);
            wake.Set();
        }

        static MediaPlaybackAutoRepeatMode RepeatMode(int mode)
        {
            if (mode == 1) return MediaPlaybackAutoRepeatMode.Track;
            if (mode == 2) return MediaPlaybackAutoRepeatMode.List;
            return MediaPlaybackAutoRepeatMode.None;
        }

        static int RepeatIndex(MediaPlaybackAutoRepeatMode? mode)
        {
            switch (mode ?? MediaPlaybackAutoRepeatMode.None)
            {
                case MediaPlaybackAutoRepeatMode.Track: return 1;
                case MediaPlaybackAutoRepeatMode.List: return 2;
                default: return 0;
            }
        }

        void Loop()
        {
            try
            {
                hub = WinRt.Get(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                hub.CurrentSessionChanged += (m, e) => wake.Set();
                hub.SessionsChanged += (m, e) => { lock (watched) watched.Clear(); wake.Set(); };
            }
            catch (Exception ex) { Log.Write("media unavailable: " + ex.Message); return; }

            int strikes = 0;
            var calmUtc = DateTime.MinValue;
            while (true)
            {
                double waitMs = artDueUtc == DateTime.MaxValue ? 10000 : (artDueUtc - DateTime.UtcNow).TotalMilliseconds;
                // Broken session: wait out the backoff
                if (strikes > 0) waitMs = Math.Max(waitMs, (calmUtc - DateTime.UtcNow).TotalMilliseconds);
                wake.WaitOne((int)Math.Max(10, waitMs));
                // Let event bursts settle
                Thread.Sleep(25);
                Drain();
                if (strikes > 0 && DateTime.UtcNow < calmUtc) continue;
                try
                {
                    Refresh();
                    strikes = 0;
                }
                catch (Exception ex)
                {
                    strikes++;
                    calmUtc = DateTime.UtcNow.AddMilliseconds(Math.Min(30000, 500 * Math.Pow(2, Math.Min(6, strikes - 1))));
                    Log.Write("media refresh: " + ex.Message);
                }
            }
        }

        void Drain()
        {
            Func<Session, IAsyncInfo> act;
            while (orders.TryDequeue(out act))
            {
                try
                {
                    var target = followed ?? hub.GetCurrentSession();
                    if (target != null) act(target);
                }
                catch (Exception ex) { Log.Write("media command: " + ex.Message); }
            }
        }

        // Chosen player, else audible, else current
        Session Choose()
        {
            var current = hub.GetCurrentSession();
            string want = preferred;
            Session wanted = null, audible = null;
            var seen = new List<string[]>();
            try
            {
                foreach (var s in hub.GetSessions())
                {
                    string id = s.SourceAppUserModelId ?? "";
                    Watch(s, id);
                    if (audible == null && Audible(s)) audible = s;
                    if (want != null && want == id) wanted = s;
                    seen.Add(new[] { id, AppName(id) });
                }
            }
            catch (Exception) { }
            players = seen.ToArray();
            if (wanted == null && want != null) preferred = null;
            if (wanted != null) return wanted;
            if (current != null && Audible(current)) return current;
            return audible ?? current;
        }

        // Background players wake us too
        void Watch(Session s, string id)
        {
            bool fresh;
            lock (watched) fresh = watched.Add(id);
            if (fresh) s.PlaybackInfoChanged += Poke;
        }

        static bool Audible(Session s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch (Exception) { return false; }
        }

        void Follow(Session s)
        {
            if (followed != null)
            {
                try { Hook(followed, false); }
                catch (Exception) { }
            }
            followed = s;
            if (s != null) Hook(s, true);
        }

        void Hook(Session s, bool attach)
        {
            if (attach)
            {
                s.MediaPropertiesChanged += Poke;
                s.PlaybackInfoChanged += Poke;
                s.TimelinePropertiesChanged += Poke;
            }
            else
            {
                s.MediaPropertiesChanged -= Poke;
                s.PlaybackInfoChanged -= Poke;
                s.TimelinePropertiesChanged -= Poke;
            }
        }

        void Poke(Session s, MediaPropertiesChangedEventArgs e) { wake.Set(); }
        void Poke(Session s, PlaybackInfoChangedEventArgs e) { wake.Set(); }
        void Poke(Session s, TimelinePropertiesChangedEventArgs e) { wake.Set(); }

        void Refresh()
        {
            var s = Choose();
            if (!ReferenceEquals(s, followed)) Follow(s);
            if (s == null)
            {
                artTrack = null;
                artDueUtc = DateTime.MaxValue;
                sent = null;
                if (showing)
                {
                    showing = false;
                    Raise(MediaInfo.None);
                }
                return;
            }
            var info = Describe(s);
            showing = true;
            // Same track, same timeline: no UI wake
            if (!Differs(sent, info)) return;
            sent = info;
            Raise(info);
        }

        MediaInfo Describe(Session s)
        {
            var props = WinRt.Get(s.TryGetMediaPropertiesAsync());
            var state = s.GetPlaybackInfo();
            var line = s.GetTimelineProperties();
            string aumid = s.SourceAppUserModelId ?? "";
            var info = new MediaInfo { Present = true, AppId = aumid, App = AppName(aumid), Sources = players };
            info.Title = props.Title ?? "";
            info.Artist = ArtistOf(props);
            info.Key = string.Join("|", info.Title, info.Artist, info.App);

            var now = DateTime.UtcNow;
            UpdateArt(info.Key, props, now);
            info.Cover = art;

            var can = state.Controls;
            info.Playing = state.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.CanSeek = can.IsPlaybackPositionEnabled;
            info.CanShuffle = can.IsShuffleEnabled && state.IsShuffleActive.HasValue;
            info.Shuffle = state.IsShuffleActive == true;
            info.CanRepeat = can.IsRepeatEnabled && state.AutoRepeatMode.HasValue;
            info.Repeat = RepeatIndex(state.AutoRepeatMode);

            info.PositionMs = line.Position.TotalMilliseconds;
            info.DurationMs = (line.EndTime - line.StartTime).TotalMilliseconds;
            // Players without a stamp: trust now
            var stamped = line.LastUpdatedTime.UtcDateTime;
            bool plausible = stamped.Year > 2000 && stamped <= now.AddSeconds(1);
            info.PositionAtUtc = plausible ? stamped : now;
            return info;
        }

        // YouTube channel suffixes: " - Topic", "VEVO"
        static readonly Regex ChannelTag = new Regex(@"(?<=\S)VEVO$|\s*-\s*Topic$");

        static string ArtistOf(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            string who = props.Artist ?? "";
            if (who.Length == 0 && !string.IsNullOrEmpty(props.AlbumArtist)) who = props.AlbumArtist;
            return ChannelTag.Replace(who, "").Trim();
        }

        void UpdateArt(string track, GlobalSystemMediaTransportControlsSessionMediaProperties props, DateTime now)
        {
            if (track != artTrack)
            {
                artTrack = track;
                art = null;
                artTries = 0;
                artDueUtc = now;
            }
            if (now < artDueUtc) return;
            artTries++;
            // Players sometimes swap art once more
            if (art == null || artTries == ArtRecheck)
            {
                var loaded = LoadArt(props.Thumbnail);
                if (loaded != null) art = loaded;
            }
            artDueUtc = artTries > ArtGapsMs.Length ? DateTime.MaxValue : now.AddMilliseconds(ArtGapsMs[artTries - 1]);
        }

        static Bitmap LoadArt(IRandomAccessStreamReference source)
        {
            try
            {
                var bytes = WinRt.ReadAll(source, 16 * 1024 * 1024);
                if (bytes == null) return null;
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                    return Ink.Grayscale(img, 160);
            }
            catch (Exception ex)
            {
                Log.Write("cover: " + ex.Message);
                return null;
            }
        }

        static bool Differs(MediaInfo before, MediaInfo after)
        {
            if (before == null) return true;
            bool sameShape = before.Key == after.Key && before.Playing == after.Playing && before.CanSeek == after.CanSeek
                && before.Shuffle == after.Shuffle && before.Repeat == after.Repeat && before.CanShuffle == after.CanShuffle
                && before.CanRepeat == after.CanRepeat && ReferenceEquals(before.Cover, after.Cover)
                && !(Math.Abs(before.DurationMs - after.DurationMs) > 1) && before.Sources.Length == after.Sources.Length;
            if (!sameShape) return true;
            var t = DateTime.UtcNow;
            return !(Math.Abs(before.Position(t) - after.Position(t)) < 1000);
        }

        void Raise(MediaInfo info)
        {
            var handler = Changed;
            if (handler != null) handler(info);
        }

        // AUMID fragment, display name
        static readonly string[][] KnownPlayers =
        {
            new[] { "spotify", "Spotify" },
            new[] { "chrome", "Chrome" },
            new[] { "msedge", "Edge" },
            new[] { "firefox", "Firefox" },
            new[] { "brave", "Brave" },
            new[] { "zunemusic", "Media Player" },
        };

        static string AppName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string lower = id.ToLowerInvariant();
            foreach (var p in KnownPlayers)
                if (lower.Contains(p[0])) return p[1];
            // "C:\...\foo.exe!App" to "foo"
            int bang = id.IndexOf('!');
            string name = bang < 0 ? id : id.Substring(0, bang);
            name = name.Substring(name.LastIndexOf('\\') + 1);
            const string Exe = ".exe";
            if (name.EndsWith(Exe, StringComparison.OrdinalIgnoreCase)) name = name.Remove(name.Length - Exe.Length);
            return name;
        }
    }

    // Blocking waits on WinRT async calls
    static class WinRt
    {
        // False if still running after ms
        public static bool Settle(IAsyncInfo op, Action<Action> onDone, int ms)
        {
            if (op.Status != AsyncStatus.Started) return true;
            var gate = new object();
            bool done = false;
            onDone(() => { lock (gate) { done = true; Monitor.PulseAll(gate); } });
            var clock = Stopwatch.StartNew();
            lock (gate)
            {
                while (!done && op.Status == AsyncStatus.Started)
                {
                    long left = ms - clock.ElapsedMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(gate, (int)left);
                }
            }
            return true;
        }

        public static bool Settle<T>(IAsyncOperation<T> op, int ms)
        {
            return Settle(op, fire => op.Completed = (o, s) => fire(), ms);
        }

        public static bool Settle(IAsyncAction op, int ms)
        {
            return Settle(op, fire => op.Completed = (o, s) => fire(), ms);
        }

        // Result within 5 s, else throws
        public static T Get<T>(IAsyncOperation<T> op)
        {
            if (!Settle(op, 5000)) throw new TimeoutException("WinRT timeout");
            if (op.Status == AsyncStatus.Completed) return op.GetResults();
            string code = op.ErrorCode != null ? " 0x" + op.ErrorCode.HResult.ToString("X8") : "";
            throw new InvalidOperationException("WinRT " + op.Status + code);
        }

        // Whole stream; null if empty or huge
        public static byte[] ReadAll(IRandomAccessStreamReference source, ulong max)
        {
            if (source == null) return null;
            using (var stream = Get(source.OpenReadAsync()))
            {
                ulong size = stream.Size;
                if (size == 0 || size > max) return null;
                using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    var bytes = new byte[Get(reader.LoadAsync((uint)size))];
                    reader.ReadBytes(bytes);
                    return bytes;
                }
            }
        }
    }
}
