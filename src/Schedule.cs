using System;
using System.Collections.Generic;
using System.Drawing;

namespace Tepi
{
    // Class schedule from Kulia: tab and reminders
    partial class Island
    {
        List<ClassSlot> classes;
        string classesError;
        readonly HashSet<string> warnedClasses = new HashSet<string>();
        readonly Spring scheduleScroll = new Spring(0, 300, 34, 0.2f);
        const float ClassRowH = 34;

        DateTime scheduleDay;
        const float HeroH = 82, ChipsH = 40;

        // Days with classes, soonest first, at most six
        List<DateTime> ScheduleDays()
        {
            var days = new List<DateTime>();
            if (classes != null)
                foreach (var c in classes)
                    if (!days.Contains(c.Start.Date) && days.Count < 6) days.Add(c.Start.Date);
            return days;
        }

        DateTime ChosenDay()
        {
            var days = ScheduleDays();
            if (days.Count == 0) return DateTime.Now.Date;
            if (days.Contains(scheduleDay)) return scheduleDay;
            var next = NextClass();
            return next != null && days.Contains(next.Start.Date) ? next.Start.Date : days[0];
        }

        List<ClassSlot> DayClasses()
        {
            var day = ChosenDay();
            return classes == null ? new List<ClassSlot>() : classes.FindAll(c => c.Start.Date == day);
        }

        ClassSlot NextClass()
        {
            if (classes == null) return null;
            var now = DateTime.Now;
            foreach (var c in classes) if (!c.Cancelled && c.End > now) return c;
            return null;
        }

        float ScheduleContentH() { return DayClasses().Count * ClassRowH; }

        float ScheduleHeight()
        {
            if (kulia == null || !kulia.Configured) return 214;
            // Room for the sign-in button
            if (!kulia.SignedIn || (classes == null && classesError != null)) return 252;
            if (classes == null || classes.Count == 0) return 214;
            return Clamp(PanelTop + 8 + HeroH + ChipsH + ScheduleContentH() + 12, 150, 372);
        }

        static string DayName(DateTime day)
        {
            var today = DateTime.Now.Date;
            if (day == today) return Lang.T("Hari ini");
            if (day == today.AddDays(1)) return Lang.T("Besok");
            return day.ToString("dddd, d MMM", Fmt.Id);
        }

        static string SlotNote(ClassSlot c)
        {
            var parts = new List<string>();
            if (c.Room.Length > 0) parts.Add(c.Room);
            if (c.Online) parts.Add(c.Url.Length > 0 ? Lang.T("daring · tautan") : Lang.T("daring"));
            if (c.Kind == "praktikum" || c.Kind == "responsi") parts.Add(Lang.T(c.Kind));
            if (c.Personal) parts.Add(Lang.T("pribadi"));
            return string.Join(" · ", parts);
        }

