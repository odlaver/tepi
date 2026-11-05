using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Snip an area, its text is copied
    partial class Island
    {
        const int ScreenTextHotkey = 19;
        System.Windows.Forms.Timer screenTextWait;

        // The next snip goes to OCR
        bool ScreenTextArmed { get { return screenTextWait != null && screenTextWait.Enabled; } }

        void ReadScreenText()
        {
            if (!OcrNow()) { ShowInfo('', Lang.T("OCR Windows belum tersedia"), Lang.T("Tambahkan bahasa di Pengaturan Windows"), true); return; }
            if (screenTextWait == null)
            {
                screenTextWait = new System.Windows.Forms.Timer { Interval = 60000 };
                screenTextWait.Tick += delegate { DisarmScreenText(); };
            }
            screenTextWait.Stop();
            screenTextWait.Start();
            ApplyClips();
            // Windows' own snip: no overlay of ours
            try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                Log.Write("screenclip: " + ex.Message);
                DisarmScreenText();
                ShowInfo('', Lang.T("Alat potong layar tidak terbuka"), "", true);
            }
        }

        void DisarmScreenText()
        {
            if (screenTextWait != null) screenTextWait.Stop();
            ApplyClips();
        }

        DateTime screenTextTookUtc;

        // True: picture was meant for text
        bool TakeScreenText(IDataObject data)
        {
            // One copy can fire twice
            if ((DateTime.UtcNow - screenTextTookUtc).TotalSeconds < 1.5) return true;
            if (!ScreenTextArmed) return false;
            DisarmScreenText();
            screenTextTookUtc = DateTime.UtcNow;
            Bitmap copy = null;
            try
            {
                using (var clip = data.GetData(DataFormats.Bitmap, true) as Bitmap)
                    if (clip != null) copy = OwnCopy(clip);
            }
            catch (Exception ex) { Log.Write("screen text read: " + ex.Message); }
            if (copy == null) return false;
            ocrBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = Path.Combine(Path.GetTempPath(), "Tepi-teks-" + Guid.NewGuid().ToString("N") + ".png");
                string text = null;
                try { copy.Save(path, ImageFormat.Png); text = Ocr.Read(path); }
                catch (Exception ex) { Log.Write("screen text: " + ex.Message); }
                finally { copy.Dispose(); TryDelete(path); }
                Post(() => { ocrBusy = false; CopyRead(text); });
            });
            return true;
        }
    }
}
