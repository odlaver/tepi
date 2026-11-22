using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Text box in a well; Enter submits
    class KitField : Panel
    {
        public readonly TextBox Box = new KitText();
        public Action Submit;
        readonly float ui;

        public KitField(float ui, bool secret, string cue, int max)
        {
            this.ui = ui;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size((int)(196 * ui), (int)(30 * ui));
            Box.BorderStyle = BorderStyle.None;
            Box.UseSystemPasswordChar = secret;
            Box.MaxLength = max;
            Box.Font = new Font(Ink.MenuFamily, 12.5f * ui, GraphicsUnit.Pixel);
            Box.BackColor = KitInk.Line;
            Box.ForeColor = KitInk.Text;
            Box.AccessibleName = cue;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (Submit != null) Submit();
            };
            Box.HandleCreated += delegate { Native.SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue); };
            Controls.Add(Box);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int h = Box.PreferredHeight;
            Box.SetBounds((int)(10 * ui), (Height - h) / 2, Width - (int)(20 * ui), h);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Ink.Round(r, 8 * ui))
            using (var b = new SolidBrush(KitInk.Line)) g.FillPath(b, path);
            if (Box.Focused)
                using (var path = Ink.Round(r, 8 * ui))
                using (var pen = new Pen(KitInk.Text, 1.6f)) g.DrawPath(pen, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Box.Font != null) Box.Font.Dispose();
            base.Dispose(disposing);
        }
    }

    partial class Island
    {
        partial class PrefsWindow
        {
            readonly Dictionary<string, KitRow> statusRows = new Dictionary<string, KitRow>();
            bool sourcesHooked;

            public void ShowSection(string id) { Go(id); }

            // Rebuild after the current event finishes
            void Later(Action a)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(a); } catch (InvalidOperationException) { }
            }

            // Keeps the scroll position
            void Rebuild()
            {
                Later(delegate
                {
                    if (IsDisposed) return;
                    var at = content.AutoScrollPosition;
                    Relayout();
                    content.AutoScrollPosition = new Point(-at.X, -at.Y);
                });
            }

            static void Say(KitRow row, string text)
            {
                if (row == null || row.IsDisposed) return;
                row.Detail = text ?? "";
                row.Invalidate();
            }

            const string NotionTokenUrl = "https://www.notion.so/my-integrations", TodoistTokenUrl = "https://app.todoist.com/app/settings/integrations/developer";
            const string KuliaUrl = "https://kulia.live", SpotifyDashUrl = "https://developer.spotify.com/dashboard", SpotifyRedirect = "http://127.0.0.1/callback";
            const string GoogleCalUrl = "https://calendar.google.com/calendar/r/settings", ICloudCalUrl = "https://www.icloud.com/calendar/";
            const string OutlookHomeUrl = "https://outlook.live.com/calendar/0/options/calendar/SharedCalendars", OutlookWorkUrl = "https://outlook.office.com/calendar/options/calendar/SharedCalendars";

            // 0 Google, 1 Outlook, 2 iCloud, 3 other
            int calFrom;
            readonly HashSet<string> guideOpen = new HashSet<string>();

            // Steps stay folded until asked
            bool Guide(Action<KitRow> add, string id, string title)
            {
                bool open = guideOpen.Contains(id);
                var b = Button(open ? Lang.T("Sembunyikan") : Lang.T("Lihat langkah"), false, delegate { if (!guideOpen.Remove(id)) guideOpen.Add(id); Rebuild(); });
                b.AccessibleName = title;
                add(Step('\uE897', title, null, b));
                return open;
            }

            // Numbered setup row with its icon
            KitRow Step(char glyph, string title, string detail, Control field)
            {
                var r = Row(title, detail, field);
                r.Glyph = glyph;
                return r;
            }

            KitButton CopyButton(string text, Func<KitRow> row)
            {
                return Button(Lang.T("Salin"), false, delegate
                {
                    try { Clipboard.SetText(text); Say(row(), Lang.F("{0} tersalin", text)); }
                    catch (Exception ex) { Log.Write("salin: " + ex.Message); }
                });
            }

            Control Pair(Control a, Control b)
            {
                var p = new Panel { BackColor = KitInk.Group };
                int gap = (int)(8 * Ui);
                p.Size = new Size(a.Width + gap + b.Width, Math.Max(a.Height, b.Height));
                a.Location = new Point(0, (p.Height - a.Height) / 2);
                b.Location = new Point(a.Width + gap, (p.Height - b.Height) / 2);
                p.Controls.Add(a);
                p.Controls.Add(b);
                return p;
            }

            // Kulia, Notion, Todoist, iCal calendars
            void Integrations(Action<KitRow> add, Action<string> cap, bool show)
            {
                if (show)
                {
                    statusRows.Clear();
                    HookSources();
                }
                cap("Kulia");
                if (island.KuliaOn)
                {
                    add(Row("Kulia", island.kulia.Email, Button(Lang.T("Keluar dari Kulia"), false, delegate { island.SignOutKulia(); Rebuild(); })));
                    add(Row(Lang.T("Tugas dan jadwal"), Lang.T("Diperbarui tiap 10 menit"), Button(Lang.T("Perbarui"), false, delegate { island.RefreshTasks(true); })));
                }
                else
                {
                    bool open = Guide(add, "kulia", Lang.T("Cara masuk ke Kulia"));
                    if (open) add(Step('\uE774', Lang.T("1. Siapkan akun Kulia"), Lang.T("Belum punya? Daftar dulu di kulia.live"), Button(Lang.T("Buka kulia.live"), false, delegate { OpenUrl(KuliaUrl); })));
                    add(Step('\uE77B', open ? Lang.T("2. Masuk dengan email akun itu") : "Kulia", open ? Lang.T("Kode masuk dikirim ke email itu, lalu ketik di Tepi") : Lang.T("Belum masuk"),
                        Button(Lang.T("Masuk"), true, delegate { island.OpenLogin(); Rebuild(); })));
                }
                var hub = island.sources;
                if (hub == null) return;
                cap("Notion");
                NotionRows(hub, add);
                cap("Todoist");
                TodoistRows(hub, add);
                cap("Spotify");
                SpotifyRows(add);
                cap(Lang.T("Kalender"));
                CalendarRows(hub, add);
            }

            void HookSources()
            {
                if (sourcesHooked) return;
                sourcesHooked = true;
                Action h = OnSources;
                island.SourcesChanged += h;
                FormClosed += delegate { island.SourcesChanged -= h; };
            }

            // Status lines follow syncs without a rebuild
            void OnSources()
            {
                if (IsDisposed || island.sources == null) return;
                foreach (var kv in statusRows)
                {
                    var s = island.sources.Find(kv.Key);
                    if (s == null || kv.Value.IsDisposed) continue;
                    string d = Detail(s);
                    if (d != kv.Value.Detail) Say(kv.Value, d);
                }
            }

            static string Detail(Source s)
            {
                string status = SourceHub.Status(s);
                var feed = s as IcsFeed;
                if (feed != null) return feed.Host + " · " + status;
                var n = s as NotionSource;
                if (n != null && n.Workspace.Length > 0) return n.Workspace + " · " + status;
                return status;
            }

            void NotionRows(SourceHub hub, Action<KitRow> add)
            {
                var n = hub.Notion;
                if (n == null)
                {
                    bool open = Guide(add, "notion", Lang.T("Cara menyambungkan Notion"));
                    if (open)
                    {
                        add(Step('\uE774', Lang.T("1. Buat integrasi internal"), Lang.T("Tekan New integration, pilih workspace, lalu Save"), Button(Lang.T("Buka Notion"), false, delegate { OpenUrl(NotionTokenUrl); })));
                        add(Step('\uE8D7', Lang.T("2. Beri izin baca dan ubah"), Lang.T("Di Capabilities, centang Read content dan Update content"), null));
                        add(Step('\uE72D', Lang.T("3. Bagikan database tugasmu"), Lang.T("Buka database-nya di Notion: ••• › Connections › pilih integrasi tadi"), null));
                    }
                    var field = new KitField(Ui, true, Lang.T("Token Notion"), 400);
                    KitRow row = null;
                    KitButton go = null;
                    go = Button(Lang.T("Hubungkan"), true, delegate { ConnectNotion(field, go, row); });
                    field.Submit = delegate { go.PerformClick(); };
                    row = Step('\uE77F', open ? Lang.T("4. Tempel Internal Integration Secret") : "Notion", open ? Lang.T("Diawali ntn_ atau secret_") : Lang.T("Token integrasi internal"), Pair(field, go));
                    add(row);
                    return;
                }
                var status = Row("Notion", Detail(n), Button(Lang.T("Lepas"), false, delegate { hub.DropNotion(); Rebuild(); }));
                statusRows[n.Key] = status;
                add(status);
                if (n.Dbs.Count == 0) add(Row(Lang.T("Belum ada database"), Lang.T("Di Notion: ••• › Connections › integrasi ini"), null));
                foreach (var db in n.Dbs)
                {
                    var d = db;
                    var on = new KitSwitch { Checked = d.On, Size = new Size((int)(46 * Ui), (int)(26 * Ui)), Enabled = d.Dates.Count > 0 };
                    on.CheckedChanged += delegate
                    {
                        // Stale row after disconnect: never re-save
                        var live = building ? null : LiveDb(hub, n, d.Id);
                        if (live == null) return;
                        live.On = on.Checked;
                        n.Save();
                        hub.Touch(n);
                        Rebuild();
                    };
                    add(Row(d.Title, d.Dates.Count == 0 ? Lang.T("Tanpa kolom tanggal") : Columns(d), on));
                    if (!d.On || d.Dates.Count == 0) continue;
                    var dates = d.Dates.ConvertAll(x => Source.Clean(x.Name, 40)).ToArray();
                    int at = d.Date == null ? 0 : Math.Max(0, d.Dates.FindIndex(x => x.Id == d.Date.Id));
                    add(Row(Lang.T("Kolom tanggal"), null, Popup(dates, at, i =>
                    {
                        var live = LiveDb(hub, n, d.Id);
                        var pick = live == null ? null : live.Dates.Find(x => x.Id == d.Dates[i].Id);
                        if (pick == null) { Rebuild(); return; }
                        live.Date = pick;
                        n.Save();
                        hub.Touch(n);
                        Rebuild();
                    })));
                    var dones = new List<string> { Lang.T("Tidak ada") };
                    foreach (var x in d.Dones) dones.Add(Source.Clean(x.Name, 40) + (x.Type == "checkbox" ? Lang.T(" (centang)") : ""));
                    int doneAt = d.Done == null ? 0 : d.Dones.FindIndex(x => x.Id == d.Done.Id) + 1;
                    add(Row(Lang.T("Kolom selesai"), d.Done == null ? Lang.T("Tanpa ini, tugas hanya bisa dibaca") : null, Popup(dones.ToArray(), Math.Max(0, doneAt), i =>
                    {
                        var live = LiveDb(hub, n, d.Id);
                        var pick = live == null || i == 0 ? null : live.Dones.Find(x => x.Id == d.Dones[i - 1].Id);
                        if (live == null || (i > 0 && pick == null)) { Rebuild(); return; }
                        live.Done = pick;
                        n.Save();
                        hub.Touch(n);
                        Rebuild();
                    })));
                }
                var link = new KitField(Ui, false, Lang.T("Tautan database"), 2000);
                KitRow linkRow = null;
                KitButton addBtn = null;
                addBtn = Button(Lang.T("Tambah"), false, delegate { AddNotionLink(link, addBtn, linkRow); });
                link.Submit = delegate { addBtn.PerformClick(); };
                linkRow = Row(Lang.T("Tambah database"), Lang.T("Database yang belum muncul di daftar"), Pair(link, addBtn));
                add(linkRow);
                KitRow scanRow = null;
                KitButton scanBtn = null;
                scanBtn = Button(Lang.T("Muat ulang"), false, delegate { RescanNotion(scanBtn, scanRow); });
                scanRow = Row(Lang.T("Daftar database"), Lang.T("Database yang dibagikan ke integrasi"), scanBtn);
                add(scanRow);
            }

            // Absorb may swap the row's database
            static NotionDb LiveDb(SourceHub hub, NotionSource n, string id)
            {
                return hub.Notion == n ? n.Dbs.Find(x => x.Id == id) : null;
            }

            static string Columns(NotionDb d)
            {
                return Lang.F("Tanggal: {0} · Selesai: {1}", d.Date != null ? Source.Clean(d.Date.Name, 30) : "-", d.Done != null ? Source.Clean(d.Done.Name, 30) : Lang.T("tidak ada"));
            }

            void ConnectNotion(KitField field, KitButton go, KitRow row)
            {
                string token = field.Box.Text.Trim();
                if (token.Length == 0) { Say(row, Lang.T("Token kosong")); field.Box.Focus(); return; }
                go.Enabled = field.Enabled = false;
                Say(row, Lang.T("Memeriksa token…"));
                ThreadPool.QueueUserWorkItem(delegate
                {
                    NotionSource made;
                    string err;
                    try { err = NotionSource.Connect(token, out made); }
                    catch (Exception ex) { made = null; err = Lang.T("Gagal terhubung"); Log.Write("notion connect: " + ex.GetType().Name); }
                    island.Post(delegate
                    {
                        if (err == null) { island.sources.SetNotion(made); Rebuild(); return; }
                        if (go.IsDisposed) return;
                        go.Enabled = field.Enabled = true;
                        Say(row, err);
                    });
                });
            }

            void AddNotionLink(KitField field, KitButton go, KitRow row)
            {
                var n = island.sources.Notion;
                string text = field.Box.Text.Trim();
                if (n == null) return;
                if (NotionSource.DatabaseId(text) == null) { Say(row, Lang.T("Tautan database tidak dikenali")); return; }
                go.Enabled = field.Enabled = false;
                Say(row, Lang.T("Memeriksa tautan…"));
                string token = n.Token;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    List<NotionDb> added;
                    string err;
                    try { err = NotionSource.AddLink(token, text, out added); }
                    catch (Exception ex) { added = null; err = Lang.T("Gagal terhubung"); Log.Write("notion link: " + ex.GetType().Name); }
                    island.Post(delegate
                    {
                        var live = island.sources.Notion;
                        if (err == null && live != null && live.Token == token)
                        {
                            foreach (var db in added) if (!live.Dbs.Exists(x => x.Id == db.Id)) live.Dbs.Add(db);
                            live.Save();
                            island.sources.Touch(live);
                            Rebuild();
                            return;
                        }
                        if (go.IsDisposed) return;
                        go.Enabled = field.Enabled = true;
                        Say(row, err);
                    });
                });
            }

            void RescanNotion(KitButton go, KitRow row)
            {
                var n = island.sources.Notion;
                if (n == null) return;
                go.Enabled = false;
                Say(row, Lang.T("Mencari database…"));
                var copy = (NotionSource)n.Snapshot();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string err;
                    try { err = NotionSource.Scan(copy, copy.Dbs); }
                    catch (Exception ex) { err = Lang.T("Gagal terhubung"); Log.Write("notion scan: " + ex.GetType().Name); }
                    island.Post(delegate
                    {
                        var live = island.sources.Notion;
                        if (err == null && live != null && live.Token == copy.Token)
                        {
                            live.Dbs = copy.Dbs;
                            live.Save();
                            island.sources.Touch(live);
                            Rebuild();
                            return;
                        }
                        if (go.IsDisposed) return;
                        go.Enabled = true;
                        Say(row, err ?? "");
                    });
                });
            }

            // Each user brings own Spotify app
            void SpotifyRows(Action<KitRow> add)
            {
                if (SpotifyLink.Connected)
                {
                    string who = SpotifyLink.User.Length > 0 ? SpotifyLink.User : "Spotify";
                    add(Row("Spotify", SpotifyLink.Premium ? Lang.F("{0} · Premium. Coba \"putar Evaluasi dari Hindia\".", who) : Lang.F("{0} · bukan Premium, jadi lagu pilihan tidak bisa diputar", who),
                        Button(Lang.T("Lepas"), false, delegate { SpotifyLink.Forget(); Rebuild(); })));
                    return;
                }
                var field = new KitField(Ui, true, Lang.T("Client ID Spotify"), 200);
                KitRow row = null;
                KitButton go = null;
                go = Button(Lang.T("Masuk"), true, delegate
                {
                    string id = field.Box.Text.Trim();
                    if (id.Length == 0) { Say(row, Lang.T("Client ID kosong")); field.Box.Focus(); return; }
                    go.Enabled = field.Enabled = false;
                    Say(row, Lang.T("Selesaikan login di browser…"));
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err = SpotifyLink.SignIn(id);
                        island.Post(delegate
                        {
                            if (err == null) { Rebuild(); return; }
                            if (go.IsDisposed) return;
                            go.Enabled = field.Enabled = true;
                            Say(row, err);
                        });
                    });
                });
                field.Submit = delegate { go.PerformClick(); };
                bool open = Guide(add, "spotify", Lang.T("Cara menyambungkan Spotify"));
                if (open)
                {
                    add(Step('\uE774', Lang.T("1. Buat app di dashboard Spotify"), Lang.T("Tekan Create app, isi nama dan deskripsi bebas"), Button(Lang.T("Buka dashboard"), false, delegate { OpenUrl(SpotifyDashUrl); })));
                    KitRow uri = null;
                    uri = Step('\uE8C8', Lang.T("2. Isi Redirect URIs"), Lang.F("Tempel {0}, centang Web API, lalu Save", SpotifyRedirect), CopyButton(SpotifyRedirect, () => uri));
                    add(uri);
                }
                row = Step('\uE77F', open ? Lang.T("3. Tempel Client ID") : "Spotify", open ? Lang.T("Ada di halaman app tadi") : Lang.T("Butuh Premium. Client ID app-mu"), Pair(field, go));
                add(row);
            }

            void TodoistRows(SourceHub hub, Action<KitRow> add)
            {
                var t = hub.Todoist;
                if (t != null)
                {
                    var status = Row("Todoist", Detail(t), Button(Lang.T("Lepas"), false, delegate { hub.DropTodoist(); Rebuild(); }));
                    statusRows[t.Key] = status;
                    add(status);
                    return;
                }
                var field = new KitField(Ui, true, Lang.T("Token API Todoist"), 200);
                KitRow row = null;
                KitButton go = null;
                go = Button(Lang.T("Hubungkan"), true, delegate
                {
                    string token = field.Box.Text.Trim();
                    if (token.Length == 0) { Say(row, Lang.T("Token kosong")); field.Box.Focus(); return; }
                    go.Enabled = field.Enabled = false;
                    Say(row, Lang.T("Memeriksa token…"));
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err;
                        try { err = TodoistSource.Check(token); }
                        catch (Exception ex) { err = Lang.T("Gagal terhubung"); Log.Write("todoist connect: " + ex.GetType().Name); }
                        island.Post(delegate
                        {
                            if (err == null) { island.sources.SetTodoist(token); Rebuild(); return; }
                            if (go.IsDisposed) return;
                            go.Enabled = field.Enabled = true;
                            Say(row, err);
                        });
                    });
                });
                field.Submit = delegate { go.PerformClick(); };
                bool open = Guide(add, "todoist", Lang.T("Cara menyambungkan Todoist"));
                if (open) add(Step('\uE774', Lang.T("1. Buka halaman Developer Todoist"), Lang.T("Masuk ke akun Todoist-mu kalau diminta"), Button(Lang.T("Buka Todoist"), false, delegate { OpenUrl(TodoistTokenUrl); })));
                row = Step('\uE77F', open ? Lang.T("2. Salin API token, tempel di sini") : "Todoist", open ? Lang.T("Tombol salinnya di bagian API token") : Lang.T("Token API dari Todoist"), Pair(field, go));
                add(row);
            }

            void CalendarRows(SourceHub hub, Action<KitRow> add)
            {
                foreach (var feed in hub.Feeds)
                {
                    var f = feed;
                    var kind = Popup(new[] { Lang.T("Jadwal"), Lang.T("Tugas") }, f.Kind == "tugas" ? 1 : 0, i => { hub.SetFeedKind(f, i == 1 ? "tugas" : "jadwal"); Rebuild(); });
                    kind.AccessibleName = Lang.F("Isi {0}", f.Name);
                    var del = Button(Lang.T("Hapus"), false, delegate { hub.RemoveFeed(f); Rebuild(); });
                    del.AccessibleName = Lang.F("Hapus {0}", f.Name);
                    var row = Row(f.Name, Detail(f), Pair(kind, del));
                    statusRows[f.Key] = row;
                    add(row);
                }
                string pasteTitle = Lang.T("Tambah kalender"), pasteNote = Lang.T("Tautan https atau webcal");
                bool open = Guide(add, "kalender", Lang.T("Cara mengambil tautan iCal"));
                if (open) add(Step('\uE787', Lang.T("Kalendermu ada di"), null, Seg(new[] { "Google", "Outlook", "iCloud", Lang.T("Lain") }, calFrom, i => { calFrom = i; Rebuild(); })));
                if (open) switch (calFrom)
                {
                    case 0:
                        add(Step('\uE774', Lang.T("1. Buka Setelan Google Calendar"), Lang.T("Lewat browser, aplikasi Google Calendar di HP tidak punya menu ini"), Button(Lang.T("Buka Setelan"), false, delegate { OpenUrl(GoogleCalUrl); })));
                        add(Step('\uE713', Lang.T("2. Pilih kalendermu"), Lang.T("Di kiri: Setelan untuk kalender saya › nama kalender › Integrasikan kalender"), null));
                        pasteTitle = Lang.T("3. Tempel alamat rahasia format iCal");
                        pasteNote = Lang.T("Bukan alamat publik");
                        break;
                    case 1:
                        add(Step('\uE774', Lang.T("1. Buka Kalender bersama di Outlook"), Lang.T("Pilih sesuai akun Microsoft-mu"), Pair(
                            Button(Lang.T("Akun pribadi"), false, delegate { OpenUrl(OutlookHomeUrl); }),
                            Button(Lang.T("Akun kantor/kampus"), false, delegate { OpenUrl(OutlookWorkUrl); }))));
                        add(Step('\uE72D', Lang.T("2. Terbitkan kalender"), Lang.T("Pilih kalendernya, Dapat melihat semua detail, lalu Terbitkan"), null));
                        pasteTitle = Lang.T("3. Salin tautan ICS, tempel di sini");
                        pasteNote = Lang.T("Yang ICS, bukan HTML");
                        break;
                    case 2:
                        add(Step('\uE774', Lang.T("1. Buka Kalender di iCloud"), Lang.T("Masuk dengan Apple ID yang dipakai iPhone-mu"), Button(Lang.T("Buka iCloud"), false, delegate { OpenUrl(ICloudCalUrl); })));
                        add(Step('\uE72D', Lang.T("2. Jadikan kalendernya publik"), Lang.T("Klik ikon bagikan di samping nama kalender, centang kalender publik"), null));
                        pasteTitle = Lang.T("3. Salin tautannya, tempel di sini");
                        pasteNote = Lang.T("Diawali webcal://");
                        break;
                    default:
                        add(Step('\uE721', Lang.T("1. Cari menu ekspor kalender"), Lang.T("Di pengaturan aplikasinya, biasanya bernama iCal, ICS, webcal, atau langganan"), null));
                        add(Step('\uE946', Lang.T("Contoh: Moodle kampus"), Lang.T("Kalender › Impor atau ekspor kalender › Ekspor › Dapatkan URL kalender"), null));
                        pasteTitle = Lang.T("2. Tempel tautannya di sini");
                        pasteNote = Lang.T("Diawali https atau webcal");
                        break;
                }
                var field = new KitField(Ui, false, Lang.T("Tautan iCal"), 2000);
                KitRow addRow = null;
                KitButton go = null;
                go = Button(Lang.T("Tambah"), true, delegate { AddCalendar(field, go, addRow); });
                field.Submit = delegate { go.PerformClick(); };
                addRow = Step('\uE77F', pasteTitle, pasteNote, Pair(field, go));
                add(addRow);
            }

            void AddCalendar(KitField field, KitButton go, KitRow row)
            {
                var hub = island.sources;
                string url = IcsFeed.Normalize(field.Box.Text);
                if (url == null) { Say(row, Lang.T("Tautan harus https atau webcal")); field.Box.Focus(); return; }
                if (hub.Feeds.Exists(x => x.Url == url)) { Say(row, Lang.T("Kalender ini sudah ada")); return; }
                if (hub.Feeds.Count >= 12) { Say(row, Lang.T("Paling banyak 12 kalender")); return; }
                go.Enabled = field.Enabled = false;
                Say(row, Lang.T("Memeriksa tautan…"));
                string kind = IcsFeed.GuessKind(url), id = IcsFeed.NewId();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    SourceResult r;
                    try { r = IcsFeed.Pull(url, kind, "", "ics:" + id, DateTime.UtcNow); }
                    catch (Exception ex) { r = SourceResult.Fail(Lang.T("Gagal memuat"), ex.GetType().Name, true); }
                    island.Post(delegate
                    {
                        if (r.Error == null)
                        {
                            if (!hub.Feeds.Exists(x => x.Url == url) && hub.Feeds.Count < 12) hub.AddFeed(id, url, kind, r);
                            Rebuild();
                            return;
                        }
                        Log.Write("source ics add: " + (r.Log ?? "error"));
                        if (go.IsDisposed) return;
                        go.Enabled = field.Enabled = true;
                        Say(row, r.Error);
                    });
                });
            }
        }
    }
}
