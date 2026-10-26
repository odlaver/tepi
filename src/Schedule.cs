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
        const float ClassRowH = 48, DayHeadH = 30;

        // Rows and day headers, top to bottom
        List<object> ScheduleRows()
        {
            var rows = new List<object>();
            if (classes == null) return rows;
            DateTime day = DateTime.MinValue;
            foreach (var c in classes)
            {
                if (c.Start.Date != day) { day = c.Start.Date; rows.Add(day); }
                rows.Add(c);
            }
            return rows;
        }

        float ScheduleContentH()
        {
            float h = 0;
            foreach (var r in ScheduleRows()) h += r is DateTime ? DayHeadH : ClassRowH;
            return h;
        }

        float ScheduleHeight()
        {
            if (kulia == null || !kulia.Configured || !kulia.SignedIn) return 214;
            if (classes == null || classes.Count == 0) return 214;
            return Clamp(PanelTop + 14 + ScheduleContentH(), 150, 372);
        }

        static string DayName(DateTime day)
        {
            var today = DateTime.Now.Date;
            if (day == today) return "Hari ini";
            if (day == today.AddDays(1)) return "Besok";
            return day.ToString("dddd, d MMM", Fmt.Id);
        }

        static string SlotNote(ClassSlot c)
        {
            var parts = new List<string>();
            if (c.Room.Length > 0) parts.Add(c.Room);
            if (c.Online) parts.Add("daring");
            if (c.Kind == "praktikum" || c.Kind == "responsi") parts.Add(c.Kind);
            if (c.Personal) parts.Add("pribadi");
            return string.Join(" · ", parts);
        }

        void DrawSchedulePage(float w, float h, DateTime now)
        {
            if (kulia == null || !kulia.Configured)
            {
                Empty(w, Ink.IcoCalendar, "Kulia belum diatur", "kulia.url dan kulia.key kosong di settings.ini");
                return;
            }
            if (!kulia.SignedIn)
            {
                float by = Empty(w, Ink.IcoCalendar, "Kulia belum tersambung", "Masuk pakai email akun kulia.live");
                Button(new RectangleF(w / 2 - 74, by, 148, 34), "loginSchedule", "Masuk ke Kulia", true, delegate { OpenLogin(); });
                return;
            }
            if (classes == null)
            {
                Empty(w, Ink.IcoCalendar, classesError != null ? "Jadwal gagal dimuat" : "Memuat jadwal…", classesError ?? "");
                return;
            }
            if (classes.Count == 0)
            {
                Empty(w, Ink.IcoCalendar, "Tidak ada kelas", "7 hari ke depan kosong");
                return;
            }
            var area = new RectangleF(10, PanelTop + 2, w - 20, h - PanelTop - 12);
            var st = g.Save();
            g.IntersectClip(area);
            float y = area.Y - scheduleScroll.Value;
            var nowLocal = DateTime.Now;
            foreach (var r in ScheduleRows())
            {
                if (r is DateTime)
                {
                    if (y + DayHeadH > area.Top && y < area.Bottom)
                        p.Text(DayName((DateTime)r), Ink.Small(11f), p.T.Faint, 26, y + DayHeadH / 2 + 4, w - 52, StringAlignment.Near);
                    y += DayHeadH;
                    continue;
                }
                var c = (ClassSlot)r;
                if (y + ClassRowH > area.Top && y < area.Bottom)
                {
                    float cy = y + ClassRowH / 2;
                    bool live = !c.Cancelled && nowLocal >= c.Start && nowLocal < c.End;
                    if (live) p.FillRound(new RectangleF(14, y + 2, w - 28, ClassRowH - 4), 14, p.T.Fill);
                    var faded = c.Cancelled ? p.T.Faint : p.T.Text;
                    p.Text(Fmt.Hm(c.Start), Ink.Semi(13f), faded, 26, cy - 9, 70, StringAlignment.Near);
                    p.Text(Fmt.Hm(c.End), Ink.Small(11f), p.T.Faint, 26, cy + 10, 70, StringAlignment.Near);
                    string tag = c.Cancelled ? "batal" : c.Exam ? "ujian" : live ? "sekarang" : "";
                    float tagW = tag.Length > 0 ? p.Measure(tag, Ink.Semi(11f)) + 20 : 0;
                    if (tag.Length > 0)
                    {
                        var pill = new RectangleF(w - 26 - tagW, cy - 11, tagW, 22);
                        if (c.Cancelled) p.StrokeRound(pill, 11, p.T.Faint, 1.2f);
                        else p.FillRound(pill, 11, p.T.Accent);
                        p.TextCenter(tag, Ink.Semi(11f), c.Cancelled ? p.T.Faint : p.T.OnAccent, pill.X + pill.Width / 2, cy);
                    }
                    float tx = 90, tw = w - tx - 26 - (tagW > 0 ? tagW + 10 : 0);
                    p.Text(c.Title, Ink.Semi(13f), faded, tx, cy - 9, tw, StringAlignment.Near);
                    p.Text(SlotNote(c), Ink.Small(11f), p.T.Sub, tx, cy + 10, tw, StringAlignment.Near);
                    if (c.Cancelled) g.DrawLine(p.P(p.T.Faint, 1.2f), tx, cy - 9, tx + Math.Min(tw, p.Measure(c.Title, Ink.Semi(13f))), cy - 9);
                }
                y += ClassRowH;
            }
            g.Restore(st);
        }

        // Once per class, 15 minutes before it starts
        void CheckClasses(DateTime nowUtc)
        {
            if (classes == null) return;
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
                t.Detail = (c.Exam ? "ujian · " : "") + (note.Length > 0 ? note : Math.Ceiling(mins) + " menit lagi");
                break;
            }
        }
    }
}
