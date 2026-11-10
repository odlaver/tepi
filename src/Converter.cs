using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Data.Pdf;
using IAsyncAction = Windows.Foundation.IAsyncAction;
using AsyncStatus = Windows.Foundation.AsyncStatus;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace Tepi
{
    enum ConvKind { Image, Video, Audio, Doc, Slides, Sheet, Pdf, Markdown, Html, Zip, Folder, Other }

    class ConvTarget
    {
        public string Id, Label;
        // Outside tools this target needs
        public string[] Needs = new string[0];
        // Snapshot of Ready when the box opens
        public bool Ok = true;
        public bool Ready { get { foreach (var n in Needs) if (Converter.Tool(n) == null) return false; return true; } }
    }

    class ConvJob
    {
        public List<string> Files;
        public ConvTarget Target;
        public volatile bool Cancelled, Finished;
        public volatile int Index;
        // Compress: files already as small as they get, and the bytes saved
        public int Small;
        public long BytesIn, BytesOut;
        // Current file, 0..1; below 0 unknown
        public volatile float Part = -1;
        public readonly List<string> Outputs = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public Process Proc;
        public event Action Changed;
        public void Ping() { var c = Changed; if (c != null) c(); }

        public float Overall
        {
            get
            {
                int n = Math.Max(1, Files.Count);
                // One output from many inputs: Part is the whole job
                bool single = Target != null && (Target.Id == "zip" || Target.Id == "pdf1");
                if (single) return Math.Max(0f, Math.Min(1f, Part >= 0 ? Part : (float)Index / n));
                return Math.Min(1f, (Index + Math.Max(0f, Part)) / n);
            }
        }
    }

    // Converts beside source; tools never bundled
    static class Converter
    {
        static readonly Dictionary<string, ConvKind> kinds = new Dictionary<string, ConvKind>(StringComparer.OrdinalIgnoreCase);

        static Converter()
        {
            Map(ConvKind.Image, "jpg jpeg jfif png bmp gif tif tiff ico webp heic heif avif jxr wdp");
            Map(ConvKind.Video, "mp4 m4v mov mkv webm avi wmv flv 3gp mpg mpeg ts mts m2ts");
            Map(ConvKind.Audio, "mp3 wav m4a aac flac ogg oga opus wma aif aiff amr");
            Map(ConvKind.Doc, "docx doc odt rtf");
            Map(ConvKind.Slides, "pptx ppt odp");
            Map(ConvKind.Sheet, "xlsx xls ods csv");
            Map(ConvKind.Pdf, "pdf");
            Map(ConvKind.Markdown, "md markdown");
            Map(ConvKind.Html, "html htm");
            Map(ConvKind.Zip, "zip");
        }

        static void Map(ConvKind k, string exts) { foreach (var e in exts.Split(' ')) kinds["." + e] = k; }

        public static ConvKind KindOf(string path)
        {
            if (Directory.Exists(path)) return ConvKind.Folder;
            ConvKind k;
            return kinds.TryGetValue(Path.GetExtension(path), out k) ? k : ConvKind.Other;
        }

        // Same family counts as same format
        static string Norm(string ext)
        {
            ext = ext.TrimStart('.').ToLowerInvariant();
            switch (ext)
            {
                case "jpeg": case "jfif": return "jpg";
                case "tif": return "tiff";
                case "htm": return "html";
                case "markdown": return "md";
                case "oga": return "ogg";
                default: return ext;
            }
        }

        static ConvTarget T(string id, string label, params string[] needs) { return new ConvTarget { Id = id, Label = label, Needs = needs }; }

        static List<ConvTarget> For(ConvKind k)
        {
            var l = new List<ConvTarget>();
            switch (k)
            {
                case ConvKind.Image:
                    l.Add(T("jpg", "JPG")); l.Add(T("png", "PNG")); l.Add(T("webp", "WEBP", "ffmpeg")); l.Add(T("bmp", "BMP"));
                    l.Add(T("tiff", "TIFF")); l.Add(T("gif", "GIF")); l.Add(T("ico", "ICO")); l.Add(T("pdf", "PDF"));
                    l.Add(T("ocr", Lang.T("Teks (OCR)"), "ocr")); l.Add(T("min", Lang.T("Kompres")));
                    break;
                case ConvKind.Video:
                    l.Add(T("mp4", "MP4", "ffmpeg")); l.Add(T("webm", "WEBM", "ffmpeg")); l.Add(T("gif", "GIF", "ffmpeg")); l.Add(T("mp3", "MP3", "ffmpeg"));
                    l.Add(T("min", Lang.T("Kompres"), "ffmpeg"));
                    break;
                case ConvKind.Audio:
                    l.Add(T("mp3", "MP3", "ffmpeg")); l.Add(T("m4a", "M4A", "ffmpeg")); l.Add(T("wav", "WAV", "ffmpeg")); l.Add(T("flac", "FLAC", "ffmpeg")); l.Add(T("ogg", "OGG", "ffmpeg"));
                    l.Add(T("min", Lang.T("Kompres"), "ffmpeg"));
                    break;
                case ConvKind.Doc:
                    l.Add(T("pdf", "PDF", "soffice")); l.Add(T("docx", "DOCX", "soffice")); l.Add(T("odt", "ODT", "soffice")); l.Add(T("rtf", "RTF", "soffice")); l.Add(T("txt", "TXT", "soffice"));
                    break;
                case ConvKind.Slides:
                    l.Add(T("pdf", "PDF", "soffice")); l.Add(T("pptx", "PPTX", "soffice")); l.Add(T("odp", "ODP", "soffice")); l.Add(T("png", "PNG", "soffice")); l.Add(T("jpg", "JPG", "soffice"));
                    break;
                case ConvKind.Sheet:
                    l.Add(T("pdf", "PDF", "soffice")); l.Add(T("xlsx", "XLSX", "soffice")); l.Add(T("ods", "ODS", "soffice")); l.Add(T("csv", "CSV", "soffice"));
                    break;
                case ConvKind.Pdf:
                    l.Add(T("docx", "DOCX", "pdfword")); l.Add(T("txt", "TXT", "pdfword")); l.Add(T("png", "PNG")); l.Add(T("jpg", "JPG"));
                    l.Add(T("ocr", Lang.T("Teks (OCR)"), "ocr")); l.Add(T("min", Lang.T("Kompres")));
                    break;
                case ConvKind.Markdown:
                    l.Add(T("docx", "DOCX", "pandoc")); l.Add(T("html", "HTML", "pandoc")); l.Add(T("pdf", "PDF", "pandoc", "soffice"));
                    break;
                case ConvKind.Html:
                    l.Add(T("pdf", "PDF", "soffice")); l.Add(T("docx", "DOCX", "pandoc")); l.Add(T("md", "MD", "pandoc"));
                    break;
                case ConvKind.Zip:
                    l.Add(T("unzip", Lang.T("Ekstrak")));
                    break;
            }
            return l;
        }

        // Formats every dropped file can become
        public static List<ConvTarget> Targets(IList<string> files)
        {
            List<ConvTarget> common = null;
            bool paged = files.Count > 0, still = false;
            foreach (var f in files)
            {
                var k = KindOf(f);
                if (k != ConvKind.Image && k != ConvKind.Pdf) paged = false;
                // Animations and icons lose frames when squeezed
                string ext = Norm(Path.GetExtension(f));
                if (ext == "gif" || ext == "ico") still = true;
                var mine = For(k);
                if (common == null) common = mine;
                else common = common.FindAll(t => mine.Exists(m => m.Id == t.Id));
            }
            if (common == null) common = new List<ConvTarget>();
            // Hidden when all files already match
            common.RemoveAll(t => AllAre(files, t.Id));
            if (still) common.RemoveAll(t => t.Id == "min");
            if (paged && files.Count > 1) common.Add(T("pdf1", Lang.T("Gabung PDF")));
            bool oneZip = files.Count == 1 && KindOf(files[0]) == ConvKind.Zip;
            if (!oneZip) common.Add(T("zip", "ZIP"));
            return common;
        }

        static bool AllAre(IList<string> files, string id)
        {
            foreach (var f in files) if (Norm(Path.GetExtension(f)) != id) return false;
            return true;
        }

        // "*.jpg;*.png" for a file picker
        public static string Patterns(ConvKind[] want)
        {
            var list = new List<string>();
            foreach (var kv in kinds) if (Array.IndexOf(want, kv.Value) >= 0) list.Add("*" + kv.Key);
            return string.Join(";", list.ToArray());
        }

        public static string KindName(IList<string> files)
        {
            if (files.Count == 0) return "";
            var k = KindOf(files[0]);
            foreach (var f in files) if (KindOf(f) != k) return Lang.T("Campuran");
            switch (k)
            {
                case ConvKind.Image: return Lang.T("Gambar");
                case ConvKind.Video: return "Video";
                case ConvKind.Audio: return "Audio";
                case ConvKind.Doc: return Lang.T("Dokumen");
                case ConvKind.Slides: return Lang.T("Presentasi");
                case ConvKind.Sheet: return Lang.T("Spreadsheet");
                case ConvKind.Pdf: return "PDF";
                case ConvKind.Markdown: return "Markdown";
                case ConvKind.Html: return "HTML";
                case ConvKind.Zip: return "ZIP";
                case ConvKind.Folder: return "Folder";
                default: return Lang.T("Berkas");
            }
        }

        // Absolute PATH entries, known installs only

        static readonly Dictionary<string, string> tools = new Dictionary<string, string>();
        static DateTime toolsUtc = DateTime.MinValue;

        public static string ToolName(string id)
        {
            switch (id)
            {
                case "soffice": return "LibreOffice";
                case "pandoc": return "Pandoc";
                case "pdfword": return Lang.T("Word atau LibreOffice");
                case "ocr": return Lang.T("bahasa OCR Windows");
                default: return "ffmpeg";
            }
        }

        // Word's COM server, from its registration
        static string WordExe()
        {
            try
            {
                using (var progid = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"Word.Application\CLSID"))
                {
                    string clsid = progid != null ? progid.GetValue("") as string : null;
                    if (string.IsNullOrEmpty(clsid)) return null;
                    using (var server = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32"))
                    {
                        string cmd = server != null ? server.GetValue("") as string : null;
                        if (string.IsNullOrEmpty(cmd)) return null;
                        cmd = cmd.Trim();
                        string exe = cmd.StartsWith("\"") ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1)) : cmd.Split(' ')[0];
                        return File.Exists(exe) ? exe : null;
                    }
                }
            }
            catch (Exception) { return null; }
        }

        public static string Tool(string id)
        {
            lock (tools)
            {
                if ((DateTime.UtcNow - toolsUtc).TotalSeconds > 30) { tools.Clear(); toolsUtc = DateTime.UtcNow; }
                string path;
                if (!tools.TryGetValue(id, out path)) { path = FindTool(id); tools[id] = path; }
                return path;
            }
        }

        static string FindTool(string id)
        {
            // PDF to Word: Word reads PDFs best; LibreOffice will do
            if (id == "pdfword") return WordExe() ?? FindTool("soffice");
            // OCR is Windows' own: ready once a language is installed
            if (id == "ocr") return Ocr.Available ? "windows" : null;
            string exe = id + ".exe";
            var dirs = new List<string>();
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string t = d.Trim().Trim('"');
                if (t.Length > 2 && Path.IsPathRooted(t) && !t.StartsWith(@"\\")) dirs.Add(t);
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (id == "soffice") { dirs.Add(Path.Combine(pf, @"LibreOffice\program")); dirs.Add(Path.Combine(pf86, @"LibreOffice\program")); }
            if (id == "pandoc") { dirs.Add(Path.Combine(local, "Pandoc")); dirs.Add(Path.Combine(pf, "Pandoc")); }
            if (id == "ffmpeg") dirs.Add(Path.Combine(local, @"Microsoft\WinGet\Links"));
            foreach (var d in dirs)
            {
                try
                {
                    string full = Path.Combine(d, exe);
                    if (File.Exists(full)) return full;
                }
                catch (Exception) { }
            }
            return null;
        }

        // Job

        public static void Start(ConvJob job)
        {
            var t = new Thread(delegate () { Run(job); }) { IsBackground = true, Name = "convert" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        static void Run(ConvJob job)
        {
            try
            {
                string id = job.Target.Id;
                if (id == "zip" || id == "pdf1")
                {
                    string first = job.Files[0];
                    string dir = Path.GetDirectoryName(first.TrimEnd('\\'));
                    string name = job.Files.Count == 1 ? Path.GetFileNameWithoutExtension(first.TrimEnd('\\')) : new DirectoryInfo(dir).Name;
                    if (string.IsNullOrEmpty(name)) name = "Tepi";
                    string outPath = UniqueFile(dir, name, id == "zip" ? ".zip" : ".pdf");
                    Guard(job, outPath, delegate
                    {
                        if (id == "zip") Zip(job, outPath);
                        else if (job.Files.TrueForAll(f => KindOf(f) == ConvKind.Image)) ImagesToPdf(job, job.Files, outPath, true);
                        else MergePdfs(job, outPath);
                    });
                }
                else
                {
                    for (int i = 0; i < job.Files.Count && !job.Cancelled; i++)
                    {
                        job.Index = i;
                        job.Part = -1;
                        job.Ping();
                        string src = job.Files[i];
                        try { One(job, src, id); }
                        catch (Exception ex)
                        {
                            if (job.Cancelled) break;
                            Log.Write("convert " + Path.GetFileName(src) + ": " + ex.Message);
                            lock (job.Errors) job.Errors.Add(Path.GetFileName(src) + ": " + Short(ex));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!job.Cancelled)
                {
                    Log.Write("convert: " + ex.Message);
                    lock (job.Errors) job.Errors.Add(Short(ex));
                }
            }
            job.Index = job.Files.Count;
            job.Part = 0;
            job.Finished = true;
            job.Ping();
        }

        static string Short(Exception ex)
        {
            if (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            string m = ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
            return m.Length > 140 ? m.Substring(0, 140) + "…" : m;
        }

        // Partial output goes on failure
        static void Guard(ConvJob job, string outPath, Action work)
        {
            try { work(); }
            catch (Exception)
            {
                TryDelete(outPath);
                throw;
            }
            if (job.Cancelled) { TryDelete(outPath); return; }
            lock (job.Outputs) job.Outputs.Add(outPath);
        }

        static void One(ConvJob job, string src, string id)
        {
            var kind = KindOf(src);
            string dir = Path.GetDirectoryName(src.TrimEnd('\\'));
            string name = Path.GetFileNameWithoutExtension(src.TrimEnd('\\'));
            if (kind == ConvKind.Zip && id == "unzip") { Unzip(job, src, dir, name); return; }
            if (id == "min") { Compress(job, src, kind, dir, name); return; }
            if (id == "ocr") { OcrText(job, src, kind, dir, name); return; }
            if (kind == ConvKind.Pdf && (id == "png" || id == "jpg")) { PdfToImages(job, src, dir, name, id); return; }
            if (kind == ConvKind.Slides && (id == "png" || id == "jpg")) { SlidesToImages(job, src, dir, name, id); return; }
            // Already that format: nothing to do
            if (Norm(Path.GetExtension(src)) == id) return;
            string outPath = UniqueFile(dir, name, "." + id);
            Guard(job, outPath, delegate
            {
                switch (kind)
                {
                    case ConvKind.Pdf:
                        PdfToWord(job, src, outPath, id);
                        break;
                    case ConvKind.Image:
                        if (id == "pdf") ImagesToPdf(job, new List<string> { src }, outPath, false);
                        else ImageTo(src, outPath, id);
                        break;
                    case ConvKind.Video:
                    case ConvKind.Audio:
                        Ffmpeg(job, src, outPath, FfmpegArgs(id));
                        break;
                    case ConvKind.Markdown:
                    case ConvKind.Html:
                        if (id == "pdf" && kind == ConvKind.Markdown) MarkdownToPdf(job, src, outPath);
                        else if (id == "pdf") Office(job, src, outPath, "pdf");
                        else Pandoc(job, src, outPath, kind == ConvKind.Markdown ? "markdown" : "html", id == "md" ? "gfm" : id);
                        break;
                    default:
                        Office(job, src, outPath, OfficeFilter(id));
                        break;
                }
            });
        }

        // Names

        public static string UniqueFile(string dir, string name, string ext)
        {
            string p = Path.Combine(dir, name + ext);
            for (int i = 1; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, name + " (" + i + ")" + ext);
            return p;
        }

        static string UniqueDir(string dir, string name) { return UniqueFile(dir, name, ""); }

        static void TryDelete(string p)
        {
            try
            {
                if (File.Exists(p)) File.Delete(p);
                else if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            catch (Exception) { }
        }

        // WinRT awaits without Windows.winmd
        internal static T W<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            using (var done = new ManualResetEvent(false))
            {
                op.Completed = delegate { try { done.Set(); } catch (ObjectDisposedException) { } };
                if (op.Status == AsyncStatus.Started && !done.WaitOne(120000)) { op.Cancel(); throw new TimeoutException("WinRT"); }
            }
            if (op.Status != AsyncStatus.Completed) throw op.ErrorCode ?? new InvalidOperationException("WinRT " + op.Status);
            return op.GetResults();
        }

        internal static void W(IAsyncAction op)
        {
            using (var done = new ManualResetEvent(false))
            {
                op.Completed = delegate { try { done.Set(); } catch (ObjectDisposedException) { } };
                if (op.Status == AsyncStatus.Started && !done.WaitOne(120000)) { op.Cancel(); throw new TimeoutException("WinRT"); }
            }
            if (op.Status != AsyncStatus.Completed) throw op.ErrorCode ?? new InvalidOperationException("WinRT " + op.Status);
        }

        static byte[] ReadAll(InMemoryRandomAccessStream ms)
        {
            using (var reader = new DataReader(ms.GetInputStreamAt(0)))
            {
                uint n = W(reader.LoadAsync((uint)ms.Size));
                var bytes = new byte[n];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        // Images: WIC decode keeps EXIF rotation

        static Bitmap Decode(string path)
        {
            try
            {
                var file = W(StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)));
                using (var s = W(file.OpenAsync(FileAccessMode.Read)))
                {
                    var dec = W(WinBitmapDecoder.CreateAsync(s));
                    var px = W(dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                        ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb));
                    byte[] data = px.DetachPixelData();
                    int w = (int)dec.OrientedPixelWidth, h = (int)dec.OrientedPixelHeight;
                    var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try { for (int y = 0; y < h; y++) Marshal.Copy(data, y * w * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w * 4); }
                    finally { bmp.UnlockBits(bd); }
                    return bmp;
                }
            }
            catch (Exception ex)
            {
                Log.Write("decode wic: " + ex.Message);
                // GDI+ as a fallback, no file lock
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
        }

        // JPG and BMP have no alpha: white under it
        static Bitmap Flatten(Bitmap src)
        {
            var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return b;
        }

        static ImageCodecInfo Codec(string mime)
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == mime) return c;
            return null;
        }

        static byte[] Jpeg(Bitmap src, long quality)
        {
            using (var flat = Flatten(src))
            using (var ms = new MemoryStream())
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                flat.Save(ms, Codec("image/jpeg"), ep);
                return ms.ToArray();
            }
        }

        static void ImageTo(string src, string outPath, string id)
        {
            if (id == "webp") { ImageViaFfmpeg(src, outPath); return; }
            using (var bmp = Decode(src))
            {
                switch (id)
                {
                    case "jpg": File.WriteAllBytes(outPath, Jpeg(bmp, 92)); break;
                    case "png": bmp.Save(outPath, ImageFormat.Png); break;
                    case "bmp": using (var flat = Flatten(bmp)) flat.Save(outPath, ImageFormat.Bmp); break;
                    case "tiff": bmp.Save(outPath, ImageFormat.Tiff); break;
                    case "gif": bmp.Save(outPath, ImageFormat.Gif); break;
                    case "ico": WriteIco(bmp, outPath); break;
                    default: throw new NotSupportedException(id);
                }
            }
        }

        // WIC has no WebP encoder
        static void ImageViaFfmpeg(string src, string outPath)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "tepi-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (var bmp = Decode(src)) bmp.Save(tmp, ImageFormat.Png);
                Ffmpeg(null, tmp, outPath, "-c:v libwebp -quality 90");
            }
            finally { TryDelete(tmp); }
        }

        // PNG-in-ICO, Vista and later
        static void WriteIco(Bitmap src, string outPath)
        {
            var sizes = new List<int>();
            int big = Math.Max(src.Width, src.Height);
            foreach (int s in new[] { 256, 64, 48, 32, 16 }) if (s <= big || s == 16) sizes.Add(s);
            var images = new List<byte[]>();
            foreach (int s in sizes)
                using (var sq = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(sq))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float k = (float)s / big;
                        float w = src.Width * k, h = src.Height * k;
                        g.DrawImage(src, new RectangleF((s - w) / 2, (s - h) / 2, w, h));
                    }
                    using (var ms = new MemoryStream()) { sq.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                }
            using (var fs = File.Create(outPath))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Count);
                int offset = 6 + 16 * sizes.Count;
                for (int i = 0; i < sizes.Count; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((short)1); bw.Write((short)32);
                    bw.Write(images[i].Length); bw.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) bw.Write(img);
            }
        }

        // PDF: one A4 page per image, JPEG inside
        static void ImagesToPdf(ConvJob job, List<string> files, string outPath, bool track)
        {
            const float PageW = 595.28f, PageH = 841.89f, Margin = 28f;
            var pages = new List<byte[]>();
            var dims = new List<Size>();
            for (int i = 0; i < files.Count && !job.Cancelled; i++)
            {
                if (track) { job.Index = i; job.Ping(); }
                using (var bmp = Decode(files[i]))
                {
                    pages.Add(Jpeg(bmp, 88));
                    dims.Add(bmp.Size);
                }
            }
            if (job.Cancelled) return;
            var inv = CultureInfo.InvariantCulture;
            using (var fs = File.Create(outPath))
            {
                var offsets = new List<long>();
                Action<string> put = s => { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); };
                put("%PDF-1.4\n%âã\n");
                int n = pages.Count, objs = 2 + n * 3;
                var kids = new StringBuilder();
                for (int i = 0; i < n; i++) kids.Append(3 + i * 3).Append(" 0 R ");
                offsets.Add(fs.Position); put("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                offsets.Add(fs.Position); put("2 0 obj\n<< /Type /Pages /Kids [" + kids + "] /Count " + n + " >>\nendobj\n");
                for (int i = 0; i < n; i++)
                {
                    int pg = 3 + i * 3, ct = pg + 1, im = pg + 2;
                    var d = dims[i];
                    bool wide = d.Width > d.Height;
                    float pw = wide ? PageH : PageW, ph = wide ? PageW : PageH;
                    // 96 dpi at most, never blown up
                    float k = Math.Min(0.75f, Math.Min((pw - 2 * Margin) / d.Width, (ph - 2 * Margin) / d.Height));
                    float dw = d.Width * k, dh = d.Height * k;
                    string draw = string.Format(inv, "q {0:0.##} 0 0 {1:0.##} {2:0.##} {3:0.##} cm /Im0 Do Q", dw, dh, (pw - dw) / 2, (ph - dh) / 2);
                    offsets.Add(fs.Position);
                    put(string.Format(inv, "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {1:0.##} {2:0.##}] /Resources << /XObject << /Im0 {3} 0 R >> >> /Contents {4} 0 R >>\nendobj\n", pg, pw, ph, im, ct));
                    offsets.Add(fs.Position);
                    put(ct + " 0 obj\n<< /Length " + draw.Length + " >>\nstream\n" + draw + "\nendstream\nendobj\n");
                    offsets.Add(fs.Position);
                    put(im + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + d.Width + " /Height " + d.Height + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + pages[i].Length + " >>\nstream\n");
                    fs.Write(pages[i], 0, pages[i].Length);
                    put("\nendstream\nendobj\n");
                }
                long xref = fs.Position;
                var sb = new StringBuilder();
                sb.Append("xref\n0 ").Append(objs + 1).Append("\n0000000000 65535 f \n");
                foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
                sb.Append("trailer\n<< /Size ").Append(objs + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
                put(sb.ToString());
            }
        }

        // From memory: no file handle left behind
        static PdfDocument LoadPdf(string path, out InMemoryRandomAccessStream ms)
        {
            ms = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(ms.GetOutputStreamAt(0)))
            {
                w.WriteBytes(File.ReadAllBytes(path));
                W(w.StoreAsync());
                W(w.FlushAsync());
                w.DetachStream();
            }
            return W(PdfDocument.LoadFromStreamAsync(ms));
        }

        // Windows' PDF reader counts; optionally draws page one
        static uint PdfPages(string path, bool render)
        {
            InMemoryRandomAccessStream ms;
            var doc = LoadPdf(path, out ms);
            using (ms)
            {
                if (render && doc.PageCount > 0)
                    using (var page = doc.GetPage(0))
                    using (var img = new InMemoryRandomAccessStream())
                        W(page.RenderToStreamAsync(img, new PdfPageRenderOptions { DestinationWidth = 160 }));
                return doc.PageCount;
            }
        }

        // A rewritten PDF must open, page for page
        static void Verify(string src, string made)
        {
            if (PdfPages(made, true) != PdfPages(src, false)) throw new InvalidDataException(Lang.T("Hasilnya tidak lolos pemeriksaan"));
        }

        // PDF pages: Windows renders them
        static void PdfToImages(ConvJob job, string src, string dir, string name, string id)
        {
            InMemoryRandomAccessStream held;
            var doc = LoadPdf(src, out held);
            using (held)
            {
                PdfPagesOut(job, doc, dir, name, id);
            }
        }

        static void PdfPagesOut(ConvJob job, PdfDocument doc, string dir, string name, string id)
        {
            if (doc.IsPasswordProtected) throw new InvalidOperationException(Lang.T("PDF dikunci kata sandi"));
            uint count = doc.PageCount;
            string outDir = count > 1 ? UniqueDir(dir, name) : dir;
            if (count > 1) Directory.CreateDirectory(outDir);
            var made = new List<string>();
            try
            {
                for (uint i = 0; i < count && !job.Cancelled; i++)
                {
                    job.Part = (float)i / count;
                    job.Ping();
                    string outPath = count > 1 ? Path.Combine(outDir, name + "-" + (i + 1) + "." + id) : UniqueFile(dir, name, "." + id);
                    using (var page = doc.GetPage(i))
                    using (var ms = new InMemoryRandomAccessStream())
                    {
                        // 192 dpi: sharp text, sane size
                        var opt = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * 2) };
                        if (id == "jpg") opt.BitmapEncoderId = BitmapEncoder.JpegEncoderId;
                        W(page.RenderToStreamAsync(ms, opt));
                        File.WriteAllBytes(outPath, ReadAll(ms));
                    }
                    made.Add(outPath);
                }
            }
            catch (Exception)
            {
                foreach (var m in made) TryDelete(m);
                if (count > 1) TryDelete(outDir);
                throw;
            }
            if (job.Cancelled) { foreach (var m in made) TryDelete(m); if (count > 1) TryDelete(outDir); return; }
            lock (job.Outputs) job.Outputs.Add(count > 1 ? outDir : made[0]);
        }

        // Archives

        static void Zip(ConvJob job, string outPath)
        {
            var entries = new List<KeyValuePair<string, string>>();
            foreach (var f in job.Files)
            {
                string clean = f.TrimEnd('\\');
                if (Directory.Exists(clean))
                {
                    string root = Path.GetDirectoryName(clean);
                    foreach (var sub in Directory.GetFiles(clean, "*", SearchOption.AllDirectories))
                        entries.Add(new KeyValuePair<string, string>(sub, sub.Substring(root.Length).TrimStart('\\').Replace('\\', '/')));
                }
                else entries.Add(new KeyValuePair<string, string>(clean, Path.GetFileName(clean)));
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var z = ZipFile.Open(outPath, ZipArchiveMode.Create))
                for (int i = 0; i < entries.Count && !job.Cancelled; i++)
                {
                    job.Part = (float)i / Math.Max(1, entries.Count);
                    job.Ping();
                    string entry = entries[i].Value;
                    for (int n = 1; !used.Add(entry); n++)
                        entry = Path.GetFileNameWithoutExtension(entries[i].Value) + " (" + n + ")" + Path.GetExtension(entries[i].Value);
                    z.CreateEntryFromFile(entries[i].Key, entry, CompressionLevel.Optimal);
                }
        }

        // Entries stay inside the target folder
        static void Unzip(ConvJob job, string src, string dir, string name)
        {
            string outDir = UniqueDir(dir, name);
            string root = Path.GetFullPath(outDir) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(outDir);
            try
            {
                using (var z = ZipFile.OpenRead(src))
                {
                    int i = 0, n = Math.Max(1, z.Entries.Count);
                    foreach (var e in z.Entries)
                    {
                        if (job.Cancelled) break;
                        job.Part = (float)i++ / n;
                        job.Ping();
                        string full = Path.GetFullPath(Path.Combine(outDir, e.FullName));
                        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Lang.T("ZIP berisi jalur di luar folder"));
                        if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(full); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        e.ExtractToFile(full, false);
                    }
                }
            }
            catch (Exception) { TryDelete(outDir); throw; }
            if (job.Cancelled) { TryDelete(outDir); return; }
            lock (job.Outputs) job.Outputs.Add(outDir);
        }

        // Outside tools

        static string Q(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        static string FfmpegArgs(string id)
        {
            switch (id)
            {
                case "mp4": return "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -c:a aac -b:a 160k -movflags +faststart";
                case "webm": return "-c:v libvpx-vp9 -crf 33 -b:v 0 -row-mt 1 -deadline good -cpu-used 4 -c:a libopus -b:a 128k";
                case "gif": return "-an -vf \"fps=12,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -loop 0";
                case "mp3": return "-vn -c:a libmp3lame -q:a 2";
                case "m4a": return "-vn -c:a aac -b:a 192k";
                case "wav": return "-vn -c:a pcm_s16le";
                case "flac": return "-vn -c:a flac";
                case "ogg": return "-vn -c:a libvorbis -q:a 5";
                default: throw new NotSupportedException(id);
            }
        }

        static Process Launch(string exe, string args, bool readOut)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = readOut,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath()
            };
            if (readOut) psi.StandardOutputEncoding = Encoding.UTF8;
            var p = Process.Start(psi);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            return p;
        }

        // Ends the tool and its children
        public static void Kill(Process p)
        {
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
                using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + p.Id) { UseShellExecute = false, CreateNoWindow = true })) k.WaitForExit(5000);
            }
            catch (Exception ex) { Log.Write("convert kill: " + ex.Message); }
        }

        static void Ffmpeg(ConvJob job, string src, string outPath, string args)
        {
            string exe = Converter.Tool("ffmpeg");
            if (exe == null) throw new FileNotFoundException(Lang.T("ffmpeg tidak ditemukan"));
            string line = "-hide_banner -nostdin -y -i " + Q(src) + " " + args + " -progress pipe:1 -nostats " + Q(outPath);
            using (var p = Launch(exe, line, true))
            {
                if (job != null) job.Proc = p;
                double total = 0;
                var tail = new Queue<string>();
                var err = new Thread(delegate ()
                {
                    string l;
                    while ((l = p.StandardError.ReadLine()) != null)
                    {
                        int at = l.IndexOf("Duration: ");
                        TimeSpan ts;
                        if (at >= 0 && total == 0 && TimeSpan.TryParse(l.Substring(at + 10, Math.Min(11, l.Length - at - 10)), CultureInfo.InvariantCulture, out ts)) total = ts.TotalSeconds;
                        lock (tail) { tail.Enqueue(l); if (tail.Count > 4) tail.Dequeue(); }
                    }
                }) { IsBackground = true };
                err.Start();
                string o;
                while ((o = p.StandardOutput.ReadLine()) != null)
                {
                    if (job == null || total <= 0 || !o.StartsWith("out_time_us=")) continue;
                    long us;
                    if (long.TryParse(o.Substring(12), out us) && us > 0) { job.Part = (float)Math.Min(1, us / 1e6 / total); job.Ping(); }
                }
                p.WaitForExit();
                err.Join(2000);
                if (job != null) job.Proc = null;
                if (job != null && job.Cancelled) return;
                if (p.ExitCode != 0) { lock (tail) throw new InvalidOperationException(tail.Count > 0 ? tail.ToArray()[tail.Count - 1] : "ffmpeg " + p.ExitCode); }
            }
        }

        static string OfficeFilter(string id)
        {
            switch (id)
            {
                case "txt": return "txt:Text (encoded):UTF8";
                case "csv": return "csv:Text - txt - csv (StarCalc):44,34,76";
                case "rtf": return "rtf:Rich Text Format";
                default: return id;
            }
        }

        // LibreOffice: own profile, temp output
        static void Office(ConvJob job, string src, string outPath, string filter, string infilter = null)
        {
            string exe = Converter.Tool("soffice");
            if (exe == null) throw new FileNotFoundException(Lang.T("LibreOffice tidak ditemukan"));
            string tmp = Path.Combine(Path.GetTempPath(), "tepi-ubah-" + Guid.NewGuid().ToString("N"));
            string profile = new Uri(Path.Combine(Path.GetTempPath(), "tepi-lo-profile")).AbsoluteUri;
            Directory.CreateDirectory(tmp);
            try
            {
                string line = "--headless --norestore --nolockcheck --nodefault --nologo " + Q("-env:UserInstallation=" + profile) +
                    (infilter != null ? " --infilter=" + Q(infilter) : "") + " --convert-to " + Q(filter) + " --outdir " + Q(tmp) + " " + Q(src);
                using (var p = Launch(exe, line, true))
                {
                    job.Proc = p;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(180000)) { Kill(p); throw new TimeoutException(Lang.T("LibreOffice terlalu lama")); }
                    job.Proc = null;
                    if (job.Cancelled) return;
                }
                var made = Directory.GetFiles(tmp);
                if (made.Length == 0) throw new InvalidOperationException(Lang.T("LibreOffice tidak menghasilkan berkas"));
                File.Move(made[0], outPath);
            }
            finally { TryDelete(tmp); }
        }

        static void Pandoc(ConvJob job, string src, string outPath, string from, string to)
        {
            string exe = Converter.Tool("pandoc");
            if (exe == null) throw new FileNotFoundException(Lang.T("Pandoc tidak ditemukan"));
            string line = "-f " + from + " -t " + to + (to == "html" ? " -s --metadata pagetitle=" + Q(Path.GetFileNameWithoutExtension(src)) : "") + " -o " + Q(outPath) + " " + Q(src);
            using (var p = Launch(exe, line, false))
            {
                job.Proc = p;
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000)) { Kill(p); throw new TimeoutException("Pandoc"); }
                job.Proc = null;
                if (job.Cancelled) return;
                if (p.ExitCode != 0) throw new InvalidOperationException(err.Trim().Length > 0 ? err.Trim().Split('\n')[0] : "pandoc " + p.ExitCode);
            }
        }

        static void MarkdownToPdf(ConvJob job, string src, string outPath)
        {
            string tmp = TempPath(".docx");
            try
            {
                Pandoc(job, src, tmp, "markdown", "docx");
                if (!job.Cancelled) Office(job, tmp, outPath, "pdf");
            }
            finally { TryDelete(tmp); }
        }

        static string TempPath(string ext) { return Path.Combine(Path.GetTempPath(), "tepi-" + Guid.NewGuid().ToString("N") + ext); }

        // Slides: through PDF, then a picture per slide
        static void SlidesToImages(ConvJob job, string src, string dir, string name, string id)
        {
            string tmp = TempPath(".pdf");
            try
            {
                Office(job, src, tmp, "pdf");
                if (!job.Cancelled) PdfToImages(job, tmp, dir, name, id);
            }
            finally { TryDelete(tmp); }
        }

        // Images become pages first, then one PDF
        static void MergePdfs(ConvJob job, string outPath)
        {
            var parts = new List<string>();
            var temps = new List<string>();
            uint pages = 0;
            try
            {
                for (int i = 0; i < job.Files.Count && !job.Cancelled; i++)
                {
                    job.Index = i;
                    job.Ping();
                    string part = job.Files[i];
                    if (KindOf(part) == ConvKind.Image)
                    {
                        part = TempPath(".pdf");
                        temps.Add(part);
                        ImagesToPdf(job, new List<string> { job.Files[i] }, part, false);
                    }
                    parts.Add(part);
                    pages += PdfPages(part, false);
                }
                if (job.Cancelled) return;
                PdfLite.Merge(parts, outPath, k => { if (job.Cancelled) throw new OperationCanceledException(); job.Part = k; job.Ping(); });
                if (PdfPages(outPath, true) != pages) throw new InvalidDataException(Lang.T("Hasilnya tidak lolos pemeriksaan"));
            }
            finally { foreach (var t in temps) TryDelete(t); }
        }

        // Compress

        // Smaller copy beside the original
        static void Compress(ConvJob job, string src, ConvKind kind, string dir, string name)
        {
            string stem = name + " (" + Lang.T("kompres") + ")";
            switch (kind)
            {
                case ConvKind.Pdf:
                    string pdf = UniqueFile(dir, stem, ".pdf");
                    Shrunk(job, src, pdf, delegate
                    {
                        // About 150 dpi on a full page
                        PdfLite.Compress(src, pdf, 1800, 68, k => { if (job.Cancelled) throw new OperationCanceledException(); job.Part = k; job.Ping(); });
                        Verify(src, pdf);
                    });
                    break;
                case ConvKind.Image:
                    using (var bmp = Decode(src))
                    {
                        // Transparency stays PNG; photos become JPEG
                        bool alpha = HasAlpha(bmp);
                        string img = UniqueFile(dir, stem, alpha ? ".png" : ".jpg");
                        Shrunk(job, src, img, delegate
                        {
                            double k = Math.Min(1.0, 2560.0 / Math.Max(bmp.Width, bmp.Height));
                            using (var small = Scaled(bmp, Math.Max(1, (int)Math.Round(bmp.Width * k)), Math.Max(1, (int)Math.Round(bmp.Height * k))))
                            {
                                if (alpha) small.Save(img, ImageFormat.Png);
                                else File.WriteAllBytes(img, Jpeg(small, 78));
                            }
                        });
                    }
                    break;
                case ConvKind.Video:
                    string mp4 = UniqueFile(dir, stem, ".mp4");
                    // Long side at most 1080p's width, even sizes for H.264
                    Shrunk(job, src, mp4, delegate
                    {
                        Ffmpeg(job, src, mp4, "-c:v libx264 -preset medium -crf 28 -vf \"scale='if(gt(iw,ih),trunc(min(1920,iw)/2)*2,-2)':'if(gt(iw,ih),-2,trunc(min(1920,ih)/2)*2)'\" -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart");
                    });
                    break;
                case ConvKind.Audio:
                    string mp3 = UniqueFile(dir, stem, ".mp3");
                    Shrunk(job, src, mp3, delegate { Ffmpeg(job, src, mp3, "-vn -c:a libmp3lame -b:a 128k"); });
                    break;
                default:
                    throw new NotSupportedException(Lang.T("Berkas ini tidak bisa dikompres"));
            }
        }

        // Kept only when clearly smaller; the original is never touched
        static void Shrunk(ConvJob job, string src, string outPath, Action work)
        {
            try { work(); }
            catch (Exception) { TryDelete(outPath); throw; }
            if (job.Cancelled) { TryDelete(outPath); return; }
            long before = new FileInfo(src).Length, after = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            if (after == 0 || after > before * 0.97) { TryDelete(outPath); job.Small++; return; }
            job.BytesIn += before;
            job.BytesOut += after;
            lock (job.Outputs) job.Outputs.Add(outPath);
        }

        static bool HasAlpha(Bitmap bmp)
        {
            if (!Image.IsAlphaPixelFormat(bmp.PixelFormat)) return false;
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, row.Length);
                    for (int x = 3; x < row.Length; x += 4) if (row[x] != 255) return true;
                }
            }
            finally { bmp.UnlockBits(bd); }
            return false;
        }

        static Bitmap Scaled(Bitmap src, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            using (var wrap = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                wrap.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, wrap);
            }
            return b;
        }

        // PDF to Word

        // DOCX: Word if installed, else LibreOffice's PDF import.
        // TXT comes out of that DOCX: their own text export skips text boxes
        static void PdfToWord(ConvJob job, string src, string outPath, string id)
        {
            string docx = id == "txt" ? TempPath(".docx") : outPath;
            try
            {
                if (WordExe() != null) WordSave(job, src, docx);
                else Office(job, src, docx, "docx:MS Word 2007 XML", "writer_pdf_import");
                if (id != "txt" || job.Cancelled) return;
                string text = DocxText(docx);
                // A scan has no text layer: read the pages instead
                if (text.Trim().Length == 0 && Ocr.Available) text = OcrPdf(job, src) ?? "";
                if (job.Cancelled) return;
                if (text.Trim().Length == 0) throw new InvalidOperationException(Lang.T("PDF ini tidak berisi teks, mungkin hasil scan"));
                File.WriteAllText(outPath, Lines(text), new UTF8Encoding(false));
            }
            finally { if (id == "txt") TryDelete(docx); }
        }

        // Paragraphs, tabs, and breaks, text boxes included
        static string DocxText(string path)
        {
            const string Ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var sb = new StringBuilder();
            using (var z = ZipFile.OpenRead(path))
            {
                var entry = z.GetEntry("word/document.xml");
                if (entry == null) return "";
                using (var r = System.Xml.XmlReader.Create(entry.Open()))
                {
                    bool inText = false, more = r.Read();
                    for (; more; more = r.Read())
                    {
                        // Text boxes come twice: skip the old VML copy
                        while (more && r.NodeType == System.Xml.XmlNodeType.Element && r.LocalName == "Fallback" && r.NamespaceURI == "http://schemas.openxmlformats.org/markup-compatibility/2006")
                        {
                            r.Skip();
                            more = !r.EOF;
                        }
                        if (!more) break;
                        if (r.NodeType == System.Xml.XmlNodeType.Element && r.NamespaceURI == Ns)
                        {
                            if (r.LocalName == "t") inText = !r.IsEmptyElement;
                            else if (r.LocalName == "tab") sb.Append('\t');
                            else if (r.LocalName == "br" || r.LocalName == "cr") sb.Append('\n');
                        }
                        else if (r.NodeType == System.Xml.XmlNodeType.EndElement && r.NamespaceURI == Ns)
                        {
                            if (r.LocalName == "t") inText = false;
                            else if (r.LocalName == "p") sb.Append('\n');
                        }
                        else if (inText && (r.NodeType == System.Xml.XmlNodeType.Text || r.NodeType == System.Xml.XmlNodeType.SignificantWhitespace || r.NodeType == System.Xml.XmlNodeType.Whitespace)) sb.Append(r.Value);
                    }
                }
            }
            return sb.ToString();
        }

        // Windows line ends, no runs of blank lines
        static string Lines(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
            return text.Trim().Replace("\n", "\r\n") + "\r\n";
        }

        // OCR

        // Words in pictures and scans, read by Windows
        static void OcrText(ConvJob job, string src, ConvKind kind, string dir, string name)
        {
            string outPath = UniqueFile(dir, name, ".txt");
            Guard(job, outPath, delegate
            {
                string text = kind == ConvKind.Pdf ? OcrPdf(job, src) : Ocr.Read(src);
                if (job.Cancelled) return;
                if (text == null) throw new InvalidOperationException(Lang.T("Bahasa OCR Windows belum terpasang"));
                if (text.Trim().Length == 0) throw new InvalidOperationException(Lang.T("Tidak ada teks yang terbaca"));
                File.WriteAllText(outPath, Lines(text), new UTF8Encoding(false));
            });
        }

        // Page by page at about 190 dpi
        static string OcrPdf(ConvJob job, string src)
        {
            InMemoryRandomAccessStream held;
            var doc = LoadPdf(src, out held);
            using (held)
            {
                var sb = new StringBuilder();
                for (uint i = 0; i < doc.PageCount && !job.Cancelled; i++)
                {
                    job.Part = (float)i / doc.PageCount;
                    job.Ping();
                    using (var page = doc.GetPage(i))
                    using (var ms = new InMemoryRandomAccessStream())
                    {
                        W(page.RenderToStreamAsync(ms, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * 2) }));
                        ms.Seek(0);
                        string text = Ocr.Read(ms);
                        if (text == null) return null;
                        if (text.Trim().Length > 0) sb.Append(text).Append("\n\n");
                    }
                }
                return sb.ToString();
            }
        }

        // Word on its own thread; no window, no prompts, no macros
        static void WordSave(ConvJob job, string src, string outPath)
        {
            Exception error = null;
            Process mine = null;
            var before = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("WINWORD")) { before.Add(p.Id); p.Dispose(); }
            var t = new Thread(delegate ()
            {
                object word = null;
                try
                {
                    word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application", true));
                    foreach (var p in Process.GetProcessesByName("WINWORD")) { if (mine == null && !before.Contains(p.Id)) mine = p; else p.Dispose(); }
                    Com(word, "Visible", BindingFlags.SetProperty, false);
                    Com(word, "DisplayAlerts", BindingFlags.SetProperty, 0);
                    Com(word, "AutomationSecurity", BindingFlags.SetProperty, 3);
                    var docs = Com(word, "Documents", BindingFlags.GetProperty);
                    var doc = ComNamed(docs, "Open", new object[] { src, false, true, false }, "FileName", "ConfirmConversions", "ReadOnly", "AddToRecentFiles");
                    try
                    {
                        // 16: DOCX; a background save must land first
                        ComNamed(doc, "SaveAs2", new object[] { outPath, 16 }, "FileName", "FileFormat");
                        for (int i = 0; i < 600 && Convert.ToInt32(Com(word, "BackgroundSavingStatus", BindingFlags.GetProperty)) > 0; i++) Thread.Sleep(100);
                    }
                    finally
                    {
                        ComNamed(doc, "Close", new object[] { false }, "SaveChanges");
                        Marshal.ReleaseComObject(doc);
                        Marshal.ReleaseComObject(docs);
                    }
                }
                catch (Exception ex) { error = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex; }
                finally
                {
                    // Quit only a Word this job started
                    if (word != null)
                    {
                        try { if (mine != null) ComNamed(word, "Quit", new object[] { false }, "SaveChanges"); } catch (Exception) { }
                        try { Marshal.ReleaseComObject(word); } catch (Exception) { }
                    }
                }
            }) { IsBackground = true, Name = "word" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            var started = DateTime.UtcNow;
            while (!t.Join(250))
            {
                if (!job.Cancelled && (DateTime.UtcNow - started).TotalMinutes < 4) continue;
                // Stuck or cancelled: end only our Word
                if (mine != null) try { mine.Kill(); } catch (Exception) { }
                t.Join(5000);
                if (mine != null) mine.Dispose();
                if (job.Cancelled) return;
                throw new TimeoutException(Lang.T("Word terlalu lama"));
            }
            if (mine != null) mine.Dispose();
            if (error != null) throw error;
            if (!File.Exists(outPath)) throw new InvalidOperationException(Lang.T("Word tidak menghasilkan berkas"));
        }

        static object Com(object target, string name, BindingFlags how, params object[] args)
        {
            return target.GetType().InvokeMember(name, how, null, target, args);
        }

        static object ComNamed(object target, string name, object[] args, params string[] names)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, null, null, names);
        }
    }
}
