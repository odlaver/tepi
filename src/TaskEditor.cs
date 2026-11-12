using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    // Edits a draft only; the caller commits after OK. Cancel never mutates a task.
    sealed class TaskEditor : KitWindow
    {
        readonly KitText title = new KitText();
        readonly KitCheck hasDue = new KitCheck { Text = "Tenggat" };
        readonly KitPopup date = new KitPopup();
        readonly KitPopup time = new KitPopup();
        readonly KitCheck star = new KitCheck { Text = "Prioritas" };
        readonly KitCheck done = new KitCheck { Text = "Selesai" };
        readonly Label warning = new Label { AutoSize = false };
        bool titleInvalid;
        readonly KitButton save = new KitButton { Text = "Simpan", Primary = true };
        readonly KitButton cancel = new KitButton { Text = "Batal" };
        readonly Label titleLabel = new Label { Text = "Judul tugas", AutoSize = true };

        // Longest list the date picker shows; rebuilt around the chosen month
        const int DaysShown = 60;
        DateTime chosen;
        Font uiFont, fSmall;

        public string TaskTitle { get { return title.Text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); } }
        public DateTime? Due { get { return hasDue.Checked ? (DateTime?)(chosen.Date + TimeSpan.FromMinutes(time.Selected * 15)) : null; } }
        public bool Star { get { return star.Checked; } }
        public bool Done { get { return done.Checked; } }

        public TaskEditor(TodoItem item)
        {
            Text = item == null ? "Tugas baru" : "Edit tugas";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            title.AccessibleName = "Judul tugas";
            title.MaxLength = 2000;
            date.AccessibleName = "Tanggal tenggat";
            time.AccessibleName = "Jam tenggat";
            title.Text = item == null ? "" : item.Title;
            var fallback = DateTime.Today.AddDays(1);
            chosen = item != null && item.Due.HasValue ? item.Due.Value.Date : fallback;
            hasDue.Checked = item != null && item.Due.HasValue;
            star.Checked = item != null && item.Star; done.Checked = item != null && item.Done;

            date.Items = DateItems();
            date.Selected = Math.Max(0, Math.Min(DaysShown - 1, 7));
            time.Items = TimeItems();
            int slot = item != null && item.Due.HasValue ? ((item.Due.Value.Hour * 60 + item.Due.Value.Minute) / 15) : (9 * 4);
            time.Selected = Math.Max(0, Math.Min(95, slot));
            // A new list keeps the chosen day; re-picking only recentres it with the same items
            date.Changed += delegate(int i) { chosen = DayFrom(i); UpdateDue(); };
            time.Changed += delegate(int i) { UpdateDue(); };

            hasDue.CheckedChanged += delegate { UpdateDue(); };
            date.Enabled = time.Enabled = hasDue.Checked;
            UpdateDue();
            title.TextChanged += delegate { if (titleInvalid && TaskTitle.Length > 0) { titleInvalid = false; warning.Text = hasDue.Checked && Due.HasValue && Due.Value <= DateTime.Now ? "Tenggat sudah lewat" : ""; } };
            save.Click += delegate
            {
                if (TaskTitle.Length == 0) { titleInvalid = true; warning.Text = "Judul wajib diisi"; title.Focus(); return; }
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { titleLabel, title, hasDue, date, time, star, done, warning, save, cancel });
            Shown += delegate { title.Focus(); title.SelectAll(); };
        }

        static string DayLabel(DateTime d) { return d.ToString("ddd, dd MMM", UiCulture); }
        static System.Globalization.CultureInfo UiCulture { get { return System.Globalization.CultureInfo.CurrentCulture; } }

        string[] DateItems()
        {
            var items = new string[DaysShown];
            for (int i = 0; i < DaysShown; i++) items[i] = DayLabel(chosen.AddDays(i - 7));
            return items;
        }

        DateTime DayFrom(int i)
        {
            if (i < 0 || i >= DaysShown) return chosen;
            return chosen.AddDays(i - 7);
        }

        string[] TimeItems()
        {
            var items = new string[96];
            for (int i = 0; i < 96; i++) items[i] = (i / 4).ToString("00") + ":" + (i % 4 * 15).ToString("00");
            return items;
        }

        void UpdateDue()
        {
            date.Enabled = time.Enabled = hasDue.Checked;
            if (hasDue.Checked && Due.Value <= DateTime.Now) warning.Text = "Tenggat sudah lewat";
            else if (warning.Text == "Judul wajib diisi" && titleInvalid) return;
            else if (warning.Text == "Tenggat sudah lewat") warning.Text = "";
            date.AccessibleDescription = "Tanggal tenggat";
            time.AccessibleDescription = "Jam tenggat lokal";
            warning.ForeColor = Color.IndianRed;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }

        public override void Relayout()
        {
            if (!IsHandleCreated) return;
            if (uiFont == null) uiFont = UiFont(14);
            Font = uiFont; BackColor = KitInk.Window; ForeColor = KitInk.Text;
            float s = Ui;
            titleLabel.Font = UiFont(12.5f);
            titleLabel.SetBounds((int)(20*s), (int)(18*s), (int)(430*s), (int)(24*s));
            title.SetBounds((int)(20*s), (int)(48*s), (int)(440*s), (int)(30*s));
            title.BackColor = KitInk.Group; title.ForeColor = KitInk.Text;
            if (fSmall == null) fSmall = UiFont(12.5f);
            date.Font = time.Font = star.Font = done.Font = hasDue.Font = fSmall;
            hasDue.SetBounds((int)(20*s), (int)(94*s), (int)(140*s), (int)(28*s));
            date.SetBounds((int)(20*s), (int)(130*s), (int)(258*s), (int)(30*s));
            time.SetBounds((int)(288*s), (int)(130*s), (int)(172*s), (int)(30*s));
            star.SetBounds((int)(20*s), (int)(174*s), (int)(140*s), (int)(28*s));
            done.SetBounds((int)(170*s), (int)(174*s), (int)(140*s), (int)(28*s));
            warning.SetBounds((int)(20*s), (int)(212*s), (int)(440*s), (int)(30*s)); warning.ForeColor = Color.IndianRed;
            cancel.SetBounds((int)(250*s), (int)(258*s), (int)(100*s), (int)(34*s));
            save.SetBounds((int)(360*s), (int)(258*s), (int)(100*s), (int)(34*s));
            ClientSize = new Size((int)(480*s), (int)(310*s));
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.Enter)) { save.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keys);
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) { if (uiFont != null) uiFont.Dispose(); if (fSmall != null) fSmall.Dispose(); } }
    }
}
