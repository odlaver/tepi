using System;
using System.Collections.Generic;
using System.Drawing;

namespace Tepi
{
    // Overview: card grid, most relevant first
    partial class Island
    {
        const float HomeCardH = 92, HomeMusicH = 76, HomeGap = 10;

        class Sender
        {
            public NotifItem Latest;
            public int Count;
        }

        enum HomeCard { Agents, Class, Messages, Timer, Shelf }

        bool HomeHasMedia { get { return mediaInfo.Present && mediaInfo.Title.Length > 0; } }

        // Warm JIT and fonts offscreen
        void WarmUp()
        {
            if (g == null || current == View.Panel) return;
            var keepG = g;
            int keepPage = page;
            bool keepHits = hitsOn;
            var keepT = p.T;
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
                        DrawPage(pg, PanelW, 340, now);
                    }
                    DrawAlbumLayout(PanelW, 340, now, 0);
                    DrawLyricsLayout(PanelW, 340, now, 0);
                    // Warm every font size used
                    const string sample = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 .,:;·…%+-'\"!?()";
                    foreach (var px in new[] { 12f, 12.5f, 13f, 13.5f, 14f, 14.5f, 15f, 15.5f, 17f, 18f, 20f, 22f, 26f, 34f }) p.Text(sample, Ink.Display(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 10.5f, 11f, 11.5f, 12f, 12.5f, 13f, 13.5f, 14f, 14.5f, 15f }) p.Text(sample, Ink.Semi(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 9.5f, 10.5f, 11f, 11.5f, 12f }) p.Text(sample, Ink.Small(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 11.5f, 12f, 12.5f, 13f, 13.5f, 14f, 15f }) p.Text(sample, Ink.Text(px), p.T.Text, 0, 20);
                    foreach (var px in new[] { 11.5f, 12f }) p.Text(sample, Ink.Mono(px), p.T.Text, 0, 20);
                    char[] glyphs = { Ink.IcoVol0, Ink.IcoVol1, Ink.IcoVol2, Ink.IcoVol3, Ink.IcoMute, Ink.IcoBolt, Ink.IcoWifi, Ink.IcoGlobe, Ink.IcoCam, Ink.IcoMic,
                                      Ink.IcoTerminal, Ink.IcoCalendar, Ink.IcoRefresh, Ink.IcoLock, Ink.IcoBell, '\uE74D', '\uE8D6', '' };
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
                tabSnap = true;
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

        // Cards with content, most pressing first
        List<HomeCard> HomeCards()
        {
            var list = new List<HomeCard>();
            if (approvals.Count > 0 || BusyCount() > 0) list.Add(HomeCard.Agents);
            if (TimerOn) list.Add(HomeCard.Timer);
            if (ClassCardOn()) list.Add(HomeCard.Class);
            if (notifs.Count > 0 && NotifOn) list.Add(HomeCard.Messages);
            if (!list.Contains(HomeCard.Agents) && sessions.Count > 0) list.Add(HomeCard.Agents);
            if (shelf.Count > 0) list.Add(HomeCard.Shelf);
            return list;
        }

        bool ClassCardOn()
        {
            var next = NextClass();
            return (next != null && (next.Start - DateTime.Now).TotalHours < 24) || (tasks != null && tasks.Count > 0);
        }

        // Least relevant cards drop instead of clipping
        List<HomeCard> FittingCards()
        {
            var list = HomeCards();
            float top = PanelTop + 4 + QuotaGlanceH() + (HomeHasMedia ? HomeMusicH + HomeGap : 0);
            int rows = Math.Max(0, (int)((MaxH - 4 - top + HomeGap) / (HomeCardH + HomeGap)));
            if (list.Count > rows * 2) list.RemoveRange(rows * 2, list.Count - rows * 2);
            return list;
        }

