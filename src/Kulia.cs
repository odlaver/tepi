using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    class KuliaTask
    {
        public string Id = "", Title = "", Course = "", Kind = "tugas", Status = "belum";
        public DateTime DeadlineUtc;
        public bool Personal;
        // kulia, notion, todoist, ics:<id>
        public string Source = "kulia";
        // No time of day; no done action
        public bool DateOnly, ReadOnly;
        string url;

        public bool Closed { get { return Status == "selesai" || Status == "dilewati"; } }
        public string Url
        {
            get { return url ?? "https://kulia.live/tugas/" + (Personal ? "pribadi/" : "") + Id; }
            set { url = value; }
        }
    }

    class ClassSlot
    {
        public string Title = "", Room = "", Kind = "", Url = "";
        public bool Online, Personal, Cancelled, Exam, AllDay;
        public string Source = "kulia";
        public DateTime Start, End;
    }

    // OTP login, then PostgREST under RLS
    class KuliaClient
    {
        // Public client config, same as kulia.live
        public const string DefaultUrl = "https://ptsopuwxpogqgnaazyer.supabase.co";
        public const string DefaultKey = "sb_publishable_LKZS4deN4E3z7zH-EKP2KQ_zQ38JkH_";

        static readonly string SessionFile = Path.Combine(Settings.Dir, "kulia.session");
        readonly string url, key;
        readonly object sync = new object();
        string access, refresh, userId, email, classLabel;
        DateTime accessUntilUtc;
        bool classKnown;

        public KuliaClient(string url, string key)
        {
            this.url = (url ?? "").TrimEnd('/');
            this.key = key ?? "";
            var saved = Json.Parse(Vault.Load(SessionFile) ?? "");
            if (saved != null)
            {
                refresh = Json.Str(saved, "refresh");
                email = Json.Str(saved, "email");
                userId = Json.Str(saved, "user");
                if (refresh.Length == 0) refresh = null;
            }
        }

        public bool Configured { get { return url.StartsWith("https://") && key.Length > 0; } }
        public bool SignedIn { get { return refresh != null; } }
        public string Email { get { return email ?? ""; } }

        Dictionary<string, string> Headers(bool auth)
        {
            var h = new Dictionary<string, string> { { "apikey", key }, { "Content-Type", "application/json" } };
            if (auth && access != null) h["Authorization"] = "Bearer " + access;
            return h;
        }

        // Captcha blocks OTP here; kulia.live/tepi signs in instead
        public static string LinkUrl(int port, string state)
        {
            return "https://kulia.live/tepi?port=" + port + "&state=" + state;
        }

        // One-time token from kulia.live/tepi; null on success
        public string Redeem(string tokenHash)
        {
            var r = Web.Send("POST", url + "/auth/v1/verify", Headers(false),
                Json.Encode(new Dictionary<string, object> { { "type", "magiclink" }, { "token_hash", tokenHash } }), 15000);
            if (r.Status == 0) return Lang.T("Kulia tidak terjangkau");
            if (!r.Ok) { Log.Write("kulia redeem " + r.Status); return Lang.T("Tautan masuk kedaluwarsa, coba lagi"); }
            lock (sync)
            {
                var session = Json.Parse(r.Body);
                Take(session);
                var user = Json.Obj(session, "user");
                email = user != null ? Json.Str(user, "email") : "";
                classLabel = null;
                classKnown = false;
                Persist();
            }
            return null;
        }

        public void SignOut()
        {
            lock (sync)
            {
                if (access != null) Web.Send("POST", url + "/auth/v1/logout", Headers(true), "{}", 8000);
                access = refresh = userId = email = classLabel = null;
                classKnown = false;
                Vault.Delete(SessionFile);
            }
        }

        void Take(object session)
        {
            access = Json.Str(session, "access_token");
            refresh = Json.Str(session, "refresh_token");
            accessUntilUtc = DateTime.UtcNow.AddSeconds(Json.Num(session, "expires_in", 3600) - 60);
            var user = Json.Obj(session, "user");
            if (user != null) userId = Json.Str(user, "id");
        }

        void Persist()
        {
            try
            {
                Vault.Save(SessionFile, Json.Encode(new Dictionary<string, object> { { "refresh", refresh ?? "" }, { "email", email ?? "" }, { "user", userId ?? "" } }));
            }
            catch (Exception ex) { Log.Write("kulia persist: " + ex.Message); }
        }

        // Refresh tokens rotate: save each one
        string EnsureAccess()
        {
            if (refresh == null) return Lang.T("Belum masuk ke Kulia.");
            if (access != null && DateTime.UtcNow < accessUntilUtc) return null;
            var r = Web.Send("POST", url + "/auth/v1/token?grant_type=refresh_token", Headers(false),
                Json.Encode(new Dictionary<string, object> { { "refresh_token", refresh } }), 15000);
            if (r.Status == 0) return Lang.T("Kulia tidak terjangkau · dicoba lagi otomatis");
            if (r.Status == 400 || r.Status == 401)
            {
                access = refresh = null;
                Vault.Delete(SessionFile);
                return Lang.T("Sesi Kulia habis");
            }
            if (!r.Ok) return Lang.T("Kulia sedang bermasalah (") + r.Status + ").";
            Take(Json.Parse(r.Body));
            Persist();
            return null;
        }

        WebResult Call(string method, string path, string body, Dictionary<string, string> extra)
        {
            var h = Headers(true);
            if (extra != null) foreach (var kv in extra) h[kv.Key] = kv.Value;
            var r = Web.Send(method, url + path, h, body, 15000);
            if (r.Status == 401)
            {
                accessUntilUtc = DateTime.MinValue;
                if (EnsureAccess() == null)
                {
                    h["Authorization"] = "Bearer " + access;
                    r = Web.Send(method, url + path, h, body, 15000);
                }
            }
            return r;
        }

        void EnsureClass()
        {
            if (classKnown || userId == null) return;
            var pr = Call("GET", "/rest/v1/profiles?select=class_label&id=eq." + userId, null, null);
            if (!pr.Ok) return;
            var rows = Rows(pr.Body);
            if (rows.Length > 0) classLabel = Json.Str(rows[0], "class_label");
            classKnown = true;
        }

        static object[] Rows(string body) { return Json.Parse(body) as object[] ?? new object[0]; }

        // Next 7 days, the same rows kulia.live/jadwal shows
        public List<ClassSlot> Schedule(out string error)
        {
            lock (sync)
            {
                error = EnsureAccess();
                if (error != null) return null;
                EnsureClass();
                var en = Call("GET", "/rest/v1/enrollments?select=cohort_course_id&user_id=eq." + userId, null, null);
                if (!en.Ok) { error = en.Status == 0 ? Lang.T("Kulia tidak terjangkau · dicoba lagi otomatis") : Lang.T("Jadwal gagal dimuat (") + en.Status + ")."; return null; }
                var ids = new List<string>();
                foreach (var row in Rows(en.Body)) ids.Add(Json.Str(row, "cohort_course_id"));
                var course = new object[0];
                if (ids.Count > 0)
                {
                    var cr = Call("GET", "/rest/v1/course_schedules?select=weekday,starts_at,ends_at,room,kind,mode,meeting_url,class_date,class_label," +
                        "cohort_courses!inner(is_active,courses(name,short_name)),schedule_confirmations(class_date,status)" +
                        "&cohort_courses.is_active=eq.true&cohort_course_id=in.(" + string.Join(",", ids) + ")", null, null);
                    if (!cr.Ok) { error = Lang.T("Jadwal gagal dimuat (") + cr.Status + ")."; return null; }
                    course = Rows(cr.Body);
                }
                var pr = Call("GET", "/rest/v1/personal_schedules?select=id,title,category,weekday,event_date,starts_at,ends_at,room", null, null);
                var personal = pr.Ok ? Rows(pr.Body) : new object[0];
                // Invites not yet answered stay off the schedule
                var pending = new HashSet<string>();
                var mr = Call("POST", "/rest/v1/rpc/my_schedule_partners", "{}", null);
                if (mr.Ok)
                    foreach (var m in Rows(mr.Body))
                        if (Json.Str(m, "user_id") == userId && Json.Str(m, "status") == "pending") pending.Add(Json.Str(m, "schedule_id"));
                return Expand(course, personal, pending);
            }
        }

        List<ClassSlot> Expand(object[] course, object[] personal, HashSet<string> pending)
        {
            var list = new List<ClassSlot>();
            // Kulia stores WIB wall-clock times
            var wib = TimeSpan.FromHours(7);
            var today = DateTimeOffset.UtcNow.ToOffset(wib).Date;
            for (int d = 0; d < 7; d++)
            {
                var day = today.AddDays(d);
                int iso = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
                string date = day.ToString("yyyy-MM-dd");
                foreach (var r in course)
                {
                    string once = Json.Str(r, "class_date");
                    if (once.Length > 0 ? once != date : (int)Json.Num(r, "weekday", 0) != iso) continue;
                    string forClass = Json.Str(r, "class_label");
                    if (forClass.Length > 0 && forClass != classLabel) continue;
                    var c = Json.Obj(Json.Obj(r, "cohort_courses"), "courses");
                    string name = Json.Str(c, "short_name");
                    if (name.Length == 0) name = Json.Str(c, "name");
                    var slot = Slot(day, wib, r, name.Length > 0 ? name : Lang.T("Mata kuliah"));
                    slot.Kind = Json.Str(r, "kind");
                    slot.Online = Json.Str(r, "mode") == "online";
                    string link = Json.Str(r, "meeting_url");
                    if (slot.Online && link.StartsWith("https://", StringComparison.Ordinal)) slot.Url = link;
                    foreach (var cf in Json.Arr(r, "schedule_confirmations"))
                        if (Json.Str(cf, "class_date") == date)
                        {
                            slot.Cancelled = Json.Str(cf, "status") == "batal";
                            slot.Exam = Json.Str(cf, "status") == "ujian";
                        }
                    list.Add(slot);
                }
                foreach (var r in personal)
                {
                    if (pending.Contains(Json.Str(r, "id"))) continue;
                    bool oneOff = Json.Str(r, "category") == "kegiatan";
                    if (oneOff ? Json.Str(r, "event_date") != date : (int)Json.Num(r, "weekday", 0) != iso) continue;
                    var slot = Slot(day, wib, r, Json.Str(r, "title"));
                    slot.Personal = true;
                    list.Add(slot);
                }
            }
            list.RemoveAll(s => s.End < DateTime.Now);
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            return list;
        }

        static ClassSlot Slot(DateTime day, TimeSpan wib, object r, string title)
        {
            TimeSpan from, to;
            TimeSpan.TryParse(Json.Str(r, "starts_at"), out from);
            TimeSpan.TryParse(Json.Str(r, "ends_at"), out to);
            return new ClassSlot
            {
                Title = title,
                Room = Json.Str(r, "room"),
                Start = new DateTimeOffset(day + from, wib).LocalDateTime,
                End = new DateTimeOffset(day + to, wib).LocalDateTime,
            };
        }

        public List<KuliaTask> Feed(out string error)
        {
            lock (sync)
            {
                error = EnsureAccess();
                if (error != null) return null;
                EnsureClass();
                var r = Call("POST", "/rest/v1/rpc/my_feed", "{}", null);
                if (r.Status == 0) { error = Lang.T("Kulia tidak terjangkau · dicoba lagi otomatis"); return null; }
                if (!r.Ok) { error = Lang.T("Kulia menolak permintaan (") + r.Status + ")."; return null; }
                return Parse(Json.Parse(r.Body));
            }
        }

        List<KuliaTask> Parse(object bundle)
        {
            var statuses = new Dictionary<string, string>();
            foreach (var s in Json.Arr(bundle, "statuses")) statuses[Json.Str(s, "task_id")] = Json.Str(s, "status");
            var list = new List<KuliaTask>();
            foreach (var t in Json.Arr(bundle, "tasks"))
            {
                string cls = Json.Str(t, "class_label");
                if (cls.Length > 0 && classLabel != null && cls != classLabel) continue;
                var task = new KuliaTask { Id = Json.Str(t, "id"), Title = Json.Str(t, "title"), Kind = Json.Str(t, "kind"), DeadlineUtc = Json.Time(t, "deadline_at") };
                var course = Json.Obj(Json.Obj(t, "cohort_courses"), "courses");
                if (course != null) task.Course = Json.Str(course, "short_name").Length > 0 ? Json.Str(course, "short_name") : Json.Str(course, "name");
                string st;
                task.Status = statuses.TryGetValue(task.Id, out st) ? st : "belum";
                list.Add(task);
            }
            foreach (var t in Json.Arr(bundle, "personal"))
                list.Add(new KuliaTask { Id = Json.Str(t, "id"), Title = Json.Str(t, "title"), Course = Lang.T("Pribadi"), Status = Json.Str(t, "status"), DeadlineUtc = Json.Time(t, "deadline_at"), Personal = true });

            var now = DateTime.UtcNow;
            list = list.FindAll(t => !t.Closed && t.DeadlineUtc != DateTime.MinValue && (t.Kind != "ujian" || t.DeadlineUtc > now) && t.DeadlineUtc > now.AddDays(-14));
            list.Sort((a, b) => a.DeadlineUtc.CompareTo(b.DeadlineUtc));
            return list;
        }

        public bool SetStatus(KuliaTask task, string status)
        {
            lock (sync)
            {
                if (EnsureAccess() != null || userId == null) return false;
                string stamp = DateTime.UtcNow.ToString("o");
                if (task.Personal)
                {
                    var r = Call("PATCH", "/rest/v1/personal_tasks?id=eq." + task.Id,
                        Json.Encode(new Dictionary<string, object> { { "status", status }, { "updated_at", stamp } }),
                        new Dictionary<string, string> { { "Prefer", "return=representation" } });
                    if (!r.Ok) return false;
                    var rows = Json.Parse(r.Body) as object[];
                    return rows != null && rows.Length > 0;
                }
                var u = Call("POST", "/rest/v1/task_progress?on_conflict=user_id,task_id",
                    Json.Encode(new Dictionary<string, object> { { "user_id", userId }, { "task_id", task.Id }, { "status", status }, { "updated_at", stamp } }),
                    new Dictionary<string, string> { { "Prefer", "resolution=merge-duplicates,return=minimal" } });
                return u.Ok;
            }
        }
    }

    // Sign-in finishes on kulia.live, back via loopback
    class KuliaLogin : Form
    {
        readonly KuliaClient client;
        readonly Label heading = new Label(), hint = new Label(), status = new Label();
        readonly Button primary = new Button(), cancel = new Button();
        readonly object gate = new object();
        TcpListener listener;
        int round;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public KuliaLogin(KuliaClient client)
        {
            this.client = client;
            Text = Lang.T("Masuk ke Kulia");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Load += delegate { KitWindow.ScaleToDpi(this); };
            HandleCreated += delegate { Native.RoundCorners(Handle); };
            ClientSize = new Size(400, 214);
            ShowInTaskbar = true;
            TopMost = true;
            var bg = Ink.Light ? Color.FromArgb(245, 245, 245) : Color.FromArgb(10, 10, 10);
            var fg = Ink.Light ? Color.FromArgb(10, 10, 10) : Color.FromArgb(244, 244, 244);
            var sub = Ink.Light ? Color.FromArgb(88, 88, 88) : Color.FromArgb(160, 160, 160);
            BackColor = bg;
            ForeColor = fg;
            Font = Ink.Text(14f);

            heading.SetBounds(24, 20, 352, 30);
            heading.Font = Ink.Display(20f);
            heading.Text = Lang.T("Masuk ke Kulia");
            hint.SetBounds(24, 56, 352, 44);
            hint.ForeColor = sub;
            hint.Text = Lang.T("Browser terbuka di kulia.live. Masuk di sana, lalu tekan Hubungkan Tepi.");
            status.SetBounds(24, 108, 352, 44);
            status.ForeColor = fg;

            primary.SetBounds(236, 162, 140, 34);
            Style(primary, fg, bg);
            primary.Text = Lang.T("Buka lagi");
            cancel.SetBounds(24, 162, 140, 34);
            Style(cancel, bg, fg);
            cancel.FlatAppearance.BorderColor = sub;
            cancel.FlatAppearance.BorderSize = 1;
            cancel.Text = Lang.T("Batal");
            cancel.Click += delegate { Close(); };
            primary.Click += delegate { Start(); };
            AcceptButton = primary;
            CancelButton = cancel;
            Shown += delegate { Start(); };
            FormClosed += delegate { Stop(); };

            Controls.AddRange(new Control[] { heading, hint, status, primary, cancel });
        }

        static void Style(Button b, Color bg, Color fg)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.Font = Ink.Semi(14f);
            b.Cursor = Cursors.Hand;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = Ink.Light ? 0 : 1;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch (Exception) { }
        }

        void Stop()
        {
            lock (gate)
            {
                round++;
                if (listener != null) try { listener.Stop(); } catch (Exception) { }
                listener = null;
            }
        }

        // Fresh port and state each round
        void Start()
        {
            Stop();
            TcpListener l;
            int mine;
            string state = Guid.NewGuid().ToString("N");
            try
            {
                l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
            }
            catch (Exception ex) { Log.Write("kulia listen: " + ex.Message); status.Text = Lang.T("Tepi tidak bisa menunggu balasan browser"); return; }
            lock (gate) { listener = l; mine = round; }
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            try { Process.Start(new ProcessStartInfo(KuliaClient.LinkUrl(port, state)) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("kulia browser: " + ex.Message); }
            status.Text = Lang.T("Menunggu kulia.live…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = Lang.T("Belum ada balasan dari kulia.live dalam 10 menit. Tekan Buka lagi.");
                var got = SpotifyLink.WaitForCallback(l, TimeSpan.FromMinutes(10), "/kulia", q =>
                {
                    string s, hash;
                    if (!q.TryGetValue("state", out s) || s != state || !q.TryGetValue("token_hash", out hash)) err = Lang.T("Tautan masuk tidak cocok. Tekan Buka lagi.");
                    else
                    {
                        try { err = client.Redeem(hash); }
                        catch (Exception ex) { err = Lang.T("Gagal masuk ke Kulia"); Log.Write("kulia redeem: " + ex.Message); }
                    }
                    return err == null
                        ? new[] { Lang.T("Tepi tersambung ke Kulia"), Lang.T("Tab ini boleh ditutup.") }
                        : new[] { Lang.T("Tepi belum tersambung"), err };
                });
                try { l.Stop(); } catch (Exception) { }
                lock (gate) { if (mine != round) return; }
                Finish(got == null && err == null ? Lang.T("Gagal masuk ke Kulia") : err);
            });
        }

        void Finish(string error)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                if (IsDisposed) return;
                if (error != null) { status.Text = error; return; }
                DialogResult = DialogResult.OK;
                Close();
            }));
        }
    }
}
