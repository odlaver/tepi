using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // Export, import, reset; secrets never travel
    partial class Island
    {
        // Preferences only; tokens, ports, pairing and state stay out
        static readonly HashSet<string> Portable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "material", "theme", "lyrics", "lyrics.online", "visualizer", "music.view", "notif", "notif.content", "notif.apps", "notif.vip",
            "agent.wait", "agent.quota", "update.check", "accent", "monitor", "focus.break", "dock", "position", "panel.width", "panel.tabs",
            "panel.first", "panel.open", "panel.delay", "tabs.style", "mini", "mini.notif", "island.eyes", "island.follow", "island.except",
            "prompter.wpm", "prompter.size", "prompter.mirror", "prompter.width", "prompter.height", "prompter.bg", "lang", "capture.show",
            "a11y.announce", "clipboard", "downloads", "class.island", "shelf", "shelf.keep", "shelf.expire", "convert", "shots", "drives",
            "meeting", "keys.caps", "keys.num", "keys.lang", "battery.unplug", "note.to", "dnd", "dnd.follow", "motion.reduce", "motion.feel",
            "pray", "pray.city", "pray.before", "drink", "drink.every", "drink.goal", "eyes", "stretch", "stretch.every", "theme.pack",
            "theme.voice", "weather", "clock.24", "look.custom", "look.bg", "look.text", "look.accent", "sound.agent", "phone.notifs", "phone.otp",
        };

        // Kept on reset: identity, links, internal state
        static readonly string[] KeepOnReset = { "agent.token", "agent.port", "phone", "phone.port", "kulia.url", "kulia.key", "settings.v", "tabs.convert" };

        static bool IsPortable(string key) { return Portable.Contains(key) || key.StartsWith("key.", StringComparison.OrdinalIgnoreCase); }

        public int ExportSettings(string path)
        {
            var keys = settings.Keys();
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder("# Tepi " + AppVersion + "\r\n");
            int n = 0;
            foreach (var k in keys)
            {
                if (!IsPortable(k)) continue;
                sb.Append(k).Append('=').Append(settings.Get(k, "")).Append("\r\n");
                n++;
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return n;
        }

        // Known keys only, values trimmed and capped
        public int ImportSettings(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) throw new InvalidDataException("too large");
            int n = 0;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (!IsPortable(key) || value.Length > 2000) continue;
                var clean = new StringBuilder();
                foreach (char c in value) if (!char.IsControl(c)) clean.Append(c);
                settings.Set(key, clean.ToString());
                n++;
            }
            if (n > 0) settings.Save();
            return n;
        }

        public void ResetSettings()
        {
            foreach (var k in settings.Keys())
                if (Array.IndexOf(KeepOnReset, k) < 0) settings.Remove(k);
            settings.Save();
        }

        // New process waits for this one, like an update
        public void RestartTepi()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = false }); }
            catch (Exception ex) { Log.Write("restart: " + ex.Message); return; }
            Close();
        }
    }
}
