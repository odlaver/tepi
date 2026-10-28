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
            bool hover = Hover(id), down = Pressed(id);
            AddHit(r, id, act);
            // Held: the button sinks a little
            if (down) r.Inflate(-1.5f, -1.5f);
            if (filled)
            {
                p.FillRound(r, 10, down ? Ink.A(p.T.Text, 0.68f) : hover ? Ink.A(p.T.Text, 0.82f) : p.T.Text);
                p.TextCenter(label, Ink.Semi(13f), p.T.Bg, r.X + r.Width / 2, r.Y + r.Height / 2);
            }
            else
            {
                if (hover) p.FillRound(r, 10, down ? p.T.FillHover : p.T.Fill);
                p.StrokeRound(r, 10, p.T.Sub, 1.2f);
                p.TextCenter(label, Ink.Semi(13f), p.T.Text, r.X + r.Width / 2, r.Y + r.Height / 2);
            }
        }

        // Music

        void DrawControl(RectangleF r, string id, Action act, Action<RectangleF, Color> glyph)
        {
            AddHit(r, id, act);
            if (Hover(id)) p.Circle(r.X + r.Width / 2, r.Y + r.Height / 2, r.Width / 2 - (Pressed(id) ? 1.5f : 0), Pressed(id) ? p.T.FillHover : p.T.Fill);
            if (Pressed(id)) r.Inflate(-1f, -1f);
            glyph(r, p.T.Text);
        }

        // Scrolling lyrics; current line sweeps
        void DrawTeleprompter(RectangleF box, DateTime now)
        {
            if (lyrics == null || lyrics.Key != mediaInfo.Key || lyrics.State == LyricState.Loading)
            {
                p.Orbit(box.X + 8, box.Y + 26, 6f, orbitPhase, p.T.Sub);
                p.Text(Lang.T("Mencari lirik…"), Ink.Text(12.5f), p.T.Sub, box.X + 22, box.Y + 26, box.Width - 22, StringAlignment.Near);
                return;
            }
            if (lyrics.State == LyricState.Plain) { DrawPlainLyrics(box); return; }
            if (lyrics.State != LyricState.Synced)
            {
                string msg = lyrics.State == LyricState.Failed ? Lang.T("LRCLIB tidak terjangkau · dicoba lagi otomatis") : !settings.GetBool("lyrics.online", true) ? Lang.T("Pencarian lirik online dimatikan") : Lang.T("Lirik tidak ditemukan di LRCLIB");
                p.TextBox(msg, Ink.Text(12.5f), p.T.Sub, new RectangleF(box.X, box.Y + 16, box.Width, 40));
                return;
            }
            // Seek targets where lines come to rest
            float anchor = box.Y + 34;
            if (mediaInfo.CanSeek)
                for (int i = Math.Max(0, (int)(lyricScroll.Target / LyricLineH) - 2); i < lyrics.Lines.Count; i++)
                {
                    float y = anchor + i * LyricLineH - lyricScroll.Target;
                    if (y > box.Bottom + LyricLineH) break;
                    if (lyrics.Lines[i].Text.Length == 0 || y <= box.Top || y >= box.Bottom) continue;
                    double at = lyrics.Lines[i].AtMs;
                    AddHit(new RectangleF(box.X, y - LyricLineH / 2, box.Width, LyricLineH), "ly" + i, delegate { media.Seek(at + 50); });
                }
            // List scrolls and wipes alone
            LiveRect(box, t => PaintTeleprompter(box, t));
        }

        double PaintTeleprompter(RectangleF box, DateTime now)
        {
            var st = g.Save();
            g.IntersectClip(box);
            double pos = mediaInfo.Position(now);
            float anchor = box.Y + 34, prog = 1;
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
                    prog = (float)((pos - line.AtMs) / Math.Max(1, lyrics.SungUntil(i) - line.AtMs));
                    // Long lines shrink to fit
                    var big = Ink.Display(15.5f);
                    float mw = p.Measure(line.Text, big);
                    var font = mw <= box.Width ? big : Ink.Display(Math.Max(12f, (float)Math.Floor(15.5f * box.Width / mw * 2) / 2));
                    p.Sweep(line.Text, font, p.T.Text, p.T.Faint, box.X, y, box.Width, StringAlignment.Near, Ease.Clamp01(prog));
                }
                else p.Text(line.Text, Ink.Text(13.5f), i < lyricIndex ? p.T.Faint : p.T.Sub, box.X, y, box.Width, StringAlignment.Near);
            }
            p.Alpha = alpha;
            g.Restore(st);
            // Scroll 60, wipe 30, wake next line
            if (!lyricScroll.Settled) return 1 / 60.0;
            if (!mediaInfo.Playing) return 0;
            if (prog < 1) return 1 / LiveFps;
            double next = lyricIndex >= 0 ? lyrics.NextAt(lyricIndex) : lyrics.Lines.Count > 0 ? lyrics.Lines[0].AtMs : double.MaxValue;
            return next == double.MaxValue ? 0 : Math.Max(0.01, (next - pos) / 1000 + 0.005);
        }

        readonly Spring plainScroll = new Spring(0, 300, 34, 0.2f);
        string plainKey;
        const float PlainLineH = 22f;

        // Untimed lyrics, wheel-scrolled
        void DrawPlainLyrics(RectangleF box)
        {
            if (plainKey != lyrics.Key) { plainKey = lyrics.Key; plainScroll.Snap(0); }
            p.Text(Lang.T("Lirik tanpa waktu"),Ink.Small(11f), p.T.Faint, box.X, box.Y + 8, box.Width, StringAlignment.Near);
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

        // Session open to its checklist
        string planOpen;

        float AgentRowH(AgentSession s)
        {
            float h = 56;
            if (s.Plan != null && s.Plan.Count > 0) h += 12;
            if (planOpen == s.Id && s.Plan != null) h += PlanListH(s);
            return h;
        }

        float AgentsContentH()
        {
            float h = 0;
            foreach (var s in sessions.Values) h += AgentRowH(s);
            return h;
        }

        void DrawAgentsPage(float w, float h, DateTime now)
        {
            if (hub != null && hub.Error != null)
            {
                Empty(w, Ink.IcoTerminal, Lang.T("Server hook mati"), hub.Error + Lang.T(" · agent.port di settings.ini"));
                return;
            }
            float qh = DrawQuotaBlock(w, PanelTop + 2);
            var list = SortedSessions();
            if (list.Count == 0)
            {
                var keep = g.Save();
                g.TranslateTransform(0, Math.Max(0, qh - 8));
                Empty(w, Ink.IcoTerminal, Lang.T("Belum ada sesi agen"), linked.Length > 0 ? Lang.T("Terhubung: ") + linked : Lang.T("Belum terhubung · tray > Hubungkan agen"));
                g.Restore(keep);
                return;
            }
            var area = new RectangleF(10, PanelTop + 4 + qh, w - 20, h - PanelTop - 14 - qh);
            var st = g.Save();
            g.IntersectClip(area);
            float y = area.Y - agentScroll.Value;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                float rowH = AgentRowH(s), dy, a = RowIn(i, now, out dy);
                float top = y + dy;
                y += rowH;
                if (top + rowH < area.Top || top > area.Bottom) continue;
                float keep = p.Alpha;
                p.Alpha = keep * a;
                DrawAgentRow(s, w, top, rowH, i < list.Count - 1);
                p.Alpha = keep;
            }
            g.Restore(st);
        }

        void DrawAgentRow(AgentSession s, float w, float y, float rowH, bool divider)
        {
            float cy = y + 28;
            var row = new RectangleF(14, y + 3, w - 28, rowH - 6);
            string rowId = "agent" + s.Id;
            var session = s;
            AddHit(row, rowId, delegate { if (!FocusTerminal(session)) ShowInfo(Ink.IcoTerminal, Lang.T("Terminal tidak ditemukan"), session.Project, true); });
            if (Hover(rowId)) p.FillRound(row, 16, Pressed(rowId) ? p.T.FillHover : p.T.Fill);
            DrawStateMark(s, 36, cy);
            var semi = Ink.Semi(13.5f);
            p.Text(s.Project, semi, p.T.Text, 60, cy - 10, 220, StringAlignment.Near);
            if (s.Profile.Length > 0)
            {
                float px = 60 + Math.Min(220, p.Measure(s.Project, semi)) + 8;
                p.Text(s.Profile, Ink.Small(11f), p.T.Sub, px, cy - 9.5f, 120, StringAlignment.Near);
            }
            // Hover offers prompt box for live sessions
            string sendId = "send" + s.Id;
            if ((Hover(rowId) || Hover(sendId)) && s.State != AgentState.Ended && s.Window != IntPtr.Zero)
            {
                var send = new RectangleF(w - 26 - 58, cy - 21, 58, 22);
                AddHit(send, sendId, delegate { AskAgent(session); });
                p.FillRound(send, 11, Hover(sendId) ? p.T.Text : p.T.Accent);
                p.TextCenter(Lang.T("Chat"), Ink.Semi(10.5f), Hover(sendId) ? p.T.Bg : p.T.OnAccent, send.X + send.Width / 2, send.Y + 11);
            }
            else
            {
                // Working time ticks alone, once a second
                LiveRect(new RectangleF(w - 186, cy - 18, 160, 16), s.State == AgentState.Working ? TickFps : 0, () => StateWord(session).GetHashCode(),
                         t => p.TextRight(StateWord(session), Ink.Small(11f), p.T.Sub, w - 26, cy - 10));
            }
            string meta = Summary(s, DateTime.UtcNow);
            var mf = Ink.Small(11f);
            float lineW = w - 60 - 26 - p.Measure(meta, mf) - 14;
            p.TextRight(meta, mf, p.T.Faint, w - 26, cy + 10);
            string now = PlanNow(s);
            if ((s.State == AgentState.Done || s.State == AgentState.Ended) && s.LastMessage.Length > 0)
                p.Text(s.LastMessage, Ink.Text(12f), p.T.Sub, 60, cy + 10, lineW, StringAlignment.Near);
            else if (now.Length > 0)
                p.Text(now, Ink.Text(12f), p.T.Sub, 60, cy + 10, lineW, StringAlignment.Near);
            else
                p.Text(AgentLine(s), Ink.Mono(11.5f), p.T.Sub, 60, cy + 10, lineW, StringAlignment.Near);
            if (s.Plan != null && s.Plan.Count > 0)
            {
                // Plan: segments, count, list toggle
                var bar = new RectangleF(60, cy + 24, w - 60 - 26 - 64, 4);
                DrawPlanBar(s, bar);
                string toggle = "plan" + s.Id;
                var chip = new RectangleF(w - 26 - 56, cy + 17, 56, 18);
                AddHit(chip, toggle, delegate { planOpen = planOpen == session.Id ? null : session.Id; Kick(); });
                p.TextRight(PlanCount(s) + (planOpen == s.Id ? "  ▴" : "  ▾"), Ink.Semi(11f), Hover(toggle) ? p.T.Text : p.T.Sub, w - 26, cy + 26);
                if (planOpen == s.Id) DrawPlanList(s, 60, cy + 38, w - 60 - 26);
            }
            if (divider) g.DrawLine(p.P(p.T.Line, 1f), 60, y + rowH - 0.5f, w - 26, y + rowH - 0.5f);
        }

        void DrawStateMark(AgentSession s, float cx, float cy)
        {
            switch (s.State)
            {
                case AgentState.Working: LiveRect(new RectangleF(cx - 10, cy - 10, 20, 20), LiveFps, t => p.Orbit(cx, cy, 8f, orbitPhase, p.T.Text)); break;
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
                case AgentState.Working: return Lang.T("bekerja · ") + Duration(DateTime.UtcNow - (s.TurnStartedUtc > DateTime.MinValue ? s.TurnStartedUtc : s.StartedUtc));
                case AgentState.Waiting: return Lang.T("menunggu izin");
                case AgentState.Done: return Lang.T("selesai · ") + Fmt.Ago(s.LastUtc);
                case AgentState.Ready: return Lang.T("siap");
            }
            return Lang.T("ditutup · ") + Fmt.Ago(s.LastUtc);
        }

        // Kulia

        void DrawTasksPage(float w, float h, DateTime now)
        {
            var state = TaskState();
            if (state == DataState.Off)
            {
                NothingConnected(w, "linkTasks", true);
                return;
            }
            if (state == DataState.Failed)
            {
                float ry = Empty(w, Ink.IcoCalendar, Lang.T("Tugas gagal dimuat"), taskNote);
                Button(new RectangleF(w / 2 - 60, ry, 120, 34), "retry", Lang.T("Coba lagi"), false, delegate { RefreshAll(1); });
                return;
            }
            if (state == DataState.Loading)
            {
                float cy = PanelTop + (h - PanelTop) / 2 - 6;
                // Spinner and line centred as one
                string loading = OnlyKuliaTasks ? Lang.T("Memuat tugas dari Kulia…") : Lang.T("Memuat tugas…");
                float lx = w / 2 - (p.Measure(loading, Ink.Text(13f)) + 24) / 2;
                p.Orbit(lx + 7, cy, 7f, orbitPhase, p.T.Sub);
                p.Text(loading, Ink.Text(13f), p.T.Sub, lx + 24, cy, w - lx - 40, StringAlignment.Near);
                return;
            }
            if (state == DataState.Empty)
            {
                Empty(w, Ink.IcoCalendar, Lang.T("Tidak ada tugas aktif"), taskNote ?? (OnlyKuliaTasks ? Lang.T("Diperbarui tiap 10 menit") : Lang.T("Diperbarui tiap 15 menit")));
                return;
            }
            // Partial failure: one quiet line below
            float foot = TaskFoot();
            if (foot > 0) p.Text(taskNote, Ink.Small(11f), p.T.Sub, 26, h - 18, w - 52, StringAlignment.Near);

            const float rowH = 54;
            var area = new RectangleF(10, PanelTop + 2, w - 20, h - PanelTop - 12 - foot);
            var st = g.Save();
            g.IntersectClip(area);
            var nowUtc = DateTime.UtcNow;
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                float y = area.Y + i * rowH - taskScroll.Value;
                if (y + rowH < area.Top || y > area.Bottom) continue;
                var row = new RectangleF(14, y + 2, w - 28, rowH - 4);
                string rowId = "task" + t.Id, doneId = "taskDone" + t.Id;
                bool hover = Hover(rowId) || Hover(doneId);
                if (hover) p.FillRound(row, 14, p.T.Fill);
                float cy = row.Y + row.Height / 2;
                var local = t.DeadlineUtc.ToLocalTime();
                p.TextCenter(local.Day.ToString(), Ink.Display(17f), p.T.Text, 44, cy - 8);
                p.TextCenter(local.ToString("MMM", Fmt.Id), Ink.Small(10.5f), p.T.Sub, 44, cy + 10);
                float tx = 74, right = row.Right - 12;
                var left = t.DeadlineUtc - nowUtc;
                string when = Relative(t, nowUtc);
                bool urgent = left.TotalHours < 24;
                var wf = Ink.Semi(11.5f);
                float ww = p.Measure(when, wf) + (urgent ? 18 : 0);
                var btn = new RectangleF(right - 116, cy - 15, 116, 30);
                // Read-only sources keep the date
                if (hover && !t.ReadOnly)
                {
                    if (Hover(doneId)) p.FillRound(btn, 10, p.T.FillHover);
                    p.StrokeRound(btn, 10, p.T.Sub, 1.2f);
                    p.TextCenter(Lang.T("Tandai selesai"), Ink.Semi(12f), p.T.Text, btn.X + btn.Width / 2, cy);
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
                string course = t.Course + (t.Kind == "ujian" ? Lang.T(" · ujian") : "") + (t.Status == "proses" ? Lang.T(" · dikerjakan") : "");
                string from = SourceHub.TaskLabel(t.Source);
                if (from.Length > 0 && from != t.Course) course = course.Length > 0 ? from + " · " + course : from;
                p.Text(course, Ink.Small(11f), p.T.Sub, tx, cy - 10, right - ww - tx - 12, StringAlignment.Near);
                p.Text(t.Title, Ink.Semi(13f), p.T.Text, tx, cy + 9, right - ww - tx - 12, StringAlignment.Near);
                string url = t.Url;
                AddHit(row, rowId, delegate { OpenUrl(url); });
                var task = t;
                if (!t.ReadOnly) AddHit(btn, doneId, delegate { MarkDone(task); });
            }
            g.Restore(st);
        }

        static string Relative(DateTime dueUtc, DateTime nowUtc)
        {
            var left = dueUtc - nowUtc;
            var local = dueUtc.ToLocalTime();
            if (left.TotalMinutes < 0) return Lang.F("lewat {0}", Fmt.AgoShort(dueUtc));
            if (left.TotalMinutes < 60) return (int)Math.Max(1, left.TotalMinutes) + Lang.T(" menit lagi");
            if (left.TotalHours < 12) return (int)left.TotalHours + Lang.T(" jam lagi");
            var today = DateTime.Now.Date;
            if (local.Date == today) return Lang.T("hari ini ") + Fmt.Hm(local);
            if (local.Date == today.AddDays(1)) return Lang.T("besok ") + Fmt.Hm(local);
            if ((local.Date - today).TotalDays < 7) return local.ToString("ddd", Fmt.Id) + " " + Fmt.Hm(local);
            return local.ToString("d MMM", Fmt.Id);
        }

        // Messages

        void DrawMessagesPage(float w, float h)
        {
            if (!NotifOn)
            {
                Empty(w, Ink.IcoBell, Lang.T("Notifikasi pesan dimatikan"), Lang.T("Saklarnya ada di menu klik kanan"));
                return;
            }
            if (notifAccess != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
            {
                float ny = Empty(w, Ink.IcoLock, Lang.T("Akses notifikasi ditolak"), Lang.T("Pengaturan Windows > Privasi > Notifikasi"));
                Button(new RectangleF(w / 2 - 80, ny, 160, 34), "notifPerm", Lang.T("Buka pengaturan"), false, delegate
                {
                    try { Process.Start("ms-settings:privacy-notifications"); } catch (Exception ex) { Log.Write("settings: " + ex.Message); }
                });
                return;
            }
            if (notifs.Count == 0)
            {
                Empty(w, Ink.IcoBell, Lang.T("Belum ada pesan"), Lang.T("WhatsApp, Telegram, Discord, Teams, email"));
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
                string id = "notif" + item.Id, del = "notifDel" + item.Id;
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
