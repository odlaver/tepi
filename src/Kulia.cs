using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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

        public bool Closed { get { return Status == "selesai" || Status == "dilewati"; } }
        public string Url { get { return "https://kulia.live/tugas/" + (Personal ? "pribadi/" : "") + Id; } }
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

        public string SendCode(string address)
        {
            var r = Web.Send("POST", url + "/auth/v1/otp", Headers(false), Json.Encode(new Dictionary<string, object> { { "email", address }, { "create_user", false } }), 15000);
            if (r.Ok) return null;
            if (r.Status == 0) return "Kulia tidak terjangkau";
            if (r.Status == 429) return "Kode baru bisa diminta lagi dalam ±1 menit";
            if (r.Body.IndexOf("signup", StringComparison.OrdinalIgnoreCase) >= 0 || r.Status == 422) return "Email ini belum terdaftar di Kulia";
            return "Kode gagal terkirim (" + r.Status + ")";
        }

        public string Verify(string address, string code)
        {
            var r = Web.Send("POST", url + "/auth/v1/verify", Headers(false),
                Json.Encode(new Dictionary<string, object> { { "type", "email" }, { "email", address }, { "token", code } }), 15000);
            if (r.Status == 0) return "Kulia tidak terjangkau";
            if (!r.Ok) return "Kode salah atau kedaluwarsa";
            lock (sync)
            {
                Take(Json.Parse(r.Body));
                email = address;
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
            if (refresh == null) return "Belum masuk ke Kulia.";
            if (access != null && DateTime.UtcNow < accessUntilUtc) return null;
            var r = Web.Send("POST", url + "/auth/v1/token?grant_type=refresh_token", Headers(false),
                Json.Encode(new Dictionary<string, object> { { "refresh_token", refresh } }), 15000);
            if (r.Status == 0) return "Kulia tidak terjangkau · dicoba lagi otomatis";
            if (r.Status == 400 || r.Status == 401)
            {
                access = refresh = null;
                Vault.Delete(SessionFile);
                return "Sesi Kulia habis";
            }
            if (!r.Ok) return "Kulia sedang bermasalah (" + r.Status + ").";
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

        public List<KuliaTask> Feed(out string error)
        {
            lock (sync)
            {
                error = EnsureAccess();
                if (error != null) return null;
                if (!classKnown && userId != null)
                {
                    var pr = Call("GET", "/rest/v1/profiles?select=class_label&id=eq." + userId, null, null);
                    if (pr.Ok)
                    {
                        var rows = Json.Parse(pr.Body) as object[];
                        if (rows != null && rows.Length > 0) classLabel = Json.Str(rows[0], "class_label");
                        classKnown = true;
                    }
                }
                var r = Call("POST", "/rest/v1/rpc/my_feed", "{}", null);
                if (r.Status == 0) { error = "Kulia tidak terjangkau · dicoba lagi otomatis"; return null; }
                if (!r.Ok) { error = "Kulia menolak permintaan (" + r.Status + ")."; return null; }
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
                list.Add(new KuliaTask { Id = Json.Str(t, "id"), Title = Json.Str(t, "title"), Course = "Pribadi", Status = Json.Str(t, "status"), DeadlineUtc = Json.Time(t, "deadline_at"), Personal = true });

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

    // Email + 6-digit code, like kulia.live/masuk
    class KuliaLogin : Form
    {
        readonly KuliaClient client;
        readonly Label heading = new Label(), hint = new Label(), status = new Label();
        readonly TextBox field = new TextBox();
        readonly Button primary = new Button(), back = new Button();
        string address;
        bool codeStep;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public KuliaLogin(KuliaClient client)
        {
            this.client = client;
            Text = "Masuk ke Kulia";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(400, 236);
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
            heading.Text = "Masuk ke Kulia";
            hint.SetBounds(24, 54, 352, 40);
            hint.ForeColor = sub;
            field.SetBounds(24, 100, 352, 30);
            field.BorderStyle = BorderStyle.FixedSingle;
            field.BackColor = Ink.Light ? Color.White : Color.FromArgb(24, 24, 24);
            field.ForeColor = fg;
            field.Font = Ink.Text(15f);
            status.SetBounds(24, 136, 352, 36);
            status.ForeColor = fg;

            primary.SetBounds(236, 184, 140, 34);
            Style(primary, fg, bg);
            back.SetBounds(24, 184, 140, 34);
            Style(back, bg, fg);
            back.FlatAppearance.BorderColor = sub;
            back.FlatAppearance.BorderSize = 1;
            back.Text = "Ganti email";
            back.Click += delegate { ShowEmailStep(); };
            primary.Click += delegate { Submit(); };
            AcceptButton = primary;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            Controls.AddRange(new Control[] { heading, hint, field, status, primary, back });
            ShowEmailStep();
            if (client.Email.Length > 0) field.Text = client.Email;
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

        void ShowEmailStep()
        {
            codeStep = false;
            hint.Text = "Email akun kulia.live";
            field.Text = address ?? client.Email;
            field.MaxLength = 120;
            Native.SendMessage(field.Handle, 0x1501, (IntPtr)1, "email@contoh.com");
            primary.Text = "Kirim kode";
            back.Visible = false;
            status.Text = "";
            field.Focus();
        }

        void ShowCodeStep()
        {
            codeStep = true;
            hint.Text = "Kode 6 angka terkirim ke " + address;
            field.Text = "";
            field.MaxLength = 10;
            Native.SendMessage(field.Handle, 0x1501, (IntPtr)1, "6 angka");
            primary.Text = "Masuk";
            back.Visible = true;
            status.Text = "";
            field.Focus();
        }

        void Submit()
        {
            string value = field.Text.Trim();
            if (!codeStep)
            {
                if (!Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) { status.Text = "Format email belum lengkap"; return; }
                address = value;
                Busy("Mengirim kode…");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string err;
                    try { err = client.SendCode(address); } catch (Exception ex) { err = "Gagal mengirim kode"; Log.Write("kulia otp: " + ex.Message); }
                    Finish(err, delegate { ShowCodeStep(); });
                });
                return;
            }
            if (!Regex.IsMatch(value, @"^\d{6,10}$")) { status.Text = "Kode berisi 6 angka"; return; }
            Busy("Memeriksa kode…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                try { err = client.Verify(address, value); } catch (Exception ex) { err = "Gagal memeriksa kode"; Log.Write("kulia verify: " + ex.Message); }
                Finish(err, delegate { DialogResult = DialogResult.OK; Close(); });
            });
        }

        void Busy(string text)
        {
            status.Text = text;
            primary.Enabled = field.Enabled = back.Enabled = false;
        }

        void Finish(string error, Action ok)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                primary.Enabled = field.Enabled = back.Enabled = true;
                if (error != null) { status.Text = error; field.Focus(); return; }
                ok();
            }));
        }
    }
}