        float HomeHeight()
        {
            int cards = FittingCards().Count;
            float qg = QuotaGlanceH();
            if (cards == 0 && !HomeHasMedia) return PanelTop + 150 + qg;
            float h = PanelTop + 4 + qg + (HomeHasMedia ? HomeMusicH + HomeGap : 0);
            int rows = (cards + 1) / 2;
            return Math.Min(MaxH, h + rows * (HomeCardH + HomeGap) + 4);
        }

        void DrawHome(float w, float h, DateTime now)
        {
            var cards = FittingCards();
            float y = PanelTop + 4, x0 = 14, full = w - 28;
            int at = 0;
            float dy;
            float qg = QuotaGlanceH();
            if (qg > 0)
            {
                float a = RowIn(at++, now, out dy), keep = p.Alpha;
                p.Alpha = keep * a;
                DrawQuotaGlance(x0, y + dy, full);
                p.Alpha = keep;
                y += qg;
            }
            if (cards.Count == 0 && !HomeHasMedia) { DrawHomeHero(w, now, qg); return; }
            if (HomeHasMedia)
            {
                float a = RowIn(at++, now, out dy), keep = p.Alpha;
                p.Alpha = keep * a;
                DrawHomeMusic(new RectangleF(x0, y + dy, full, HomeMusicH), now);
                p.Alpha = keep;
                y += HomeMusicH + HomeGap;
            }
            float half = (full - HomeGap) / 2;
            for (int i = 0; i < cards.Count; i++)
            {
                // Odd one out spans the row
                bool wide = i == cards.Count - 1 && cards.Count % 2 == 1;
                var r = new RectangleF(x0 + (i % 2) * (half + HomeGap), y + (i / 2) * (HomeCardH + HomeGap), wide ? full : half, HomeCardH);
                float a = RowIn(at++, now, out dy), keep = p.Alpha;
                p.Alpha = keep * a;
                r.Offset(0, dy);
                DrawHomeCard(cards[i], r, now);
                p.Alpha = keep;
            }
        }

        // Nothing going on: the time, large
        void DrawHomeHero(float w, DateTime now, float shift)
        {
            var local = DateTime.Now;
            float cy = PanelTop + 52 + shift;
            p.TextCenter(Fmt.Hm(local), Ink.Display(34f), p.T.Text, w / 2, cy);
            p.TextCenter(local.ToString("dddd, d MMMM", Fmt.Id), Ink.Text(13f), p.T.Sub, w / 2, cy + 34);
            p.TextCenter(Lang.T("Tidak ada yang menunggu"), Ink.Small(11.5f), p.T.Faint, w / 2, cy + 56);
        }

        // Card shell: hover lifts, press sinks
        void CardBase(RectangleF r, string id, Action act)
        {
            AddHit(r, id, act);
            bool down = Pressed(id);
            if (down) r.Inflate(-1.5f, -1.5f);
            p.FillRound(r, 20, down ? p.T.FillHover : Hover(id) ? Ink.Mix(p.T.Fill, p.T.FillHover, 0.6f) : p.T.Fill);
        }