        void DrawSchedulePage(float w, float h, DateTime now)
        {
            if (kulia == null || !kulia.Configured)
            {
                Empty(w, Ink.IcoCalendar, Lang.T("Kulia belum diatur"), Lang.T("kulia.url dan kulia.key kosong di settings.ini"));
                return;
            }
            if (!kulia.SignedIn)
            {
                float by = Empty(w, Ink.IcoCalendar, Lang.T("Kulia belum tersambung"), Lang.T("Masuk pakai email akun kulia.live"));
                Button(new RectangleF(w / 2 - 74, by, 148, 34), "loginSchedule", Lang.T("Masuk ke Kulia"), true, delegate { OpenLogin(); });
                return;
            }
            if (classes == null)
            {
                Empty(w, Ink.IcoCalendar, classesError != null ? Lang.T("Jadwal gagal dimuat") : Lang.T("Memuat jadwal…"), classesError ?? "");
                return;
            }
            if (classes.Count == 0)
            {
                Empty(w, Ink.IcoCalendar, Lang.T("Tidak ada kelas"), Lang.T("7 hari ke depan kosong"));
                return;
            }
            var nowLocal = DateTime.Now;
            // Hero: the class that matters next
            var next = NextClass();
            var hero = new RectangleF(16, PanelTop + 4, w - 32, HeroH - 8);
            p.FillRound(hero, 18, p.T.Fill);
            if (next != null)
            {
                bool live = nowLocal >= next.Start;
                string when = live ? Lang.T("Sedang berlangsung · sampai ") + Fmt.Hm(next.End)
                                   : Lang.T("Berikutnya · ") + (next.Start.Date == nowLocal.Date ? Fmt.Left(next.Start - nowLocal) + Lang.T(" lagi") : DayName(next.Start.Date).ToLower());
                p.Text(when, Ink.Small(11f), live ? p.T.Accent : p.T.Sub, hero.X + 16, hero.Y + 18, hero.Width - 32, StringAlignment.Near);
                p.Text(next.Title, Ink.Semi(15f), p.T.Text, hero.X + 16, hero.Y + 40, hero.Width - 150, StringAlignment.Near);
                p.TextRight(Fmt.Hm(next.Start) + "–" + Fmt.Hm(next.End), Ink.Semi(12.5f), p.T.Text, hero.Right - 16, hero.Y + 40);
                string note = SlotNote(next);
                if (note.Length > 0) p.Text(note, Ink.Small(11f), p.T.Sub, hero.X + 16, hero.Y + 60, hero.Width - 32, StringAlignment.Near);
                if (next.Url.Length > 0)
                {
                    var join = new RectangleF(hero.Right - 92, hero.Y + 50, 76, 22);
                    p.FillRound(join, 11, Hover("joinNext") ? p.T.Text : p.T.Accent);
                    p.TextCenter(Lang.T("Buka kelas"), Ink.Semi(10.5f), Hover("joinNext") ? p.T.Bg : p.T.OnAccent, join.X + join.Width / 2, join.Y + 11);
                    string url = next.Url;
                    AddHit(join, "joinNext", delegate { OpenUrl(url); });
                }
            }
            else p.Text(Lang.T("Tidak ada kelas lagi minggu ini"), Ink.Semi(13f), p.T.Sub, hero.X + 16, hero.Y + hero.Height / 2, hero.Width - 32, StringAlignment.Near);

            // Day chips
            var chosen = ChosenDay();
            var days = ScheduleDays();
            var labels = ChipLabels(days, nowLocal.Date, w - 32);
            float cx = 16, cy = PanelTop + HeroH + ChipsH / 2 - 2;
            for (int di = 0; di < days.Count; di++)
            {
                var day = days[di];
                string label = labels[di];
                float cw = p.Measure(label, Ink.Semi(11.5f)) + 24;
                if (cx + cw > w - 16) break;
                var chip = new RectangleF(cx, cy - 13, cw, 26);
                string id = "day" + day.ToString("yyyyMMdd");
                bool on = day == chosen;
                if (on) p.FillRound(chip, 13, p.T.Accent);
                else if (Hover(id)) p.FillRound(chip, 13, p.T.FillHover);
                else p.StrokeRound(chip, 13, p.T.Line, 1f);
                p.TextCenter(label, Ink.Semi(11.5f), on ? p.T.OnAccent : p.T.Sub, chip.X + cw / 2, cy);
                var pick = day;
                AddHit(chip, id, delegate { scheduleDay = pick; scheduleScroll.Snap(0); Kick(); });
                cx += cw + 6;
            }

            // Classes of the chosen day, one line each
            var area = new RectangleF(10, PanelTop + HeroH + ChipsH, w - 20, h - PanelTop - HeroH - ChipsH - 8);
            var st = g.Save();
            g.IntersectClip(area);
            float y = area.Y - scheduleScroll.Value;
            foreach (var c in DayClasses())
            {
                float rc = y + ClassRowH / 2;
                if (y + ClassRowH > area.Top && y < area.Bottom)
                {
                    if (c.Url.Length > 0)
                    {
                        var row = new RectangleF(14, y + 2, w - 28, ClassRowH - 4);
                        string rowId = "class" + c.Start.Ticks, url = c.Url;
                        if (Hover(rowId)) p.FillRound(row, 12, p.T.Fill);
                        if (row.Bottom <= area.Bottom && row.Top >= area.Top) AddHit(row, rowId, delegate { OpenUrl(url); });
                    }
                    bool live = !c.Cancelled && nowLocal >= c.Start && nowLocal < c.End;
                    var ink = c.Cancelled || nowLocal >= c.End ? p.T.Faint : p.T.Text;
                    p.Text(Fmt.Hm(c.Start), Ink.Semi(12.5f), live ? p.T.Accent : ink, 26, rc, 52, StringAlignment.Near);
                    string tag = c.Cancelled ? Lang.T("batal") : c.Exam ? Lang.T("ujian") : live ? Lang.T("sekarang") : "";
                    string room = SlotNote(c);
                    float roomW = room.Length > 0 ? Math.Min(150, p.Measure(room, Ink.Small(11f))) : 0;
                    float tagW = tag.Length > 0 ? p.Measure(tag, Ink.Semi(10.5f)) + 16 : 0;
                    float right = w - 26;
                    if (tag.Length > 0)
                    {
                        var pill = new RectangleF(right - tagW, rc - 10, tagW, 20);
                        if (c.Cancelled) p.StrokeRound(pill, 10, p.T.Faint, 1f); else p.FillRound(pill, 10, p.T.Accent);
                        p.TextCenter(tag, Ink.Semi(10.5f), c.Cancelled ? p.T.Faint : p.T.OnAccent, pill.X + tagW / 2, rc);
                        right -= tagW + 8;
                    }
                    if (roomW > 0) p.Text(room, Ink.Small(11f), p.T.Faint, right, rc, roomW, StringAlignment.Far);
                    float tx = 84, tw = right - roomW - 12 - tx;
                    p.Text(c.Title, Ink.Semi(12.5f), ink, tx, rc, tw, StringAlignment.Near);
                    if (c.Cancelled) g.DrawLine(p.P(p.T.Faint, 1f), tx, rc, tx + Math.Min(tw, p.Measure(c.Title, Ink.Semi(12.5f))), rc);
                }
                y += ClassRowH;
            }
            g.Restore(st);
        }

