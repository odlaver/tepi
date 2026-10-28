using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Tepi
{
    class FetchResult
    {
        public int Status, Length;
        public byte[] Data = new byte[0];
        public string RetryAfter = "", ContentType = "";
        public bool TooBig, Revoked, Insecure;
        public bool Ok { get { return Status >= 200 && Status < 300 && !TooBig; } }
        public string Text { get { return Encoding.UTF8.GetString(Data, 0, Length); } }
    }

    // HTTPS only, capped body, manual redirects
    static class Fetch
    {
        public const int JsonCap = 2 * 1024 * 1024;

        static Fetch()
        {
            // TLS 1.2+ only; legacy defaults include SSL3
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch (Exception) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
        }

        public static FetchResult Send(string method, string url, Dictionary<string, string> headers, string body, int maxBytes, int timeoutMs)
        {
            var res = new FetchResult();
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) { res.Insecure = true; return res; }
            for (int hop = 0; hop <= 3; hop++)
            {
                HttpWebResponse resp = null;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(uri);
                    req.Method = method;
                    req.AllowAutoRedirect = false;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    req.Timeout = req.ReadWriteTimeout = timeoutMs;
                    req.UserAgent = "Tepi/" + Island.AppVersion + " (+https://github.com/odlaver/tepi)";
                    req.ServicePoint.Expect100Continue = false;
                    if (headers != null)
                        foreach (var h in headers)
                        {
                            if (h.Key == "Content-Type") req.ContentType = h.Value;
                            else if (h.Key == "Accept") req.Accept = h.Value;
                            else req.Headers[h.Key] = h.Value;
                        }
                    if (body != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(body);
                        if (req.ContentType == null) req.ContentType = "application/json";
                        req.ContentLength = bytes.Length;
                        using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    }
                    else if (method != "GET") req.ContentLength = 0;
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException ex)
                    {
                        resp = ex.Response as HttpWebResponse;
                        if (resp == null) { res.Status = 0; return res; }
                    }
                    res.Status = (int)resp.StatusCode;
                    res.RetryAfter = resp.Headers["Retry-After"] ?? "";
                    res.ContentType = resp.ContentType ?? "";
                    if (res.Status >= 300 && res.Status < 400 && res.Status != 304)
                    {
                        Uri next;
                        string loc = resp.Headers["Location"] ?? "";
                        if (loc.Length == 0 || !Uri.TryCreate(uri, loc, out next)) return res;
                        // Outlook: dead links redirect to errors
                        string l = next.AbsoluteUri.ToLowerInvariant();
                        if (l.Contains("httpcode=404") || l.Contains("errorfe.aspx") || l.Contains("olkerror")) { res.Revoked = true; res.Status = 404; return res; }
                        if (next.Scheme != Uri.UriSchemeHttps) { res.Insecure = true; return res; }
                        // Token or body never crosses origins
                        bool cross = Uri.Compare(uri, next, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0;
                        if (cross && (method != "GET" || (headers != null && headers.ContainsKey("Authorization")))) return res;
                        if (res.Status == 303) { method = "GET"; body = null; }
                        uri = next;
                        continue;
                    }
                    Read(resp, res, maxBytes, timeoutMs * 2);
                    return res;
                }
                catch (Exception) { res.Status = 0; return res; }
                finally { if (resp != null) resp.Close(); }
            }
            res.Status = 0;
            return res;
        }

        // Size cap and an overall deadline
        static void Read(HttpWebResponse resp, FetchResult res, int max, int deadlineMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[32 * 1024];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > max) { res.TooBig = true; break; }
                    ms.Write(buf, 0, n);
                    if (clock.ElapsedMilliseconds > deadlineMs) { res.Status = 0; break; }
                }
                res.Data = ms.GetBuffer();
                res.Length = (int)ms.Length;
            }
        }

        // Seconds from Retry-After, else 0
        public static int RetrySeconds(FetchResult r)
        {
            int s;
            return int.TryParse(r.RetryAfter.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out s) ? Math.Min(s, 3600) : 0;
        }
    }

    class SourceResult
    {
        public List<KuliaTask> Tasks;
        public List<ClassSlot> Events;
        public string Error, Log, Name;
        // Needs the user: token, link, sharing
        public bool Stop;
        public int RetryAfter;
        public TimeSpan Every;
        public object Extra;

        public static SourceResult Fail(string error, string log, bool stop)
        {
            return new SourceResult { Error = error, Log = log, Stop = stop };
        }

        public static SourceResult Http(string who, FetchResult r)
        {
            if (r.Status == 0) return Fail(Lang.F("{0} tidak terjangkau · dicoba lagi otomatis", who), "net", false);
            if (r.TooBig) return Fail(Lang.T("Respons terlalu besar"), "too big", true);
            if (r.Status == 401) return Fail(Lang.F("Token {0} ditolak", who), "http 401", true);
            if (r.Status == 403) return Fail(Lang.T("Tanpa izin akses"), "http 403", true);
            if (r.Status == 429) return new SourceResult { Error = Lang.T("Terlalu banyak permintaan · dicoba lagi otomatis"), Log = "http 429", RetryAfter = Fetch.RetrySeconds(r) };
            if (r.Status >= 500) return new SourceResult { Error = Lang.F("{0} sedang bermasalah · dicoba lagi otomatis", who), Log = "http " + r.Status, RetryAfter = Fetch.RetrySeconds(r) };
            return Fail(Lang.F("{0} menolak permintaan ({1})", who, r.Status), "http " + r.Status, false);
        }
    }

    // A connected task or calendar source
    abstract class Source
    {
        public string Key = "", Name = "";
        // UI-thread state
        public bool Busy, Stopped;
        public string Error;
        // HoldUtc: server asked to wait until
        public DateTime OkUtc = DateTime.MinValue, TriedUtc = DateTime.MinValue, NextUtc = DateTime.MinValue, HoldUtc = DateTime.MinValue;
        public int Failures, Version;
        // Removed from the hub; workers stop
        public volatile bool Gone;
        public List<KuliaTask> Tasks;
        public List<ClassSlot> Events;
        public TimeSpan Every = TimeSpan.FromMinutes(15);

        public abstract object Snapshot();
        // Worker thread; reads the snapshot only
        public abstract SourceResult Fetch(object snap, DateTime nowUtc);
        public virtual string Close(object snap, KuliaTask t) { return Lang.T("Tidak bisa ditandai dari Tepi"); }
        // UI thread; true means save config
        public virtual bool Absorb(SourceResult r) { return false; }
        public virtual bool GivesTasks { get { return true; } }
        public virtual bool GivesEvents { get { return false; } }

        // Fmt's set, plus breaks, fillers, tags
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Bounded work for huge fields
            if (s.Length > 8192) s = s.Substring(0, 8192);
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int c = s[i];
                if (c == 0x2028 || c == 0x2029) { sb.Append(' '); continue; }
                if (c == 0x034F || c == 0x115F || c == 0x1160 || c == 0x17B4 || c == 0x17B5 || c == 0x3164 || c == 0xFFA0 || (c >= 0x206A && c <= 0x206F) || (c >= 0xFFF9 && c <= 0xFFFB)) continue;
                // Tag characters U+E0000..E007F
                if (c == 0xDB40 && i + 1 < s.Length && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDC7F) { i++; continue; }
                sb.Append(s[i]);
            }
            string t = Fmt.OneLine(Fmt.StripHidden(sb.ToString()), max);
            // Never end on half a pair
            return t.Length > 0 && char.IsHighSurrogate(t[t.Length - 1]) ? t.Substring(0, t.Length - 1) : t;
        }

        // Printable ASCII only: safe in a header
        public static bool TokenOk(string t, int max)
        {
            if (t.Length < 20 || t.Length > max) return false;
            foreach (char c in t) if (c < 0x21 || c > 0x7E) return false;
            return true;
        }

        // http(s) links only
        public static string SafeLink(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 2000 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return "";
            return u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp ? u.AbsoluteUri : "";
        }

        // Deadline for a date without time
        public static DateTime EndOfDay(DateTime date)
        {
            return DateTime.SpecifyKind(date.Date.AddHours(23).AddMinutes(59), DateTimeKind.Local).ToUniversalTime();
        }
    }

    // Subscribed sources; sync off the UI thread
    class SourceHub
    {
        public NotionSource Notion;
        public TodoistSource Todoist;
        public readonly List<IcsFeed> Feeds = new List<IcsFeed>();
        // true: data changed
        public event Action<bool> Changed;

        readonly Action<Action> post;
        readonly object gate = new object();
        readonly Queue<Job> pending = new Queue<Job>();
        bool running;

        class Job
        {
            public Source Src;
            public object Snap;
            public int Version;
            public SourceResult Result;
        }

        public SourceHub(Action<Action> post) { this.post = post; }

        public void Load()
        {
            Notion = NotionSource.Load();
            Todoist = TodoistSource.Load();
            Feeds.Clear();
            Feeds.AddRange(IcsFeed.LoadAll());
        }

        public List<Source> All()
        {
            var list = new List<Source>();
            if (Notion != null) list.Add(Notion);
            if (Todoist != null) list.Add(Todoist);
            list.AddRange(Feeds);
            return list;
        }

        public bool Any { get { return Notion != null || Todoist != null || Feeds.Count > 0; } }

        public bool Busy
        {
            get
            {
                if ((Notion != null && Notion.Busy) || (Todoist != null && Todoist.Busy)) return true;
                foreach (var f in Feeds) if (f.Busy) return true;
                return false;
            }
        }

        public bool HasTasks
        {
            get
            {
                if (Notion != null || Todoist != null) return true;
                foreach (var f in Feeds) if (f.GivesTasks) return true;
                return false;
            }
        }

        public bool HasEvents
        {
            get
            {
                foreach (var f in Feeds) if (f.GivesEvents) return true;
                return false;
            }
        }

        // Never tried yet, or syncing
        public bool Waiting(bool tasks)
        {
            if (Notion != null && Pending(Notion, tasks)) return true;
            if (Todoist != null && Pending(Todoist, tasks)) return true;
            foreach (var f in Feeds) if (Pending(f, tasks)) return true;
            return false;
        }

        static bool Pending(Source s, bool tasks) { return (tasks ? s.GivesTasks : s.GivesEvents) && (s.Busy || s.TriedUtc == DateTime.MinValue); }

        public Source Find(string key)
        {
            foreach (var s in All()) if (s.Key == key) return s;
            return null;
        }

        bool Has(Source s) { return s == Notion || s == Todoist || (s is IcsFeed && Feeds.Contains((IcsFeed)s)); }

        // Every second; queues what is due
        public void Tick(DateTime now)
        {
            if (Notion != null && Due(Notion, now)) Queue(Notion);
            if (Todoist != null && Due(Todoist, now)) Queue(Todoist);
            for (int i = 0; i < Feeds.Count; i++) if (Due(Feeds[i], now)) Queue(Feeds[i]);
        }

        static bool Due(Source s, DateTime now) { return !s.Busy && now >= s.NextUtc && now >= s.HoldUtc; }

        // Page opened: refresh stale, healthy sources
        public void Stale(TimeSpan age)
        {
            var now = DateTime.UtcNow;
            foreach (var s in All())
                if (!s.Busy && !s.Stopped && s.Failures == 0 && now - s.TriedUtc > age && now >= s.HoldUtc) Queue(s);
        }

        // Manual refresh; ten seconds apart
        public int Refresh()
        {
            var now = DateTime.UtcNow;
            int n = 0;
            foreach (var s in All())
                if (!s.Busy && (now - s.TriedUtc).TotalSeconds >= 10 && now >= s.HoldUtc) { s.Stopped = false; Queue(s); n++; }
            return n;
        }

        // Network back: retry failures now
        public void Retry()
        {
            var now = DateTime.UtcNow;
            foreach (var s in All()) if (!s.Busy && !s.Stopped && s.Failures > 0 && now >= s.HoldUtc) Queue(s);
        }

        public void Queue(Source s)
        {
            if (s.Busy) return;
            s.Busy = true;
            var job = new Job { Src = s, Snap = s.Snapshot(), Version = s.Version };
            bool start;
            lock (gate)
            {
                pending.Enqueue(job);
                start = !running;
                running = true;
            }
            if (start) ThreadPool.QueueUserWorkItem(delegate { Work(); });
            Raise(false);
        }

        // One request queue for every source
        void Work()
        {
            while (true)
            {
                Job job;
                lock (gate)
                {
                    if (pending.Count == 0) { running = false; return; }
                    job = pending.Dequeue();
                }
                // Dropped or reconfigured meanwhile: no request
                if (!job.Src.Gone && Thread.VolatileRead(ref job.Src.Version) == job.Version)
                {
                    try { job.Result = job.Src.Fetch(job.Snap, DateTime.UtcNow); }
                    catch (Exception ex) { job.Result = SourceResult.Fail(Lang.T("Gagal memuat"), ex.GetType().Name, false); }
                }
                if (job.Result == null) job.Result = SourceResult.Fail(Lang.T("Gagal memuat"), "empty", false);
                var done = job;
                post(delegate { Apply(done); });
            }
        }

        void Apply(Job job)
        {
            var s = job.Src;
            var now = DateTime.UtcNow;
            s.Busy = false;
            s.TriedUtc = now;
            if (!Has(s)) { Raise(false); return; }
            var r = job.Result;
            if (r.RetryAfter > 0) s.HoldUtc = now.AddSeconds(r.RetryAfter);
            // Settings changed mid-fetch: fetch again
            if (job.Version != s.Version) { s.NextUtc = now; Raise(false); return; }
            bool data = r.Tasks != null || r.Events != null;
            if (data)
            {
                s.Tasks = r.Tasks;
                s.Events = r.Events;
                s.OkUtc = now;
            }
            s.Error = r.Error;
            s.Stopped = r.Stop && !data;
            if (r.Error == null || data)
            {
                s.Failures = 0;
                s.NextUtc = now + Interval(s, r);
            }
            else if (s.Stopped) s.NextUtc = DateTime.MaxValue;
            else
            {
                s.Failures++;
                s.NextUtc = now + Backoff(s.Failures, r.RetryAfter);
            }
            if (s.Absorb(r)) Persist(s);
            Log.Write("source " + s.Key + (r.Error == null ? ": ok " + Count(s.Tasks) + "/" + Count(s.Events) : ": " + (r.Log ?? "error")));
            Raise(data || r.Error != null);
        }

        void Persist(Source s)
        {
            if (s == Notion) Notion.Save();
            else if (s is IcsFeed) IcsFeed.SaveAll(Feeds);
        }

        static int Count<T>(List<T> list) { return list == null ? 0 : list.Count; }

        // Feed hint wins, within 15 min..6 h
        static TimeSpan Interval(Source s, SourceResult r)
        {
            var every = r.Every > s.Every ? r.Every : s.Every;
            return every > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : every;
        }

        static TimeSpan Backoff(int failures, int retryAfter)
        {
            if (retryAfter > 0) return TimeSpan.FromSeconds(Math.Max(30, retryAfter));
            int[] steps = { 2, 5, 10, 20, 40, 60 };
            return TimeSpan.FromMinutes(steps[Math.Min(failures, steps.Length) - 1]);
        }

        void Raise(bool data)
        {
            var h = Changed;
            if (h != null) h(data);
        }

        // Mark done; result on the UI thread
        public void Close(KuliaTask t, Action<string> done)
        {
            var s = Find(t.Source);
            if (s == null) { done(Lang.T("Sumbernya sudah dilepas")); return; }
            var snap = s.Snapshot();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                try { err = s.Close(snap, t); }
                catch (Exception ex) { err = Lang.T("Gagal tersimpan"); Log.Write("source " + s.Key + " close: " + ex.GetType().Name); }
                post(delegate
                {
                    // A fetch in flight may predate this
                    if (err == null && Has(s))
                    {
                        if (s.Busy) s.Version++;
                        else s.NextUtc = DateTime.UtcNow.AddSeconds(3);
                    }
                    if (err != null && s == Notion && Notion.NoWrite) Notion.Save();
                    done(err);
                    Raise(true);
                });
            });
        }

        // Settings changed: fetch with new config
        public void Touch(Source s)
        {
            // Stale handle from an old settings row
            if (!Has(s)) return;
            s.Version++;
            s.Stopped = false;
            // Rate limited: Tick sends it later
            if (!s.Busy && DateTime.UtcNow >= s.HoldUtc) Queue(s);
            else s.NextUtc = DateTime.UtcNow;
            Raise(true);
        }

        static void Retire(Source s)
        {
            if (s == null) return;
            s.Version++;
            s.Gone = true;
        }

        public void SetNotion(NotionSource n)
        {
            Retire(Notion);
            Notion = n;
            n.Save();
            Queue(n);
            Raise(true);
        }

        public void DropNotion()
        {
            if (Notion == null) return;
            Retire(Notion);
            Notion = null;
            NotionSource.Forget();
            Raise(true);
        }

        public void SetTodoist(string token)
        {
            Retire(Todoist);
            Todoist = new TodoistSource { Token = token };
            Todoist.Save();
            Queue(Todoist);
            Raise(true);
        }

        public void DropTodoist()
        {
            if (Todoist == null) return;
            Retire(Todoist);
            Todoist = null;
            TodoistSource.Forget();
            Raise(true);
        }

        public IcsFeed AddFeed(string id, string url, string kind, SourceResult first)
        {
            var f = IcsFeed.Make(id, url, first != null ? first.Name : "", kind);
            Feeds.Add(f);
            if (first != null && first.Error == null)
            {
                f.Tasks = first.Tasks;
                f.Events = first.Events;
                f.OkUtc = f.TriedUtc = DateTime.UtcNow;
                f.NextUtc = DateTime.UtcNow + Interval(f, first);
                f.Absorb(first);
            }
            else Queue(f);
            IcsFeed.SaveAll(Feeds);
            Raise(true);
            return f;
        }

        public void RemoveFeed(IcsFeed f)
        {
            Retire(f);
            Feeds.Remove(f);
            IcsFeed.SaveAll(Feeds);
            Raise(true);
        }

        public void SetFeedKind(IcsFeed f, string kind)
        {
            if (f.Kind == kind || !Feeds.Contains(f)) return;
            f.Kind = kind;
            IcsFeed.SaveAll(Feeds);
            Touch(f);
        }

        // Status line for settings
        public static string Status(Source s)
        {
            if (s.Busy && s.OkUtc == DateTime.MinValue) return Lang.T("Memuat…");
            if (s.Error != null) return s.OkUtc == DateTime.MinValue ? s.Error : s.Error + " · " + Lang.F("data {0}", Fmt.Ago(s.OkUtc));
            if (s.OkUtc == DateTime.MinValue) return Lang.T("Menunggu sinkron");
            var parts = new List<string>();
            if (s.Tasks != null && (s.Tasks.Count > 0 || s.Events == null)) parts.Add(s.Tasks.Count == 1 ? Lang.T("1 tugas") : Lang.F("{0} tugas", s.Tasks.Count));
            if (s.Events != null && (s.Events.Count > 0 || s.Tasks == null || s.Tasks.Count == 0)) parts.Add(s.Events.Count == 1 ? Lang.T("1 acara") : Lang.F("{0} acara", s.Events.Count));
            parts.Add(s.Busy ? Lang.T("memperbarui…") : Fmt.Ago(s.OkUtc));
            return string.Join(" · ", parts);
        }

        // Labels only when lists mix sources
        static Dictionary<string, string> names = new Dictionary<string, string>();
        static bool taskLabels, eventLabels;

        public static void SetLabels(Dictionary<string, string> map, bool tasks, bool events)
        {
            names = map;
            taskLabels = tasks;
            eventLabels = events;
        }

        public static string TaskLabel(string key) { return taskLabels ? Name(key) : ""; }
        public static string EventLabel(string key) { return eventLabels ? Name(key) : ""; }

        static string Name(string key)
        {
            string n;
            return key != null && names.TryGetValue(key, out n) ? n : "";
        }
    }

    // One iCal feed: schedule or tasks
    class IcsFeed : Source
    {
        static readonly string FilePath = Path.Combine(Settings.Dir, "calendars.vault");
        public string Id = "", Url = "", Kind = "jadwal";
        public bool Todos;

        public string Host
        {
            get
            {
                Uri u;
                return Uri.TryCreate(Url, UriKind.Absolute, out u) ? Clean(u.Host, 80) : "";
            }
        }

        public override bool GivesTasks { get { return Kind == "tugas" || Todos; } }
        public override bool GivesEvents { get { return Kind != "tugas"; } }

        public override object Snapshot() { return new[] { Url, Kind, Name, Key }; }

        // webcal and http become https; else null
        public static string Normalize(string input)
        {
            string s = (input ?? "").Trim();
            if (s.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s.Substring(10);
            else if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s.Substring(9);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s.Substring(7);
            Uri u;
            if (s.Length > 2000 || !Uri.TryCreate(s, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps || u.Host.Length == 0) return null;
            return s;
        }

        // Task apps publish their tasks as events
        public static string GuessKind(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return "jadwal";
            string h = u.Host.ToLowerInvariant();
            foreach (var app in new[] { "todoist.com", "ticktick.com", "asana.com", "clickup.com" })
                if (h == app || h.EndsWith("." + app)) return "tugas";
            return "jadwal";
        }

        public static string Provider(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return Lang.T("Kalender");
            string h = u.Host.ToLowerInvariant();
            if (h.EndsWith("calendar.google.com")) return "Google Calendar";
            if (h.StartsWith("outlook.")) return "Outlook";
            if (h.EndsWith("icloud.com")) return "iCloud";
            if (h.EndsWith("proton.me")) return "Proton Calendar";
            if (h == "user.fm") return "Fastmail";
            if (h.EndsWith("todoist.com")) return "Todoist";
            if (h.EndsWith("ticktick.com")) return "TickTick";
            return Clean(u.Host, 60);
        }

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 12); }

        public static IcsFeed Make(string id, string url, string name, string kind)
        {
            var feed = new IcsFeed { Id = id, Key = "ics:" + id, Url = url, Kind = kind == "tugas" ? "tugas" : "jadwal" };
            feed.Name = Clean(name, 60);
            if (feed.Name.Length == 0) feed.Name = Provider(url);
            return feed;
        }

        public static List<IcsFeed> LoadAll()
        {
            var list = new List<IcsFeed>();
            object saved;
            try { saved = Json.Parse(Vault.Load(FilePath) ?? ""); }
            catch (Exception ex) { Log.Write("calendars load: " + ex.GetType().Name); return list; }
            foreach (var o in Json.Arr(saved, "feeds"))
            {
                string url = Normalize(Json.Str(o, "url")), id = Json.Str(o, "id");
                if (url == null || id.Length == 0 || list.Exists(x => x.Id == id)) continue;
                var feed = new IcsFeed { Id = id, Key = "ics:" + id, Url = url, Kind = Json.Str(o, "kind") == "tugas" ? "tugas" : "jadwal", Todos = Json.Str(o, "todos") == "True" };
                feed.Name = Clean(Json.Str(o, "name"), 60);
                if (feed.Name.Length == 0) feed.Name = Provider(url);
                list.Add(feed);
            }
            return list;
        }

        public static void SaveAll(List<IcsFeed> feeds)
        {
            if (feeds.Count == 0) { Vault.Delete(FilePath); return; }
            var arr = new List<object>();
            foreach (var f in feeds)
                arr.Add(new Dictionary<string, object> { { "id", f.Id }, { "url", f.Url }, { "name", f.Name }, { "kind", f.Kind }, { "todos", f.Todos } });
            try { Vault.Save(FilePath, Json.Encode(new Dictionary<string, object> { { "feeds", arr.ToArray() } })); }
            catch (Exception ex) { Log.Write("calendars save: " + ex.GetType().Name); }
        }

        public override SourceResult Fetch(object snap, DateTime nowUtc)
        {
            var s = (string[])snap;
            return Pull(s[0], s[1], s[2], s[3], nowUtc);
        }

        // Fetch and expand; also validates links
        public static SourceResult Pull(string url, string kind, string name, string key, DateTime nowUtc)
        {
            var r = Tepi.Fetch.Send("GET", url, new Dictionary<string, string> { { "Accept", "text/calendar, text/plain;q=0.8, */*;q=0.5" } }, null, Ics.MaxBytes, 30000);
            if (r.Insecure) return SourceResult.Fail(Lang.T("Hanya tautan https"), "insecure", true);
            if (r.Revoked || r.Status == 400 || r.Status == 401 || r.Status == 403 || r.Status == 404 || r.Status == 410)
                return SourceResult.Fail(Lang.T("Tautan kalender tidak berlaku"), "http " + r.Status, true);
            if (r.TooBig) return SourceResult.Fail(Lang.T("Kalender lebih dari 5 MB"), "too big", true);
            if (!r.Ok) return SourceResult.Http(Lang.T("Kalender"), r);
            return Read(r.Data, r.Length, url, kind, name, key);
        }

        // Feed bytes into tasks and events
        public static SourceResult Read(byte[] data, int length, string url, string kind, string name, string key)
        {
            if (!Calendar(data, length)) return SourceResult.Fail(Lang.T("Tautan ini bukan kalender iCal"), "not ics", true);
            var today = DateTime.Now.Date;
            bool asTasks = kind == "tugas";
            var from = (asTasks ? today.AddDays(-14) : today.AddDays(-1)).ToUniversalTime();
            var to = today.AddDays(22).ToUniversalTime();
            var ics = Ics.Parse(data, length, from, to);
            var res = new SourceResult { Name = Clean(ics.Name, 60), Every = ics.Refresh, Tasks = new List<KuliaTask>(), Events = asTasks ? null : new List<ClassSlot>() };
            if (string.IsNullOrEmpty(name)) name = res.Name.Length > 0 ? res.Name : Provider(url);
            Convert(ics, res, asTasks, key, name, today);
            if (!asTasks && res.Tasks.Count == 0) res.Tasks = null;
            res.Extra = ics.Todos > 0;
            if (ics.Capped) Log.Write("source " + key + ": capped");
            return res;
        }

        static bool Calendar(byte[] data, int length)
        {
            int i = 0;
            if (length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
            while (i < length && (data[i] == 32 || data[i] == 9 || data[i] == 10 || data[i] == 13)) i++;
            const string head = "BEGIN:VCALENDAR";
            if (length - i < head.Length) return false;
            return string.Equals(Encoding.ASCII.GetString(data, i, head.Length), head, StringComparison.OrdinalIgnoreCase);
        }

        static void Convert(IcsResult ics, SourceResult res, bool asTasks, string key, string name, DateTime today)
        {
            var course = Clean(name, 60);
            var ids = new HashSet<string>();
            foreach (var it in ics.Items)
            {
                string title = Clean(it.Summary, 160), room = Clean(it.Location, 80), link = SafeLink(it.Url), meet = SafeLink(it.Meet);
                // Link as location: meeting link
                if (room.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || room.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    if (meet.Length == 0) meet = SafeLink(room);
                    room = "";
                }
                if (it.Todo || asTasks)
                {
                    if (it.Cancelled || res.Tasks.Count >= 300) continue;
                    string id = key + ":" + IdPart(it.Uid) + ":" + it.Start.Ticks;
                    // Hit regions key on ids
                    if (!ids.Add(id)) continue;
                    var due = it.AllDay ? EndOfDay(it.End > it.Start ? it.End.AddDays(-1) : it.Start) : it.Start;
                    res.Tasks.Add(new KuliaTask
                    {
                        Id = id, Title = title.Length > 0 ? title : Lang.T("Tanpa judul"), Course = course,
                        DeadlineUtc = DateTime.SpecifyKind(due, DateTimeKind.Utc), DateOnly = it.AllDay, ReadOnly = true, Source = key, Url = link,
                    });
                    continue;
                }
                if (res.Events.Count >= 1500) continue;
                var slot = new ClassSlot { Title = title.Length > 0 ? title : Lang.T("Sibuk"), Room = room, Url = meet.Length > 0 ? meet : link, Online = meet.Length > 0, Cancelled = it.Cancelled, Source = key };
                if (!it.AllDay)
                {
                    slot.Start = it.Start.ToLocalTime();
                    slot.End = it.End.ToLocalTime();
                    res.Events.Add(slot);
                    continue;
                }
                // One row per all-day date
                for (var d = it.Start.Date; d < it.End.Date; d = d.AddDays(1))
                {
                    if (d < today.AddDays(-1)) continue;
                    if (d > today.AddDays(22)) break;
                    res.Events.Add(new ClassSlot
                    {
                        Title = slot.Title, Room = slot.Room, Url = slot.Url, Online = slot.Online, Cancelled = slot.Cancelled, Source = key, AllDay = true,
                        Start = DateTime.SpecifyKind(d, DateTimeKind.Local), End = DateTime.SpecifyKind(d.AddDays(1), DateTimeKind.Local),
                    });
                }
            }
        }

        // UID as a short, single-line id part
        static string IdPart(string uid)
        {
            foreach (char c in uid) if (c < 0x21 || c > 0x7E) return Json.Sha256(uid).Substring(0, 32);
            return uid.Length > 100 ? Json.Sha256(uid).Substring(0, 32) : uid;
        }

        // Remembers the feed's own name and to-dos
        public override bool Absorb(SourceResult r)
        {
            bool changed = false, todos = r.Extra is bool && (bool)r.Extra;
            if (todos != Todos) { Todos = todos; changed = true; }
            if (!string.IsNullOrEmpty(r.Name) && r.Name != Name) { Name = r.Name; changed = true; }
            return changed;
        }
    }

    // Merges every source into UI lists
    partial class Island
    {
        enum DataState { Off, Loading, Failed, Empty, Ready }

        SourceHub sources;
        List<KuliaTask> kuliaTasks;
        List<ClassSlot> kuliaClasses;
        bool kuliaLoading;
        // 1: tasks refresh asked, 2: schedule
        int refreshAsked;
        string taskNote, eventNote;

        // Kulia or any source syncing
        bool tasksLoading { get { return kuliaLoading || (sources != null && sources.Busy); } }

        // Settings listens for status lines
        event Action SourcesChanged;

        bool KuliaOn { get { return kulia != null && kulia.SignedIn; } }
        bool OnlyKuliaTasks { get { return sources == null || !sources.HasTasks; } }
        bool OnlyKuliaEvents { get { return sources == null || !sources.HasEvents; } }

        void StartSources()
        {
            sources = new SourceHub(Post);
            sources.Changed += OnSourcesChanged;
            try { sources.Load(); }
            catch (Exception ex) { Log.Write("sources load: " + ex.GetType().Name); }
            sources.Tick(DateTime.UtcNow);
        }

        void OnSourcesChanged(bool data)
        {
            if (data) MergeSources();
            else if (refreshAsked != 0 && !tasksLoading) MergeSources();
            Kick();
            var h = SourcesChanged;
            if (h != null) h();
        }

        void MergeSources()
        {
            var all = kuliaTasks != null ? new List<KuliaTask>(kuliaTasks) : null;
            var slots = kuliaClasses != null ? new List<ClassSlot>(kuliaClasses) : null;
            var labels = new Dictionary<string, string> { { "kulia", "Kulia" } };
            int taskFrom = kuliaTasks != null && kuliaTasks.Count > 0 ? 1 : 0, eventFrom = kuliaClasses != null && kuliaClasses.Count > 0 ? 1 : 0;
            taskNote = KuliaOn && tasksError != null ? tasksError : null;
            eventNote = KuliaOn && kuliaClasses == null ? classesError ?? tasksError : null;
            if (sources != null)
            {
                bool noWrite = sources.Notion != null && sources.Notion.NoWrite;
                int count = sources.All().Count + (KuliaOn ? 1 : 0);
                foreach (var s in sources.All())
                {
                    labels[s.Key] = s.Name;
                    if (s.Tasks != null)
                    {
                        if (all == null) all = new List<KuliaTask>();
                        foreach (var t in s.Tasks)
                        {
                            if (noWrite && t.Source == "notion") t.ReadOnly = true;
                            all.Add(t);
                        }
                        if (s.Tasks.Count > 0) taskFrom++;
                    }
                    if (s.Events != null)
                    {
                        if (slots == null) slots = new List<ClassSlot>();
                        slots.AddRange(s.Events);
                        if (s.Events.Count > 0) eventFrom++;
                    }
                    if (s.Error == null) continue;
                    string note = count > 1 ? s.Name + ": " + s.Error : s.Error;
                    if (s.GivesTasks && taskNote == null) taskNote = note;
                    if (s.GivesEvents && eventNote == null) eventNote = note;
                }
            }
            if (all != null)
            {
                // Hit regions key on ids: keep one
                var ids = new HashSet<string>();
                all.RemoveAll(t => t.Closed || !ids.Add(t.Id ?? ""));
                all.Sort((a, b) => a.DeadlineUtc.CompareTo(b.DeadlineUtc));
                if (all.Count > 300) all.RemoveRange(300, all.Count - 300);
            }
            if (slots != null)
            {
                var now = DateTime.Now;
                slots.RemoveAll(c => c.End < now);
                slots.Sort(SlotOrder);
                if (slots.Count > 1500) slots.RemoveRange(1500, slots.Count - 1500);
            }
            tasks = all;
            classes = slots;
            SourceHub.SetLabels(labels, taskFrom > 1, eventFrom > 1);
            if (refreshAsked != 0 && !tasksLoading)
            {
                if (refreshAsked == 1 && tasks != null) ShowInfo(Ink.IcoRefresh, Lang.T("Tugas diperbarui"), tasks.Count + Lang.T(" aktif"));
                else if (refreshAsked == 2 && classes != null) ShowInfo(Ink.IcoRefresh, Lang.T("Jadwal diperbarui"), classes.Count == 1 ? Lang.T("1 acara") : Lang.F("{0} acara", classes.Count));
                refreshAsked = 0;
            }
        }

        // Day first, all-day on top, then time
        static int SlotOrder(ClassSlot a, ClassSlot b)
        {
            int d = a.Start.Date.CompareTo(b.Start.Date);
            if (d != 0) return d;
            if (a.AllDay != b.AllDay) return a.AllDay ? -1 : 1;
            return a.Start.CompareTo(b.Start);
        }

        // Room for a partial-failure line
        float TaskFoot() { return taskNote != null && tasks != null && tasks.Count > 0 ? 22 : 0; }
        float EventFoot() { return eventNote != null && classes != null && classes.Count > 0 ? 20 : 0; }

        DataState TaskState()
        {
            if (!KuliaOn && OnlyKuliaTasks) return DataState.Off;
            if (tasks != null) return tasks.Count > 0 ? DataState.Ready : DataState.Empty;
            if (tasksLoading || (sources != null && sources.Waiting(true)) || (KuliaOn && tasksError == null)) return DataState.Loading;
            return taskNote != null ? DataState.Failed : DataState.Loading;
        }

        DataState ScheduleState()
        {
            if (!KuliaOn && OnlyKuliaEvents) return DataState.Off;
            if (classes != null) return classes.Count > 0 ? DataState.Ready : DataState.Empty;
            if (tasksLoading || (sources != null && sources.Waiting(false)) || (KuliaOn && tasksError == null && classesError == null)) return DataState.Loading;
            return eventNote != null ? DataState.Failed : DataState.Loading;
        }

        // Kulia and every connected source
        void RefreshAll(int what)
        {
            refreshAsked = what;
            if (KuliaOn) RefreshTasks(false);
            int queued = sources != null ? sources.Refresh() : 0;
            if (!KuliaOn && queued == 0) refreshAsked = 0;
            Kick();
        }

        void SyncSourcesTick(DateTime now)
        {
            if (sources != null) sources.Tick(now);
        }

        // Nothing linked: one way to Settings
        void NothingConnected(float w, string id, bool forTasks)
        {
            bool any = sources != null && sources.Any;
            string title = !any ? Lang.T("Belum ada yang tersambung") : forTasks ? Lang.T("Belum ada sumber tugas") : Lang.T("Belum ada sumber jadwal");
            string detail = !any ? Lang.T("Kulia, Notion, Todoist, atau kalender iCal") : forTasks ? Lang.T("Kulia, Notion, Todoist, atau kalender tugas") : Lang.T("Kulia, atau kalender Google, Outlook, iCloud");
            float by = Empty(w, Ink.IcoCalendar, title, detail);
            Button(new RectangleF(w / 2 - 80, by, 160, 34), id, Lang.T("Buka Integrasi"), true, delegate { OpenIntegrations(); });
        }

        void OpenIntegrations()
        {
            expanded = false;
            Kick();
            if (prefs == null || prefs.IsDisposed) prefs = new PrefsWindow(this);
            prefs.ShowSection("integrasi");
            if (!prefs.Visible) prefs.Show();
            else prefs.Activate();
        }

        void CloseExternal(KuliaTask task)
        {
            string old = task.Status;
            task.Status = "selesai";
            if (tasks != null) tasks.Remove(task);
            Kick();
            sources.Close(task, err =>
            {
                if (err == null) { ShowInfo('\0', Lang.T("Ditandai selesai"), task.Title, true); return; }
                task.Status = old;
                MergeSources();
                ShowInfo(Ink.IcoLock, err, task.Title, true);
            });
        }

        // Date-only tasks show no time
        static string Relative(KuliaTask t, DateTime nowUtc)
        {
            if (!t.DateOnly) return Relative(t.DeadlineUtc, nowUtc);
            var day = t.DeadlineUtc.ToLocalTime().Date;
            var today = DateTime.Now.Date;
            if (day < today) return Lang.F("lewat {0}", (int)(today - day).TotalDays + Lang.T(" hari"));
            if (day == today) return Lang.T("hari ini");
            if (day == today.AddDays(1)) return Lang.T("besok");
            if ((day - today).TotalDays < 7) return day.ToString("dddd", Fmt.Id);
            return day.ToString("d MMM", Fmt.Id);
        }
    }
}
