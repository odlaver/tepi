using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Tepi
{
    partial class Island
    {
        const int PageHome = 0, PageMusic = 1, PageAgents = 2, PageTasks = 3, PageMessages = 4, PageSchedule = 5, PageClips = 6;
        static readonly string[] PageNames = { "Ringkasan", "Musik", "Agen", "Tugas", "Pesan", "Jadwal", "Clipboard" };
        const float PanelTop = 54;
        float panelW = 488;
        float PanelW { get { return panelW; } }
        int[] tabs = { 0, 1, 2, 3, 4, 5, 6 };

        // Width, shift and tab order from settings
        void LoadLayout()
        {
            panelW = Clamp(settings.GetInt("panel.width", 488), 440, 560);
            shiftPct = Math.Max(-40, Math.Min(40, settings.GetInt("position", 0)));
            var list = new List<int>();
            foreach (var part in settings.Get("tabs", "0,1,2,3,4,5,6").Split(','))
            {
                int v;
                if (int.TryParse(part.Trim(), out v) && v >= 0 && v < PageNames.Length && !list.Contains(v)) list.Add(v);
            }
            if (list.Count == 0) list.Add(PageHome);
            tabs = list.ToArray();
            snapTab = true;
        }

        DateTime panelOpenedUtc, pageSwitchUtc;
        int prevPage;
        bool snapTab = true;
        readonly Spring tabX = new Spring(20, 420, 36, 0.05f), tabW = new Spring(64, 420, 36, 0.05f);
        readonly Spring agentScroll = new Spring(0, 300, 34, 0.2f), taskScroll = new Spring(0, 300, 34, 0.2f), notifScroll = new Spring(0, 300, 34, 0.2f);

        // Height follows each tab's content
        float PanelHeight()
        {
            switch (page)
            {
                case PageHome:
                    return HomeHeight();
                case PageMusic:
                    return MusicHeight();
                case PageAgents:
                    if (sessions.Count == 0 || (hub != null && hub.Error != null)) return 214;
                    return Clamp(PanelTop + 14 + sessions.Count * 56, 150, 360);
                case PageSchedule:
                    return ScheduleHeight();
                case PageClips:
                    return ClipsHeight();
                case PageTasks:
                    if (kulia == null || !kulia.Configured) return 214;
                    if (!kulia.SignedIn || (tasks == null && tasksError != null)) return 252;
                    if (tasks == null || tasks.Count == 0) return 214;
                    return Clamp(PanelTop + 14 + tasks.Count * 54, 150, 372);
                default:
                    if (!NotifOn || notifs.Count == 0) return 214;
                    if (notifAccess != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) return 252;
                    return Clamp(PanelTop + 14 + notifs.Count * 50, 150, 372);
            }
        }

        float MusicHeight() { return mediaInfo.Present && mediaInfo.Title.Length > 0 ? 340 : 214; }

        int DefaultPage(bool hover)
        {
            int want = settings.GetInt("panel.page", -1);
            return Array.IndexOf(tabs, want) >= 0 ? want : tabs[0];
        }

        void SetPage(int pg, bool snap)
        {
            if (pg != page)
            {
                prevPage = page;
                page = pg;
                pageSwitchUtc = DateTime.UtcNow;
            }
            if (snap) { snapTab = true; prevPage = pg; }
            if (pg == PageMessages) unread = 0;
            if (pg == PageTasks && kulia != null && kulia.SignedIn && (DateTime.UtcNow - tasksAtUtc).TotalMinutes > 2) RefreshTasks(false);
            Kick();
        }

        void StepPanel(float dt)
        {
            tabX.Step(dt); tabW.Step(dt);
            agentScroll.Step(dt); taskScroll.Step(dt); notifScroll.Step(dt); plainScroll.Step(dt); scheduleScroll.Step(dt);
            StepMusic(dt);
        }

        bool PanelMoving()
        {
            return !tabX.Settled || !tabW.Settled || !agentScroll.Settled || !taskScroll.Settled || !notifScroll.Settled || !plainScroll.Settled || !scheduleScroll.Settled ||
                   (DateTime.UtcNow - pageSwitchUtc).TotalSeconds < 0.4 || MusicMoving(DateTime.UtcNow);
        }

        bool ScrollPanel(int delta)
        {
            Spring s; int count; float rowH;
            if (page == PageAgents) { s = agentScroll; count = sessions.Count; rowH = 56; }
            else if (page == PageTasks) { s = taskScroll; count = tasks != null ? tasks.Count : 0; rowH = 54; }
            else if (page == PageMessages) { s = notifScroll; count = notifs.Count; rowH = 50; }
            else if (page == PageClips) { s = clipScroll; count = clips.Count; rowH = ClipRowH; }
            else if (page == PageSchedule)
            {
                float maxSchedule = Math.Max(0, ScheduleContentH() - (PanelHeight() - PanelTop - 20));
                scheduleScroll.Target = Math.Max(0, Math.Min(maxSchedule, scheduleScroll.Target - delta / 120f * ClassRowH * 1.5f));
                Kick();
                return true;
            }
            else if (page == PageMusic && lyrics != null && lyrics.State == LyricState.Plain && lyrics.Key == mediaInfo.Key)
            {
                float maxPlain = Math.Max(0, lyrics.Lines.Count * PlainLineH - 150);
                plainScroll.Target = Math.Max(0, Math.Min(maxPlain, plainScroll.Target - delta / 120f * PlainLineH * 3));
                Kick();
                return true;
            }
            else return false;
            float max = Math.Max(0, count * rowH - (PanelHeight() - PanelTop - 20));
            s.Target = Math.Max(0, Math.Min(max, s.Target - delta / 120f * rowH * 1.5f));
            Kick();
            return true;
        }

        void DrawPanel(float w, float h, DateTime now)
        {
            float k = Ease.OutCubic((float)((now - pageSwitchUtc).TotalSeconds / 0.34));
            float musicVis = BackdropShown(now) ? 1 : 0;
            double t0 = Prof.T();
            if (mediaInfo.Present) DrawBackdrop(musicVis);
            Prof.Add("backdrop", t0);
            t0 = Prof.T();
            DrawTabs(w, now);
            Prof.Add("tabs", t0);
            t0 = Prof.T();
            int dir = Math.Sign(page - prevPage);
            var area = new RectangleF(0, PanelTop - 6, w, h - PanelTop + 6);
            var st = g.Save();
            g.IntersectClip(area);
            float alpha = p.Alpha;
            // Outgoing page, skipped once it has faded out
            float outAlpha = alpha * (1 - Ease.Clamp01(k * 2.2f));
            if (k < 1 && prevPage != page && outAlpha >= 0.04f)
            {
                p.Alpha = outAlpha;
                var s1 = g.Save();
                g.TranslateTransform(-dir * k * 30, 0);
                bool hold = hitsOn;
                hitsOn = false;
                DrawPage(prevPage, w, h, now);
                hitsOn = hold;
                g.Restore(s1);
            }
            p.Alpha = alpha * Ease.Clamp01(k * 1.3f);
            var s2 = g.Save();
            g.TranslateTransform(dir * (1 - k) * 30, 0);
            if (k < 0.85f) hitsOn = false;
            DrawPage(page, w, h, now);
            g.Restore(s2);
            p.Alpha = alpha;
            g.Restore(st);
            Prof.Add("page", t0);
        }

        // Cover backdrop swaps at mid-switch, hidden by the content fade;
        // a per-frame alpha fade of it cost ~5 ms
        bool BackdropShown(DateTime now)
        {
            bool mid = (now - pageSwitchUtc).TotalSeconds >= 0.34 * 0.45;
            if (page == PageMusic) return prevPage == PageMusic || mid;
            return prevPage == PageMusic && !mid;
        }

        void DrawPage(int pg, float w, float h, DateTime now)
        {
            switch (pg)
            {
                case PageHome: DrawHome(w, h, now); break;
                case PageMusic: DrawMusicPage(w, h, now); break;
                case PageAgents: DrawAgentsPage(w, h, now); break;
                case PageTasks: DrawTasksPage(w, h, now); break;
                case PageMessages: DrawMessagesPage(w, h); break;
                case PageSchedule: DrawSchedulePage(w, h, now); break;
                case PageClips: DrawClipsPage(w, h, now); break;
            }
        }

        const float TabSize = 44, TabH = 32, TabGap = 6, TabY = 12;
        void DrawTabs(float w, DateTime now) { DrawTabsLive(w, now); }

        void DrawTabsLive(float w, DateTime now)
        {
            int n = tabs.Length, at = Array.IndexOf(tabs, page);
            // Narrower tabs keep clear of the action button
            float size = Math.Min(TabSize, (w - 116 - (n - 1) * TabGap) / n);
            float total = n * size + (n - 1) * TabGap, x0 = w / 2 - total / 2;
            var rects = new RectangleF[n];
            for (int i = 0; i < n; i++) rects[i] = new RectangleF(x0 + i * (size + TabGap), TabY, size, TabH);

            // Liquid pill: leading edge first
            var target = rects[Math.Max(0, at)];
            bool right = target.X > tabX.Value + 0.5f;
            Tune(tabW, right ? 560 : 230, 0.8f);
            Tune(tabX, right ? 230 : 560, 0.8f);
            tabX.Target = target.X;
            tabW.Target = target.Right;
            if (snapTab) { tabX.Snap(tabX.Target); tabW.Snap(tabW.Target); snapTab = false; }
            var pill = RectangleF.FromLTRB(tabX.Value, TabY, Math.Max(tabX.Value + TabH, tabW.Value), TabY + TabH);

            for (int i = 0; i < n; i++)
            {
                var r = rects[i];
                int to = tabs[i];
                if (Hover("tab" + to) && to != page) p.FillRound(r, TabH / 2, p.T.Fill);
                DrawTabIcon(to, r.X + r.Width / 2, r.Y + r.Height / 2, Hover("tab" + to) ? p.T.Text : p.T.Sub);
                AddHit(r, "tab" + to, delegate { SetPage(to, false); });
            }
            // Hidden tab opened by a link: no pill
            if (at >= 0)
            {
                p.FillRound(pill, TabH / 2, p.T.Accent);
                if (tabX.Settled && tabW.Settled) DrawTabIcon(page, rects[at].X + rects[at].Width / 2, rects[at].Y + rects[at].Height / 2, p.T.OnAccent);
                else
                {
                    // Icons under the sliding pill flip colour
                    var st = g.Save();
                    using (var clip = Ink.Round(pill, TabH / 2)) g.SetClip(clip, CombineMode.Intersect);
                    for (int i = 0; i < n; i++) DrawTabIcon(tabs[i], rects[i].X + rects[i].Width / 2, rects[i].Y + rects[i].Height / 2, p.T.OnAccent);
                    g.Restore(st);
                }
            }
            for (int i = 0; i < n; i++)
            {
                int pg = tabs[i];
                bool dot = (pg == PageMessages && unread > 0) || (pg == PageAgents && approvals.Count + BusyCount() > 0 && page != PageAgents);
                if (!dot) continue;
                p.Circle(rects[i].Right - 9, rects[i].Y + 6, 4.2f, p.T.Bg);
                p.Circle(rects[i].Right - 9, rects[i].Y + 6, 3f, p.T.Text);
            }
            DrawTabAction(w);
        }

        void DrawTabIcon(int i, float cx, float cy, Color c)
        {
            var pen = p.P(c, 1.7f);
            switch (i)
            {
                case PageHome:
                    // Overview: a 2x2 grid of tiles
                    for (int gy = 0; gy < 2; gy++)
                        for (int gx = 0; gx < 2; gx++)
                            using (var tile = Ink.Round(new RectangleF(cx - 7.5f + gx * 8.5f, cy - 7.5f + gy * 8.5f, 6.5f, 6.5f), 2f)) g.DrawPath(p.P(c, 1.5f), tile);
                    break;
                case PageSchedule:
                    p.Icon(Ink.IcoCalendar, 15f, c, cx, cy);
                    break;
                case PageClips:
                    p.Icon('\uE77F', 15f, c, cx, cy);
                    break;
                case PageMusic:
                    g.DrawLine(pen, cx - 3.5f, cy + 4.5f, cx - 3.5f, cy - 5.5f);
                    g.DrawLine(pen, cx + 5.5f, cy + 3f, cx + 5.5f, cy - 7f);
                    using (var beam = new GraphicsPath())
                    {
                        beam.AddPolygon(new[] { new PointF(cx - 4.3f, cy - 5.6f), new PointF(cx + 6.3f, cy - 7.4f), new PointF(cx + 6.3f, cy - 4.4f), new PointF(cx - 4.3f, cy - 2.6f) });
                        g.FillPath(p.B(c), beam);
                    }
                    g.FillEllipse(p.B(c), cx - 8.6f, cy + 2.4f, 5.6f, 4.4f);
                    g.FillEllipse(p.B(c), cx + 0.4f, cy + 0.9f, 5.6f, 4.4f);
                    break;
                case PageAgents:
                    // Spark turns while an agent works
                    float spin = BusyCount() > 0 ? orbitPhase * 360f : 0f;
                    for (int k = 0; k < 8; k++)
                    {
                        double a = (spin + k * 45) * Math.PI / 180;
                        float len = k % 2 == 0 ? 8f : 5.2f;
                        g.DrawLine(p.P(c, 2f), cx + (float)Math.Cos(a) * 1.6f, cy + (float)Math.Sin(a) * 1.6f, cx + (float)Math.Cos(a) * len, cy + (float)Math.Sin(a) * len);
                    }
                    break;
                case PageTasks:
                    using (var box = Ink.Round(new RectangleF(cx - 7.5f, cy - 7.5f, 15, 15), 4.5f)) g.DrawPath(pen, box);
                    p.Check(cx + 0.3f, cy + 0.6f, 9.5f, c, 1);
                    break;
                default:
                    using (var bubble = Ink.Round(new RectangleF(cx - 8.5f, cy - 7f, 17, 12.5f), 4.6f)) g.DrawPath(pen, bubble);
                    g.DrawLines(pen, new[] { new PointF(cx - 5.5f, cy + 5.4f), new PointF(cx - 6.8f, cy + 9f), new PointF(cx - 1.8f, cy + 5.4f) });
                    for (int d = -1; d <= 1; d++) p.Circle(cx + d * 3.6f, cy - 0.8f, 1.15f, c);
                    break;
            }
        }

        // Contextual action right of tabs
        void DrawTabAction(float w)
        {
            var r = new RectangleF(w - 52, TabY, 34, TabH);
            if (page == PageMusic && mediaInfo.Present && mediaInfo.Title.Length > 0)
            {
                bool album = AlbumMode();
                if (Hover("musicView")) p.FillRound(r, TabH / 2, p.T.Fill);
                Color c = Hover("musicView") ? p.T.Text : p.T.Sub;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                if (album)
                {
                    // Lyrics glyph: ragged text lines
                    var pen = p.P(c, 1.7f);
                    g.DrawLine(pen, cx - 7, cy - 5, cx + 7, cy - 5);
                    g.DrawLine(pen, cx - 7, cy, cx + 3, cy);
                    g.DrawLine(pen, cx - 7, cy + 5, cx + 5.5f, cy + 5);
                }
                else
                {
                    // Record glyph
                    g.DrawEllipse(p.P(c, 1.7f), cx - 8, cy - 8, 16, 16);
                    g.DrawArc(p.P(c, 1.2f), cx - 5, cy - 5, 10, 10, 200, 70);
                    p.Circle(cx, cy, 2f, c);
                }
                AddHit(r, "musicView", delegate { ToggleMusicView(); });
            }
            else if (page == PageTasks && kulia != null && kulia.SignedIn)
            {
                if (tasksLoading) p.Orbit(r.X + 17, r.Y + 16, 6.5f, orbitPhase, p.T.Sub);
                else
                {
                    if (Hover("refresh")) p.FillRound(r, TabH / 2, p.T.Fill);
                    p.Icon(Ink.IcoRefresh, 13f, Hover("refresh") ? p.T.Text : p.T.Sub, r.X + 17, r.Y + 16);
                    AddHit(r, "refresh", delegate { RefreshTasks(true); });
                }
            }
            else if (page == PageMessages && notifs.Count > 0)
            {
                if (Hover("clearAll")) p.FillRound(r, TabH / 2, p.T.Fill);
                p.Icon('', 13f, Hover("clearAll") ? p.T.Text : p.T.Sub, r.X + 17, r.Y + 16);
                AddHit(r, "clearAll", delegate { notifScroll.Snap(0); RemoveNotifs(new List<NotifItem>(notifs)); });
            }
            else if (page == PageClips && clips.Count > 0)
            {
                if (Hover("clearClips")) p.FillRound(r, TabH / 2, p.T.Fill);
                p.Icon('\uE74D', 13f, Hover("clearClips") ? p.T.Text : p.T.Sub, r.X + 17, r.Y + 16);
                AddHit(r, "clearClips", delegate { clipScroll.Snap(0); clips.Clear(); Kick(); });
            }
        }

        // Empty state; returns button top
        float Empty(float w, char glyph, string title, string detail)
        {
            float top = PanelTop + 20;
            p.Icon(glyph, 20f, p.T.Sub, w / 2, top + 12);
            p.TextCenter(title, Ink.Semi(15f), p.T.Text, w / 2, top + 44);
            p.TextBox(detail, Ink.Text(12.5f), p.T.Sub, new RectangleF(48, top + 58, w - 96, 38), StringAlignment.Center);
            return top + 104;
        }

        void Button(RectangleF r, string id, string label, bool filled, Action act)
        {
            bool hover = Hover(id);
            if (filled)
            {
                p.FillRound(r, 10, hover ? Ink.A(p.T.Text, 0.82f) : p.T.Text);
                p.TextCenter(label, Ink.Semi(13f), p.T.Bg, r.X + r.Width / 2, r.Y + r.Height / 2);
            }
            else
            {
                if (hover) p.FillRound(r, 10, p.T.Fill);
                p.StrokeRound(r, 10, p.T.Sub, 1.2f);
                p.TextCenter(label, Ink.Semi(13f), p.T.Text, r.X + r.Width / 2, r.Y + r.Height / 2);
            }
            AddHit(r, id, act);
        }

        // Music

        void DrawControl(RectangleF r, string id, Action act, Action<RectangleF, Color> glyph)
        {
            if (Hover(id)) p.Circle(r.X + r.Width / 2, r.Y + r.Height / 2, r.Width / 2, p.T.Fill);
            glyph(r, p.T.Text);
            AddHit(r, id, act);
        }

        // Scrolling lyrics; current line sweeps
        void DrawTeleprompter(RectangleF box, DateTime now)
        {
            if (lyrics == null || lyrics.Key != mediaInfo.Key || lyrics.State == LyricState.Loading)
            {
                p.Orbit(box.X + 8, box.Y + 26, 6f, orbitPhase, p.T.Sub);
                p.Text("Mencari lirik…", Ink.Text(12.5f), p.T.Sub, box.X + 22, box.Y + 26, box.Width - 22, StringAlignment.Near);
                return;
            }
            if (lyrics.State == LyricState.Plain) { DrawPlainLyrics(box); return; }
            if (lyrics.State != LyricState.Synced)
            {
                string msg = lyrics.State == LyricState.Failed ? "LRCLIB tidak terjangkau · dicoba lagi otomatis" : !settings.GetBool("lyrics.online", true) ? "Pencarian lirik online dimatikan" : "Lirik tidak ditemukan di LRCLIB";
                p.TextBox(msg, Ink.Text(12.5f), p.T.Sub, new RectangleF(box.X, box.Y + 16, box.Width, 40));
                return;
            }
            var st = g.Save();
            g.IntersectClip(box);
            double pos = mediaInfo.Position(now);
            float anchor = box.Y + 34;
            float alpha = p.Alpha;
            int first = Math.Max(0, (int)(lyricScroll.Value / LyricLineH) - 2);
            for (int i = first; i < lyrics.Lines.Count; i++)
            {
                float d = i * LyricLineH - lyricScroll.Value;
                float y = anchor + d;
                if (y > box.Bottom + LyricLineH) break;
                if (y < box.Top - LyricLineH) continue;
                var line = lyrics.Lines[i];
                if (line.Text.Length == 0) continue;
                float edge = Ease.Clamp01((y - box.Top - 4) / 24f) * Ease.Clamp01((box.Bottom - y - 4) / 24f);
                p.Alpha = alpha * edge;
                if (i == lyricIndex)
                {
                    float prog = (float)((pos - line.AtMs) / Math.Max(1, lyrics.SungUntil(i) - line.AtMs));
                    // Long lines shrink to fit
                    var big = Ink.Display(15.5f);
                    float mw = p.Measure(line.Text, big);
                    var font = mw <= box.Width ? big : Ink.Display(Math.Max(12f, (float)Math.Floor(15.5f * box.Width / mw * 2) / 2));
                    p.Sweep(line.Text, font, p.T.Text, p.T.Faint, box.X, y, box.Width, StringAlignment.Near, Ease.Clamp01(prog));
                }
                else p.Text(line.Text, Ink.Text(13.5f), i < lyricIndex ? p.T.Faint : p.T.Sub, box.X, y, box.Width, StringAlignment.Near);
                if (mediaInfo.CanSeek && y > box.Top && y < box.Bottom)
                {
                    double at = line.AtMs;
                    AddHit(new RectangleF(box.X, y - LyricLineH / 2, box.Width, LyricLineH), "ly" + i, delegate { media.Seek(at + 50); });
                }
            }
            p.Alpha = alpha;
            g.Restore(st);
        }

        readonly Spring plainScroll = new Spring(0, 300, 34, 0.2f);
        string plainKey;
        const float PlainLineH = 22f;

        // Untimed lyrics, wheel-scrolled
        void DrawPlainLyrics(RectangleF box)
        {
            if (plainKey != lyrics.Key) { plainKey = lyrics.Key; plainScroll.Snap(0); }
            p.Text("Lirik tanpa waktu",Ink.Small(11f), p.T.Faint, box.X, box.Y + 8, box.Width, StringAlignment.Near);
            var area = new RectangleF(box.X, box.Y + 20, box.Width, box.Height - 20);
            var st = g.Save();
            g.IntersectClip(area);
            float alpha = p.Alpha;
            for (int i = 0; i < lyrics.Lines.Count; i++)
            {
                float y = area.Y + 12 + i * PlainLineH - plainScroll.Value;
                if (y < area.Top - PlainLineH) continue;
                if (y > area.Bottom + PlainLineH) break;
                float edge = Ease.Clamp01((y - area.Top) / 18f) * Ease.Clamp01((area.Bottom - y) / 18f);
                p.Alpha = alpha * edge;
                p.Text(lyrics.Lines[i].Text, Ink.Text(13f), p.T.Sub, area.X, y, area.Width, StringAlignment.Near);
            }
            p.Alpha = alpha;
            g.Restore(st);
        }

        // Agents

        void DrawAgentsPage(float w, float h, DateTime now)
        {
            if (hub != null && hub.Error != null)
            {
                Empty(w, Ink.IcoTerminal, "Server hook mati", hub.Error + " · agent.port di settings.ini");
                return;
            }
            var list = SortedSessions();
            if (list.Count == 0)
            {
                Empty(w, Ink.IcoTerminal, "Belum ada sesi agen", linked.Length > 0 ? "Terhubung: " + linked : "Belum terhubung · tray > Hubungkan agen");
                return;
            }
            const float rowH = 56;
            var area = new RectangleF(10, PanelTop + 4, w - 20, h - PanelTop - 14);
            var st = g.Save();
            g.IntersectClip(area);
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                float y = area.Y + i * rowH - agentScroll.Value;
                if (y + rowH < area.Top || y > area.Bottom) continue;
                float cy = y + rowH / 2;
                var row = new RectangleF(14, y + 3, w - 28, rowH - 6);
                string rowId = "agent" + i;
                if (Hover(rowId)) p.FillRound(row, 14, p.T.Fill);
                var session = s;
                AddHit(row, rowId, delegate { if (!FocusTerminal(session)) ShowInfo(Ink.IcoTerminal, "Terminal tidak ditemukan", session.Project, true); });
                DrawStateMark(s, 36, cy);
                var semi = Ink.Semi(13.5f);
                p.Text(s.Project, semi, p.T.Text, 60, cy - 10, 220, StringAlignment.Near);
                if (s.Profile.Length > 0)
                {
                    float px = 60 + Math.Min(220, p.Measure(s.Project, semi)) + 8;
                    p.Text(s.Profile, Ink.Small(11f), p.T.Sub, px, cy - 9.5f, 120, StringAlignment.Near);
                }
                p.TextRight(StateWord(s), Ink.Small(11f), p.T.Sub, w - 26, cy - 10);
                string meta = Summary(s, DateTime.UtcNow);
                var mf = Ink.Small(11f);
                float lineW = w - 60 - 26 - p.Measure(meta, mf) - 14;
                p.TextRight(meta, mf, p.T.Faint, w - 26, cy + 10);
                if ((s.State == AgentState.Done || s.State == AgentState.Ended) && s.LastMessage.Length > 0)
                    p.Text(s.LastMessage, Ink.Text(12f), p.T.Sub, 60, cy + 10, lineW, StringAlignment.Near);
                else
                    p.Text(AgentLine(s), Ink.Mono(11.5f), p.T.Sub, 60, cy + 10, lineW, StringAlignment.Near);
                if (i < list.Count - 1) g.DrawLine(p.P(p.T.Line, 1f), 60, y + rowH - 0.5f, w - 26, y + rowH - 0.5f);
            }
            g.Restore(st);
        }

        void DrawStateMark(AgentSession s, float cx, float cy)
        {
            switch (s.State)
            {
                case AgentState.Working: p.Orbit(cx, cy, 8f, orbitPhase, p.T.Text); break;
                case AgentState.Waiting: p.Icon(Ink.IcoTerminal, 14f, p.T.Text, cx, cy); break;
                case AgentState.Done:
                    p.Circle(cx, cy, 10, p.T.Text);
                    if (s.Failed) p.Cross(cx, cy, 9, p.T.Bg);
                    else p.Check(cx, cy, 10, p.T.Bg, 1);
                    break;
                case AgentState.Ready: g.DrawEllipse(p.P(p.T.Sub, 1.6f), cx - 8, cy - 8, 16, 16); break;
                default: g.DrawLine(p.P(p.T.Faint, 2f), cx - 6, cy, cx + 6, cy); break;
            }
        }

        static string StateWord(AgentSession s)
        {
            switch (s.State)
            {
                case AgentState.Working: return "bekerja · " + Duration(DateTime.UtcNow - (s.TurnStartedUtc > DateTime.MinValue ? s.TurnStartedUtc : s.StartedUtc));
                case AgentState.Waiting: return "menunggu izin";
                case AgentState.Done: return "selesai · " + Fmt.Ago(s.LastUtc);
                case AgentState.Ready: return "siap";
            }
            return "ditutup · " + Fmt.Ago(s.LastUtc);
        }

        // Kulia

        void DrawTasksPage(float w, float h, DateTime now)
        {
            if (kulia == null || !kulia.Configured)
            {
                Empty(w, Ink.IcoCalendar, "Kulia belum diatur", "kulia.url dan kulia.key kosong di settings.ini");
                return;
            }
            if (!kulia.SignedIn)
            {
                float by = Empty(w, Ink.IcoCalendar, "Kulia belum tersambung", "Masuk pakai email akun kulia.live");
                Button(new RectangleF(w / 2 - 74, by, 148, 34), "login", "Masuk ke Kulia", true, delegate { OpenLogin(); });
                return;
            }
            if (tasks == null)
            {
                if (tasksError != null)
                {
                    float ry = Empty(w, Ink.IcoCalendar, "Tugas gagal dimuat", tasksError);
                    Button(new RectangleF(w / 2 - 60, ry, 120, 34), "retry", "Coba lagi", false, delegate { RefreshTasks(false); });
                }
                else
                {
                    float cy = PanelTop + (h - PanelTop) / 2 - 6;
                    p.Orbit(w / 2 - 74, cy, 7f, orbitPhase, p.T.Sub);
                    p.Text("Memuat tugas dari Kulia…", Ink.Text(13f), p.T.Sub, w / 2 - 60, cy, 200, StringAlignment.Near);
                }
                return;
            }
            if (tasks.Count == 0)
            {
                Empty(w, Ink.IcoCalendar, "Tidak ada tugas aktif", "Diperbarui tiap 10 menit");
                return;
            }
            if (tasksError != null) p.TextRight("data " + Fmt.Ago(tasksAtUtc), Ink.Small(11f), p.T.Sub, w - 58, 28);

            const float rowH = 54;
            var area = new RectangleF(10, PanelTop + 2, w - 20, h - PanelTop - 12);
            var st = g.Save();
            g.IntersectClip(area);
            var nowUtc = DateTime.UtcNow;
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                float y = area.Y + i * rowH - taskScroll.Value;
                if (y + rowH < area.Top || y > area.Bottom) continue;
                var row = new RectangleF(14, y + 2, w - 28, rowH - 4);
                string rowId = "task" + i, doneId = "taskDone" + i;
                bool hover = Hover(rowId) || Hover(doneId);
                if (hover) p.FillRound(row, 14, p.T.Fill);
                float cy = row.Y + row.Height / 2;
                var local = t.DeadlineUtc.ToLocalTime();
                p.TextCenter(local.Day.ToString(), Ink.Display(17f), p.T.Text, 44, cy - 6);
                p.TextCenter(local.ToString("MMM", Fmt.Id), Ink.Small(10.5f), p.T.Sub, 44, cy + 12);
                float tx = 74, right = row.Right - 12;
                var left = t.DeadlineUtc - nowUtc;
                string when = Relative(t.DeadlineUtc, nowUtc);
                bool urgent = left.TotalHours < 24;
                var wf = Ink.Semi(11.5f);
                float ww = p.Measure(when, wf) + (urgent ? 18 : 0);
                var btn = new RectangleF(right - 116, cy - 15, 116, 30);
                if (hover)
                {
                    if (Hover(doneId)) p.FillRound(btn, 10, p.T.FillHover);
                    p.StrokeRound(btn, 10, p.T.Sub, 1.2f);
                    p.TextCenter("Tandai selesai", Ink.Semi(12f), p.T.Text, btn.X + btn.Width / 2, cy);
                    ww = 116;
                }
                else if (urgent)
                {
                    // Inverted label: due within a day
                    var pill = new RectangleF(right - ww, cy - 11, ww, 22);
                    p.FillRound(pill, 11, p.T.Text);
                    p.TextCenter(when, wf, p.T.Bg, pill.X + pill.Width / 2, cy);
                }
                else p.TextRight(when, wf, p.T.Sub, right, cy);
                string course = t.Course + (t.Kind == "ujian" ? " · ujian" : "") + (t.Status == "proses" ? " · dikerjakan" : "");
                p.Text(course, Ink.Small(11f), p.T.Sub, tx, cy - 10, right - ww - tx - 12, StringAlignment.Near);
                p.Text(t.Title, Ink.Semi(13f), p.T.Text, tx, cy + 9, right - ww - tx - 12, StringAlignment.Near);
                string url = t.Url;
                AddHit(row, rowId, delegate { OpenUrl(url); });
                var task = t;
                AddHit(btn, doneId, delegate { MarkDone(task); });
            }
            g.Restore(st);
        }

        static string Relative(DateTime dueUtc, DateTime nowUtc)
        {
            var left = dueUtc - nowUtc;
            var local = dueUtc.ToLocalTime();
            if (left.TotalMinutes < 0) return "lewat " + Fmt.Ago(dueUtc).Replace(" lalu", "");
            if (left.TotalMinutes < 60) return (int)Math.Max(1, left.TotalMinutes) + " menit lagi";
            if (left.TotalHours < 12) return (int)left.TotalHours + " jam lagi";
            var today = DateTime.Now.Date;
            if (local.Date == today) return "hari ini " + Fmt.Hm(local);
            if (local.Date == today.AddDays(1)) return "besok " + Fmt.Hm(local);
            if ((local.Date - today).TotalDays < 7) return local.ToString("ddd", Fmt.Id) + " " + Fmt.Hm(local);
            return local.ToString("d MMM", Fmt.Id);
        }

        // Messages

        void DrawMessagesPage(float w, float h)
        {
            if (!NotifOn)
            {
                Empty(w, Ink.IcoBell, "Notifikasi pesan dimatikan", "Saklarnya ada di menu klik kanan");
                return;
            }
            if (notifAccess != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
            {
                float ny = Empty(w, Ink.IcoLock, "Akses notifikasi ditolak", "Pengaturan Windows > Privasi > Notifikasi");
                Button(new RectangleF(w / 2 - 80, ny, 160, 34), "notifPerm", "Buka pengaturan", false, delegate
                {
                    try { Process.Start("ms-settings:privacy-notifications"); } catch (Exception ex) { Log.Write("settings: " + ex.Message); }
                });
                return;
            }
            if (notifs.Count == 0)
            {
                Empty(w, Ink.IcoBell, "Belum ada pesan", "WhatsApp, Telegram, Discord, Teams, email");
                return;
            }
            const float rowH = 50;
            var area = new RectangleF(10, PanelTop + 2, w - 20, h - PanelTop - 12);
            var st = g.Save();
            g.IntersectClip(area);
            for (int i = 0; i < notifs.Count; i++)
            {
                var item = notifs[i];
                float y = area.Y + i * rowH - notifScroll.Value;
                if (y + rowH < area.Top || y > area.Bottom) continue;
                var row = new RectangleF(14, y + 2, w - 28, rowH - 4);
                string id = "notif" + i, del = "notifDel" + i;
                bool hover = Hover(id) || Hover(del);
                if (hover) p.FillRound(row, 14, p.T.Fill);
                float cy = row.Y + row.Height / 2;
                DrawAppBadge(item, new RectangleF(row.X + 8, cy - 16, 32, 32));
                float tx = row.X + 50;
                p.Text(item.Title.Length > 0 ? item.Title : item.AppName, Ink.Semi(12.5f), p.T.Text, tx, cy - 9, row.Right - tx - 80, StringAlignment.Near);
                p.Text(NotifBody(item), Ink.Text(11.5f), p.T.Sub, tx, cy + 9, row.Right - tx - 44, StringAlignment.Near);
                var x = new RectangleF(row.Right - 36, cy - 14, 28, 28);
                if (hover)
                {
                    p.Circle(x.X + 14, x.Y + 14, 12, Hover(del) ? p.T.FillHover : p.T.Fill);
                    p.Cross(x.X + 14, x.Y + 14, 10, p.T.Sub);
                }
                else p.TextRight(Fmt.Ago(item.Time), Ink.Small(11f), p.T.Sub, row.Right - 12, cy - 9);
                var open = item;
                AddHit(row, id, delegate { OpenNotifApp(open); });
                // Added last: wins over the row
                var victim = item;
                AddHit(x, del, delegate { RemoveNotifs(new List<NotifItem> { victim }); });
            }
            g.Restore(st);
        }
    }
}
