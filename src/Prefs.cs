using System;
using System.Collections.Generic;
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
            if (key == "glass") ApplyGlass();
            else if (key == "dock") ResetFrost();
            else if (key == "notif") notifWatcher.Enabled = NotifOn;
            else if (key == "lyrics.online") { lyrics = null; SyncLyrics(); }
            else if (key == "clipboard") ApplyClips();
            else if (key == "downloads") ApplyDownloads();
            else if (key == "capture.show") ApplyGlass();
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

        class PrefsWindow : Form
        {
            readonly Island island;
            readonly Settings settings;
            readonly Color bg, fg, sub, field;
            readonly CheckedListBox tabList = new CheckedListBox();
            readonly Timer preview = new Timer { Interval = 2500 };
            bool loading = true;

            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

            public PrefsWindow(Island island)
            {
                this.island = island;
                settings = island.settings;
                Text = Lang.T("Pengaturan Tepi");
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size(760, 676);
                ShowInTaskbar = true;
                Icon = island.tray != null ? island.tray.Icon : null;
                bg = Ink.Light ? Color.FromArgb(245, 245, 245) : Color.FromArgb(10, 10, 10);
                fg = Ink.Light ? Color.FromArgb(10, 10, 10) : Color.FromArgb(244, 244, 244);
                sub = Ink.Light ? Color.FromArgb(88, 88, 88) : Color.FromArgb(160, 160, 160);
                field = Ink.Light ? Color.White : Color.FromArgb(24, 24, 24);
                BackColor = bg;
                ForeColor = fg;
                Font = Ink.Text(14f);
                KeyPreview = true;
                KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
                preview.Tick += delegate { preview.Stop(); island.PreviewLayout(false); };
                FormClosed += delegate { preview.Stop(); island.PreviewLayout(false); };

                BuildLook(24);
                BuildPanel(400);
                loading = false;
            }

            // Rebuild in new colours or language, same spot
            void Reopen()
            {
                var at = Location;
                BeginInvoke((Action)delegate
                {
                    Close();
                    island.OpenPrefs();
                    island.prefs.Location = at;
                });
            }

            // Left column: look, placement, accessibility
            void BuildLook(int x)
            {
                int y = 20;
                Heading("Tampilan", x, ref y);
                var lang = Combo(x, ref y, "Bahasa", new[] { "Indonesia", "English" }, Lang.English ? 1 : 0);
                lang.SelectedIndexChanged += delegate
                {
                    if (loading) return;
                    island.SetLanguage(lang.SelectedIndex == 1);
                    Reopen();
                };
                var theme = Combo(x, ref y, "Tema", new[] { Lang.T("Gelap"), Lang.T("Terang") }, Ink.Light ? 1 : 0);
                theme.SelectedIndexChanged += delegate
                {
                    if (loading) return;
                    island.SetLight(theme.SelectedIndex == 1);
                    Reopen();
                };

                var names = new List<string>();
                int accentAt = 0;
                string accent = settings.Get("accent", "");
                for (int i = 0; i < Accents.Length; i++) { names.Add(Lang.T(Accents[i][0])); if (Accents[i][1] == accent) accentAt = i; }
                var accentBox = Combo(x, ref y, "Aksen", names.ToArray(), accentAt);
                accentBox.SelectedIndexChanged += delegate { if (!loading) island.SetAccentHex(Accents[accentBox.SelectedIndex][1]); };

                var screens = Screen.AllScreens;
                var labels = new string[screens.Length];
                string chosen = settings.Get("monitor", "");
                int screenAt = 0;
                for (int i = 0; i < screens.Length; i++)
                {
                    labels[i] = Lang.F("Layar {0}", i + 1) + (screens[i].Primary ? " · " + Lang.T("utama") : "") + " · " + screens[i].Bounds.Width + "×" + screens[i].Bounds.Height;
                    if (chosen.Length > 0 ? screens[i].DeviceName == chosen : screens[i].Primary) screenAt = i;
                }
                var screenBox = Combo(x, ref y, "Layar", labels, screenAt);
                screenBox.Enabled = screens.Length > 1;
                screenBox.SelectedIndexChanged += delegate
                {
                    if (loading) return;
                    var sc = screens[screenBox.SelectedIndex];
                    island.UseMonitor(sc.Primary ? "" : sc.DeviceName);
                };

                Check(x, ref y, "Efek kaca", "glass", true);
                Check(x, ref y, "Menempel di tepi atas", "dock", true);

                y += 14;
                Heading("Tata letak", x, ref y);
                Slider(x, ref y, "Posisi", -40, 40, settings.GetInt("position", 0), 5, v => v == 0 ? Lang.T("tengah") : Lang.T(v < 0 ? "kiri" : "kanan") + " " + Math.Abs(v) + "%",
                       v => { island.SetLayout("position", v); Preview(); });
                Slider(x, ref y, "Lebar panel", 440, 560, (int)island.PanelW, 8, v => v + " px",
                       v => { island.SetLayout("panel.width", v); Preview(); });

                y += 14;
                Heading("Aksesibilitas", x, ref y);
                Check(x, ref y, "Tampil di rekaman layar (tanpa efek kaca)", "capture.show", false);
                Check(x, ref y, "Umumkan ke pembaca layar", "a11y.announce", true);
            }

            // Right column: tabs and island content
            void BuildPanel(int x)
            {
                int y = 20;
                Heading("Tab panel", x, ref y);
                tabList.SetBounds(x, y, 220, PageNames.Length * 24 + 6);
                tabList.BorderStyle = BorderStyle.FixedSingle;
                tabList.BackColor = field;
                tabList.ForeColor = fg;
                tabList.CheckOnClick = true;
                var order = new List<int>(island.tabs);
                for (int pg = 0; pg < PageNames.Length; pg++) if (!order.Contains(pg)) order.Add(pg);
                foreach (int pg in order) tabList.Items.Add(new TabItem(pg), Array.IndexOf(island.tabs, pg) >= 0);
                tabList.ItemCheck += (s, e) =>
                {
                    if (loading) return;
                    // At least one tab stays
                    if (e.NewValue == CheckState.Unchecked && tabList.CheckedItems.Count <= 1) { e.NewValue = CheckState.Checked; return; }
                    BeginInvoke((Action)SaveTabs);
                };
                Controls.Add(tabList);
                var up = SmallButton("Naik", x + 232, y, delegate { Shift(-1); });
                SmallButton("Turun", x + 232, up.Bottom + 8, delegate { Shift(1); });
                y += tabList.Height + 12;

                var openNames = new List<string> { Lang.T("Tab pertama") };
                foreach (var n in PageNames) openNames.Add(Lang.T(n));
                int openAt = settings.GetInt("panel.page", -1) + 1;
                var openBox = Combo(x, ref y, "Dibuka di", openNames.ToArray(), Math.Max(0, Math.Min(openNames.Count - 1, openAt)));
                openBox.SelectedIndexChanged += delegate { if (!loading) island.SetLayout("panel.page", openBox.SelectedIndex - 1); };

                y += 14;
                Heading("Island", x, ref y);
                Check(x, ref y, "Lirik di island", "lyrics", true);
                Check(x, ref y, "Cari lirik online", "lyrics.online", true);
                Check(x, ref y, "Visualizer", "visualizer", true);
                Check(x, ref y, "Notifikasi pesan", "notif", true);
                Check(x, ref y, "Tampilkan isi pesan", "notif.content", true);
                Check(x, ref y, "Kelas yang berlangsung", "class.island", true);
                Check(x, ref y, "Progres unduhan", "downloads", true);
                Check(x, ref y, "Riwayat clipboard", "clipboard", false);
                var auto = new CheckBox { Text = Lang.T("Mulai bersama Windows"), FlatStyle = FlatStyle.Flat, AutoSize = false };
                auto.SetBounds(x, y, 336, 28);
                try { auto.Checked = AutoStart.Enabled; } catch (Exception) { }
                auto.CheckedChanged += delegate
                {
                    if (loading) return;
                    try { AutoStart.Enabled = auto.Checked; } catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
                };
                Controls.Add(auto);
                y += 32;

                var restLabel = new Label { Text = Lang.T("Istirahat fokus (menit)"), ForeColor = sub, AutoSize = false };
                restLabel.SetBounds(x, y + 4, 220, 24);
                var rest = new NumericUpDown { Minimum = 1, Maximum = 30, BorderStyle = BorderStyle.FixedSingle, BackColor = field, ForeColor = fg };
                rest.SetBounds(x + 232, y, 104, 30);
                rest.Value = Math.Max(1, Math.Min(30, settings.GetInt("focus.break", 5)));
                rest.ValueChanged += delegate { if (loading) return; settings.Set("focus.break", (int)rest.Value); settings.Save(); };
                Controls.Add(restLabel);
                Controls.Add(rest);
            }

            class TabItem
            {
                public readonly int Page;
                public TabItem(int page) { Page = page; }
                public override string ToString() { return Lang.T(PageNames[Page]); }
            }

            void Shift(int step)
            {
                int i = tabList.SelectedIndex, j = i + step;
                if (i < 0 || j < 0 || j >= tabList.Items.Count) return;
                var item = tabList.Items[i];
                bool on = tabList.GetItemChecked(i);
                loading = true;
                tabList.Items.RemoveAt(i);
                tabList.Items.Insert(j, item);
                tabList.SetItemChecked(j, on);
                tabList.SelectedIndex = j;
                loading = false;
                SaveTabs();
            }

            void SaveTabs()
            {
                var on = new List<string>();
                for (int i = 0; i < tabList.Items.Count; i++)
                    if (tabList.GetItemChecked(i)) on.Add(((TabItem)tabList.Items[i]).Page.ToString());
                island.SetLayout("tabs", string.Join(",", on.ToArray()));
                Preview();
            }

            void Preview()
            {
                island.PreviewLayout(true);
                preview.Stop();
                preview.Start();
            }

            void Heading(string text, int x, ref int y)
            {
                var l = new Label { Text = Lang.T(text), Font = Ink.Display(18f), AutoSize = false };
                l.SetBounds(x, y, 336, 30);
                Controls.Add(l);
                y += 38;
            }

            ComboBox Combo(int x, ref int y, string label, string[] items, int selected)
            {
                var l = new Label { Text = Lang.T(label), ForeColor = sub, AutoSize = false };
                l.SetBounds(x, y + 4, 110, 24);
                var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = fg };
                c.SetBounds(x + 116, y, 220, 30);
                c.Items.AddRange(items);
                if (items.Length > 0) c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, selected));
                Controls.Add(l);
                Controls.Add(c);
                y += 38;
                return c;
            }

            void Check(int x, ref int y, string label, string key, bool fallback)
            {
                var c = new CheckBox { Text = Lang.T(label), FlatStyle = FlatStyle.Flat, AutoSize = false, Checked = settings.GetBool(key, fallback) };
                c.SetBounds(x, y, 336, 28);
                c.CheckedChanged += delegate { if (!loading) island.SetFlag(key, c.Checked); };
                Controls.Add(c);
                y += 32;
            }

            void Slider(int x, ref int y, string label, int min, int max, int value, int step, Func<int, string> show, Action<int> apply)
            {
                var l = new Label { Text = Lang.T(label), ForeColor = sub, AutoSize = false };
                l.SetBounds(x, y, 200, 24);
                var v = new Label { ForeColor = fg, AutoSize = false, TextAlign = ContentAlignment.TopRight };
                v.SetBounds(x + 200, y, 136, 24);
                var t = new TrackBar { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step, TickFrequency = step * 2, AutoSize = false, BackColor = bg };
                t.SetBounds(x - 4, y + 26, 344, 32);
                t.Value = Math.Max(min, Math.Min(max, value));
                v.Text = show(t.Value);
                t.ValueChanged += delegate
                {
                    // Snap to the step
                    int snapped = min + (int)Math.Round((t.Value - min) / (double)step) * step;
                    if (snapped != t.Value) { t.Value = Math.Min(max, snapped); return; }
                    v.Text = show(t.Value);
                    if (!loading) apply(t.Value);
                };
                Controls.Add(l);
                Controls.Add(v);
                Controls.Add(t);
                y += 66;
            }

            Button SmallButton(string text, int x, int y, Action act)
            {
                var b = new Button { Text = Lang.T(text), FlatStyle = FlatStyle.Flat, BackColor = field, ForeColor = fg, Cursor = Cursors.Hand };
                b.FlatAppearance.BorderColor = sub;
                b.SetBounds(x, y, 104, 32);
                b.Click += delegate { act(); };
                Controls.Add(b);
                return b;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int dark = Ink.Light ? 0 : 1;
                try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch (Exception) { }
            }
        }
    }
}
