using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Tepi
{
    // Browser downloads, read from partial files in Downloads
    partial class Island
    {
        class Download
        {
            public string Partial, Final, Name;
            public long Size, LastSize;
            public double Speed;
        }

        static readonly string[] PartialExt = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };
        const char IcoDownload = '';

        readonly Dictionary<string, Download> downloads = new Dictionary<string, Download>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer downloadTimer = new System.Windows.Forms.Timer { Interval = 700 };
        FileSystemWatcher downloadWatcher;
        string downloadDir;

        bool DownloadsOn { get { return settings.GetBool("downloads", true); } }

        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        static string DownloadsFolder()
        {
            IntPtr raw;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out raw) != 0) return null;
            try { return Marshal.PtrToStringUni(raw); }
            finally { Marshal.FreeCoTaskMem(raw); }
        }

        void ApplyDownloads()
        {
            bool want = DownloadsOn;
            if (want && downloadWatcher == null)
            {
                downloadDir = DownloadsFolder();
                if (downloadDir == null || !Directory.Exists(downloadDir)) return;
                downloadTimer.Tick -= DownloadTick;
                downloadTimer.Tick += DownloadTick;
                try
                {
                    downloadWatcher = new FileSystemWatcher(downloadDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                    FileSystemEventHandler wake = (s, e) => Post(delegate { if (!downloadTimer.Enabled) DownloadTick(null, null); });
                    downloadWatcher.Created += wake;
                    downloadWatcher.Renamed += (s, e) => wake(s, e);
                    downloadWatcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) { Log.Write("downloads watch: " + ex.Message); downloadWatcher = null; return; }
                DownloadTick(null, null);
            }
            else if (!want && downloadWatcher != null)
            {
                downloadWatcher.Dispose();
                downloadWatcher = null;
                downloadTimer.Stop();
                downloads.Clear();
                Kick();
            }
        }

        static string FinalName(string partial)
        {
            foreach (var ext in PartialExt)
                if (partial.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return partial.Substring(0, partial.Length - ext.Length);
            return null;
        }

        // Polls only while something is downloading
        void DownloadTick(object sender, EventArgs e)
        {
            if (downloadDir == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in Directory.EnumerateFiles(downloadDir))
                {
                    string final = FinalName(path);
                    if (final == null) continue;
                    seen.Add(path);
                    long size;
                    try { size = new FileInfo(path).Length; } catch (IOException) { continue; }
                    Download d;
                    if (!downloads.TryGetValue(path, out d))
                    {
                        d = new Download { Partial = path, Final = final, Name = Path.GetFileName(final), LastSize = size };
                        downloads[path] = d;
                    }
                    double rate = Math.Max(0, size - d.LastSize) / (downloadTimer.Interval / 1000.0);
                    d.Speed = d.Speed <= 0 ? rate : d.Speed * 0.6 + rate * 0.4;
                    d.LastSize = d.Size = size;
                }
            }
            catch (Exception ex) { Log.Write("downloads scan: " + ex.Message); }

            foreach (var gone in new List<string>(downloads.Keys))
            {
                if (seen.Contains(gone)) continue;
                var d = downloads[gone];
                downloads.Remove(gone);
                // Final file present means it finished, else cancelled
                if (File.Exists(d.Final) && !d.Name.StartsWith("Unconfirmed ", StringComparison.Ordinal)) DownloadDone(d);
            }
            if (downloads.Count > 0) { if (!downloadTimer.Enabled) downloadTimer.Start(); }
            else downloadTimer.Stop();
            Kick();
        }

        void DownloadDone(Download d)
        {
            var t = ShowToast(View.Info, 6, 2, false, true);
            if (t == null) return;
            t.Glyph = IcoDownload;
            t.Title = Lang.T("Unduhan selesai");
            t.Detail = d.Name;
            string file = d.Final;
            t.Data = (Action)delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch (Exception ex) { Log.Write("open download: " + ex.Message); }
            };
        }

        Download ActiveDownload()
        {
            Download best = null;
            foreach (var d in downloads.Values)
                if (d.Size > 0 && (best == null || d.Speed > best.Speed)) best = d;
            return best;
        }

        string DownloadStats(Download d)
        {
            string s = Fmt.Bytes(d.Size);
            if (d.Speed > 1024) s += " · " + Fmt.Bytes((long)d.Speed) + Lang.T("/dtk");
            if (downloads.Count > 1) s += " · +" + (downloads.Count - 1);
            return s;
        }

        float DownloadWidth()
        {
            var d = ActiveDownload();
            if (d == null || p.G == null) return 260;
            return Clamp(p.Measure(d.Name, Ink.Semi(12.5f)) + p.Measure(DownloadStats(d), Ink.Semi(12f)) + 84, 260, 460);
        }

        void DrawDownload(float w, float h, DateTime now)
        {
            var d = ActiveDownload();
            if (d == null) return;
            float cy = h / 2;
            p.Icon(IcoDownload, 13f, p.T.Text, 21, cy);
            string stats = DownloadStats(d);
            float sw = p.Measure(stats, Ink.Semi(12f));
            p.Text(d.Name, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - sw - 30, StringAlignment.Near);
            p.TextRight(stats, Ink.Semi(12f), p.T.Sub, w - 18, cy);
            string dir = downloadDir;
            AddHit(new RectangleF(0, 0, w, h), "download", delegate { try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch (Exception) { } });
        }
    }
}
