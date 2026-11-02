using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Shelf file: path only, never contents
    class ShelfItem
    {
        public string Path = "", Name = "";
        public Bitmap Thumb;
        public long Id;
        public DateTime AddedUtc;
    }

    // Rak: drop in, drag out; memory only
    partial class Island
    {
        readonly List<ShelfItem> shelf = new List<ShelfItem>();
        readonly Spring shelfScroll = new Spring(0, 300, 34, 0.2f);
        const int ShelfMax = 20;
        const float ShelfCellW = 104, ShelfCellH = 100, ShelfThumbPx = 56;
        long shelfIds;
        bool dropActive;
        // Drop target: 0 Rak, 1 Ubah
        int dropTarget;
        DateTime dropUtc = DateTime.MinValue, droppedUtc = DateTime.MinValue;
        int droppedCount;

        bool ShelfOn { get { return settings.GetBool("shelf", true); } }

        // UNC and link icons can leak credentials
        static readonly string[] RiskyExt = { ".lnk", ".url", ".scf", ".library-ms", ".searchconnector-ms" };

        static bool ShelfSafe(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\") || path.StartsWith("//")) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(RiskyExt, ext) >= 0) return false;
            try { return File.Exists(path) || Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        void AddToShelf(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (var raw in paths)
            {
                if (!ShelfSafe(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); } catch (Exception) { continue; }
                var old = shelf.Find(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
                if (old != null) shelf.Remove(old);
                else old = new ShelfItem { Path = full, Name = Path.GetFileName(full.TrimEnd('\\')), Id = ++shelfIds };
                old.AddedUtc = DateTime.UtcNow;
                shelf.Insert(0, old);
                if (old.Thumb == null) LoadThumb(old);
                added++;
            }
            while (shelf.Count > ShelfMax)
            {
                var last = shelf[shelf.Count - 1];
                if (last.Thumb != null) last.Thumb.Dispose();
                shelf.RemoveAt(shelf.Count - 1);
            }
            droppedCount = added;
            droppedUtc = DateTime.UtcNow;
            SaveShelf();
            Kick();
        }

        void RemoveFromShelf(ShelfItem item)
        {
            int at = shelf.IndexOf(item);
            if (at < 0) return;
            shelf.RemoveAt(at);
            shelfPicked.Remove(item.Id);
            if (item.Thumb != null) item.Thumb.Dispose();
            SaveShelf();
            Kick();
        }

        void ClearShelf()
        {
            foreach (var s in shelf) if (s.Thumb != null) s.Thumb.Dispose();
            shelf.Clear();
            shelfPicked.Clear();
            SaveShelf();
            Kick();
        }

        // Drop in: pill grows into a target

        void InitShelfDrop()
        {
            AllowDrop = ShelfOn || ConvertSetting;
        }

        bool DropSplit { get { return ShelfOn && ConvertSetting; } }

        int DropTargetAt(int screenX, int screenY)
        {
            if (!DropSplit) return ShelfOn ? 0 : 1;
            var c = PointToClient(new Point(screenX, screenY));
            return c.X / scale < shell.X + shell.Width / 2 ? 0 : 1;
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if ((!ShelfOn && !ConvertSetting) || !e.Data.GetDataPresent(DataFormats.FileDrop) || PrompterOn) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Copy;
            dropTarget = DropTargetAt(e.X, e.Y);
            if (!dropActive) { dropActive = true; dropUtc = DateTime.UtcNow; if (!Anim.Reduced) topGap.Velocity += 60; Kick(); }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = dropActive ? DragDropEffects.Copy : DragDropEffects.None;
            int at = DropTargetAt(e.X, e.Y);
            if (dropActive && at != dropTarget) { dropTarget = at; Kick(); }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            dropActive = false;
            Kick();
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            dropActive = false;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && DropTargetAt(e.X, e.Y) == 1) OpenConvert(files);
            else if (files != null && files.Length > 0)
            {
                AddToShelf(files);
                // Straight to Rak, Berkas segment
                SetSegment(PageShelf, 0, true);
                Show(PageShelf);
            }
            Kick();
        }

        // Drag out: copy or link, never move

        ShelfItem dragItem;
        Point dragFrom;
        bool draggingOut;

        void ShelfMouseDown(ShelfItem item) { dragItem = item; dragFrom = Control.MousePosition; }

        // Mouse move with left button held
        bool ShelfDragCheck()
        {
            if (dragItem == null || draggingOut || (Control.MouseButtons & MouseButtons.Left) == 0) return false;
            var at = Control.MousePosition;
            var size = SystemInformation.DragSize;
            if (Math.Abs(at.X - dragFrom.X) < size.Width && Math.Abs(at.Y - dragFrom.Y) < size.Height) return false;
            var item = dragItem;
            dragItem = null;
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) { RemoveFromShelf(item); return true; }
            draggingOut = true;
            pressedId = null;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, ShelfDragPaths(item));
                DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Link);
            }
            catch (Exception ex) { Log.Write("shelf drag: " + ex.Message); }
            finally { draggingOut = false; }
            Kick();
            return true;
        }

        void OpenShelfItem(ShelfItem item)
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("shelf open: " + ex.Message); ShowInfo('', Lang.T("Berkas tidak bisa dibuka"), item.Name, true); }
        }

        void RevealShelfItem(ShelfItem item)
        {
            try { Process.Start("explorer.exe", "/select,\"" + item.Path + "\""); }
            catch (Exception ex) { Log.Write("shelf reveal: " + ex.Message); }
        }

        void ShelfMenu(ShelfItem item)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(Lang.T("Buka"), null, delegate { OpenShelfItem(item); });
            m.Items.Add(Lang.T("Tampilkan di folder"), null, delegate { RevealShelfItem(item); });
            m.Items.Add(Lang.T("Salin jalur"), null, delegate { try { Clipboard.SetText(item.Path); } catch (Exception) { } });
            string ext = Path.GetExtension(item.Path).ToLowerInvariant();
            if (OcrShown && (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff"))
                m.Items.Add(Lang.T("Salin teks"), null, delegate { CopyText(item.Path); });
            if (PhonePaired && File.Exists(item.Path)) m.Items.Add(Lang.T("Kirim ke HP"), null, delegate { SendToPhone(item.Path); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Lang.T("Keluarkan dari rak"), null, delegate { Vanish(new[] { ShelfCellRect(item) }, false); RemoveFromShelf(item); });
            if (shelfPicked.Count > 1 && shelfPicked.Contains(item.Id)) m.Items.Add(Lang.F("Keluarkan {0} yang dipilih", shelfPicked.Count), null, delegate { RemovePicked(); });
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(Control.MousePosition);
        }

        // Page

        int ShelfCols(float w) { return Math.Max(3, (int)((w - 28) / ShelfCellW)); }

        float ShelfContentH()
        {
            int cols = ShelfCols(PanelW);
            return (shelf.Count + ShelfLead + cols - 1) / cols * ShelfCellH;
        }

        // Add cell, plus Paste while the clipboard has some
        int ShelfLead { get { return shelfPaste ? 2 : 1; } }

        float ShelfHeight()
        {
            if (shelf.Count == 0) return 252;
            return Clamp(PanelTop + 12 + ShelfContentH(), 170, MaxH - SegTop);
        }

        // Last drawn cell per item, for effects
        readonly Dictionary<long, RectangleF> shelfCells = new Dictionary<long, RectangleF>();

        RectangleF ShelfCellRect(ShelfItem item)
        {
            RectangleF r;
            return shelfCells.TryGetValue(item.Id, out r) ? r : RectangleF.Empty;
        }

        void DrawShelfPage(float w, float h, DateTime now)
        {
            if (!ShelfOn)
            {
                float by = Empty(w, '', Lang.T("Rak mati"), Lang.T("Berkas yang diseret ke island tidak ditahan"));
                Button(new RectangleF(w / 2 - 74, by, 148, 34), "shelfOn", Lang.T("Nyalakan"), true, delegate { SetFlag("shelf", true); });
                return;
            }
            if (shelf.Count == 0)
            {
                float by = Empty(w, '', Lang.T("Rak kosong"), Lang.T("Seret berkas ke island, atau pilih dari folder"));
                if (shelfPaste)
                {
                    Button(new RectangleF(w / 2 - 148, by, 140, 34), "shelfPick", Lang.T("Pilih berkas"), true, PickShelfFiles);
                    Button(new RectangleF(w / 2 + 8, by, 140, 34), "shelfPasteBtn", Lang.T("Tempel"), false, PasteToShelf);
                }
                else Button(new RectangleF(w / 2 - 74, by, 148, 34), "shelfPick", Lang.T("Pilih berkas"), true, PickShelfFiles);
                return;
            }
            int cols = ShelfCols(w);
            float gridW = cols * ShelfCellW, x0 = w / 2 - gridW / 2;
            var area = new RectangleF(10, PanelTop + 4, w - 20, h - PanelTop - 10);
            var st = g.Save();
            g.IntersectClip(area);
            shelfCells.Clear();
            int lead = ShelfLead;
            for (int i = 0; i < shelf.Count + lead; i++)
            {
                float dy, a = RowIn(i / cols, now, out dy);
                var cell = new RectangleF(x0 + (i % cols) * ShelfCellW, area.Y + (i / cols) * ShelfCellH - shelfScroll.Value + dy, ShelfCellW, ShelfCellH);
                if (cell.Bottom < area.Top || cell.Y > area.Bottom) continue;
                float keep = p.Alpha;
                p.Alpha = keep * a;
                if (i == 0) DrawShelfAdd(cell);
                else if (i < lead) DrawShelfPaste(cell);
                else
                {
                    var item = shelf[i - lead];
                    shelfCells[item.Id] = new RectangleF(cell.X + originX, cell.Y + originY + hitDy, cell.Width, cell.Height);
                    DrawShelfCell(item, cell);
                }
                p.Alpha = keep;
            }
            g.Restore(st);
        }

        // First cell: add from a picker
        void DrawShelfAdd(RectangleF cell)
        {
            var hit = RectangleF.Inflate(cell, -4, -4);
            AddHit(hit, "shelfAdd", PickShelfFiles);
            bool hover = Hover("shelfAdd");
            if (hover) p.FillRound(hit, 16, Pressed("shelfAdd") ? p.T.FillHover : p.T.Fill);
            var box = new RectangleF(cell.X + cell.Width / 2 - ShelfThumbPx / 2, cell.Y + 10, ShelfThumbPx, ShelfThumbPx);
            using (var pen = new Pen(Ink.A(p.T.Text, (hover ? 0.6f : 0.35f) * p.Alpha), 1.4f) { DashPattern = new[] { 4f, 3f } })
            using (var path = Ink.Round(box, 12)) g.DrawPath(pen, path);
            float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            var plus = p.P(hover ? p.T.Text : p.T.Sub, 1.8f);
            g.DrawLine(plus, cx - 8, cy, cx + 8, cy);
            g.DrawLine(plus, cx, cy - 8, cx, cy + 8);
            p.Text(Lang.T("Tambah"), Ink.Small(11f), hover ? p.T.Text : p.T.Sub, cell.X + cell.Width / 2, cell.Y + 82, cell.Width - 12, StringAlignment.Center);
        }

        // Second cell while the clipboard has files or a picture
        void DrawShelfPaste(RectangleF cell)
        {
            var hit = RectangleF.Inflate(cell, -4, -4);
            AddHit(hit, "shelfPaste", PasteToShelf);
            bool hover = Hover("shelfPaste");
            if (hover) p.FillRound(hit, 16, Pressed("shelfPaste") ? p.T.FillHover : p.T.Fill);
            var box = new RectangleF(cell.X + cell.Width / 2 - ShelfThumbPx / 2, cell.Y + 10, ShelfThumbPx, ShelfThumbPx);
            using (var pen = new Pen(Ink.A(p.T.Text, (hover ? 0.6f : 0.35f) * p.Alpha), 1.4f) { DashPattern = new[] { 4f, 3f } })
            using (var path = Ink.Round(box, 12)) g.DrawPath(pen, path);
            p.Icon(IcoPaste, 20f, hover ? p.T.Text : p.T.Sub, box.X + box.Width / 2, box.Y + box.Height / 2);
            p.Text(Lang.T("Tempel"), Ink.Small(11f), hover ? p.T.Text : p.T.Sub, cell.X + cell.Width / 2, cell.Y + 82, cell.Width - 12, StringAlignment.Center);
        }

        const char IcoPaste = '\uE77F';

        void PickShelfFiles()
        {
            using (var d = new OpenFileDialog { Multiselect = true, Title = Lang.T("Pilih berkas untuk rak"), RestoreDirectory = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.FileNames.Length == 0) return;
                AddToShelf(d.FileNames);
                SetSegment(PageShelf, 0, true);
                Show(PageShelf);
            }
        }

        void DrawShelfCell(ShelfItem item, RectangleF cell)
        {
            string id = "shelf" + item.Id, del = "shelfDel" + item.Id;
            var it = item;
            var hit = RectangleF.Inflate(cell, -4, -4);
            AddHit(hit, id, delegate { ShelfClick(it); });
            bool hover = Hover(id) || Hover(del), picked = shelfPicked.Contains(item.Id);
            if (hover || picked) p.FillRound(hit, 16, Pressed(id) || picked ? p.T.FillHover : p.T.Fill);
            if (picked) p.StrokeRound(hit, 16, p.T.Accent, 1.6f);
            if (Pressed(id)) ShelfMouseDown(it);
            var thumb = new RectangleF(cell.X + cell.Width / 2 - ShelfThumbPx / 2, cell.Y + 10, ShelfThumbPx, ShelfThumbPx);
            DrawShelfThumb(item, thumb, 12);
            // Check badge on the picked
            if (picked)
            {
                p.Circle(thumb.X + 2, thumb.Y + 2, 9, p.T.Accent);
                p.Icon('\uE73E', 10f, p.T.OnAccent, thumb.X + 2, thumb.Y + 2);
            }
            p.Text(item.Name, Ink.Small(11f), p.T.Text, cell.X + cell.Width / 2, cell.Y + 82, cell.Width - 12, StringAlignment.Center);
            if (!hover) return;
            // Remove button over the thumbnail corner
            var x = new RectangleF(thumb.Right - 12, thumb.Y - 6, 20, 20);
            AddHit(x, del, delegate { Vanish(new[] { ShelfCellRect(it) }, false); RemoveFromShelf(it); });
            p.Circle(x.X + 10, x.Y + 10, 10, Hover(del) ? p.T.Text : Ink.Mix(p.T.Bg, p.T.Text, 0.25f));
            p.Cross(x.X + 10, x.Y + 10, 8, Hover(del) ? p.T.Bg : p.T.Text);
        }

        void DrawShelfThumb(ShelfItem item, RectangleF r, float radius)
        {
            if (item.Thumb != null) { p.Image(item.Thumb, r, radius); return; }
            p.FillRound(r, radius, p.T.Fill);
            string ext = Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
            if (ext.Length > 4) ext = ext.Substring(0, 4);
            if (ext.Length > 0) p.TextCenter(ext, Ink.Semi(Math.Max(9f, r.Height * 0.22f)), p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
            else p.Icon('\uE8B7', r.Height * 0.4f, p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        // Compact drop target and its thanks
        float ShelfDropWidth() { return dropActive ? (DropSplit ? 380 : 300) : 260; }

        void DrawShelfDrop(float w, float h, DateTime now)
        {
            float cy = h / 2;
            if (dropActive && DropSplit) { DrawDropBoxes(w, h, now); return; }
            if (dropActive)
            {
                // Dashed target that breathes while files hover
                float breath = Anim.Reduced ? 0 : (float)Math.Sin((now - dropUtc).TotalSeconds * Math.PI * 2) * 1.5f;
                var target = new RectangleF(10 - breath, 8 - breath, w - 20 + breath * 2, h - 16 + breath * 2);
                using (var pen = new Pen(Ink.A(p.T.Text, 0.55f * p.Alpha), 1.4f) { DashPattern = new[] { 4f, 3f } })
                using (var path = Ink.Round(target, (h - 16) / 2)) g.DrawPath(pen, path);
                p.Icon(ShelfOn ? '' : IcoConvert, 16f, p.T.Text, 34, cy);
                p.Text(ShelfOn ? Lang.T("Lepas untuk menaruh di rak") : Lang.T("Lepas untuk mengubah format"), Ink.Semi(13f), p.T.Text, 54, cy, w - 70, StringAlignment.Near);
                return;
            }
            float k = Ease.OutBack(Ease.Clamp01((float)((now - droppedUtc).TotalSeconds / 0.45)));
            p.Circle(30, cy, 13 * k, p.T.Text);
            p.Check(30, cy, 12, p.T.Bg, Ease.Clamp01((float)((now - droppedUtc).TotalSeconds / 0.5)));
            p.Text(droppedCount > 0 ? Lang.F("{0} berkas di rak", shelf.Count) : Lang.T("Tidak ada yang bisa ditaruh"), Ink.Semi(13f), p.T.Text, 54, cy, w - 70, StringAlignment.Near);
            AddHit(new RectangleF(0, 0, w, h), "shelfToast", delegate { DismissToast(); SetSegment(PageShelf, 0, true); Show(PageShelf); });
        }

        // Box under the files lights up
        void DrawDropBoxes(float w, float h, DateTime now)
        {
            float breath = Anim.Reduced ? 0 : (float)Math.Sin((now - dropUtc).TotalSeconds * Math.PI * 2) * 1.5f;
            float bw = (w - 26) / 2, bh = h - 16;
            for (int i = 0; i < 2; i++)
            {
                bool on = i == dropTarget;
                var box = new RectangleF(10 + i * (bw + 6), 8, bw, bh);
                if (on) { box.Inflate(breath, breath); p.FillRound(box, bh / 2, p.T.Fill); }
                using (var pen = new Pen(Ink.A(on ? p.T.Text : p.T.Sub, (on ? 0.6f : 0.3f) * p.Alpha), 1.4f) { DashPattern = new[] { 4f, 3f } })
                using (var path = Ink.Round(box, bh / 2)) g.DrawPath(pen, path);
                string label = i == 0 ? Lang.T("Taruh di rak") : Lang.T("Ubah format");
                var font = Ink.Semi(13f);
                float tw = p.Measure(label, font), x = box.X + box.Width / 2 - (tw + 24) / 2;
                p.Icon(i == 0 ? IcoShelf : IcoConvert, 15f, on ? p.T.Text : p.T.Sub, x + 8, h / 2);
                p.Text(label, font, on ? p.T.Text : p.T.Sub, x + 24, h / 2, tw + 4, StringAlignment.Near);
            }
        }

        // Thumbnails: STA worker, shell's own cache

        readonly Queue<ShelfItem> thumbQueue = new Queue<ShelfItem>();
        readonly AutoResetEvent thumbSignal = new AutoResetEvent(false);
        Thread thumbThread;

        void LoadThumb(ShelfItem item)
        {
            lock (thumbQueue) thumbQueue.Enqueue(item);
            if (thumbThread == null)
            {
                thumbThread = new Thread(ThumbLoop) { IsBackground = true, Name = "thumbs", Priority = ThreadPriority.BelowNormal };
                thumbThread.SetApartmentState(ApartmentState.STA);
                thumbThread.Start();
            }
            thumbSignal.Set();
        }

        void ThumbLoop()
        {
            while (true)
            {
                thumbSignal.WaitOne();
                while (true)
                {
                    ShelfItem item;
                    lock (thumbQueue)
                    {
                        if (thumbQueue.Count == 0) break;
                        item = thumbQueue.Dequeue();
                    }
                    int px = (int)Math.Ceiling(ShelfThumbPx * scale);
                    Bitmap bmp = null;
                    try { bmp = ShellThumbs.Get(item.Path, px); }
                    catch (Exception ex) { Log.Write("thumb: " + ex.Message); }
                    if (bmp == null) continue;
                    var made = bmp;
                    Post(delegate
                    {
                        if (!shelf.Contains(item) && !ConvOwns(item)) { made.Dispose(); return; }
                        if (item.Thumb != null) item.Thumb.Dispose();
                        item.Thumb = made;
                        Kick();
                    });
                }
            }
        }
    }

    // IShellItemImageFactory: cache, then thumbnail, then icon
    static class ShellThumbs
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        const int InCacheOnly = 0x10, ThumbnailOnly = 0x8, IconOnly = 0x4, BiggerOk = 0x1;

        public static Bitmap Get(string path, int px)
        {
            IShellItemImageFactory f;
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
            try
            {
                var size = new SIZE { cx = px, cy = px };
                IntPtr h;
                foreach (int flags in new[] { InCacheOnly | BiggerOk, ThumbnailOnly | BiggerOk, IconOnly })
                    if (f.GetImage(size, flags, out h) == 0 && h != IntPtr.Zero)
                        try { return FromDib(h, px); }
                        finally { DeleteObject(h); }
                return null;
            }
            finally { Marshal.ReleaseComObject(f); }
        }

        // Keep the alpha that Image.FromHbitmap throws away
        static Bitmap FromDib(IntPtr h, int px)
        {
            BITMAP info;
            if (GetObject(h, Marshal.SizeOf(typeof(BITMAP)), out info) == 0) return null;
            int w = info.bmWidth, ht = info.bmHeight;
            var bih = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -ht, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * ht * 4];
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            try { if (GetDIBits(dc, h, 0, (uint)ht, bits, ref bih, 0) == 0) return null; }
            finally { DeleteDC(dc); }
            bool anyAlpha = false;
            for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
            // No alpha at all: an opaque photo
            if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
            var raw = new Bitmap(w, ht, PixelFormat.Format32bppPArgb);
            var data = raw.LockBits(new Rectangle(0, 0, w, ht), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < ht; y++) Marshal.Copy(bits, y * w * 4, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), w * 4); }
            finally { raw.UnlockBits(data); }
            if (w == px && ht == px) return raw;
            // Centre-crop to the shown square
            using (raw)
            {
                var sq = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                using (var gr = Graphics.FromImage(sq))
                {
                    gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float s = Math.Max((float)px / w, (float)px / ht);
                    float dw = w * s, dh = ht * s;
                    gr.DrawImage(raw, new RectangleF((px - dw) / 2, (px - dh) / 2, dw, dh));
                }
                return sq;
            }
        }
    }
}
