using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Offline model download, outlives the window
    class DictFetch
    {
        public volatile bool Running, Cancel;
        public long Done, Total;
        public string Error, Model;
    }

    partial class Island
    {
        DictFetch dictFetch;

        void FetchModel(Speech.LocalModel m)
        {
            if (dictFetch != null && dictFetch.Running) return;
            var f = new DictFetch { Running = true, Model = m.Id, Total = Speech.Missing(m) };
            dictFetch = f;
            ThreadPool.QueueUserWorkItem(delegate
            {
                f.Error = Speech.Fetch(m, (done, total) => { Interlocked.Exchange(ref f.Done, done); f.Total = total; }, () => f.Cancel);
                f.Running = false;
                Post(delegate
                {
                    if (f.Error == null) DictSay(Lang.T("Dikte offline siap"), Lang.T(m.Name), true);
                    else if (!f.Cancel) DictSay(f.Error, "", true);
                });
            });
        }

        static string Mb(long bytes) { return Math.Max(1, (bytes + 524288) / 1048576).ToString(CultureInfo.InvariantCulture) + " MB"; }

        partial class PrefsWindow
        {
            void DictateRows(Action<KitRow> add, Action<string> cap)
            {
                var words = Words.Load();
                string keyName = settings.Get("dictate.key", "rctrl") == "ralt" ? Lang.T("Alt kanan") : Lang.T("Ctrl kanan");
                string missing = settings.GetBool("dictate", false) ? island.DictMissing() : null;
                add(Row(Lang.T("Dikte"), missing ?? Lang.F("Tahan {0}, bicara, lepas. Teks ditempel di aplikasi yang sedang aktif.", keyName), Switch("dictate", false, on =>
                {
                    settings.Set("dictate", on);
                    settings.Save();
                    island.ApplyDictate();
                    Rebuild();
                })));
                add(Row(Lang.T("Tombol dikte"), Lang.T("Ketuk dua kali untuk dikte tanpa tangan, Esc membatalkan"),
                    Popup(new[] { Lang.T("Ctrl kanan"), Lang.T("Alt kanan") }, settings.Get("dictate.key", "rctrl") == "ralt" ? 1 : 0, i => { settings.Set("dictate.key", i == 1 ? "ralt" : "rctrl"); settings.Save(); Rebuild(); })));
                string lang = settings.Get("dictate.lang", Lang.English ? "en" : "id");
                add(Row(Lang.T("Bahasa ucapan"), Lang.T("Istilah Inggris tetap dikenali"),
                    Seg(new[] { "Indonesia", "English", Lang.T("Otomatis") }, lang == "en" ? 1 : lang == "auto" ? 2 : 0, i => { settings.Set("dictate.lang", i == 1 ? "en" : i == 2 ? "auto" : "id"); settings.Save(); })));

                var sp = Ai.Stt;
                string engine = Ai.SttId == Ai.Offline ? Lang.T("Offline") : sp != null ? Ai.Label(sp) : "?";
                add(Row(Lang.T("Mesin dikte"), Lang.F("{0} · diatur di halaman AI", engine), Button(Lang.T("Buka AI"), false, delegate { Go("ai"); })));

                cap(Lang.T("Hasil"));
                add(Row(Lang.T("Kamus dan snippet"), Lang.F("{0} istilah, {1} pengganti, {2} snippet", words.Terms.Count, words.Swaps.Count, words.Snippets.Count),
                    Button(Lang.T("Ubah…"), false, delegate { using (var w = new WordsWindow()) w.ShowDialog(this); island.ApplyDictate(); Rebuild(); })));
                add(Row(Lang.T("Buang kata pengisi"), Lang.T("eh, em, anu, apa namanya"), Switch("dictate.fillers", true, null)));
                add(Row(Lang.T("Perintah baris baru"), Lang.T("Ucapkan \"baris baru\" atau \"paragraf baru\""), Switch("dictate.commands", true, null)));
                add(Row(Lang.T("Gaya per aplikasi"), Lang.T("Chat dan terminal tanpa titik penutup; terminal tanpa Enter"), Switch("dictate.style", true, null)));
                add(Row(Lang.T("Kecilkan musik"), Lang.T("Selama kamu bicara"), Switch("dictate.duck", true, null)));

                cap(Lang.T("Rapat"));
                var rec = Button(island.RapatOn ? Lang.T("Hentikan") : Lang.T("Mulai rekam"), !island.RapatOn, delegate { island.ToggleRapat(); Rebuild(); });
                rec.Enabled = !island.rapatWriting;
                add(Row(Lang.T("Rekam rapat"), island.rapatWriting ? Lang.T("Transkrip sedang ditulis") : Lang.T("Mic jadi Kamu, suara laptop jadi Lawan bicara"), rec));
                bool hasText = Ai.Ready(Ai.Text);
                add(Row(Lang.T("Ringkasan dan to-do"), hasText ? Lang.F("Ditulis {0}, layanan teks di halaman AI", Ai.Label(Ai.Text)) : Lang.T("Butuh layanan teks di halaman AI; tanpa itu hanya transkrip"), Switch("rapat.summary", true, null)));
                add(Row(Lang.T("Folder transkrip"), Lang.T("Audio dihapus setelah transkrip jadi"), Button(Lang.T("Buka"), false, delegate
                {
                    try { System.IO.Directory.CreateDirectory(RapatDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + RapatDir + "\""); }
                    catch (Exception ex) { Log.Write("folder rapat: " + ex.Message); }
                })));

                int total = settings.GetInt("dictate.words", 0);
                if (total > 0)
                {
                    int today = settings.Get("dictate.day", "") == DateTime.Now.ToString("yyyy-MM-dd") ? settings.GetInt("dictate.today", 0) : 0;
                    cap(Lang.T("Hitungan"));
                    var count = Row(Lang.F("{0} kata hari ini", today), Lang.F("{0} kata sejak awal, dihitung di laptop ini", total), null);
                    count.Glyph = '\uE9D9';
                    add(count);
                }
            }

            System.Windows.Forms.Timer fetchTick;

            void OfflineRows(Action<KitRow> add, bool remote)
            {
                var m = Speech.Model(settings.Get("dictate.model", "seimbang"));
                string[] names = new string[Speech.Models.Length];
                int at = 0;
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = Lang.T(Speech.Models[i].Name) + " · " + Mb(Speech.Models[i].Size);
                    if (Speech.Models[i].Id == m.Id) at = i;
                }
                add(Row(Lang.T("Model offline"), Lang.T("Makin besar, makin teliti dan lambat"),
                    Popup(names, at, i => { settings.Set("dictate.model", Speech.Models[i].Id); settings.Save(); Rebuild(); })));
                var f = island.dictFetch;
                if (f != null && f.Running && f.Model == m.Id)
                {
                    KitRow row = null;
                    row = Row(Lang.T("Mengunduh"), "", Button(Lang.T("Batal"), false, delegate { f.Cancel = true; Rebuild(); }));
                    add(row);
                    if (fetchTick == null)
                    {
                        fetchTick = new System.Windows.Forms.Timer { Interval = 300 };
                        FormClosed += delegate { fetchTick.Stop(); fetchTick.Dispose(); };
                    }
                    fetchTick.Tick -= FetchTickHandler;
                    fetchTick.Tick += FetchTickHandler;
                    fetchRow = row;
                    fetchTick.Start();
                    FetchTickHandler(null, EventArgs.Empty);
                    return;
                }
                if (Speech.LocalReady(m))
                {
                    add(Row(Lang.T("Siap dipakai"), remote ? Lang.F("{0} di laptop ini, dipakai saat internet putus", Mb(Speech.LocalBytes())) : Lang.F("{0} di laptop ini", Mb(Speech.LocalBytes())),
                        Button(Lang.T("Hapus"), false, delegate { Speech.RemoveLocal(); Rebuild(); })));
                    return;
                }
                string note = f != null && f.Error != null && !f.Cancel && f.Model == m.Id ? f.Error : Lang.T("whisper.cpp dan model dari Hugging Face, dicek SHA-256");
                add(Row(Lang.T("Unduh model"), Mb(Speech.Missing(m)) + " · " + note, Button(Lang.T("Unduh"), !remote, delegate { island.FetchModel(m); Rebuild(); })));
            }

            KitRow fetchRow;

            void FetchTickHandler(object sender, EventArgs e)
            {
                var f = island.dictFetch;
                if (f == null || !f.Running) { if (fetchTick != null) fetchTick.Stop(); if (sender != null) Rebuild(); return; }
                long done = Interlocked.Read(ref f.Done);
                int pct = f.Total > 0 ? (int)(done * 100 / f.Total) : 0;
                Say(fetchRow, Lang.F("{0}% dari {1}", pct, Mb(f.Total)));
            }
        }
    }

    // Terms, replacements, snippets in one place
    class WordsWindow : KitWindow
    {
        readonly KitSegmented tabs = new KitSegmented();
        readonly TextBox[] boxes = { new KitText(), new KitText(), new KitText() };
        readonly Label hint = new Label();
        readonly KitButton save = new KitButton(), close = new KitButton();
        static readonly string[] Hints =
        {
            "Satu istilah per baris. Ejaannya dipakai persis, misalnya useEffect atau Supabase.",
            "Satu per baris: ucapan => tulisan. Contoh: kulia => Kulia",
            "Satu per baris: pemicu => teks. \\n jadi baris baru. Dikte yang sama persis dengan pemicu menempel teksnya.",
        };

        public WordsWindow()
        {
            Text = Lang.T("Kamus dan snippet");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            var w = Words.Load();
            boxes[0].Text = string.Join("\r\n", w.Terms.ToArray());
            var swaps = new List<string>();
            foreach (var s in w.Swaps) swaps.Add(s.Key + " => " + s.Value);
            boxes[1].Text = string.Join("\r\n", swaps.ToArray());
            var snips = new List<string>();
            foreach (var s in w.Snippets) snips.Add(s.Key + " => " + s.Value.Replace("\r", "").Replace("\n", "\\n"));
            boxes[2].Text = string.Join("\r\n", snips.ToArray());
            for (int i = 0; i < boxes.Length; i++)
            {
                var b = boxes[i];
                b.Multiline = true;
                b.AcceptsReturn = true;
                b.ScrollBars = ScrollBars.Vertical;
                b.BorderStyle = BorderStyle.None;
                b.WordWrap = false;
                b.Visible = i == 0;
                b.AccessibleName = Lang.T(new[] { "Kamus", "Pengganti", "Snippet" }[i]);
                b.GotFocus += delegate { Invalidate(); };
                b.LostFocus += delegate { Invalidate(); };
                Controls.Add(b);
            }
            tabs.Items = new[] { Lang.T("Kamus"), Lang.T("Pengganti"), Lang.T("Snippet") };
            tabs.Selected = 0;
            tabs.AccessibleName = Lang.T("Jenis");
            tabs.Changed += i => { for (int k = 0; k < boxes.Length; k++) boxes[k].Visible = k == i; hint.Text = Lang.T(Hints[i]); boxes[i].Focus(); };
            hint.Text = Lang.T(Hints[0]);
            hint.AutoSize = false;
            save.Text = Lang.T("Simpan");
            save.Primary = true;
            save.Click += delegate { Save(); DialogResult = DialogResult.OK; Close(); };
            close.Text = Lang.T("Tutup");
            close.Click += delegate { Close(); };
            Controls.Add(tabs);
            Controls.Add(hint);
            Controls.Add(save);
            Controls.Add(close);
            AcceptButton = null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ClientSize = new Size((int)(520 * Ui), (int)(420 * Ui));
            MinimumSize = Size;
            Relayout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.S)) { save.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keys);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Place(); }

        public override void Relayout()
        {
            BackColor = KitInk.Window;
            Native.DarkFrame(Handle, !Ink.Light);
            foreach (var b in boxes) { b.Font = new Font(Ink.MenuFamily, 13f * Ui, GraphicsUnit.Pixel); b.BackColor = KitInk.Group; b.ForeColor = KitInk.Text; Native.ThemeScroll(b); }
            hint.Font = UiFont(12f);
            hint.ForeColor = KitInk.Sub;
            hint.BackColor = KitInk.Window;
            tabs.Font = UiFont(12.5f);
            foreach (var b in new[] { save, close }) b.Font = UiFont(12.5f);
            Place();
            Invalidate(true);
        }

        RectangleF well;

        // Margins as in Tepi's other dialogs
        void Place()
        {
            float s = Ui;
            Func<float, int> u = v => (int)Math.Round(v * s);
            int m = u(24), w = ClientSize.Width - m * 2;
            tabs.SetBounds(m, u(20), u(330), u(30));
            hint.SetBounds(m + u(2), tabs.Bottom + u(10), w, u(36));
            int btnH = u(30), bottom = ClientSize.Height - u(16) - btnH;
            well = new RectangleF(m, hint.Bottom + u(6), w, bottom - hint.Bottom - u(22));
            foreach (var b in boxes) b.SetBounds((int)well.X + u(14), (int)well.Y + u(12), (int)well.Width - u(20), (int)well.Height - u(20));
            save.Size = new Size(TextRenderer.MeasureText(save.Text, save.Font).Width + u(32), btnH);
            close.Size = new Size(TextRenderer.MeasureText(close.Text, close.Font).Width + u(32), btnH);
            save.Location = new Point(ClientSize.Width - u(16) - save.Width, bottom);
            close.Location = new Point(save.Left - u(8) - close.Width, bottom);
            Invalidate();
        }

        // Text well with a quiet focus ring
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(KitInk.Window);
            if (well.Width <= 0) return;
            KitInk.Smooth(g);
            bool focused = Array.Exists(boxes, b => b.Focused);
            using (var path = Ink.Round(well, 12 * Ui))
            {
                using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                using (var pen = new Pen(focused ? KitInk.Faint : KitInk.Line, 1.2f)) g.DrawPath(pen, path);
            }
        }

        void Save()
        {
            var text = "[kamus]\n" + boxes[0].Text.Replace("\r", "") + "\n[ganti]\n" + boxes[1].Text.Replace("\r", "") + "\n[snippet]\n" + boxes[2].Text.Replace("\r", "");
            var w = new Words();
            w.Parse(text);
            try { w.Save(); }
            catch (Exception ex) { Log.Write("simpan dikte.txt: " + ex.Message); }
        }
    }
}
