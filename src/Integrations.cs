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

            public void ShowSection(string id)
            {
                section = id;
                if (!IsHandleCreated) return;
                building = true;
                search.Text = "";
                building = false;
                side.Selected = id;
                side.Invalidate();
                Fill();
            }

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
                bool signed = island.KuliaOn;
                add(Row("Kulia", signed ? island.kulia.Email : Lang.T("Belum masuk"),
                    signed ? (Control)Button(Lang.T("Keluar dari Kulia"), false, delegate { island.SignOutKulia(); Rebuild(); })
                           : Button(Lang.T("Masuk"), true, delegate { island.OpenLogin(); Rebuild(); })));
                if (signed) add(Row(Lang.T("Tugas dan jadwal"), Lang.T("Diperbarui tiap 10 menit"), Button(Lang.T("Perbarui"), false, delegate { island.RefreshTasks(true); })));
                var hub = island.sources;
                if (hub == null) return;
                cap("Notion");
                NotionRows(hub, add);
                cap("Todoist");
                TodoistRows(hub, add);
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
                    var field = new KitField(Ui, true, Lang.T("Token Notion"), 400);
                    KitRow row = null;
                    KitButton go = null;
                    go = Button(Lang.T("Hubungkan"), true, delegate { ConnectNotion(field, go, row); });
                    field.Submit = delegate { go.PerformClick(); };
                    row = Row("Notion", Lang.T("Token integrasi atau token pribadi"), Pair(field, go));
                    add(row);
                    return;
                }
                var status = Row("Notion", Detail(n), Button(Lang.T("Lepas"), false, delegate { hub.DropNotion(); Rebuild(); }));
                statusRows[n.Key] = status;
                add(status);
                if (n.Dbs.Count == 0) add(Row(Lang.T("Belum ada database"), Lang.T("Di Notion: ••• › Koneksi › integrasi ini"), null));
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
                row = Row("Todoist", Lang.T("Token dari Integrasi › Developer"), Pair(field, go));
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
                var field = new KitField(Ui, false, Lang.T("Tautan iCal"), 2000);
                KitRow addRow = null;
                KitButton go = null;
                go = Button(Lang.T("Tambah"), true, delegate { AddCalendar(field, go, addRow); });
                field.Submit = delegate { go.PerformClick(); };
                addRow = Row(Lang.T("Tambah kalender"), Lang.T("Tautan .ics atau webcal, disimpan terenkripsi"), Pair(field, go));
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
