using System;
using System.Collections.Generic;
using System.Drawing;

namespace Tepi
{
    // Overview tab: music, senders, deadlines; static
    partial class Island
    {
        const float HomeRowH = 40f;

        class Sender
        {
            public NotifItem Latest;
            public int Count;
        }

        bool HomeHasMedia { get { return mediaInfo.Present && mediaInfo.Title.Length > 0; } }

        // Warm JIT and fonts offscreen
        void WarmUp()
        {
            if (g == null || current == View.Panel) return;
            var keepG = g;
            int keepPage = page;
            bool keepHits = hitsOn;
            var keepT = p.T;
            float keepX = tabX.Value, keepW = tabW.Value;
            var now = DateTime.UtcNow;
            try
            {
                using (var bmp = new Bitmap(canvasW, canvasH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                using (var wg = Graphics.FromImage(bmp))
                {
                    wg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    wg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    wg.ScaleTransform(scale, scale);
                    g = wg;
                    p.G = wg;
                    hitsOn = false;
                    p.T = Ink.Base;
                    for (int pg = 0; pg < PageNames.Length; pg++)
                    {
                        page = pg;
                        DrawTabsLive(PanelW, now);
                        DrawPage(pg, PanelW, 340, now);
                    }
                    DrawAlbumLayout(PanelW, 340, now, 0);
                    DrawLyricsLayout(PanelW, 340, now, 0);
                    // Warm every font size used
                    const string sample = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 .,:;·…%+-'\"!?()";
                    foreach (var px in new[] { 12f, 12.5f, 13f, 13.5f, 14f, 14.5f, 15f, 15.5f, 17f, 18f, 20f, 22f }) p.Text(sample, Ink.Display(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 10.5f, 11f, 11.5f, 12f, 12.5f, 13f, 13.5f, 14f, 14.5f, 15f }) p.Text(sample, Ink.Semi(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 9.5f, 10.5f, 11f, 11.5f, 12f }) p.Text(sample, Ink.Small(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 11.5f, 12f, 12.5f, 13f, 13.5f, 14f, 15f }) p.Text(sample, Ink.Text(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 11.5f, 12f }) p.Text(sample, Ink.Mono(px), p.T.Text, 0, 20);
                    char[] glyphs = { Ink.IcoVol0, Ink.IcoVol1, Ink.IcoVol2, Ink.IcoVol3, Ink.IcoMute, Ink.IcoBolt, Ink.IcoWifi, Ink.IcoGlobe, Ink.IcoCam, Ink.IcoMic,
                                      Ink.IcoTerminal, Ink.IcoCalendar, Ink.IcoRefresh, Ink.IcoLock, Ink.IcoBell, '', '' };
                    foreach (var px in new[] { 10f, 12f, 13f, 14f, 15f, 16f, 20f })
                        foreach (var c in glyphs) p.Icon(c, px, p.T.Text, 20, 20);
                }
                SliceShadow(30, 1f);
            }
            catch (Exception ex) { Log.Write("warmup: " + ex.Message); }
            finally
            {
                g = keepG;
                p.G = keepG;
                page = keepPage;
                hitsOn = keepHits;
                p.T = keepT;
                tabX.Snap(keepX);
                tabW.Snap(keepW);
                snapTab = true;
            }
        }

        // Messages grouped by sender, newest first
        List<Sender> Senders()
        {
            var list = new List<Sender>();
            var index = new Dictionary<string, Sender>();
            foreach (var n in notifs)
            {
                string key = n.AppId + "|" + (n.Title.Length > 0 ? n.Title : n.AppName);
                Sender s;
                if (!index.TryGetValue(key, out s))
                {
                    s = new Sender { Latest = n };
                    index[key] = s;
                    list.Add(s);
                }
                s.Count++;
            }
            return list;
        }

        int HomeRows()
        {
            int msgs = Math.Min(4, Senders().Count), due = tasks != null ? Math.Min(4, tasks.Count) : 0;
            return Math.Max(1, Math.Max(msgs, due));
        }

        float HomeTop() { return PanelTop + 4 + (HomeHasMedia ? 68 : 0); }

        float HomeHeight() { return HomeTop() + 26 + HomeRows() * HomeRowH + 14; }

        void DrawHome(float w, float h, DateTime now)
        {
            if (HomeHasMedia) DrawHomeMusic(w);
            float top = HomeTop(), colW = (w - 16 * 2 - 12) / 2;
            DrawHomeMessages(new RectangleF(16, top, colW, h - top - 8));
            DrawHomeTasks(new RectangleF(16 + colW + 12, top, colW, h - top - 8));
        }

        void DrawHomeMusic(float w)
        {
            var strip = new RectangleF(16, PanelTop + 4, w - 32, 56);
            bool hover = Hover("homeMusic");
            p.FillRound(strip, 16, hover ? p.T.FillHover : p.T.Fill);
            DrawCover(new RectangleF(strip.X + 8, strip.Y + 8, 40, 40), 10);
            float tx = strip.X + 60, cy = strip.Y + strip.Height / 2;
            p.Text(mediaInfo.Title, Ink.Semi(13.5f), p.T.Text, tx, cy - 9, strip.Right - tx - 56, StringAlignment.Near);
            p.Text(mediaInfo.Artist, Ink.Small(11.5f), p.T.Sub, tx, cy + 10, strip.Right - tx - 56, StringAlignment.Near);
            AddHit(new RectangleF(strip.X, strip.Y, strip.Width - 52, strip.Height), "homeMusic", delegate { SetPage(PageMusic, false); });
            var btn = new RectangleF(strip.Right - 46, cy - 17, 34, 34);
            p.Circle(btn.X + 17, btn.Y + 17, Hover("homePlay") ? 17 : 15.5f, p.T.Text);
            if (mediaInfo.Playing) p.Pause(btn.X + 17, btn.Y + 17, 11, p.T.Bg);
            else p.Play(btn.X + 18.5f, btn.Y + 17, 12, p.T.Bg);
            AddHit(btn, "homePlay", delegate { media.TogglePlay(); });
        }

        void HomeHeader(RectangleF col, string label, string count, string id, int target)
        {
            var head = new RectangleF(col.X, col.Y, col.Width, 22);
            p.Text(label, Ink.Semi(11.5f), Hover(id) ? p.T.Text : p.T.Sub, col.X + 4, col.Y + 11, col.Width - 40, StringAlignment.Near);
            if (count.Length > 0) p.TextRight(count, Ink.Small(11f), p.T.Sub, col.Right - 4, col.Y + 11);
            AddHit(head, id, delegate { SetPage(target, false); });
        }

        void DrawHomeMessages(RectangleF col)
        {
            var senders = Senders();
            HomeHeader(col, Lang.T("Pesan"), senders.Count > 0 ? notifs.Count.ToString() : "", "homeMsgs", PageMessages);
            float y = col.Y + 26;
            if (!NotifOn || senders.Count == 0)
            {
                p.Text(NotifOn ? Lang.T("Tidak ada pesan baru") : Lang.T("Notifikasi mati"), Ink.Small(11.5f), p.T.Sub, col.X + 4, y + 14, col.Width - 8, StringAlignment.Near);
                return;
            }
            for (int i = 0; i < Math.Min(4, senders.Count); i++)
            {
                var s = senders[i];
                var row = new RectangleF(col.X, y + i * HomeRowH, col.Width, HomeRowH - 4);
                string id = "homeMsg" + i;
                if (Hover(id)) p.FillRound(row, 12, p.T.Fill);
                float cy = row.Y + row.Height / 2;
                DrawAppBadge(s.Latest, new RectangleF(row.X + 5, cy - 12, 24, 24));
                float tx = row.X + 36, right = row.Right - 6;
                string meta = s.Count > 1 ? s.Count.ToString() : Fmt.Ago(s.Latest.Time).Replace(" lalu", "");
                float mw = p.Measure(meta, Ink.Small(10.5f));
                if (s.Count > 1)
                {
                    var pill = new RectangleF(right - mw - 10, cy - 17, mw + 10, 16);
                    p.FillRound(pill, 8, p.T.Text);
                    p.TextCenter(meta, Ink.Semi(10.5f), p.T.Bg, pill.X + pill.Width / 2, pill.Y + 8);
                }
                else p.TextRight(meta, Ink.Small(10.5f), p.T.Sub, right, cy - 9);
                p.Text(s.Latest.Title.Length > 0 ? s.Latest.Title : s.Latest.AppName, Ink.Semi(12.5f), p.T.Text, tx, cy - 8, right - tx - mw - 14, StringAlignment.Near);
                p.Text(NotifBody(s.Latest), Ink.Small(11f), p.T.Sub, tx, cy + 9, right - tx, StringAlignment.Near);
                var open = s.Latest;
                AddHit(row, id, delegate { OpenNotifApp(open); });
            }
        }

        void DrawHomeTasks(RectangleF col)
        {
            HomeHeader(col, Lang.T("Tugas"), tasks != null && tasks.Count > 0 ? tasks.Count.ToString() : "", "homeTasks", PageTasks);
            float y = col.Y + 26;
            string empty = null;
            if (kulia == null || !kulia.SignedIn) empty = Lang.T("Kulia belum tersambung");
            else if (tasks == null) empty = tasksError ?? Lang.T("Memuat…");
            else if (tasks.Count == 0) empty = Lang.T("Tidak ada tugas aktif");
            if (empty != null)
            {
                p.Text(empty, Ink.Small(11.5f), p.T.Sub, col.X + 4, y + 14, col.Width - 8, StringAlignment.Near);
                return;
            }
            var nowUtc = DateTime.UtcNow;
            for (int i = 0; i < Math.Min(4, tasks.Count); i++)
            {
                var t = tasks[i];
                var row = new RectangleF(col.X, y + i * HomeRowH, col.Width, HomeRowH - 4);
                string id = "homeTask" + i;
                if (Hover(id)) p.FillRound(row, 12, p.T.Fill);
                float cy = row.Y + row.Height / 2;
                var local = t.DeadlineUtc.ToLocalTime();
                p.TextCenter(local.Day.ToString(), Ink.Display(14f), p.T.Text, row.X + 18, cy - 6);
                p.TextCenter(local.ToString("MMM", Fmt.Id), Ink.Small(9.5f), p.T.Sub, row.X + 18, cy + 9);
                float tx = row.X + 38, right = row.Right - 6;
                p.Text(t.Title, Ink.Semi(12.5f), p.T.Text, tx, cy - 8, right - tx, StringAlignment.Near);
                string when = Relative(t.DeadlineUtc, nowUtc);
                bool urgent = (t.DeadlineUtc - nowUtc).TotalHours < 24;
                var f = urgent ? Ink.Semi(11f) : Ink.Small(11f);
                p.Text(when + (t.Course.Length > 0 ? " · " + t.Course : ""), f, urgent ? p.T.Text : p.T.Sub, tx, cy + 9, right - tx, StringAlignment.Near);
                string url = t.Url;
                AddHit(row, id, delegate { OpenUrl(url); });
            }
        }
    }
}
