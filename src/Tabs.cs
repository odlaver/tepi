using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Tepi
{
    // Panel structure: tabs, segments, heights, scrolling
    partial class Island
    {
        const int PageHome = 0, PageMusic = 1, PageAgents = 2, PageClass = 3, PageMessages = 4, PageShelf = 5, PageControl = 6, PageRemind = 7;
        // Ids for settings; names for labels
        static readonly string[] PageIds = { "home", "music", "agents", "class", "messages", "shelf", "control", "remind" };
        static readonly string[] PageNames = { "Ringkasan", "Musik", "Agen", "Kuliah", "Pesan", "Rak", "Kontrol", "Pengingat" };
        const float PanelTop = 54, SegTop = 36;
        float panelW = 488;
        float PanelW { get { return panelW; } }
        int[] tabs = { 0, 1, 2, 3, 4, 5, 6, 7 };
        // Segments: Kuliah tasks/schedule, Rak files/clipboard
        int classSeg, shelfSeg;
        // Hit offset under a segment bar
        float hitDy;

        // Width, shift and tab order from settings
        void LoadLayout()
        {
            panelW = Clamp(settings.GetInt("panel.width", 488), 440, 560);
            shiftPct = Math.Max(-40, Math.Min(40, settings.GetInt("position", 0)));
            MigrateTabs();
            var list = new List<int>();
            foreach (var part in settings.Get("panel.tabs", string.Join(",", PageIds)).Split(','))
            {
                int v = Array.IndexOf(PageIds, part.Trim());
                if (v >= 0 && !list.Contains(v)) list.Add(v);
            }
            if (list.Count == 0) list.Add(PageHome);
            tabs = list.ToArray();
            tabSnap = true;
        }

        // 1.x: 3 tasks, 5 schedule, 6 clipboard
        void MigrateTabs()
        {
            if (settings.Has("panel.tabs") || settings.GetInt("settings.v", 1) >= 2) return;
            string[] old = { "home", "music", "agents", "class", "messages", "class", "shelf" };
            if (settings.Has("tabs"))
            {
                var ids = new List<string>();
                foreach (var part in settings.Get("tabs", "").Split(','))
                {
                    int v;
                    if (int.TryParse(part.Trim(), out v) && v >= 0 && v < old.Length && !ids.Contains(old[v])) ids.Add(old[v]);
                }
                foreach (var add in new[] { "shelf", "control", "remind" }) if (!ids.Contains(add)) ids.Add(add);
                settings.Set("panel.tabs", string.Join(",", ids.ToArray()));
            }
            int first = settings.GetInt("panel.page", -1);
            if (first >= 0 && first < old.Length) settings.Set("panel.first", old[first]);
            settings.Set("settings.v", 2);
            settings.Save();
        }

        string TabIds(IEnumerable<int> pages)
        {
            var ids = new List<string>();
            foreach (int pg in pages) if (pg >= 0 && pg < PageIds.Length) ids.Add(PageIds[pg]);
            return string.Join(",", ids.ToArray());
        }

        DateTime panelOpenedUtc, pageSwitchUtc, segSwitchUtc;
        int prevPage, prevSeg;
        readonly Spring agentScroll = new Spring(0, 300, 34, 0.2f), taskScroll = new Spring(0, 300, 34, 0.2f), notifScroll = new Spring(0, 300, 34, 0.2f);
        readonly Spring classSegX = new Spring(0, 420, 34, 0.004f), shelfSegX = new Spring(0, 420, 34, 0.004f);

        // Deep links into segments
        void ShowTasks() { SetSegment(PageClass, 0, true); Show(PageClass); }
        void ShowSchedule() { SetSegment(PageClass, 1, true); Show(PageClass); }
        void ShowClips() { SetSegment(PageShelf, 1, true); Show(PageShelf); }
        void Show(int pg) { if (expanded) SetPage(pg, false); else Expand(pg); }

        bool SegmentPage(int pg) { return pg == PageClass || pg == PageShelf; }

        // Height follows each tab's content
        float PanelHeight() { return PageHeight(page); }

        float PageHeight(int pg)
        {
            switch (pg)
            {
                case PageHome: return HomeHeight();
                case PageMusic: return MusicHeight();
                case PageAgents: return AgentsHeight();
                case PageClass: return Math.Min(MaxH, SegTop + (classSeg == 0 ? TasksHeight() : ScheduleHeight()));
                case PageShelf: return Math.Min(MaxH, SegTop + (shelfSeg == 0 ? ShelfHeight() : ClipsHeight()));
                case PageControl: return ControlHeight();
                case PageRemind: return RemindHeight();
                default: return MessagesHeight();
            }
        }

        float MusicHeight() { return mediaInfo.Present && mediaInfo.Title.Length > 0 ? 340 : 214; }

        float AgentsHeight()
        {
            if (hub != null && hub.Error != null) return 214;
            float qh = QuotaBlockH();
            if (sessions.Count == 0) return 214 + Math.Max(0, qh - 8);
            return Clamp(PanelTop + 14 + qh + AgentsContentH(), 150, 360);
        }

        float TasksHeight()
        {
            var state = TaskState();
            // Room for the button
            if (state == DataState.Off || state == DataState.Failed) return 252;
            if (state != DataState.Ready) return 214;
            return Clamp(PanelTop + 14 + tasks.Count * 54 + TaskFoot(), 150, 372);
        }

        float MessagesHeight()
        {
            if (!NotifOn || notifs.Count == 0) return 214;
            if (notifAccess != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) return 252;
            return Clamp(PanelTop + 14 + notifs.Count * 50, 150, 372);
        }

        int DefaultPage(bool hover)
        {
            int want = Array.IndexOf(PageIds, settings.Get("panel.first", ""));
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
            if (snap) { tabSnap = true; prevPage = pg; }
            if (pg == PageMessages) unread = 0;
            if (pg == PageClass) RefreshTasksIfStale();
            Kick();
        }

        void SetSegment(int pg, int seg, bool snap)
        {
            var sx = pg == PageClass ? classSegX : shelfSegX;
            int was = pg == PageClass ? classSeg : shelfSeg;
            if (pg == PageClass) classSeg = seg; else shelfSeg = seg;
            if (snap) { sx.Snap(seg); return; }
            if (was == seg) return;
            prevSeg = was;
            sx.Target = seg;
            segSwitchUtc = DateTime.UtcNow;
            if (pg == PageClass) RefreshTasksIfStale();
            Kick();
        }

        void RefreshTasksIfStale()
        {
            if (kulia != null && kulia.SignedIn && (DateTime.UtcNow - tasksAtUtc).TotalMinutes > 2) RefreshTasks(false);
            if (sources != null) sources.Stale(TimeSpan.FromMinutes(2));
        }

        void StepPanel(float dt)
        {
            tabMorph.Step(dt);
            classSegX.Step(dt); shelfSegX.Step(dt);
            agentScroll.Step(dt); taskScroll.Step(dt); notifScroll.Step(dt); plainScroll.Step(dt); scheduleScroll.Step(dt); clipScroll.Step(dt); shelfScroll.Step(dt);
            // Rubber band: let go after a beat
            if (rubber != null && (DateTime.UtcNow - rubberUtc).TotalSeconds > 0.14)
            {
                rubber.Target = Math.Max(0, Math.Min(rubberMax, rubber.Target));
                rubber = null;
            }
            StepMusic(dt);
        }

        bool PanelMoving()
        {
            var now = DateTime.UtcNow;
            return !tabMorph.Settled || !classSegX.Settled || !shelfSegX.Settled ||
                   !agentScroll.Settled || !taskScroll.Settled || !notifScroll.Settled || !plainScroll.Settled || !scheduleScroll.Settled || !clipScroll.Settled || !shelfScroll.Settled ||
                   (now - pageSwitchUtc).TotalSeconds < 0.7 || (now - panelOpenedUtc).TotalSeconds < 0.7 || (now - segSwitchUtc).TotalSeconds < 0.6 || MusicMoving(now);
        }

        Spring rubber;
        float rubberMax;
        DateTime rubberUtc;

        // Past an edge: stretch, spring back
        void ScrollTo(Spring s, float want, float max)
        {
            if (Anim.Reduced || (want >= 0 && want <= max)) { s.Target = Math.Max(0, Math.Min(max, want)); return; }
            float over = want < 0 ? want : want - max;
            s.Target = (want < 0 ? 0 : max) + Math.Sign(over) * Math.Min(28, Math.Abs(over) * 0.35f);
            rubber = s;
            rubberMax = max;
            rubberUtc = DateTime.UtcNow;
        }

        // Staggered row entry; dy is remaining drop
        float RowIn(int i, DateTime now, out float dy)
        {
            var since = pageSwitchUtc > panelOpenedUtc ? pageSwitchUtc : panelOpenedUtc;
            if (segSwitchUtc > since) since = segSwitchUtc;
            float t = ((float)(now - since).TotalSeconds - 0.06f - Math.Min(i, 8) * 0.03f) / 0.32f;
            if (Anim.Reduced || t >= 1) { dy = 0; return 1; }
            float k = Ease.OutCubic(t);
            dy = (1 - k) * 8;
            return k;
        }

        bool ScrollPanel(int delta)
        {
            // Visible list height under tabs (and segments)
            float view = PanelHeight() - PanelTop - 20 - (SegmentPage(page) ? SegTop : 0);
            Spring s;
            float content, step;
            if (page == PageAgents) { s = agentScroll; content = AgentsContentH() + QuotaBlockH(); step = 84; }
            else if (page == PageClass && classSeg == 0) { s = taskScroll; content = (tasks != null ? tasks.Count : 0) * 54 + TaskFoot(); step = 81; }
            else if (page == PageClass) { s = scheduleScroll; content = ScheduleContentH() + EventFoot(); step = ClassRowH * 1.5f; }
            else if (page == PageMessages) { s = notifScroll; content = notifs.Count * 50; step = 75; }
            else if (page == PageShelf && shelfSeg == 1) { s = clipScroll; content = clips.Count * ClipRowH; step = ClipRowH * 1.5f; }
            else if (page == PageShelf) { s = shelfScroll; content = ShelfContentH(); step = 60; }
            else if (page == PageMusic && lyrics != null && lyrics.State == LyricState.Plain && lyrics.Key == mediaInfo.Key)
            {
                s = plainScroll;
                content = lyrics.Lines.Count * PlainLineH;
                view = 150;
                step = PlainLineH * 3;
            }
            else return false;
            // Content fits: wheel changes tabs instead
            if (content - view <= 1 && s.Target <= 0.5f) return false;
            ScrollTo(s, s.Target - delta / 120f * step, Math.Max(0, content - view));
            Kick();
            return true;
        }

        int wheelAcc;
        DateTime wheelTabUtc;

        // One tab per notch, no skipping
        void WheelTab(int delta)
        {
            if ((DateTime.UtcNow - wheelTabUtc).TotalSeconds > 0.5) wheelAcc = 0;
            wheelAcc += delta;
            if (Math.Abs(wheelAcc) < 100 || (DateTime.UtcNow - wheelTabUtc).TotalSeconds < 0.18) return;
            int step = Math.Sign(wheelAcc);
            wheelAcc = 0;
            wheelTabUtc = DateTime.UtcNow;
            int at = Array.IndexOf(tabs, page);
            int next = at + step;
            // Stop at the ends, like paging
            if (at < 0 || next < 0 || next >= tabs.Length) { if (!Anim.Reduced) tabNudge.Velocity += step * 220; Kick(); return; }
            SetPage(tabs[next], false);
            Announce(Lang.T(PageNames[tabs[next]]));
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
            int dir = Math.Sign(Array.IndexOf(tabs, page) - Array.IndexOf(tabs, prevPage));
            var area = new RectangleF(0, PanelTop - 6, w, h - PanelTop + 6);
            var st = g.Save();
            g.IntersectClip(area);
            float alpha = p.Alpha;
            // Outgoing page, skipped once faded
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

        // Cover backdrop swaps mid-fade
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
                case PageClass:
                case PageShelf: DrawSegmentPage(pg, w, h, now); break;
                case PageMessages: DrawMessagesPage(w, h); break;
                case PageControl: DrawControlPage(w, h, now); break;
                case PageRemind: DrawRemindPage(w, h, now); break;
            }
        }

        // Segments

        void DrawSegmentPage(int pg, float w, float h, DateTime now)
        {
            bool isClass = pg == PageClass;
            int seg = isClass ? classSeg : shelfSeg;
            DrawSegments(w, pg, isClass ? Lang.T("Tugas") : Lang.T("Berkas"), isClass ? Lang.T("Jadwal") : Lang.T("Clipboard"));
            float k = Ease.OutCubic((float)((now - segSwitchUtc).TotalSeconds / 0.3));
            if (pg != page) k = 1;
            int dir = seg > prevSeg ? 1 : -1;
            var st = g.Save();
            g.IntersectClip(new RectangleF(0, PanelTop + SegTop - 6, w, h - PanelTop - SegTop + 6));
            float alpha = p.Alpha, keepDy = hitDy;
            bool hold = hitsOn;
            if (k < 1 && prevSeg != seg)
            {
                p.Alpha = alpha * (1 - Ease.Clamp01(k * 2.2f));
                var s1 = g.Save();
                g.TranslateTransform(-dir * k * 24, SegTop);
                hitsOn = false;
                DrawSub(pg, prevSeg, w, h - SegTop, now);
                g.Restore(s1);
            }
            p.Alpha = alpha * Ease.Clamp01(k * 1.3f);
            var s2 = g.Save();
            g.TranslateTransform(dir * (1 - k) * 24, SegTop);
            hitsOn = hold && k >= 0.85f;
            hitDy = keepDy + SegTop;
            DrawSub(pg, seg, w, h - SegTop, now);
            hitDy = keepDy;
            hitsOn = hold;
            g.Restore(s2);
            p.Alpha = alpha;
            g.Restore(st);
        }

        void DrawSub(int pg, int seg, float w, float h, DateTime now)
        {
            if (pg == PageClass) { if (seg == 0) DrawTasksPage(w, h, now); else DrawSchedulePage(w, h, now); }
            else { if (seg == 0) DrawShelfPage(w, h, now); else DrawClipsPage(w, h, now); }
        }

        // Two-way switch with a sliding thumb
        void DrawSegments(float w, int pg, string a, string b)
        {
            int seg = pg == PageClass ? classSeg : shelfSeg;
            var sx = pg == PageClass ? classSegX : shelfSegX;
            var box = new RectangleF(w / 2 - 112, PanelTop, 224, 28);
            p.FillRound(box, 14, p.T.Fill);
            float half = (box.Width - 4) / 2;
            var thumb = new RectangleF(box.X + 2 + half * Math.Max(-0.05f, Math.Min(1.05f, sx.Value)), box.Y + 2, half, box.Height - 4);
            p.FillRound(thumb, 12, Ink.Mix(p.T.FillHover, p.T.Text, 0.12f));
            for (int i = 0; i < 2; i++)
            {
                var r = new RectangleF(box.X + 2 + half * i, box.Y + 2, half, box.Height - 4);
                string id = "seg" + pg + "_" + i;
                int to = i;
                AddHit(r, id, delegate { SetSegment(pg, to, false); });
                p.TextCenter(i == 0 ? a : b, Ink.Semi(12f), i == seg || Hover(id) ? p.T.Text : p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
            }
        }

        // Icon tabs; active one widens with name

        const float TabIcon = 32, TabH = 28, TabGap = 4, TabY = 10, DotCell = 16, DotActive = 30, TabGlyph = 0.86f;
        readonly Spring tabMorph = new Spring(1, 300, 26, 0.002f);
        // Wheel past last tab: bar leans back
        readonly Spring tabNudge = new Spring(0, 420, 18, 0.05f);
        bool tabDotsDrawn;

        // Many tabs: dots plus page title
        bool DotTabs
        {
            get
            {
                string style = settings.Get("tabs.style", "auto");
                return style == "dots" || (style == "auto" && tabs.Length > 7);
            }
        }
        float[] tabFromX = new float[0], tabFromW = new float[0], tabToX = new float[0], tabToW = new float[0];
        int tabMorphPage = -1;
        bool tabSnap = true;
        float tabLayoutW;

        string TabLabel(int pg) { return Lang.T(PageNames[pg]); }

        void TabLayout(float w, int active, float[] xs, float[] ws)
        {
            int n = tabs.Length;
            if (DotTabs)
            {
                float dotsW = (n - 1) * DotCell + DotActive + (n - 1) * 6;
                float dx = w / 2 - dotsW / 2 + 30;
                for (int i = 0; i < n; i++)
                {
                    xs[i] = dx;
                    ws[i] = i == active ? DotActive : DotCell;
                    dx += ws[i] + 6;
                }
                return;
            }
            float room = w - 116, cell = TabIcon;
            float label = active >= 0 ? p.Measure(TabLabel(tabs[active]), Ink.Semi(12f)) + 7 : 0;
            float total = n * cell + (n - 1) * TabGap + label;
            // Too tight: icons only
            if (total > room) { label = 0; total = n * cell + (n - 1) * TabGap; }
            if (total > room) { cell = (room - (n - 1) * TabGap) / n; total = room; }
            float x = w / 2 - total / 2;
            for (int i = 0; i < n; i++)
            {
                xs[i] = x;
                ws[i] = cell + (i == active ? label : 0);
                x += ws[i] + TabGap;
            }
        }

        void DrawTabs(float w, DateTime now)
        {
            int n = tabs.Length, at = Array.IndexOf(tabs, page);
            if (tabToX.Length != n) { tabToX = new float[n]; tabToW = new float[n]; tabFromX = new float[n]; tabFromW = new float[n]; tabSnap = true; }
            if (tabDotsDrawn != DotTabs) tabSnap = true;
            tabDotsDrawn = DotTabs;
            if (tabMorphPage != page || tabSnap || tabLayoutW != w)
            {
                float k0 = tabMorph.Value;
                for (int i = 0; i < n; i++) { tabFromX[i] = Ease.Lerp(tabFromX[i], tabToX[i], k0); tabFromW[i] = Ease.Lerp(tabFromW[i], tabToW[i], k0); }
                TabLayout(w, at, tabToX, tabToW);
                if (tabSnap)
                {
                    Array.Copy(tabToX, tabFromX, n);
                    Array.Copy(tabToW, tabFromW, n);
                    tabMorph.Snap(1);
                    tabSnap = false;
                }
                else
                {
                    // Liquid: a touch of overshoot
                    Tune(tabMorph, 300, 0.74f);
                    tabMorph.Snap(0);
                    tabMorph.Target = 1;
                }
                tabMorphPage = page;
                tabLayoutW = w;
            }
            float k = tabMorph.Value;
            var rects = new RectangleF[n];
            float nudge = tabNudge.Value;
            if (tabDotsDrawn)
            {
                for (int i = 0; i < n; i++) rects[i] = new RectangleF(Ease.Lerp(tabFromX[i], tabToX[i], k) + nudge, TabY, Ease.Lerp(tabFromW[i], tabToW[i], k), TabH);
                DrawDots(w, at, rects);
                return;
            }
            for (int i = 0; i < n; i++) rects[i] = new RectangleF(Ease.Lerp(tabFromX[i], tabToX[i], k) + nudge, TabY, Math.Max(TabH, Ease.Lerp(tabFromW[i], tabToW[i], k)), TabH);

            for (int i = 0; i < n; i++)
            {
                int to = tabs[i];
                var hit = new RectangleF(tabToX[i], TabY, tabToW[i], TabH);
                AddHit(hit, "tab" + to, delegate { SetPage(to, false); });
                if (Hover("tab" + to) && to != page) p.FillRound(rects[i], TabH / 2, Pressed("tab" + to) ? p.T.FillHover : p.T.Fill);
                DrawTabIconFast(to, rects[i].X + TabIcon / 2, TabY + TabH / 2, Hover("tab" + to) ? p.T.Text : p.T.Sub, to != page);
            }
            // Hidden tab via link: no pill
            if (at >= 0)
            {
                var pill = rects[at];
                p.FillRound(pill, TabH / 2, p.T.Accent);
                var st = g.Save();
                using (var clip = Ink.Round(pill, TabH / 2)) g.SetClip(clip, CombineMode.Intersect);
                for (int i = 0; i < n; i++)
                    if (rects[i].IntersectsWith(pill)) DrawTabIconFast(tabs[i], rects[i].X + TabIcon / 2, TabY + TabH / 2, p.T.OnAccent, true);
                // Name fades in as the pill opens
                float la = Ease.Clamp01((Math.Min(1, k) - 0.35f) / 0.65f);
                if (la > 0.01f && tabToW[at] > TabIcon + 1)
                {
                    float keep = p.Alpha;
                    p.Alpha = keep * la;
                    p.Text(TabLabel(page), Ink.Semi(12f), p.T.OnAccent, pill.X + TabIcon - 4, TabY + TabH / 2, pill.Width - TabIcon + 4, StringAlignment.Near);
                    p.Alpha = keep;
                }
                g.Restore(st);
            }
            for (int i = 0; i < n; i++)
            {
                int pg = tabs[i];
                bool dot = (pg == PageMessages && unread > 0) || (pg == PageAgents && approvals.Count + BusyCount() > 0 && page != PageAgents);
                if (!dot) continue;
                float dx = rects[i].X + TabIcon - 9;
                p.Circle(dx, rects[i].Y + 6, 4.2f, p.T.Bg);
                p.Circle(dx, rects[i].Y + 6, 3f, p.T.Text);
            }
            DrawTabAction(w);
        }

        // Dots: active stretches; hover names page
        void DrawDots(float w, int at, RectangleF[] rects)
        {
            int n = tabs.Length, hover = -1;
            float cy = TabY + TabH / 2;
            for (int i = 0; i < n; i++)
            {
                int to = tabs[i];
                var hit = new RectangleF(tabToX[i] - 3, TabY, tabToW[i] + 6, TabH);
                AddHit(hit, "tab" + to, delegate { SetPage(to, false); });
                if (Hover("tab" + to)) hover = i;
                var r = rects[i];
                bool news = (to == PageMessages && unread > 0) || (to == PageAgents && approvals.Count + BusyCount() > 0);
                if (i == at) p.FillRound(new RectangleF(r.X, cy - 4, r.Width, 8), 4, p.T.Accent);
                else
                {
                    float rad = Hover("tab" + to) ? 4.5f : news ? 4f : 3.2f;
                    p.Circle(r.X + r.Width / 2, cy, rad, Hover("tab" + to) || news ? p.T.Text : p.T.Faint);
                }
            }
            // Title: current or hovered page
            float titleRight = (n > 0 ? rects[0].X : w / 2) - 14;
            int named = hover >= 0 ? tabs[hover] : page;
            p.Text(TabLabel(named), Ink.Display(15f), hover >= 0 && tabs[hover] != page ? p.T.Sub : p.T.Text, 62, cy, titleRight - 62, StringAlignment.Near);
            DrawTabAction(w);
        }

        // Cached tab icons; vector while animating
        readonly Dictionary<long, Bitmap> tabIcons = new Dictionary<long, Bitmap>();
        float tabIconScale;
        int tabIconEpoch = -1;
        const float TabIconBox = 24f;

        void DrawTabIconFast(int i, float cx, float cy, Color c, bool spin)
        {
            // Busy agents: spark spins, still under pill
            if (i == PageAgents && BusyCount() > 0)
            {
                if (spin) LiveRect(new RectangleF(cx - 12, cy - 12, 24, 24), LiveFps, t => DrawTabIcon(i, cx, cy, c));
                else DrawTabIcon(i, cx, cy, c);
                return;
            }
            if (p.Alpha < 0.99f) { DrawTabIcon(i, cx, cy, c); return; }
            if (tabIconScale != scale || tabIconEpoch != Ink.Epoch || tabIcons.Count > 48)
            {
                foreach (var b in tabIcons.Values) b.Dispose();
                tabIcons.Clear();
                tabIconScale = scale;
                tabIconEpoch = Ink.Epoch;
            }
            long key = ((long)i << 32) | (uint)c.ToArgb();
            Bitmap bmp;
            if (!tabIcons.TryGetValue(key, out bmp))
            {
                int px = (int)Math.Ceiling(TabIconBox * scale) + 2;
                bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                Graphics keepG = g, bg = Graphics.FromImage(bmp);
                float keepAlpha = p.Alpha;
                try
                {
                    bg.SmoothingMode = SmoothingMode.AntiAlias;
                    bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.ScaleTransform(scale, scale);
                    g = bg;
                    p.G = bg;
                    p.Alpha = 1f;
                    DrawTabIcon(i, TabIconBox / 2, TabIconBox / 2, c);
                }
                finally { g = keepG; p.G = keepG; p.Alpha = keepAlpha; bg.Dispose(); }
                tabIcons[key] = bmp;
            }
            // Whole device pixels, 1:1
            var pt = new[] { new PointF(cx, cy) };
            g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, pt);
            int x = (int)Math.Round(pt[0].X - TabIconBox / 2 * scale), y = (int)Math.Round(pt[0].Y - TabIconBox / 2 * scale);
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, new Rectangle(x, y, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
            g.Restore(st);
        }

        void DrawTabIcon(int i, float cx, float cy, Color c)
        {
            // Glyphs a touch smaller than drawn
            var iconState = g.Save();
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(TabGlyph, TabGlyph);
            cx = 0;
            cy = 0;
            var pen = p.P(c, 1.7f);
            switch (i)
            {
                case PageHome:
                    // Overview: a 2x2 grid of tiles
                    for (int gy = 0; gy < 2; gy++)
                        for (int gx = 0; gx < 2; gx++)
                            using (var tile = Ink.Round(new RectangleF(cx - 7.5f + gx * 8.5f, cy - 7.5f + gy * 8.5f, 6.5f, 6.5f), 2f)) g.DrawPath(p.P(c, 1.5f), tile);
                    break;
                case PageClass:
                    p.Icon('', 16f, c, cx, cy);
                    break;
                case PageShelf:
                    // Tray with a lip
                    using (var tray = Ink.Round(new RectangleF(cx - 8, cy - 5.5f, 16, 12), 3.5f)) g.DrawPath(pen, tray);
                    g.DrawLines(pen, new[] { new PointF(cx - 8, cy + 0.5f), new PointF(cx - 3.2f, cy + 0.5f), new PointF(cx - 2, cy + 2.6f), new PointF(cx + 2, cy + 2.6f), new PointF(cx + 3.2f, cy + 0.5f), new PointF(cx + 8, cy + 0.5f) });
                    break;
                case PageRemind:
                    p.Icon('\uE823', 15f, c, cx, cy);
                    break;
                case PageControl:
                    // Two sliders
                    g.DrawLine(pen, cx - 7.5f, cy - 3.5f, cx + 7.5f, cy - 3.5f);
                    g.DrawLine(pen, cx - 7.5f, cy + 3.5f, cx + 7.5f, cy + 3.5f);
                    p.Circle(cx - 2.5f, cy - 3.5f, 2.6f, c);
                    p.Circle(cx + 3.5f, cy + 3.5f, 2.6f, c);
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
                default:
                    using (var bubble = Ink.Round(new RectangleF(cx - 8.5f, cy - 7f, 17, 12.5f), 4.6f)) g.DrawPath(pen, bubble);
                    g.DrawLines(pen, new[] { new PointF(cx - 5.5f, cy + 5.4f), new PointF(cx - 6.8f, cy + 9f), new PointF(cx - 1.8f, cy + 5.4f) });
                    for (int d = -1; d <= 1; d++) p.Circle(cx + d * 3.6f, cy - 0.8f, 1.15f, c);
                    break;
            }
            g.Restore(iconState);
        }

        // Chevron up: close the panel into Mini
        void DrawHideButton()
        {
            var r = new RectangleF(18, TabY, 34, TabH);
            AddHit(r, "toMini", delegate { GoMini(); });
            if (Hover("toMini")) p.FillRound(r, TabH / 2, Pressed("toMini") ? p.T.FillHover : p.T.Fill);
            p.Symbol(Sym.ChevronUp, r.X + 17, r.Y + TabH / 2, 14f, Hover("toMini") ? p.T.Text : p.T.Sub);
        }

        void GoMini()
        {
            if (!MiniWanted()) ToggleMini();
            expanded = false;
            Kick();
        }

        // Round action button; trash swallows flyers
        void TabButton(string id, char glyph, Action act, bool trash)
        {
            var r = new RectangleF(tabLayoutW - 52, TabY, 34, TabH);
            AddHit(r, id, act);
            if (Hover(id)) p.FillRound(r, TabH / 2, Pressed(id) ? p.T.FillHover : p.T.Fill);
            if (trash) { var c = CanvasRect(r); trashPt = new PointF(c.X + c.Width / 2, c.Y + c.Height / 2); }
            float s = trash ? TrashScale(DateTime.UtcNow) : 1;
            p.Icon(glyph, 13f * s, Hover(id) || s > 1.01f ? p.T.Text : p.T.Sub, r.X + 17, r.Y + TabH / 2);
        }

        // Contextual action right of tabs
        void DrawTabAction(float w)
        {
            DrawHideButton();
            var r = new RectangleF(w - 52, TabY, 34, TabH);
            if (page == PageMusic && mediaInfo.Present && mediaInfo.Title.Length > 0)
            {
                bool album = AlbumMode();
                AddHit(r, "musicView", delegate { ToggleMusicView(); });
                if (Hover("musicView")) p.FillRound(r, TabH / 2, Pressed("musicView") ? p.T.FillHover : p.T.Fill);
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
            }
            else if (page == PageClass && (classSeg == 0 ? TaskState() : ScheduleState()) != DataState.Off)
            {
                int what = classSeg == 0 ? 1 : 2;
                if (tasksLoading && classSeg == 0) p.Orbit(r.X + 17, r.Y + TabH / 2, 6.5f, orbitPhase, p.T.Sub);
                else TabButton("refresh", Ink.IcoRefresh, delegate { if (!tasksLoading) RefreshAll(what); }, false);
            }
            else if (page == PageMessages && notifs.Count > 0)
                TabButton("clearAll", '', delegate { Vanish(RowRects("notif", "notifDel"), true); notifScroll.Snap(0); RemoveNotifs(new List<NotifItem>(notifs)); }, true);
            else if (page == PageShelf && shelfSeg == 1 && clips.Count > 0)
                TabButton("clearClips", '', delegate { Vanish(RowRects("clip", null), true); clipScroll.Snap(0); clips.Clear(); Kick(); }, true);
            else if (page == PageShelf && shelfSeg == 0 && shelf.Count > 0)
                TabButton("clearShelf", '', delegate { Vanish(new List<RectangleF>(shelfCells.Values), true); shelfScroll.Snap(0); ClearShelf(); }, true);
        }
    }
}
