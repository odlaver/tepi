using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // Relay hooks: Codex, Gemini CLI, Antigravity
    static class AgentSetup
    {
        public static readonly string[] Ids = { "codex", "gemini", "antigravity" };

        class Hook
        {
            public string Event;
            public bool Tool, Reply;
            public Hook(string ev, bool tool, bool reply) { Event = ev; Tool = tool; Reply = reply; }
        }

        static Hook[] HooksFor(string id)
        {
            switch (id)
            {
                case "codex":
                    return new[] { new Hook("SessionStart", false, false), new Hook("UserPromptSubmit", false, false), new Hook("PreToolUse", true, false),
                                   new Hook("PermissionRequest", true, true), new Hook("Stop", false, false) };
                case "gemini":
                    return new[] { new Hook("SessionStart", false, false), new Hook("BeforeAgent", false, false), new Hook("BeforeTool", true, false),
                                   new Hook("AfterTool", true, false), new Hook("Notification", false, false), new Hook("AfterAgent", false, false),
                                   new Hook("SessionEnd", false, false) };
                default:
                    return new[] { new Hook("SessionStart", false, false), new Hook("PreInvocation", false, false), new Hook("PreToolUse", true, false),
                                   new Hook("PostToolUse", true, false), new Hook("Stop", false, false) };
            }
        }

        public static string Label(string id)
        {
            return id == "codex" ? "Codex" : id == "gemini" ? "Gemini CLI" : "Antigravity";
        }

        static string Home
        {
            get { return Environment.GetEnvironmentVariable("TEPI_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }

        public static string ConfigFile(string id)
        {
            if (id == "codex")
            {
                string ch = Environment.GetEnvironmentVariable("CODEX_HOME");
                bool own = !string.IsNullOrEmpty(ch) && Environment.GetEnvironmentVariable("TEPI_HOME") == null;
                return Path.Combine(own ? ch : Path.Combine(Home, ".codex"), "hooks.json");
            }
            if (id == "gemini") return Path.Combine(Home, ".gemini", "settings.json");
            return Path.Combine(Home, ".gemini", "config", "hooks.json");
        }

        public static bool Present(string id)
        {
            if (id == "codex") return Directory.Exists(Path.GetDirectoryName(ConfigFile(id)));
            if (id == "gemini") return File.Exists(ConfigFile(id)) || OnPath("gemini");
            string g = Path.Combine(Home, ".gemini");
            return Directory.Exists(Path.Combine(g, "config")) || Directory.Exists(Path.Combine(g, "antigravity-cli")) || Directory.Exists(Path.Combine(g, "antigravity-ide"));
        }

        static bool OnPath(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                foreach (var ext in new[] { ".cmd", ".exe", ".ps1" })
                {
                    try { if (File.Exists(Path.Combine(dir.Trim(), name + ext))) return true; }
                    catch (ArgumentException) { }
                }
            }
            return false;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

        // 8.3 path: no quotes for Codex
        static string Exe()
        {
            string path = System.Reflection.Assembly.GetEntryAssembly().Location;
            if (path.IndexOf(' ') < 0) return path;
            var sb = new StringBuilder(520);
            if (GetShortPathName(path, sb, sb.Capacity) > 0 && sb.ToString().IndexOf(' ') < 0) return sb.ToString();
            return "\"" + path + "\"";
        }

        // PowerShell won't wait for GUI exes
        static string Command(string id, Hook h)
        {
            string relay = Exe() + " --relay " + id;
            if (id == "codex") return relay + (h.Reply ? " --reply" : "");
            if (id == "gemini") return "cmd /c " + relay + " --json";
            // Antigravity blocks tools on failure: fallback
            return "cmd /c \"" + relay + " --json --event " + h.Event + " || echo {}\"";
        }

        static Dictionary<string, object> Handler(string id, Hook h)
        {
            var d = new Dictionary<string, object>();
            if (id == "gemini") d["name"] = "tepi";
            d["type"] = "command";
            d["command"] = Command(id, h);
            // Gemini counts milliseconds; the others seconds
            d["timeout"] = id == "gemini" ? 5000 : h.Reply ? 130 : 10;
            return d;
        }

        static bool IsOurs(object handler, string id)
        {
            return Json.Str(handler, "command").Contains(" --relay " + id);
        }

        public static bool Installed(string id)
        {
            var root = HookSetup.Load(ConfigFile(id));
            if (root == null) return false;
            var events = id == "antigravity" ? Json.Obj(root, "tepi") : Json.Obj(root, "hooks");
            if (events == null) return false;
            foreach (var h in HooksFor(id))
            {
                bool found = false;
                foreach (var item in HookSetup.AsList(HookSetup.Get(events, h.Event)))
                {
                    var handlers = h.Tool || id != "antigravity" ? HookSetup.AsList(HookSetup.Get(item, "hooks")) : new[] { item };
                    foreach (var x in handlers)
                        if (Json.Str(x, "command") == Command(id, h)) found = true;
                }
                if (!found) return false;
            }
            return true;
        }

        public static string Install(string id)
        {
            string file = ConfigFile(id);
            string dir = Path.GetDirectoryName(file);
            if (id == "codex" && !Directory.Exists(dir)) return Lang.T("Folder Codex tidak ditemukan");
            // cmd would read these as operators
            if (Exe().IndexOfAny("&|<>^%".ToCharArray()) >= 0) return Lang.T("Pindahkan Tepi.exe ke folder tanpa & | < > ^ %");
            Directory.CreateDirectory(dir);
            var root = File.Exists(file) ? HookSetup.Load(file) : new Dictionary<string, object>();
            if (root == null) return Path.GetFileName(file) + Lang.T(" tidak terbaca");
            HookSetup.Backup(file);
            if (id == "antigravity")
            {
                // Named group: replaced whole
                var group = new Dictionary<string, object>();
                foreach (var h in HooksFor(id))
                {
                    object handler = Handler(id, h);
                    group[h.Event] = new List<object> { h.Tool ? new Dictionary<string, object> { { "matcher", "*" }, { "hooks", new List<object> { handler } } } : handler };
                }
                root["tepi"] = group;
            }
            else
            {
                var hooks = Json.Obj(root, "hooks");
                if (hooks == null) root["hooks"] = hooks = new Dictionary<string, object>();
                foreach (var h in HooksFor(id))
                {
                    var groups = Strip(hooks, h.Event, id);
                    var entry = new Dictionary<string, object>();
                    if (h.Tool) entry["matcher"] = id == "gemini" ? ".*" : "*";
                    entry["hooks"] = new List<object> { Handler(id, h) };
                    groups.Add(entry);
                    hooks[h.Event] = groups;
                }
            }
            HookSetup.Save(file, root);
            return null;
        }

        public static void Remove(string id)
        {
            string file = ConfigFile(id);
            var root = HookSetup.Load(file);
            if (root == null) return;
            HookSetup.Backup(file);
            if (id == "antigravity") root.Remove("tepi");
            else
            {
                var hooks = Json.Obj(root, "hooks");
                if (hooks == null) return;
                foreach (var h in HooksFor(id))
                {
                    if (!hooks.ContainsKey(h.Event)) continue;
                    var groups = Strip(hooks, h.Event, id);
                    if (groups.Count == 0) hooks.Remove(h.Event);
                    else hooks[h.Event] = groups;
                }
                if (hooks.Count == 0) root.Remove("hooks");
            }
            // Leave Gemini's settings file in place
            if (root.Count == 0 && id != "gemini") File.Delete(file);
            else HookSetup.Save(file, root);
        }

        static List<object> Strip(Dictionary<string, object> hooks, string ev, string id)
        {
            var kept = new List<object>();
            foreach (var gr in HookSetup.AsList(HookSetup.Get(hooks, ev)))
            {
                bool ours = false;
                foreach (var x in HookSetup.AsList(HookSetup.Get(gr, "hooks")))
                    if (IsOurs(x, id)) ours = true;
                if (!ours) kept.Add(gr);
            }
            return kept;
        }
    }
}
