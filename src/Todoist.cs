using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Tepi
{
    // Todoist API v1 with a personal token
    class TodoistSource : Source
    {
        const string Api = "https://api.todoist.com";
        static readonly string FilePath = Path.Combine(Settings.Dir, "todoist.vault");
        public string Token = "";

        public TodoistSource() { Key = "todoist"; Name = "Todoist"; }

        public static TodoistSource Load()
        {
            try
            {
                string token = Json.Str(Json.Parse(Vault.Load(FilePath) ?? ""), "token");
                return token.Length > 0 ? new TodoistSource { Token = token } : null;
            }
            catch (Exception ex) { Log.Write("todoist load: " + ex.GetType().Name); return null; }
        }

        public void Save()
        {
            try { Vault.Save(FilePath, Json.Encode(new Dictionary<string, object> { { "token", Token } })); }
            catch (Exception ex) { Log.Write("todoist save: " + ex.GetType().Name); }
        }

        public static void Forget() { Vault.Delete(FilePath); }

        public override object Snapshot() { return Token; }

        static FetchResult Call(string token, string method, string path)
        {
            return Tepi.Fetch.Send(method, Api + path, new Dictionary<string, string> { { "Authorization", "Bearer " + token } }, null, Tepi.Fetch.JsonCap, 20000);
        }

        static object Parse(FetchResult r)
        {
            try { return Json.Parse(r.Text); }
            catch (Exception) { return null; }
        }

        static SourceResult Refused(FetchResult r)
        {
            var res = SourceResult.Http("Todoist", r);
            // retry_after may come on any error
            var extra = Json.Obj(Parse(r), "error_extra");
            int s = (int)Json.Num(extra, "retry_after", 0);
            if (s > 0 && !res.Stop) res.RetryAfter = Math.Min(s, 3600);
            return res;
        }

        // Valid token or a short reason
        public static string Check(string token)
        {
            token = (token ?? "").Trim();
            if (!TokenOk(token, 200)) return Lang.T("Token tidak lengkap");
            var r = Call(token, "GET", "/api/v1/projects?limit=1");
            return r.Ok ? null : Refused(r).Error;
        }

        public override SourceResult Fetch(object snap, DateTime nowUtc)
        {
            string token = (string)snap;
            var projects = new Dictionary<string, string>();
            string cursor = null;
            for (int i = 0; i < 5; i++)
            {
                if (Gone) return null;
                var r = Call(token, "GET", "/api/v1/projects?limit=200" + Next(cursor));
                if (r.Status == 401 || r.Status == 403 || r.Status == 429) return Refused(r);
                if (!r.Ok) break;
                var o = Parse(r);
                foreach (var p in Json.Arr(o, "results")) projects[Json.Str(p, "id")] = Clean(Json.Str(p, "name"), 60);
                cursor = Json.Str(o, "next_cursor");
                if (cursor.Length == 0) break;
            }
            // Overdue, today, and three weeks ahead
            string filter = "/api/v1/tasks/filter?query=" + Uri.EscapeDataString("overdue | today | 21 days") + "&limit=200";
            bool plain = false;
            var tasks = new List<KuliaTask>();
            cursor = null;
            for (int i = 0; i < 10; i++)
            {
                // Disconnected meanwhile: no more requests
                if (Gone) return null;
                var r = Call(token, "GET", (plain ? "/api/v1/tasks?limit=200" : filter) + Next(cursor));
                // Query refused: all active tasks instead
                if (r.Status == 400 && !plain && cursor == null) { plain = true; i--; continue; }
                if (!r.Ok) return Refused(r);
                var o = Parse(r);
                if (o == null) return SourceResult.Fail(Lang.T("Respons Todoist tidak terbaca"), "json", false);
                tasks.AddRange(ParseTasks(o, projects, nowUtc));
                cursor = Json.Str(o, "next_cursor");
                if (cursor.Length == 0) break;
            }
            return new SourceResult { Tasks = tasks };
        }

        static string Next(string cursor) { return string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor); }

        public static List<KuliaTask> ParseTasks(object o, Dictionary<string, string> projects, DateTime nowUtc)
        {
            var list = new List<KuliaTask>();
            foreach (var t in Json.Arr(o, "results"))
            {
                string id = Json.Str(t, "id");
                // Ids go into URLs: no dots, slashes
                if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$") || Json.Str(t, "checked") == "True" || Json.Str(t, "is_deleted") == "True") continue;
                DateTime due = DateTime.MinValue;
                bool dateOnly = false, ok = false;
                var dueObj = Json.Obj(t, "due");
                var deadline = Json.Obj(t, "deadline");
                if (dueObj != null) ok = When(Json.Str(dueObj, "date"), out due, out dateOnly);
                if (!ok && deadline != null) ok = When(Json.Str(deadline, "date"), out due, out dateOnly);
                if (!ok || due < nowUtc.AddDays(-14) || due > nowUtc.AddDays(22)) continue;
                string project;
                projects.TryGetValue(Json.Str(t, "project_id"), out project);
                string title = Clean(Json.Str(t, "content"), 160);
                // API priority 4 means p1
                if (Json.Num(t, "priority", 1) >= 4) project = (project ?? "").Length > 0 ? project + " · P1" : "P1";
                list.Add(new KuliaTask
                {
                    Id = "todoist:" + id, Title = title.Length > 0 ? title : Lang.T("Tanpa judul"), Course = project ?? "", DeadlineUtc = due, DateOnly = dateOnly,
                    Source = "todoist", Url = "https://app.todoist.com/app/task/" + Uri.EscapeDataString(id),
                });
            }
            return list;
        }

        // YYYY-MM-DD, floating local, or UTC with Z
        static bool When(string s, out DateTime due, out bool dateOnly)
        {
            due = DateTime.MinValue;
            dateOnly = false;
            if (s.Length == 10)
            {
                DateTime d;
                if (!DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return false;
                due = EndOfDay(d);
                dateOnly = true;
                return true;
            }
            DateTime at;
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out at)) return false;
            due = DateTime.SpecifyKind(at, DateTimeKind.Utc);
            return true;
        }

        public override string Close(object snap, KuliaTask t)
        {
            string id = t.Id.StartsWith("todoist:", StringComparison.Ordinal) ? t.Id.Substring(8) : "";
            if (id.Length == 0) return Lang.T("Tugas tidak dikenal");
            var r = Call((string)snap, "POST", "/api/v1/tasks/" + Uri.EscapeDataString(id) + "/close");
            // Gone already counts as closed
            if (r.Ok || r.Status == 404) return null;
            if (r.Status == 401) return Lang.F("Token {0} ditolak", "Todoist");
            if (r.Status == 0) return Lang.F("{0} tidak terjangkau", "Todoist");
            return Lang.F("Gagal tersimpan di {0}", "Todoist");
        }
    }
}
