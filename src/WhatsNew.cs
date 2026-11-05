using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    // Release highlights, shown once per version
    static class WhatsNew
    {
        public class Note
        {
            public string Version;
            // Title, detail, icon from Settings
            public string[][] Items;
        }

        // Newest first; Indonesian source, English in Lang
        public static readonly Note[] Notes =
        {
            new Note
            {
                Version = "2.6.0.1",
                Items = new[]
                {
                    new[] { "Dikte", "Tahan Ctrl kanan di aplikasi apa pun, bicara, lalu lepas: teksnya ditempel di tempat kursor. Ada kamus istilah dan snippet, lewat Groq atau offline.", "\uE720" },
                    new[] { "Suruh Tepi", "Ketik atau ucapkan \"ingetin minum 30 menit lagi\", \"timer 5:30\", \"15% dari 240.000\", atau \"5 km ke mil\". Pengingat, timer, catatan, hitungan, dan konversi satuan jalan tanpa internet.", "\uE765" },
                    new[] { "Transkrip rapat", "Rekam panggilan tanpa bot dengan Ctrl+Alt+T. Mic jadi Kamu, suara laptop jadi Lawan bicara, lengkap dengan ringkasan dan to-do.", "\uE7C8" },
                    new[] { "Tab Bicara", "Dikte, rekam rapat, Suruh Tepi, pengingat yang menunggu, dan riwayatnya di satu tab.", "\uE8A9" },
                    new[] { "Ringkasan setelah fokus", "Selama sesi fokus, pesan biasa ditahan. Begitu selesai, island merangkum siapa saja yang mengirim pesan.", "\uE7E7" },
                    new[] { "Garis cahaya", "Progres timer, unduhan, dan cas baterai menyala di tepi island. Saat tugas hari ini habis, island ikut merayakan.", "\uE781" },
                    new[] { "Salin teks dari layar", "Pilih area mana pun, teksnya langsung tersalin. Ada di menu klik kanan, dan pintasannya bisa dipasang di Pengaturan.", "\uE8C8" },
                    new[] { "Samarkan", "Kartu screenshot punya tombol Samarkan: seret di atas data pribadi sebelum gambarnya dibagikan.", "\uE7B3" },
                    new[] { "Kirim saran", "Ide fitur atau laporan masalah bisa dikirim langsung dari Pengaturan, bagian Tentang.", "\uE939" },
                    new[] { "Tur dan Apa yang baru", "Pengguna baru dipandu sembilan langkah di bawah island, dan setiap pembaruan merangkum isinya di sini.", "\uE7C1" },
                    new[] { "Pengaturan lebih lega", "Jendelanya terbuka lebih besar, daftar di samping bisa digulir, dan ada opsi menyisakan ruang di tepi atas untuk jendela.", "\uE713" },
                    new[] { "Perbaikan kecil untuk agent", "Antigravity tidak lagi terblokir saat terhubung ke Tepi, setiap sesi agent kini bertanda Claude, Codex, atau Antigravity, dan sesi Codex atau Antigravity yang sudah diam 30 menit hilang sendiri dari daftar.", "\uE756" },
                },
            },
            new Note
            {
                Version = "2.5.2",
                Items = new[]
                {
                    new[] { "Ibadah sendiri", "Tambahkan doa, kebaktian, meditasi, atau waktu ibadah lain dengan jam, hari, dan suaranya sendiri, di samping jadwal sholat.", "" },
                    new[] { "Suara pengingat", "Minum, Istirahat mata, dan Peregangan kini berbunyi, dan tiap pengingat bisa diberi suara sendiri, misalnya berkas adzan untuk waktu sholat.", "" },
                    new[] { "Pengingat yang benar-benar terbaca", "Selama pengingat tampil, notifikasi lain menunggu dan musik dikecilkan sebentar.", "" },
                    new[] { "Mata saat diam", "Island yang diam menampilkan mata, jam tetap bisa dipilih.", "" },
                    new[] { "Mini lebih mudah dibuka", "Cukup diamkan kursor di dekat tepi atas, tidak harus tepat di piksel teratas.", "" },
                },
            },
        };

        static Version Parse(string v)
        {
            Version r;
            return Version.TryParse(v ?? "", out r) ? r : new Version(0, 0);
        }

        // Unseen notes up to now, newest first
        public static List<Note> Since(string seen, string current)
        {
            var list = new List<Note>();
            Version cur = Parse(current), last = Parse(seen);
            foreach (var n in Notes)
            {
                var v = Parse(n.Version);
                if (v <= cur && (string.IsNullOrEmpty(seen) ? v == cur : v > last)) list.Add(n);
            }
            return list;
        }

        public static Note For(string version)
        {
            foreach (var n in Notes) if (n.Version == version) return n;
            return null;
        }
    }

    partial class Island
    {
        // First Settings open after an update
        void OfferWhatsNew(Form owner)
        {
            string seen = settings.Get("whatsnew.seen", "");
            if (seen == AppVersion) return;
            settings.Set("whatsnew.seen", AppVersion);
            settings.Save();
            var notes = WhatsNew.Since(seen, AppVersion);
            if (notes.Count == 0) return;
            ShowWhatsNew(owner, notes);
        }

        void ShowWhatsNew(Form owner, List<WhatsNew.Note> notes)
        {
            using (var w = new WhatsNewWindow(notes)) w.ShowDialog(owner);
        }
    }

    class WhatsNewWindow : KitWindow
    {
        readonly List<WhatsNew.Note> notes;
        readonly Panel scroller = new Panel();
        readonly NoteList list = new NoteList();
        readonly KitButton done = new KitButton(), full = new KitButton();

        public WhatsNewWindow(List<WhatsNew.Note> notes)
        {
            this.notes = notes;
            list.Notes = notes;
            Text = Lang.T("Apa yang baru");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            scroller.AutoScroll = true;
            scroller.Controls.Add(list);
            done.Text = Lang.T("Oke");
            done.Primary = true;
            done.Click += delegate { Close(); };
            full.Text = Lang.T("Catatan lengkap");
            full.AccessibleDescription = "GitHub";
            full.Click += delegate { Island.OpenUrl("https://github.com/odlaver/tepi/releases/tag/v" + notes[0].Version); };
            Controls.Add(scroller);
            Controls.Add(done);
            Controls.Add(full);
            AcceptButton = done;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Relayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            done.Focus();
        }

        // Heading in the Settings voice
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(KitInk.Window);
            float s = Ui;
            using (var f = UiFont(24f))
                TextRenderer.DrawText(e.Graphics, Lang.F("Baru di Tepi {0}", notes[0].Version), f, new Point((int)(26 * s), (int)(22 * s)), KitInk.Text, TextFormatFlags.NoPrefix);
        }

        public override void Relayout()
        {
            float s = Ui;
            Func<float, int> u = v => (int)Math.Round(v * s);
            BackColor = KitInk.Window;
            Native.DarkFrame(Handle, !Ink.Light);
            // Settings page metrics: 28 margin, 520 group
            int m = u(28), w = u(520), top = u(78), btnH = u(30);
            list.Width = w;
            list.Measure(s);
            var area = Screen.FromHandle(Handle).WorkingArea;
            int room = Math.Max(u(200), area.Height - top - btnH - u(200));
            bool scrolls = list.Height > room;
            scroller.BackColor = KitInk.Window;
            Native.ThemeScroll(scroller);
            scroller.SetBounds(m, top, w + (scrolls ? SystemInformation.VerticalScrollBarWidth : 0), Math.Min(list.Height, room));
            list.Location = Point.Empty;
            foreach (var b in new[] { done, full })
            {
                b.Font = UiFont(12.5f);
                b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + u(32), btnH);
            }
            int by = scroller.Bottom + u(18);
            done.Location = new Point(scroller.Left + w - done.Width, by);
            full.Location = new Point(done.Left - u(8) - full.Width, by);
            ClientSize = new Size(scroller.Right + m, by + btnH + u(22));
            Invalidate(true);
        }

        // Rows drawn like Settings groups
        class NoteList : Control
        {
            public List<WhatsNew.Note> Notes;
            float s = 1;
            readonly List<RectangleF> groups = new List<RectangleF>();
            readonly List<KeyValuePair<float, string[]>> rows = new List<KeyValuePair<float, string[]>>();
            readonly List<KeyValuePair<float, string>> captions = new List<KeyValuePair<float, string>>();

            public NoteList()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                AccessibleRole = AccessibleRole.List;
            }

            const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            float TextX { get { return 56 * s; } }
            int TextW { get { return (int)(Width - TextX - 18 * s); } }

            public void Measure(float scale)
            {
                s = scale;
                groups.Clear(); rows.Clear(); captions.Clear();
                var spoken = new System.Text.StringBuilder();
                float y = 0;
                using (var title = new Font(Ink.MenuFamily, 13.5f * s, GraphicsUnit.Pixel))
                using (var detail = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
                    for (int k = 0; k < Notes.Count; k++)
                    {
                        // Older versions: a caption, like Settings
                        if (k > 0)
                        {
                            y += 22 * s;
                            captions.Add(new KeyValuePair<float, string>(y, Lang.F("Tepi {0}", Notes[k].Version)));
                            y += 24 * s;
                        }
                        float top = y;
                        foreach (var it in Notes[k].Items)
                        {
                            string t = Lang.T(it[0]), d = Lang.T(it[1]);
                            float th = TextRenderer.MeasureText(t, title, new Size(TextW, int.MaxValue), Wrap).Height;
                            float dh = TextRenderer.MeasureText(d, detail, new Size(TextW, int.MaxValue), Wrap).Height;
                            rows.Add(new KeyValuePair<float, string[]>(y, new[] { t, d, it.Length > 2 ? it[2] : "", (th).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                            y += 13 * s + th + 3 * s + dh + 14 * s;
                            spoken.Append(t).Append(". ").Append(d).Append(' ');
                        }
                        groups.Add(new RectangleF(0, top, Width - 1, y - top));
                    }
                Height = (int)Math.Ceiling(y) + 2;
                AccessibleName = spoken.ToString();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(KitInk.Window);
                KitInk.Smooth(g);
                foreach (var r in groups)
                    using (var path = Ink.Round(r, 12 * s))
                    using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                using (var cap = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
                    foreach (var c in captions) TextRenderer.DrawText(g, c.Value, cap, new Point((int)(14 * s), (int)c.Key), KitInk.Sub, TextFormatFlags.NoPrefix);
                using (var title = new Font(Ink.MenuFamily, 13.5f * s, GraphicsUnit.Pixel))
                using (var detail = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
                using (var icons = new Font(Ink.IconFamily, 15f * s, GraphicsUnit.Pixel))
                using (var line = new Pen(KitInk.Line, 1f))
                    for (int i = 0; i < rows.Count; i++)
                    {
                        float y = rows[i].Key;
                        var row = rows[i].Value;
                        float th = float.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture);
                        // Divider unless a group starts here
                        bool first = groups.Exists(gr => Math.Abs(gr.Y - y) < 0.5f);
                        if (!first) g.DrawLine(line, TextX, y, Width - 16 * s, y);
                        if (row[2].Length > 0)
                        {
                            var tile = new RectangleF(14 * s, y + 13 * s, 30 * s, 30 * s);
                            using (var path = Ink.Round(tile, 8 * s))
                            using (var b = new SolidBrush(KitInk.Hover)) g.FillPath(b, path);
                            KitInk.Draw(g, row[2], icons, KitInk.Text, tile, ContentAlignment.MiddleCenter);
                        }
                        TextRenderer.DrawText(g, row[0], title, new Rectangle((int)TextX, (int)(y + 13 * s), TextW, (int)th + 2), KitInk.Text, Wrap);
                        TextRenderer.DrawText(g, row[1], detail, new Rectangle((int)TextX, (int)(y + 16 * s + th), TextW, Height), KitInk.Sub, Wrap);
                    }
            }
        }
    }
}