        void DrawHomeMusic(RectangleF r, DateTime now)
        {
            CardBase(r, "homeMusic", delegate { SetPage(PageMusic, false); });
            DrawCover(new RectangleF(r.X + 12, r.Y + 12, 52, 52), 12);
            float tx = r.X + 76, right = r.Right - 128;
            p.Text(mediaInfo.Title, Ink.Semi(14f), p.T.Text, tx, r.Y + 28, right - tx, StringAlignment.Near);
            p.Text(mediaInfo.Artist.Length > 0 ? mediaInfo.Artist : mediaInfo.App, Ink.Text(12f), p.T.Sub, tx, r.Y + 48, right - tx, StringAlignment.Near);
            // Transport on the right
            float cy = r.Y + 38, bx = r.Right - 112;
            var prev = new RectangleF(bx, cy - 15, 30, 30);
            var play = new RectangleF(bx + 36, cy - 18, 36, 36);
            var next = new RectangleF(bx + 78, cy - 15, 30, 30);
            DrawControl(prev, "homePrev", delegate { media.Previous(); }, (b, c) => p.Skip(b.X + b.Width / 2, b.Y + b.Height / 2, 11, c, false));
            AddHit(play, "homePlay", delegate { media.TogglePlay(); });
            bool down = Pressed("homePlay");
            p.Circle(play.X + 18, play.Y + 18, down ? 15.5f : Hover("homePlay") ? 18 : 17, p.T.Text);
            if (mediaInfo.Playing) p.Pause(play.X + 18, play.Y + 18, 11.5f, p.T.Bg);
            else p.Play(play.X + 19.5f, play.Y + 18, 12, p.T.Bg);
            DrawControl(next, "homeNext", delegate { media.Next(); }, (b, c) => p.Skip(b.X + b.Width / 2, b.Y + b.Height / 2, 11, c, true));
            // Thin progress along the bottom
            if (mediaInfo.DurationMs > 0)
            {
                var track = new RectangleF(tx, r.Bottom - 12, r.Right - 16 - tx, 3);
                LiveRect(track, ProgressFps(track.Width), t =>
                {
                    float f = Ease.Clamp01((float)(mediaInfo.Position(t) / mediaInfo.DurationMs));
                    p.FillRound(track, 1.5f, p.T.Line);
                    p.FillRound(new RectangleF(track.X, track.Y, Math.Max(3, track.Width * f), 3), 1.5f, p.T.Progress);
                });
            }
        }

        void DrawHomeCard(HomeCard kind, RectangleF r, DateTime now)
        {
            switch (kind)
            {
                case HomeCard.Agents: DrawAgentsCard(r, now); break;
                case HomeCard.Class: DrawClassCard(r); break;
                case HomeCard.Messages: DrawMessagesCard(r); break;
                case HomeCard.Timer: DrawTimerCard(r, now); break;
                case HomeCard.Shelf: DrawShelfCard(r); break;
            }
        }

        // Small caption at the card's top
        void CardCaption(RectangleF r, char glyph, string caption)
        {
            p.Icon(glyph, 12f, p.T.Sub, r.X + 22, r.Y + 22);
            p.Text(caption, Ink.Semi(11.5f), p.T.Sub, r.X + 34, r.Y + 22, r.Width - 48, StringAlignment.Near);
        }

        void DrawAgentsCard(RectangleF r, DateTime now)
        {
            CardBase(r, "homeAgents", delegate { SetPage(PageAgents, false); });
            var s = BusySession();
            if (s == null) { var list = SortedSessions(); s = list.Count > 0 ? list[0] : null; }
            int busy = BusyCount();
            CardCaption(r, Ink.IcoTerminal, approvals.Count > 0 ? Lang.T("Menunggu izin") : busy > 1 ? Lang.F("{0} agen bekerja", busy) : Lang.T("Agen"));
            if (s == null) return;
            DrawStateMark(s, r.X + 26, r.Y + 56);
            float tx = r.X + 46;
            p.Text(s.Project, Ink.Semi(13.5f), p.T.Text, tx, r.Y + 47, r.Right - tx - 14, StringAlignment.Near);
            string line = s.State == AgentState.Done && s.LastMessage.Length > 0 ? s.LastMessage : AgentLine(s);
            p.Text(line, Ink.Small(11.5f), p.T.Sub, tx, r.Y + 66, r.Right - tx - 14, StringAlignment.Near);
            DrawPlanBar(s, new RectangleF(tx, r.Bottom - 12, r.Right - tx - 14, 3));
        }

