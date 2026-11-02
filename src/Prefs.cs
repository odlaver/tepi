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

        void OpenPrefs(string section)
        {
            OpenPrefs();
            prefs.Go(section);
        }

        void SetClock24(bool h24)
        {
            settings.Set("clock.24", h24);
            settings.Save();
            Fmt.H24 = h24;
            clockRoller.Set(Fmt.Clock(DateTime.Now));
            // Cached prayer strings follow at once
            if (prayToday != null)
            {
                for (int i = 0; i < 6; i++) prayHm[i] = Fmt.Hm(prayToday[i]);
                prayTomorrowHm = Fmt.Hm(prayTomorrow);
            }
            Kick();
        }

        // theme: gelap, terang, otomatis
        void SetThemeMode(string mode)
        {
            settings.Set("theme", mode);
            settings.Save();
            ApplyThemeMode();
        }

        bool ThemeLight()
        {
            string mode = settings.Get("theme", "gelap");
            return mode == "terang" || (mode == "otomatis" && !winDark);
        }

        void ApplyThemeMode()
        {
            bool light = ThemeLight();
            if (light == Ink.Light) return;
            Ink.Apply(light);
            ResetFrost();
            if (prefs != null && !prefs.IsDisposed) prefs.Relayout();
            Kick();
        }

        void SetAccentHex(string hex)
        {
            settings.Set("accent", hex);
            settings.Save();
            Ink.SetAccent(hex);
            albumAccentFor = null;
            albumTinted = false;
            AlbumAccent();
            Kick();
        }

        void SetFlag(string key, bool on)
        {
            settings.Set(key, on);
            settings.Save();
            if (key == "dock") ResetFrost();
            else if (key == "notif") notifWatcher.Enabled = NotifOn;
            else if (key == "lyrics.online") { lyrics = null; SyncLyrics(); }
            else if (key == "shots") ApplyClips();
            else if (key == "clipboard")
            {
                ApplyClips();
                if (on && Array.IndexOf(tabs, PageShelf) < 0) SetLayout("panel.tabs", TabIds(tabs) + ",shelf");
            }
            else if (key == "downloads") ApplyDownloads();
            else if (key == "capture.show") ApplyGlass();
            else if (key == "shelf" || key == "convert") InitShelfDrop();
            Kick();
        }

        void SetChoice(string key, string value)
        {
            settings.Set(key, value);
            settings.Save();
            if (key == "material") { ApplyGlass(); ResetFrost(); }
            else if (key == "motion.reduce" || key == "motion.feel") Anim.Read(settings);
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
            new[] { "Dari sampul album", AccentAlbum },
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
                new[] { "activity", "Aktivitas", "" }, new[] { "agents", "Agen", "" }, new[] { "phone", "HP", "" }, new[] { "integrasi", "Integrasi", "" },
                new[] { "remind", "Pengingat", "" }, new[] { "access", "Aksesibilitas", "" }, new[] { "keys", "Pintasan", "" }, new[] { "about", "Tentang", "" },
            };

            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

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
                search.GotFocus += delegate { Invalidate(new Rectangle(0, 0, (int)(220 * Ui), (int)(60 * Ui))); };
                search.LostFocus += delegate { Invalidate(new Rectangle(0, 0, (int)(220 * Ui), (int)(60 * Ui))); };
                side.Picked += id => { section = id; building = true; search.Text = ""; building = false; Fill(); };
                Controls.Add(search);
                Controls.Add(side);
                Controls.Add(content);
            }

            public void Go(string id)
            {
                section = id;
                building = true;
                search.Text = "";
                building = false;
                if (IsHandleCreated) { side.Selected = id; side.Invalidate(); Fill(); }
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
                // Scrollbar follows the theme
                try { SetWindowTheme(content.Handle, Ink.Light ? "Explorer" : "DarkMode_Explorer", null); } catch (Exception) { }
                Text = Lang.T("Pengaturan Tepi");
                side.Items = new List<string[]>();
                foreach (var s in Sections) side.Items.Add(new[] { s[0], Lang.T(s[1]), s[2] });
                side.Selected = section;
                search.Font = UiFont(13f);
                search.BackColor = KitInk.Line;
                search.ForeColor = KitInk.Text;
                // Hint stays while focused
                SendMessage(search.Handle, 0x1501, (IntPtr)1, Lang.T("Cari pengaturan"));
                Place();
                Fill();
                Invalidate(true);
            }

            void Place()
            {
                float s = Ui;
                int sideW = (int)(220 * s);
                // Text centred on the well, by its real height
                int h = search.PreferredHeight;
                search.SetBounds((int)(40 * s), (int)Math.Round(SearchWell().Y + SearchWell().Height / 2 - h / 2f + 1 * s), sideW - (int)(62 * s), h);
                side.SetBounds(0, (int)(58 * s), sideW, ClientSize.Height - (int)(58 * s));
                content.SetBounds(sideW, 0, Math.Max(10, ClientSize.Width - sideW), ClientSize.Height);
                content.BackColor = KitInk.Window;
                side.Invalidate();
            }

            RectangleF SearchWell() { return new RectangleF(12 * Ui, 16 * Ui, 220 * Ui - 24 * Ui, 32 * Ui); }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(KitInk.Window);
                int sideW = (int)(220 * Ui);
                using (var b = new SolidBrush(KitInk.Side)) e.Graphics.FillRectangle(b, 0, 0, sideW, ClientSize.Height);
                // Search field: a rounded well
                var r = SearchWell();
                KitInk.Smooth(e.Graphics);
                using (var path = Ink.Round(r, 10 * Ui))
                {
                    using (var b = new SolidBrush(KitInk.Line)) e.Graphics.FillPath(b, path);
                    if (search.Focused) using (var pen = new Pen(KitInk.Faint, 1.2f)) e.Graphics.DrawPath(pen, path);
                }
                // Magnifier leads the field
                using (var icons = new Font(Ink.IconFamily, 13f * Ui, GraphicsUnit.Pixel))
                    KitInk.Draw(e.Graphics, "\uE721", icons, KitInk.Sub, new RectangleF(r.X + 5 * Ui, r.Y, 24 * Ui, r.Height), ContentAlignment.MiddleCenter);
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

            // Blank a Label draws before its first letter
            static int LabelPad(Label l)
            {
                var size = l.PreferredSize;
                if (size.Width <= 0 || size.Height <= 0) return 0;
                using (var bmp = new Bitmap(size.Width, size.Height))
                {
                    l.Size = size;
                    l.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
                    var back = l.BackColor;
                    for (int x = 0; x < size.Width; x++)
                        for (int y = 0; y < size.Height; y++)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (Math.Abs(c.R - back.R) + Math.Abs(c.G - back.G) + Math.Abs(c.B - back.B) > 90) return x;
                        }
                }
                return 0;
            }

            void Heading(string text)
            {
                var l = new Label { Text = text, Font = UiFont(24f), ForeColor = KitInk.Text, BackColor = KitInk.Window, AutoSize = true };
                // Ink on the group's edge
                l.Location = new Point((int)(28 * Ui) - LabelPad(l), (int)y);
                content.Controls.Add(l);
                y += 52 * Ui;
            }

            // Tepi 2.0 big, build digits tiny
            KitRow VersionRow(Control field)
            {
                var r = Row("Tepi " + AppVersion, Lang.T("Dynamic island untuk Windows · MIT"), field);
                string[] ver = AppVersion.Split('.');
                if (ver.Length > 2) { r.Title = "Tepi " + ver[0] + "." + ver[1]; r.Suffix = "." + string.Join(".", ver, 2, ver.Length - 2); }
                return r;
            }

            void Caption(string text)
            {
                var l = new Label { Text = text, Font = UiFont(12f), ForeColor = KitInk.Sub, BackColor = KitInk.Window, AutoSize = true };
                // Ink lines up with the row icons
                l.Location = new Point((int)(42 * Ui) - LabelPad(l), (int)y);
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

            // Row icons by source title
            static readonly string[][] RowGlyphs =
            {
                new[] { "Bahasa", "\uE774" }, new[] { "Mulai bersama Windows", "\uE7E8" }, new[] { "Cek versi baru", "\uE72C" },
                new[] { "Folder data", "\uE8B7" }, new[] { "Folder paket", "\uE8B7" }, new[] { "Tema", "\uE706" }, new[] { "Material", "\uE81E" },
                new[] { "Aksen", "\uE790" }, new[] { "Paket", "\uE7B8" }, new[] { "Suara sapaan paket tema", "\uE767" },
                new[] { "Mode", "\uE8A0" }, new[] { "Notifikasi saat mini", "\uEA8F" }, new[] { "Mata", "\uE890" }, new[] { "Ikuti kursor", "\uE7C9" }, new[] { "Buka panel", "\uE8A7" }, new[] { "Jeda buka", "\uE916" },
                new[] { "Menempel di tepi atas", "\uE840" }, new[] { "Posisi", "\uE759" }, new[] { "Lebar panel", "\uE740" }, new[] { "Layar", "\uE7F4" },
                new[] { "Gaya tab", "\uECA5" }, new[] { "Dibuka di", "\uE8A7" },
                new[] { "Lirik di island", "\uE8D6" }, new[] { "Cari lirik online", "\uE721" }, new[] { "Visualizer", "\uE9E9" },
                new[] { "Notifikasi pesan", "\uE8BD" }, new[] { "Tampilkan isi pesan", "\uE8BD" }, new[] { "Caps Lock", "\uE765" }, new[] { "Ingatkan cabut charger", "\uE83E" }, new[] { "Cuaca dan hujan", "\uE753" }, new[] { "Mode rapat", "\uE720" },
                new[] { "Num Lock, Scroll Lock", "\uE765" }, new[] { "Bahasa input", "\uE765" }, new[] { "Kelas yang berlangsung", "\uE7BE" },
                new[] { "Progres unduhan", "\uE896" }, new[] { "Riwayat clipboard", "\uE77F" }, new[] { "Flashdisk", "\uE88E" },
                new[] { "Preview screenshot", "\uE91B" }, new[] { "Rak berkas", "\uE7B8" }, new[] { "Istirahat fokus", "\uE916" },
                new[] { "Claude Code", "\uE756" }, new[] { "Kuota Claude", "\uEC4A" }, new[] { "Menahan izin", "\uE72E" },
                new[] { "Kulia", "\uE7BE" }, new[] { "Tugas dan jadwal", "\uE787" }, new[] { "Kolom tanggal", "\uE787" }, new[] { "Kolom selesai", "\uE73E" },
                new[] { "Belum ada database", "\uE8F1" }, new[] { "Pengingat sholat", "\uE708" }, new[] { "Kota", "\uE707" }, new[] { "Ingatkan sebelum", "\uEA8F" },
                new[] { "Minum air", "\uEB42" }, new[] { "Selang minum", "\uE916" }, new[] { "Target harian", "\uE73E" },
                new[] { "Istirahat mata 20-20-20", "\uE890" }, new[] { "Peregangan", "\uE805" }, new[] { "Selang peregangan", "\uE916" },
                new[] { "Tampil di rekaman layar", "\uE7F4" }, new[] { "Umumkan ke pembaca layar", "\uE7F5" }, new[] { "Kurangi gerak", "\uE7AD" }, new[] { "Gerak", "\uE7AD" },
                new[] { "Senyap", "\uE708" }, new[] { "Pembaruan", "\uE72C" }, new[] { "Layanan luar", "\uE774" }, new[] { "Traktir Tepi kopi", "\uEC32" },
                new[] { "Ringkasan", "\uE80F" }, new[] { "Musik", "\uE8D6" }, new[] { "Agen", "\uE756" }, new[] { "Kuliah", "\uE7BE" },
                new[] { "Pesan", "\uE8BD" }, new[] { "Rak", "\uE7B8" }, new[] { "Kontrol", "\uE9E9" }, new[] { "Pengingat", "\uEA8F" },
                new[] { "HP", "\uE8EA" }, new[] { "Ubah", "\uE8AB" }, new[] { "Ubah format", "\uE8AB" },
                new[] { "Hubungkan HP", "\uE8EA" }, new[] { "Notifikasi HP", "\uE8BD" }, new[] { "Kode OTP", "\uE8D7" },
                new[] { "Notion", "\uE8F1" }, new[] { "Todoist", "\uE73E" }, new[] { "Kalender iCal", "\uE787" },
                new[] { "Sembunyikan island", "\uED1A" }, new[] { "Navigasi keyboard", "\uE765" }, new[] { "Mini", "\uE898" }, new[] { "Mikrofon", "\uE720" },
                new[] { "Catatan kilat", "\uE70B" }, new[] { "Cermin kamera", "\uE714" }, new[] { "Jeda dan lanjut", "\uE769" }, new[] { "Ulang dari awal", "\uE72C" },
                new[] { "Mundur 5 detik", "\uE76B" }, new[] { "Maju 5 detik", "\uE76C" }, new[] { "Lebih cepat", "\uE710" }, new[] { "Lebih lambat", "\uE738" },
                new[] { "Buka jendela teleprompter", "\uE8A7" }, new[] { "Hentikan", "\uE71A" }, new[] { "Pintasan bawaan", "\uE765" }, new[] { "Tambah aplikasi", "\uE710" }, new[] { "Format jam", "\uE823" }, new[] { "Pakai warna sendiri", "\uE790" }, new[] { "Simpan jadi paket", "\uE74E" }, new[] { "Pembuat paket", "\uE774" }, new[] { "Pindai dari HP", "\uED14" }, new[] { "Aplikasi chat tambahan", "\uE8BD" }, new[] { "Pengirim prioritas", "\uE77B" }, new[] { "Senyap ikut Windows", "\uE708" }, new[] { "Suara", "\uE767" }, new[] { "Simpan isi rak", "\uE74E" }, new[] { "Kosongkan rak otomatis", "\uE916" }, new[] { "Ambil warna", "\uE790" }, new[] { "Ekspor pengaturan", "\uEDE1" }, new[] { "Impor pengaturan", "\uE8B5" }, new[] { "Setel ulang pengaturan", "\uE72C" },
            };

            static char GlyphFor(string title)
            {
                foreach (var g in RowGlyphs)
                    if (Lang.T(g[0]) == title || g[0] == title) return g[1][0];
                if (title.StartsWith("Tepi")) return KitRow.MarkGlyph;
                return '\uE713';
            }

            KitRow Row(string title, string detail, Control field)
            {
                var r = new KitRow { Title = title, Detail = detail ?? "", Field = field, Height = (int)((string.IsNullOrEmpty(detail) ? 52 : 62) * Ui), Glyph = GlyphFor(title) };
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
                var c = new KitSlider { Minimum = min, Maximum = max, Step = step, Value = Math.Max(min, Math.Min(max, value)), Format = fmt, ShowValue = true, Size = new Size((int)(270 * Ui), (int)(30 * Ui)) };
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

            // Comma list saved on Enter or leaving the box
            // Row says Tersimpan for a moment after a save
            KitRow ListRow(string title, string detail, string key, string cue)
            {
                var f = new KitField(Ui, false, cue, 300);
                f.Box.Text = settings.Get(key, "");
                var row = Row(title, detail, f);
                var back = new System.Windows.Forms.Timer { Interval = 2500 };
                back.Tick += delegate { back.Stop(); row.Detail = detail; row.Invalidate(); };
                f.Disposed += delegate { back.Dispose(); };
                Action save = delegate
                {
                    string clean = string.Join(", ", CommaList(f.Box.Text));
                    if (clean == settings.Get(key, "")) return;
                    settings.Set(key, clean);
                    settings.Save();
                    island.ApplyNotifLists();
                    row.Detail = Lang.T("Tersimpan");
                    row.Invalidate();
                    back.Stop();
                    back.Start();
                };
                f.Submit = save;
                f.Box.LostFocus += delegate { save(); };
                return row;
            }

            // Swatch row; a pick repaints island and window
            // Starting points that read well together
            static string[][] SwatchPresets(string key)
            {
                if (key == "bg") return new[] { new[] { "Hitam", "#000000" }, new[] { "Arang", "#1C1C1E" }, new[] { "Navy", "#0E1734" }, new[] { "Hijau tua", "#0F2A22" }, new[] { "Putih", "#FFFFFF" }, new[] { "Krem", "#F5EFE6" } };
                if (key == "text") return new[] { new[] { "Putih", "#FFFFFF" }, new[] { "Putih lembut", "#EEF4FF" }, new[] { "Hitam", "#111111" }, new[] { "Cokelat tua", "#3B2F2F" } };
                var list = new System.Collections.Generic.List<string[]>();
                foreach (var a in Accents) if (a[1].StartsWith("#")) list.Add(a);
                return list.ToArray();
            }

            KitRow ColorRow(string key, string title, string detail)
            {
                var sw = new KitSwatch { Font = UiFont(12.5f), Value = island.CustomColor(key), Presets = SwatchPresets(key) };
                sw.Size = new Size(TextRenderer.MeasureText("      #DDDDDD", sw.Font).Width + (int)(32 * Ui), (int)(30 * Ui));
                sw.Picked += c => { island.SetCustomColor(key, c); BeginInvoke(new Action(Relayout)); };
                var row = Row(title, detail, sw);
                row.Glyph = '\uE790';
                return row;
            }

            string ContrastNote()
            {
                double c = Contrast(island.CustomColor("text"), island.CustomColor("bg"));
                string v = c.ToString("0.0", Lang.English ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo("id-ID"));
                return c >= 4.5 ? Lang.F("Kontras dengan latar {0}:1", v) : Lang.F("Kontras {0}:1, teks sulit dibaca", v);
            }

            // Shortcut row: detail shows default or the clash
            KitRow HotkeyRow(HotkeyDef d)
            {
                var field = new KitHotkey { Font = UiFont(12.5f) };
                var row = Row(Lang.T(d.Name), HotkeyDetail(d), field);
                field.Size = new Size(TextRenderer.MeasureText("Ctrl+Alt+Shift+PageDown", field.Font).Width + (int)(32 * Ui), (int)(30 * Ui));
                field.Combo = island.HotkeyText(d.Id);
                field.Begin = delegate { island.HoldHotkeys(true); };
                field.End = delegate { island.HoldHotkeys(false); };
                field.Commit = combo =>
                {
                    string error = island.SetHotkey(d.Id, combo);
                    field.Combo = island.HotkeyText(d.Id);
                    row.Detail = error ?? HotkeyDetail(d);
                    field.AccessibleDescription = row.Detail;
                    row.Invalidate();
                    return error == null;
                };
                return row;
            }

            string HotkeyDetail(HotkeyDef d)
            {
                if (island.HotkeyTaken(d.Id)) return Lang.T("Dipakai aplikasi lain");
                return d.Fallback.Length == 0 ? Lang.T("Tanpa pintasan bawaan") : Lang.F("Bawaan: {0}", d.Fallback);
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
                        add(VersionRow(null));
                        flush();
                        add(Row(Lang.T("Bahasa"), null, Seg(new[] { "Indonesia", "English" }, Lang.English ? 1 : 0, i => { island.SetLanguage(i == 1); Relayout(); })));
                        add(Row(Lang.T("Format jam"), null, Seg(new[] { "14.30", "2.30 pm" }, Fmt.H24 ? 0 : 1, i => island.SetClock24(i == 0))));
                        add(Row(Lang.T("Mulai bersama Windows"), null, StartupSwitch()));
                        add(Row(Lang.T("Cek versi baru"), Lang.T("Sekali sehari ke GitHub; unduhan hanya saat diminta"), Switch("update.check", true, on => { settings.Set("update.check", on); settings.Save(); })));
                        cap(Lang.T("Data"));
                        add(Row(Lang.T("Folder data"), Settings.Dir, Button(Lang.T("Buka"), false, delegate { try { Process.Start("explorer.exe", "\"" + Settings.Dir + "\""); } catch (Exception) { } })));
                        KitRow io = null;
                        io = Row(Lang.T("Ekspor pengaturan"), Lang.T("Tanpa token, port, dan pasangan HP"), Button(Lang.T("Simpan…"), false, delegate
                        {
                            using (var d = new SaveFileDialog { FileName = "tepi-pengaturan.ini", Filter = "INI|*.ini", RestoreDirectory = true })
                            {
                                if (d.ShowDialog(this) != DialogResult.OK) return;
                                try { io.Detail = Lang.F("{0} pengaturan disimpan", island.ExportSettings(d.FileName)); }
                                catch (Exception ex) { io.Detail = Lang.T("Gagal menyimpan"); Log.Write("export: " + ex.Message); }
                                io.Invalidate();
                            }
                        }));
                        add(io);
                        KitRow inRow = null;
                        inRow = Row(Lang.T("Impor pengaturan"), Lang.T("Tepi dibuka ulang sesudahnya"), Button(Lang.T("Buka…"), false, delegate
                        {
                            using (var d = new OpenFileDialog { Filter = "INI|*.ini|*.*|*.*", RestoreDirectory = true })
                            {
                                if (d.ShowDialog(this) != DialogResult.OK) return;
                                int n = 0;
                                try { n = island.ImportSettings(d.FileName); }
                                catch (Exception ex) { Log.Write("import: " + ex.Message); }
                                if (n == 0) { inRow.Detail = Lang.T("Tidak ada pengaturan Tepi di berkas itu"); inRow.Invalidate(); return; }
                                island.RestartTepi();
                            }
                        }));
                        add(inRow);
                        // Second click within 4 s confirms
                        KitButton resetBtn = null;
                        KitRow resetRow = null;
                        string resetNote = Lang.T("Integrasi, agen, dan HP tetap tersambung");
                        var armed = new System.Windows.Forms.Timer { Interval = 4000 };
                        armed.Tick += delegate { armed.Stop(); resetBtn.Text = Lang.T("Setel ulang"); resetRow.Detail = resetNote; resetBtn.Invalidate(); resetRow.Invalidate(); };
                        resetBtn = Button(Lang.T("Setel ulang"), false, delegate
                        {
                            if (!armed.Enabled)
                            {
                                armed.Start();
                                resetBtn.Text = Lang.T("Klik lagi");
                                resetRow.Detail = Lang.T("Klik sekali lagi dalam 4 detik untuk menyetel ulang");
                                resetBtn.Invalidate();
                                resetRow.Invalidate();
                                return;
                            }
                            armed.Stop();
                            island.ResetSettings();
                            island.RestartTepi();
                        });
                        resetBtn.Disposed += delegate { armed.Dispose(); };
                        resetRow = Row(Lang.T("Setel ulang pengaturan"), resetNote, resetBtn);
                        resetRow.Glyph = '\uE7A7';
                        add(resetRow);
                        break;
                    case "look":
                        string themeMode = settings.Get("theme", "gelap");
                        add(Row(Lang.T("Tema"), themeMode == "otomatis" ? Lang.T("Mengikuti mode aplikasi Windows") : null,
                            Seg(new[] { Lang.T("Gelap"), Lang.T("Terang"), Lang.T("Otomatis") }, themeMode == "terang" ? 1 : themeMode == "otomatis" ? 2 : 0, i =>
                            {
                                island.SetThemeMode(i == 1 ? "terang" : i == 2 ? "otomatis" : "gelap");
                                BeginInvoke(new Action(Relayout));
                            })));
                        string mat = island.MaterialName;
                        add(Row(Lang.T("Material"), Lang.T("Hitam paling hemat daya"),
                            Seg(new[] { "Liquid", "Frost", Lang.T("Hitam") }, mat == "frost" ? 1 : mat == "solid" ? 2 : 0, i => island.SetChoice("material", i == 1 ? "frost" : i == 2 ? "solid" : "liquid"))));
                        var names = new List<string>();
                        int at = 0;
                        string accent = settings.Get("accent", "");
                        for (int i = 0; i < Accents.Length; i++) { names.Add(Lang.T(Accents[i][0])); if (Accents[i][1] == accent) at = i; }
                        bool custom = island.CustomOn;
                        var accentPick = Popup(names.ToArray(), at, i => island.SetAccentHex(Accents[i][1]));
                        accentPick.Enabled = !custom;
                        add(Row(Lang.T("Aksen"), custom ? Lang.T("Warna sendiri sedang dipakai") : settings.Get("theme.pack", "").Length > 0 ? Lang.T("Paket tema bisa menimpanya") : null, accentPick));
                        cap(Lang.T("Warna sendiri"));
                        add(Row(Lang.T("Pakai warna sendiri"), Lang.T("Menimpa warna paket tema, gambarnya tetap"), Switch("look.custom", false, on =>
                        {
                            island.SetCustom(on);
                            BeginInvoke(new Action(Relayout));
                        })));
                        if (custom)
                        {
                            add(ColorRow("bg", Lang.T("Latar"), Lang.T("Klik untuk memilih atau ambil dari layar")));
                            add(ColorRow("text", Lang.T("Teks"), ContrastNote()));
                            add(ColorRow("accent", Lang.T("Aksen"), Lang.T("Klik untuk memilih atau ambil dari layar")));
                            KitRow saved = null;
                            saved = Row(Lang.T("Simpan jadi paket"), Lang.T("Ke folder themes, lalu bisa ditambah gambar"), Button(Lang.T("Simpan"), false, delegate
                            {
                                try { saved.Detail = Lang.F("Tersimpan sebagai {0}", island.SaveCustomPack()); }
                                catch (Exception ex) { saved.Detail = Lang.T("Gagal menyimpan"); Log.Write("save pack: " + ex.Message); }
                                saved.Invalidate();
                            }));
                            add(saved);
                        }
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
                        add(Row(Lang.T("Pembuat paket"), Lang.T("Gambar, sapaan, dan suara, di situs Tepi"), Button(Lang.T("Buka"), false, delegate { OpenUrl("https://odlaver.github.io/tepi/pack/"); })));
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
                        add(Row(Lang.T("Mode"), Lang.T("Otomatis: mini saat tertutup jendela atau diam"),
                            Seg(new[] { Lang.T("Normal"), Lang.T("Mini"), Lang.T("Otomatis") }, mode == "normal" ? 0 : mode == "mini" ? 1 : 2, i => island.SetMiniMode(i == 0 ? "normal" : i == 1 ? "mini" : "auto"))));
                        add(Row(Lang.T("Notifikasi saat mini"), null, Switch("mini.notif", true, null)));
                        add(Row(Lang.T("Mata"), null, Switch("island.eyes", true, on => { island.SetFlag("island.eyes", on); BeginInvoke(new Action(Relayout)); })));
                        // Only means something while eyes show
                        var follow = Switch("island.follow", true, null);
                        follow.Enabled = settings.GetBool("island.eyes", true);
                        add(Row(Lang.T("Ikuti kursor"), follow.Enabled ? Lang.T("Mata melirik ke arah kursor") : Lang.T("Nyalakan Mata dulu"), follow));
                        bool byClick = settings.Get("panel.open", "hover") == "click";
                        var delay = Slider(150, 800, 50, settings.GetInt("panel.delay", 330), v => v + " ms", v => { settings.Set("panel.delay", v); settings.Save(); });
                        delay.Enabled = !byClick;
                        add(Row(Lang.T("Buka panel"), null, Seg(new[] { Lang.T("Arahkan kursor"), Lang.T("Klik") }, byClick ? 1 : 0, i =>
                        {
                            island.SetChoice("panel.open", i == 1 ? "click" : "hover");
                            delay.Enabled = i == 0;
                        })));
                        add(Row(Lang.T("Jeda buka"), Lang.T("Sebelum panel terbuka"), delay));
                        cap(Lang.T("Pengecualian aplikasi"));
                        var rules = island.ExceptRules();
                        foreach (var rule in rules)
                        {
                            var r = rule;
                            var modes = new[] { Lang.T("Sembunyikan island"), Lang.T("Tanpa notifikasi"), Lang.T("Hapus dari daftar") };
                            var appRow = Row(r.Name, r.Exe, Popup(modes, r.Mode == ExceptQuiet ? 1 : 0, i =>
                            {
                                if (i == 2) rules.Remove(r);
                                else r.Mode = i == 1 ? ExceptQuiet : ExceptHide;
                                island.SaveExceptRules(rules);
                                if (i == 2) BeginInvoke(new Action(Relayout));
                            }));
                            appRow.Glyph = '\uE71D';
                            add(appRow);
                        }
                        KitButton pick = null;
                        pick = Button(Lang.T("Pilih aplikasi"), false, delegate
                        {
                            var m = new ContextMenuStrip();
                            foreach (var app in island.OpenApps())
                            {
                                var a = app;
                                bool listed = rules.Exists(x => x.Exe == a.Exe);
                                m.Items.Add(new ToolStripMenuItem(a.Name, null, delegate
                                {
                                    rules.Add(a);
                                    island.SaveExceptRules(rules);
                                    BeginInvoke(new Action(Relayout));
                                }) { Enabled = !listed, ToolTipText = a.Exe });
                            }
                            if (m.Items.Count == 0) m.Items.Add(new ToolStripMenuItem(Lang.T("Tidak ada jendela aplikasi yang terbuka")) { Enabled = false });
                            MenuLook.Apply(m);
                            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
                            m.Show(pick, new Point(0, pick.Height));
                        });
                        add(Row(Lang.T("Tambah aplikasi"), Lang.T("Island menyingkir atau diam saat aplikasinya di depan"), pick));
                        cap(Lang.T("Tab panel"));
                        string style = settings.Get("tabs.style", "auto");
                        add(Row(Lang.T("Gaya tab"), Lang.T("Otomatis: titik bila lebih dari tujuh tab"),
                            Seg(new[] { Lang.T("Otomatis"), Lang.T("Ikon"), Lang.T("Titik") }, style == "icons" ? 1 : style == "dots" ? 2 : 0, i => island.SetChoice("tabs.style", i == 1 ? "icons" : i == 2 ? "dots" : "auto"))));
                        var openNames = new List<string> { Lang.T("Tab pertama") };
                        foreach (var n in PageNames) openNames.Add(Lang.T(n));
                        int openAt = Array.IndexOf(PageIds, settings.Get("panel.first", "")) + 1;
                        add(Row(Lang.T("Dibuka di"), null, Popup(openNames.ToArray(), Math.Max(0, openAt), i => island.SetLayout("panel.first", i > 0 ? PageIds[i - 1] : ""))));
                        cap(Lang.T("Tab yang tampil"));
                        // Shown in panel order, hidden after
                        var order = new List<int>(island.tabs);
                        for (int pg = 0; pg < PageNames.Length; pg++) if (!order.Contains(pg)) order.Add(pg);
                        tabFocus.Clear();
                        foreach (int pg in order) add(TabRow(pg));
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
                        add(ListRow(Lang.T("Aplikasi chat tambahan"), Lang.T("Nama aplikasi dipisah koma, Enter menyimpan"), "notif.apps", "Element, Zalo"));
                        add(ListRow(Lang.T("Pengirim prioritas"), Lang.T("Tetap muncul saat Senyap, Enter menyimpan"), "notif.vip", Lang.T("Ibu, dosen")));
                        cap(Lang.T("Keyboard"));
                        add(Row("Caps Lock", Lang.T("HUD saat ditekan, penanda selama nyala"), Switch("keys.caps", true, null)));
                        add(Row("Num Lock, Scroll Lock", Lang.T("HUD saat ditekan"), Switch("keys.num", false, null)));
                        add(Row(Lang.T("Bahasa input"), Lang.T("HUD saat layout keyboard berganti"), Switch("keys.lang", true, null)));
                        cap(Lang.T("Lainnya"));
                        add(Row(Lang.T("Kelas yang berlangsung"), null, Switch("class.island", true, null)));
                        add(Row(Lang.T("Progres unduhan"), null, Switch("downloads", true, null)));
                        add(Row(Lang.T("Riwayat clipboard"), Lang.T("Hanya di memori"), Switch("clipboard", false, null)));
                        int[] unplugAt = { 0, 80, 85, 90 };
                        add(Row(Lang.T("Ingatkan cabut charger"), Lang.T("Menjaga umur baterai"), Popup(new[] { Lang.T("Mati"), "80%", "85%", "90%" }, Math.Max(0, Array.IndexOf(unplugAt, settings.GetInt("battery.unplug", 0))), i => { settings.Set("battery.unplug", unplugAt[i]); settings.Save(); })));
                        add(Row(Lang.T("Mode rapat"), Lang.T("Mikrofon dipakai lebih dari 20 detik: durasi tampil, notifikasi biasa ditahan"), Switch("meeting", true, null)));
                        add(Row(Lang.T("Flashdisk"), Lang.T("Saat dicolok, dengan tombol Buka dan Keluarkan"), Switch("drives", true, null)));
                        add(Row(Lang.T("Preview screenshot"), Lang.T("Gambar yang disalin tampil di island; salinan sementara dihapus saat Tepi ditutup"), Switch("shots", true, null)));
                        add(Row(Lang.T("Rak berkas"), Lang.T("Jalur berkas saja, hanya di memori"), Switch("shelf", true, null)));
                        add(Row(Lang.T("Simpan isi rak"), Lang.T("Jalur berkas tetap ada setelah Tepi dibuka lagi"), Switch("shelf.keep", false, on => { settings.Set("shelf.keep", on); settings.Save(); island.SaveShelf(); })));
                        int[] expireAt = { 0, 24, 72 };
                        add(Row(Lang.T("Kosongkan rak otomatis"), null, Popup(new[] { Lang.T("Tidak"), Lang.T("Setelah 1 hari"), Lang.T("Setelah 3 hari") },
                            Math.Max(0, Array.IndexOf(expireAt, settings.GetInt("shelf.expire", 0))), i => { settings.Set("shelf.expire", expireAt[i]); settings.Save(); })));
                        add(Row(Lang.T("Ubah format"), Lang.T("Kotak di samping Rak saat berkas diseret ke island"), Switch("convert", true, null)));
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
                            var agentRow = Row(AgentSetup.Label(aid), !present ? Lang.T("tidak ditemukan") : on ? Lang.T("terhubung") : Lang.T("belum terhubung"),
                                AgentButton(present, on, delegate { island.ToggleAgent(aid); Relayout(); }));
                            agentRow.Glyph = '\uE756';
                            add(agentRow);
                        }
                        cap(Lang.T("Pemberitahuan"));
                        add(Row(Lang.T("Suara"), Lang.T("Saat agen minta izin atau selesai, dan saat --run selesai"), Switch("sound.agent", false, null)));
                        cap(Lang.T("Server hook"));
                        int port = island.hub != null ? island.hub.Port : settings.GetInt("agent.port", 47821);
                        add(Row(Lang.F("Port {0}", port), port == 47821 ? Lang.T("Port bawaan lama; port acak lebih sulit ditebak") : Lang.T("Hanya bisa dijangkau dari laptop ini"),
                            Button(Lang.T("Ganti port acak"), false, delegate { island.MoveToRandomPort(); Relayout(); })));
                        add(Row(Lang.T("Menahan izin"), Lang.T("Lewat batas ini, izin dijawab di terminal"), Slider(30, 300, 10, settings.GetInt("agent.wait", 110), v => v + Lang.T(" dtk"), v => { settings.Set("agent.wait", v); settings.Save(); })));
                        break;
                    case "phone":
                        add(Row(Lang.T("Hubungkan HP"), Lang.T("Tepi di Android, lewat Wi-Fi yang sama"), Switch("phone", false, on =>
                        {
                            if (on) island.StartPhone(); else island.StopPhone();
                            Relayout();
                        })));
                        if (island.phone != null)
                        {
                            if (island.phone.Error != null) add(Row(island.phone.Error, null, null));
                            string pc = PhoneLink.Address() + ":" + island.phone.Port;
                            string pair = island.phone.Code;
                            add(Row(pair != null ? pair : island.phone.IsPaired ? Lang.F("Tersambung ke {0}", island.phone.Device) : Lang.T("Belum ada HP"),
                                pair != null ? Lang.F("Alamat {0} · berlaku 5 menit", pc) : Lang.F("Alamat {0}", pc),
                                Button(island.phone.IsPaired ? Lang.T("Ganti HP") : Lang.T("Buat kode"), false, delegate { island.phone.Forget(); island.phone.NewCode(); Relayout(); })));
                            if (pair != null)
                            {
                                var link = island.phone;
                                var qr = new KitQr(PhoneLink.PairLink(link.Port, pair)) { Size = new Size((int)(168 * Ui), (int)(168 * Ui)), AccessibleName = Lang.T("Kode QR pasangan") };
                                // Paired, expired, or replaced: rebuild
                                qr.StillValid = () => link.Code == pair;
                                qr.Expired += () => BeginInvoke(new Action(Relayout));
                                var qrRow = Row(Lang.T("Pindai dari HP"), Lang.T("Di HP: Integrasi › Laptop › Pindai QR"), qr);
                                qrRow.Height = (int)(188 * Ui);
                                add(qrRow);
                            }
                        }
                        cap(Lang.T("Dari HP"));
                        // Meaningless until a phone is linked
                        bool linked = island.phone != null;
                        var fromNotifs = Switch("phone.notifs", true, null);
                        var fromOtp = Switch("phone.otp", true, null);
                        fromNotifs.Enabled = fromOtp.Enabled = linked;
                        add(Row(Lang.T("Notifikasi HP"), linked ? Lang.T("Pesan dari HP ikut masuk ke Pesan") : Lang.T("Nyalakan Hubungkan HP dulu"), fromNotifs));
                        add(Row(Lang.T("Kode OTP"), linked ? Lang.T("Di island; hilang dari clipboard 1 menit kemudian") : Lang.T("Nyalakan Hubungkan HP dulu"), fromOtp));
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
                        var weatherSw = Switch("weather", false, null);
                        weatherSw.Enabled = island.PrayCity() != null;
                        add(Row(Lang.T("Cuaca dan hujan"), weatherSw.Enabled ? Lang.T("Suhu di Ringkasan dan peringatan hujan, dari Open-Meteo") : Lang.T("Pilih kota dulu"), weatherSw));
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
                            Seg(new[] { Lang.T("Otomatis"), Lang.T("Nyala"), Lang.T("Mati") }, motion == "1" ? 1 : motion == "0" ? 2 : 0, i => island.SetChoice("motion.reduce", i == 1 ? "1" : i == 2 ? "0" : "auto"))));
                        string feel = settings.Get("motion.feel", "bawaan");
                        add(Row(Lang.T("Gerak"), Lang.T("Kecepatan dan pantulan island"),
                            Seg(new[] { Lang.T("Tenang"), Lang.T("Bawaan"), Lang.T("Lincah") }, feel == "tenang" ? 0 : feel == "lincah" ? 2 : 1, i => island.SetChoice("motion.feel", i == 0 ? "tenang" : i == 2 ? "lincah" : "bawaan"))));
                        add(Row(Lang.T("Senyap"), Lang.T("Hanya izin, tenggat, sholat, dan timer yang muncul"), Switch("dnd", false, null)));
                        add(Row(Lang.T("Senyap ikut Windows"), Lang.T("Ikut menyala saat Jangan ganggu Windows aktif"), Switch("dnd.follow", false, null)));
                        break;
                    case "keys":
                        // How to change, said once up top
                        cap(Lang.T("Klik pintasan, lalu tekan kombinasi baru. Backspace mengosongkan, Esc batal."));
                        bool prompterCap = false;
                        foreach (var d in Hotkeys)
                        {
                            if (d.Prompter && !prompterCap) { cap(Lang.T("Teleprompter, hanya selama berjalan")); prompterCap = true; }
                            add(HotkeyRow(d));
                        }
                        flush();
                        add(Row(Lang.T("Pintasan bawaan"), null, Button(Lang.T("Kembalikan"), false, delegate { island.ResetHotkeys(); Relayout(); })));
                        break;
                    case "about":
                        add(VersionRow(Button("GitHub", false, delegate { OpenUrl("https://github.com/odlaver/tepi"); })));
                        add(UpdateRow());
                        add(Row(Lang.T("Layanan luar"), Lang.T("LRCLIB, NetEase, Kulia, GitHub, Open-Meteo, dan integrasi yang tersambung; semuanya bisa dimatikan"), null));
                        // QRIS dialog lives on the site
                        add(Row(Lang.T("Traktir Tepi kopi"), Lang.T("QRIS, dari bank atau e-wallet mana pun"), Button(Lang.T("Traktir"), false, delegate { OpenUrl("https://odlaver.github.io/tepi/#kopi"); })));
                        cap(Lang.F("Dibuat oleh {0}", "R. Revaldo Vabiansyah"));
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

            // Tab row: grip | icon, name, switch
            readonly Dictionary<string, Control> tabFocus = new Dictionary<string, Control>();

            KitRow TabRow(int page)
            {
                int at = Array.IndexOf(island.tabs, page);
                string name = Lang.T(PageNames[page]);
                var sw = new KitSwitch { Checked = at >= 0, Size = new Size((int)(46 * Ui), (int)(26 * Ui)) };
                sw.CheckedChanged += delegate
                {
                    if (building) return;
                    var list = new List<int>(island.tabs);
                    if (sw.Checked && !list.Contains(page)) list.Add(page);
                    else if (!sw.Checked && list.Count > 1) list.Remove(page);
                    island.SetLayout("panel.tabs", island.TabIds(list));
                    Preview();
                    RefillTabs(page + "sw");
                };
                tabFocus[page + "sw"] = sw;
                var r = Row(name, null, sw);
                r.Lead = 38;
                if (at < 0) return r;
                r.Divider = true;
                var grip = new TabGrip { Size = new Size((int)(30 * Ui), (int)(30 * Ui)), AccessibleName = Lang.F("Urutan {0}", name), AccessibleDescription = Lang.T("Seret, atau tekan panah atas dan bawah") };
                grip.Location = new Point((int)(5 * Ui), (r.Height - grip.Height) / 2);
                grip.Dropped = to => MoveTabTo(page, to);
                grip.Nudge = step => MoveTabTo(page, at + step);
                r.Controls.Add(grip);
                tabFocus[page + "grip"] = grip;
                return r;
            }

            void MoveTabTo(int page, int to)
            {
                var list = new List<int>(island.tabs);
                int i = list.IndexOf(page);
                if (i < 0 || to < 0 || to >= list.Count || to == i) return;
                list.RemoveAt(i);
                list.Insert(to, page);
                island.SetLayout("panel.tabs", island.TabIds(list));
                Preview();
                RefillTabs(page + "grip");
            }

            // Rebuild rows; scroll and focus stay
            void RefillTabs(string focusKey)
            {
                var at = content.AutoScrollPosition;
                bool refocus = ContainsFocus;
                BeginInvoke(new Action(delegate
                {
                    Fill();
                    content.AutoScrollPosition = new Point(-at.X, -at.Y);
                    Control c;
                    if (refocus && tabFocus.TryGetValue(focusKey, out c) && c.Visible) c.Focus();
                }));
            }

            // Six-dot handle: drag, or arrow keys
            class TabGrip : KitControl
            {
                public Action<int> Dropped, Nudge;
                KitRow row;
                List<KitRow> peers;
                int grabY, startTop, baseTop, slot, target;
                bool dragging;

                public TabGrip() { Cursor = Cursors.SizeNS; }

                protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

                protected override void OnKeyDown(KeyEventArgs e)
                {
                    base.OnKeyDown(e);
                    if ((e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) || Nudge == null) return;
                    e.Handled = true;
                    Nudge(e.KeyCode == Keys.Up ? -1 : 1);
                }

                static bool Shown(KitRow r)
                {
                    foreach (Control c in r.Controls) if (c is TabGrip) return true;
                    return false;
                }

                // Group space: scrolling keeps the row under the pointer
                int GroupY(MouseEventArgs e) { return row.Parent.PointToClient(PointToScreen(e.Location)).Y; }

                protected override void OnMouseDown(MouseEventArgs e)
                {
                    base.OnMouseDown(e);
                    if (e.Button != MouseButtons.Left || Parent == null) return;
                    row = Parent as KitRow;
                    if (row == null || row.Parent == null) return;
                    peers = new List<KitRow>();
                    foreach (Control c in row.Parent.Controls)
                    {
                        var r = c as KitRow;
                        if (r != null && Shown(r)) peers.Add(r);
                    }
                    peers.Sort((a, b) => a.Top.CompareTo(b.Top));
                    slot = target = peers.IndexOf(row);
                    baseTop = peers[0].Top;
                    startTop = row.Top;
                    grabY = GroupY(e);
                    dragging = true;
                    Capture = true;
                    row.BringToFront();
                    Lift(true);
                }

                protected override void OnMouseMove(MouseEventArgs e)
                {
                    base.OnMouseMove(e);
                    if (!dragging) return;
                    int h = row.Height, max = baseTop + (peers.Count - 1) * h;
                    int top = Math.Max(baseTop, Math.Min(max, startTop + GroupY(e) - grabY));
                    row.Top = top;
                    target = Math.Max(0, Math.Min(peers.Count - 1, (top - baseTop + h / 2) / h));
                    // Others close ranks around the gap
                    int k = 0;
                    foreach (var p in peers)
                    {
                        if (p == row) continue;
                        if (k == target) k++;
                        p.Top = baseTop + k * h;
                        k++;
                    }
                    row.Parent.Invalidate();
                }

                protected override void OnMouseUp(MouseEventArgs e)
                {
                    base.OnMouseUp(e);
                    Drop();
                }

                protected override void OnMouseCaptureChanged(EventArgs e)
                {
                    base.OnMouseCaptureChanged(e);
                    if (!Capture) Drop();
                }

                void Drop()
                {
                    if (!dragging) return;
                    dragging = false;
                    Capture = false;
                    Lift(false);
                    row.Top = baseTop + target * row.Height;
                    if (target != slot && Dropped != null) Dropped(target);
                }

                // Held row takes the hover tint
                void Lift(bool on)
                {
                    row.BackColor = on ? KitInk.Hover : Color.Transparent;
                    row.Invalidate(true);
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    var g = e.Graphics;
                    KitInk.Smooth(g);
                    g.Clear(KitInk.Under(this));
                    float s = Ui, cx = Width / 2f, cy = Height / 2f, d = 3f * s;
                    if (Hovered && !dragging) Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f, KitInk.Line);
                    using (var b = new SolidBrush(dragging || Hovered || Focused ? KitInk.Text : KitInk.Sub))
                        for (int x = -1; x <= 1; x += 2)
                            for (int y = -1; y <= 1; y++)
                                g.FillEllipse(b, cx + x * 3f * s - d / 2, cy + y * 5f * s - d / 2, d, d);
                    Ring(g, new RectangleF(1, 1, Width - 2, Height - 2), Height / 2f);
                }
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
