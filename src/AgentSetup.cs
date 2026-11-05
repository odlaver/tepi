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
        public static readonly string[] Ids = { "codex", "gemini", "antigravity", "copilot" };

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
                case "copilot":
                    return new[] { new Hook("sessionStart", false, false), new Hook("userPromptSubmitted", false, false), new Hook("preToolUse", false, false),
                                   new Hook("postToolUse", false, false), new Hook("agentStop", false, false), new Hook("sessionEnd", false, false) };
                default:
                    return new[] { new Hook("SessionStart", false, false), new Hook("PreInvocation", false, false), new Hook("PreToolUse", true, false),
                                   new Hook("PostToolUse", true, false), new Hook("Stop", false, false) };
            }
        }

        public static string Label(string id)
        {
            return id == "codex" ? "Codex" : id == "gemini" ? "Gemini CLI" : id == "copilot" ? "Copilot CLI" : "Antigravity";
        }

        static string Home { get { return Settings.Home; } }

        public static string ConfigFile(string id)
        {
            if (id == "codex")
            {
                string ch = Environment.GetEnvironmentVariable("CODEX_HOME");
                bool own = !string.IsNullOrEmpty(ch) && !Settings.FakeHome;
                return Path.Combine(own ? ch : Path.Combine(Home, ".codex"), "hooks.json");
            }
            if (id == "gemini") return Path.Combine(Home, ".gemini", "settings.json");
            if (id == "copilot")
            {
                string ch = Environment.GetEnvironmentVariable("COPILOT_HOME");
                bool own = !string.IsNullOrEmpty(ch) && !Settings.FakeHome;
                return Path.Combine(own ? ch : Path.Combine(Home, ".copilot"), "settings.json");
            }
            return Path.Combine(Home, ".gemini", "config", "hooks.json");
        }

        public static bool Present(string id)
        {
            if (id == "codex") return Directory.Exists(Path.GetDirectoryName(ConfigFile(id)));
            if (id == "gemini") return File.Exists(ConfigFile(id)) || OnPath("gemini");
            if (id == "copilot") return Directory.Exists(Path.GetDirectoryName(ConfigFile(id))) || OnPath("copilot");
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
        internal static string Exe()
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
            // Copilot: silence keeps its own permissions
            if (id == "copilot") return "cmd /c " + relay + " --event " + h.Event;
            // Antigravity adds cmd /c and escapes quotes; {} denies tools
            return relay + " --json --event " + h.Event + (h.Event == "PreToolUse" ? " || exit /b 0" : " || echo {}");
        }

        // Rewrite stale Antigravity hooks
        public static void Refresh()
        {
            try
            {
                var root = HookSetup.Load(ConfigFile("antigravity"));
                if (root != null && root.ContainsKey("tepi") && !Installed("antigravity")) Install("antigravity");
                if (Installed("antigravity") && new Settings().GetBool(StatusSetup.AgentsKey, true)) StatusSetup.AgyInstall();
            }
            catch (Exception ex) { Log.Write("agent refresh: " + ex.Message); }
        }

        static Dictionary<string, object> Handler(string id, Hook h)
        {
            var d = new Dictionary<string, object>();
            if (id == "copilot")
            {
                // Copilot picks the shell field itself
                d["type"] = "command";
                d["powershell"] = Command(id, h);
                d["bash"] = Exe().Replace('\\', '/') + " --relay copilot --event " + h.Event;
                d["timeoutSec"] = 10;
                return d;
            }
            if (id == "gemini") d["name"] = "tepi";
            d["type"] = "command";
            d["command"] = Command(id, h);
            // Gemini counts milliseconds; the others seconds
            d["timeout"] = id == "gemini" ? 5000 : h.Reply ? 130 : 10;
            return d;
        }

        static bool IsOurs(object handler, string id)
        {
            foreach (var key in new[] { "command", "powershell", "bash" })
                if (Json.Str(handler, key).Contains(" --relay " + id)) return true;
            return false;
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
                    bool flat = id == "copilot" || (id == "antigravity" && !h.Tool);
                    var handlers = flat ? new[] { item } : HookSetup.AsList(HookSetup.Get(item, "hooks"));
                    foreach (var x in handlers)
                        if (Json.Str(x, id == "copilot" ? "powershell" : "command") == Command(id, h)) found = true;
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
            if (id == "antigravity" && Exe().IndexOf('"') >= 0) return Lang.T("Pindahkan Tepi.exe ke folder tanpa spasi");
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
                // Quota comes through its status line
                if (new Settings().GetBool(StatusSetup.AgentsKey, true)) StatusSetup.AgyInstall();
            }
            else
            {
                var hooks = Json.Obj(root, "hooks");
                if (hooks == null) root["hooks"] = hooks = new Dictionary<string, object>();
                foreach (var h in HooksFor(id))
                {
                    var groups = Strip(hooks, h.Event, id);
                    // Copilot lists handlers directly
                    if (id == "copilot") { groups.Add(Handler(id, h)); hooks[h.Event] = groups; continue; }
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
            if (id == "antigravity") { root.Remove("tepi"); StatusSetup.AgyRemove(); }
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
            // Leave shared settings files in place
            if (root.Count == 0 && id != "gemini" && id != "copilot") File.Delete(file);
            else HookSetup.Save(file, root);
        }

        static List<object> Strip(Dictionary<string, object> hooks, string ev, string id)
        {
            var kept = new List<object>();
            foreach (var gr in HookSetup.AsList(HookSetup.Get(hooks, ev)))
            {
                bool ours = id == "copilot" && IsOurs(gr, id);
                foreach (var x in HookSetup.AsList(HookSetup.Get(gr, "hooks")))
                    if (IsOurs(x, id)) ours = true;
                if (!ours) kept.Add(gr);
            }
            return kept;
        }
    }
}