        // Class running now, shown on the island
        ClassSlot ClassNow()
        {
            if (classes == null || !settings.GetBool("class.island", true)) return null;
            var now = DateTime.Now;
            foreach (var c in classes) if (!c.Cancelled && now >= c.Start && now < c.End) return c;
            return null;
        }

        float ClassWidth()
        {
            var c = ClassNow();
            if (c == null || p.G == null) return 240;
            return Clamp(p.Measure(c.Title, Ink.Semi(12.5f)) + p.Measure(Lang.F("sampai {0}", Fmt.Hm(c.End)), Ink.Semi(12f)) + 84, 220, 440);
        }

        readonly Roller classRoller = new Roller();

        void DrawClass(float w, float h, DateTime now)
        {
            var c = ClassNow();
            if (c == null) return;
            float cy = h / 2;
            // Ring empties as the class runs
            var rect = new RectangleF(12.5f, cy - 7.5f, 15, 15);
            g.DrawEllipse(p.P(p.T.Line, 2f), rect);
            float left = (float)Ease.Clamp01((float)((c.End - DateTime.Now).TotalSeconds / Math.Max(1, (c.End - c.Start).TotalSeconds)));
            if (left > 0.003f) g.DrawArc(p.P(p.T.Accent, 2.2f), rect, -90, -360 * left);
            classRoller.Set(Lang.F("sampai {0}", Fmt.Hm(c.End)));
            var f = Ink.Semi(12f);
            float rw = classRoller.Width(p, f);
            p.Text(c.Title, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - rw - 30, StringAlignment.Near);
            classRoller.Draw(p, f, p.T.Sub, w - 18, cy, StringAlignment.Far, now);
            string url = c.Url;
            AddHit(new RectangleF(0, 0, w, h), "classNow", delegate { if (url.Length > 0) OpenUrl(url); else Expand(PageSchedule); });
        }

        // Shorter labels until every day fits
        string[] ChipLabels(List<DateTime> days, DateTime today, float room)
        {
            var formats = new Func<DateTime, string>[]
            {
                d => d == today ? Lang.T("Hari ini") : d == today.AddDays(1) ? Lang.T("Besok") : d.ToString("ddd d", Fmt.Id),
                d => d.ToString("ddd d", Fmt.Id),
                d => d.ToString("ddd", Fmt.Id),
            };
            string[] labels = null;
            foreach (var format in formats)
            {
                labels = days.ConvertAll(d => format(d)).ToArray();
                float total = -6;
                foreach (var l in labels) total += p.Measure(l, Ink.Semi(11.5f)) + 30;
                if (total <= room) break;
            }
            return labels;
        }

        // Once per class, 15 minutes before it starts
        void CheckClasses(DateTime nowUtc)
        {
            // Held while a script runs; fires after if still due
            if (classes == null || PrompterOn) return;
            var now = nowUtc.ToLocalTime();
            foreach (var c in classes)
            {
                double mins = (c.Start - now).TotalMinutes;
                string key = c.Title + "|" + c.Start.ToString("o");
                if (c.Cancelled || mins <= 0 || mins > 15 || warnedClasses.Contains(key)) continue;
                warnedClasses.Add(key);
                var t = ShowToast(View.Info, 8, 3, false, true);
                if (t == null) break;
                t.Glyph = Ink.IcoCalendar;
                t.Title = c.Title + " · " + Fmt.Hm(c.Start);
                string note = SlotNote(c);
                t.Detail = (c.Exam ? Lang.T("ujian · ") : "") + (note.Length > 0 ? note : Math.Ceiling(mins) + Lang.T(" menit lagi"));
                break;
            }
        }
    }
}
