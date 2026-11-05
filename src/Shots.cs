using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Copied images and screenshots, previewed
    partial class Island
    {
        class Shot
        {
            public Bitmap Thumb;
            public int W, H;
            public DateTime Utc;
            // Saved by Windows, else our temp copy
            public string Found, TempPath;
            public string Path { get { return Found ?? TempPath; } }
        }

        const float ShotThumbH = 48, ShotThumbW = 85;
        const char IcoShelf = '';
        Shot shot;
        System.Windows.Forms.Timer shotFind;

        bool ShotsOn { get { return settings.GetBool("shots", true); } }

        static string ShotsTemp() { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), @"Tepi\shots"); }

        void OnClipImage(IDataObject data)
        {
            Bitmap full = null;
            try
            {
                // Own copy: clipboard bitmap breaks off-thread
                using (var clip = data.GetData(DataFormats.Bitmap, true) as Bitmap)
                    if (clip != null && clip.Width >= 8 && clip.Height >= 8) full = OwnCopy(clip);
            }
            catch (Exception ex) { Log.Write("shot read: " + ex.Message); }
            if (full == null) return;
            // One copy can fire twice
            var prev = shot;
            if (prev != null && prev.W == full.Width && prev.H == full.Height && (DateTime.UtcNow - prev.Utc).TotalSeconds < 1.5) { full.Dispose(); return; }
            var s = new Shot { W = full.Width, H = full.Height, Utc = DateTime.UtcNow };
            SetShot(s);
            string dir = ShotsTemp();
            float k = scale;
            // Thumb and save off the UI thread
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap thumb = null;
                string path = null;
                try { thumb = ShotThumb(full, k); }
                catch (Exception ex) { Log.Write("shot thumb: " + ex.Message); }
                try
                {
                    Directory.CreateDirectory(dir);
                    path = System.IO.Path.Combine(dir, "Tepi " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss.fff") + ".png");
                    full.Save(path, ImageFormat.Png);
                }
                catch (Exception ex) { Log.Write("shot save: " + ex.Message); path = null; }
                finally { full.Dispose(); }
                Post(() =>
                {
                    if (shot != s) { if (thumb != null) thumb.Dispose(); TryDelete(path); return; }
                    s.Thumb = thumb;
                    // Windows saved it first: drop ours
                    if (s.Found != null) TryDelete(path); else s.TempPath = path;
                    if (thumb == null) return;
                    var t = ShowToast(View.Shot, 7, 2, true);
                    if (t != null) t.Data = s;
                });
            });
            if (shotFind == null)
            {
                shotFind = new System.Windows.Forms.Timer { Interval = 1200 };
                shotFind.Tick += delegate { shotFind.Stop(); FindSavedShot(); };
            }
            shotFind.Stop();
            shotFind.Start();
        }

        // Memory copy, not a redraw
        static Bitmap OwnCopy(Bitmap clip)
        {
            try { return clip.Clone(new Rectangle(0, 0, clip.Width, clip.Height), clip.PixelFormat); }
            catch (Exception) { return new Bitmap(clip); }
        }

        void SetShot(Shot s)
        {
            var old = shot;
            shot = s;
            if (old == null) return;
            if (old.Thumb != null) old.Thumb.Dispose();
            // Unused temp copy goes; shelved ones stay
            if (old.TempPath != null && !shelf.Exists(x => x.Path == old.TempPath)) TryDelete(old.TempPath);
        }

        // Snipping Tool saves to Screenshots
        void FindSavedShot()
        {
            var s = shot;
            if (s == null) return;
            string dir = KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"));
            if (dir == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string found = null;
                try
                {
                    var since = s.Utc.AddSeconds(-4);
                    DateTime best = since;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.png"))
                        if (f.CreationTimeUtc > best) { best = f.CreationTimeUtc; found = f.FullName; }
                }
                catch (Exception ex) { Log.Write("shot find: " + ex.Message); }
                if (found == null) return;
                Post(() =>
                {
                    if (shot != s) return;
                    s.Found = found;
                    if (s.TempPath != null) { TryDelete(s.TempPath); s.TempPath = null; }
                    Kick();
                });
            });
        }

        static string KnownFolder(Guid id)
        {
            IntPtr raw;
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out raw) != 0) return null;
            try { return Marshal.PtrToStringUni(raw); }
            finally { Marshal.FreeCoTaskMem(raw); }
        }

        static void TryDelete(string path)
        {
            if (path == null) return;
            try { File.Delete(path); } catch (Exception) { }
        }

        // Temp copies: a day, or until exit
        void CleanShots(bool all)
        {
            try
            {
                var dir = new DirectoryInfo(ShotsTemp());
                if (!dir.Exists) return;
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var f in dir.EnumerateFiles("Tepi *.png"))
                    if (all || f.LastWriteTimeUtc < cutoff) TryDelete(f.FullName);
            }
            catch (Exception ex) { Log.Write("shot clean: " + ex.Message); }
        }

        // Center crop, sized for this screen
        static Bitmap ShotThumb(Bitmap full, float scale)
        {
            int tw = (int)Math.Ceiling(ShotThumbW * scale), th = (int)Math.Ceiling(ShotThumbH * scale);
            float want = (float)tw / th, have = (float)full.Width / full.Height;
            var src = have > want
                ? new RectangleF((full.Width - full.Height * want) / 2, 0, full.Height * want, full.Height)
                : new RectangleF(0, (full.Height - full.Width / want) / 2, full.Width, full.Width / want);
            var bmp = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
            using (var g2 = Graphics.FromImage(bmp))
            {
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g2.DrawImage(full, new RectangleF(0, 0, tw, th), src, GraphicsUnit.Pixel);
            }
            return bmp;
        }

        void DrawShot(float w, float h)
        {
            var s = toast != null ? toast.Data as Shot : null;
            if (s == null || s != shot) return;
            float cy = h / 2;
            var thumb = new RectangleF(14, cy - ShotThumbH / 2, ShotThumbW, ShotThumbH);
            bool ready = s.Path != null;
            AddHit(thumb, "shotThumb", delegate { OpenShot(); });
            if (ready && Pressed("shotThumb")) dragItem = new ShelfItem { Path = s.Path, Name = System.IO.Path.GetFileName(s.Path) };
            p.Image(s.Thumb, thumb, 10);
            p.StrokeRound(thumb, 10, Ink.A(p.T.Text, 0.14f), 1f);
            // The thumbnail opens; pills do the rest
            float x = thumb.Right + 14, pw = 52, bw = ShotBlurW, gap = 6;
            int pills = OcrShown ? 2 : 1;
            float right = w - 16 - bw - pw * pills - gap * pills;
            p.Text(s.Found != null ? Lang.T("Screenshot") : Lang.T("Gambar disalin"), Ink.Semi(13.5f), p.T.Text, x, cy - 10, right - x - 8, StringAlignment.Near);
            p.Text(ocrBusy ? Lang.T("Membaca teks…") : s.W + " × " + s.H, Ink.Text(12.5f), p.T.Sub, x, cy + 11, right - x - 8, StringAlignment.Near);
            if (OcrShown) { Pill(new RectangleF(right, cy - 15, pw, 30), "shotText", Lang.T("Teks"), PillKind.Secondary, delegate { CopyText(s.Path); }, ready && !ocrBusy); right += pw + gap; }
            Pill(new RectangleF(right, cy - 15, bw, 30), "shotBlur", Lang.T("Samarkan"), PillKind.Secondary, delegate { OpenRedact(s.Path); }, ready);
            Pill(new RectangleF(right + bw + gap, cy - 15, pw, 30), "shotShelf", Lang.T("Rak"), PillKind.Secondary, delegate { ShelveShot(); }, ready);
        }

        const float ShotBlurW = 74;

        // Checked on first need, not at startup: OCR runtime costs memory
        bool OcrShown
        {
            get
            {
                if (!ocrKnown && !ocrProbing)
                {
                    ocrProbing = true;
                    ThreadPool.QueueUserWorkItem(delegate { ocrReady = Ocr.Available; ocrKnown = true; Post(Kick); });
                }
                return ocrKnown && ocrReady;
            }
        }
        volatile bool ocrKnown, ocrReady, ocrBusy, ocrProbing;

        // Menus can't wait for the async check
        bool OcrNow()
        {
            if (!ocrKnown) { ocrReady = Ocr.Available; ocrKnown = true; }
            return ocrReady;
        }

        // Read, then copy; the toast says how it went
        void CopyText(string path)
        {
            if (path == null || ocrBusy) return;
            ocrBusy = true;
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = null;
                try { text = Ocr.Read(path); }
                catch (Exception ex) { Log.Write("ocr read: " + ex.Message); }
                Post(() => { ocrBusy = false; CopyRead(text); });
            });
        }

        // The toast says how it went
        void CopyRead(string text)
        {
            if (string.IsNullOrEmpty(text)) { ShowInfo('\uE8C8', Lang.T("Tidak ada teks terbaca"), "", true); return; }
            try { Clipboard.SetText(text); }
            catch (Exception ex) { Log.Write("ocr copy: " + ex.Message); return; }
            int lines = text.Split('\n').Length;
            ShowInfo('\uE8C8', Lang.T("Teks disalin"), Lang.F("{0} baris", lines), true);
        }

        void ShelveShot()
        {
            var s = shot;
            if (s == null || s.Path == null) return;
            AddToShelf(new[] { s.Path });
            DismissToast();
            ShowInfo(IcoShelf, Lang.T("Ditaruh di rak"), System.IO.Path.GetFileName(s.Path), true);
        }

        void OpenShot()
        {
            var s = shot;
            if (s == null || s.Path == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s.Path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("shot open: " + ex.Message); }
            DismissToast();
        }
    }
}
