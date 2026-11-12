using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace Tepi
{
    // One task in the work window
    sealed class TodoItem
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Title = "";
        public bool Done, Star;
        public DateTime? Due;
        public DateTime Created = DateTime.Now;
        public DateTime? DoneAt;
        public override string ToString() { return Title; }
    }

    // tugas.tsv: id, done, due, star, created, doneAt, title; plain text on purpose
    static class TodoFile
    {
        const string Stamp = "yyyy-MM-ddTHH:mm";
        static string FilePath { get { return Path.Combine(Settings.Dir, "tugas.tsv"); } }

        static DateTime? Parse(string s)
        {
            DateTime d;
            return s.Length > 0 && DateTime.TryParseExact(s, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? (DateTime?)d : null;
        }

        static string Put(DateTime? d) { return d.HasValue ? d.Value.ToString(Stamp, CultureInfo.InvariantCulture) : ""; }

        public static List<TodoItem> Load()
        {
            var list = new List<TodoItem>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = line.Split('\t');
                    if (f.Length < 7 || f[6].Trim().Length == 0) continue;
                    var t = new TodoItem { Id = f[0], Done = f[1] == "1", Due = Parse(f[2]), Star = f[3] == "1", Title = f[6] };
                    var made = Parse(f[4]);
                    if (made.HasValue) t.Created = made.Value;
                    t.DoneAt = Parse(f[5]);
                    list.Add(t);
                }
            }
            catch (Exception ex) { Log.Write("tugas: " + ex.Message); }
            return list;
        }

        public static bool Save(List<TodoItem> items)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var t in items)
                    sb.Append(t.Id).Append('\t').Append(t.Done ? '1' : '0').Append('\t').Append(Put(t.Due)).Append('\t').Append(t.Star ? '1' : '0').Append('\t')
                      .Append(Put(t.Created)).Append('\t').Append(Put(t.DoneAt)).Append('\t')
                      .Append(t.Title.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
                Directory.CreateDirectory(Settings.Dir);
                AtomicFile.Write(FilePath, new UTF8Encoding(false).GetBytes(sb.ToString()));
                return true;
            }
            catch (Exception ex) { Log.Write("simpan tugas: " + ex.Message); return false; }
        }
    }

    sealed class NoteInfo
    {
        public string Path = "", Title = "", Preview = "";
        public DateTime Time;
        public bool Pin, Quick;
        public override string ToString() { return Title; }
    }

    // One Markdown file per note; the quick-note log (catatan.md) is listed too
    static class NoteFiles
    {
        public static string Dir { get { return System.IO.Path.Combine(Settings.Dir, "catatan"); } }
        public static string QuickPath { get { return System.IO.Path.Combine(Settings.Dir, "catatan.md"); } }
        static string PinPath { get { return System.IO.Path.Combine(Settings.Dir, "catatan-sematan.txt"); } }
        public const string QuickKey = ":kilat";

        static string KeyOf(string path) { return path == QuickPath ? QuickKey : System.IO.Path.GetFileName(path); }

        public static HashSet<string> LoadPins()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(PinPath)) foreach (var l in File.ReadAllLines(PinPath, Encoding.UTF8)) if (l.Trim().Length > 0) set.Add(l.Trim()); }
            catch (Exception ex) { Log.Write("sematan catatan: " + ex.Message); }
            return set;
        }

        public static void SavePins(HashSet<string> pins)
        {
            try { Directory.CreateDirectory(Settings.Dir); AtomicFile.Write(PinPath, new UTF8Encoding(false).GetBytes(string.Join("\n", new List<string>(pins).ToArray()))); }
            catch (Exception ex) { Log.Write("simpan sematan: " + ex.Message); }
        }

        public static bool IsPinned(HashSet<string> pins, string path) { return pins.Contains(KeyOf(path)); }

        public static void SetPin(HashSet<string> pins, string path, bool on)
        {
            if (on) pins.Add(KeyOf(path)); else pins.Remove(KeyOf(path));
            SavePins(pins);
        }

        public static string NewPath()
        {
            Directory.CreateDirectory(Dir);
            string stem = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), p = System.IO.Path.Combine(Dir, stem + ".md");
            for (int i = 2; File.Exists(p); i++) p = System.IO.Path.Combine(Dir, stem + "-" + i + ".md");
            return p;
        }

        public static bool TryRead(string path, out string text)
        {
            text = null;
            try { text = File.ReadAllText(path, Encoding.UTF8); return true; }
            catch (Exception ex) { Log.Write("baca catatan: " + ex.Message); return false; }
        }
        public static string Read(string path)
        {
            string text;
            return TryRead(path, out text) ? text : "";
        }

        public static bool Write(string path, string text)
        {
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)); AtomicFile.Write(path, new UTF8Encoding(false).GetBytes(text)); return true; }
            catch (Exception ex) { Log.Write("simpan catatan: " + ex.Message); return false; }
        }

        public static bool Remove(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                return !File.Exists(path);
            }
            catch (Exception ex) { Log.Write("hapus catatan: " + ex.Message); return false; }
        }

        static string Head(string path, int max)
        {
            using (var r = new StreamReader(path, Encoding.UTF8))
            {
                var buf = new char[max];
                int n = r.Read(buf, 0, max);
                return new string(buf, 0, Math.Max(0, n));
            }
        }

        // Title: first non-empty line without heading marks; preview: the next line
        static void Describe(string text, NoteInfo n)
        {
            string title = "", preview = "";
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.Trim().TrimStart('#', ' ', '-', '*').Trim();
                if (l.Length == 0) continue;
                if (title.Length == 0) title = l; else { preview = l; break; }
            }
            n.Title = title.Length > 0 ? title : Lang.T("Catatan kosong");
            n.Preview = preview;
        }

        public static List<NoteInfo> List(string query, HashSet<string> pins)
        {
            var list = new List<NoteInfo>();
            query = (query ?? "").Trim();
            try
            {
                var paths = new List<string>();
                if (Directory.Exists(Dir)) paths.AddRange(Directory.GetFiles(Dir, "*.md"));
                if (File.Exists(QuickPath)) paths.Add(QuickPath);
                foreach (var p in paths)
                {
                    var n = new NoteInfo { Path = p, Quick = p == QuickPath, Time = File.GetLastWriteTime(p) };
                    n.Pin = pins.Contains(KeyOf(p));
                    if (n.Quick) { n.Title = Lang.T("Catatan kilat"); n.Preview = ""; }
                    else Describe(Head(p, 600), n);
                    if (query.Length > 0)
                    {
                        bool hit = n.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || n.Preview.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   Read(p).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!hit) continue;
                    }
                    list.Add(n);
                }
            }
            catch (Exception ex) { Log.Write("daftar catatan: " + ex.Message); }
            list.Sort((a, b) => a.Pin != b.Pin ? (a.Pin ? -1 : 1) : b.Time.CompareTo(a.Time));
            return list;
        }
    }

    // Tasks and notes in one window
    partial class Island
    {
        const int WorkHotkey = 40;
        const string TodoTag = "Tugas: ";
        WorkWindow workWin;
        internal List<TodoItem> todos;

        internal IntPtr WorkHwnd { get { return workWin != null && !workWin.IsDisposed && workWin.IsHandleCreated ? workWin.Handle : IntPtr.Zero; } }
        internal bool WorkOpen { get { return workWin != null && !workWin.IsDisposed && workWin.Visible; } }
        internal void SyncWorkDock() { if (dockWin != null && !dockWin.IsDisposed && dockWin.IsHandleCreated) dockWin.SyncWork(); }

        void OpenWork() { OpenWork(workWin != null && !workWin.IsDisposed ? workWin.CurrentTab : 0); }

        void OpenWork(int tab)
        {
            if (todos == null) todos = TodoFile.Load();
            if (workWin == null || workWin.IsDisposed)
            {
                workWin = new WorkWindow(this);
                workWin.FormClosed += delegate { workWin = null; SyncWorkDock(); };
                workWin.Show();
            }
            else
            {
                // Pinned behaves like a real window: the dock button or hotkey brings it
                // forward, the X hides it, and nothing is rebuilt on reopening
                if (workWin.WindowState == FormWindowState.Minimized) workWin.WindowState = FormWindowState.Normal;
                if (!workWin.Visible) workWin.Show();
            }
            workWin.SelectTab(tab);
            ShellApp.Activate(workWin.Handle);
            workWin.FocusInput();
            SyncWorkDock();
        }

        // After any edit: save, then keep the due reminders in step
        internal bool TodosChanged()
        {
            var previous = TodoFile.Load();
            if (!TodoFile.Save(todos)) return false;
            // Only replace reminders matching a previously persisted task and due time.
            // A broad prefix match would remove unrelated user reminders.
            foreach (var old in previous)
                if (!old.Done && old.Due.HasValue)
                {
                    var reminder = oneShots.Find(o => o.At == old.Due.Value && o.Text == TodoTag + old.Title);
                    if (reminder != null) oneShots.Remove(reminder);
                }
            var now = DateTime.Now;
            foreach (var t in todos)
                if (!t.Done && t.Due.HasValue && t.Due.Value > now) oneShots.Add(new OneShot { At = t.Due.Value, Text = TodoTag + t.Title });
            oneShots.Sort((a, b) => a.At.CompareTo(b.At));
            SaveOneShots();
            Kick();
            return true;
        }
    }
}
