using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // First run: guided tour under island
    partial class Island
    {
        TourCard tour;

        // Step: text, optional done check
        class TourStep
        {
            public string Title, Body;
            public Func<bool> Done;
        }

        void TourOnFirstRun()
        {
            if (!settings.Fresh || settings.GetBool("tour.done", false)) return;
            var wait = new System.Windows.Forms.Timer { Interval = 4500 };
            wait.Tick += delegate { wait.Stop(); wait.Dispose(); StartTour(); };
            wait.Start();
        }

        public void StartTour()
        {
            if (tour != null && !tour.IsDisposed) { tour.Activate(); return; }
            hiddenByUser = false;
            expanded = false;
            Kick();
            int startPage = -1;
            bool hover = settings.Get("panel.open", "hover") != "click";
            string dictKey = settings.Get("dictate.key", "rctrl") == "ralt" ? Lang.T("Alt kanan") : Lang.T("Ctrl kanan");
            string rapatKey = HotkeyText(RapatHotkey), navKey = HotkeyText(KeysHotkey);
            // Only the tabs this user shows
            var tabNames = new List<string>();
            foreach (int pg in tabs) tabNames.Add(TabLabel(pg));
            string voice = Lang.F("Tahan {0}, bicara, lalu lepas: teksnya ditempel di aplikasi yang sedang aktif.", dictKey) + " "
                + (rapatKey.Length > 0 ? Lang.F("Tekan {0} untuk merekam rapat tanpa bot; suaramu dan lawan bicara dipisah di transkripnya.", rapatKey)
                    : Lang.T("Rekam rapat dari menu klik kanan, tanpa bot; suaramu dan lawan bicara dipisah di transkripnya."))
                + (DictOn ? "" : " " + Lang.T("Siapkan dulu di Pengaturan › Dikte."));
            string ask = (DictOn ? Lang.F("Tahan Shift+{0} lalu ucapkan perintahnya, atau ketik lewat Suruh Tepi… di menu klik kanan.", dictKey)
                    : Lang.T("Ketik perintah lewat Suruh Tepi… di menu klik kanan."))
                + " " + Lang.T("Misalnya \"timer 25 menit\", \"ingetin minum 30 menit lagi\", atau \"catat beli galon\". Perintah seperti ini jalan tanpa internet.");
            string menu = Lang.T("Klik kanan island atau ikon Tepi di tray untuk timer fokus, teleprompter, catatan kilat, cermin kamera, dan Pengaturan. Sholat, minum, dan cuaca dinyalakan di Pengaturan › Pengingat.")
                + (navKey.Length > 0 ? " " + Lang.F("{0} memindahkan fokus ke island untuk dipakai dengan keyboard.", navKey) : "");
            var steps = new[]
            {
                new TourStep { Title = Lang.T("Ini Tepi"), Body = Lang.T("Island di tepi atas ini membesar saat ada yang terjadi: lagu diputar, pesan masuk, agent minta izin, unduhan berjalan, atau flashdisk dicolok. Saat tidak dipakai, ia bisa menyusut jadi garis kecil bermata yang tembus klik.") },
                new TourStep
                {
                    Title = Lang.T("Buka panel"),
                    Body = hover ? Lang.T("Arahkan kursor ke island di atas. Panelnya terbuka sendiri, dan tertutup lagi saat kursor pergi.") : Lang.T("Klik island di atas untuk membuka panelnya."),
                    Done = () => expanded,
                },
                new TourStep
                {
                    Title = Lang.T("Pindah tab"),
                    Body = Lang.F("Selagi panel terbuka, scroll di atasnya untuk pindah tab: {0}. Tab yang tampil bisa diatur di Pengaturan › Tab panel.", string.Join(", ", tabNames.ToArray())),
                    Done = () => { if (!expanded) return false; if (startPage < 0) startPage = page; return page != startPage; },
                },
                new TourStep { Title = Lang.T("Agent coding"), Body = Lang.T("Claude Code, Codex, Gemini CLI, dan Antigravity tampil di tab Agen beserta rencana kerjanya. Saat agent minta izin, pilih Izinkan atau Tolak langsung di island. Sambungkan lewat klik kanan › Hubungkan agen.") },
                new TourStep { Title = Lang.T("Tugas dan pesan"), Body = Lang.T("Tugas dan jadwal dari Kulia, Notion, Todoist, atau kalender iCal masuk ke tab Kuliah, dengan pengingat sebelum tenggat. Pesan WhatsApp, Telegram, Discord, dan lainnya dikelompokkan per pengirim. Sambungkan di Pengaturan › Integrasi.") },
                new TourStep { Title = Lang.T("Rak dan ubah format"), Body = Lang.T("Seret berkas ke island, lalu lepas di Taruh di rak untuk menyimpannya sementara, atau di Ubah format untuk mengonversinya, misalnya gambar ke PDF. Screenshot juga muncul di island dan bisa langsung diseret.") },
                new TourStep { Title = Lang.T("Dikte dan rapat"), Body = voice },
                new TourStep { Title = Lang.T("Suruh Tepi"), Body = ask },
                new TourStep { Title = Lang.T("Menu dan Pengaturan"), Body = menu },
            };
            tour = new TourCard(this, steps);
            tour.FormClosed += delegate
            {
                settings.Set("tour.done", true);
                settings.Save();
                tour = null;
            };
            tour.Show();
            PlaceTour();
            Native.SetForegroundWindow(tour.Handle);
        }

        // Just under the island, following its size
        void PlaceTour()
        {
            if (tour == null || tour.IsDisposed || tour.Pinned) return;
            int cx = lastShellPx.Width > 0 ? lastLeft + lastShellPx.X + lastShellPx.Width / 2 : monitorPx.Left + monitorPx.Width / 2;
            int y = monitorPx.Top + (int)((shell.Y + Math.Max(sh.Target, 30) + 14) * scale);
            y = Math.Min(y, monitorPx.Bottom - tour.Height - 60);
            int x = Math.Max(monitorPx.Left + 12, Math.Min(monitorPx.Right - tour.Width - 12, cx - tour.Width / 2));
            tour.Glide(new Point(x, y));
        }

        class TourCard : KitWindow
        {
            readonly Island isle;
            readonly TourStep[] steps;
            readonly Label count = new Label(), title = new Label(), body = new Label();
            readonly KitButton next = new KitButton(), back = new KitButton(), skip = new KitButton();
            readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 200 };
            readonly System.Windows.Forms.Timer glide = new System.Windows.Forms.Timer { Interval = 15 };
            int at;
            DateTime doneSince = DateTime.MinValue;
            Point target;
            bool placed;

            // Dragged by hand: stays put
            public bool Pinned;

            [DllImport("user32.dll")] static extern bool ReleaseCapture();
            [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

            public TourCard(Island isle, TourStep[] steps)
            {
                this.isle = isle;
                this.steps = steps;
                Text = Lang.T("Tur Tepi");
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                foreach (var l in new[] { count, title, body })
                {
                    l.AutoSize = true;
                    l.UseMnemonic = false;
                    l.MouseDown += (s, e) => Drag(e);
                    l.DoubleClick += delegate { Unpin(); };
                    Controls.Add(l);
                }
                next.Primary = true;
                next.Click += delegate { Go(at + 1); };
                back.Click += delegate { Go(at - 1); };
                skip.Click += delegate { Close(); };
                Controls.Add(skip);
                Controls.Add(back);
                Controls.Add(next);
                AcceptButton = next;
                // Doable steps advance once done
                watch.Tick += delegate
                {
                    isle.PlaceTour();
                    var d = steps[at].Done;
                    if (d == null || !d()) { doneSince = DateTime.MinValue; return; }
                    if (doneSince == DateTime.MinValue) doneSince = DateTime.UtcNow;
                    // Let the change be seen first
                    else if ((DateTime.UtcNow - doneSince).TotalSeconds > 1.2) Go(at + 1);
                };
                glide.Tick += delegate { Step(); };
                FormClosed += delegate { watch.Stop(); watch.Dispose(); glide.Stop(); glide.Dispose(); };
            }

            // Frameless popup with the system shadow
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Go(0);
                watch.Start();
            }

            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Drag(e); }

            protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); Unpin(); }

            void Drag(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }

            void Unpin()
            {
                Pinned = false;
                isle.PlaceTour();
            }

            protected override void WndProc(ref Message m)
            {
                // WM_ENTERSIZEMOVE: the user took it
                if (m.Msg == 0x0231) { Pinned = true; glide.Stop(); }
                base.WndProc(ref m);
            }

            public void Glide(Point to)
            {
                target = to;
                if (!placed || Anim.Reduced) { placed = true; Location = to; return; }
                if (Location != to && !glide.Enabled) glide.Start();
            }

            // Eases toward the island's new edge
            void Step()
            {
                if (Pinned) { glide.Stop(); return; }
                var p = Location;
                int dx = target.X - p.X, dy = target.Y - p.Y;
                if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) { Location = target; glide.Stop(); return; }
                Location = new Point(p.X + (int)Math.Round(dx * 0.22), p.Y + (int)Math.Round(dy * 0.22));
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keys)
            {
                if (keys == Keys.Left && at > 0) { Go(at - 1); return true; }
                if (keys == Keys.Right) { Go(at + 1); return true; }
                return base.ProcessCmdKey(ref msg, keys);
            }

            void Go(int i)
            {
                if (i >= steps.Length) { Close(); return; }
                at = Math.Max(0, i);
                doneSince = DateTime.MinValue;
                Relayout();
                // Enter and Space hit Next
                if (IsHandleCreated) next.Focus();
                AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
                // Screen readers hear each step
                body.AccessibleName = title.Text + ". " + body.Text;
                // The island waves hello
                if (at == 0) isle.ShowInfo('\0', Lang.T("Ini Tepi"), "", true);
            }

            public override void Relayout()
            {
                float s = Ui;
                Func<float, int> u = v => (int)Math.Round(v * s);
                BackColor = KitInk.Window;
                Native.DarkFrame(Handle, !Ink.Light);
                // Same margins as Tepi's other popups
                int pad = u(24), w = u(420), inner = w - pad * 2;
                var step = steps[at];
                count.Text = Lang.F("{0} dari {1}", at + 1, steps.Length);
                count.Font = UiFont(12f);
                count.ForeColor = KitInk.Sub;
                title.Text = step.Title;
                title.Font = UiFont(20f);
                title.ForeColor = KitInk.Text;
                body.Text = step.Body;
                body.Font = UiFont(13f);
                body.ForeColor = KitInk.Sub;
                body.MaximumSize = new Size(inner, 0);
                foreach (var l in new[] { count, title, body }) l.BackColor = KitInk.Window;
                count.Location = new Point(pad, u(22));
                title.Location = new Point(pad - u(2), count.Bottom + u(6));
                body.Location = new Point(pad, title.Bottom + u(6));
                // Doable steps say so instead of Lanjut
                bool doable = step.Done != null;
                next.Text = at == steps.Length - 1 ? Lang.T("Selesai") : doable ? Lang.T("Lewati langkah") : Lang.T("Lanjut");
                next.Primary = !doable;
                back.Text = Lang.T("Kembali");
                back.Visible = at > 0;
                skip.Text = Lang.T("Tutup tur");
                skip.Visible = at < steps.Length - 1;
                int btnH = u(30), by = body.Bottom + u(20);
                foreach (var b in new[] { next, back, skip })
                {
                    b.Font = UiFont(12.5f);
                    b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + u(32), btnH);
                    b.Top = by;
                }
                next.Left = w - u(16) - next.Width;
                back.Left = next.Left - u(8) - back.Width;
                skip.Left = u(16);
                ClientSize = new Size(w, by + btnH + u(16));
                isle.PlaceTour();
                Invalidate(true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                KitInk.Smooth(g);
                float s = Ui;
                // Grabber: the card can be dragged
                using (var path = Ink.Round(new RectangleF(ClientSize.Width / 2f - 18 * s, 8 * s, 36 * s, 4 * s), 2 * s))
                using (var b = new SolidBrush(KitInk.Line)) g.FillPath(b, path);
                // Progress: one dot per step
                float r = 3f * s, gap = 6f * s, x = ClientSize.Width - 24 * s, y = count.Top + count.Height / 2f;
                for (int i = steps.Length - 1; i >= 0; i--)
                {
                    float wDot = i == at ? 14 * s : r * 2;
                    x -= wDot;
                    using (var path = Ink.Round(new RectangleF(x, y - r, wDot, r * 2), r))
                    using (var b = new SolidBrush(i == at ? KitInk.Text : i < at ? KitInk.Sub : KitInk.Line)) g.FillPath(b, path);
                    x -= gap;
                }
            }
        }
    }
}
