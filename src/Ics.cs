using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Tepi
{
    // One occurrence or to-do from a feed
    class IcsItem
    {
        public string Uid = "", Summary = "", Location = "", Url = "", Meet = "";
        public bool Todo, AllDay, Cancelled;
        // Timed: UTC; all-day: dates, end exclusive
        public DateTime Start, End;
    }

    class IcsResult
    {
        public string Name = "";
        public TimeSpan Refresh;
        public readonly List<IcsItem> Items = new List<IcsItem>();
        public int Events, Todos, Complex, Broken, UnknownZones;
        public bool Capped;
    }

    // iCalendar reader with a recurrence subset
    static class Ics
    {
        public const int MaxBytes = 5 * 1024 * 1024;
        const int MaxLine = 16 * 1024, MaxComponents = 20000, MaxProps = 400, MaxDepth = 8, MaxItems = 4000, MaxPerEvent = 800, Steps = 400000;
        const int MaxAllProps = 150000, MaxOther = 4000, MaxZones = 64, MaxDates = 4000, MaxRuleParts = 100, MaxMs = 3000;

        // Only properties the reader uses
        static readonly HashSet<string> Kept = new HashSet<string>(StringComparer.Ordinal)
        {
            "X-WR-CALNAME", "REFRESH-INTERVAL", "X-PUBLISHED-TTL", "TZID", "TZOFFSETFROM", "TZOFFSETTO", "UID", "RECURRENCE-ID", "DTSTART", "DTEND", "DURATION", "DUE",
            "RRULE", "RDATE", "EXDATE", "STATUS", "COMPLETED", "PERCENT-COMPLETE", "SUMMARY", "LOCATION", "URL", "X-GOOGLE-CONFERENCE", "X-MICROSOFT-SKYPETEAMSMEETINGURL",
        };

        class Prop
        {
            public string Name, Value, Tzid, Type;
        }

        class Comp
        {
            public string Name;
            public Comp Parent;
            public readonly List<Prop> Props = new List<Prop>();
            public readonly List<Comp> Kids = new List<Comp>();

            public Prop Get(string name)
            {
                foreach (var p in Props) if (p.Name == name) return p;
                return null;
            }

            public string Text(string name)
            {
                var p = Get(name);
                return p == null ? "" : Unescape(p.Value).Trim();
            }
        }

        class Budget
        {
            public int Left = Steps;
            public bool Hit;
        }

        class Stamp
        {
            public DateTime Wall;
            public bool Date, Utc;
            public Zone Zone;
        }

        public static IcsResult Parse(byte[] data, int length, DateTime fromUtc, DateTime toUtc)
        {
            var res = new IcsResult();
            // Last line of defence: wall-clock cap
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reader = new Reader(res);
            reader.Read(data, Math.Min(length, MaxBytes));
            var ctx = new Ctx(res);
            var events = new List<Comp>();
            var todos = new List<Comp>();
            foreach (var cal in reader.Root.Kids)
            {
                if (cal.Name != "VCALENDAR") continue;
                if (res.Name.Length == 0) res.Name = cal.Text("X-WR-CALNAME");
                var ri = cal.Get("REFRESH-INTERVAL") ?? cal.Get("X-PUBLISHED-TTL");
                TimeSpan every;
                if (ri != null && Duration(ri.Value, out every) && every > res.Refresh) res.Refresh = every;
                foreach (var k in cal.Kids)
                {
                    if (k.Name == "VTIMEZONE")
                    {
                        string id = k.Text("TZID");
                        if (id.Length > 0 && !ctx.Vtz.ContainsKey(id)) ctx.Vtz[id] = k;
                    }
                    else if (k.Name == "VEVENT") events.Add(k);
                    else if (k.Name == "VTODO") todos.Add(k);
                }
            }
            res.Events = events.Count;
            res.Todos = todos.Count;
            // Overrides may come before their master
            var masters = new List<Comp>();
            var overrides = new Dictionary<string, List<Comp>>(StringComparer.Ordinal);
            foreach (var ev in events)
            {
                string uid = ev.Text("UID");
                if (uid.Length > 0 && ev.Get("RECURRENCE-ID") != null)
                {
                    List<Comp> list;
                    if (!overrides.TryGetValue(uid, out list)) overrides[uid] = list = new List<Comp>();
                    list.Add(ev);
                }
                else masters.Add(ev);
            }
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in masters)
            {
                if (res.Items.Count >= MaxItems || clock.ElapsedMilliseconds > MaxMs) { res.Capped = true; break; }
                string uid = m.Text("UID");
                List<Comp> ov = null;
                if (uid.Length > 0 && used.Add(uid)) overrides.TryGetValue(uid, out ov);
                Event(m, ov, false, ctx, fromUtc, toUtc);
            }
            foreach (var kv in overrides)
            {
                if (clock.ElapsedMilliseconds > MaxMs) { res.Capped = true; break; }
                if (!used.Contains(kv.Key))
                    foreach (var o in kv.Value) Event(o, null, true, ctx, fromUtc, toUtc);
            }
            foreach (var t in todos)
            {
                if (res.Items.Count >= MaxItems || clock.ElapsedMilliseconds > MaxMs) { res.Capped = true; break; }
                Todo(t, ctx, fromUtc, toUtc);
            }
            if (ctx.Budget.Hit) res.Capped = true;
            return res;
        }

        // Unfolds on bytes, then decodes
        class Reader
        {
            public readonly Comp Root = new Comp { Name = "" };
            readonly IcsResult res;
            readonly byte[] buf = new byte[MaxLine];
            Comp cur;
            int len, depth, skip, items, others, props;
            bool over, have;

            public Reader(IcsResult res) { this.res = res; cur = Root; }

            public void Read(byte[] data, int length)
            {
                int pos = 0;
                if (length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) pos = 3;
                while (pos < length)
                {
                    int s = pos;
                    while (pos < length && data[pos] != 10 && data[pos] != 13) pos++;
                    int e = pos;
                    if (pos < length) pos += data[pos] == 13 && pos + 1 < length && data[pos + 1] == 10 ? 2 : 1;
                    if (have && e > s && (data[s] == 32 || data[s] == 9)) { Append(data, s + 1, e - s - 1); continue; }
                    Flush();
                    have = true;
                    Append(data, s, e - s);
                }
                Flush();
            }

            void Append(byte[] data, int at, int n)
            {
                if (over || n <= 0) return;
                if (len + n > MaxLine) { over = true; return; }
                Buffer.BlockCopy(data, at, buf, len, n);
                len += n;
            }

            void Flush()
            {
                if (have)
                {
                    if (over) res.Broken++;
                    else if (len > 0) Line(Encoding.UTF8.GetString(buf, 0, len));
                }
                have = over = false;
                len = 0;
            }

            void Line(string line)
            {
                var p = ParseLine(line);
                if (p == null) { if (line.Trim().Length > 0) res.Broken++; return; }
                if (p.Name == "BEGIN")
                {
                    string what = p.Value.Trim().ToUpperInvariant();
                    bool item = what == "VEVENT" || what == "VTODO", full = item ? items >= MaxComponents : others >= MaxOther;
                    if (skip > 0 || depth >= MaxDepth || !Wanted(what) || full)
                    {
                        if (full && skip == 0 && Wanted(what)) res.Capped = true;
                        skip++;
                        return;
                    }
                    if (item) items++;
                    else others++;
                    var c = new Comp { Name = what, Parent = cur };
                    cur.Kids.Add(c);
                    cur = c;
                    depth++;
                    return;
                }
                if (p.Name == "END")
                {
                    if (skip > 0) { skip--; return; }
                    string what = p.Value.Trim().ToUpperInvariant();
                    for (var c = cur; c != null && c != Root; c = c.Parent)
                    {
                        depth--;
                        if (c.Name != what) continue;
                        cur = c.Parent;
                        return;
                    }
                    depth = 0;
                    for (var c = cur; c != null && c != Root; c = c.Parent) depth++;
                    return;
                }
                if (skip > 0 || cur == Root || cur.Props.Count >= MaxProps || !Kept.Contains(p.Name)) return;
                if (++props > MaxAllProps) { res.Capped = true; return; }
                cur.Props.Add(p);
            }

            static bool Wanted(string name)
            {
                return name == "VCALENDAR" || name == "VEVENT" || name == "VTODO" || name == "VTIMEZONE" || name == "STANDARD" || name == "DAYLIGHT";
            }
        }

        // name *(;param=value) : value, quotes respected
        static Prop ParseLine(string line)
        {
            int n = line.Length, i = 0;
            while (i < n && line[i] != ';' && line[i] != ':') i++;
            if (i == 0 || i >= n) return null;
            var p = new Prop { Name = line.Substring(0, i).Trim().ToUpperInvariant() };
            int dot = p.Name.IndexOf('.');
            if (dot >= 0) p.Name = p.Name.Substring(dot + 1);
            while (i < n && line[i] == ';')
            {
                i++;
                int ks = i;
                while (i < n && line[i] != '=' && line[i] != ';' && line[i] != ':') i++;
                string key = line.Substring(ks, i - ks).Trim().ToUpperInvariant();
                if (i >= n || line[i] != '=') continue;
                i++;
                string first = null;
                while (true)
                {
                    string v;
                    if (i < n && line[i] == '"')
                    {
                        int q = line.IndexOf('"', i + 1);
                        if (q < 0) return null;
                        v = line.Substring(i + 1, q - i - 1);
                        i = q + 1;
                    }
                    else
                    {
                        int vs = i;
                        while (i < n && line[i] != ';' && line[i] != ':' && line[i] != ',') i++;
                        v = line.Substring(vs, i - vs);
                    }
                    if (first == null) first = v;
                    if (i < n && line[i] == ',') { i++; continue; }
                    break;
                }
                first = Caret(first);
                if (key == "TZID" && p.Tzid == null) p.Tzid = first;
                else if (key == "VALUE" && p.Type == null) p.Type = first.Trim().ToUpperInvariant();
            }
            if (i >= n || line[i] != ':') return null;
            p.Value = line.Substring(i + 1);
            return p;
        }

        // RFC 6868: ^n ^^ ^'
        static string Caret(string v)
        {
            if (v.IndexOf('^') < 0) return v;
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '^' && i + 1 < v.Length)
                {
                    char k = v[i + 1];
                    if (k == 'n') { sb.Append('\n'); i++; continue; }
                    if (k == '^') { sb.Append('^'); i++; continue; }
                    if (k == '\'') { sb.Append('"'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Unescape(string v)
        {
            if (v.IndexOf('\\') < 0) return v;
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '\\' && i + 1 < v.Length)
                {
                    char k = v[++i];
                    sb.Append(k == 'n' || k == 'N' ? '\n' : k);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // P[nW][nD][T[nH][nM][nS]], signed
        static bool Duration(string text, out TimeSpan d)
        {
            d = TimeSpan.Zero;
            string v = text.Trim().ToUpperInvariant();
            int i = 0;
            bool neg = false, time = false;
            if (i < v.Length && (v[i] == '+' || v[i] == '-')) { neg = v[i] == '-'; i++; }
            if (i >= v.Length || v[i] != 'P') return false;
            long num = -1;
            double secs = 0;
            for (i++; i < v.Length; i++)
            {
                char c = v[i];
                if (c == 'T') { if (num >= 0) return false; time = true; continue; }
                if (c >= '0' && c <= '9')
                {
                    num = (num < 0 ? 0 : num) * 10 + (c - '0');
                    if (num > 100000000) return false;
                    continue;
                }
                if (num < 0) return false;
                if (c == 'W' && !time) secs += num * 604800.0;
                else if (c == 'D' && !time) secs += num * 86400.0;
                else if (c == 'H' && time) secs += num * 3600.0;
                else if (c == 'M' && time) secs += num * 60.0;
                else if (c == 'S' && time) secs += num;
                else return false;
                num = -1;
            }
            if (num >= 0) return false;
            secs = Math.Min(secs, 400 * 86400.0);
            d = TimeSpan.FromSeconds(neg ? -secs : secs);
            return true;
        }

        // DATE or DATE-TIME; lenient about separators
        static bool ParseStamp(string v, string type, out DateTime wall, out bool date, out bool utc)
        {
            wall = DateTime.MinValue;
            date = utc = false;
            var sb = new StringBuilder(20);
            foreach (char c in v.Trim()) if (c != '-' && c != ':') sb.Append(c);
            string s = sb.ToString();
            if (s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) { utc = true; s = s.Substring(0, s.Length - 1); }
            int t = s.IndexOfAny(new[] { 'T', 't' });
            string d = t < 0 ? s : s.Substring(0, t);
            if (d.Length != 8 || !Digits(d)) return false;
            int y = Num(d, 0, 4), mo = Num(d, 4, 2), da = Num(d, 6, 2);
            if (y < 1600 || y > 9000 || mo < 1 || mo > 12 || da < 1 || da > DateTime.DaysInMonth(y, mo)) return false;
            if (type == "DATE" || t < 0)
            {
                date = true;
                utc = false;
                wall = new DateTime(y, mo, da);
                return true;
            }
            string tm = s.Substring(t + 1);
            int frac = tm.IndexOf('.');
            if (frac >= 0) tm = tm.Substring(0, frac);
            if ((tm.Length != 6 && tm.Length != 4) || !Digits(tm)) return false;
            int hh = Num(tm, 0, 2), mm = Num(tm, 2, 2), ss = tm.Length == 6 ? Num(tm, 4, 2) : 0;
            if (hh > 23 || mm > 59 || ss > 60) return false;
            wall = new DateTime(y, mo, da, hh, mm, Math.Min(ss, 59));
            return true;
        }

        static bool Digits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        static int Num(string s, int at, int n)
        {
            int v = 0;
            for (int i = at; i < at + n; i++) v = v * 10 + (s[i] - '0');
            return v;
        }

        // Feed context: time zones, budget
        class Ctx
        {
            public readonly IcsResult Res;
            public readonly Budget Budget = new Budget();
            public readonly Dictionary<string, Comp> Vtz = new Dictionary<string, Comp>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, Zone> cache = new Dictionary<string, Zone>(StringComparer.OrdinalIgnoreCase);

            public Ctx(IcsResult res) { Res = res; }

            public Stamp Time(Prop p) { return p == null ? null : Time(p.Value, p.Type, p.Tzid); }

            public Stamp Time(string v, string type, string tzid)
            {
                DateTime wall;
                bool date, utc;
                if (!ParseStamp(v, type, out wall, out date, out utc)) return null;
                var t = new Stamp { Wall = wall, Date = date, Utc = utc };
                if (!date && !utc) t.Zone = string.IsNullOrEmpty(tzid) ? Local : Resolve(tzid);
                return t;
            }

            public DateTime Utc(Stamp t)
            {
                if (t.Utc) return DateTime.SpecifyKind(t.Wall, DateTimeKind.Utc);
                return (t.Zone ?? Local).ToUtc(t.Wall);
            }

            Zone Resolve(string tzid)
            {
                string id = tzid.Trim().TrimStart('/');
                Zone z;
                if (cache.TryGetValue(id, out z)) return z;
                // Lookups are costly; real feeds use few
                if (cache.Count >= MaxZones) { Res.UnknownZones++; return Local; }
                z = Find(id);
                if (z == null) { Res.UnknownZones++; z = Local; }
                cache[id] = z;
                return z;
            }

            // Windows id, ICU, VTIMEZONE, name, offset
            Zone Find(string id)
            {
                if (id.Length == 0 || id.Length > 120) return null;
                FeedZone feed = null;
                Comp vtz;
                if (Vtz.TryGetValue(id, out vtz))
                {
                    feed = new FeedZone(vtz, this);
                    if (!feed.Valid) feed = null;
                }
                var sys = Sys(id);
                if (sys != null) return sys;
                string win = WindowsId(id);
                if (win != null && (sys = Sys(win)) != null) return feed != null && !Agree(sys, feed) ? (Zone)feed : sys;
                if (feed != null) return feed;
                foreach (var tz in SystemZones())
                    if (string.Equals(tz.DisplayName, id, StringComparison.OrdinalIgnoreCase)) return new SysZone(tz);
                TimeSpan off;
                if (UtcPrefix(id, out off)) return new FixedZone(off);
                return null;
            }

            // Stale ICU data loses to the feed
            static bool Agree(Zone a, Zone b)
            {
                var now = DateTime.UtcNow;
                for (int i = 0; i < 3; i++)
                {
                    var at = now.AddDays(i * 122);
                    if (a.Offset(at) != b.Offset(at)) return false;
                }
                return true;
            }
        }

        static readonly Zone Local = new SysZone(TimeZoneInfo.Local);
        static ReadOnlyCollection<TimeZoneInfo> systemZones;

        static ReadOnlyCollection<TimeZoneInfo> SystemZones()
        {
            if (systemZones == null)
            {
                try { systemZones = TimeZoneInfo.GetSystemTimeZones(); }
                catch (Exception) { systemZones = new ReadOnlyCollection<TimeZoneInfo>(new List<TimeZoneInfo>()); }
            }
            return systemZones;
        }

        static SysZone Sys(string id)
        {
            // Backslash would open a registry subkey
            if (id.IndexOf('\0') >= 0 || id.IndexOf('\\') >= 0) return null;
            try { return new SysZone(TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (Exception) { return null; }
        }

        static bool UtcPrefix(string id, out TimeSpan off)
        {
            off = TimeSpan.Zero;
            var m = Regex.Match(id, @"^\(?(?:UTC|GMT)\s*([+-])(\d{1,2})(?::?(\d{2}))?\)?");
            if (!m.Success) return Regex.IsMatch(id, @"^\(?(?:UTC|GMT)\)?(\s|$)");
            int h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int mi = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            if (h > 14 || mi > 59) return false;
            off = new TimeSpan(h, mi, 0);
            if (m.Groups[1].Value == "-") off = -off;
            return true;
        }

        [DllImport("icu.dll", EntryPoint = "ucal_getWindowsTimeZoneID", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int ucal_getWindowsTimeZoneID(string id, int len, [Out] char[] winid, int winidCapacity, ref int status);

        static bool icuGone;

        // IANA to Windows id, or null
        public static string WindowsId(string iana)
        {
            if (icuGone || iana.Length == 0 || iana.Length > 64 || iana.IndexOf(' ') >= 0) return null;
            try
            {
                var buf = new char[128];
                int status = 0;
                int n = ucal_getWindowsTimeZoneID(iana, iana.Length, buf, buf.Length, ref status);
                return status > 0 || n <= 0 || n > buf.Length ? null : new string(buf, 0, n);
            }
            catch (DllNotFoundException) { icuGone = true; }
            catch (EntryPointNotFoundException) { icuGone = true; }
            catch (BadImageFormatException) { icuGone = true; }
            return null;
        }

        abstract class Zone
        {
            public abstract DateTime ToUtc(DateTime wall);
            public abstract TimeSpan Offset(DateTime utc);
            public DateTime ToWall(DateTime utc) { return DateTime.SpecifyKind(utc + Offset(utc), DateTimeKind.Unspecified); }
        }

        sealed class SysZone : Zone
        {
            readonly TimeZoneInfo tz;
            public SysZone(TimeZoneInfo tz) { this.tz = tz; }

            public override TimeSpan Offset(DateTime utc)
            {
                try { return tz.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)); }
                catch (ArgumentException) { return tz.BaseUtcOffset; }
            }

            public override DateTime ToUtc(DateTime wall)
            {
                var w = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
                try
                {
                    // Gap: the offset before it (RFC 5545)
                    if (tz.IsInvalidTime(w)) return DateTime.SpecifyKind(w - tz.GetUtcOffset(w.AddHours(-6)), DateTimeKind.Utc);
                    // Overlap: the first occurrence
                    if (tz.IsAmbiguousTime(w))
                    {
                        var max = TimeSpan.MinValue;
                        foreach (var o in tz.GetAmbiguousTimeOffsets(w)) if (o > max) max = o;
                        return DateTime.SpecifyKind(w - max, DateTimeKind.Utc);
                    }
                    return DateTime.SpecifyKind(w - tz.GetUtcOffset(w), DateTimeKind.Utc);
                }
                catch (ArgumentException) { return DateTime.SpecifyKind(w - tz.BaseUtcOffset, DateTimeKind.Utc); }
            }
        }

        sealed class FixedZone : Zone
        {
            readonly TimeSpan off;
            public FixedZone(TimeSpan off) { this.off = off; }
            public override TimeSpan Offset(DateTime utc) { return off; }
            public override DateTime ToUtc(DateTime wall) { return DateTime.SpecifyKind(wall - off, DateTimeKind.Utc); }
        }

        // Offsets from the feed's own VTIMEZONE
        sealed class FeedZone : Zone
        {
            class Obs
            {
                public DateTime Start;
                public TimeSpan From, To;
                public Rule Rule;
                public Stamp Until;
            }

            // Yearly rules, expanded per year on demand
            readonly List<Obs> rules = new List<Obs>();
            // One-off onsets and RDATEs, sorted once
            readonly List<KeyValuePair<DateTime, TimeSpan>> fixedOnsets = new List<KeyValuePair<DateTime, TimeSpan>>();
            readonly Dictionary<int, List<KeyValuePair<DateTime, TimeSpan>>> years = new Dictionary<int, List<KeyValuePair<DateTime, TimeSpan>>>();
            TimeSpan first;
            bool valid;

            public bool Valid { get { return valid; } }

            public FeedZone(Comp vtz, Ctx ctx)
            {
                var earliest = DateTime.MaxValue;
                foreach (var k in vtz.Kids)
                {
                    if (k.Name != "STANDARD" && k.Name != "DAYLIGHT") continue;
                    DateTime start;
                    bool date, utc;
                    TimeSpan from, to;
                    var sp = k.Get("DTSTART");
                    if (sp == null || !ParseStamp(sp.Value, null, out start, out date, out utc)) continue;
                    if (!Offset(k.Text("TZOFFSETFROM"), out from) || !Offset(k.Text("TZOFFSETTO"), out to)) continue;
                    valid = true;
                    if (start < earliest) { earliest = start; first = from; }
                    var rr = k.Get("RRULE");
                    var rule = rr != null ? Rule.Parse(rr.Value) : null;
                    // Real zones change at most yearly
                    if (rule != null && !rule.Complex && rule.Freq == 4 && rules.Count < 24)
                    {
                        var o = new Obs { Start = start, From = from, To = to, Rule = rule };
                        if (rule.Until != null) o.Until = ctx.Time(rule.Until, null, null);
                        rules.Add(o);
                    }
                    else if (fixedOnsets.Count < 2000) fixedOnsets.Add(new KeyValuePair<DateTime, TimeSpan>(start - from, to));
                    foreach (var p in k.Props)
                    {
                        if (p.Name != "RDATE") continue;
                        foreach (var raw in p.Value.Split(','))
                        {
                            DateTime d;
                            if (fixedOnsets.Count < 2000 && ParseStamp(raw, p.Type, out d, out date, out utc)) fixedOnsets.Add(new KeyValuePair<DateTime, TimeSpan>(d - from, to));
                        }
                    }
                }
                fixedOnsets.Sort((a, b) => a.Key.CompareTo(b.Key));
            }

            static bool Offset(string v, out TimeSpan off)
            {
                off = TimeSpan.Zero;
                var m = Regex.Match(v, @"^([+-])(\d{2})(\d{2})(\d{2})?$");
                if (!m.Success) return false;
                int h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), mi = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                int s = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
                if (h > 23 || mi > 59 || s > 59) return false;
                off = new TimeSpan(h, mi, s);
                if (m.Groups[1].Value == "-") off = -off;
                return true;
            }

            // Rule onsets in UTC around a year
            List<KeyValuePair<DateTime, TimeSpan>> Onsets(int year)
            {
                List<KeyValuePair<DateTime, TimeSpan>> list;
                if (years.TryGetValue(year, out list)) return list;
                if (years.Count >= 16) years.Clear();
                list = new List<KeyValuePair<DateTime, TimeSpan>>();
                var from = new DateTime(Math.Max(1601, year - 1), 1, 1);
                var to = new DateTime(Math.Min(9000, year + 1), 12, 31, 23, 59, 59);
                // Own budget: zones never starve events
                var budget = new Budget { Left = 500 };
                foreach (var o in rules)
                {
                    var ob = o;
                    Func<DateTime, bool> past = null;
                    if (o.Until != null)
                    {
                        var limit = o.Until.Utc ? o.Until.Wall : o.Until.Wall - ob.From;
                        past = w => w - ob.From > limit;
                    }
                    int n = 0;
                    foreach (var w in Occurrences(o.Rule, o.Start, from, to, past, budget))
                        if (++n <= 8) list.Add(new KeyValuePair<DateTime, TimeSpan>(w - o.From, o.To));
                }
                list.Sort((a, b) => a.Key.CompareTo(b.Key));
                years[year] = list;
                return list;
            }

            public override TimeSpan Offset(DateTime utc)
            {
                var off = first;
                var at = DateTime.MinValue;
                int i = Last(fixedOnsets, utc);
                if (i >= 0) { at = fixedOnsets[i].Key; off = fixedOnsets[i].Value; }
                var list = Onsets(utc.Year);
                int j = Last(list, utc);
                if (j >= 0 && list[j].Key >= at) off = list[j].Value;
                return off;
            }

            // Last onset at or before t
            static int Last(List<KeyValuePair<DateTime, TimeSpan>> list, DateTime t)
            {
                int lo = 0, hi = list.Count - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (list[mid].Key <= t) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                return found;
            }

            public override DateTime ToUtc(DateTime wall)
            {
                var guess = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
                var set = new List<TimeSpan> { Offset(guess.AddHours(-26)), Offset(guess), Offset(guess.AddHours(26)) };
                bool found = false;
                TimeSpan best = TimeSpan.Zero, low = set[0];
                foreach (var o in set)
                {
                    if (o < low) low = o;
                    if (Offset(guess - o) != o) continue;
                    // Overlap: the earlier instant
                    if (!found || o > best) best = o;
                    found = true;
                }
                return DateTime.SpecifyKind(guess - (found ? best : low), DateTimeKind.Utc);
            }
        }

        class Rule
        {
            // DowMask: weekdays in BYDAY, one bit each
            public int Freq, Interval = 1, Count = -1, DowMask;
            public string Until;
            public List<int> ByMonth, ByMonthDay, BySetPos;
            public List<int[]> ByDay;
            public DayOfWeek Wkst = DayOfWeek.Monday;
            public bool Complex;

            public static Rule Parse(string text)
            {
                var r = new Rule();
                foreach (var part in text.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = part.Substring(0, eq).Trim().ToUpperInvariant(), v = part.Substring(eq + 1).Trim().ToUpperInvariant();
                    int n;
                    switch (k)
                    {
                        case "FREQ": r.Freq = v == "DAILY" ? 1 : v == "WEEKLY" ? 2 : v == "MONTHLY" ? 3 : v == "YEARLY" ? 4 : -1; break;
                        case "INTERVAL":
                            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) r.Interval = Math.Min(n, 1000);
                            else r.Complex = true;
                            break;
                        case "COUNT":
                            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) r.Count = Math.Min(n, 1000000);
                            else r.Complex = true;
                            break;
                        case "UNTIL": r.Until = v; break;
                        case "BYMONTH": r.ByMonth = Ints(v, 1, 12, false, r); break;
                        case "BYMONTHDAY": r.ByMonthDay = Ints(v, 1, 31, true, r); break;
                        case "BYSETPOS": r.BySetPos = Ints(v, 1, 366, true, r); break;
                        case "BYDAY": r.ByDay = Days(v, r); break;
                        case "WKST":
                            int d = Dow(v);
                            if (d >= 0) r.Wkst = (DayOfWeek)d;
                            break;
                        case "BYWEEKNO": case "BYYEARDAY": case "BYHOUR": case "BYMINUTE": case "BYSECOND": case "RSCALE": case "SKIP":
                            r.Complex = true;
                            break;
                    }
                }
                if (r.Freq <= 0 || (r.Count > 0 && r.Until != null)) r.Complex = true;
                return r;
            }

            static List<int> Ints(string v, int min, int max, bool negative, Rule r)
            {
                var list = new List<int>();
                var parts = v.Split(',');
                if (parts.Length > MaxRuleParts) { r.Complex = true; return list; }
                foreach (var part in parts)
                {
                    int n;
                    if (!int.TryParse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) { r.Complex = true; continue; }
                    int a = Math.Abs(n);
                    if (a < min || a > max || (n < 0 && !negative)) { r.Complex = true; continue; }
                    if (!list.Contains(n)) list.Add(n);
                }
                if (list.Count == 0) r.Complex = true;
                return list;
            }

            static List<int[]> Days(string v, Rule r)
            {
                var list = new List<int[]>();
                var parts = v.Split(',');
                if (parts.Length > MaxRuleParts) { r.Complex = true; return list; }
                foreach (var raw in parts)
                {
                    string s = raw.Trim();
                    int dow = s.Length >= 2 ? Dow(s.Substring(s.Length - 2)) : -1;
                    string num = s.Length >= 2 ? s.Substring(0, s.Length - 2) : "";
                    int ord = 0;
                    if (dow < 0 || (num.Length > 0 && (!int.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ord) || ord == 0 || Math.Abs(ord) > 53)))
                    {
                        r.Complex = true;
                        continue;
                    }
                    r.DowMask |= 1 << dow;
                    if (!list.Exists(x => x[0] == ord && x[1] == dow)) list.Add(new[] { ord, dow });
                }
                if (list.Count == 0) r.Complex = true;
                return list;
            }
        }

        static int Dow(string s)
        {
            switch (s)
            {
                case "SU": return 0;
                case "MO": return 1;
                case "TU": return 2;
                case "WE": return 3;
                case "TH": return 4;
                case "FR": return 5;
                case "SA": return 6;
            }
            return -1;
        }

        // Starts in [from, to], DTSTART first
        static List<DateTime> Occurrences(Rule r, DateTime s, DateTime from, DateTime to, Func<DateTime, bool> past, Budget b)
        {
            var list = new List<DateTime>();
            if (s >= from && s <= to) list.Add(s);
            if (r.Count == 1 || (past != null && past(s))) return list;
            int count = 1;
            long k = 0;
            // No COUNT: jump close to the window
            if (r.Count < 0 && from > s) k = Math.Max(0, Periods(r, s, from) / r.Interval - 1);
            var cands = new List<DateTime>();
            for (; ; k++)
            {
                if (--b.Left <= 0) { b.Hit = true; break; }
                DateTime p;
                if (!PeriodStart(r, s, k, out p) || p > to) break;
                cands.Clear();
                Candidates(r, s, p, cands);
                if (cands.Count == 0) continue;
                b.Left -= cands.Count;
                cands.Sort();
                Dedupe(cands);
                if (r.BySetPos != null) SetPos(cands, r.BySetPos);
                foreach (var c in cands)
                {
                    if (c <= s) continue;
                    if (c > to || (past != null && past(c))) return list;
                    if (r.Count > 0 && ++count > r.Count) return list;
                    if (c < from) continue;
                    list.Add(c);
                    if (list.Count >= MaxPerEvent) { b.Hit = true; return list; }
                }
            }
            return list;
        }

        static DateTime WeekStart(DateTime d, DayOfWeek wkst)
        {
            return d.Date.AddDays(-(((int)d.DayOfWeek - (int)wkst + 7) % 7));
        }

        static long Periods(Rule r, DateTime s, DateTime w)
        {
            switch (r.Freq)
            {
                case 1: return (long)(w.Date - s.Date).TotalDays;
                case 2: return (long)(WeekStart(w, r.Wkst) - WeekStart(s, r.Wkst)).TotalDays / 7;
                case 3: return (w.Year - s.Year) * 12L + w.Month - s.Month;
                default: return w.Year - s.Year;
            }
        }

        static bool PeriodStart(Rule r, DateTime s, long k, out DateTime p)
        {
            p = DateTime.MinValue;
            long n = k * r.Interval;
            try
            {
                switch (r.Freq)
                {
                    case 1: if (n > 2500000) return false; p = s.Date.AddDays(n); return true;
                    case 2: if (n > 400000) return false; p = WeekStart(s, r.Wkst).AddDays(7 * n); return true;
                    case 3: if (n > 90000) return false; p = new DateTime(s.Year, s.Month, 1).AddMonths((int)n); return true;
                    default: if (n > 7000) return false; p = new DateTime(s.Year, 1, 1).AddYears((int)n); return true;
                }
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        static bool HasDow(Rule r, DayOfWeek d) { return (r.DowMask & (1 << (int)d)) != 0; }

        static void Candidates(Rule r, DateTime s, DateTime p, List<DateTime> c)
        {
            var tod = s.TimeOfDay;
            switch (r.Freq)
            {
                case 1:
                    if ((r.ByMonth == null || r.ByMonth.Contains(p.Month)) && MonthDayOk(r, p) && (r.ByDay == null || HasDow(r, p.DayOfWeek))) c.Add(p + tod);
                    break;
                case 2:
                    for (int i = 0; i < 7; i++)
                    {
                        var d = p.AddDays(i);
                        if (r.ByDay != null ? !HasDow(r, d.DayOfWeek) : d.DayOfWeek != s.DayOfWeek) continue;
                        if (r.ByMonth != null && !r.ByMonth.Contains(d.Month)) continue;
                        c.Add(d + tod);
                    }
                    break;
                case 3:
                    if (r.ByMonth == null || r.ByMonth.Contains(p.Month)) MonthDays(r, s, p.Year, p.Month, c);
                    break;
                default:
                    if (r.ByMonth != null) foreach (int m in r.ByMonth) MonthDays(r, s, p.Year, m, c);
                    else if (r.ByMonthDay != null) for (int m = 1; m <= 12; m++) MonthDays(r, s, p.Year, m, c);
                    else if (r.ByDay != null) YearDays(r, p.Year, tod, c);
                    else MonthDays(r, s, p.Year, s.Month, c);
                    break;
            }
        }

        static bool MonthDayOk(Rule r, DateTime d)
        {
            if (r.ByMonthDay == null) return true;
            int dim = DateTime.DaysInMonth(d.Year, d.Month);
            foreach (int md in r.ByMonthDay) if ((md > 0 ? md : dim + md + 1) == d.Day) return true;
            return false;
        }

        // Days of one month; invalid dates skipped
        static void MonthDays(Rule r, DateTime s, int y, int m, List<DateTime> c)
        {
            int dim = DateTime.DaysInMonth(y, m);
            var tod = s.TimeOfDay;
            if (r.ByMonthDay != null)
            {
                foreach (int md in r.ByMonthDay)
                {
                    int day = md > 0 ? md : dim + md + 1;
                    if (day < 1 || day > dim) continue;
                    var d = new DateTime(y, m, day);
                    if (r.ByDay != null && !HasDow(r, d.DayOfWeek)) continue;
                    c.Add(d + tod);
                }
                return;
            }
            if (r.ByDay != null)
            {
                var first = new DateTime(y, m, 1);
                foreach (var bd in r.ByDay)
                {
                    int off = (bd[1] - (int)first.DayOfWeek + 7) % 7;
                    if (bd[0] == 0 || r.Freq < 3)
                    {
                        for (int day = 1 + off; day <= dim; day += 7) c.Add(new DateTime(y, m, day) + tod);
                    }
                    else if (bd[0] > 0)
                    {
                        int day = 1 + off + (bd[0] - 1) * 7;
                        if (day <= dim) c.Add(new DateTime(y, m, day) + tod);
                    }
                    else
                    {
                        int back = ((int)new DateTime(y, m, dim).DayOfWeek - bd[1] + 7) % 7;
                        int day = dim - back + (bd[0] + 1) * 7;
                        if (day >= 1) c.Add(new DateTime(y, m, day) + tod);
                    }
                }
                return;
            }
            if (s.Day <= dim) c.Add(new DateTime(y, m, s.Day) + tod);
        }

        // YEARLY BYDAY only: counted per year
        static void YearDays(Rule r, int y, TimeSpan tod, List<DateTime> c)
        {
            var jan1 = new DateTime(y, 1, 1);
            int diy = DateTime.IsLeapYear(y) ? 366 : 365;
            foreach (var bd in r.ByDay)
            {
                int off = (bd[1] - (int)jan1.DayOfWeek + 7) % 7;
                if (bd[0] == 0) for (int d = off; d < diy; d += 7) c.Add(jan1.AddDays(d) + tod);
                else if (bd[0] > 0)
                {
                    int d = off + (bd[0] - 1) * 7;
                    if (d < diy) c.Add(jan1.AddDays(d) + tod);
                }
                else
                {
                    int back = ((int)new DateTime(y, 12, 31).DayOfWeek - bd[1] + 7) % 7;
                    int d = diy - 1 - back + (bd[0] + 1) * 7;
                    if (d >= 0) c.Add(jan1.AddDays(d) + tod);
                }
            }
        }

        static void Dedupe(List<DateTime> sorted)
        {
            for (int i = sorted.Count - 1; i > 0; i--) if (sorted[i] == sorted[i - 1]) sorted.RemoveAt(i);
        }

        static void SetPos(List<DateTime> cands, List<int> pos)
        {
            var pick = new List<DateTime>();
            foreach (int sp in pos)
            {
                int i = sp > 0 ? sp - 1 : cands.Count + sp;
                if (i >= 0 && i < cands.Count) pick.Add(cands[i]);
            }
            pick.Sort();
            Dedupe(pick);
            cands.Clear();
            cands.AddRange(pick);
        }

        // UNTIL in the same terms as DTSTART
        static Func<DateTime, bool> UntilTest(Rule r, Stamp start, Ctx ctx)
        {
            if (r.Until == null) return null;
            var u = ctx.Time(r.Until, null, null);
            if (u == null) return null;
            if (start.Date || u.Date) { var day = u.Wall.Date; return w => w.Date > day; }
            if (u.Utc)
            {
                var limit = u.Wall;
                if (start.Utc) return w => w > limit;
                var zone = start.Zone ?? Local;
                return w => zone.ToUtc(w) > limit;
            }
            var wall = u.Wall;
            return w => w > wall;
        }

        static void Event(Comp ev, List<Comp> overrides, bool single, Ctx ctx, DateTime fromUtc, DateTime toUtc)
        {
            var res = ctx.Res;
            var start = ctx.Time(ev.Get("DTSTART"));
            if (start == null) { res.Broken++; return; }
            bool allDay = start.Date;
            int days = 1;
            var dur = TimeSpan.Zero;
            TimeSpan d;
            var endP = ev.Get("DTEND");
            var durP = ev.Get("DURATION");
            if (allDay)
            {
                var e = ctx.Time(endP);
                if (e != null) days = (int)(e.Wall.Date - start.Wall.Date).TotalDays;
                else if (durP != null && Duration(durP.Value, out d)) days = (int)Math.Ceiling(d.TotalDays);
                days = Math.Max(1, Math.Min(days, 366));
            }
            else
            {
                var e = ctx.Time(endP);
                if (e != null) dur = ctx.Utc(e) - ctx.Utc(start);
                else if (durP != null && Duration(durP.Value, out d)) dur = d;
                if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;
                if (dur > TimeSpan.FromDays(31)) dur = TimeSpan.FromDays(31);
            }
            var fromDate = fromUtc.ToLocalTime().Date;
            var toDate = toUtc.ToLocalTime().Date.AddDays(1);
            // Instance keys: UTC instants, or dates
            var keys = new List<DateTime>();
            Rule rule = null;
            var rr = single ? null : ev.Get("RRULE");
            if (rr != null)
            {
                rule = Rule.Parse(rr.Value);
                if (rule.Complex) { res.Complex++; rule = null; }
            }
            if (rule == null) keys.Add(allDay ? start.Wall.Date : ctx.Utc(start));
            else
            {
                DateTime from, to;
                if (allDay) { from = fromDate.AddDays(-days); to = toDate; }
                else if (start.Utc) { from = fromUtc - dur - TimeSpan.FromDays(1); to = toUtc.AddDays(1); }
                else { from = start.Zone.ToWall(fromUtc) - dur - TimeSpan.FromDays(1); to = start.Zone.ToWall(toUtc).AddDays(1); }
                var past = UntilTest(rule, start, ctx);
                foreach (var w in Occurrences(rule, start.Wall, from, to, past, ctx.Budget))
                    keys.Add(allDay ? w.Date : start.Utc ? DateTime.SpecifyKind(w, DateTimeKind.Utc) : start.Zone.ToUtc(w));
            }
            // Exclusions gathered first, applied once
            var gone = new HashSet<DateTime>();
            var goneDays = new HashSet<DateTime>();
            if (!single)
            {
                var have = new HashSet<DateTime>(keys);
                int values = 0;
                foreach (var p in ev.Props)
                {
                    if ((p.Name != "RDATE" && p.Name != "EXDATE") || values > MaxDates) continue;
                    foreach (var raw in p.Value.Split(','))
                    {
                        if (++values > MaxDates) { res.Capped = true; break; }
                        string v = raw;
                        int slash = v.IndexOf('/');
                        if (slash > 0) v = v.Substring(0, slash);
                        var t = ctx.Time(v, p.Type, p.Tzid);
                        if (t == null) continue;
                        if (p.Name == "EXDATE") { Drop(gone, goneDays, t, allDay, ctx); continue; }
                        var add = allDay ? t.Wall.Date : !t.Date ? ctx.Utc(t)
                                : start.Utc ? DateTime.SpecifyKind(t.Wall + start.Wall.TimeOfDay, DateTimeKind.Utc) : start.Zone.ToUtc(t.Wall + start.Wall.TimeOfDay);
                        if (keys.Count < MaxPerEvent && have.Add(add)) keys.Add(add);
                    }
                }
            }
            if (overrides != null)
                foreach (var o in overrides)
                {
                    var rid = ctx.Time(o.Get("RECURRENCE-ID"));
                    if (rid != null) Drop(gone, goneDays, rid, allDay, ctx);
                }
            if (gone.Count > 0 || goneDays.Count > 0)
            {
                var zone = start.Zone ?? Local;
                keys.RemoveAll(k => gone.Contains(k) || (goneDays.Count > 0 && goneDays.Contains(allDay || start.Utc ? k.Date : zone.ToWall(k).Date)));
            }
            bool cancelled = ev.Text("STATUS").Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
            string uid = ev.Text("UID"), summary = ev.Text("SUMMARY"), location = ev.Text("LOCATION");
            string url = ev.Get("URL") != null ? ev.Get("URL").Value.Trim() : "";
            string meet = ev.Get("X-GOOGLE-CONFERENCE") != null ? ev.Get("X-GOOGLE-CONFERENCE").Value.Trim()
                        : ev.Get("X-MICROSOFT-SKYPETEAMSMEETINGURL") != null ? ev.Get("X-MICROSOFT-SKYPETEAMSMEETINGURL").Value.Trim() : "";
            keys.Sort();
            foreach (var k in keys)
            {
                var item = new IcsItem { Uid = uid, Summary = summary, Location = location, Url = url, Meet = meet, AllDay = allDay, Cancelled = cancelled };
                if (allDay)
                {
                    item.Start = k;
                    item.End = k.AddDays(days);
                    if (item.End <= fromDate || item.Start >= toDate) continue;
                }
                else
                {
                    item.Start = k;
                    item.End = k + dur;
                    if (k >= toUtc || item.End < fromUtc || (dur == TimeSpan.Zero && k < fromUtc)) continue;
                }
                if (res.Items.Count >= MaxItems) { res.Capped = true; return; }
                res.Items.Add(item);
            }
            if (overrides != null)
                foreach (var o in overrides)
                {
                    int before = res.Items.Count;
                    Event(o, null, true, ctx, fromUtc, toUtc);
                    if (cancelled) for (int i = before; i < res.Items.Count; i++) res.Items[i].Cancelled = true;
                }
        }

        // EXDATE or override: instant or day
        static void Drop(HashSet<DateTime> gone, HashSet<DateTime> days, Stamp t, bool allDay, Ctx ctx)
        {
            if (allDay || t.Date) days.Add(t.Wall.Date);
            else gone.Add(ctx.Utc(t));
        }

        static void Todo(Comp t, Ctx ctx, DateTime fromUtc, DateTime toUtc)
        {
            string status = t.Text("STATUS").ToUpperInvariant();
            if (status == "COMPLETED" || status == "CANCELLED" || t.Get("COMPLETED") != null || t.Text("PERCENT-COMPLETE") == "100") return;
            var due = ctx.Time(t.Get("DUE")) ?? ctx.Time(t.Get("DTSTART"));
            if (due == null) return;
            var item = new IcsItem { Todo = true, AllDay = due.Date, Uid = t.Text("UID"), Summary = t.Text("SUMMARY"), Location = t.Text("LOCATION") };
            var url = t.Get("URL");
            if (url != null) item.Url = url.Value.Trim();
            // Overdue up to two weeks
            if (due.Date)
            {
                item.Start = item.End = due.Wall.Date;
                if (item.Start < fromUtc.ToLocalTime().Date.AddDays(-14) || item.Start > toUtc.ToLocalTime().Date) return;
            }
            else
            {
                item.Start = item.End = ctx.Utc(due);
                if (item.Start < fromUtc.AddDays(-14) || item.Start > toUtc) return;
            }
            ctx.Res.Items.Add(item);
        }
    }
}
