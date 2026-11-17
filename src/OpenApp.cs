using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tepi
{
    // Open an installed app by name, voice or chat
    partial class Island
    {
        static readonly Regex OpenAsk = new Regex(@"^(?:buka(?:in|kan)?|open|launch|jalankan|jalanin)\s+(?:(?:aplikasi|app)\s+)?(?<app>.+)$", RegexOptions.IgnoreCase);
        // Start lists these beside the real apps
        static readonly Regex NotAnApp = new Regex(@"\b(uninstall|readme|read me|release notes|help|documentation|manual|website|license|changelog)\b", RegexOptions.IgnoreCase);

        // Generic names that differ per language
        // "=": built-in fallback
        static readonly string[][] AppAliases =
        {
            new[] { "kalkulator calculator", "Calculator", "Kalkulator", "=calc.exe" },
            new[] { "pengaturan setelan settings setting", "Settings", "Pengaturan", "=ms-settings:" },
            new[] { "fileexplorer explorer penjelajahfile filemanager", "File Explorer", "Penjelajah File", "=explorer.exe" },
            new[] { "terminal cmd commandprompt", "Terminal", "Windows Terminal", "Command Prompt", "Prompt Perintah" },
            new[] { "taskmanager pengelolatugas", "Task Manager", "Pengelola Tugas", "=taskmgr.exe" },
            new[] { "kamera camera", "Camera", "Kamera" },
            new[] { "kalender calendar", "Calendar", "Kalender" },
            new[] { "foto photos galeri gallery", "Photos", "Foto" },
            new[] { "store microsoftstore toko", "Microsoft Store" },
            new[] { "vscode vskode", "Visual Studio Code" },
            new[] { "wa", "WhatsApp" },
        };

        static readonly object appGate = new object();
        static List<KeyValuePair<string, string>> appCache;
        static DateTime appCacheAt;

        // Installed apps only, never a path or command
        static List<KeyValuePair<string, string>> InstalledApps()
        {
            lock (appGate)
            {
                if (appCache == null || DateTime.UtcNow - appCacheAt > TimeSpan.FromMinutes(2))
                {
                    var all = new List<KeyValuePair<string, string>>();
                    foreach (var a in ShellApp.AllApps()) if (!NotAnApp.IsMatch(a.Key)) all.Add(a);
                    appCache = all;
                    appCacheAt = DateTime.UtcNow;
                }
                return appCache;
            }
        }

        static string Squash(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]", ""); }

        // Shared with Start search; past 2 means far
        static int Distance(string a, string b) { return AppMatch.Distance(a, b, 2); }


        // Exact, prefix, contains, then near typos
        static KeyValuePair<string, string>? FindApp(string spoken)
        {
            string want = Squash(spoken);
            if (want.Length < 2) return null;
            int slack = want.Length >= 7 ? 2 : want.Length >= 4 ? 1 : 0;
            KeyValuePair<string, string>? best = null;
            int bestRank = 9, bestLen = int.MaxValue;
            foreach (var app in InstalledApps())
            {
                string name = Squash(app.Key);
                int rank = name == want ? 0 : name.StartsWith(want) ? 1 : name.Contains(want) ? 2 : 9;
                if (rank == 9 && slack > 0)
                {
                    if (Distance(want, name) <= slack) rank = 3;
                    else foreach (var word in app.Key.Split(' ')) if (Squash(word).Length >= 4 && Distance(want, Squash(word)) <= slack) { rank = 4; break; }
                }
                if (rank == 9) continue;
                if (rank < bestRank || (rank == bestRank && name.Length < bestLen))
                {
                    best = app;
                    bestRank = rank;
                    bestLen = name.Length;
                }
            }
            return best;
        }

        // "browser": whatever opens links
        internal static bool DefaultBrowser(out string target, out string name)
        {
            target = name = null;
            try
            {
                string progId;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                    progId = k == null ? null : k.GetValue("ProgId") as string;
                if (string.IsNullOrEmpty(progId)) return false;
                using (var app = Registry.ClassesRoot.OpenSubKey(progId + @"\Application"))
                {
                    string aumid = app == null ? null : app.GetValue("AppUserModelId") as string;
                    name = app == null ? null : app.GetValue("ApplicationName") as string;
                    if (!string.IsNullOrEmpty(aumid)) target = ShellApp.AppsFolderOf(aumid);
                }
                if (target == null)
                    using (var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    {
                        string line = cmd == null ? null : cmd.GetValue("") as string;
                        var m = line == null ? Match.Empty : Regex.Match(line, "^\\s*\"([^\"]+\\.exe)\"|^\\s*(\\S+\\.exe)", RegexOptions.IgnoreCase);
                        string exe = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
                        if (exe != null && File.Exists(exe)) target = exe;
                    }
                if (target == null) return false;
                // ApplicationName may be a resource string
                if (string.IsNullOrEmpty(name) || name.StartsWith("@"))
                    name = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? FileVersionInfo.GetVersionInfo(target).FileDescription : "Browser";
                if (string.IsNullOrEmpty(name)) name = "Browser";
                return true;
            }
            catch (Exception ex) { Log.Write("browser bawaan: " + ex.Message); return false; }
        }

        // Display name and launch target, or false
        internal static bool ResolveApp(string spoken, out string target, out string name)
        {
            target = name = null;
            string want = Squash(spoken);
            if (want == "browser" || want == "peramban" || want == "internet" || want == "webbrowser")
                return DefaultBrowser(out target, out name);
            foreach (var row in AppAliases)
            {
                if (Array.IndexOf(row[0].Split(' '), want) < 0) continue;
                for (int i = 1; i < row.Length; i++)
                {
                    if (row[i].StartsWith("=")) { target = row[i].Substring(1); name = row[1]; return true; }
                    foreach (var app in InstalledApps())
                        if (Squash(app.Key) == Squash(row[i])) { target = ShellApp.AppsFolderOf(app.Value); name = app.Key; return true; }
                }
            }
            var found = FindApp(spoken);
            if (found == null) return false;
            target = ShellApp.AppsFolderOf(found.Value.Value);
            name = found.Value.Key;
            return true;
        }

        ChatAct OpenApp(string spoken)
        {
            string target, name;
            if (!ResolveApp(spoken, out target, out name)) return new ChatAct { Glyph = IcoFail, Text = Lang.F("Aplikasi tidak ketemu: {0}", spoken.Trim()) };
            ShellApp.Launch(target, false);
            return new ChatAct { Glyph = Ink.IcoOpen, Text = Lang.F("Membuka {0}", name) };
        }

        // No match: let the AI answer
        ChatMsg TryOpen(string raw)
        {
            var m = OpenAsk.Match(Spoken(raw));
            string target, name;
            if (!m.Success || !ResolveApp(m.Groups["app"].Value, out target, out name)) return null;
            var msg = new ChatMsg();
            msg.Acts.Add(OpenApp(m.Groups["app"].Value));
            return msg;
        }

        // Names the English recognizer can say
        internal static List<KeyValuePair<string, string>> SpeakableApps()
        {
            var said = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> add = (words, app) =>
            {
                words = Regex.Replace(words.Replace("&", " and ").Replace("+", " plus "), @"\s+", " ").Trim();
                if (words.Length < 2 || words.Split(' ').Length > 4 || !Regex.IsMatch(words, @"^[A-Za-z][A-Za-z' .-]*$") || said.ContainsKey(words)) return;
                said[words] = app;
            };
            foreach (var app in InstalledApps())
            {
                string plain = Regex.Replace(app.Key, @"\([^)]*\)", "").Trim();
                add(plain, app.Key);
                // "Google Chrome" is also just "Chrome"
                var vendor = Regex.Match(plain, @"^(?:Microsoft|Google|Adobe|Mozilla|Apple)\s+(.+)$");
                if (vendor.Success) add(vendor.Groups[1].Value, app.Key);
                if (said.Count >= 400) break;
            }
            foreach (var w in new[] { "browser", "web browser", "calculator", "settings", "file explorer", "terminal", "task manager", "camera", "calendar", "photos" }) add(w, w);
            return new List<KeyValuePair<string, string>>(said);
        }
    }
}
