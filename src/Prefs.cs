using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Settings window; every change applies live
    partial class Island
    {
        PrefsWindow prefs;

        void OpenPrefs()
        {
            if (prefs != null && !prefs.IsDisposed) { prefs.Activate(); return; }
            prefs = new PrefsWindow(this);
            prefs.Show();
        }

        void SetLight(bool light)
        {
            settings.Set("theme", light ? "terang" : "gelap");
            settings.Save();
            Ink.Apply(light);
            ResetFrost();
            Kick();
        }

        void SetAccentHex(string hex)
        {
            settings.Set("accent", hex);
            settings.Save();
            Ink.SetAccent(hex);
            Kick();
        }

        void SetFlag(string key, bool on)
        {
            settings.Set(key, on);
            settings.Save();
            if (key == "dock") ResetFrost();
            else if (key == "notif") notifWatcher.Enabled = NotifOn;
            else if (key == "lyrics.online") { lyrics = null; SyncLyrics(); }
            else if (key == "clipboard")
            {
                ApplyClips();
                if (on && Array.IndexOf(tabs, PageShelf) < 0) SetLayout("panel.tabs", TabIds(tabs) + ",shelf");
            }
            else if (key == "downloads") ApplyDownloads();
            else if (key == "capture.show") ApplyGlass();
            else if (key == "shelf") InitShelfDrop();
            Kick();
        }

        void SetChoice(string key, string value)
        {
            settings.Set(key, value);
            settings.Save();
            if (key == "material") { ApplyGlass(); ResetFrost(); }
            else if (key == "motion.reduce") Anim.Read(settings);
            else if (key == "tabs.style") tabSnap = true;
            Kick();
        }

        void SetLanguage(bool english)
        {
            settings.Set("lang", english ? "en" : "id");
            settings.Save();
            Lang.English = english;
            BuildTray();
            Kick();
        }

        void SetLayout(string key, object value)
        {
            settings.Set(key, value);
            settings.Save();
            LoadLayout();
            ResetFrost();
            Kick();
        }

        // Open the panel while layout is being tuned
        void PreviewLayout(bool on)
        {
            if (on && !expanded) Expand(DefaultPage(false));
            else if (!on && expanded && !pointerInside) { expanded = false; Kick(); }
        }

        // Random port; Claude's hook URL follows
        void MoveToRandomPort()
        {
            bool claude = HooksInstalled();
            int port = AgentHub.FreePort();
            if (hub != null) hub.Stop();
            settings.Set("agent.port", port);
            settings.Save();
            StartHub();
            if (claude)
            {
                string err = HookSetup.Install(port, settings.Get("agent.token", ""));
                if (err != null) ShowInfo(Ink.IcoTerminal, Lang.T("Belum terhubung"), err, true);
            }
            CheckLinks();
            ShowInfo(Ink.IcoTerminal, Lang.F("Port {0}", port), Lang.T("sesi agen baru memakainya"), true);
        }

        static readonly string[][] Accents =
        {
            new[] { "Monokrom", "" }, new[] { "Biru", "#4C8DFF" }, new[] { "Hijau", "#3DD68C" }, new[] { "Kuning", "#FFD24A" },
            new[] { "Oranye", "#FF9F43" }, new[] { "Merah muda", "#FF6B9A" }, new[] { "Ungu", "#A78BFA" }, new[] { "Navy", "#7F8DC6" },
        };

        partial class PrefsWindow : KitWindow
        {
            readonly Island island;
            readonly Settings settings;
            readonly Timer preview = new Timer { Interval = 2500 };
            string section = "general";
            readonly TextBox search = new KitText();
            readonly Panel content = new Panel();
            readonly SideList side = new SideList();
            bool building;

            static readonly string[][] Sections =
            {
                new[] { "general", "Umum", "" }, new[] { "look", "Tampilan", "" }, new[] { "island", "Island", "" },
                new[] { "activity", "Aktivitas", "" }, new[] { "agents", "Agen", "" }, new[] { "integrasi", "Integrasi", "" },
                new[] { "remind", "Pengingat", "" }, new[] { "access", "Aksesibilitas", "" }, new[] { "about", "Tentang", "" },
            };

            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

            public PrefsWindow(Island island)
            {
                this.island = island;
                settings = island.settings;
                Text = Lang.T("Pengaturan Tepi");
                StartPosition = FormStartPosition.Manual;
                Icon = island.tray != null ? island.tray.Icon : null;
                preview.Tick += delegate { preview.Stop(); island.PreviewLayout(false); };
                FormClosed += delegate { preview.Stop(); island.PreviewLayout(false); };
                content.AutoScroll = true;
                search.BorderStyle = BorderStyle.None;
                search.TextChanged += delegate { if (!building) { section = search.Text.Trim().Length > 0 ? "search" : section == "search" ? "general" : section; Fill(); } };
                side.Picked += id => { section = id; building = true; search.Text = ""; building = false; Fill(); };
                Controls.Add(search);
                Controls.Add(side);
                Controls.Add(content);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                var area = Screen.FromHandle(Handle).WorkingArea;
                Size = new Size((int)Math.Min(860 * Ui, area.Width - 40), (int)Math.Min(620 * Ui, area.Height - 40));
                MinimumSize = new Size((int)(640 * Ui), (int)(420 * Ui));
                Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
                Relayout();
            }

            protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) { Place(); Fill(); } }

            public override void Relayout()
            {
                BackColor = KitInk.Window;
                Native.DarkFrame(Handle, !Ink.Light);
                Text = Lang.T("Pengaturan Tepi");
                side.Items = new List<string[]>();
                foreach (var s in Sections) side.Items.Add(new[] { s[0], Lang.T(s[1]), s[2] });
                side.Selected = section;
                search.Font = UiFont(13f);
                search.BackColor = KitInk.Line;
                search.ForeColor = KitInk.Text;
                SendMessage(search.Handle, 0x1501, IntPtr.Zero, Lang.T("Cari pengaturan"));
                Place();
                Fill();
                Invalidate(true);
            }

            void Place()
            {
                float s = Ui;
                int sideW = (int)(220 * s);
                search.SetBounds((int)(24 * s), (int)(21 * s), sideW - (int)(44 * s), (int)(22 * s));
                side.SetBounds(0, (int)(58 * s), sideW, ClientSize.Height - (int)(58 * s));
                content.SetBounds(sideW, 0, Math.Max(10, ClientSize.Width - sideW), ClientSize.Height);
                content.BackColor = KitInk.Window;
                side.Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(KitInk.Window);
                int sideW = (int)(220 * Ui);
                using (var b = new SolidBrush(KitInk.Side)) e.Graphics.FillRectangle(b, 0, 0, sideW, ClientSize.Height);
                // Search field: a rounded well
                var r = new RectangleF(12 * Ui, 13 * Ui, sideW - 24 * Ui, 38 * Ui);
                KitInk.Smooth(e.Graphics);
                using (var path = Ink.Round(r, 10 * Ui))
                using (var b = new SolidBrush(KitInk.Line)) e.Graphics.FillPath(b, path);
            }

            // Page builder: title, then groups of rows

            float y;
            int rowW;

            void Fill()
            {
                if (!IsHandleCreated) return;
                building = true;
                content.SuspendLayout();
                var old = new List<Control>();
                foreach (Control c in content.Controls) old.Add(c);
                content.Controls.Clear();
                foreach (var c in old) c.Dispose();
                content.AutoScrollPosition = Point.Empty;
                rowW = content.ClientSize.Width - (int)(56 * Ui) - SystemInformation.VerticalScrollBarWidth;
                y = 22 * Ui;
                if (section == "search")
                {
                    string q = search.Text.Trim();
                    Heading(Lang.F("Hasil untuk \"{0}\"", q));
                    int found = 0;
                    foreach (var s in Sections)
                    {
                        var rows = new List<KitRow>();
                        Build(s[0], rows, false);
                        var hits = rows.FindAll(r => r.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || r.Detail.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
                        foreach (var r in rows) if (!hits.Contains(r)) r.Dispose();
                        if (hits.Count == 0) continue;
                        found += hits.Count;
                        Caption(Lang.T(s[1]));
                        Group(hits);
                    }
                    if (found == 0) Caption(Lang.T("Tidak ada yang cocok"));
                }
                else
                {
                    foreach (var s in Sections) if (s[0] == section) Heading(Lang.T(s[1]));
                    Build(section, new List<KitRow>(), true);
                }
                // Room under the last group
                var spacer = new Panel { Location = new Point(0, (int)y), Size = new Size(1, (int)(16 * Ui)), BackColor = KitInk.Window };
                content.Controls.Add(spacer);
                content.ResumeLayout();
                building = false;
            }

            void Heading(string text)
            {
                var l = new Label { Text = text, Font = UiFont(24f), ForeColor = KitInk.Text, BackColor = KitInk.Window, AutoSize = true };
                l.Location = new Point((int)(28 * Ui), (int)y);
                content.Controls.Add(l);
                y += 52 * Ui;
            }

            void Caption(string text)
            {
                var l = new Label { Text = text, Font = UiFont(12f), ForeColor = KitInk.Sub, BackColor = KitInk.Window, AutoSize = true };
                l.Location = new Point((int)(40 * Ui), (int)y);
                content.Controls.Add(l);
                y += 24 * Ui;
            }

            // Rows into one rounded group
            void Group(List<KitRow> rows)
            {
                if (rows.Count == 0) return;
                var g = new KitGroup { Left = (int)(28 * Ui), Top = (int)y, Width = rowW };
                int top = 0;
                foreach (var r in rows)
                {
                    r.Width = rowW;
                    r.Top = top;
                    r.Left = 0;
                    PlaceField(r);
                    g.Controls.Add(r);
                    top += r.Height;
                }
                g.Height = top;
                content.Controls.Add(g);
                y += top + 22 * Ui;
            }

            void PlaceField(KitRow r)
            {
                if (r.Field == null) return;
                r.Field.Left = r.Width - r.Field.Width - (int)(16 * Ui);
                r.Field.Top = (r.Height - r.Field.Height) / 2;
                if (!r.Controls.Contains(r.Field)) r.Controls.Add(r.Field);
            }

            KitRow Row(string title, string detail, Control field)
            {
                var r = new KitRow { Title = title, Detail = detail ?? "", Field = field, Height = (int)((string.IsNullOrEmpty(detail) ? 46 : 60) * Ui) };
                if (field != null)
                {
                    field.AccessibleName = title;
                    if (!string.IsNullOrEmpty(detail)) field.AccessibleDescription = detail;
                }
                return r;
            }

            // Controls with settings plumbing

            KitSwitch Switch(string key, bool fallback, Action<bool> after)
            {
                var s = new KitSwitch { Checked = settings.GetBool(key, fallback), Size = new Size((int)(46 * Ui), (int)(26 * Ui)) };
                s.CheckedChanged += delegate { if (building) return; if (after != null) after(s.Checked); else island.SetFlag(key, s.Checked); };
                return s;
            }

            KitSegmented Seg(string[] items, int selected, Action<int> changed)
            {
                float w = 0;
                using (var f = UiFont(12.5f)) foreach (var it in items) w = Math.Max(w, TextRenderer.MeasureText(it, f).Width);
                var c = new KitSegmented { Items = items, Size = new Size((int)((w + 30 * Ui) * items.Length), (int)(30 * Ui)) };
                c.Selected = selected;
                c.Changed += i => { if (!building) changed(i); };
                return c;
            }

            KitSlider Slider(int min, int max, int step, int value, Func<int, string> fmt, Action<int> changed)
            {
                var c = new KitSlider { Minimum = min, Maximum = max, Step = step, Value = Math.Max(min, Math.Min(max, value)), Format = fmt, Size = new Size((int)(200 * Ui), (int)(30 * Ui)) };
                c.Changed += v => { if (!building) { changed(v); content.Invalidate(true); } };
                return c;
            }

            KitPopup Popup(string[] items, int selected, Action<int> changed)
            {
                var c = new KitPopup { Items = items, Font = UiFont(12.5f) };
                c.Selected = selected;
                float w = 0;
                using (var f = UiFont(12.5f)) foreach (var it in items) w = Math.Max(w, TextRenderer.MeasureText(it + "  ▾", f).Width);
                c.Size = new Size((int)Math.Min(260 * Ui, w + 30 * Ui), (int)(30 * Ui));
                c.Changed += i => { if (!building) changed(i); };
                return c;
            }

            KitButton Button(string text, bool primary, Action click)
            {
                var b = new KitButton { Text = text, Primary = primary, Font = UiFont(12.5f) };
                b.Size = new Size(TextRenderer.MeasureText(text, b.Font).Width + (int)(32 * Ui), (int)(30 * Ui));
                b.Click += delegate { click(); };
                return b;
            }

            void Preview()
            {
                island.PreviewLayout(true);
                preview.Stop();
                preview.Start();
            }

            // Sections; search only fills the list
            void Build(string id, List<KitRow> rows, bool show)
            {
                var group = new List<KitRow>();
                Action flush = delegate { if (show) Group(group); group = new List<KitRow>(); };
                Action<string> cap = c => { if (show) { flush(); Caption(c); } };
                Action<KitRow> add = r => { rows.Add(r); group.Add(r); };
                switch (id)
                {
                    case "general":
                        add(Row(Lang.T("Bahasa"), null, Seg(new[] { "Indonesia", "English" }, Lang.English ? 1 : 0, i => { island.SetLanguage(i == 1); Relayout(); })));
                        add(Row(Lang.T("Mulai bersama Windows"), null, StartupSwitch()));
                        add(Row(Lang.T("Cek versi baru"), Lang.T("Sekali sehari ke GitHub; unduhan hanya saat diminta"), Switch("update.check", true, on => { settings.Set("update.check", on); settings.Save(); })));
                        cap(Lang.T("Data"));
                        add(Row(Lang.T("Folder data"), Settings.Dir, Button(Lang.T("Buka"), false, delegate { try { Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); } catch (Exception) { } })));
                        break;
                    case "look":
                        add(Row(Lang.T("Tema"), null, Seg(new[] { Lang.T("Gelap"), Lang.T("Terang") }, Ink.Light ? 1 : 0, i => { island.SetLight(i == 1); Relayout(); })));
                        string mat = island.MaterialName;
                        add(Row(Lang.T("Material"), Lang.T("Kaca membaca layar di belakangnya; Hitam paling hemat"),
                            Seg(new[] { "Liquid", "Frost", Lang.T("Hitam") }, mat == "frost" ? 1 : mat == "solid" ? 2 : 0, i => island.SetChoice("material", i == 1 ? "frost" : i == 2 ? "solid" : "liquid"))));
                        var names = new List<string>();
                        int at = 0;
                        string accent = settings.Get("accent", "");
                        for (int i = 0; i < Accents.Length; i++) { names.Add(Lang.T(Accents[i][0])); if (Accents[i][1] == accent) at = i; }
                        add(Row(Lang.T("Aksen"), settings.Get("theme.pack", "").Length > 0 ? Lang.T("Paket tema bisa menimpanya") : null, Popup(names.ToArray(), at, i => island.SetAccentHex(Accents[i][1]))));
                        cap(Lang.T("Paket tema"));
                        var packs = new List<string> { Lang.T("Tanpa") };
                        packs.AddRange(ThemePacks());
                        int packAt = Math.Max(0, packs.IndexOf(settings.Get("theme.pack", "")));
                        add(Row(Lang.T("Paket"), Lang.T("Folder di themes, berisi theme.ini dan gambar"), Popup(packs.ToArray(), packAt, i =>
                        {
                            settings.Set("theme.pack", i == 0 ? "" : packs[i]);
                            settings.Save();
                            island.ApplyPack();
                            Relayout();
                        })));
                        add(Row(Lang.T("Suara sapaan paket tema"), null, Switch("theme.voice", true, null)));
                        add(Row(Lang.T("Folder paket"), null, Button(Lang.T("Buka"), false, delegate
                        {
                            string dir = System.IO.Path.Combine(Settings.Dir, "themes");
                            try { System.IO.Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch (Exception) { }
                        })));
                        break;
                    case "island":
                        add(Row(Lang.T("Menempel di tepi atas"), null, Switch("dock", true, null)));
                        add(Row(Lang.T("Posisi"), null, Slider(-40, 40, 5, settings.GetInt("position", 0), v => v == 0 ? Lang.T("tengah") : (v < 0 ? Lang.T("kiri") : Lang.T("kanan")) + " " + Math.Abs(v) + "%",
                            v => { island.SetLayout("position", v); Preview(); })));
                        add(Row(Lang.T("Lebar panel"), null, Slider(440, 560, 8, (int)island.PanelW, v => v + " px", v => { island.SetLayout("panel.width", v); Preview(); })));
                        var screens = Screen.AllScreens;
                        var labels = new string[screens.Length];
                        string chosen = settings.Get("monitor", "");
                        int screenAt = 0;
                        for (int i = 0; i < screens.Length; i++)
                        {
                            labels[i] = Lang.F("Layar {0}", i + 1) + (screens[i].Primary ? " · " + Lang.T("utama") : "");
                            if (chosen.Length > 0 ? screens[i].DeviceName == chosen : screens[i].Primary) screenAt = i;
                        }
                        var mon = Popup(labels, screenAt, i => island.UseMonitor(screens[i].Primary ? "" : screens[i].DeviceName));
                        mon.Enabled = screens.Length > 1;
                        add(Row(Lang.T("Layar"), null, mon));
                        string mode = settings.Get("mini", "auto");
                        add(Row(Lang.T("Mode"), Lang.T("Otomatis: mini saat tepi atas tertutup atau 1 menit diam"),
                            Seg(new[] { Lang.T("Normal"), Lang.T("Mini"), Lang.T("Otomatis") }, mode == "normal" ? 0 : mode == "mini" ? 1 : 2, i => island.SetMiniMode(i == 0 ? "normal" : i == 1 ? "mini" : "auto"))));
                        add(Row(Lang.T("Notifikasi saat mini"), null, Switch("mini.notif", true, null)));
                        add(Row(Lang.T("Mata"), null, Switch("island.eyes", true, null)));
                        add(Row(Lang.T("Ikuti kursor"), null, Switch("island.follow", true, null)));
                        cap(Lang.T("Tab panel"));
                        string style = settings.Get("tabs.style", "auto");
                        add(Row(Lang.T("Gaya tab"), Lang.T("Otomatis: titik bila lebih dari tujuh tab"),
                            Seg(new[] { Lang.T("Otomatis"), Lang.T("Ikon"), Lang.T("Titik") }, style == "icons" ? 1 : style == "dots" ? 2 : 0, i => island.SetChoice("tabs.style", i == 1 ? "icons" : i == 2 ? "dots" : "auto"))));
                        var openNames = new List<string> { Lang.T("Tab pertama") };
                        foreach (var n in PageNames) openNames.Add(Lang.T(n));
                        int openAt = Array.IndexOf(PageIds, settings.Get("panel.first", "")) + 1;
                        add(Row(Lang.T("Dibuka di"), null, Popup(openNames.ToArray(), Math.Max(0, openAt), i => island.SetLayout("panel.first", i > 0 ? PageIds[i - 1] : ""))));
                        cap(Lang.T("Tab yang tampil"));
                        for (int pg = 0; pg < PageNames.Length; pg++)
                        {
                            int page = pg;
                            var sw = new KitSwitch { Checked = Array.IndexOf(island.tabs, pg) >= 0, Size = new Size((int)(46 * Ui), (int)(26 * Ui)) };
                            sw.CheckedChanged += delegate
                            {
                                if (building) return;
                                var list = new List<int>(island.tabs);
                                if (sw.Checked && !list.Contains(page)) list.Add(page);
                                else if (!sw.Checked && list.Count > 1) list.Remove(page);
                                list.Sort();
                                island.SetLayout("panel.tabs", island.TabIds(list));
                                Preview();
                            };
                            add(Row(Lang.T(PageNames[pg]), null, sw));
                        }
                        break;
                    case "activity":
                        add(Row(Lang.T("Lirik di island"), null, Switch("lyrics", true, null)));
                        add(Row(Lang.T("Cari lirik online"), Lang.T("Judul, artis, dan durasi ke LRCLIB, cadangannya NetEase"), Switch("lyrics.online", true, null)));
                        add(Row(Lang.T("Visualizer"), null, Switch("visualizer", true, null)));
                        cap(Lang.T("Pesan"));
                        add(Row(Lang.T("Notifikasi pesan"), null, Switch("notif", true, on => { island.SetFlag("notif", on); Relayout(); })));
                        var body = Switch("notif.content", true, null);
                        body.Enabled = settings.GetBool("notif", true);
                        add(Row(Lang.T("Tampilkan isi pesan"), body.Enabled ? null : Lang.T("Ikut notifikasi pesan"), body));
                        cap(Lang.T("Lainnya"));
                        add(Row(Lang.T("Kelas yang berlangsung"), null, Switch("class.island", true, null)));
                        add(Row(Lang.T("Progres unduhan"), null, Switch("downloads", true, null)));
                        add(Row(Lang.T("Riwayat clipboard"), Lang.T("Hanya di memori"), Switch("clipboard", false, null)));
                        add(Row(Lang.T("Rak berkas"), Lang.T("Jalur berkas saja, hanya di memori"), Switch("shelf", true, null)));
                        add(Row(Lang.T("Istirahat fokus"), null, Slider(1, 30, 1, settings.GetInt("focus.break", 5), v => v + Lang.T(" menit"), v => { settings.Set("focus.break", v); settings.Save(); })));
                        break;
                    case "agents":
                        island.HooksInstalled();
                        add(Row("Claude Code", island.hookProfiles == 0 ? Lang.T("tidak ditemukan") : island.hooksOn ? Lang.F("terhubung · {0} profil", island.hookProfiles) : Lang.T("belum terhubung"),
                            AgentButton(island.hookProfiles > 0, island.hooksOn, delegate { island.ToggleHooks(); Relayout(); })));
                        bool quotaOn = false;
                        try { quotaOn = island.hooksOn && StatusSetup.Installed(); } catch (Exception) { }
                        var quota = new KitSwitch { Checked = quotaOn, Enabled = island.hooksOn, Size = new Size((int)(46 * Ui), (int)(26 * Ui)) };
                        quota.CheckedChanged += delegate { if (building) return; island.SetQuota(quota.Checked); Relayout(); };
                        add(Row(Lang.T("Kuota Claude"), island.QuotaNote(quotaOn), quota));
                        foreach (var agentId in AgentSetup.Ids)
                        {
                            string aid = agentId;
                            bool present = AgentSetup.Present(aid), on = false;
                            try { on = present && AgentSetup.Installed(aid); } catch (Exception) { }
                            add(Row(AgentSetup.Label(aid), !present ? Lang.T("tidak ditemukan") : on ? Lang.T("terhubung") : Lang.T("belum terhubung"),
                                AgentButton(present, on, delegate { island.ToggleAgent(aid); Relayout(); })));
                        }
                        cap(Lang.T("Server hook"));
                        int port = island.hub != null ? island.hub.Port : settings.GetInt("agent.port", 47821);
                        add(Row(Lang.F("Port {0}", port), port == 47821 ? Lang.T("Port bawaan lama; port acak lebih sulit ditebak") : Lang.T("Hanya bisa dijangkau dari laptop ini"),
                            Button(Lang.T("Ganti port acak"), false, delegate { island.MoveToRandomPort(); Relayout(); })));
                        add(Row(Lang.T("Menahan izin"), Lang.T("Lewat batas ini, izin dijawab di terminal"), Slider(30, 300, 10, settings.GetInt("agent.wait", 110), v => v + Lang.T(" dtk"), v => { settings.Set("agent.wait", v); settings.Save(); })));
                        break;
                    case "kulia":
                    case "integrasi":
                        Integrations(add, cap, show);
                        break;
                    case "remind":
                        var cities = new List<string>();
                        int cityAt = 0;
                        string city = settings.Get("pray.city", "");
                        foreach (var c in Cities.List) { if ((string)c[0] == city) cityAt = cities.Count; cities.Add((string)c[0]); }
                        add(Row(Lang.T("Pengingat sholat"), Lang.T("Dihitung di laptop ini, metode Kemenag"), Switch("pray", false, on =>
                        {
                            if (on && city.Length == 0) settings.Set("pray.city", cities[cityAt]);
                            settings.Set("pray", on);
                            settings.Save();
                            island.prayToday = null;
                            island.Kick();
                        })));
                        add(Row(Lang.T("Kota"), null, Popup(cities.ToArray(), cityAt, i => { settings.Set("pray.city", cities[i]); settings.Save(); island.prayToday = null; island.Kick(); })));
                        add(Row(Lang.T("Ingatkan sebelum"), null, Slider(0, 30, 5, settings.GetInt("pray.before", 10), v => v == 0 ? Lang.T("tidak") : v + Lang.T(" menit"), v => { settings.Set("pray.before", v); settings.Save(); })));
                        cap(Lang.T("Kesehatan"));
                        add(Row(Lang.T("Minum air"), null, Switch("drink", false, null)));
                        add(Row(Lang.T("Selang minum"), null, Slider(30, 120, 15, settings.GetInt("drink.every", 60), v => v + Lang.T(" menit"), v => { settings.Set("drink.every", v); settings.Save(); })));
                        add(Row(Lang.T("Target harian"), null, Slider(4, 12, 1, island.DrinkGoal, v => v + Lang.T(" gelas"), v => { settings.Set("drink.goal", v); settings.Save(); })));
                        add(Row(Lang.T("Istirahat mata 20-20-20"), Lang.T("Tiap 20 menit di layar, pandang jauh 20 detik"), Switch("eyes", false, null)));
                        add(Row(Lang.T("Peregangan"), null, Switch("stretch", false, null)));
                        add(Row(Lang.T("Selang peregangan"), null, Slider(30, 120, 15, settings.GetInt("stretch.every", 60), v => v + Lang.T(" menit"), v => { settings.Set("stretch.every", v); settings.Save(); })));
                        break;
                    case "access":
                        add(Row(Lang.T("Tampil di rekaman layar"), Lang.T("Ikut screenshot dan screen share, tanpa efek kaca"), Switch("capture.show", false, null)));
                        add(Row(Lang.T("Umumkan ke pembaca layar"), null, Switch("a11y.announce", true, null)));
                        string motion = settings.Get("motion.reduce", "auto");
                        add(Row(Lang.T("Kurangi gerak"), Lang.T("Tanpa pantulan dan efek terbang"),
                            Seg(new[] { Lang.T("Ikuti Windows"), Lang.T("Nyala"), Lang.T("Mati") }, motion == "1" ? 1 : motion == "0" ? 2 : 0, i => island.SetChoice("motion.reduce", i == 1 ? "1" : i == 2 ? "0" : "auto"))));
                        add(Row(Lang.T("Senyap"), Lang.T("Hanya izin, tenggat, sholat, dan timer yang muncul"), Switch("dnd", false, null)));
                        break;
                    case "about":
                        var about = Row("Tepi " + AppVersion, Lang.T("Dynamic island untuk Windows · MIT"), Button("GitHub", false, delegate { OpenUrl("https://github.com/odlaver/tepi"); }));
                        // 2.0 stays big; build digits tiny
                        string[] ver = AppVersion.Split('.');
                        if (ver.Length > 2) { about.Title = "Tepi " + ver[0] + "." + ver[1]; about.Suffix = "." + string.Join(".", ver, 2, ver.Length - 2); }
                        add(about);
                        add(UpdateRow());
                        add(Row(Lang.T("Layanan luar"), Lang.T("LRCLIB, NetEase, Kulia, GitHub, dan integrasi yang tersambung; semuanya bisa dimatikan"), null));
                        // QRIS dialog lives on the site
                        add(Row(Lang.T("Traktir Tepi kopi"), Lang.T("QRIS, dari bank atau e-wallet mana pun"), Button(Lang.T("Traktir"), false, delegate { OpenUrl("https://odlaver.github.io/tepi/#kopi"); })));
                        break;
                }
                flush();
            }

            // Check now; found build installs here
            KitRow UpdateRow()
            {
                KitRow row = null;
                KitButton b = null;
                Action show = delegate
                {
                    if (row.IsDisposed) return;
                    bool found = island.newVersion != null;
                    b.Text = found ? (island.newAsset != null ? Lang.T("Perbarui") : "GitHub") : Lang.T("Cek pembaruan");
                    b.Primary = found;
                    b.Enabled = !island.checking && !island.updating;
                    b.Width = TextRenderer.MeasureText(b.Text, b.Font).Width + (int)(32 * Ui);
                    row.Detail = island.UpdateText();
                    b.AccessibleDescription = row.Detail;
                    if (row.Parent != null) PlaceField(row);
                    b.Invalidate();
                    row.Invalidate();
                };
                b = Button(Lang.T("Cek pembaruan"), false, delegate
                {
                    if (island.newVersion != null) island.InstallUpdate();
                    else island.CheckUpdate(true);
                });
                row = Row(Lang.T("Pembaruan"), island.UpdateText(), b);
                show();
                island.UpdateChanged += show;
                row.Disposed += delegate { island.UpdateChanged -= show; };
                return row;
            }

            Control StartupSwitch()
            {
                var s = new KitSwitch { Size = new Size((int)(46 * Ui), (int)(26 * Ui)) };
                try { s.Checked = AutoStart.Enabled; } catch (Exception) { }
                s.CheckedChanged += delegate { if (building) return; try { AutoStart.Enabled = s.Checked; } catch (Exception ex) { Log.Write("autostart: " + ex.Message); } };
                return s;
            }

            Control AgentButton(bool present, bool on, Action act)
            {
                var b = Button(on ? Lang.T("Lepas") : Lang.T("Hubungkan"), !on && present, act);
                b.Enabled = present;
                return b;
            }

            protected override void Dispose(bool disposing) { if (disposing) preview.Dispose(); base.Dispose(disposing); }
        }

        // Settings sidebar: icon, name; arrows move
        class SideList : KitControl
        {
            public List<string[]> Items = new List<string[]>();
            public string Selected = "";
            public event Action<string> Picked;
            int hover = -1;

            float RowH { get { return 34 * Ui; } }

            RectangleF Cell(int i) { float s = Ui; return new RectangleF(10 * s, 6 * s + i * RowH, Width - 20 * s, RowH - 2 * s); }

            int IndexOf(string id) { for (int i = 0; i < Items.Count; i++) if (Items[i][0] == id) return i; return -1; }

            void Pick(int i)
            {
                if (i < 0 || i >= Items.Count) return;
                Selected = Items[i][0];
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, i);
                var h = Picked;
                if (h != null) h(Selected);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = -1;
                for (int i = 0; i < Items.Count; i++) if (Cell(i).Contains(e.Location)) h = i;
                if (h != hover) { hover = h; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                for (int i = 0; i < Items.Count; i++) if (Cell(i).Contains(e.Location)) { Pick(i); return; }
            }

            protected override bool IsInputKey(Keys key) { return key == Keys.Up || key == Keys.Down || base.IsInputKey(key); }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                int at = IndexOf(Selected);
                if (e.KeyCode == Keys.Up) Pick(Math.Max(0, at - 1));
                else if (e.KeyCode == Keys.Down) Pick(Math.Min(Items.Count - 1, at + 1));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                KitInk.Smooth(g);
                g.Clear(KitInk.Side);
                float s = Ui;
                using (var f = new Font(Ink.MenuFamily, 13.5f * s, GraphicsUnit.Pixel))
                using (var icons = new Font(Ink.IconFamily, 14f * s, GraphicsUnit.Pixel))
                    for (int i = 0; i < Items.Count; i++)
                    {
                        var c = Cell(i);
                        bool sel = Items[i][0] == Selected;
                        if (sel || i == hover) Fill(g, c, 9 * s, sel ? KitInk.Hover : Ink.Mix(KitInk.Side, KitInk.Hover, 0.5f));
                        KitInk.Draw(g, Items[i][2], icons, sel ? KitInk.Text : KitInk.Sub, new RectangleF(c.X + 8 * s, c.Y, 22 * s, c.Height), ContentAlignment.MiddleCenter);
                        KitInk.Draw(g, Items[i][1], f, sel ? KitInk.Text : KitInk.Sub, new RectangleF(c.X + 38 * s, c.Y, c.Width - 44 * s, c.Height), ContentAlignment.MiddleLeft);
                    }
                int at = IndexOf(Selected);
                if (at >= 0) Ring(g, Cell(at), 9 * s);
            }

            protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

            class Access : ControlAccessibleObject
            {
                readonly SideList owner;
                public Access(SideList owner) : base(owner) { this.owner = owner; }
                public override AccessibleRole Role { get { return AccessibleRole.PageTabList; } }
                public override int GetChildCount() { return owner.Items.Count; }
                public override AccessibleObject GetChild(int i) { return new Item(owner, i); }
            }

            class Item : AccessibleObject
            {
                readonly SideList owner;
                readonly int index;
                public Item(SideList owner, int index) { this.owner = owner; this.index = index; }
                public override string Name { get { return owner.Items[index][1]; } }
                public override AccessibleRole Role { get { return AccessibleRole.PageTab; } }
                public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
                public override AccessibleStates State { get { return AccessibleStates.Selectable | (owner.Items[index][0] == owner.Selected ? AccessibleStates.Selected : 0); } }
                public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Round(owner.Cell(index))); } }
                public override void DoDefaultAction() { owner.Pick(index); }
            }
        }
    }
}
