using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Tepi
{
    class MediaInfo
    {
        public static readonly MediaInfo None = new MediaInfo();

        public bool Present, Playing, CanSeek;
        public string Title = "", Artist = "", App = "", Key = "";
        public double PositionMs, DurationMs;
        public DateTime PositionAtUtc;
        public Bitmap Cover;

        public double Position(DateTime nowUtc)
        {
            double p = PositionMs;
            if (Playing) p += (nowUtc - PositionAtUtc).TotalMilliseconds;
            if (DurationMs > 0) p = Math.Min(p, DurationMs);
            return Math.Max(0, p);
        }
    }

    // Media sessions from any player
    class MediaService
    {
        public event Action<MediaInfo> Changed;

        readonly AutoResetEvent signal = new AutoResetEvent(true);
        readonly Queue<Action<GlobalSystemMediaTransportControlsSession>> commands = new Queue<Action<GlobalSystemMediaTransportControlsSession>>();
        GlobalSystemMediaTransportControlsSessionManager manager;
        GlobalSystemMediaTransportControlsSession subscribed;
        string key;
        Bitmap cover;
        int coverTries;
        DateTime nextCoverUtc = DateTime.MaxValue;
        bool present;

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "media" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void TogglePlay() { Enqueue(s => Fire(s.TryTogglePlayPauseAsync())); }
        public void Next() { Enqueue(s => Fire(s.TrySkipNextAsync())); }
        public void Previous() { Enqueue(s => Fire(s.TrySkipPreviousAsync())); }
        public void Seek(double ms) { Enqueue(s => Fire(s.TryChangePlaybackPositionAsync((long)(ms * 10000)))); }

        // Results arrive through the session events
        static void Fire(IAsyncOperation<bool> op) { }

        void Enqueue(Action<GlobalSystemMediaTransportControlsSession> cmd)
        {
            lock (commands) commands.Enqueue(cmd);
            signal.Set();
        }

        void Run()
        {
            try
            {
                manager = Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                manager.CurrentSessionChanged += delegate { signal.Set(); };
                manager.SessionsChanged += delegate { lock (watched) watched.Clear(); signal.Set(); };
            }
            catch (Exception ex) { Log.Write("media unavailable: " + ex.Message); return; }

            int failures = 0;
            DateTime retryUtc = DateTime.MinValue;
            while (true)
            {
                int timeout = 10000;
                if (nextCoverUtc != DateTime.MaxValue) timeout = Math.Max(10, (int)(nextCoverUtc - DateTime.UtcNow).TotalMilliseconds);
                // Failing session: back off to 30s
                if (failures > 0) timeout = Math.Max(timeout, (int)Math.Max(10, (retryUtc - DateTime.UtcNow).TotalMilliseconds));
                signal.WaitOne(timeout);
                Thread.Sleep(25);
                RunCommands();
                if (failures > 0 && DateTime.UtcNow < retryUtc) continue;
                try { Refresh(); failures = 0; }
                catch (Exception ex)
                {
                    failures++;
                    retryUtc = DateTime.UtcNow.AddMilliseconds(Math.Min(30000, 500 << Math.Min(6, failures - 1)));
                    Log.Write("media refresh: " + ex.Message);
                }
            }
        }

        readonly HashSet<string> watched = new HashSet<string>();

        // Audible player wins, else current session
        GlobalSystemMediaTransportControlsSession Pick()
        {
            var current = manager.GetCurrentSession();
            GlobalSystemMediaTransportControlsSession playing = current != null && IsPlaying(current) ? current : null;
            try
            {
                foreach (var s in manager.GetSessions())
                {
                    // Watch every player, not just shown
                    string id = s.SourceAppUserModelId ?? "";
                    bool fresh;
                    lock (watched) fresh = watched.Add(id);
                    if (fresh) s.PlaybackInfoChanged += OnPlayback;
                    if (playing == null && IsPlaying(s)) playing = s;
                }
            }
            catch (Exception) { }
            return playing ?? current;
        }

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch (Exception) { return false; }
        }

        void RunCommands()
        {
            while (true)
            {
                Action<GlobalSystemMediaTransportControlsSession> cmd;
                lock (commands)
                {
                    if (commands.Count == 0) return;
                    cmd = commands.Dequeue();
                }
                try
                {
                    var s = subscribed ?? manager.GetCurrentSession();
                    if (s != null) cmd(s);
                }
                catch (Exception ex) { Log.Write("media command: " + ex.Message); }
            }
        }

        void Subscribe(GlobalSystemMediaTransportControlsSession s)
        {
            if (subscribed != null)
            {
                try
                {
                    subscribed.MediaPropertiesChanged -= OnProps;
                    subscribed.PlaybackInfoChanged -= OnPlayback;
                    subscribed.TimelinePropertiesChanged -= OnTimeline;
                }
                catch (Exception) { }
            }
            subscribed = s;
            if (s == null) return;
            s.MediaPropertiesChanged += OnProps;
            s.PlaybackInfoChanged += OnPlayback;
            s.TimelinePropertiesChanged += OnTimeline;
        }

        void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) { signal.Set(); }
        void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) { signal.Set(); }
        void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) { signal.Set(); }

        void Refresh()
        {
            var s = Pick();
            if (!ReferenceEquals(s, subscribed)) Subscribe(s);
            if (s == null)
            {
                key = null;
                nextCoverUtc = DateTime.MaxValue;
                published = null;
                if (present) { present = false; Publish(MediaInfo.None); }
                return;
            }

            var props = Await(s.TryGetMediaPropertiesAsync());
            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            string app = AppName(s.SourceAppUserModelId);
            string title = props.Title ?? "";
            string artist = props.Artist ?? "";
            if (artist.Length == 0 && !string.IsNullOrEmpty(props.AlbumArtist)) artist = props.AlbumArtist;
            artist = System.Text.RegularExpressions.Regex.Replace(artist, @"\s*-\s*Topic$|(?<=\S)VEVO$", "").Trim();
            string k = title + "|" + artist + "|" + app;

            var now = DateTime.UtcNow;
            if (k != key)
            {
                key = k;
                cover = null;
                coverTries = 0;
                nextCoverUtc = now;
            }
            // Artwork lands after the title
            if (now >= nextCoverUtc)
            {
                coverTries++;
                if (cover == null || coverTries == 4) LoadCover(props.Thumbnail);
                nextCoverUtc = coverTries >= 5 ? DateTime.MaxValue : now.AddMilliseconds(coverTries < 3 ? 450 : 1200);
            }

            var info = new MediaInfo();
            info.Present = true;
            info.Title = title;
            info.Artist = artist;
            info.App = app;
            info.Key = k;
            info.Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            info.CanSeek = playback.Controls.IsPlaybackPositionEnabled;
            info.PositionMs = timeline.Position.TotalMilliseconds;
            info.DurationMs = (timeline.EndTime - timeline.StartTime).TotalMilliseconds;
            var updated = timeline.LastUpdatedTime.UtcDateTime;
            info.PositionAtUtc = updated.Year > 2000 && updated <= now.AddSeconds(1) ? updated : now;
            info.Cover = cover;
            present = true;
            // Unchanged track and timeline: no UI wake
            if (Same(published, info)) return;
            published = info;
            Publish(info);
        }

        MediaInfo published;

        static bool Same(MediaInfo a, MediaInfo b)
        {
            if (a == null || a.Key != b.Key || a.Playing != b.Playing || a.CanSeek != b.CanSeek || !ReferenceEquals(a.Cover, b.Cover) || Math.Abs(a.DurationMs - b.DurationMs) > 1) return false;
            var now = DateTime.UtcNow;
            return Math.Abs(a.Position(now) - b.Position(now)) < 1000;
        }

        void Publish(MediaInfo info)
        {
            var h = Changed;
            if (h != null) h(info);
        }

        void LoadCover(IRandomAccessStreamReference reference)
        {
            if (reference == null) return;
            try
            {
                byte[] bytes;
                using (var stream = Await(reference.OpenReadAsync()))
                {
                    ulong size = stream.Size;
                    if (size == 0 || size > 16 * 1024 * 1024) return;
                    using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                    {
                        uint n = Await(reader.LoadAsync((uint)size));
                        bytes = new byte[n];
                        reader.ReadBytes(bytes);
                    }
                }
                using (var ms = new MemoryStream(bytes))
                using (var src = Image.FromStream(ms))
                    cover = Ink.Grayscale(src, 160);
            }
            catch (Exception ex) { Log.Write("cover: " + ex.Message); }
        }

        static string AppName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string l = id.ToLowerInvariant();
            if (l.Contains("spotify")) return "Spotify";
            if (l.Contains("chrome")) return "Chrome";
            if (l.Contains("msedge")) return "Edge";
            if (l.Contains("firefox")) return "Firefox";
            if (l.Contains("brave")) return "Brave";
            if (l.Contains("zunemusic")) return "Media Player";
            string n = id.Split('!')[0];
            n = n.Substring(n.LastIndexOf('\\') + 1);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }

        internal static T Await<T>(IAsyncOperation<T> op)
        {
            using (var done = new ManualResetEvent(false))
            {
                op.Completed = delegate { try { done.Set(); } catch (ObjectDisposedException) { } };
                if (op.Status == AsyncStatus.Started && !done.WaitOne(5000)) throw new TimeoutException("WinRT timeout");
            }
            if (op.Status != AsyncStatus.Completed)
                throw new InvalidOperationException("WinRT " + op.Status + (op.ErrorCode != null ? " 0x" + op.ErrorCode.HResult.ToString("X8") : ""));
            return op.GetResults();
        }
    }
}
