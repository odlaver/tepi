using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tepi
{
    // Tepi http hooks in every profile
    static class HookSetup
    {
        static readonly string[] Events = { "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Notification", "Stop", "StopFailure", "SessionEnd" };

        static string Url(int port) { return "http://127.0.0.1:" + port + "/hook"; }

        public static List<string> Profiles()
        {
            var home = Settings.Home;
            var list = new List<string>();
            if (!Directory.Exists(home)) return list;
            foreach (var dir in Directory.GetDirectories(home, ".claude*"))
            {
                string name = Path.GetFileName(dir);
                if (name != ".claude" && !name.StartsWith(".claude-")) continue;
                if (File.Exists(Path.Combine(dir, "settings.json")) || Directory.Exists(Path.Combine(dir, "projects"))) list.Add(dir);
            }
            return list;
        }

        public static bool Installed(int port)
        {
            var profiles = Profiles();
            if (profiles.Count == 0) return false;
            foreach (var dir in profiles)
            {
                var root = Load(Path.Combine(dir, "settings.json"));
                if (root == null || !HasOurs(root, port)) return false;
            }
            return true;
        }

        public static string Install(int port, string token)
        {
            int done = 0;
            foreach (var dir in Profiles())
            {
                string file = Path.Combine(dir, "settings.json");
                var root = File.Exists(file) ? Load(file) : new Dictionary<string, object>();
                if (root == null) return Lang.T("settings.json di ") + Path.GetFileName(dir) + Lang.T(" tidak terbaca");
                Backup(file);
                var hooks = Hooks(root, true);
                foreach (var ev in Events)
                {
                    var groups = Strip(hooks, ev, port);
                    groups.Add(new Dictionary<string, object> { { "hooks", new List<object> { Handler(ev, port, token) } } });
                    hooks[ev] = groups;
                }
                Save(file, root);
                done++;
            }
            return done == 0 ? Lang.T("Profil Claude Code tidak ditemukan") : null;
        }

        public static void Remove(int port)
        {
            foreach (var dir in Profiles())
            {
                string file = Path.Combine(dir, "settings.json");
                var root = Load(file);
                if (root == null || !HasOurs(root, port)) continue;
                Backup(file);
                var hooks = Hooks(root, false);
                foreach (var ev in Events)
                {
                    if (!hooks.ContainsKey(ev)) continue;
                    var groups = Strip(hooks, ev, port);
                    if (groups.Count == 0) hooks.Remove(ev);
                    else hooks[ev] = groups;
                }
                if (hooks.Count == 0) root.Remove("hooks");
                Save(file, root);
            }
        }

        // Stop/SessionEnd use curl: silent when down
        static Dictionary<string, object> Handler(string ev, int port, string token)
        {
            if (ev == "Stop" || ev == "StopFailure" || ev == "SessionEnd")
                return new Dictionary<string, object>
                {
                    { "type", "command" },
                    { "shell", "bash" },
                    { "command", "curl -s -m 3 -H 'Expect:' -H 'X-Tepi-Token: " + token + "' -H 'Content-Type: application/json' --data-binary @- " + Url(port) + "; exit 0" },
                    { "timeout", 5 },
                };
            return new Dictionary<string, object>
            {
                { "type", "http" },
                { "url", Url(port) },
                { "headers", new Dictionary<string, object> { { "X-Tepi-Token", token } } },
                { "timeout", ev == "PermissionRequest" ? 130 : 5 },
            };
        }

        static bool IsOurs(object handler, int port)
        {
            return Json.Str(handler, "url") == Url(port) || Json.Str(handler, "command").Contains(" " + Url(port));
        }

        static bool HasOurs(Dictionary<string, object> root, int port)
        {
            var hooks = Hooks(root, false);
            foreach (var ev in Events)
            {
                object groups;
                if (!hooks.TryGetValue(ev, out groups)) return false;
                bool found = false;
                foreach (var gr in AsList(groups))
                    foreach (var h in AsList(Get(gr, "hooks")))
                        if (IsOurs(h, port)) found = true;
                if (!found) return false;
            }
            return true;
        }

        // Event groups minus Tepi's own
        static List<object> Strip(Dictionary<string, object> hooks, string ev, int port)
        {
            var kept = new List<object>();
            object groups;
            if (!hooks.TryGetValue(ev, out groups)) return kept;
            foreach (var gr in AsList(groups))
            {
                bool ours = false;
                foreach (var h in AsList(Get(gr, "hooks")))
                    if (IsOurs(h, port)) ours = true;
                if (!ours) kept.Add(gr);
            }
            return kept;
        }

        static Dictionary<string, object> Hooks(Dictionary<string, object> root, bool create)
        {
            object h;
            var hooks = root.TryGetValue("hooks", out h) ? h as Dictionary<string, object> : null;
            if (hooks == null)
            {
                hooks = new Dictionary<string, object>();
                if (create) root["hooks"] = hooks;
            }
            return hooks;
        }

        internal static object Get(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        internal static IEnumerable<object> AsList(object o)
        {
            var e = o as IEnumerable;
            if (e == null || o is string || o is IDictionary) yield break;
            foreach (var x in e) yield return x;
        }

        internal static Dictionary<string, object> Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                return Json.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception ex) { Log.Write("hook setup read: " + ex.Message); return null; }
        }

        internal static void Backup(string file)
        {
            string bak = file + ".bak-tepi";
            if (File.Exists(file) && !File.Exists(bak)) File.Copy(file, bak);
        }

        internal static void Save(string file, Dictionary<string, object> root)
        {
            var sb = new StringBuilder();
            Write(sb, root, 0);
            sb.Append('\n');
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        // Two-space JSON like Claude Code writes
        static void Write(StringBuilder sb, object v, int depth)
        {
            string pad = new string(' ', depth * 2), inner = new string(' ', depth * 2 + 2);
            var dict = v as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    sb.Append(inner);
                    Str(sb, kv.Key.ToString());
                    sb.Append(": ");
                    Write(sb, kv.Value, depth + 1);
                    sb.Append(++i < dict.Count ? ",\n" : "\n");
                }
                sb.Append(pad).Append('}');
                return;
            }
            if (v is string) { Str(sb, (string)v); return; }
            if (v == null) { sb.Append("null"); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            var list = v as IEnumerable;
            if (list != null)
            {
                var items = new List<object>();
                foreach (var x in list) items.Add(x);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(inner);
                    Write(sb, items[i], depth + 1);
                    sb.Append(i + 1 < items.Count ? ",\n" : "\n");
                }
                sb.Append(pad).Append(']');
                return;
            }
            sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
