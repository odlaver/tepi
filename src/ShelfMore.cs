using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // Rak extras: pick many, paste, expiry, keep
    partial class Island
    {
        readonly HashSet<long> shelfPicked = new HashSet<long>();
        long shelfAnchor;
        // Clipboard holds files or a picture
        bool shelfPaste;

        static string ShelfFile { get { return Path.Combine(Settings.Dir, "shelf.txt"); } }

        // Ctrl toggles, Shift spans, plain opens
        void ShelfClick(ShelfItem item)
        {
            var keys = Control.ModifierKeys;
            if ((keys & Keys.Control) != 0)
            {
                if (!shelfPicked.Remove(item.Id)) shelfPicked.Add(item.Id);
                shelfAnchor = item.Id;
            }
            else if ((keys & Keys.Shift) != 0)
            {
                int a = shelf.FindIndex(s => s.Id == shelfAnchor), b = shelf.IndexOf(item);
                if (a < 0) a = b;
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) shelfPicked.Add(shelf[i].Id);
            }
            else
            {
                shelfPicked.Clear();
                OpenShelfItem(item);
            }
            Kick();
        }

        // Dragging a picked item takes all picked
        string[] ShelfDragPaths(ShelfItem item)
        {
            if (!shelfPicked.Contains(item.Id) || shelfPicked.Count < 2) return new[] { item.Path };
            var list = new List<string>();
            foreach (var s in shelf) if (shelfPicked.Contains(s.Id) && (File.Exists(s.Path) || Directory.Exists(s.Path))) list.Add(s.Path);
            return list.ToArray();
        }

        void RemovePicked()
        {
            var gone = shelf.FindAll(s => shelfPicked.Contains(s.Id));
            var cells = new List<RectangleF>();
            foreach (var s in gone) cells.Add(ShelfCellRect(s));
            Vanish(cells.ToArray(), false);
            foreach (var s in gone) RemoveFromShelf(s);
            shelfPicked.Clear();
        }

        // Read on Rak open and clipboard change only
        void ShelfClipCheck()
        {
            bool had = shelfPaste;
            try { shelfPaste = Clipboard.ContainsFileDropList() || Clipboard.ContainsImage(); }
            catch (Exception) { shelfPaste = false; }
            if (had != shelfPaste) Kick();
        }

        void PasteToShelf()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = new List<string>();
                    foreach (var f in Clipboard.GetFileDropList()) files.Add(f);
                    AddToShelf(files);
                }
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    {
                        if (img == null) return;
                        string dir = ShotsTemp();
                        Directory.CreateDirectory(dir);
                        string path = Path.Combine(dir, "Tepi " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss.fff") + ".png");
                        img.Save(path, ImageFormat.Png);
                        AddToShelf(new[] { path });
                    }
                }
            }
            catch (Exception ex) { Log.Write("shelf paste: " + ex.Message); }
        }

        // Each minute; true when something left
        bool ShelfExpireTick()
        {
            int hours = settings.GetInt("shelf.expire", 0);
            if (hours <= 0 || shelf.Count == 0) return false;
            var cut = DateTime.UtcNow.AddHours(-hours);
            var old = shelf.FindAll(s => s.AddedUtc < cut);
            foreach (var s in old) { shelfPicked.Remove(s.Id); RemoveFromShelf(s); }
            return old.Count > 0;
        }

        // shelf.keep: ticks and path per line
        void SaveShelf()
        {
            try
            {
                if (!settings.GetBool("shelf.keep", false))
                {
                    if (File.Exists(ShelfFile)) File.Delete(ShelfFile);
                    return;
                }
                var sb = new StringBuilder();
                foreach (var s in shelf) sb.Append(s.AddedUtc.Ticks).Append('\t').Append(s.Path).Append("\r\n");
                AtomicFile.Write(ShelfFile, new UTF8Encoding(false).GetBytes(sb.ToString()));
            }
            catch (Exception ex) { Log.Write("shelf save: " + ex.Message); }
        }

        // Same checks as a drop; gone files skipped
        void LoadShelf()
        {
            if (!settings.GetBool("shelf.keep", false) || !File.Exists(ShelfFile)) return;
            try
            {
                foreach (var line in File.ReadAllLines(ShelfFile, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    long ticks;
                    if (tab <= 0 || !long.TryParse(line.Substring(0, tab), out ticks) || shelf.Count >= ShelfMax) continue;
                    string path = line.Substring(tab + 1);
                    if (!ShelfSafe(path) || shelf.Exists(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                    var item = new ShelfItem { Path = path, Name = Path.GetFileName(path.TrimEnd('\\')), Id = ++shelfIds, AddedUtc = new DateTime(ticks, DateTimeKind.Utc) };
                    shelf.Add(item);
                    LoadThumb(item);
                }
            }
            catch (Exception ex) { Log.Write("shelf load: " + ex.Message); }
        }
    }
}
