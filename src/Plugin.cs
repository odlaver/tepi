using System;
using System.Collections.Generic;
using System.Text;

namespace Tepi
{
    // Scripts talk to the island: Tepi.exe --notify, or POST /notify
    partial class Island
    {
        DateTime lastPluginUtc;

        static string CleanText(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (char ch in Fmt.StripHidden(s))
            {
                if (char.IsControl(ch)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(ch);
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim();
        }

        void OnPlugin(HookEvent ev)
        {
            string title = CleanText(Json.Str(ev.Data, "title"), 80), detail = CleanText(Json.Str(ev.Data, "detail"), 160);
            string id = CleanText(Json.Str(ev.Data, "id"), 40);
            if (title.Length == 0) return;
            int progress;
            if (int.TryParse(Json.Str(ev.Data, "progress"), out progress) && id.Length > 0)
            {
                string key = "plugin:" + id;
                Download d;
                if (!downloads.TryGetValue(key, out d))
                {
                    int bars = 0;
                    foreach (var x in downloads.Values) if (x.Percent >= 0) bars++;
                    if (bars >= 8) return;
                    downloads[key] = d = new Download { Partial = key };
                }
                d.Name = title;
                d.Percent = Math.Max(0, Math.Min(100, progress));
                d.Seen = DateTime.UtcNow;
                if (d.Percent >= 100)
                {
                    downloads.Remove(key);
                    if ((DateTime.UtcNow - lastPluginUtc).TotalSeconds >= 0.5) lastPluginUtc = DateTime.UtcNow;
                    else { Kick(); return; }
                    ShowInfo('', title, detail.Length > 0 ? detail : Lang.T("selesai"), true);
                }
                if (downloads.Count > 0 && !downloadTimer.Enabled) downloadTimer.Start();
                Kick();
                return;
            }
            // One toast per half second, so a loop cannot flood
            if ((DateTime.UtcNow - lastPluginUtc).TotalSeconds < 0.5) return;
            lastPluginUtc = DateTime.UtcNow;
            ShowInfo('', title, detail, true);
        }
    }

    // Tepi.exe --notify <title> [detail] [--progress 0-100] [--id name]
    static class NotifyCommand
    {
        public static int Run(string[] args)
        {
            var words = new List<string>();
            string progress = null, id = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--progress" && i + 1 < args.Length) progress = args[++i];
                else if (args[i] == "--id" && i + 1 < args.Length) id = args[++i];
                else words.Add(args[i]);
            }
            if (words.Count == 0) return 2;
            var o = new Dictionary<string, object> { { "hook_event_name", "Notify" }, { "title", words[0] } };
            if (words.Count > 1) o["detail"] = string.Join(" ", words.GetRange(1, words.Count - 1).ToArray());
            if (progress != null) o["progress"] = progress;
            if (id != null) o["id"] = id;
            if (!Relay.Running()) return 1;
            Relay.Post("/notify", Encoding.UTF8.GetBytes(Json.Encode(o)), 2000);
            return 0;
        }
    }
}