        void DrawClassCard(RectangleF r)
        {
            var next = NextClass();
            var task = tasks != null && tasks.Count > 0 ? tasks[0] : null;
            var now = DateTime.Now;
            bool classFirst = next != null && (next.Start - now).TotalHours < 12 && (task == null || next.Start.ToUniversalTime() < task.DeadlineUtc);
            if (classFirst)
            {
                CardBase(r, "homeClass", delegate { ShowSchedule(); });
                bool running = now >= next.Start;
                bool kuliaClass = next.Source == "kulia";
                CardCaption(r, Ink.IcoCalendar, running ? (kuliaClass ? Lang.T("Kelas berjalan") : Lang.T("Acara berjalan")) : kuliaClass ? Lang.T("Kelas berikutnya") : Lang.T("Acara berikutnya"));
                string big = running ? Lang.F("sampai {0}", Fmt.Hm(next.End)) : Fmt.Left(next.Start - now);
                p.Text(big, Ink.Display(20f), p.T.Text, r.X + 16, r.Y + 50, r.Width - 30, StringAlignment.Near);
                p.Text(next.Title + (next.Room.Length > 0 ? " · " + next.Room : ""), Ink.Small(11.5f), p.T.Sub, r.X + 16, r.Y + 72, r.Width - 30, StringAlignment.Near);
                return;
            }
            CardBase(r, "homeTask", delegate { ShowTasks(); });
            CardCaption(r, '', task.Kind == "ujian" ? Lang.T("Ujian terdekat") : Lang.T("Tenggat terdekat"));
            p.Text(Relative(task, DateTime.UtcNow), Ink.Display(20f), p.T.Text, r.X + 16, r.Y + 50, r.Width - 30, StringAlignment.Near);
            p.Text(task.Title, Ink.Small(11.5f), p.T.Sub, r.X + 16, r.Y + 72, r.Width - 30, StringAlignment.Near);
        }

        void DrawMessagesCard(RectangleF r)
        {
            CardBase(r, "homeMsgs", delegate { SetPage(PageMessages, false); });
            var senders = Senders();
            CardCaption(r, Ink.IcoBell, Lang.F("{0} pesan", notifs.Count));
            // Stacked app badges, newest on top
            int show = Math.Min(3, senders.Count);
            for (int i = show - 1; i >= 0; i--)
            {
                var b = new RectangleF(r.X + 14 + i * 16, r.Y + 38, 26, 26);
                p.Circle(b.X + 13, b.Y + 13, 14, p.T.Bg);
                DrawAppBadge(senders[i].Latest, b);
            }
            if (senders.Count == 0) return;
            var latest = senders[0].Latest;
            float tx = r.X + 14 + show * 16 + 18;
            p.Text(latest.Title.Length > 0 ? latest.Title : latest.AppName, Ink.Semi(12.5f), p.T.Text, tx, r.Y + 51, r.Right - tx - 12, StringAlignment.Near);
            p.Text(NotifBody(latest), Ink.Small(11.5f), p.T.Sub, r.X + 16, r.Y + 75, r.Width - 30, StringAlignment.Near);
        }

        void DrawTimerCard(RectangleF r, DateTime now)
        {
            CardBase(r, "homeTimer", delegate { SetPage(PageControl, false); });
            CardCaption(r, '', timerPaused ? TimerLabel() + Lang.T(" · dijeda") : TimerLabel());
            LiveRect(new RectangleF(r.X + 15, r.Y + 47, 26, 26), timerPaused ? 0 : TickFps, t => DrawTimerRing(r.X + 28, r.Y + 60, 11f));
            LiveTimer(Ink.Display(22f), timerPaused ? p.T.Sub : p.T.Text, r.X + 48, r.Y + 60, StringAlignment.Near, r.Right - r.X - 58);
        }

        void DrawShelfCard(RectangleF r)
        {
            CardBase(r, "homeShelf", delegate { shelfSeg = 0; shelfSegX.Snap(0); SetPage(PageShelf, false); });
            CardCaption(r, '', Lang.F("{0} berkas di rak", shelf.Count));
            int show = Math.Min(4, shelf.Count);
            for (int i = 0; i < show; i++) DrawShelfThumb(shelf[i], new RectangleF(r.X + 14 + i * 40, r.Y + 40, 34, 34), 9);
        }
    }
}
