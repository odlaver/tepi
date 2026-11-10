using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Notifications and calendar card (Win+N)
        sealed class CalendarSheet : GlassSheet
        {
            readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1000 };
            int month, rows, first;
            float notifH;

            const float Pad = 20, RowH = 58, Cell = 44, CellH = 38, Foot = 52, MaxRows = 4;

            public CalendarSheet(Island isle, DockWindow dock) : base(isle, dock, "cal", "Tepi Calendar")
            {
                W = 360;
                refresh.Tick += delegate { Render(); };
            }

            protected override void Opening(Rectangle mon, int barTop)
            {
                rows = isle.NotifOn ? (int)Math.Min(MaxRows, Math.Max(1, isle.notifs.Count)) : 0;
                notifH = isle.NotifOn ? 46 + (isle.notifs.Count == 0 ? 44 : rows * RowH) + 8 : 0;
                H = notifH + 76 + 24 + 6 * CellH + 12 + Foot;
                refresh.Start();
            }

            protected override void Teardown() { refresh.Stop(); refresh.Dispose(); }

            protected override void Backdrop(RectangleF r) { Shelf(r, Foot); }

            protected override void Draw()
            {
                if (isle.NotifOn) DrawNotifs();
                DrawMonth(notifH);
                DrawFoot();
            }

            void DrawNotifs()
            {
                var list = isle.notifs;
                Caption(Lang.T("Notifikasi"), Pad + 4, 26);
                if (list.Count > 0)
                {
                    var f = Ink.Small(12f);
                    string label = Lang.T("Hapus semua");
                    float w = p.Measure(label, f) + 24;
                    var b = new RectangleF(W - Pad - w, 13, w, 26);
                    Hit(b, "clear");
                    p.FillRound(b, 8, hotId == "clear" ? p.T.FillHover : p.T.Fill);
                    p.TextCenter(label, f, p.T.Text, b.X + w / 2, 26);
                }
                if (list.Count == 0)
                {
                    p.Text(Lang.T("Tidak ada notifikasi baru"), Ink.Small(12f), p.T.Sub, Pad + 4, 68, W - Pad * 2, StringAlignment.Near);
                    return;
                }
                first = Math.Max(0, Math.Min(first, list.Count - rows));
                for (int i = 0; i < rows && first + i < list.Count; i++)
                {
                    var item = list[first + i];
                    var row = new RectangleF(Pad - 8, 46 + i * RowH, W - Pad * 2 + 16, RowH - 4);
                    string id = "n" + (first + i), del = "x" + (first + i);
                    bool hover = hotId == id || hotId == del;
                    Hit(row, id);
                    if (hover) p.FillRound(row, 14, pressId == id ? p.T.FillPressed : p.T.Fill);
                    float cy = row.Y + row.Height / 2;
                    var logo = new RectangleF(row.X + 10, cy - 16, 32, 32);
                    if (item.Logo != null) g.DrawImage(item.Logo, logo);
                    else
                    {
                        p.FillRound(logo, 9, p.T.FillHover);
                        string name = item.AppName.Length > 0 ? item.AppName : "?";
                        p.TextCenter(name.Substring(0, 1).ToUpperInvariant(), Ink.Semi(13f), p.T.Text, logo.X + 16, cy);
                    }
                    float tx = row.X + 52;
                    p.Text(isle.NotifTitle(item), Ink.Semi(12.5f), p.T.Text, tx, cy - 9, row.Right - tx - 74, StringAlignment.Near);
                    p.Text(isle.NotifBody(item), Ink.Text(11.5f), p.T.Sub, tx, cy + 9, row.Right - tx - 40, StringAlignment.Near);
                    var x = new RectangleF(row.Right - 36, cy - 14, 28, 28);
                    if (hover)
                    {
                        Hit(x, del);
                        p.Circle(x.X + 14, x.Y + 14, 12, hotId == del ? p.T.FillHover : p.T.Fill);
                        p.Cross(x.X + 14, x.Y + 14, 10, p.T.Sub);
                    }
                    else p.TextRight(Fmt.Ago(item.Time), Ink.Small(11f), p.T.Sub, row.Right - 12, cy - 9);
                }
                // More than fit: a thin scroll mark
                if (list.Count > rows)
                {
                    float track = rows * RowH - 8, thumb = Math.Max(20, track * rows / list.Count);
                    float y = 46 + (track - thumb) * first / Math.Max(1, list.Count - rows);
                    p.FillRound(new RectangleF(W - 7, y, 3, thumb), 1.5f, p.T.Line);
                }
                g.DrawLine(p.P(p.T.Line, 1f), Pad, notifH - 4, W - Pad, notifH - 4);
            }

            void DrawMonth(float top)
            {
                var ci = Fmt.Id;
                var today = DateTime.Today;
                var shown = new DateTime(today.Year, today.Month, 1).AddMonths(month);
                p.Text(today.ToString("dddd, d MMMM", ci), Ink.Semi(15f), p.T.Text, Pad + 4, top + 24, W - Pad * 2, StringAlignment.Near);
                p.Text(shown.ToString("MMMM yyyy", ci), Ink.Small(12.5f), month == 0 ? p.T.Sub : p.T.Accent, Pad + 4, top + 54, 200, StringAlignment.Near);
                if (month != 0) Hit(new RectangleF(Pad, top + 42, 160, 24), "today");
                Button(new RectangleF(W - Pad - 34, top + 38, 32, 32), "next", (char)0xE76C, Lang.T("Bulan berikutnya"));
                Button(new RectangleF(W - Pad - 70, top + 38, 32, 32), "prev", (char)0xE76B, Lang.T("Bulan sebelumnya"));
                var dayOne = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
                float x0 = (W - 7 * Cell) / 2, y = top + 88;
                for (int c = 0; c < 7; c++)
                {
                    var dow = (DayOfWeek)(((int)dayOne + c) % 7);
                    p.TextCenter(ci.DateTimeFormat.GetShortestDayName(dow), Ink.Small(11f), p.T.Sub, x0 + c * Cell + Cell / 2, y);
                }
                y += 16;
                int lead = ((int)shown.DayOfWeek - (int)dayOne + 7) % 7;
                var day = shown.AddDays(-lead);
                for (int r = 0; r < 6; r++)
                    for (int c = 0; c < 7; c++, day = day.AddDays(1))
                    {
                        float cx = x0 + c * Cell + Cell / 2, cy = y + r * CellH + CellH / 2;
                        bool inMonth = day.Month == shown.Month, isToday = day == today;
                        if (isToday) p.Circle(cx, cy, 16, p.T.Accent);
                        p.TextCenter(day.Day.ToString(CultureInfo.InvariantCulture), isToday ? Ink.Semi(12.5f) : Ink.Small(12.5f),
                                     isToday ? p.T.OnAccent : inMonth ? p.T.Text : p.T.Sub, cx, cy);
                    }
            }

            void DrawFoot()
            {
                float cy = H - Foot / 2;
                bool quiet = isle.QuietSwitch;
                var f = Ink.Small(12f);
                string label = Lang.T("Senyap");
                var b = new RectangleF(Pad - 4, cy - 15, p.Measure(label, f) + 46, 30);
                Hit(b, "quiet");
                p.FillRound(b, 15, quiet ? p.T.Accent : hotId == "quiet" ? p.T.FillHover : p.T.Fill);
                p.Icon(Ink.IcoQuiet, 13f, quiet ? p.T.OnAccent : p.T.Text, b.X + 17, cy);
                p.Text(label, f, quiet ? p.T.OnAccent : p.T.Text, b.X + 32, cy, b.Width - 36, StringAlignment.Near);
                Button(new RectangleF(W - Pad - 38, cy - 19, 38, 38), "gear", (char)0xE713, Lang.T("Tanggal dan waktu"));
            }

            static int IndexOf(string id, char tag)
            {
                int i;
                return id != null && id.Length > 1 && id[0] == tag && int.TryParse(id.Substring(1), out i) ? i : -1;
            }

            protected override void Wheel(string id, int delta)
            {
                if (IndexOf(id, 'n') >= 0 || IndexOf(id, 'x') >= 0) first -= Math.Sign(delta);
                else month -= Math.Sign(delta);
            }

            protected override void Clicked(string id, MouseButtons button)
            {
                if (button != MouseButtons.Left) return;
                var list = isle.notifs;
                int n = IndexOf(id, 'n'), x = IndexOf(id, 'x');
                if (n >= 0 && n < list.Count) { var item = list[n]; Done(); isle.OpenNotifApp(item); return; }
                if (x >= 0 && x < list.Count) { isle.RemoveNotifs(new List<NotifItem> { list[x] }); return; }
                switch (id)
                {
                    case "clear": isle.RemoveNotifs(new List<NotifItem>(list)); break;
                    case "prev": month--; break;
                    case "next": month++; break;
                    case "today": month = 0; break;
                    case "quiet": isle.actQuiet(); break;
                    case "gear": Open("ms-settings:dateandtime", null); Done(); break;
                }
            }
        }
    }
}
