using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Tepi
{
    // Tasks and notes, one window. Tasks live in Island.todos; notes are Markdown files.
    sealed class WorkWindow : KitWindow
    {
        sealed class FlatList : ListBox
        {
            public string EmptyText = "";
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x000F && Items.Count == 0 && IsHandleCreated)
                    using (var g = CreateGraphics())
                        KitInk.Draw(g, EmptyText, Font, KitInk.Sub, ClientRectangle, ContentAlignment.MiddleCenter);
            }

            public FlatList()
            {
                // Native ListBox must retain WM_PAINT to raise DrawItem.
                SetStyle(ControlStyles.UserPaint, false);
                DrawMode = DrawMode.OwnerDrawFixed;
                BorderStyle = BorderStyle.None;
                IntegralHeight = false;
                ScrollAlwaysVisible = false;
            }

            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.ThemeScroll(this); }
        }

        readonly Island owner;
        readonly KitSegmented tabs = new KitSegmented();

        // Tasks
        readonly KitText addBox = new KitText();
        readonly KitButton addBtn = new KitButton { Primary = true };
        readonly FlatList taskList = new FlatList();
        readonly KitButton clearDone = new KitButton();
        readonly KitButton newTask = new KitButton { Text = "Baru" };
        readonly KitPopup taskFilter = new KitPopup();
        readonly Label saveState = new Label();
        List<TodoItem> savedTasks;
        string baseline;
        bool saveFailed;
        List<TodoItem> view = new List<TodoItem>();

        // Notes
        readonly KitText search = new KitText();
        readonly FlatList noteList = new FlatList();
        readonly KitText editor = new KitText();
        readonly KitButton newNote = new KitButton { Primary = true };
        readonly KitButton pinNote = new KitButton();
        readonly KitButton delNote = new KitButton();
        readonly Timer saveTimer = new Timer { Interval = 700 };
        HashSet<string> pins;
        List<NoteInfo> notes = new List<NoteInfo>();
        string current;
        bool loading, dirty;

        Font fText, fSub, fSmall, fBold;

        int Tab { get { return tabs.Selected; } }
        internal int CurrentTab { get { return tabs.Selected; } }
        internal void FocusInput() { FocusMain(); }

        public WorkWindow(Island owner)
        {
            this.owner = owner;
            Text = Lang.T("Tugas dan catatan");
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(640, 420);
            pins = NoteFiles.LoadPins();

            tabs.Items = new[] { Lang.T("Tugas"), Lang.T("Catatan") };
            tabs.AccessibleName = Lang.T("Tugas dan catatan");
            tabs.Changed += delegate(int next)
            {
                if (next == 0 && Tab == 1 && !SaveCurrent(true))
                {
                    tabs.Selected = 1;
                    ApplyTab();
                    return;
                }
                ApplyTab();
            };

            addBtn.Text = Lang.T("Tambah");
            newTask.Text = Lang.T("Tugas baru…");
            addBtn.Click += delegate { AddTask(); };
            addBox.BorderStyle = BorderStyle.None;
            addBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddTask(); } };
            clearDone.Text = Lang.T("Hapus selesai");
            clearDone.Click += delegate { ClearDone(); };
            taskList.EmptyText = "Belum ada tugas";
            noteList.EmptyText = "Belum ada catatan";
            taskList.DrawItem += DrawTask;
            taskList.MouseDown += TaskMouse;
            taskList.MouseMove += TaskHover;
            taskList.MouseLeave += delegate { hoverRow = -1; taskList.Cursor = Cursors.Default; taskList.Invalidate(); };
            taskList.KeyDown += TaskKey;
            taskList.DoubleClick += delegate { EditSelectedTask(); };
            newTask.Click += delegate { EditTask(null); };
            taskFilter.Items = new[] { "Semua", "Aktif", "Hari ini", "Terlambat", "Selesai" };
            taskFilter.Selected = 0;
            taskFilter.AccessibleName = "Filter tugas";
            taskFilter.Changed += delegate(int i) { RefillTasks(null); };
            var taskMenu = new ContextMenuStrip();
            taskMenu.Items.Add("Edit", null, delegate { EditSelectedTask(); });
            taskMenu.Items.Add("Hapus", null, delegate { DeleteSelectedTask(); });
            taskList.ContextMenuStrip = taskMenu;
            Disposed += delegate { taskMenu.Dispose(); };
            taskList.AccessibleName = Lang.T("Daftar tugas");
            addBox.AccessibleName = Lang.T("Judul tugas");

            search.BorderStyle = BorderStyle.None;
            search.TextChanged += delegate { RefreshNotes(current); };
            noteList.DrawItem += DrawNote;
            noteList.MouseDown += NoteMouse;
            noteList.SelectedIndexChanged += delegate { if (!loading) OpenSelected(); };
            noteList.AccessibleName = Lang.T("Daftar catatan");
            search.AccessibleName = Lang.T("Cari catatan");
            editor.AccessibleName = Lang.T("Isi catatan");
            editor.Enabled = false;
            editor.BorderStyle = BorderStyle.None;
            editor.Multiline = true;
            editor.AcceptsReturn = true;
            editor.WordWrap = true;
            editor.ScrollBars = ScrollBars.Vertical;
            editor.TextChanged += delegate { if (!loading && current != null) { dirty = true; saveFailed = false; SetSaveState(""); saveTimer.Stop(); saveTimer.Start(); } };
            editor.HandleCreated += delegate { Native.ThemeScroll(editor); };
            newNote.Text = Lang.T("Baru");
            newNote.Click += delegate { NewNote(); };
            pinNote.Text = Lang.T("Sematkan");
            pinNote.Click += delegate { TogglePin(); };
            delNote.Text = Lang.T("Hapus");
            delNote.Click += delegate { DeleteNote(); };
            saveTimer.Tick += delegate { saveTimer.Stop(); if (SaveCurrent(false)) RefreshNotes(current); };
            saveState.AutoEllipsis = true;
            saveState.Visible = false;

            Controls.AddRange(new Control[] { newTask, taskFilter, saveState });
            Controls.AddRange(new Control[] { tabs, addBox, addBtn, taskList, clearDone, search, noteList, editor, newNote, pinNote, delNote });
            FormClosing += (s, e) =>
            {
                if (!SaveCurrent(true)) { e.Cancel = true; return; }
                saveTimer.Stop();
                // Keep one persistent work window. The dock launcher and hotkey
                // simply restore it; Windows shutdown and app exit still close it.
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    owner.SyncWorkDock();
                }
            };
            VisibleChanged += delegate { owner.SyncWorkDock(); };
            Activated += delegate { owner.SyncWorkDock(); };
            Deactivate += delegate { owner.SyncWorkDock(); };
            Resize += delegate
            {
                if (IsHandleCreated && WindowState != FormWindowState.Minimized) Relayout();
                owner.SyncWorkDock();
            };
        }

        public void SelectTab(int i)
        {
            i = Math.Max(0, Math.Min(1, i));
            if (tabs.Selected != i && i == 0 && !SaveCurrent(true)) return;
            tabs.Selected = i;
            ApplyTab();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ShellApp.SetWindowAppId(Handle, "Tepi.Work");
            Relayout();
            savedTasks = CopyTasks(owner.todos);
            RefillTasks(null);
            RefreshNotes(null);
            ApplyTab();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FocusMain();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) saveTimer.Dispose();
            base.Dispose(disposing);
            if (disposing)
                foreach (var f in new[] { fText, fSub, fSmall, fBold }) if (f != null) f.Dispose();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys key)
        {
            if (key == (Keys.Control | Keys.D1)) { SelectTab(0); return true; }
            if (key == (Keys.Control | Keys.D2)) { SelectTab(1); return true; }
            if (key == (Keys.Control | Keys.S)) { SaveCurrent(true); return true; }
            if (key == (Keys.Control | Keys.N)) { if (Tab == 1) NewNote(); else EditTask(null); return true; }
            if (key == (Keys.Control | Keys.F) && Tab == 1) { search.Focus(); search.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, key);
        }

        void FocusMain()
        {
            if (Tab == 0) addBox.Focus(); else if (current != null) editor.Focus(); else search.Focus();
        }

        void ApplyTab()
        {
            bool t = Tab == 0;
            if (!t && current == null && notes.Count > 0) { ShowNote(notes[0].Path); RefreshNotes(current); }
            addBox.Visible = addBtn.Visible = taskList.Visible = newTask.Visible = taskFilter.Visible = t;
            saveState.Visible = !t && !string.IsNullOrEmpty(saveState.Text);
            clearDone.Visible = t && HasDone();
            search.Visible = noteList.Visible = editor.Visible = newNote.Visible = !t;
            pinNote.Visible = delNote.Visible = !t && current != null;
            Relayout();
            Invalidate();
        }

        // ---------- layout ----------

        Rectangle wellAdd, wellList, wellSearch, wellNotes, wellEditor;

        public override void Relayout()
        {
            if (!IsHandleCreated) return;
            float s = Ui;
            BackColor = KitInk.Window;
            // Controls may retain an equivalent Font instance; never dispose a font
            // still assigned to a hidden tab before its native handle is created.
            if (fText == null)
            {
                fText = UiFont(14f); fSub = UiFont(12.5f); fSmall = UiFont(11.5f);
                fBold = new Font(Ink.MenuFamily, 14f * s, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            int pad = (int)(16 * s), W = ClientSize.Width, H = ClientSize.Height;
            var tip = new ToolTip { OwnerDraw = true, BackColor = KitInk.Window, ForeColor = KitInk.Text, UseAnimation = !Anim.Reduced };
            tip.Draw += (o, e) => { e.DrawBackground(); KitInk.Draw(e.Graphics, tip.GetToolTip(e.AssociatedControl), e.Font, KitInk.Text, e.Bounds, ContentAlignment.MiddleLeft); };
            tip.SetToolTip(pinNote, Lang.T("Sematkan catatan ke atas"));
            tip.SetToolTip(delNote, Lang.T("Hapus catatan"));
            Disposed += delegate { tip.Dispose(); };
            tabs.Font = fSub;
            float tw = 0;
            foreach (var it in tabs.Items) tw = Math.Max(tw, TextRenderer.MeasureText(it, fSub).Width);
            tabs.SetBounds(pad, pad, (int)((tw + 34 * s) * tabs.Items.Length), (int)(30 * s));
            int y0 = tabs.Bottom + (int)(10 * s);
            int bottomRow = (int)(30 * s), gap = (int)(8 * s), inset = (int)(6 * s);

            // Tasks
            addBtn.Font = fSub;
            addBtn.Size = new Size(TextRenderer.MeasureText(addBtn.Text, fSub).Width + (int)(32 * s), (int)(40 * s));
            addBtn.Location = new Point(W - pad - addBtn.Width, y0);
            wellAdd = new Rectangle(pad, y0, addBtn.Left - gap - pad, addBtn.Height);
            addBox.Font = fText; addBox.BackColor = KitInk.Group; addBox.ForeColor = KitInk.Text;
            addBox.SetBounds(wellAdd.X + (int)(14 * s), 0, wellAdd.Width - (int)(28 * s), addBox.Height);
            addBox.Top = wellAdd.Y + (wellAdd.Height - addBox.Height) / 2;
            Native.SetCue(addBox.Handle, Lang.T("Tambah tugas"));
            taskFilter.Font = fSub;
            taskFilter.SetBounds(pad, wellAdd.Bottom + gap, (int)(128*s), (int)(30*s));
            int listTop = taskFilter.Bottom + gap;
            wellList = new Rectangle(pad, listTop, W - 2 * pad, H - listTop - pad - bottomRow - gap);
            taskList.BackColor = KitInk.Group; taskList.ForeColor = KitInk.Text; taskList.Font = fText;
            taskList.ItemHeight = (int)(38 * s);
            taskList.SetBounds(wellList.X + inset, wellList.Y + inset, wellList.Width - 2 * inset, wellList.Height - 2 * inset);
            clearDone.Text = Lang.T("Hapus selesai");
            clearDone.Font = fSub;
            clearDone.Size = new Size(TextRenderer.MeasureText(clearDone.Text, fSub).Width + (int)(32 * s), bottomRow);
            clearDone.Location = new Point(pad, H - pad - bottomRow);
            newTask.Font = fSub;
            newTask.Size = new Size(TextRenderer.MeasureText(newTask.Text, fSub).Width + (int)(32 * s), bottomRow);
            newTask.Location = new Point(W - pad - newTask.Width, H - pad - bottomRow);

            // Notes
            int lw = Math.Min((int)(250 * s), W / 3);
            wellSearch = new Rectangle(pad, y0, lw, (int)(36 * s));
            search.Font = fText; search.BackColor = KitInk.Group; search.ForeColor = KitInk.Text;
            search.SetBounds(wellSearch.X + (int)(12 * s), 0, wellSearch.Width - (int)(24 * s), search.Height);
            search.Top = wellSearch.Y + (wellSearch.Height - search.Height) / 2;
            Native.SetCue(search.Handle, Lang.T("Cari catatan"));
            int nTop = wellSearch.Bottom + gap;
            wellNotes = new Rectangle(pad, nTop, lw, H - nTop - pad - bottomRow - gap);
            noteList.BackColor = KitInk.Group; noteList.ForeColor = KitInk.Text; noteList.Font = fText;
            noteList.ItemHeight = (int)(56 * s);
            noteList.SetBounds(wellNotes.X + inset, wellNotes.Y + inset, wellNotes.Width - 2 * inset, wellNotes.Height - 2 * inset);
            int ex = pad + lw + gap + (int)(4 * s);
            wellEditor = new Rectangle(ex, y0, W - pad - ex, H - y0 - pad - bottomRow - gap);
            editor.Font = fText; editor.BackColor = KitInk.Group; editor.ForeColor = KitInk.Text;
            editor.SetBounds(wellEditor.X + (int)(14 * s), wellEditor.Y + (int)(12 * s), wellEditor.Width - (int)(28 * s), Math.Max((int)(40*s), wellEditor.Height - (int)(42*s)));
            newNote.Font = fSub;
            newNote.Size = new Size(TextRenderer.MeasureText(newNote.Text, fSub).Width + (int)(32 * s), bottomRow);
            newNote.Location = new Point(pad, H - pad - bottomRow);
            delNote.Font = fSub; pinNote.Font = fSub;
            delNote.Size = new Size(TextRenderer.MeasureText(delNote.Text, fSub).Width + (int)(32 * s), bottomRow);
            delNote.Location = new Point(W - pad - delNote.Width, H - pad - bottomRow);
            pinNote.Size = new Size(TextRenderer.MeasureText(Lang.T("Lepas sematan"), fSub).Width + (int)(32 * s), bottomRow);
            pinNote.Location = new Point(delNote.Left - gap - pinNote.Width, H - pad - bottomRow);

            saveState.Font = fSmall; saveState.ForeColor = saveFailed ? Color.IndianRed : KitInk.Sub;
            saveState.SetBounds(ex + (int)(14*s), wellEditor.Bottom-(int)(22*s), wellEditor.Width-(int)(28*s), (int)(18*s));
            Invalidate(true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(KitInk.Window);
            KitInk.Smooth(g);
            float s = Ui;
            if (fSub == null) return;
            if (Tab == 0)
            {
                Well(g, wellAdd, 12 * s); Well(g, wellList, 14 * s);
            }
            else
            {
                Well(g, wellSearch, 12 * s); Well(g, wellNotes, 14 * s); Well(g, wellEditor, 14 * s);
            }
        }

        static void Well(Graphics g, Rectangle r, float radius)
        {
            using (var path = Ink.Round(r, radius))
            using (var b = new SolidBrush(KitInk.Group))
                g.FillPath(b, path);
        }

        void PaintEmpty(Graphics g, Control c, string text)
        {
            if (((ListBox)c).Items.Count > 0) return;
            KitInk.Smooth(g);
            KitInk.Draw(g, text, fSub, KitInk.Faint, new RectangleF(0, 0, c.Width, c.Height), ContentAlignment.MiddleCenter);
        }

        // ---------- tasks ----------

        bool HasDone()
        {
            foreach (var t in owner.todos) if (t.Done) return true;
            return false;
        }

        void RefillTasks(string keepId)
        {
            if (keepId == null && taskList.SelectedIndex >= 0 && taskList.SelectedIndex < view.Count) keepId = view[taskList.SelectedIndex].Id;
            int f = taskFilter.Selected;
            view = owner.todos.FindAll(t => f == 1 ? !t.Done :
                f == 2 ? !t.Done && t.Due.HasValue && t.Due.Value.Date == DateTime.Today :
                f == 3 ? !t.Done && t.Due.HasValue && t.Due.Value < DateTime.Now :
                f == 4 ? t.Done : true);
            taskList.EmptyText = owner.todos.Count == 0 ? "Belum ada tugas" : "Tidak ada tugas";
            view.Sort(delegate (TodoItem a, TodoItem b)
            {
                if (a.Done != b.Done) return a.Done ? 1 : -1;
                if (a.Star != b.Star) return a.Star ? -1 : 1;
                if (a.Due.HasValue != b.Due.HasValue) return a.Due.HasValue ? -1 : 1;
                if (a.Due.HasValue && a.Due.Value != b.Due.Value) return a.Due.Value.CompareTo(b.Due.Value);
                int c = a.Created.CompareTo(b.Created);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            taskList.BeginUpdate();
            taskList.Items.Clear();
            int sel = -1;
            for (int i = 0; i < view.Count; i++) { taskList.Items.Add(view[i]); if (view[i].Id == keepId) sel = i; }
            if (sel >= 0) taskList.SelectedIndex = sel;
            taskList.EndUpdate();
            clearDone.Visible = Tab == 0 && HasDone();
            Invalidate();
        }

        static List<TodoItem> CopyTasks(List<TodoItem> items)
        {
            return items.ConvertAll(t => new TodoItem { Id=t.Id, Title=t.Title, Due=t.Due, Star=t.Star, Done=t.Done, Created=t.Created, DoneAt=t.DoneAt });
        }

        void Changed(string keepId)
        {
            if (owner.TodosChanged()) savedTasks = CopyTasks(owner.todos);
            else
            {
                if (savedTasks != null) owner.todos = CopyTasks(savedTasks);
                KitAsk.Notice(this, "Tugas gagal disimpan. Perubahan dibatalkan.");
            }
            RefillTasks(keepId);
        }

        // Bounds of the row buttons; kept one place so paint and hit-testing never drift
        Rectangle RowEdit(int index) { var r = taskList.GetItemRectangle(index); return new Rectangle(r.Right - (int)(78 * Ui), r.Top + (int)(5 * Ui), (int)(30 * Ui), (int)(28 * Ui)); }
        Rectangle RowDelete(int index) { var r = taskList.GetItemRectangle(index); return new Rectangle(r.Right - (int)(44 * Ui), r.Top + (int)(5 * Ui), (int)(30 * Ui), (int)(28 * Ui)); }
        Rectangle RowStar(int index) { var r = taskList.GetItemRectangle(index); return new Rectangle(r.Right - (int)(110 * Ui), r.Top, (int)(32 * Ui), r.Height); }
        Rectangle NoteDelete(int index) { var r = noteList.GetItemRectangle(index); return new Rectangle(r.Right - (int)(34 * Ui), r.Top + (int)(8 * Ui), (int)(26 * Ui), (int)(28 * Ui)); }

        void EditSelectedTask()
        {
            int i = taskList.SelectedIndex;
            if (i >= 0 && i < view.Count) EditTask(view[i]);
        }

        void EditTask(TodoItem item)
        {
            using (var dlg = new TaskEditor(item))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var t = item ?? new TodoItem();
                t.Title = dlg.TaskTitle; t.Due = dlg.Due; t.Star = dlg.Star;
                if (t.Done != dlg.Done) t.DoneAt = dlg.Done ? (DateTime?)DateTime.Now : null;
                t.Done = dlg.Done;
                if (item == null) owner.todos.Add(t);
                Changed(t.Id);
            }
        }

        void DeleteSelectedTask() { DeleteTaskAt(taskList.SelectedIndex); }

        // "Buy milk @besok 09:00": date word or yyyy-MM-dd, then an optional time
        static bool ParseDue(string text, out string title, out DateTime? due)
        {
            title = text.Trim(); due = null;
            text = text.Replace("@hari ini", "@hariini");
            int at = text.LastIndexOf('@');
            if (at < 0 || (at > 0 && !char.IsWhiteSpace(text[at - 1]))) return true;
            DateTime? date = null; TimeSpan? time = null;
            foreach (var raw in text.Substring(at + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = raw.ToLowerInvariant();
                DateTime d; TimeSpan t;
                if (w == "besok" || w == "tomorrow") date = DateTime.Today.AddDays(1);
                else if (w == "hari" || w == "hariini" || w == "today") date = DateTime.Today;
                else if (DateTime.TryParseExact(w, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) date = d.Date;
                else if (TimeSpan.TryParseExact(w.Replace('.', ':'), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out t)) time = t;
                else return true; // not a reminder: keep the text whole
            }
            if (!date.HasValue && !time.HasValue) return true;
            string head = text.Substring(0, at).Trim();
            if (head.Length == 0) return true;
            title = head;
            var day = date ?? DateTime.Today;
            var when = day + (time ?? new TimeSpan(9, 0, 0));
            if (!date.HasValue && when <= DateTime.Now) when = when.AddDays(1);
            due = when;
            return true;
        }

        void AddTask()
        {
            string text = addBox.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) return;
            string title; DateTime? due;
            ParseDue(text, out title, out due);
            var item = new TodoItem { Title = title, Due = due };
            owner.todos.Add(item);
            addBox.Clear();
            Changed(item.Id);
            addBox.Focus();
        }

        void DeleteTaskAt(int index)
        {
            if (index < 0 || index >= view.Count) return;
            if (!KitAsk.Confirm(this, "Hapus tugas ini?")) return;
            owner.todos.Remove(view[index]); Changed(null);
        }

        void ClearDone()
        {
            if (!KitAsk.Confirm(this, "Hapus semua tugas yang selesai?", "Hapus", "Batal")) return;
            owner.todos.RemoveAll(t => t.Done);
            Changed(null);
        }

        void ToggleDone(TodoItem t)
        {
            t.Done = !t.Done;
            t.DoneAt = t.Done ? (DateTime?)DateTime.Now : null;
            Changed(t.Id);
        }

        // 0 = row itself, 1 = edit, 2 = delete, 3 = star
        int Cell(int index, Point p)
        {
            if (index < 0 || index >= view.Count) return 0;
            if (RowEdit(index).Contains(p)) return 1;
            if (RowDelete(index).Contains(p)) return 2;
            if (RowStar(index).Contains(p)) return 3;
            return 0;
        }

        void TaskMouse(object sender, MouseEventArgs e)
        {
            int i = taskList.IndexFromPoint(e.Location);
            if (i < 0 || i >= view.Count) return;
            var t = view[i];
            taskList.SelectedIndex = i;
            if (e.Button == MouseButtons.Right) return;
            int cell = Cell(i, e.Location);
            if (cell == 1) { EditTask(t); return; }
            if (cell == 2) { DeleteTaskAt(i); return; }
            if (cell == 3) { t.Star = !t.Star; Changed(t.Id); return; }
            if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
            // The circle toggles; double-click on the row still opens the editor
            if (e.X < (taskList.GetItemRectangle(i).Left + 38 * Ui)) ToggleDone(t);
        }

        // Small round icon button inside a row
        void RowIcon(Graphics g, Rectangle r, int index, char glyph, bool hot)
        {
            float s = Ui;
            if (hot)
                using (var p = Ink.Round(r, 9 * s))
                using (var b = new SolidBrush(KitInk.Line)) g.FillPath(b, p);
            // Symbol font, same one the rest of Tepi draws its icons with
            var f = Ink.Icons(15 * s);
            KitInk.Draw(g, glyph.ToString(), f, hot ? KitInk.Text : KitInk.Faint, r, ContentAlignment.MiddleCenter);
        }

        // Hover state for the row icons; the list repaints only the row that changed
        int hoverRow = -1, hitCell;

        void TaskHover(object sender, MouseEventArgs e)
        {
            int i = taskList.IndexFromPoint(e.Location);
            int cell = Cell(i, e.Location);
            if (i == hoverRow && cell == hitCell) return;
            int old = hoverRow;
            hoverRow = i; hitCell = cell;
            taskList.Invalidate();
            if (old >= 0 && old < view.Count) taskList.Invalidate(taskList.GetItemRectangle(old));
            if (i >= 0 && i < view.Count) taskList.Invalidate(taskList.GetItemRectangle(i));
            taskList.Cursor = cell == 1 || cell == 2 ? Cursors.Hand : Cursors.Default;
        }

        void TaskKey(object sender, KeyEventArgs e)
        {
            int i = taskList.SelectedIndex;
            if (i < 0 || i >= view.Count) return;
            var t = view[i];
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedTask();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F2 || e.KeyCode == Keys.Enter) { EditTask(t); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Space) { ToggleDone(t); e.SuppressKeyPress = true; }
        }

        string DueText(DateTime d)
        {
            string time = d.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (d.Date == DateTime.Today) return Lang.T("Hari ini") + " " + time;
            if (d.Date == DateTime.Today.AddDays(1)) return Lang.T("Besok") + " " + time;
            return d.ToString("d MMM yyyy", CultureInfo.CurrentCulture) + " " + time;
        }

        void DrawTask(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= view.Count) return;
            var g = e.Graphics; var t = view[e.Index]; float s = Ui;
            KitInk.Smooth(g);
            using (var b = new SolidBrush(KitInk.Group)) g.FillRectangle(b, e.Bounds);
            var row = new RectangleF(e.Bounds.X + 2 * s, e.Bounds.Y + 2 * s, e.Bounds.Width - 4 * s, e.Bounds.Height - 4 * s);
            if ((e.State & DrawItemState.Selected) != 0)
                using (var p = Ink.Round(row, 10 * s)) using (var b = new SolidBrush(KitInk.Hover)) g.FillPath(b, p);
            // Check circle
            float d = 18 * s, cx = row.X + 10 * s, cy = row.Y + (row.Height - d) / 2;
            var circle = new RectangleF(cx, cy, d, d);
            if (t.Done)
            {
                using (var b = new SolidBrush(KitInk.Accent)) g.FillEllipse(b, circle);
                KitInk.Draw(g, "✓", fSmall, KitInk.OnAccent, circle, ContentAlignment.MiddleCenter);
            }
            else using (var pen = new Pen(KitInk.Faint, 1.6f * s)) g.DrawEllipse(pen, circle);
            // Row actions: star, edit, delete, drawn as icons in the row itself
            var starBox = RowStar(e.Index);
            var editBox = RowEdit(e.Index);
            var delBox = RowDelete(e.Index);
            KitInk.Draw(g, t.Star ? "★" : "☆", fText, t.Star ? KitInk.Accent : KitInk.Faint, starBox, ContentAlignment.MiddleCenter);
            RowIcon(g, editBox, e.Index, '\uE70F', hoverRow == e.Index && hitCell == 1);
            RowIcon(g, delBox, e.Index, '\uE74D', hoverRow == e.Index && hitCell == 2);
            float right = starBox.X - 4 * s;
            // Due
            if (t.Due.HasValue)
            {
                bool late = !t.Done && t.Due.Value < DateTime.Now;
                string ds = DueText(t.Due.Value);
                int w = TextRenderer.MeasureText(ds, fSmall).Width + (int)(8 * s);
                KitInk.Draw(g, ds, fSmall, late ? Color.FromArgb(224, 96, 84) : KitInk.Sub, new RectangleF(right - w, row.Y, w, row.Height), ContentAlignment.MiddleRight);
                right -= w + 8 * s;
            }
            // Title
            using (var f = new Font(fText, t.Done ? FontStyle.Strikeout : FontStyle.Regular))
                KitInk.Draw(g, t.Title, f, t.Done ? KitInk.Faint : KitInk.Text, new RectangleF(cx + d + 10 * s, row.Y, Math.Max(10, right - (cx + d + 10 * s)), row.Height), ContentAlignment.MiddleLeft);
        }

        // ---------- notes ----------

        void RefreshNotes(string keepPath)
        {
            if (!IsHandleCreated) return;
            loading = true;
            try
            {
                notes = NoteFiles.List(search.Text, pins);
                noteList.BeginUpdate();
                noteList.Items.Clear();
                int sel = -1;
                for (int i = 0; i < notes.Count; i++) { noteList.Items.Add(notes[i]); if (notes[i].Path == keepPath) sel = i; }
                if (sel >= 0) noteList.SelectedIndex = sel;
                noteList.EndUpdate();
            }
            finally { loading = false; }
            // Filtering the sidebar never clears the current editor.
            Invalidate();
        }

        void NoteMouse(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int i = noteList.IndexFromPoint(e.Location);
            if (i < 0 || i >= notes.Count) return;
            if (!NoteDelete(i).Contains(e.Location)) return;
            DeleteNote(notes[i].Path);
        }

        void OpenSelected()
        {
            int i = noteList.SelectedIndex;
            if (i < 0 || i >= notes.Count) return;
            string path = notes[i].Path;
            if (path == current) return;
            if (!SaveCurrent(true)) { RefreshNotes(current); return; }
            ShowNote(path);
        }

        void ShowNote(string path)
        {
            string text = "";
            if (path != null && !NoteFiles.TryRead(path, out text))
            {
                KitAsk.Notice(this, "Catatan tidak dapat dibaca.");
                RefreshNotes(current); return;
            }
            current = path; baseline = text;
            loading = true;
            editor.Text = text;
            editor.Enabled = path != null;
            SetSaveState("");
            loading = false;
            dirty = false;
            pinNote.Text = path != null && NoteFiles.IsPinned(pins, path) ? Lang.T("Lepas sematan") : Lang.T("Sematkan");
            pinNote.Visible = delNote.Visible = Tab == 1 && path != null;
            Invalidate(true);
        }

        void SetSaveState(string text)
        {
            saveState.Text = text;
            saveState.Visible = !string.IsNullOrEmpty(text) && Tab == 1;
            saveState.ForeColor = saveFailed ? Color.IndianRed : KitInk.Sub;
        }

        bool SaveCurrent() { return SaveCurrent(true); }
        bool SaveCurrent(bool notify)
        {
            if (current == null || !dirty) return true;
            saveTimer.Stop();
            string disk;
            // Compare content, not timestamps: quick-note append and external edits
            // must never be silently overwritten by an old editor buffer.
            if (!NoteFiles.TryRead(current, out disk) || disk != baseline)
            {
                saveFailed = true; SetSaveState("Konflik");
                if (!notify) return false;
                saveTimer.Stop();
                if (!KitAsk.Confirm(this, "Catatan berubah di luar aplikasi. Timpa draf ini?", "Timpa")) return false;
                if (!NoteFiles.Write(current, editor.Text)) return SaveError(notify);
            }
            else if (!NoteFiles.Write(current, editor.Text)) return SaveError(notify);
            saveTimer.Stop();
            baseline = editor.Text; dirty = false; saveFailed = false;
            SetSaveState("");
            RefreshNotes(current);
            return true;
        }

        bool SaveError(bool notify)
        {
            saveFailed = true; SetSaveState("Gagal menyimpan");
            if (notify) KitAsk.Notice(this, "Gagal menyimpan.");
            return false;
        }

        void NewNote()
        {
            if (!SaveCurrent(true)) return;
            string path;
            try { path = NoteFiles.NewPath(); }
            catch (Exception ex) { KitAsk.Notice(this, ex.Message); return; }
            if (!NoteFiles.Write(path, "")) { SaveError(true); return; }
            search.Text = "";
            ShowNote(path);
            RefreshNotes(path);
            editor.Focus();
        }

        void TogglePin()
        {
            if (current == null) return;
            if (!SaveCurrent(true)) return;
            NoteFiles.SetPin(pins, current, !NoteFiles.IsPinned(pins, current));
            RefreshNotes(current);
            ShowNote(current);
        }

        void DeleteNote() { DeleteNote(null); }

        void DeleteNote(string path)
        {
            // A pinned draft is saved first; deleting another row must not drop it
            if (!SaveCurrent(true)) return;
            string target = path ?? current;
            if (target == null) return;
            if (!KitAsk.Confirm(this, "Hapus catatan ini?")) return;
            saveTimer.Stop();
            string deleted = target;
            if (!NoteFiles.Remove(deleted)) { KitAsk.Notice(this, "Catatan gagal dihapus."); return; }
            if (NoteFiles.IsPinned(pins, deleted)) NoteFiles.SetPin(pins, deleted, false);
            bool wasOpen = deleted == current;
            if (wasOpen) { dirty = false; current = null; }
            RefreshNotes(deleted);
            if (wasOpen) { if (notes.Count > 0) ShowNote(notes[0].Path); else ShowNote(null); }
            else if (current != null) ShowNote(current);
        }

        void DrawNote(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= notes.Count) return;
            var g = e.Graphics; var n = notes[e.Index]; float s = Ui;
            KitInk.Smooth(g);
            using (var b = new SolidBrush(KitInk.Group)) g.FillRectangle(b, e.Bounds);
            var row = new RectangleF(e.Bounds.X + 2 * s, e.Bounds.Y + 2 * s, e.Bounds.Width - 4 * s, e.Bounds.Height - 4 * s);
            if ((e.State & DrawItemState.Selected) != 0)
                using (var p = Ink.Round(row, 10 * s)) using (var b = new SolidBrush(KitInk.Hover)) g.FillPath(b, p);
            float x = row.X + 12 * s, w = row.Width - 24 * s;
            if (n.Pin)
            {
                using (var b = new SolidBrush(KitInk.Accent)) g.FillEllipse(b, row.Right - 18 * s, row.Y + 12 * s, 7 * s, 7 * s);
                w -= 14 * s;
            }
            KitInk.Draw(g, n.Title, fBold, KitInk.Text, new RectangleF(x, row.Y + 5 * s, w, 22 * s), ContentAlignment.MiddleLeft);
            string sub = n.Time.ToString("d MMM", CultureInfo.CurrentCulture) + (n.Preview.Length > 0 ? "  ·  " + n.Preview : "");
            KitInk.Draw(g, sub, fSmall, KitInk.Sub, new RectangleF(x, row.Y + 28 * s, w, 20 * s), ContentAlignment.MiddleLeft);
            RowIcon(g, NoteDelete(e.Index), e.Index, '\uE74D', false);
        }
    }
}
