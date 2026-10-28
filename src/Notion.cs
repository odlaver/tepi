using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tepi
{
    class NotionProp
    {
        public string Id = "", Name = "", Type = "";
        // Status options counted as done: id, name
        public List<string[]> Done = new List<string[]>();
    }

    class NotionDb
    {
        public string Id = "", Title = "", Kind = "";
        public bool On;
        public NotionProp Date, Done;
        public List<NotionProp> Dates = new List<NotionProp>(), Dones = new List<NotionProp>();
    }

    // Notion data sources as tasks, API 2026-03-11
    class NotionSource : Source
    {
        const string Api = "https://api.notion.com", ApiVersion = "2026-03-11";
        static readonly string FilePath = Path.Combine(Settings.Dir, "notion.vault");
        static readonly object gate = new object();
        static DateTime lastCall = DateTime.MinValue;

        public string Token = "", Workspace = "";
        public List<NotionDb> Dbs = new List<NotionDb>();
        // Integration lacks the update capability
        public volatile bool NoWrite;

        public NotionSource() { Key = "notion"; Name = "Notion"; }

        public static NotionSource Load()
        {
            try
            {
                var o = Json.Parse(Vault.Load(FilePath) ?? "");
                string token = Json.Str(o, "token");
                if (token.Length == 0) return null;
                var n = new NotionSource { Token = token, Workspace = Json.Str(o, "workspace"), NoWrite = Json.Str(o, "nowrite") == "True" };
                foreach (var d in Json.Arr(o, "dbs"))
                {
                    var db = DbFrom(d);
                    if (db != null) n.Dbs.Add(db);
                }
                return n;
            }
            catch (Exception ex) { Log.Write("notion load: " + ex.GetType().Name); return null; }
        }

        public void Save()
        {
            var dbs = new List<object>();
            foreach (var db in Dbs) dbs.Add(DbTo(db));
            try { Vault.Save(FilePath, Json.Encode(new Dictionary<string, object> { { "token", Token }, { "workspace", Workspace }, { "nowrite", NoWrite }, { "dbs", dbs.ToArray() } })); }
            catch (Exception ex) { Log.Write("notion save: " + ex.GetType().Name); }
        }

        public static void Forget() { Vault.Delete(FilePath); }

        public override object Snapshot()
        {
            var c = new NotionSource { Token = Token, Workspace = Workspace };
            foreach (var db in Dbs) c.Dbs.Add(DbFrom(DbTo(db)));
            return c;
        }

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static object PropTo(NotionProp p)
        {
            if (p == null) return null;
            var opts = new List<object>();
            foreach (var o in p.Done) opts.Add(new object[] { o[0], o[1] });
            return D("id", p.Id, "name", p.Name, "type", p.Type, "done", opts.ToArray());
        }

        static NotionProp PropFrom(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            var p = new NotionProp { Id = Json.Str(d, "id"), Name = Json.Str(d, "name"), Type = Json.Str(d, "type") };
            foreach (var opt in Json.Arr(d, "done"))
            {
                var pair = opt as object[] ?? (opt is System.Collections.ArrayList ? ((System.Collections.ArrayList)opt).ToArray() : null);
                if (pair != null && pair.Length == 2) p.Done.Add(new[] { Convert.ToString(pair[0], CultureInfo.InvariantCulture), Convert.ToString(pair[1], CultureInfo.InvariantCulture) });
            }
            return p.Id.Length > 0 ? p : null;
        }

        static object DbTo(NotionDb db)
        {
            var dates = new List<object>();
            foreach (var p in db.Dates) dates.Add(PropTo(p));
            var dones = new List<object>();
            foreach (var p in db.Dones) dones.Add(PropTo(p));
            return D("id", db.Id, "title", db.Title, "kind", db.Kind, "on", db.On, "date", PropTo(db.Date), "done", PropTo(db.Done), "dates", dates.ToArray(), "dones", dones.ToArray());
        }

        static NotionDb DbFrom(object o)
        {
            var db = new NotionDb { Id = Json.Str(o, "id"), Title = Json.Str(o, "title"), Kind = Json.Str(o, "kind"), On = Json.Str(o, "on") == "True" };
            if (!SafeId(db.Id)) return null;
            foreach (var p in Json.Arr(o, "dates")) { var x = PropFrom(p); if (x != null) db.Dates.Add(x); }
            foreach (var p in Json.Arr(o, "dones")) { var x = PropFrom(p); if (x != null) db.Dones.Add(x); }
            var date = PropFrom(Json.Obj(o, "date"));
            var done = PropFrom(Json.Obj(o, "done"));
            db.Date = date == null ? null : db.Dates.Find(x => x.Id == date.Id) ?? date;
            db.Done = done == null ? null : db.Dones.Find(x => x.Id == done.Id) ?? done;
            return db;
        }

        static bool SafeId(string id) { return id != null && Regex.IsMatch(id, "^[0-9a-fA-F-]{32,36}$"); }

        // Serialized, about three requests a second
        static FetchResult Call(string token, string method, string path, object body)
        {
            var h = new Dictionary<string, string> { { "Authorization", "Bearer " + token }, { "Notion-Version", ApiVersion } };
            if (body != null) h["Content-Type"] = "application/json";
            lock (gate)
            {
                var wait = lastCall.AddMilliseconds(350) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                var r = Tepi.Fetch.Send(method, Api + path, h, body != null ? Json.Encode(body) : null, Tepi.Fetch.JsonCap, 20000);
                lastCall = DateTime.UtcNow;
                return r;
            }
        }

        static object Parse(FetchResult r)
        {
            try { return Json.Parse(r.Text); }
            catch (Exception) { return null; }
        }

        static string Plain(object[] rich)
        {
            var sb = new StringBuilder();
            foreach (var t in rich) sb.Append(Json.Str(t, "plain_text"));
            return sb.ToString();
        }

        public override SourceResult Fetch(object snap, DateTime nowUtc)
        {
            var cfg = (NotionSource)snap;
            var tasks = new List<KuliaTask>();
            SourceResult first = null;
            int ok = 0;
            bool stop = true;
            var fixedDbs = new List<NotionDb>();
            foreach (var db in cfg.Dbs)
            {
                // Disconnected meanwhile: no more requests
                if (Gone) return null;
                if (!db.On || db.Date == null) continue;
                FetchResult fail;
                var list = Query(cfg.Token, db, out fail);
                if (list == null && fail.Status == 400)
                {
                    // Columns renamed: read the schema again
                    FetchResult sr;
                    var fresh = Schema(cfg.Token, db.Id, out sr);
                    if (fresh != null && Remap(db, fresh))
                    {
                        fixedDbs.Add(db);
                        list = Query(cfg.Token, db, out fail);
                    }
                }
                if (list != null) { ok++; tasks.AddRange(list); continue; }
                var why = Failure(fail, db);
                if (fail.Status == 401) return why;
                // Rate limited: keep its Retry-After, stop
                if (fail.Status == 429) { first = why; stop = false; break; }
                if (first == null) first = why;
                stop &= why.Stop;
            }
            var res = new SourceResult { Extra = fixedDbs.Count > 0 ? fixedDbs : null };
            if (first != null)
            {
                res.Error = first.Error;
                res.Log = first.Log;
                res.RetryAfter = first.RetryAfter;
                res.Stop = ok == 0 && stop;
            }
            if (ok > 0 || first == null) res.Tasks = tasks;
            return res;
        }

        static SourceResult Failure(FetchResult r, NotionDb db)
        {
            if (r.Status == 404) return SourceResult.Fail(Lang.F("{0} belum dibagikan ke integrasi", Clean(db.Title, 40)), "http 404", true);
            if (r.Status == 400) return SourceResult.Fail(Lang.F("Kolom {0} berubah", Clean(db.Title, 40)), "http 400", true);
            if (r.Status == 403) return SourceResult.Fail(Lang.T("Integrasi Notion tanpa izin baca"), "http 403", true);
            return SourceResult.Http("Notion", r);
        }

        // Open items, two weeks back, three ahead
        static List<KuliaTask> Query(string token, NotionDb db, out FetchResult fail)
        {
            fail = null;
            if (!SafeId(db.Id)) { fail = new FetchResult { Status = 404 }; return null; }
            var today = DateTime.Now.Date;
            var and = new List<object>();
            if (db.Done != null)
            {
                if (db.Done.Type == "checkbox") and.Add(D("property", db.Done.Name, "checkbox", D("equals", false)));
                else foreach (var o in db.Done.Done) and.Add(D("property", db.Done.Name, "status", D("does_not_equal", o[1])));
            }
            and.Add(D("property", db.Date.Name, "date", D("on_or_after", today.AddDays(-14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
            and.Add(D("property", db.Date.Name, "date", D("on_or_before", today.AddDays(21).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
            var body = D("filter", D("and", and.ToArray()), "sorts", new object[] { D("property", db.Date.Name, "direction", "ascending") }, "page_size", 100);
            string path = "/v1/data_sources/" + db.Id + "/query" + Trim(db);
            var list = new List<KuliaTask>();
            for (int page = 0; page < 5; page++)
            {
                var r = Call(token, "POST", path, body);
                if (!r.Ok) { fail = r; return null; }
                var o = Parse(r);
                if (o == null) { fail = new FetchResult { Status = 502 }; return null; }
                foreach (var pg in Json.Arr(o, "results"))
                {
                    var t = Page(pg, db);
                    if (t != null) list.Add(t);
                }
                string next = Json.Str(o, "next_cursor");
                if (Json.Str(o, "has_more") != "True" || next.Length == 0) break;
                body["start_cursor"] = next;
            }
            return list;
        }

        // Only the columns Tepi reads
        static string Trim(NotionDb db)
        {
            var ids = new List<string> { "title", db.Date.Id };
            if (db.Done != null) ids.Add(db.Done.Id);
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (!Regex.IsMatch(id, "^[A-Za-z0-9%._~-]{1,64}$")) return "";
                sb.Append(sb.Length == 0 ? "?" : "&").Append("filter_properties=").Append(id);
            }
            return sb.ToString();
        }

        public static KuliaTask Page(object pg, NotionDb db)
        {
            if (Json.Str(pg, "object") != "page" || Json.Str(pg, "in_trash") == "True" || Json.Str(pg, "is_archived") == "True") return null;
            var props = Json.Obj(pg, "properties");
            string pageId = Json.Str(pg, "id");
            if (props == null || !SafeId(pageId)) return null;
            string title = "";
            Dictionary<string, object> date = null;
            bool done = false;
            foreach (var kv in props)
            {
                var v = kv.Value as Dictionary<string, object>;
                if (v == null) continue;
                string id = Json.Str(v, "id"), type = Json.Str(v, "type");
                if (type == "title") title = Plain(Json.Arr(v, "title"));
                else if (type == "date" && db.Date != null && id == db.Date.Id) date = Json.Obj(v, "date");
                else if (db.Done != null && id == db.Done.Id) done = IsDone(v, db.Done);
            }
            if (done || date == null) return null;
            string when = Json.Str(date, "end");
            if (when.Length == 0) when = Json.Str(date, "start");
            DateTime due;
            bool dateOnly;
            if (!When(when, out due, out dateOnly)) return null;
            title = Clean(title, 160);
            return new KuliaTask
            {
                Id = "notion:" + db.Id + "/" + pageId, Title = title.Length > 0 ? title : Lang.T("Tanpa judul"), Course = Clean(db.Title, 60),
                DeadlineUtc = due, DateOnly = dateOnly, Source = "notion", Url = SafeLink(Json.Str(pg, "url")), ReadOnly = db.Done == null,
            };
        }

        static bool IsDone(Dictionary<string, object> v, NotionProp prop)
        {
            if (prop.Type == "checkbox") return Json.Str(v, "checkbox") == "True";
            string sid = Json.Str(Json.Obj(v, "status"), "id");
            foreach (var o in prop.Done) if (o[0] == sid) return true;
            return false;
        }

        // Date only, or a moment with offset
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
            DateTimeOffset at;
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out at)) return false;
            due = at.UtcDateTime;
            return true;
        }

        public override string Close(object snap, KuliaTask t)
        {
            var cfg = (NotionSource)snap;
            // notion:<data source>/<page>
            string rest = t.Id.StartsWith("notion:", StringComparison.Ordinal) ? t.Id.Substring(7) : "";
            int slash = rest.IndexOf('/');
            if (slash <= 0) return Lang.T("Tugas tidak dikenal");
            string dsId = rest.Substring(0, slash), pageId = rest.Substring(slash + 1);
            var db = cfg.Dbs.Find(d => d.Id == dsId);
            if (db == null || db.Done == null || !SafeId(pageId)) return Lang.T("Kolom selesai belum dipilih");
            object value;
            if (db.Done.Type == "checkbox") value = D("checkbox", true);
            else if (db.Done.Done.Count > 0) value = D("status", D("id", db.Done.Done[0][0]));
            else return Lang.T("Kolom selesai belum dipilih");
            var r = Call(cfg.Token, "PATCH", "/v1/pages/" + pageId, D("properties", D(db.Done.Id, value)));
            if (r.Ok) return null;
            if (r.Status == 403) { NoWrite = true; return Lang.T("Integrasi Notion tanpa izin ubah"); }
            if (r.Status == 401) return Lang.F("Token {0} ditolak", "Notion");
            if (r.Status == 404) return Lang.T("Halaman Notion tidak ditemukan");
            if (r.Status == 0) return Lang.F("{0} tidak terjangkau", "Notion");
            return Lang.F("Gagal tersimpan di {0}", "Notion");
        }

        // Check the token, list shared databases
        public static string Connect(string token, out NotionSource made)
        {
            made = null;
            token = (token ?? "").Trim();
            if (!TokenOk(token, 400)) return Lang.T("Token tidak lengkap");
            var r = Call(token, "GET", "/v1/users/me", null);
            if (r.Status == 401) return Lang.F("Token {0} ditolak", "Notion");
            if (!r.Ok) return SourceResult.Http("Notion", r).Error;
            var n = new NotionSource { Token = token, Workspace = Clean(Json.Str(Json.Obj(Parse(r), "bot"), "workspace_name"), 60) };
            string err = Scan(n, null);
            if (err != null) return err;
            made = n;
            return null;
        }

        // Search: data sources shared with token
        public static string Scan(NotionSource n, List<NotionDb> keep)
        {
            var found = new List<NotionDb>();
            var body = D("filter", D("property", "object", "value", "data_source"), "sort", D("timestamp", "last_edited_time", "direction", "descending"), "page_size", 100);
            for (int page = 0; page < 5; page++)
            {
                var r = Call(n.Token, "POST", "/v1/search", body);
                if (!r.Ok) return SourceResult.Http("Notion", r).Error;
                var o = Parse(r);
                if (o == null) return Lang.T("Respons Notion tidak terbaca");
                foreach (var ds in Json.Arr(o, "results"))
                {
                    var db = Db(ds);
                    if (db != null && !found.Exists(x => x.Id == db.Id)) found.Add(db);
                }
                string next = Json.Str(o, "next_cursor");
                if (Json.Str(o, "has_more") != "True" || next.Length == 0) break;
                body["start_cursor"] = next;
            }
            if (keep != null)
            {
                foreach (var db in found)
                {
                    var old = keep.Find(k => k.Id == db.Id);
                    if (old != null) Carry(old, db);
                }
                // Search lags; linked ones stay
                foreach (var old in keep) if (!found.Exists(x => x.Id == old.Id)) found.Add(old);
            }
            if (!found.Exists(d => d.On)) AutoPick(found);
            n.Dbs = found;
            return null;
        }

        static void Carry(NotionDb old, NotionDb db)
        {
            db.On = old.On;
            if (old.Date != null) db.Date = db.Dates.Find(p => p.Id == old.Date.Id) ?? db.Date;
            db.Done = old.Done == null ? null : db.Dones.Find(p => p.Id == old.Done.Id) ?? db.Done;
        }

        // Task-type databases first; else a lone one
        static void AutoPick(List<NotionDb> list)
        {
            foreach (var db in list) if (db.Kind == "tasks" && db.Date != null) db.On = true;
            if (!list.Exists(d => d.On) && list.Count == 1 && list[0].Date != null) list[0].On = true;
        }

        public static NotionDb Db(object ds)
        {
            if (Json.Str(ds, "object") != "data_source" || Json.Str(ds, "in_trash") == "True") return null;
            var props = Json.Obj(ds, "properties");
            string id = Json.Str(ds, "id");
            if (props == null || !SafeId(id)) return null;
            var db = new NotionDb { Id = id, Title = Clean(Plain(Json.Arr(ds, "title")), 60), Kind = Json.Str(ds, "database_type") };
            if (db.Title.Length == 0) db.Title = Lang.T("Tanpa judul");
            foreach (var kv in props)
            {
                var p = kv.Value as Dictionary<string, object>;
                if (p == null) continue;
                var prop = new NotionProp { Id = Json.Str(p, "id"), Name = Json.Str(p, "name"), Type = Json.Str(p, "type") };
                if (prop.Name.Length == 0) prop.Name = kv.Key;
                if (prop.Id.Length == 0) continue;
                if (prop.Type == "date") db.Dates.Add(prop);
                else if (prop.Type == "checkbox") db.Dones.Add(prop);
                else if (prop.Type == "status")
                {
                    DoneGroup(Json.Obj(p, "status"), prop);
                    if (prop.Done.Count > 0) db.Dones.Add(prop);
                }
            }
            db.Date = Prefer(db.Dates, "due", "deadline", "tenggat", "tanggal", "date");
            db.Done = db.Dones.Find(x => x.Type == "status") ?? Prefer(db.Dones, "done", "selesai", "complete", "beres");
            return db;
        }

        static NotionProp Prefer(List<NotionProp> list, params string[] words)
        {
            foreach (var w in words)
                foreach (var p in list)
                    if (p.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return p;
            return list.Count > 0 ? list[0] : null;
        }

        // Complete group options; else the last group
        static void DoneGroup(Dictionary<string, object> status, NotionProp prop)
        {
            var options = Json.Arr(status, "options");
            var groups = Json.Arr(status, "groups");
            object pick = null;
            foreach (var g in groups)
            {
                string n = Json.Str(g, "name").ToLowerInvariant();
                if (n.Contains("complete") || n.Contains("done") || n.Contains("selesai")) { pick = g; break; }
            }
            if (pick == null && groups.Length >= 2) pick = groups[groups.Length - 1];
            if (pick == null) return;
            foreach (var oid in Json.Arr(pick, "option_ids"))
            {
                string id = Convert.ToString(oid, CultureInfo.InvariantCulture);
                foreach (var o in options)
                    if (Json.Str(o, "id") == id) prop.Done.Add(new[] { id, Json.Str(o, "name") });
            }
        }

        static NotionDb Schema(string token, string id, out FetchResult r)
        {
            r = Call(token, "GET", "/v1/data_sources/" + id, null);
            return r.Ok ? Db(Parse(r)) : null;
        }

        // Fresh names for the chosen columns
        static bool Remap(NotionDb db, NotionDb fresh)
        {
            var date = db.Date == null ? null : fresh.Dates.Find(p => p.Id == db.Date.Id);
            if (date == null) return false;
            NotionProp done = null;
            if (db.Done != null)
            {
                done = fresh.Dones.Find(p => p.Id == db.Done.Id);
                if (done == null) return false;
            }
            bool changed = date.Name != db.Date.Name || (done != null && (done.Name != db.Done.Name || Names(done) != Names(db.Done)));
            db.Date = date;
            db.Done = done;
            db.Dates = fresh.Dates;
            db.Dones = fresh.Dones;
            return changed;
        }

        static string Names(NotionProp p)
        {
            var sb = new StringBuilder();
            foreach (var o in p.Done) sb.Append(o[0]).Append('=').Append(o[1]).Append('\n');
            return sb.ToString();
        }

        // UI thread: keep fixed column names
        public override bool Absorb(SourceResult r)
        {
            var fixedDbs = r.Extra as List<NotionDb>;
            if (fixedDbs == null) return false;
            foreach (var f in fixedDbs)
            {
                int i = Dbs.FindIndex(d => d.Id == f.Id);
                if (i >= 0) { f.On = Dbs[i].On; Dbs[i] = f; }
            }
            return true;
        }

        // A pasted database link: its data sources
        public static string AddLink(string token, string link, out List<NotionDb> added)
        {
            added = null;
            string id = DatabaseId(link);
            if (id == null) return Lang.T("Tautan database tidak dikenali");
            var r = Call(token, "GET", "/v1/databases/" + id, null);
            if (r.Status == 404) return Lang.T("Database tidak ditemukan atau belum dibagikan");
            if (!r.Ok) return SourceResult.Http("Notion", r).Error;
            var list = new List<NotionDb>();
            foreach (var ds in Json.Arr(Parse(r), "data_sources"))
            {
                string dsId = Json.Str(ds, "id");
                if (!SafeId(dsId)) continue;
                FetchResult sr;
                var db = Schema(token, dsId, out sr);
                if (db == null) continue;
                db.On = db.Date != null;
                list.Add(db);
            }
            if (list.Count == 0) return Lang.T("Database ini tanpa sumber data");
            added = list;
            return null;
        }

        // Database id: last 32-hex run before ?v=
        public static string DatabaseId(string link)
        {
            string s = (link ?? "").Trim();
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            var uuid = Regex.Matches(s, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (uuid.Count > 0) return uuid[uuid.Count - 1].Value.Replace("-", "").ToLowerInvariant();
            var hex = Regex.Matches(s, "(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])");
            if (hex.Count == 0) hex = Regex.Matches(s, "[0-9a-fA-F]{32}(?![0-9a-fA-F])");
            return hex.Count > 0 ? hex[hex.Count - 1].Value.ToLowerInvariant() : null;
        }
    }
}
