using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace Tepi
{
    // Ubah: drop into the box, pick a format
    partial class Island
    {
        const char IcoConvert = '', IcoAlert = '';
        const float ConvertW = 420, ConvPad = 18, ConvChipH = 34, ConvGap = 8, ConvHead = 72;
        const int ConvCols = 4;
        List<ShelfItem> convItems;
        List<ConvTarget> convTargets;
        ConvJob convJob;
        DateTime convSeenUtc, convDoneUtc;

        bool ConvertSetting { get { return settings.GetBool("convert", true); } }
        bool ConvertOn { get { return convItems != null; } }
        bool ConvRunning { get { return convJob != null && !convJob.Finished; } }

        List<string> ConvPaths()
        {
            var l = new List<string>();
            if (convItems != null) foreach (var i in convItems) l.Add(i.Path);
            return l;
        }

        void OpenConvert(string[] files)
        {
            if (ConvRunning) { ShowInfo(IcoConvert, Lang.T("Masih mengubah berkas lain"), "", true); return; }
            var list = new List<ShelfItem>();
            foreach (var raw in files)
            {
                if (!ShelfSafe(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); } catch (Exception) { continue; }
                if (list.Exists(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new ShelfItem { Path = full, Name = Path.GetFileName(full.TrimEnd('\\')), Id = ++shelfIds, AddedUtc = DateTime.UtcNow });
            }
            if (list.Count == 0) { ShowInfo(IcoConvert, Lang.T("Tidak ada yang bisa diubah"), "", true); return; }
            DropConvertItems();
            convItems = list;
            for (int i = 0; i < Math.Min(2, list.Count); i++) LoadThumb(list[i]);
            convTargets = Converter.Targets(ConvPaths());
            convJob = null;
            convSeenUtc = DateTime.UtcNow;
            expanded = false;
            Kick();
        }

        void DropConvertItems()
        {
            if (convItems == null) return;
            foreach (var i in convItems) if (i.Thumb != null) i.Thumb.Dispose();
            convItems = null;
        }

        void CloseConvert()
        {
            if (convItems == null) return;
            if (ConvRunning) CancelConvert();
            DropConvertItems();
            convJob = null;
            Kick();
        }

        void StartConvert(ConvTarget target)
        {
            if (ConvRunning || convItems == null) return;
            var job = new ConvJob { Files = ConvPaths(), Target = target };
            job.Changed += delegate { ConvChanged(job); };
            convJob = job;
            Converter.Start(job);
            Announce(Lang.F("Mengubah ke {0}", target.Label));
            Kick();
        }

        void CancelConvert()
        {
            var job = convJob;
            if (job == null || job.Finished) return;
            job.Cancelled = true;
            var proc = job.Proc;
            if (proc != null) System.Threading.ThreadPool.QueueUserWorkItem(delegate { Converter.Kill(proc); });
        }

        // Progress redraws ride the live rate
        void ConvChanged(ConvJob job)
        {
            if (job.Finished) Post(delegate { ConvFinished(job); });
        }

        void ConvFinished(ConvJob job)
        {
            if (!ReferenceEquals(job, convJob)) return;
            convDoneUtc = DateTime.UtcNow;
            if (job.Cancelled) { convJob = null; Kick(); return; }
            if (job.Outputs.Count > 0) RememberConvert(job);
            if (job.Outputs.Count > 0 && ShelfOn) AddToShelf(job.Outputs);
            Announce(ConvTitle(job));
            Kick();
        }

        // Done leaves after a while untouched
        void TickConvert()
        {
            if (convItems == null) return;
            var now = DateTime.UtcNow;
            if (pointerInside) { convSeenUtc = now; return; }
            if (ConvRunning) return;
            bool done = convJob != null && convJob.Finished;
            if ((now - convSeenUtc).TotalSeconds > (done ? 12 : 90)) CloseConvert();
        }

        // Size

        float ConvertHeight()
        {
            if (convJob == null) return ConvHead + ConvChipRows() * (ConvChipH + ConvGap) - ConvGap + (ConvMissing().Length > 0 ? 30 : 0) + ConvPad;
            if (!convJob.Finished) return 100;
            return ConvHead + ConvChipH + ConvPad;
        }

        int ConvChipRows() { return Math.Max(1, (convTargets.Count + ConvCols - 1) / ConvCols); }

        // Tools behind the dimmed chips
        string ConvMissing()
        {
            var names = new List<string>();
            foreach (var t in convTargets)
                foreach (var n in t.Needs)
                    if (Converter.Tool(n) == null && !names.Contains(Converter.ToolName(n))) names.Add(Converter.ToolName(n));
            return string.Join(", ", names);
        }

        string ConvTitle(ConvJob job)
        {
            int ok = job.Outputs.Count, bad = job.Errors.Count;
            if (ok == 0) return Lang.T("Gagal mengubah");
            if (bad > 0) return Lang.F("{0} dari {1} berhasil", ok, ok + bad);
            if (ok == 1) return Path.GetFileName(job.Outputs[0]);
            return Lang.F("{0} berkas jadi {1}", ok, job.Target.Label);
        }

        // Draw

        void DrawConvert(float w, float h, DateTime now)
        {
            if (convItems == null) return;
            var job = convJob;
            if (job == null) DrawConvertPick(w);
            else if (!job.Finished) DrawConvertRun(w, job, now);
            else DrawConvertDone(w, job);
        }

        void ConvHeader(float w, string title, string detail)
        {
            float x = ConvPad + 40 + 14, right = w - ConvPad - 34;
            p.Text(title, Ink.Semi(14f), p.T.Text, x, 30, right - x, StringAlignment.Near);
            p.Text(detail, Ink.Text(12.5f), p.T.Sub, x, 50, right - x, StringAlignment.Near);
        }

        // Stacked thumbs say "more than one"
        void ConvThumbs(RectangleF r)
        {
            if (convItems.Count > 1)
            {
                var back = new RectangleF(r.X + 5, r.Y - 4, r.Width - 4, r.Height - 4);
                float keep = p.Alpha;
                p.Alpha = keep * 0.55f;
                DrawShelfThumb(convItems[1], back, 10);
                p.Alpha = keep;
            }
            DrawShelfThumb(convItems[0], r, 10);
        }

        void ConvClose(float w)
        {
            float cx = w - ConvPad - 10, cy = 38;
            AddHit(new RectangleF(cx - 14, cy - 14, 28, 28), "convClose", CloseConvert);
            if (Hover("convClose")) p.Circle(cx, cy, 14, Pressed("convClose") ? p.T.FillPressed : p.T.FillHover);
            p.Cross(cx, cy, 9, Hover("convClose") ? p.T.Text : p.T.Sub);
        }

        long ConvBytes()
        {
            long n = 0;
            foreach (var i in convItems)
                try { if (File.Exists(i.Path)) n += new FileInfo(i.Path).Length; }
                catch (Exception) { }
            return n;
        }

        void DrawConvertPick(float w)
        {
            ConvThumbs(new RectangleF(ConvPad, 18, 40, 40));
            string title = convItems.Count == 1 ? convItems[0].Name : Lang.F("{0} berkas", convItems.Count);
            long bytes = ConvBytes();
            string kind = Converter.KindName(ConvPaths());
            ConvHeader(w, title, bytes > 0 ? kind + " · " + Fmt.Bytes(bytes) : kind);
            ConvClose(w);
            float chipW = (w - 2 * ConvPad - (ConvCols - 1) * ConvGap) / ConvCols;
            for (int i = 0; i < convTargets.Count; i++)
            {
                var t = convTargets[i];
                var r = new RectangleF(ConvPad + (i % ConvCols) * (chipW + ConvGap), ConvHead + (i / ConvCols) * (ConvChipH + ConvGap), chipW, ConvChipH);
                Pill(r, "conv_" + t.Id, t.Label, PillKind.Secondary, delegate { StartConvert(t); }, t.Ready);
            }
            string missing = ConvMissing();
            if (missing.Length > 0)
            {
                float y = ConvHead + ConvChipRows() * (ConvChipH + ConvGap) - ConvGap + 18;
                p.Text(Lang.F("Yang redup perlu {0} terpasang", missing), Ink.Text(12f), p.T.Sub, ConvPad, y, w - 2 * ConvPad, StringAlignment.Near);
            }
        }

        void DrawConvertRun(float w, ConvJob job, DateTime now)
        {
            ConvThumbs(new RectangleF(ConvPad, 18, 40, 40));
            int i = Math.Min(job.Index, job.Files.Count - 1);
            string detail = job.Files.Count > 1 ? Lang.F("{0} dari {1}", i + 1, job.Files.Count) + " · " + Path.GetFileName(job.Files[i].TrimEnd('\\')) : Path.GetFileName(job.Files[0].TrimEnd('\\'));
            if (job.Cancelled) detail = Lang.T("Membatalkan…");
            float x = ConvPad + 54, bx = w - ConvPad - 64;
            p.Text(Lang.F("Mengubah ke {0}", job.Target.Label), Ink.Semi(14f), p.T.Text, x, 30, bx - 8 - x, StringAlignment.Near);
            p.Text(detail, Ink.Text(12.5f), p.T.Sub, x, 50, bx - 8 - x, StringAlignment.Near);
            Pill(new RectangleF(bx, 23, 64, 30), "convCancel", Lang.T("Batal"), PillKind.Secondary, CancelConvert, !job.Cancelled);
            var track = new RectangleF(ConvPad, ConvHead + 6, w - 2 * ConvPad, 4);
            p.FillRound(track, 2, p.T.Line);
            bool known = job.Part >= 0 || job.Files.Count > 1;
            if (known) p.FillRound(new RectangleF(track.X, track.Y, Math.Max(4, track.Width * job.Overall), track.Height), 2, p.T.Accent);
            else
            {
                // Unknown length: a sliding segment
                float seg = track.Width * 0.28f, at = (track.Width + seg) * orbitPhase - seg;
                var st = g.Save();
                g.IntersectClip(track);
                p.FillRound(new RectangleF(track.X + at, track.Y, seg, track.Height), 2, p.T.Accent);
                g.Restore(st);
            }
        }

        void DrawConvertDone(float w, ConvJob job)
        {
            bool ok = job.Outputs.Count > 0;
            float cx = ConvPad + 20, cy = 38;
            ShelfItem made = null;
            if (ok) { var d = convHistory.Find(x => string.Equals(x.Item.Path, job.Outputs[0], StringComparison.OrdinalIgnoreCase)); if (d != null) made = d.Item; }
            if (made != null)
            {
                // Result thumb drags out like a Rak cell
                var r = new RectangleF(ConvPad, 18, 40, 40);
                AddHit(r, "convMade", delegate { OpenShelfItem(made); });
                if (Pressed("convMade")) ShelfMouseDown(made);
                DrawShelfThumb(made, r, 10);
                p.Circle(r.Right - 2, r.Bottom - 2, 8, p.T.Text);
                p.Check(r.Right - 2, r.Bottom - 2, 8, p.T.Bg, 1f);
            }
            else if (ok)
            {
                p.Circle(cx, cy, 16, p.T.Text);
                p.Check(cx, cy, 14, p.T.Bg, 1f);
            }
            else
            {
                p.Circle(cx, cy, 18, p.T.Fill);
                p.Icon(IcoAlert, 15f, p.T.Negative, cx, cy);
            }
            string detail = job.Errors.Count > 0 ? ConvError(job) : ShelfOn ? Lang.T("Di samping berkas asli, juga di Rak") : Lang.T("Di samping berkas asli");
            ConvHeader(w, ConvTitle(job), detail);
            ConvClose(w);
            float y = ConvHead, bw = 104;
            if (ok)
            {
                string first = job.Outputs[0];
                Pill(new RectangleF(ConvPad, y, bw, ConvChipH), "convOpen", Lang.T("Buka"), PillKind.Primary, delegate { OpenPath(first); });
                Pill(new RectangleF(ConvPad + bw + ConvGap, y, bw + 22, ConvChipH), "convReveal", Lang.T("Tampilkan di folder"), PillKind.Secondary, delegate { RevealPath(first); });
            }
            else Pill(new RectangleF(ConvPad, y, bw, ConvChipH), "convRetry", Lang.T("Coba lagi"), PillKind.Primary, delegate { var t = job.Target; convJob = null; StartConvert(t); });
            Pill(new RectangleF(w - ConvPad - bw - 10, y, bw + 10, ConvChipH), "convAgain", Lang.T("Format lain"), PillKind.Tertiary, delegate { convJob = null; Kick(); });
        }

        // One file: its name is already the title
        string ConvError(ConvJob job)
        {
            string e = job.Errors[0];
            if (job.Files.Count != 1) return e;
            string prefix = Path.GetFileName(job.Files[0].TrimEnd('\\')) + ": ";
            return e.StartsWith(prefix) ? e.Substring(prefix.Length) : e;
        }

        void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("convert open: " + ex.Message); ShowInfo(IcoAlert, Lang.T("Berkas tidak bisa dibuka"), Path.GetFileName(path), true); }
        }

        void RevealPath(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception ex) { Log.Write("convert reveal: " + ex.Message); }
        }

        string DescribeConvert()
        {
            if (convItems == null) return "";
            if (convJob == null) return Lang.T("Ubah format") + ". " + Lang.F("{0} berkas", convItems.Count);
            if (!convJob.Finished) return Lang.F("Mengubah ke {0}", convJob.Target.Label);
            return ConvTitle(convJob);
        }
    }
}
