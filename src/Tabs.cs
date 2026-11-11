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
        const int PageHome = 0, PageMusic = 1, PageAgents = 2, PageClass = 3, PageMessages = 4, PageShelf = 5, PageControl = 6, PageRemind = 7, PagePhone = 8;
        // Ids for settings; names for labels
        static readonly string[] PageIds = { "home", "music", "agents", "class", "messages", "shelf", "control", "remind", "phone", "convert", "suara" };
        static readonly string[] PageNames = { "Ringkasan", "Musik", "Agen", "Kuliah", "Pesan", "Rak", "Kontrol", "Pengingat", "HP", "Ubah", "Bicara" };
        // HP joins once a phone is linked
        const string DefaultTabs = "home,music,agents,class,messages,shelf,control,remind,convert,suara";
        const float PanelTop = 54, SegTop = 36;
        float panelW = 560;
        float PanelW { get { return panelW; } }
        int[] tabs = { 0, 1, 2, 3, 4, 5, 6, 7 };
        // Segments: Kuliah tasks/schedule, Rak files/clipboard
        int classSeg, shelfSeg;
        // Hit offset under a segment bar
        float hitDy;

        // Width, shift and tab order from settings
        void LoadLayout()
        {
            panelW = Clamp(settings.GetInt("panel.width", 560), 440, 640);
            shiftPct = Math.Max(-40, Math.Min(40, settings.GetInt("position", 0)));
            MigrateTabs();
            AddConvertTab();
            AddVoiceTab();
            var list = new List<int>();
            foreach (var part in settings.Get("panel.tabs", DefaultTabs).Split(','))
            {
                int v = Array.IndexOf(PageIds, part.Trim());
                if (v >= 0 && !list.Contains(v)) list.Add(v);
            }
            if (list.Count == 0) list.Add(PageHome);
            tabs = list.ToArray();
            tabSnap = true;
            LoadHome();
            LoadPlace();
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
                case PagePhone: return PhoneHeight();
                case PageConvert: return ConvertPageHeight();
                case PageVoice: return VoicePageHeight();
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
            if (pg == PageShelf) ShelfClipCheck();
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
            dotOpen.Step(dt); dotHotX.Step(dt); dotHotA.Step(dt);
            classSegX.Step(dt); shelfSegX.Step(dt);
            agentScroll.Step(dt); taskScroll.Step(dt); notifScroll.Step(dt); plainScroll.Step(dt); scheduleScroll.Step(dt); clipScroll.Step(dt); shelfScroll.Step(dt); convScroll.Step(dt); homeScroll.Step(dt);
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
            return !tabMorph.Settled || DotsMoving() || !classSegX.Settled || !shelfSegX.Settled ||
                   !agentScroll.Settled || !taskScroll.Settled || !notifScroll.Settled || !plainScroll.Settled || !scheduleScroll.Settled || !clipScroll.Settled || !shelfScroll.Settled || !homeScroll.Settled ||
                   (now - pageSwitchUtc).TotalSeconds < 0.7 || (now - panelOpenedUtc).TotalSeconds < 0.7 || (now - segSwitchUtc).TotalSeconds < 0.6 || MusicMoving(now) ||
                   (page == PageAgents && (now - agentFocusUtc).TotalSeconds < 1.6);
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
            if (page == PageHome && !HomeQuiet) { s = homeScroll; content = homeContentH; view = HomeView(); step = 72; }
            else if (page == PageAgents) { s = agentScroll; content = AgentsContentH() + QuotaBlockH(); step = 84; }
            else if (page == PageClass && classSeg == 0) { s = taskScroll; content = (tasks != null ? tasks.Count : 0) * 54 + TaskFoot(); step = 81; }
            else if (page == PageClass) { s = scheduleScroll; content = ScheduleContentH() + EventFoot(); step = ClassRowH * 1.5f; }
            else if (page == PageMessages) { s = notifScroll; content = notifs.Count * 50; step = 75; }
            else if (page == PageShelf && shelfSeg == 1) { s = clipScroll; content = clips.Count * ClipRowH; step = ClipRowH * 1.5f; }
            else if (page == PageShelf) { s = shelfScroll; content = ShelfContentH(); step = 60; }
            else if (page == PageVoice) { s = voiceScroll; content = VoiceContentH(VoiceItems(), PanelW); view = VoiceListView(PanelHeight()); step = VoiceRowH * 1.5f; }
            else if (page == PageConvert && ConvertSetting) { s = convScroll; content = convHistory.Count * ConvRowH; view = ConvListView(PanelHeight()); step = ConvRowH * 1.5f; }
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
                case PagePhone: DrawPhonePage(w, h, now); break;
                case PageConvert: DrawConvertPage(w, h, now); break;
                case PageVoice: DrawVoicePage(w, h, now); break;
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

        const float TabIcon = 32, TabH = 28, TabGap = 4, TabY = 10, DotCell = 22, DotActive = 30, TabGlyph = 0.86f;
        readonly Spring tabMorph = new Spring(1, 300, 26, 0.002f);
        // Wheel past last tab: bar leans back
        readonly Spring tabNudge = new Spring(0, 420, 18, 0.05f);
        bool tabDotsDrawn;
        // One progress for the whole strip: hover or focus anywhere opens every dot
        readonly Spring dotOpen = new Spring(0, 460, 30, 0.002f);
        // Highlight pill slides to the item the pointer would pick
        readonly Spring dotHotX = new Spring(0, 620, 38, 0.05f), dotHotA = new Spring(0, 620, 38, 0.004f);
        DateTime stripUtc = DateTime.MinValue;
        bool stripOpen;
        // Dots band: full tab row tall; open cells keep this much room for the title on the left
        const float DotBandH = 48, DotPadX = 6, DotPadY = 4;

        bool DotsMoving()
        {
            return !dotOpen.Settled || !dotHotX.Settled || !dotHotA.Settled || (DateTime.UtcNow - stripUtc).TotalSeconds < 0.32;
        }

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
                float dx = Math.Min(w / 2 - dotsW / 2, w - 92 - dotsW);
                for (int i = 0; i < n; i++)
                {
                    xs[i] = dx;
                    ws[i] = i == active ? DotActive : DotCell;
                    dx += ws[i] + 6;
                }
                return;
            }
            // Right: action and gear
            float room = w - 110, cell = TabIcon;
            float label = active >= 0 ? p.Measure(TabLabel(tabs[active]), Ink.Semi(12f)) + 7 : 0;
            float total = n * cell + (n - 1) * TabGap + label;
            // Too tight: icons only
            if (total > room) { label = 0; total = n * cell + (n - 1) * TabGap; }
            if (total > room) { cell = (room - (n - 1) * TabGap) / n; total = room; }
            float x = Math.Min(w / 2 - total / 2, w - 92 - total);
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

        // Dots: hover or focus anywhere opens the whole strip into icons; hover names page
        void DrawDots(float w, int at, RectangleF[] rects)
        {
            int n = tabs.Length, hover = -1;
            float cy = 18f;
            float labelY = 39f;
            var now = DateTime.UtcNow;
            // Open layout: equal cells about the strip's centre, clear of the title and the action buttons
            float pitch = Math.Max(28, Math.Min(40, (w - 110) / Math.Max(1, n)));
            float total = n * pitch;
            float mid = n > 0 ? (tabToX[0] + tabToX[n - 1] + tabToW[n - 1]) / 2 : w / 2;
            float x0 = Math.Max(18, Math.Min(mid - total / 2, w - 92 - total));
            var closedBand = n > 0 ? new RectangleF(tabToX[0] - 3, 0, tabToX[n - 1] + tabToW[n - 1] - tabToX[0] + 6, DotBandH) : RectangleF.Empty;
            var openBand = new RectangleF(x0, 0, total, DotBandH);
            bool focusIn = keyboardMode && focusId != null && focusId.Length > 3 && focusId.StartsWith("tab", StringComparison.Ordinal) && char.IsDigit(focusId[3]);
            // Open: stay open inside the open band plus padding; closed: only the dots themselves open it
            var local = new PointF(mouse.X - originX, mouse.Y - originY - hitDy);
            var zone = stripOpen ? RectangleF.Inflate(openBand, DotPadX, DotPadY) : closedBand;
            bool over = pointerInside && zone.Contains(local);
            if (over || focusIn) stripUtc = now;
            double idle = (now - stripUtc).TotalSeconds;
            // Leaving collapses after a beat
            bool open = over || focusIn || idle < 0.28;
            stripOpen = open;
            // Panel was closed meanwhile: start shut
            if (!open && idle > 1) dotOpen.Snap(0);
            dotOpen.Target = open ? 1 : 0;
            if (Anim.Reduced) dotOpen.Snap(dotOpen.Target);
            float e = Ease.Clamp01(dotOpen.Value);
            // Hits follow the geometry the user sees once open
            for (int i = 0; i < n; i++)
            {
                int to = tabs[i];
                var hit = open ? new RectangleF(x0 + i * pitch, 0, pitch, DotBandH) : new RectangleF(tabToX[i] - 3, TabY - 2, tabToW[i] + 6, TabH + 4);
                string id = "tab" + to;
                AddHit(hit, id, delegate { SetPage(to, false); });
                if (Hover(id) || (keyboardMode && focusId == id)) hover = i;
            }
            float hotTo = x0 + Math.Max(0, hover) * pitch + pitch / 2;
            dotHotA.Target = open && hover >= 0 && hover != at ? 1 : 0;
            if (dotHotA.Target > 0 && dotHotA.Value < 0.02f) dotHotX.Snap(hotTo); else dotHotX.Target = hotTo;
            if (Anim.Reduced) { dotHotA.Snap(dotHotA.Target); dotHotX.Snap(hotTo); }
            float ha = Ease.Clamp01(dotHotA.Value) * e;
            if (ha > 0.01f)
            {
                float keepA = p.Alpha;
                p.Alpha = keepA * ha;
                string hid = "tab" + tabs[Math.Max(0, hover)];
                p.FillRound(new RectangleF(dotHotX.Value - (pitch - 4) / 2, cy - 12, pitch - 4, 24), 12, Pressed(hid) ? p.T.FillPressed : p.T.FillHover);
                p.Alpha = keepA;
            }
            for (int i = 0; i < n; i++)
            {
                int to = tabs[i];
                var r = rects[i];
                bool lit = i == hover;
                bool news = (to == PageMessages && unread > 0) || (to == PageAgents && approvals.Count + BusyCount() > 0);
                bool mark = (to == PageMessages && unread > 0) || (to == PageAgents && approvals.Count + BusyCount() > 0 && page != PageAgents);
                float cx = Ease.Lerp(r.X + r.Width / 2, x0 + i * pitch + pitch / 2, e);
                // Small stagger across the strip; all settled once e reaches 1
                float t = Ease.Clamp01(e * 1.35f - 0.35f * i / Math.Max(1, n - 1));
                float ia = Ease.Clamp01((t - 0.35f) / 0.65f), keep = p.Alpha;
                Color iconColor;
                if (i == at)
                {
                    float pw = Ease.Lerp(r.Width, pitch - 4, t), ph = Ease.Lerp(8, 24, t);
                    p.FillRound(new RectangleF(cx - pw / 2, cy - ph / 2, pw, ph), ph / 2, p.T.Accent);
                    iconColor = p.T.OnAccent;
                }
                else
                {
                    // The dot melts away as its icon arrives
                    float rad = Ease.Lerp(lit ? 4.5f : news ? 4f : 3.2f, 7f, t);
                    p.Alpha = keep * (1 - t);
                    if (t < 0.99f) p.Circle(cx, cy, rad, lit || news ? p.T.Text : p.T.Faint);
                    p.Alpha = keep;
                    iconColor = lit ? p.T.Text : p.T.Sub;
                }
                if (ia > 0.01f)
                {
                    float grow = (0.55f + 0.45f * t) * (lit && i != at ? 1.1f : 1f);
                    p.Alpha = keep * ia;
                    var st = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.ScaleTransform(grow, grow);
                    DrawTabIcon(to, 0, 0, iconColor);
                    g.Restore(st);
                    // Unread or busy marker stays on the icon
                    if (mark)
                    {
                        p.Circle(cx + 8, cy - 8, 4.2f, p.T.Bg);
                        p.Circle(cx + 8, cy - 8, 3f, p.T.Text);
                    }
                    p.Alpha = keep;
                }
            }
            // Title: current or hovered page, centered below the strip
            float stripMid = n > 0 ? Ease.Lerp((rects[0].X + rects[n - 1].Right) / 2, x0 + total / 2, e) : w / 2;
            int named = hover >= 0 ? tabs[hover] : page;
            p.TextCenter(TabLabel(named), Ink.Semi(12f), hover >= 0 ? p.T.Text : p.T.Sub, stripMid, labelY);
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
                case PageVoice:
                    p.Icon(Ink.IcoMic, 15f, c, cx, cy);
                    break;
                case PageRemind:
                    p.Icon('\uE823', 15f, c, cx, cy);
                    break;
                case PagePhone:
                    p.Icon(IcoPhone, 15f, c, cx, cy);
                    break;
                case PageConvert:
                    // Two opposing arrows
                    g.DrawLine(pen, cx - 7, cy - 3.5f, cx + 7, cy - 3.5f);
                    g.DrawLines(pen, new[] { new PointF(cx + 3.5f, cy - 7), new PointF(cx + 7, cy - 3.5f), new PointF(cx + 3.5f, cy) });
                    g.DrawLine(pen, cx + 7, cy + 3.5f, cx - 7, cy + 3.5f);
                    g.DrawLines(pen, new[] { new PointF(cx - 3.5f, cy), new PointF(cx - 7, cy + 3.5f), new PointF(cx - 3.5f, cy + 7) });
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

        // Click always switches; hover may too; the wheel always does
        bool TabsByHover { get { return settings.Get("tabs.trigger", "click") == "hover"; } }
        readonly System.Windows.Forms.Timer tabHover = new System.Windows.Forms.Timer { Interval = 140 };
        bool tabHoverWired;

        // Pointer rests on a tab a moment: open it
        void TabHoverCheck(string id)
        {
            bool onTab = id != null && id.Length > 3 && id.StartsWith("tab", StringComparison.Ordinal) && char.IsDigit(id[3]);
            tabHover.Stop();
            if (!onTab || !TabsByHover) return;
            if (!tabHoverWired)
            {
                tabHoverWired = true;
                tabHover.Tick += delegate
                {
                    tabHover.Stop();
                    int pg;
                    if (hoverId != null && hoverId.StartsWith("tab", StringComparison.Ordinal) && int.TryParse(hoverId.Substring(3), out pg) && pg != page) SetPage(pg, false);
                };
            }
            tabHover.Start();
        }

        // Gear, far right: settings without right-click
        void DrawPrefsButton(float w)
        {
            var r = new RectangleF(w - 52, TabY, 34, TabH);
            AddHit(r, "prefsOpen", openPrefsAct ?? (openPrefsAct = delegate { BeginInvoke(new Action(OpenPrefs)); }));
            if (Hover("prefsOpen")) p.FillRound(r, TabH / 2, Pressed("prefsOpen") ? p.T.FillHover : p.T.Fill);
            p.Icon('', 13f, Hover("prefsOpen") ? p.T.Text : p.T.Sub, r.X + 17, r.Y + TabH / 2);
        }

        Action openPrefsAct;

        // Round action button; trash swallows flyers
        void TabButton(string id, char glyph, Action act, bool trash)
        {
            var r = new RectangleF(tabLayoutW - 88, TabY, 34, TabH);
            AddHit(r, id, act);
            if (Hover(id)) p.FillRound(r, TabH / 2, Pressed(id) ? p.T.FillHover : p.T.Fill);
            if (trash) { var c = CanvasRect(r); trashPt = new PointF(c.X + c.Width / 2, c.Y + c.Height / 2); }
            float s = trash ? TrashScale(DateTime.UtcNow) : 1;
            p.Icon(glyph, 13f * s, Hover(id) || s > 1.01f ? p.T.Text : p.T.Sub, r.X + 17, r.Y + TabH / 2);
        }

        // Contextual action right of tabs
        void DrawTabAction(float w)
        {
            DrawPrefsButton(w);
            var r = new RectangleF(w - 88, TabY, 34, TabH);
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
            else if (page == PageVoice)
                TabButton("voiceFolder", '\uE8B7', OpenRapatFolder, false);
            else if (page == PageConvert && ConvertSetting && convHistory.Count > 0)
                TabButton("clearConv", '', ClearConvertHistory, true);
        }
    }
}
