using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Suggestions go to a Google Form
    static class Feedback
    {
        // Form's formResponse address and its field ids
        const string FormUrl = "https://docs.google.com/forms/d/e/1FAIpQLSdDsBrxC6LIIlq4Itbab9WwW416oyCjpZDQu5JySwDmBdVrUg/formResponse";
        const string EntryKind = "entry.467859773", EntryText = "entry.617759107", EntryContact = "entry.263767280",
            EntryVersion = "entry.1329022008", EntryWindows = "entry.1141987323";

        public static bool Ready { get { return FormUrl.Length > 0 && EntryText.Length > 0; } }

        static string Windows()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return Environment.OSVersion.VersionString;
                    string name = Convert.ToString(k.GetValue("ProductName")), build = Convert.ToString(k.GetValue("CurrentBuild"));
                    int b;
                    // Registry still says 10 on 11
                    if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return name + " build " + build;
                }
            }
            catch (Exception) { return Environment.OSVersion.VersionString; }
        }

        // Worker: null when sent, else why not
        public static string Send(string kind, string text, string contact, bool withSystem)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(EntryKind, kind),
                new KeyValuePair<string, string>(EntryText, text),
                new KeyValuePair<string, string>(EntryContact, contact),
                new KeyValuePair<string, string>(EntryVersion, withSystem ? Island.AppVersion + (Lang.English ? " · en" : " · id") : ""),
                new KeyValuePair<string, string>(EntryWindows, withSystem ? Windows() : ""),
            };
            var body = new StringBuilder();
            foreach (var f in fields)
            {
                if (f.Key.Length == 0 || f.Value.Length == 0) continue;
                if (body.Length > 0) body.Append('&');
                body.Append(Uri.EscapeDataString(f.Key)).Append('=').Append(Uri.EscapeDataString(f.Value));
            }
            var r = Web.Send("POST", FormUrl, new Dictionary<string, string> { { "Content-Type", "application/x-www-form-urlencoded" } }, body.ToString(), 20000);
            if (r.Ok) return null;
            Log.Write("saran: " + r.Status);
            return r.Status == 0 ? Lang.T("Gagal terkirim, cek internet") : Lang.F("Formulir menolak ({0})", r.Status);
        }
    }

    // Write a suggestion, send it once
    class FeedbackWindow : KitWindow
    {
        static readonly string[] Kinds = { "Saran fitur", "Ada masalah", "Lainnya" };
        readonly KitSegmented kind = new KitSegmented();
        readonly KitText text = new KitText(), contact = new KitText();
        readonly KitSwitch system = new KitSwitch { Checked = true };
        readonly Label systemLabel = new Label(), note = new Label();
        readonly KitButton send = new KitButton(), close = new KitButton();
        RectangleF textWell, contactWell;

        public FeedbackWindow()
        {
            Text = Lang.T("Kirim saran");
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            var names = new string[Kinds.Length];
            for (int i = 0; i < names.Length; i++) names[i] = Lang.T(Kinds[i]);
            kind.Items = names;
            kind.Selected = 0;
            kind.AccessibleName = Lang.T("Jenis");
            text.Multiline = true;
            text.AcceptsReturn = true;
            text.ScrollBars = ScrollBars.Vertical;
            text.MaxLength = 2000;
            text.AccessibleName = Lang.T("Pesan");
            contact.MaxLength = 120;
            contact.AccessibleName = Lang.T("Kontak");
            foreach (var t in new TextBox[] { text, contact })
            {
                t.BorderStyle = BorderStyle.None;
                t.GotFocus += delegate { Invalidate(); };
                t.LostFocus += delegate { Invalidate(); };
                t.TextChanged += delegate { send.Enabled = text.Text.Trim().Length >= 3; };
            }
            system.AccessibleName = Lang.T("Sertakan versi Tepi dan Windows");
            systemLabel.UseMnemonic = note.UseMnemonic = false;
            systemLabel.AutoSize = false;
            note.AutoSize = false;
            send.Text = Lang.T("Kirim");
            send.Primary = true;
            send.Enabled = false;
            send.Click += delegate { Submit(); };
            close.Text = Lang.T("Tutup");
            close.Click += delegate { Close(); };
            foreach (Control c in new Control[] { kind, text, contact, system, systemLabel, note, close, send }) Controls.Add(c);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Relayout();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); text.Focus(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == (Keys.Control | Keys.Enter) && send.Enabled) { send.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keys);
        }

        public override void Relayout()
        {
            float s = Ui;
            Func<float, int> u = v => (int)Math.Round(v * s);
            BackColor = KitInk.Window;
            Native.DarkFrame(Handle, !Ink.Light);
            int m = u(24), w = u(480), inner = w - 2 * m;
            kind.Font = UiFont(12.5f);
            kind.SetBounds(m, u(20), u(330), u(30));
            textWell = new RectangleF(m, kind.Bottom + u(14), inner, u(150));
            text.Font = UiFont(13f);
            text.SetBounds((int)textWell.X + u(14), (int)textWell.Y + u(12), (int)textWell.Width - u(20), (int)textWell.Height - u(20));
            Native.ThemeScroll(text);
            contactWell = new RectangleF(m, textWell.Bottom + u(10), inner, u(38));
            contact.Font = UiFont(13f);
            contact.SetBounds((int)contactWell.X + u(14), (int)contactWell.Y + (u(38) - contact.PreferredHeight) / 2, (int)contactWell.Width - u(28), contact.PreferredHeight);
            Native.SetCue(contact.Handle, Lang.T("Kontak untuk dibalas (opsional): email atau Instagram"));
            system.Size = new Size(u(46), u(26));
            system.Location = new Point(m + inner - system.Width, (int)contactWell.Bottom + u(14));
            systemLabel.Text = Lang.T("Sertakan versi Tepi dan Windows");
            systemLabel.Font = UiFont(12.5f);
            systemLabel.SetBounds(m + u(2), system.Top, inner - system.Width - u(12), system.Height);
            systemLabel.TextAlign = ContentAlignment.MiddleLeft;
            note.Font = UiFont(12f);
            note.SetBounds(m + u(2), system.Bottom + u(8), inner, u(36));
            if (note.Text.Length == 0) note.Text = Lang.T("Dikirim ke Google Form pembuat Tepi. Tanpa nama, tanpa isi layar.");
            foreach (var l in new[] { systemLabel, note }) { l.BackColor = KitInk.Window; l.ForeColor = l == note ? KitInk.Sub : KitInk.Text; }
            foreach (var t in new TextBox[] { text, contact }) { t.BackColor = KitInk.Group; t.ForeColor = KitInk.Text; }
            int btnH = u(30), by = note.Bottom + u(12);
            foreach (var b in new[] { send, close })
            {
                b.Font = UiFont(12.5f);
                b.Size = new Size(TextRenderer.MeasureText(b.Text, b.Font).Width + u(32), btnH);
                b.Top = by;
            }
            send.Left = m + inner - send.Width;
            close.Left = send.Left - u(8) - close.Width;
            ClientSize = new Size(w, by + btnH + u(18));
            Invalidate(true);
        }

        // Wells match the quick note
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(KitInk.Window);
            KitInk.Smooth(g);
            foreach (var pair in new[] { new KeyValuePair<RectangleF, TextBox>(textWell, text), new KeyValuePair<RectangleF, TextBox>(contactWell, contact) })
            {
                if (pair.Key.Width <= 0) continue;
                using (var path = Ink.Round(pair.Key, 12 * Ui))
                {
                    using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                    using (var pen = new Pen(pair.Value.Focused ? KitInk.Faint : KitInk.Line, 1.2f)) g.DrawPath(pen, path);
                }
            }
        }

        void Submit()
        {
            string k = Kinds[Math.Max(0, kind.Selected)], t = text.Text.Trim(), c = contact.Text.Trim();
            bool sys = system.Checked;
            send.Enabled = close.Enabled = text.Enabled = contact.Enabled = false;
            send.Text = Lang.T("Mengirim…");
            Relayout();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                try { err = Feedback.Send(k, t, c, sys); }
                catch (Exception ex) { err = Lang.T("Gagal terkirim, cek internet"); Log.Write("saran: " + ex.Message); }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed) return;
                        if (err == null)
                        {
                            note.Text = Lang.T("Terkirim. Terima kasih sudah meluangkan waktu.");
                            send.Text = Lang.T("Terkirim");
                            close.Enabled = true;
                            close.Text = Lang.T("Tutup");
                            Relayout();
                            close.Focus();
                            return;
                        }
                        // Failed: the text stays for another try
                        note.Text = err;
                        send.Text = Lang.T("Coba lagi");
                        send.Enabled = close.Enabled = text.Enabled = contact.Enabled = true;
                        Relayout();
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }
    }
}
