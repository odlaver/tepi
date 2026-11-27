using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Unduhan and Recycle Bin at the end of the dock
        sealed partial class DockWindow
        {
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            struct BinInfo { public int Size; public long Bytes, Items; }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct FileOp
            {
                public IntPtr Hwnd;
                public uint Func;
                public string From, To;
                public ushort Flags;
                public bool Aborted;
                public IntPtr Names;
                public string Title;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref BinInfo info);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref FileOp op);

            bool DownloadsTile { get { return S.GetBool("dockbar.downloads", isle.TopBarOn); } }
            bool TrashTile { get { return S.GetBool("dockbar.trash", isle.TopBarOn); } }

            long binItems = -1;
            int binBusy;

            // Off the UI thread: a big bin can take a moment to count
            void ReadBin()
            {
                if (!TrashTile || Interlocked.Exchange(ref binBusy, 1) == 1) return;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    long n = -1;
                    try
                    {
                        var info = new BinInfo { Size = Marshal.SizeOf(typeof(BinInfo)) };
                        if (SHQueryRecycleBin(null, ref info) == 0) n = info.Items;
                    }
                    catch (Exception) { }
                    binBusy = 0;
                    if (n == binItems) return;
                    try { BeginInvoke(new Action(delegate { binItems = n; if (!closing) Render(); })); } catch (Exception) { }
                });
            }

            // Running downloads as one 0..1 value, -1 when none
            float DownloadProgress()
            {
                if (isle.downloads.Count == 0) return -1;
                int sum = 0, n = 0;
                foreach (var d in isle.downloads.Values) if (d.Percent >= 0) { sum += d.Percent; n++; }
                return n == 0 ? 0.08f : sum / (100f * n);
            }

            void DrawDownloads(RectangleF r, float cx, float cy, float u, Color ink)
            {
                p.Icon('', 16 * u, ink, cx, cy);
                float pr = DownloadProgress();
                if (pr < 0) return;
                // A thin ring fills as files arrive
                var ring = new RectangleF(cx - 15 * u, cy - 15 * u, 30 * u, 30 * u);
                g.DrawEllipse(p.P(Ink.A(ink, 0.22f), 2f * u), ring);
                g.DrawArc(p.P(p.T.Text, 2f * u), ring, -90, Math.Max(8, 360 * pr));
            }

            void DrawTrash(RectangleF r, float cx, float cy, float u, Color ink)
            {
                p.Icon('', 16 * u, ink, cx, cy);
                // Something inside: a small dot, not a count
                if (binItems > 0) p.FillRound(new RectangleF(cx + 7 * u, cy - 12 * u, 6 * u, 6 * u), 3 * u, ink);
            }

            string StackLabel(string id)
            {
                if (id == "downloads")
                {
                    float pr = DownloadProgress();
                    return pr < 0 ? Lang.T("Unduhan") : Lang.T("Unduhan") + " · " + (int)Math.Round(pr * 100) + "%";
                }
                if (binItems > 0) return Lang.F("Recycle Bin · {0} item", binItems);
                return "Recycle Bin";
            }

            // Newest ten, like a stack opened upward
            void DownloadsMenu()
            {
                string dir = DownloadsFolder();
                var m = new ContextMenuStrip();
                var items = new List<FileSystemInfo>();
                try
                {
                    if (dir != null && Directory.Exists(dir))
                        foreach (var f in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                        {
                            if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || FinalName(f.FullName) != null) continue;
                            items.Add(f);
                        }
                }
                catch (Exception ex) { Log.Write("dock downloads: " + ex.Message); }
                items.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                if (items.Count == 0) m.Items.Add(new ToolStripMenuItem(Lang.T("Belum ada unduhan")) { Enabled = false });
                for (int i = 0; i < items.Count && i < 10; i++)
                {
                    var f = items[i];
                    var it = new ToolStripMenuItem(Shorten(f.Name, 44), SmallIcon(f));
                    it.ToolTipText = f.Name;
                    it.Click += delegate { Launch(f.FullName); };
                    m.Items.Add(it);
                }
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Buka folder Unduhan"), null, delegate { if (dir != null) Launch(dir); });
                ShowAbove(m, "sys:downloads");
            }

            void TrashMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Buka"), null, delegate { OpenBin(); });
                // Windows asks before anything is erased
                var empty = new ToolStripMenuItem(Lang.T("Kosongkan Recycle Bin")) { Enabled = binItems != 0 };
                empty.Click += delegate
                {
                    try { SHEmptyRecycleBin(Handle, null, 0); } catch (Exception ex) { Log.Write("empty bin: " + ex.Message); }
                    ReadBin();
                };
                m.Items.Add(empty);
                ShowAbove(m, "sys:trash");
            }

            void OpenBin() { Launch("shell:RecycleBinFolder"); }

            // Dropped on the bin: to the Recycle Bin, undoable
            void Recycle(string[] files)
            {
                try
                {
                    var op = new FileOp { Hwnd = Handle, Func = 3, From = string.Join("\0", files) + "\0\0", Flags = 0x40 };
                    SHFileOperation(ref op);
                }
                catch (Exception ex) { Log.Write("dock recycle: " + ex.Message); }
                ReadBin();
            }

            void ShowAbove(ContextMenuStrip m, string id)
            {
                CloseCard();
                if (sheet != null) SheetDone(sheet);
                labelShow.Target = 0;
                MenuLook.Apply(m);
                m.Closed += delegate { BeginInvoke(new Action(delegate { foreach (ToolStripItem it in m.Items) if (it.Image != null) it.Image.Dispose(); m.Dispose(); })); };
                MenuGuard.Watch(m);
                var at = HitScreenRect(id);
                m.Show(new Point(at.X, at.Top - (int)(6 * scale)), ToolStripDropDownDirection.AboveRight);
            }

            static void Launch(string what)
            {
                try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("dock open: " + ex.Message); }
            }

            static string Shorten(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 12) + "…" + s.Substring(s.Length - 11); }

            static Image SmallIcon(FileSystemInfo f)
            {
                try
                {
                    if (f is DirectoryInfo) return null;
                    using (var ic = Icon.ExtractAssociatedIcon(f.FullName))
                    using (var big = ic.ToBitmap())
                    {
                        var b = new Bitmap(16, 16);
                        using (var g2 = Graphics.FromImage(b))
                        {
                            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g2.DrawImage(big, 0, 0, 16, 16);
                        }
                        return b;
                    }
                }
                catch (Exception) { return null; }
            }
        }
    }
}
