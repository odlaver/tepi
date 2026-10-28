using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace Tepi
{
    // Tepi.exe --statusline < status.json
    static class StatusLine
    {
        const int InputCap = 1024 * 1024, OutputCap = 16 * 1024, PostMs = 150, ChainMs = 5000;
        static readonly string[] Windows = { "five_hour", "seven_day", "spend_limit" };

        public static int Run(string[] args)
        {
            var clock = Stopwatch.StartNew();
            byte[] input = ReadInput();
            Dictionary<string, object> data = null;
            try { data = LiteJson.Parse(Encoding.UTF8.GetString(input)) as Dictionary<string, object>; }
            catch (Exception) { }
            var settings = new Settings();
            Lang.English = settings.Get("lang", "id") == "en";
            string name = ConfigName(data);
            var usage = Usage(data, name);
            Thread post = null;
            if (usage != null && settings.GetBool("agent.quota", true) && Relay.Running())
            {
                byte[] body = Encoding.UTF8.GetBytes(LiteJson.Encode(usage));
                post = new Thread(delegate () { Relay.Post("/usage", body, PostMs); }) { IsBackground = true };
                post.Start();
            }
            string text = null;
            // A chained child never chains again
            if (Environment.GetEnvironmentVariable("TEPI_STATUSLINE") == null)
            {
                string prev = StatusSetup.Previous(name);
                if (prev != null) text = Chain(prev, input);
            }
            if (string.IsNullOrEmpty(text)) text = Line(data);
            Write(text);
            if (post != null) post.Join(Math.Max(0, PostMs - (int)clock.ElapsedMilliseconds));
            return 0;
        }

        static byte[] ReadInput()
        {
            if (!Console.IsInputRedirected) return new byte[0];
            var ms = new MemoryStream();
            try
            {
                var input = Console.OpenStandardInput();
                var buf = new byte[16384];
                int n;
                while (ms.Length < InputCap && (n = input.Read(buf, 0, (int)Math.Min(buf.Length, InputCap - ms.Length))) > 0) ms.Write(buf, 0, n);
            }
            catch (Exception) { }
            return ms.ToArray();
        }

        static void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var o = new UTF8Encoding(false).GetBytes(text);
                using (var so = Console.OpenStandardOutput()) so.Write(o, 0, o.Length);
            }
            catch (Exception) { }
        }

        // Profile folder: env first, then transcript
        public static string ConfigName(Dictionary<string, object> data)
        {
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(dir)) return SafeName(AgentHub.LastPart(dir.Trim()));
            string p = Json.Str(data, "transcript_path").Replace('/', '\\');
            int i = p.IndexOf("\\.claude", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return ".claude";
            int end = p.IndexOf('\\', i + 1);
            return SafeName(end > 0 ? p.Substring(i + 1, end - i - 1) : p.Substring(i + 1));
        }

        public static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_') sb.Append(c);
                if (sb.Length >= 40) break;
            }
            return sb.Length > 0 ? sb.ToString() : ".claude";
        }

        public static string ProfileLabel(string name)
        {
            if (name == ".claude") return "";
            return name.StartsWith(".claude-") ? name.Substring(8) : name.TrimStart('.');
        }

        // Only the numbers Tepi shows
        static Dictionary<string, object> Usage(Dictionary<string, object> data, string name)
        {
            var limits = Json.Obj(data, "rate_limits");
            if (limits == null) return null;
            var o = new Dictionary<string, object>
            {
                { "hook_event_name", "Usage" },
                { "session_id", Ascii(Json.Str(data, "session_id"), 80) },
                { "profile", ProfileLabel(name) },
                { "model", Ascii(Json.Str(Json.Obj(data, "model"), "display_name"), 40) },
            };
            int found = 0;
            foreach (var key in Windows)
            {
                var src = Json.Obj(limits, key);
                double pct = Json.Num(src, "used_percentage", double.NaN), at = Json.Num(src, "resets_at", double.NaN);
                if (double.IsNaN(pct) || double.IsNaN(at)) continue;
                var w = new Dictionary<string, object> { { "used_percentage", pct }, { "resets_at", at } };
                double used = Json.Num(src, "used_usd", double.NaN), limit = Json.Num(src, "limit_usd", double.NaN);
                if (!double.IsNaN(used)) w["used_usd"] = used;
                if (!double.IsNaN(limit)) w["limit_usd"] = limit;
                o[key] = w;
                found++;
            }
            return found > 0 ? o : null;
        }

        // Own line: model and windows, ASCII only
        static string Line(Dictionary<string, object> data)
        {
            var parts = new List<string>();
            string model = Ascii(Json.Str(Json.Obj(data, "model"), "display_name"), 32);
            if (model.Length > 0) parts.Add(model);
            var limits = Json.Obj(data, "rate_limits");
            string[] labels = { Lang.T("5j"), Lang.T("7h"), Lang.T("batas") };
            for (int i = 0; i < Windows.Length; i++)
            {
                double v = Json.Num(Json.Obj(limits, Windows[i]), "used_percentage", double.NaN);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                parts.Add(labels[i] + " " + (int)Math.Floor(Math.Max(0, Math.Min(100, v))) + "%");
            }
            return string.Join(" | ", parts.ToArray());
        }

        static string Ascii(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c >= 0x20 && c < 0x7F) sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim();
        }

        // Previous status line, same stdin
        static string Chain(string command, byte[] input)
        {
            try
            {
                var psi = Shell(command);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.EnvironmentVariables["TEPI_STATUSLINE"] = "1";
                using (var proc = Process.Start(psi))
                {
                    var read = proc.StandardOutput.ReadToEndAsync();
                    var stdin = proc.StandardInput.BaseStream;
                    // Own thread: a child may never read
                    new Thread(delegate ()
                    {
                        try { stdin.Write(input, 0, input.Length); stdin.Close(); }
                        catch (Exception) { }
                    }) { IsBackground = true }.Start();
                    if (!read.Wait(ChainMs))
                    {
                        try { proc.Kill(); }
                        catch (Exception) { }
                        return null;
                    }
                    string s = read.Result;
                    return s.Length > OutputCap ? s.Substring(0, OutputCap) : s;
                }
            }
            catch (Exception) { return null; }
        }

        // Same shell Claude Code picks
        static ProcessStartInfo Shell(string command)
        {
            string bash = GitBash();
            if (bash != null) return new ProcessStartInfo(bash, "-c " + Arg(command));
            return new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        }

        static string GitBash()
        {
            string env = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (dir.Trim().Length == 0 || !File.Exists(Path.Combine(dir.Trim(), "git.exe"))) continue;
                    string b = Path.GetFullPath(Path.Combine(dir.Trim(), "..", "bin", "bash.exe"));
                    if (File.Exists(b)) return b;
                }
                catch (Exception) { }
            }
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("LOCALAPPDATA") + "\\Programs" })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string b = Path.Combine(root, "Git", "bin", "bash.exe");
                if (File.Exists(b)) return b;
            }
            return null;
        }

        // One argv entry, Windows quoting rules
        static string Arg(string s)
        {
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }
    }

    // Small JSON reader for the fast path
    static class LiteJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i, 0);
            Skip(s, ref i);
            if (i != s.Length) throw new FormatException();
            return v;
        }

        static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException();
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException();
                    string key = Text(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i++] != ':') throw new FormatException();
                    d[key] = Value(s, ref i, depth + 1);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i, depth + 1));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatException();
                }
            }
            if (c == '"') return Text(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double n;
            if (i == start || !double.TryParse(s.Substring(start, i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n)) throw new FormatException();
            return n;
        }

        static string Text(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException();
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException();
        }

        // Flat values: strings, numbers, objects
        public static string Encode(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            var d = v as Dictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (v is double) { sb.Append(((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); return; }
            sb.Append('"');
            foreach (char c in Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }

    // statusLine per Claude profile, old kept
    static class StatusSetup
    {
        static string Store { get { return Path.Combine(Settings.Dir, "statusline.json"); } }

        // Forward slashes survive Git Bash
        public static string Command()
        {
            return "\"" + System.Reflection.Assembly.GetEntryAssembly().Location.Replace('\\', '/') + "\" --statusline";
        }

        static bool IsOurs(object line)
        {
            string c = Json.Str(line, "command");
            return c.StartsWith("\"") && c.EndsWith(".exe\" --statusline", StringComparison.OrdinalIgnoreCase);
        }

        public static bool Installed()
        {
            var profiles = HookSetup.Profiles();
            if (profiles.Count == 0) return false;
            foreach (var dir in profiles)
            {
                var root = HookSetup.Load(Path.Combine(dir, "settings.json"));
                if (root == null || !IsOurs(HookSetup.Get(root, "statusLine"))) return false;
            }
            return true;
        }

        public static string Install()
        {
            // Bash would expand these inside quotes
            if (Command().IndexOfAny("$`".ToCharArray()) >= 0) return Lang.T("Pindahkan Tepi.exe ke folder tanpa $ atau `");
            var store = Load();
            int done = 0;
            foreach (var dir in HookSetup.Profiles())
            {
                string name = StatusLine.SafeName(Path.GetFileName(dir)), file = Path.Combine(dir, "settings.json");
                var root = File.Exists(file) ? HookSetup.Load(file) : new Dictionary<string, object>();
                if (root == null) return Lang.T("settings.json di ") + Path.GetFileName(dir) + Lang.T(" tidak terbaca");
                object cur;
                bool had = root.TryGetValue("statusLine", out cur);
                if (!IsOurs(cur))
                {
                    // Saved before settings.json changes
                    var rec = new Dictionary<string, object> { { "had", had } };
                    if (had) rec["value"] = cur;
                    store[name] = rec;
                    Save(store);
                }
                var mine = new Dictionary<string, object> { { "type", "command" }, { "command", Command() } };
                var old = Json.Obj(Json.Obj(store, name), "value");
                foreach (var k in new[] { "padding", "refreshInterval" })
                    if (old != null && old.ContainsKey(k)) mine[k] = old[k];
                HookSetup.Backup(file);
                root["statusLine"] = mine;
                HookSetup.Save(file, root);
                done++;
            }
            return done == 0 ? Lang.T("Profil Claude Code tidak ditemukan") : null;
        }

        // Puts back exactly what was there
        public static void Remove()
        {
            var store = Load();
            foreach (var dir in HookSetup.Profiles())
            {
                string name = StatusLine.SafeName(Path.GetFileName(dir)), file = Path.Combine(dir, "settings.json");
                var root = HookSetup.Load(file);
                if (root != null && IsOurs(HookSetup.Get(root, "statusLine")))
                {
                    var rec = Json.Obj(store, name);
                    HookSetup.Backup(file);
                    if (rec != null && true.Equals(HookSetup.Get(rec, "had"))) root["statusLine"] = HookSetup.Get(rec, "value");
                    else root.Remove("statusLine");
                    HookSetup.Save(file, root);
                }
                store.Remove(name);
            }
            Save(store);
        }

        // Client path: no JavaScriptSerializer load
        public static string Previous(string name)
        {
            object store = null;
            try { if (File.Exists(Store)) store = LiteJson.Parse(File.ReadAllText(Store, Encoding.UTF8)); }
            catch (Exception) { return null; }
            var value = Json.Obj(Json.Obj(store, name), "value");
            if (value == null || Json.Str(value, "type") != "command" || IsOurs(value)) return null;
            string cmd = Json.Str(value, "command");
            return cmd.Trim().Length > 0 ? cmd : null;
        }

        static Dictionary<string, object> Load()
        {
            return (File.Exists(Store) ? HookSetup.Load(Store) : null) ?? new Dictionary<string, object>();
        }

        static void Save(Dictionary<string, object> store)
        {
            Directory.CreateDirectory(Settings.Dir);
            if (store.Count == 0) { if (File.Exists(Store)) File.Delete(Store); return; }
            HookSetup.Save(Store, store);
        }
    }

    class QuotaWindow
    {
        public bool Has, Stale;
        public float Pct;
        public DateTime ResetUtc;
        public int Told;
        public string PctText = "", ResetText = "";
    }

    class QuotaInfo
    {
        public string Profile = "", Model = "", Tail = "";
        public DateTime AtUtc;
        public readonly QuotaWindow[] W = { new QuotaWindow(), new QuotaWindow(), new QuotaWindow() };
    }

    // Claude plan usage: Agen line, glance, warnings
    partial class Island
    {
        // Toast marker: DrawInfo draws a ring
        const char IcoGauge = '';
        const float QuotaRowH = 28, QuotaGlanceRowH = 32;
        static readonly string[] QuotaKeys = { "five_hour", "seven_day", "spend_limit" };
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        readonly List<QuotaInfo> quotas = new List<QuotaInfo>();
        readonly string[] quotaLabels = new string[3];
        int quotaVersion, quotaBuilt = -1, quotaSig;
        long quotaMinute;
        bool quotaEnglish;
        Action actQuotaOpen;
        float quotaToastLevel;
        // Column starts, shared by stacked lines
        readonly float[] quotaCols = new float[3];
        // Pulse slots: no closure per frame
        readonly float[] pulseX = new float[6], pulseY = new float[6];
        Action<DateTime>[] pulseActs;

        void QuotaActs()
        {
            if (actQuotaOpen != null) return;
            actQuotaOpen = delegate { Show(PageAgents); };
            pulseActs = new Action<DateTime>[pulseX.Length];
            for (int i = 0; i < pulseActs.Length; i++)
            {
                int k = i;
                pulseActs[i] = t => QuotaPulse(pulseX[k], pulseY[k], t);
            }
        }

        void OnUsage(HookEvent ev)
        {
            if (!settings.GetBool("agent.quota", true)) return;
            string profile = StatusLine.SafeName(CleanText(Json.Str(ev.Data, "profile"), 24));
            if (Json.Str(ev.Data, "profile").Length == 0) profile = "";
            var q = quotas.Find(x => x.Profile == profile);
            if (q == null)
            {
                // Few profiles; oldest goes
                if (quotas.Count >= 4)
                {
                    var oldest = quotas[0];
                    foreach (var x in quotas) if (x.AtUtc < oldest.AtUtc) oldest = x;
                    quotas.Remove(oldest);
                }
                q = new QuotaInfo { Profile = profile };
                quotas.Add(q);
                quotas.Sort((a, b) => string.CompareOrdinal(a.Profile, b.Profile));
            }
            var now = DateTime.UtcNow;
            q.Model = CleanText(Json.Str(ev.Data, "model"), 40);
            q.AtUtc = now;
            double lo = (now.AddDays(-8) - Epoch).TotalSeconds, hi = (now.AddDays(40) - Epoch).TotalSeconds;
            for (int i = 0; i < QuotaKeys.Length; i++)
            {
                var src = Json.Obj(ev.Data, QuotaKeys[i]);
                double pct = Json.Num(src, "used_percentage", double.NaN), at = Json.Num(src, "resets_at", double.NaN);
                var w = q.W[i];
                if (src == null || double.IsNaN(pct) || double.IsInfinity(pct) || double.IsNaN(at) || at < lo || at > hi) { w.Has = false; continue; }
                var reset = Epoch.AddSeconds(Math.Round(at));
                // A later reset is a new window
                if (!w.Has || reset > w.ResetUtc.AddMinutes(30)) w.Told = 0;
                w.Has = true;
                w.Pct = (float)Math.Max(0, Math.Min(100, pct));
                w.ResetUtc = reset;
                QuotaWarn(q, i, w);
            }
            quotaVersion++;
            int sig = QuotaSig();
            if (sig != quotaSig && expanded) Kick();
            quotaSig = sig;
        }

        int QuotaSig()
        {
            int h = quotas.Count;
            foreach (var q in quotas)
                foreach (var w in q.W) h = h * 31 + (w.Has ? (int)w.Pct * 7919 + w.ResetUtc.Minute + 1 : 0);
            return h;
        }

        // Once per window at 80 and 95
        void QuotaWarn(QuotaInfo q, int i, QuotaWindow w)
        {
            int level = w.Pct >= 95 ? 95 : w.Pct >= 80 ? 80 : 0;
            if (level <= w.Told || DateTime.UtcNow >= w.ResetUtc) return;
            w.Told = level;
            QuotaActs();
            var t = level >= 95 ? ShowToast(View.Info, 5, 3, false, true) : ShowToast(View.Info, 3.6, 2, false);
            if (t == null) return;
            t.Glyph = IcoGauge;
            quotaToastLevel = w.Pct / 100f;
            t.Title = Lang.F("Kuota {0} {1}", QuotaLabel(i), Ink.Percent((int)w.Pct));
            t.Detail = QuotaReset(w.ResetUtc) + (q.Profile.Length > 0 ? " · " + q.Profile : "");
            t.Data = actQuotaOpen;
        }

        static string QuotaLabel(int i) { return i == 0 ? Lang.T("5 jam") : i == 1 ? Lang.T("7 hari") : Lang.T("Batas"); }

        static string QuotaReset(DateTime utc)
        {
            var local = utc.ToLocalTime();
            if (local.Date == DateTime.Today) return Lang.T("reset ") + Fmt.Hm(local);
            return Lang.T("reset ") + local.ToString("ddd", Fmt.Id) + " " + Fmt.Hm(local);
        }

        // Data older than a week hides
        static bool QuotaShown(QuotaInfo q)
        {
            if ((DateTime.UtcNow - q.AtUtc).TotalDays > 8) return false;
            foreach (var w in q.W) if (w.Has) return true;
            return false;
        }

        static float QuotaPeak(QuotaInfo q)
        {
            float top = 0;
            var now = DateTime.UtcNow;
            foreach (var w in q.W) if (w.Has && now < w.ResetUtc) top = Math.Max(top, w.Pct);
            return top;
        }

        int QuotaCount(bool notable)
        {
            int n = 0;
            foreach (var q in quotas) if (QuotaShown(q) && (!notable || QuotaPeak(q) >= 70)) n++;
            return Math.Min(2, n);
        }

        // Agen page height, 0 = none
        float QuotaBlockH() { int n = QuotaCount(false); return n == 0 ? 0 : n * QuotaRowH + 8; }

        float QuotaGlanceH() { int n = QuotaCount(true); return n == 0 ? 0 : n * QuotaGlanceRowH + HomeGap - 2; }

        // Texts rebuilt on change or minute
        void QuotaTexts()
        {
            long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            if (quotaBuilt == quotaVersion && quotaMinute == minute && quotaEnglish == Lang.English) return;
            quotaBuilt = quotaVersion;
            quotaMinute = minute;
            quotaEnglish = Lang.English;
            for (int i = 0; i < 3; i++) quotaLabels[i] = QuotaLabel(i);
            var now = DateTime.UtcNow;
            bool several = QuotaCount(false) > 1;
            foreach (var q in quotas)
            {
                foreach (var w in q.W)
                {
                    if (!w.Has) continue;
                    w.Stale = now >= w.ResetUtc;
                    w.PctText = Ink.Percent((int)Math.Floor(w.Pct));
                    w.ResetText = w.Stale ? Lang.T("sudah reset") : QuotaReset(w.ResetUtc);
                }
                string ago = Lang.T("diperbarui ") + Fmt.Ago(q.AtUtc);
                q.Tail = several ? (q.Profile.Length > 0 ? q.Profile : Lang.T("utama")) + " · " + ago : ago;
            }
        }

        // Agen page: one calm line per profile
        float DrawQuotaBlock(float w, float top)
        {
            float h = QuotaBlockH();
            if (h <= 0) return 0;
            QuotaActs();
            QuotaTexts();
            int shown = 0, slot = 0;
            QuotaColumns(false);
            foreach (var q in quotas)
            {
                if (!QuotaShown(q) || shown >= 2) continue;
                QuotaLine(q, 36, w - 26, top + QuotaRowH / 2 + shown * QuotaRowH, q.Tail, ref slot);
                shown++;
            }
            float y = top + h - 3.5f;
            g.DrawLine(p.P(p.T.Line, 1f), 26, y, w - 26, y);
            return h;
        }

        // Ringkasan: only when a window runs high
        void DrawQuotaGlance(float x, float y, float full)
        {
            QuotaActs();
            QuotaTexts();
            int shown = 0, slot = 0;
            QuotaColumns(true);
            foreach (var q in quotas)
            {
                if (!QuotaShown(q) || QuotaPeak(q) < 70 || shown >= 2) continue;
                var r = new RectangleF(x, y + shown * QuotaGlanceRowH, full, QuotaGlanceRowH - 2);
                string id = shown == 0 ? "homeQuota" : "homeQuota2";
                AddHit(r, id, actQuotaOpen);
                if (Hover(id)) p.FillRound(r, Ink.RadiusControl, Pressed(id) ? p.T.FillPressed : p.T.FillHover);
                QuotaLine(q, x + 16, r.Right - 12, r.Y + r.Height / 2, q.Profile.Length > 0 ? q.Profile : Lang.T("Kuota Claude"), ref slot);
                shown++;
            }
        }

        // Widest item per window across lines
        void QuotaColumns(bool notable)
        {
            var lf = Ink.Caption;
            var pf = Ink.SmallSemi(11.5f);
            for (int i = 0; i < 3; i++)
            {
                float widest = 0;
                foreach (var q in quotas)
                {
                    var w = q.W[i];
                    if (!w.Has || !QuotaShown(q) || (notable && QuotaPeak(q) < 70)) continue;
                    float iw = 20 + p.Measure(quotaLabels[i], lf) + 5 + (w.Stale ? 0 : p.Measure(w.PctText, pf) + 7) + p.Measure(w.ResetText, lf);
                    widest = Math.Max(widest, iw);
                }
                quotaCols[i] = widest;
            }
        }

        // Ring, label, percent, reset; tail right
        void QuotaLine(QuotaInfo q, float cx, float right, float cy, string tail, ref int slot)
        {
            var lf = Ink.Caption;
            var pf = Ink.SmallSemi(11.5f);
            float x = cx - 6.5f;
            for (int i = 0; i < 3; i++)
            {
                var w = q.W[i];
                if (!w.Has) continue;
                bool hot = !w.Stale && w.Pct >= 80;
                float rx = x + 6.5f;
                if (!w.Stale && w.Pct >= 95 && !Anim.Reduced && slot < pulseX.Length)
                {
                    pulseX[slot] = rx;
                    pulseY[slot] = cy;
                    LiveRect(new RectangleF(rx - 11, cy - 11, 22, 22), 15, pulseActs[slot]);
                    slot++;
                }
                else Ring(rx, cy, 6.5f, w.Stale ? 0 : w.Pct / 100f, p.T.Accent, 2f);
                float tx = x + 20;
                p.Text(quotaLabels[i], lf, hot ? p.T.Text : p.T.Sub, tx, cy, 200, StringAlignment.Near);
                tx += p.Measure(quotaLabels[i], lf) + 5;
                if (!w.Stale)
                {
                    p.Text(w.PctText, pf, hot ? p.T.Text : p.T.Sub, tx, cy, 60, StringAlignment.Near);
                    tx += p.Measure(w.PctText, pf) + 7;
                }
                p.Text(w.ResetText, lf, p.T.Faint, tx, cy, 200, StringAlignment.Near);
                x += Math.Max(quotaCols[i], tx + p.Measure(w.ResetText, lf) - x) + 22;
            }
            if (right - x > p.Measure(tail, lf) + 4) p.TextRight(tail, lf, p.T.Faint, right, cy);
        }

        // Near the limit: the ring breathes
        void QuotaPulse(float cx, float cy, DateTime t)
        {
            double phase = (t.Ticks / TimeSpan.TicksPerMillisecond % 1800) / 1800.0;
            float k = (float)(0.5 - 0.5 * Math.Cos(phase * Math.PI * 2));
            p.Circle(cx, cy, 9.5f + k * 1.5f, Ink.A(p.T.Accent, 0.10f + 0.16f * k));
            Ring(cx, cy, 6.5f, 1, p.T.Accent, 2f);
        }

        // Settings row: what Tepi last saw
        string QuotaNote(bool installed)
        {
            if (!hooksOn) return Lang.T("Butuh Claude Code terhubung");
            if (!installed) return Lang.T("Lewat status line Claude Code");
            QuotaTexts();
            foreach (var q in quotas)
            {
                if (!QuotaShown(q)) continue;
                var parts = new List<string>();
                for (int i = 0; i < 3; i++)
                    if (q.W[i].Has) parts.Add(QuotaLabel(i) + " " + (q.W[i].Stale ? Lang.T("sudah reset") : q.W[i].PctText));
                parts.Add(q.Tail);
                return string.Join(" · ", parts.ToArray());
            }
            return Lang.T("Muncul setelah balasan pertama di sesi baru");
        }

        // Settings switch: status line on or off
        void SetQuota(bool on)
        {
            settings.Set("agent.quota", on);
            settings.Save();
            try
            {
                if (HooksInstalled())
                {
                    string err = null;
                    if (on) err = StatusSetup.Install();
                    else StatusSetup.Remove();
                    if (err != null) ShowInfo(IcoGauge, Lang.T("Kuota belum dipantau"), err, true);
                    else ShowInfo(IcoGauge, on ? Lang.T("Kuota Claude dipantau") : Lang.T("Kuota Claude dilepas"), on ? Lang.T("lewat status line") : Lang.T("status line dikembalikan"), true);
                }
            }
            catch (Exception ex)
            {
                Log.Write("statusline setup: " + ex.Message);
                ShowInfo(IcoGauge, Lang.T("Kuota belum dipantau"), Fmt.OneLine(ex.Message, 60), true);
            }
            if (!on) { quotas.Clear(); quotaVersion++; }
            Kick();
        }

        // Follows the Claude Code connection
        void QuotaFollowHooks(bool connected)
        {
            try
            {
                if (!connected) StatusSetup.Remove();
                else if (settings.GetBool("agent.quota", true))
                {
                    string err = StatusSetup.Install();
                    if (err != null) Log.Write("statusline setup: " + err);
                }
            }
            catch (Exception ex) { Log.Write("statusline setup: " + ex.Message); }
        }
    }
}
