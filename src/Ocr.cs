using System;
using System.IO;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tepi
{
    // Windows OCR on this machine; nothing leaves it
    static class Ocr
    {
        // Language list only; no model loaded
        public static bool Available
        {
            get
            {
                try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
                catch (Exception ex) { Log.Write("ocr: " + ex.Message); return false; }
            }
        }

        // Profile languages first, else any installed
        static OcrEngine Engine()
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
                foreach (var lang in OcrEngine.AvailableRecognizerLanguages) { engine = OcrEngine.TryCreateFromLanguage(lang); if (engine != null) break; }
            return engine;
        }

        // Off the UI thread; the engine goes with the call
        public static string Read(string path)
        {
            var file = Converter.W(StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));
            using (var s = Converter.W(file.OpenAsync(FileAccessMode.Read))) return Read(s);
        }

        // Any encoded picture: photos, rendered PDF pages
        public static string Read(IRandomAccessStream s)
        {
            var engine = Engine();
            if (engine == null) return null;
            var dec = Converter.W(BitmapDecoder.CreateAsync(s));
            uint w = dec.PixelWidth, h = dec.PixelHeight, max = OcrEngine.MaxImageDimension;
            var t = new BitmapTransform();
            // Oversized images shrink to fit the engine
            if (w > max || h > max)
            {
                double k = (double)max / Math.Max(w, h);
                t.ScaledWidth = (uint)(w * k);
                t.ScaledHeight = (uint)(h * k);
            }
            using (var bmp = Converter.W(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)))
            {
                var result = Converter.W(engine.RecognizeAsync(bmp));
                var sb = new StringBuilder();
                foreach (var line in result.Lines) sb.AppendLine(line.Text);
                return sb.ToString().TrimEnd();
            }
        }
    }
}
