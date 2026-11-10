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
            bool agy = Json.Str(data, "product") == "antigravity";
            string name = agy ? StatusSetup.AgyName : ConfigName(data);
            var usage = agy ? AgyUsage(data) : Usage(data, name);
            Thread post = null;
            if (usage != null && settings.GetBool(agy ? StatusSetup.AgentsKey : "agent.quota", true) && Relay.Running())
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
            if (string.IsNullOrEmpty(text)) text = agy ? AgyLine(data) : Line(data);
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

        // Antigravity: weekly Gemini and other-model quota
        static readonly string[] AgyKeys = { "gemini-weekly", "3p-weekly" }, AgyKinds = { "gemini", "other" };

        static Dictionary<string, object> AgyUsage(Dictionary<string, object> data)
        {
            var quota = Json.Obj(data, "quota");
            if (quota == null) return null;
            var o = new Dictionary<string, object>
            {
                { "hook_event_name", "Usage" },
                { "agent", "antigravity" },
                { "model", Ascii(Json.Str(Json.Obj(data, "model"), "display_name"), 40) },
            };
            int found = 0;
            for (int i = 0; i < AgyKeys.Length; i++)
            {
                var src = Json.Obj(quota, AgyKeys[i]);
                double left = Json.Num(src, "remaining_fraction", double.NaN), at = AgyReset(src);
                if (double.IsNaN(left) || double.IsNaN(at)) continue;
                o["w" + found++] = new Dictionary<string, object> { { "kind", AgyKinds[i] }, { "used_percentage", (1 - left) * 100 }, { "resets_at", at } };
            }
            return found > 0 ? o : null;
        }

        // Unix seconds from reset_time, else reset_in_seconds
        static double AgyReset(Dictionary<string, object> src)
        {
            DateTime t;
            if (DateTime.TryParse(Json.Str(src, "reset_time"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t))
                return (t - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            double s = Json.Num(src, "reset_in_seconds", double.NaN);
            return double.IsNaN(s) ? double.NaN : (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds + s;
        }

        static string AgyLine(Dictionary<string, object> data)
        {
            var parts = new List<string>();
            string model = Ascii(Json.Str(Json.Obj(data, "model"), "display_name"), 32);
            if (model.Length > 0) parts.Add(model);
            var quota = Json.Obj(data, "quota");
            string[] labels = { "Gemini", Lang.T("lain") };
            for (int i = 0; i < AgyKeys.Length; i++)
            {
                double left = Json.Num(Json.Obj(quota, AgyKeys[i]), "remaining_fraction", double.NaN);
                if (double.IsNaN(left) || double.IsInfinity(left)) continue;
                parts.Add(labels[i] + " " + (int)Math.Floor(Math.Max(0, Math.Min(100, (1 - left) * 100))) + "%");
            }
            return string.Join(" | ", parts.ToArray());
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

        // Antigravity CLI: own statusLine, old kept
        public const string AgyName = "antigravity", AgentsKey = "agent.quota.agents";
        static string AgyFile { get { return Path.Combine(Settings.Home, ".gemini", "antigravity-cli", "settings.json"); } }

        // agy adds cmd /c and escapes quotes
        static string AgyCommand() { return AgentSetup.Exe() + " --statusline"; }

        static bool AgyOurs(object line)
        {
            return Json.Str(line, "command").EndsWith(".exe --statusline", StringComparison.OrdinalIgnoreCase);
        }

        public static bool AgyInstalled()
        {
            var root = HookSetup.Load(AgyFile);
            var line = root == null ? null : HookSetup.Get(root, "statusLine");
            return AgyOurs(line) && true.Equals(HookSetup.Get(line, "enabled")) && Json.Str(line, "command") == AgyCommand();
        }

        public static void AgyInstall()
        {
            string file = AgyFile;
            if (!File.Exists(file) || AgyCommand().IndexOf('"') >= 0 || AgyInstalled()) return;
            var root = HookSetup.Load(file);
            if (root == null) return;
            var store = Load();
            object cur;
            bool had = root.TryGetValue("statusLine", out cur);
            if (!AgyOurs(cur))
            {
                var rec = new Dictionary<string, object> { { "had", had } };
                if (had) rec["value"] = cur;
                store[AgyName] = rec;
                Save(store);
            }
            HookSetup.Backup(file);
            root["statusLine"] = new Dictionary<string, object> { { "type", "command" }, { "command", AgyCommand() }, { "enabled", true } };
            HookSetup.Save(file, root);
        }

        public static void AgyRemove()
        {
            var store = Load();
            var root = HookSetup.Load(AgyFile);
            if (root != null && AgyOurs(HookSetup.Get(root, "statusLine")))
            {
                var rec = Json.Obj(store, AgyName);
                HookSetup.Backup(AgyFile);
                if (rec != null && true.Equals(HookSetup.Get(rec, "had"))) root["statusLine"] = HookSetup.Get(rec, "value");
                else root.Remove("statusLine");
                HookSetup.Save(AgyFile, root);
            }
            store.Remove(AgyName);
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

    // Codex: newest session log with rate limits
    static class CodexLog
    {
        const int TailBytes = 512 * 1024, MaxFiles = 6;

        public static Dictionary<string, object> Usage(string root)
        {
            if (!Directory.Exists(root)) return null;
            var files = new List<FileInfo>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int d = 0; d < 8 && files.Count < 12; d++)
            {
                var day = DateTime.Now.Date.AddDays(-d);
                string dir = Path.Combine(root, day.ToString("yyyy", ci), day.ToString("MM", ci), day.ToString("dd", ci));
                if (Directory.Exists(dir)) files.AddRange(new DirectoryInfo(dir).GetFiles("rollout-*.jsonl"));
            }
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = 0; i < files.Count && i < MaxFiles; i++)
            {
                var limits = LastLimits(files[i].FullName);
                if (limits != null) return ToUsage(limits);
            }
            return null;
        }

        // Agents keep the file open: share it
        internal static string Tail(string path, int bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - bytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int n = 0, r;
                while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                return Encoding.UTF8.GetString(buf, 0, n);
            }
        }

        static Dictionary<string, object> LastLimits(string path)
        {
            var lines = Tail(path, TailBytes).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].IndexOf("\"rate_limits\":{", StringComparison.Ordinal) < 0) continue;
                Dictionary<string, object> o;
                try { o = Json.Parse(lines[i].Trim()) as Dictionary<string, object>; }
                catch (Exception) { continue; }
                var limits = Json.Obj(Json.Obj(o, "payload"), "rate_limits");
                if (limits != null) return limits;
            }
            return null;
        }

        static Dictionary<string, object> ToUsage(Dictionary<string, object> limits)
        {
            var o = new Dictionary<string, object> { { "hook_event_name", "Usage" }, { "agent", "codex" }, { "model", Json.Str(limits, "plan_type") } };
            int found = 0;
            foreach (var key in new[] { "primary", "secondary" })
            {
                var src = Json.Obj(limits, key);
                double pct = Json.Num(src, "used_percent", double.NaN), at = Json.Num(src, "resets_at", double.NaN);
                if (double.IsNaN(pct) || double.IsNaN(at)) continue;
                o["w" + found++] = new Dictionary<string, object> { { "used_percentage", pct }, { "resets_at", at }, { "minutes", Json.Num(src, "window_minutes", 0) } };
            }
            return found > 0 ? o : null;
        }
    }

    // Copilot: quotaSnapshots in its newest session events
    static class CopilotLog
    {
        const int TailBytes = 4 * 1024 * 1024, MaxFiles = 6;
        const string Key = "\"quotaSnapshots\":{";

        public static Dictionary<string, object> Usage(string root)
        {
            if (!Directory.Exists(root)) return null;
            var files = new List<FileInfo>();
            foreach (var d in new DirectoryInfo(root).GetDirectories())
            {
                var f = new FileInfo(Path.Combine(d.FullName, "events.jsonl"));
                if (f.Exists) files.Add(f);
            }
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = 0; i < files.Count && i < MaxFiles; i++)
            {
                var usage = ToUsage(LastSnapshot(files[i].FullName));
                if (usage != null) return usage;
            }
            return null;
        }

        // Lines carry whole prompts: cut the object out
        static Dictionary<string, object> LastSnapshot(string path)
        {
            string text = CodexLog.Tail(path, TailBytes);
            int at = text.LastIndexOf(Key, StringComparison.Ordinal);
            while (at >= 0)
            {
                int start = at + Key.Length - 1, end = ObjectEnd(text, start);
                if (end > start)
                {
                    try
                    {
                        var o = Json.Parse(text.Substring(start, end - start + 1)) as Dictionary<string, object>;
                        if (o != null) return o;
                    }
                    catch (Exception) { }
                }
                at = at > 0 ? text.LastIndexOf(Key, at - 1, StringComparison.Ordinal) : -1;
            }
            return null;
        }

        // Matching brace, strings skipped
        static int ObjectEnd(string s, int start)
        {
            int depth = 0;
            bool str = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (str) { if (c == '\\') i++; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        static Dictionary<string, object> ToUsage(Dictionary<string, object> snap)
        {
            var src = Json.Obj(snap, "premium_interactions");
            double total = Json.Num(src, "entitlementRequests", 0), left = Json.Num(src, "remainingPercentage", double.NaN);
            if (src == null || true.Equals(HookSetup.Get(src, "isUnlimitedEntitlement")) || total <= 0 || double.IsNaN(left)) return null;
            DateTime reset;
            if (!DateTime.TryParse(Json.Str(src, "resetDate"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out reset)) return null;
            var w = new Dictionary<string, object>
            {
                { "kind", "premium" },
                { "used_percentage", 100 - left },
                { "resets_at", (reset - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds },
            };
            return new Dictionary<string, object> { { "hook_event_name", "Usage" }, { "agent", "copilot" }, { "model", "" }, { "w0", w } };
        }
    }

    class QuotaWindow
    {
        public bool Has, Stale;
        public float Pct;
        public DateTime ResetUtc;
        public int Told, Minutes;
        public string PctText = "", ResetText = "", Kind = "", Label = "";
    }

    class QuotaInfo
    {
        // Agent empty for Claude profiles
        public string Profile = "", Model = "", Tail = "", Agent = "", Who = "";
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
        int quotaVersion, quotaBuilt = -1, quotaSig;
        long quotaMinute;
        bool quotaEnglish;
        Action actQuotaOpen;
        float quotaToastLevel;
        // Column starts, shared by stacked lines
        readonly float[] quotaCols = new float[3];
        float quotaWhoW;
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
            string agent = Json.Str(ev.Data, "agent");
            if (agent != "" && agent != "codex" && agent != "antigravity" && agent != "copilot") return;
            // Claude and other agents switch apart
            if (!settings.GetBool(agent.Length > 0 ? StatusSetup.AgentsKey : "agent.quota", true)) return;
            string profile = agent.Length > 0 ? AgentAdapters.Label(agent) : StatusLine.SafeName(CleanText(Json.Str(ev.Data, "profile"), 24));
            if (agent.Length == 0 && Json.Str(ev.Data, "profile").Length == 0) profile = "";
            var q = quotas.Find(x => x.Profile == profile);
            if (q == null)
            {
                // Few profiles; oldest goes
                if (quotas.Count >= 6)
                {
                    var oldest = quotas[0];
                    foreach (var x in quotas) if (x.AtUtc < oldest.AtUtc) oldest = x;
                    quotas.Remove(oldest);
                }
                q = new QuotaInfo { Profile = profile, Agent = agent };
                quotas.Add(q);
                // Claude first, then other agents
                quotas.Sort((a, b) => a.Agent.Length != b.Agent.Length ? a.Agent.Length.CompareTo(b.Agent.Length) : string.CompareOrdinal(a.Profile, b.Profile));
            }
            var now = DateTime.UtcNow;
            q.Model = CleanText(Json.Str(ev.Data, "model"), 40);
            q.AtUtc = now;
            double lo = (now.AddDays(-8) - Epoch).TotalSeconds, hi = (now.AddDays(40) - Epoch).TotalSeconds;
            for (int i = 0; i < QuotaKeys.Length; i++)
            {
                var src = Json.Obj(ev.Data, agent.Length > 0 ? "w" + i : QuotaKeys[i]);
                q.W[i].Kind = CleanText(Json.Str(src, "kind"), 12);
                q.W[i].Minutes = (int)Math.Max(0, Math.Min(100000, Json.Num(src, "minutes", 0)));
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
            if (level >= 95) EyesReact(EyeMood.Worried, 0);
            t.Glyph = IcoGauge;
            quotaToastLevel = w.Pct / 100f;
            t.Title = Lang.F("Kuota {0} {1}", QuotaWho(q) + " " + WindowLabel(w, i), Ink.Percent((int)w.Pct));
            t.Detail = QuotaReset(w.ResetUtc);
            t.Data = actQuotaOpen;
        }

        // Which AI the line belongs to
        static string QuotaWho(QuotaInfo q)
        {
            if (q.Agent.Length > 0) return q.Profile;
            return q.Profile.Length > 0 ? "Claude · " + q.Profile : "Claude";
        }

        static string QuotaLabel(int i) { return i == 0 ? Lang.T("5 jam") : i == 1 ? Lang.T("7 hari") : Lang.T("Batas"); }

        // Claude windows are fixed; others carry theirs
        static string WindowLabel(QuotaWindow w, int i)
        {
            if (w.Kind == "gemini") return "Gemini";
            if (w.Kind == "other") return Lang.T("Model lain");
            if (w.Kind == "premium") return "Premium";
            if (w.Minutes == 300) return QuotaLabel(0);
            if (w.Minutes == 10080) return QuotaLabel(1);
            if (w.Minutes > 0) return w.Minutes % 1440 == 0 ? Lang.F("{0} hari", w.Minutes / 1440) : Lang.F("{0} jam", Math.Max(1, w.Minutes / 60));
            return QuotaLabel(i);
        }

        static string QuotaReset(DateTime utc)
        {
            var local = utc.ToLocalTime();
            if (local.Date == DateTime.Today) return Lang.T("reset ") + Fmt.Hm(local);
            // Past a week a weekday misleads
            if ((local.Date - DateTime.Today).TotalDays >= 6) return Lang.T("reset ") + local.ToString("d MMM", Fmt.Id) + " " + Fmt.Hm(local);
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
            return Math.Min(4, n);
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
            var now = DateTime.UtcNow;
            foreach (var q in quotas)
            {
                q.Who = QuotaWho(q);
                for (int i = 0; i < q.W.Length; i++)
                {
                    var w = q.W[i];
                    if (!w.Has) continue;
                    w.Label = WindowLabel(w, i);
                    w.Stale = now >= w.ResetUtc;
                    w.PctText = Ink.Percent((int)Math.Floor(w.Pct));
                    w.ResetText = w.Stale ? Lang.T("sudah reset") : QuotaReset(w.ResetUtc);
                }
                q.Tail = Lang.T("diperbarui ") + Fmt.Ago(q.AtUtc);
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
                if (!QuotaShown(q) || shown >= 4) continue;
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
                AddHit(r, id, actQuotaOpen, null, HomeMenuAct(HomeCard.Quota));
                if (Hover(id)) p.FillRound(r, Ink.RadiusControl, Pressed(id) ? p.T.FillPressed : p.T.FillHover);
                QuotaLine(q, x + 16, r.Right - 12, r.Y + r.Height / 2, q.Tail, ref slot);
                shown++;
            }
        }

        // Widest item per window across lines
        void QuotaColumns(bool notable)
        {
            var lf = Ink.Caption;
            var pf = Ink.SmallSemi(11.5f);
            quotaWhoW = 0;
            foreach (var q in quotas)
                if (QuotaShown(q) && (!notable || QuotaPeak(q) >= 70)) quotaWhoW = Math.Max(quotaWhoW, p.Measure(q.Who, pf));
            for (int i = 0; i < 3; i++)
            {
                float widest = 0;
                foreach (var q in quotas)
                {
                    var w = q.W[i];
                    if (!w.Has || !QuotaShown(q) || (notable && QuotaPeak(q) < 70)) continue;
                    float iw = 20 + p.Measure(w.Label, lf) + 5 + (w.Stale ? 0 : p.Measure(w.PctText, pf) + 7) + p.Measure(w.ResetText, lf);
                    widest = Math.Max(widest, iw);
                }
                quotaCols[i] = widest;
            }
        }

        // AI name, then ring, label, percent, reset; tail right
        void QuotaLine(QuotaInfo q, float cx, float right, float cy, string tail, ref int slot)
        {
            var lf = Ink.Caption;
            var pf = Ink.SmallSemi(11.5f);
            float x = cx - 6.5f;
            p.Text(q.Who, pf, p.T.Text, x, cy, quotaWhoW + 2, StringAlignment.Near);
            x += quotaWhoW + 14;
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
                p.Text(w.Label, lf, hot ? p.T.Text : p.T.Sub, tx, cy, 200, StringAlignment.Near);
                tx += p.Measure(w.Label, lf) + 5;
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

        // Any agent's quota watched: one switch for all
        bool QuotaOn { get { return settings.GetBool("agent.quota", true) || settings.GetBool(StatusSetup.AgentsKey, true); } }

        // Settings row: each agent's first window
        string QuotaNote()
        {
            if (!QuotaOn) return Lang.T("Claude, Codex, Antigravity, dan Copilot, dari status line dan lognya");
            QuotaTexts();
            var parts = new List<string>();
            foreach (var q in quotas)
            {
                if (!QuotaShown(q)) continue;
                foreach (var w in q.W)
                {
                    if (!w.Has) continue;
                    parts.Add(q.Who + " " + w.Label + " " + (w.Stale ? Lang.T("sudah reset") : w.PctText));
                    break;
                }
            }
            return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : Lang.T("Muncul setelah sesi agent berikutnya");
        }

        // Claude and Antigravity by status line, Codex and Copilot by log
        void SetQuota(bool on)
        {
            settings.Set("agent.quota", on);
            settings.Set(StatusSetup.AgentsKey, on);
            settings.Save();
            string err = null;
            try { if (HooksInstalled()) { if (on) err = StatusSetup.Install(); else StatusSetup.Remove(); } }
            catch (Exception ex) { Log.Write("statusline setup: " + ex.Message); err = Fmt.OneLine(ex.Message, 60); }
            try { if (AgentSetup.Installed("antigravity")) { if (on) StatusSetup.AgyInstall(); else StatusSetup.AgyRemove(); } }
            catch (Exception ex) { Log.Write("agy statusline: " + ex.Message); }
            if (on) { LogQuota("codex"); LogQuota("copilot"); }
            else { quotas.Clear(); quotaVersion++; }
            if (err != null) ShowInfo(IcoGauge, Lang.T("Kuota belum dipantau"), err, true);
            else ShowInfo(IcoGauge, on ? Lang.T("Kuota agent dipantau") : Lang.T("Kuota agent dilepas"), "", true);
            Kick();
        }

        readonly HashSet<string> logReading = new HashSet<string>();

        // Codex and Copilot: quota in their session logs
        void LogQuota(string agent)
        {
            if (logReading.Contains(agent) || !settings.GetBool(StatusSetup.AgentsKey, true) || !AgentSetup.Installed(agent)) return;
            logReading.Add(agent);
            string home = Path.GetDirectoryName(AgentSetup.ConfigFile(agent));
            new Thread(delegate ()
            {
                Dictionary<string, object> usage = null;
                try { usage = agent == "codex" ? CodexLog.Usage(Path.Combine(home, "sessions")) : CopilotLog.Usage(Path.Combine(home, "session-state")); }
                catch (Exception ex) { Log.Write(agent + " quota: " + ex.Message); }
                Post(() =>
                {
                    logReading.Remove(agent);
                    if (usage != null) OnUsage(new HookEvent { Agent = "usage", Name = "Usage", Data = usage });
                });
            }) { IsBackground = true, Name = agent + " quota" }.Start();
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
