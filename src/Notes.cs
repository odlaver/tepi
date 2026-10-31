using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Quick note: one line, Ctrl+Alt+N
    partial class Island
    {
        const int NoteHotkey = 13;
        NoteBox noteBox;

        static string NotesPath { get { return Path.Combine(Settings.Dir, "catatan.md"); } }

        void StartNotes()
        {
            if (!RegisterHotKey(Handle, NoteHotkey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.N)) Log.Write("hotkey Ctrl+Alt+N taken");
        }

        void OpenNote()
        {
            if (noteBox != null && !noteBox.IsDisposed) { noteBox.Activate(); return; }
            var targets = new List<string> { "rak" };
            if (sources != null && sources.Find("todoist") != null) targets.Add("todoist");
            if (sources != null && sources.Find("notion") != null) targets.Add("notion");
            noteBox = new NoteBox(targets, settings.Get("note.to", "rak"), SaveNote);
            // Just under the island
            var area = Screen.FromHandle(Handle).WorkingArea;
            noteBox.StartPosition = FormStartPosition.Manual;
            noteBox.Show();
            noteBox.Location = new Point(area.X + (area.Width - noteBox.Width) / 2, area.Y + (int)(70 * scale));
            Native.SetForegroundWindow(noteBox.Handle);
        }

        void SaveNote(string text, string to)
        {
            settings.Set("note.to", to);
            settings.Save();
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) return;
            if (to == "rak")
            {
                try
                {
                    File.AppendAllText(NotesPath, "- " + DateTime.Now.ToString("yyyy-MM-dd HH.mm") + "  " + text + Environment.NewLine);
                    if (settings.GetBool("shelf", true)) AddToShelf(new[] { NotesPath });
                    ShowInfo(IcoShelf, Lang.T("Disimpan ke catatan"), Preview(text, 40), true);
                }
                catch (Exception ex) { Log.Write("note: " + ex.Message); ShowInfo(IcoShelf, Lang.T("Catatan gagal disimpan"), "", true); }
                return;
            }
            string name = to == "notion" ? "Notion" : "Todoist";
            sources.Create(to, text, err =>
            {
                if (err == null) ShowInfo(Ink.IcoCalendar, Lang.F("Tugas baru di {0}", name), Preview(text, 40), true);
                else ShowInfo(Ink.IcoCalendar, err, "", true);
            });
        }

        class NoteBox : KitWindow
        {
            readonly KitText box = new KitText();
            readonly KitSegmented seg = new KitSegmented();
            readonly KitButton save = new KitButton { Primary = true };
            readonly List<string> targets;
            readonly Action<string, string> done;

            public NoteBox(List<string> targets, string last, Action<string, string> done)
            {
                this.targets = targets;
                this.done = done;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                Text = Lang.T("Catatan kilat");
                box.BorderStyle = BorderStyle.None;
                box.Multiline = false;
                var names = new List<string>();
                foreach (var t in targets) names.Add(t == "rak" ? Lang.T("Rak") : t == "notion" ? "Notion" : "Todoist");
                seg.Items = names.ToArray();
                seg.Selected = Math.Max(0, targets.IndexOf(last));
                seg.AccessibleName = Lang.T("Simpan ke");
                save.Text = Lang.T("Simpan");
                save.Click += delegate { Commit(); };
                box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
                Controls.Add(box);
                Controls.Add(seg);
                Controls.Add(save);
                Deactivate += delegate { if (!IsDisposed) Close(); };
            }

            // Shadow for a frameless popup
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Relayout();
            }

            public override void Relayout()
            {
                float s = Ui;
                BackColor = KitInk.Window;
                ClientSize = new Size((int)(460 * s), (int)(106 * s));
                box.Font = UiFont(15f);
                box.BackColor = KitInk.Group;
                box.ForeColor = KitInk.Text;
                box.SetBounds((int)(30 * s), (int)(22 * s), ClientSize.Width - (int)(60 * s), (int)(24 * s));
                Native.SetCue(box.Handle, Lang.T("Tulis catatan"));
                seg.Font = UiFont(12.5f);
                float w = 0;
                using (var f = UiFont(12.5f)) foreach (var it in seg.Items) w = Math.Max(w, TextRenderer.MeasureText(it, f).Width);
                seg.SetBounds((int)(16 * s), ClientSize.Height - (int)(44 * s), (int)((w + 30 * s) * seg.Items.Length), (int)(30 * s));
                seg.Visible = seg.Items.Length > 1;
                save.Font = UiFont(12.5f);
                save.Size = new Size(TextRenderer.MeasureText(save.Text, save.Font).Width + (int)(32 * s), (int)(30 * s));
                save.Location = new Point(ClientSize.Width - save.Width - (int)(16 * s), ClientSize.Height - (int)(44 * s));
                Invalidate();
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                box.Focus();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(KitInk.Window);
                KitInk.Smooth(g);
                float s = Ui;
                // Text well with a quiet focus ring
                var well = new RectangleF(16 * s, 12 * s, ClientSize.Width - 32 * s, 44 * s);
                using (var path = Ink.Round(well, 12 * s))
                {
                    using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                    using (var pen = new Pen(KitInk.Faint, 1.2f)) g.DrawPath(pen, path);
                }
                // One destination: say where it goes
                if (!seg.Visible)
                    using (var f = UiFont(12.5f))
                        KitInk.Draw(g, Lang.T("Disimpan ke Rak"), f, KitInk.Sub, new RectangleF(20 * s, ClientSize.Height - 44 * s, 240 * s, 30 * s), ContentAlignment.MiddleLeft);
            }

            void Commit()
            {
                string text = box.Text;
                string to = targets[Math.Max(0, Math.Min(targets.Count - 1, seg.Selected))];
                Close();
                done(text, to);
            }
        }
    }
}
