using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Tepi
{
    // Ubah tab: pick files, tools, recent results
    partial class Island
    {
        const int PageConvert = 9;
        const float ConvBoxH = 112, ConvRowH = 54, ConvKindH = 30;
        const int ConvHistoryMax = 12;

        class ConvDone
        {
            public ShelfItem Item;
            public string From = "", To = "";
            public DateTime Utc;
        }

        readonly List<ConvDone> convHistory = new List<ConvDone>();
        readonly Spring convScroll = new Spring(0, 300, 34, 0.2f);
        // Hero pairs: from, to
        static readonly string[][] ConvPairs =
        {
            new[] { "PNG", "JPG" }, new[] { "MOV", "MP4" }, new[] { "WAV", "MP3" }, new[] { "DOCX", "PDF" },
            new[] { "PDF", "PNG" }, new[] { "HEIC", "JPG" }, new[] { "MD", "DOCX" }, new[] { "MKV", "GIF" },
        };
        readonly Roller convFrom = new Roller(), convTo = new Roller();
        Func<DateTime, double> convHeroPaint;
        RectangleF convArt;
        // Next pair each time the tab is shown
        int convPair = -1;
        DateTime convShownUtc, convPairUtc;

        bool ConvOwns(ShelfItem item)
        {
            if (convItems != null && convItems.Contains(item)) return true;
            return convHistory.Exists(d => d.Item == item);
        }

        void RememberConvert(ConvJob job)
        {
            string from = job.Files.Count == 1 ? Path.GetExtension(job.Files[0]).TrimStart('.').ToUpperInvariant() : Lang.F("{0} berkas", job.Files.Count);
            if (from.Length == 0) from = Path.GetFileName(job.Files[0].TrimEnd('\\'));
            foreach (var path in job.Outputs)
            {
                var item = new ShelfItem { Path = path, Name = Path.GetFileName(path.TrimEnd('\\')), Id = ++shelfIds, AddedUtc = DateTime.UtcNow };
                LoadThumb(item);
                convHistory.Insert(0, new ConvDone { Item = item, From = from, To = job.Target.Label, Utc = DateTime.UtcNow });
            }
            while (convHistory.Count > ConvHistoryMax) ForgetConvert(convHistory[convHistory.Count - 1]);
        }

        void ForgetConvert(ConvDone d)
        {
            convHistory.Remove(d);
            if (d.Item.Thumb != null) d.Item.Thumb.Dispose();
            Kick();
        }

        void ClearConvertHistory()
        {
            Vanish(RowRects("convh", null), true);
            convScroll.Snap(0);
            foreach (var d in convHistory.ToArray()) ForgetConvert(d);
        }

        // Picker buttons: label, kinds
        static readonly object[][] ConvKinds =
        {
            new object[] { "Gambar", new[] { ConvKind.Image } },
            new object[] { "Video", new[] { ConvKind.Video } },
            new object[] { "Audio", new[] { ConvKind.Audio } },
            new object[] { "Dokumen", new[] { ConvKind.Doc, ConvKind.Slides, ConvKind.Sheet, ConvKind.Markdown, ConvKind.Html } },
            new object[] { "PDF", new[] { ConvKind.Pdf } },
            new object[] { "Arsip", new[] { ConvKind.Zip } },
        };

        void PickConvertFiles() { PickConvertFiles(null, null); }

        void PickConvertFiles(string label, ConvKind[] kinds)
        {
            string all = Lang.T("Semua berkas") + "|*.*";
            string filter = kinds == null ? all : Lang.T(label) + "|" + Converter.Patterns(kinds) + "|" + all;
            using (var d = new OpenFileDialog { Multiselect = true, Title = Lang.T("Pilih berkas untuk diubah"), RestoreDirectory = true, Filter = filter })
            {
                if (d.ShowDialog(this) != DialogResult.OK || d.FileNames.Length == 0) return;
                OpenConvert(d.FileNames);
            }
        }

        // Layout

        float ConvListTop { get { return PanelTop + 4 + ConvBoxH + 12 + ConvKindH + 14; } }

        float ConvListView(float h) { return h - ConvListTop - 26 - 10; }

        float ConvertPageHeight()
        {
            if (!ConvertSetting) return 252;
            float list = convHistory.Count == 0 ? 52 : 26 + convHistory.Count * ConvRowH;
            return Math.Min(MaxH, ConvListTop + list + 10);
        }

        void DrawConvertPage(float w, float h, DateTime now)
        {
            if (!ConvertSetting)
            {
                float by = Empty(w, IcoConvert, Lang.T("Ubah format mati"), Lang.T("Berkas yang diseret ke island hanya bisa ditaruh di rak"));
                Button(new RectangleF(w / 2 - 74, by, 148, 34), "convOn", Lang.T("Nyalakan"), true, delegate { SetFlag("convert", true); });
                return;
            }
            var shown = pageSwitchUtc > panelOpenedUtc ? pageSwitchUtc : panelOpenedUtc;
            if (shown != convShownUtc && (convPair < 0 || !Anim.Reduced))
            {
                convShownUtc = shown;
                convPair = (convPair + 1) % ConvPairs.Length;
                convPairUtc = DateTime.UtcNow;
            }
            var hero = new RectangleF(16, PanelTop + 4, w - 32, ConvBoxH);
            DrawConvertHero(hero);
            DrawConvertKinds(new RectangleF(hero.X, hero.Bottom + 12, hero.Width, ConvKindH));
            DrawConvertHistory(w, h);
        }

        // Hero: outlined source becomes a solid result
        void DrawConvertHero(RectangleF box)
        {
            AddHit(box, "convPick", PickConvertFiles);
            bool hover = Hover("convPick");
            p.FillRound(box, 22, Pressed("convPick") ? p.T.FillPressed : hover ? p.T.FillHover : p.T.Fill);
            using (var pen = new Pen(Ink.A(p.T.Text, (hover ? 0.42f : 0.22f) * p.Alpha), 1.4f) { DashPattern = new[] { 4f, 3f } })
            using (var path = Ink.Round(RectangleF.Inflate(box, -6, -6), 17)) g.DrawPath(pen, path);
            convArt = new RectangleF(box.X + 18, box.Y + 14, 150, box.Height - 28);
            if (convHeroPaint == null) convHeroPaint = PaintConvertArt;
            LiveRect(convArt, convHeroPaint);
            float x = convArt.Right + 14, cy = box.Y + box.Height / 2;
            p.Text(Lang.T("Seret berkas ke island"), Ink.Semi(15.5f), p.T.Text, x, cy - 11, box.Right - x - 18, StringAlignment.Near);
            p.Text(Lang.T("atau klik di sini untuk memilih"), Ink.Text(12.5f), p.T.Sub, x, cy + 11, box.Right - x - 18, StringAlignment.Near);
        }

        // One roll per visit, then still
        double PaintConvertArt(DateTime now)
        {
            int i = Math.Max(0, convPair);
            convFrom.Set(ConvPairs[i][0]);
            convTo.Set(ConvPairs[i][1]);
            var r = convArt;
            float dw = 50, dh = 62, cy = r.Y + r.Height / 2;
            var a = new RectangleF(r.X, cy - dh / 2, dw, dh);
            var b = new RectangleF(r.Right - dw, cy - dh / 2, dw, dh);
            ConvDoc(a, false);
            ConvDoc(b, true);
            var label = Ink.Semi(11.5f);
            convFrom.Draw(p, label, p.T.Text, a.X + dw / 2, cy + 8, StringAlignment.Center, now);
            convTo.Draw(p, label, p.T.Bg, b.X + dw / 2, cy + 8, StringAlignment.Center, now);
            // Arrow between, nudged while rolling
            float k = convTo.Moving(now) ? (float)Math.Sin(Math.Min(1, (now - convPairUtc).TotalSeconds / 0.42) * Math.PI) * 4 : 0;
            float x0 = a.Right + 12 + k, x1 = b.X - 12 + k;
            var pen = p.P(p.T.Sub, 1.6f);
            g.DrawLine(pen, x0, cy, x1, cy);
            g.DrawLines(pen, new[] { new PointF(x1 - 5, cy - 5), new PointF(x1, cy), new PointF(x1 - 5, cy + 5) });
            return convFrom.Moving(now) || convTo.Moving(now) ? 1 / 30.0 : 0;
        }

        // Page with a folded corner
        void ConvDoc(RectangleF r, bool solid)
        {
            float fold = 13;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddLine(r.X + 8, r.Y, r.Right - fold, r.Y);
                path.AddLine(r.Right - fold, r.Y, r.Right, r.Y + fold);
                path.AddLine(r.Right, r.Y + fold, r.Right, r.Bottom - 8);
                path.AddArc(r.Right - 16, r.Bottom - 16, 16, 16, 0, 90);
                path.AddArc(r.X, r.Bottom - 16, 16, 16, 90, 90);
                path.AddArc(r.X, r.Y, 16, 16, 180, 90);
                path.CloseFigure();
                if (solid) g.FillPath(p.B(p.T.Text), path);
                else g.DrawPath(p.P(p.T.Sub, 1.5f), path);
            }
            var c = solid ? Ink.Mix(p.T.Text, p.T.Bg, 0.35f) : p.T.Sub;
            g.DrawLines(p.P(c, 1.3f), new[] { new PointF(r.Right - fold, r.Y), new PointF(r.Right - fold, r.Y + fold), new PointF(r.Right, r.Y + fold) });
            // Two text lines above the label
            g.DrawLine(p.P(c, 1.6f), r.X + 10, r.Y + 17, r.Right - fold - 6, r.Y + 17);
            g.DrawLine(p.P(c, 1.6f), r.X + 10, r.Y + 24, r.Right - 16, r.Y + 24);
        }

        // Kind buttons open a filtered picker
        void DrawConvertKinds(RectangleF row)
        {
            int n = ConvKinds.Length;
            float gap = 6, cw = (row.Width - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                string label = (string)ConvKinds[i][0];
                var kinds = (ConvKind[])ConvKinds[i][1];
                Pill(new RectangleF(row.X + i * (cw + gap), row.Y, cw, row.Height), "convKind" + i, Lang.T(label), PillKind.Secondary, delegate { PickConvertFiles(label, kinds); });
            }
        }

        void DrawConvertHistory(float w, float h)
        {
            float top = ConvListTop;
            p.Text(Lang.T("Terakhir"), Ink.Semi(12f), p.T.Sub, 24, top + 8, 200, StringAlignment.Near);
            if (convHistory.Count == 0)
            {
                p.Text(Lang.T("Hasil ubahan muncul di sini dan bisa diseret ke aplikasi lain"), Ink.Text(12.5f), p.T.Faint, 24, top + 36, w - 48, StringAlignment.Near);
                return;
            }
            var area = new RectangleF(10, top + 22, w - 20, ConvListView(h));
            var st = g.Save();
            g.IntersectClip(area);
            float y = area.Y - convScroll.Value;
            for (int i = 0; i < convHistory.Count; i++, y += ConvRowH)
            {
                if (y + ConvRowH < area.Top || y > area.Bottom) continue;
                var d = convHistory[i];
                var row = new RectangleF(area.X + 4, y + 2, area.Width - 8, ConvRowH - 4);
                string id = "convh" + d.Item.Id;
                bool hover = Hover(id);
                if (hover) p.FillRound(row, 14, Pressed(id) ? p.T.FillHover : p.T.Fill);
                float rcy = row.Y + row.Height / 2;
                DrawShelfThumb(d.Item, new RectangleF(row.X + 10, rcy - 17, 34, 34), 9);
                float tx = row.X + 56;
                p.Text(d.Item.Name, Ink.Semi(12.5f), p.T.Text, tx, rcy - 8, row.Right - tx - 14, StringAlignment.Near);
                p.Text(d.From + " → " + d.To + " · " + Fmt.Ago(d.Utc), Ink.Small(11f), p.T.Faint, tx, rcy + 9, row.Right - tx - 14, StringAlignment.Near);
                var pick = d;
                if (row.Top >= area.Top && row.Bottom <= area.Bottom)
                {
                    AddHit(row, id, delegate { OpenShelfItem(pick.Item); }, null, delegate { ConvertMenu(pick); });
                    if (Pressed(id)) ShelfMouseDown(pick.Item);
                }
            }
            g.Restore(st);
        }

        void ConvertMenu(ConvDone d)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(Lang.T("Buka"), null, delegate { OpenShelfItem(d.Item); });
            m.Items.Add(Lang.T("Tampilkan di folder"), null, delegate { RevealShelfItem(d.Item); });
            m.Items.Add(Lang.T("Salin jalur"), null, delegate { try { Clipboard.SetText(d.Item.Path); } catch (Exception) { } });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Lang.T("Hapus dari daftar"), null, delegate { ForgetConvert(d); });
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(Control.MousePosition);
        }

        // Old settings lack the new tab
        void AddConvertTab()
        {
            if (settings.GetBool("tabs.convert", false)) return;
            string list = settings.Get("panel.tabs", "");
            if (list.Length > 0 && Array.IndexOf(list.Split(','), "convert") < 0) settings.Set("panel.tabs", list + ",convert");
            settings.Set("tabs.convert", true);
            settings.Save();
        }
    }
}
