using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Tepi
{
    // Rapikan Downloads: loose files into folders, undoable
    partial class Island
    {
        FileSystemWatcher tidyWatch;
        System.Threading.Timer tidyLater;
        int tidyNudged;
        DateTime tidyNudgedUtc = DateTime.MinValue;
        volatile bool tidyBusy;

        bool TidyOn { get { return settings.GetBool("tidy", false); } }
        static string TidyUndoPath { get { return Path.Combine(Settings.Dir, "rapikan.txt"); } }
        const int TidyUndoMin = 60;

        static readonly string[] Partial = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2", ".lnk", ".url" };

        // Extension groups, names translated when moving
        static readonly string[][] TidyKinds =
        {
            new[] { "Dokumen", ".pdf .doc .docx .odt .rtf .txt .md .xls .xlsx .csv .ods .ppt .pptx .odp .epub" },
            new[] { "Gambar", ".png .jpg .jpeg .jfif .gif .webp .bmp .heic .svg .tif .tiff .ico .avif" },
            new[] { "Video", ".mp4 .mkv .mov .avi .webm .m4v .wmv .flv .3gp" },
            new[] { "Audio", ".mp3 .wav .m4a .ogg .opus .flac .aac .wma" },
            new[] { "Arsip", ".zip .rar .7z .tar .gz .tgz .bz2 .xz .iso" },
            new[] { "Installer", ".exe .msi .msix .appx .apk .dmg" },
            new[] { "Kode", ".py .js .ts .json .html .htm .css .java .c .cpp .cs .php .go .rs .sql .sh .ps1 .bat .ipynb .xml .yml .yaml" },
        };

        // Test runs never touch the real Downloads
        static string TidyDir() { return Settings.TestMode ? Path.Combine(Settings.Dir, "Downloads") : DownloadsFolder(); }

        static string TidyKind(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext.Length > 0)
                foreach (var k in TidyKinds) if ((" " + k[1] + " ").Contains(" " + ext + " ")) return Lang.T(k[0]);
            return Lang.T("Lainnya");
        }

        void ApplyTidy()
        {
            string dir = TidyDir();
            bool want = TidyOn && dir != null && Directory.Exists(dir);
            if (!want)
            {
                if (tidyWatch != null) { tidyWatch.Dispose(); tidyWatch = null; }
                return;
            }
            if (tidyWatch != null) return;
            try
            {
                // Top level only; folders are left alone
                tidyWatch = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                FileSystemEventHandler h = delegate { TidySoon(); };
                tidyWatch.Created += h;
                tidyWatch.Renamed += (s, e) => TidySoon();
                tidyWatch.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("rapikan pantau: " + ex.Message); tidyWatch = null; }
        }

        // Debounced: downloads land in bursts
        void TidySoon()
        {
            if (tidyLater == null) tidyLater = new System.Threading.Timer(delegate { Post(TidyNudge); });
            tidyLater.Change(20000, Timeout.Infinite);
        }

        // Ask once per batch, not per file
        void TidyNudge()
        {
            int every = settings.GetInt("tidy.nudge", 15);
            if (!TidyOn || every <= 0 || tidyBusy) return;
            string dir = TidyDir();
            if (dir == null) return;
            int n = Loose(dir).Count;
            bool fresh = n >= every && (n >= tidyNudged + every || (DateTime.UtcNow - tidyNudgedUtc).TotalHours >= 24);
            if (!fresh) return;
            tidyNudged = n;
            tidyNudgedUtc = DateTime.UtcNow;
            var t = ShowToast(View.Info, 10, 2, true);
            if (t == null) return;
            t.Glyph = '\uE8B7';
            t.Title = Lang.F("{0} berkas lepas di Downloads", n);
            t.Detail = Lang.T("Klik untuk merapikan");
            t.Data = new Action(delegate { RunTidy(); });
            Kick();
        }

        // Settled, unlocked, not a partial download
        static List<string> Loose(string dir)
        {
            var list = new List<string>();
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles())
                {
                    if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    string ext = f.Extension.ToLowerInvariant();
                    if (Array.IndexOf(Partial, ext) >= 0 || f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 15) continue;
                    // Firefox keeps an empty twin of a .part
                    if (f.Length == 0 || File.Exists(f.FullName + ".part")) continue;
                    list.Add(f.FullName);
                }
            }
            catch (Exception ex) { Log.Write("rapikan baca: " + ex.Message); }
            return list;
        }

        static bool Free(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true; }
            catch (Exception) { return false; }
        }

        // Never overwrites: "name (1).ext"
        static string FreeName(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 1; i < 1000; i++)
            {
                path = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
            return null;
        }

        void RunTidy()
        {
            if (tidyBusy) return;
            string dir = TidyDir();
            if (dir == null) return;
            tidyBusy = true;
            bool byYear = settings.Get("tidy.layout", "type") == "year";
            // Shelf keeps paths: leave those
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in shelf) held.Add(it.Path);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var moves = new List<KeyValuePair<string, string>>();
                var made = new List<string>();
                int skipped = 0;
                foreach (var f in Loose(dir))
                {
                    if (held.Contains(f) || !Free(f)) { skipped++; continue; }
                    try
                    {
                        string sub = TidyKind(f);
                        if (byYear)
                        {
                            string year = Path.Combine(dir, File.GetLastWriteTime(f).Year.ToString());
                            if (!Directory.Exists(year)) { Directory.CreateDirectory(year); made.Add(year); }
                            sub = Path.Combine(year, sub);
                        }
                        string target = Path.Combine(dir, sub);
                        if (!Directory.Exists(target)) { Directory.CreateDirectory(target); made.Add(target); }
                        string to = FreeName(target, Path.GetFileName(f));
                        if (to == null) { skipped++; continue; }
                        File.Move(f, to);
                        moves.Add(new KeyValuePair<string, string>(f, to));
                    }
                    catch (Exception ex) { skipped++; Log.Write("rapikan pindah: " + ex.Message); }
                }
                SaveTidyUndo(moves, made);
                Post(delegate
                {
                    tidyBusy = false;
                    tidyNudged = 0;
                    var t = ShowToast(View.Info, 4, 3, true, true);
                    if (t == null) return;
                    t.Glyph = '\uE8B7';
                    t.Title = moves.Count == 0 ? Lang.T("Downloads sudah rapi") : Lang.F("{0} berkas dirapikan", moves.Count);
                    t.Detail = moves.Count == 0 ? (skipped > 0 ? Lang.F("{0} berkas sedang dipakai", skipped) : "") : Lang.T("Klik untuk mengurungkan");
                    if (moves.Count > 0) t.Data = new Action(delegate { UndoTidy(); });
                    Kick();
                });
            });
        }

        // Lines: from, to; or dir, path
        static void SaveTidyUndo(List<KeyValuePair<string, string>> moves, List<string> made)
        {
            if (moves.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.Ticks).Append('\n');
            foreach (var m in moves) sb.Append(m.Key).Append('\t').Append(m.Value).Append('\n');
            foreach (var d in made) sb.Append("dir\t").Append(d).Append('\n');
            try { AtomicFile.Write(TidyUndoPath, Encoding.UTF8.GetBytes(sb.ToString())); }
            catch (Exception ex) { Log.Write("rapikan simpan: " + ex.Message); }
        }

        // Minutes left to undo, 0 when gone
        public int TidyUndoLeft()
        {
            try
            {
                if (!File.Exists(TidyUndoPath)) return 0;
                string first = File.ReadAllLines(TidyUndoPath)[0];
                var at = new DateTime(long.Parse(first), DateTimeKind.Utc);
                return Math.Max(0, TidyUndoMin - (int)(DateTime.UtcNow - at).TotalMinutes);
            }
            catch (Exception) { return 0; }
        }

        public void UndoTidy()
        {
            if (tidyBusy || TidyUndoLeft() <= 0) return;
            tidyBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int back = 0, missing = 0;
                try
                {
                    var lines = File.ReadAllLines(TidyUndoPath);
                    var dirs = new List<string>();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var parts = lines[i].Split('\t');
                        if (parts.Length != 2) continue;
                        if (parts[0] == "dir") { dirs.Add(parts[1]); continue; }
                        if (!File.Exists(parts[1])) { missing++; continue; }
                        string to = FreeName(Path.GetDirectoryName(parts[0]), Path.GetFileName(parts[0]));
                        if (to == null) { missing++; continue; }
                        try { File.Move(parts[1], to); back++; }
                        catch (Exception ex) { missing++; Log.Write("rapikan urung: " + ex.Message); }
                    }
                    // Only empty folders this run made
                    dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
                    foreach (var d in dirs)
                        try { if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch (Exception) { }
                    File.Delete(TidyUndoPath);
                }
                catch (Exception ex) { Log.Write("rapikan urung: " + ex.Message); }
                Post(delegate
                {
                    tidyBusy = false;
                    // Restored files don't count as new
                    string here = TidyDir();
                    tidyNudged = here == null ? 0 : Loose(here).Count;
                    tidyNudgedUtc = DateTime.UtcNow;
                    ShowInfo('\uE7A7', Lang.F("{0} berkas dikembalikan", back), missing > 0 ? Lang.F("{0} sudah dipindah atau dihapus", missing) : "", true);
                });
            });
        }
    }
}
