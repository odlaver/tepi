using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Weather card (Win+W)
        sealed class WeatherSheet : GlassSheet
        {
            const float Pad = 20, HourH = 78, DayH = 34, Foot = 44;

            public WeatherSheet(Island isle, DockWindow dock) : base(isle, dock, "weather", "Tepi Weather") { W = 340; }

            WeatherNow Now
            {
                get
                {
                    var w = isle.weather;
                    return w != null && (DateTime.UtcNow - w.Utc).TotalHours <= 3 ? w : null;
                }
            }

            protected override void Opening(Rectangle mon, int barTop)
            {
                var w = Now;
                if (!isle.WeatherOn) H = 170;
                else if (w == null) H = 130;
                else H = 196 + (w.Hours.Count > 0 ? HourH + 12 : 0) + Math.Min(5, w.Days.Count) * DayH + 16 + Foot;
            }

            protected override void Backdrop(RectangleF r) { if (Now != null && isle.WeatherOn) Shelf(r, Foot); }

            protected override void Draw()
            {
                if (!isle.WeatherOn) { DrawSetup(); return; }
                var city = isle.PrayCity();
                p.Icon((char)0xE707, 13f, p.T.Sub, Pad + 10, 26);
                p.Text(city != null ? (string)city[0] : Lang.T("Cuaca"), Ink.Semi(14f), p.T.Text, Pad + 26, 26, W - Pad * 2 - 26, StringAlignment.Near);
                var w = Now;
                if (w == null)
                {
                    p.Text(Lang.T("Memuat cuaca…"), Ink.Small(12.5f), p.T.Sub, Pad + 4, 76, W - Pad * 2, StringAlignment.Near);
                    return;
                }
                DrawNow(w);
                float y = 196;
                if (w.Hours.Count > 0) { DrawHours(w, y); y += HourH + 12; }
                DrawDays(w, y);
                p.Text(Lang.F("Diperbarui {0}", Fmt.Ago(w.Utc)), Ink.Small(11f), p.T.Sub, Pad + 4, H - Foot / 2, 200, StringAlignment.Near);
                p.TextRight("Open-Meteo", Ink.Small(11f), p.T.Sub, W - Pad - 4, H - Foot / 2);
            }

            void DrawSetup()
            {
                Caption(Lang.T("Cuaca"), Pad + 4, 26);
                p.Text(Lang.T("Pilih kota dulu, untuk cuaca dan peringatan hujan"), Ink.Small(12f), p.T.Sub, Pad + 4, 60, W - Pad * 2, StringAlignment.Near);
                var f = Ink.Semi(12.5f);
                string label = Lang.T("Pilih kota");
                var b = new RectangleF(Pad, 96, p.Measure(label, f) + 36, 36);
                Hit(b, "setup");
                p.FillRound(b, 18, hotId == "setup" ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f) : p.T.Accent);
                p.TextCenter(label, f, p.T.OnAccent, b.X + b.Width / 2, b.Y + b.Height / 2);
            }

            void DrawNow(WeatherNow w)
            {
                string temp = w.Temp + "°";
                var big = Ink.Display(44f);
                p.Text(temp, big, p.T.Text, Pad, 82, 160, StringAlignment.Near);
                float tx = Pad + p.Measure(temp, big) + 12;
                p.Text(WeatherWord(w.Code), Ink.Semi(14f), p.T.Text, tx, 72, W - tx - 70, StringAlignment.Near);
                if (w.Feels != int.MinValue) p.Text(Lang.F("Terasa {0}°", w.Feels), Ink.Small(12f), p.T.Sub, tx, 93, W - tx - 70, StringAlignment.Near);
                DockWindow.WeatherIcon(p, w.Code, W - Pad - 26, 80, 34f, p.T.Text);
                string rain = w.Raining ? Lang.T("Sedang hujan") : w.RainIn < 0 ? Lang.T("Belum ada tanda hujan 2 jam ke depan")
                            : w.RainIn < 15 ? Lang.T("Sebentar lagi hujan") : Lang.F("Hujan sekitar {0} menit lagi", (w.RainIn + 7) / 15 * 15);
                bool wet = w.Raining || w.RainIn >= 0;
                p.Text(rain, Ink.Small(12f), wet ? p.T.Accent : p.T.Sub, Pad + 4, 130, W - Pad * 2, StringAlignment.Near);
                // Rain, per quarter hour
                int n = w.Rain15.Length;
                if (n == 0) return;
                float gap = 4, bw = (W - Pad * 2 - gap * (n - 1)) / n, baseY = 176;
                for (int i = 0; i < n; i++)
                {
                    float hgt = 4 + (float)Math.Min(1, w.Rain15[i] / 4) * 24;
                    var bar = new RectangleF(Pad + i * (bw + gap), baseY - hgt, bw, hgt);
                    p.FillRound(bar, 2, w.Rain15[i] >= 0.2 ? p.T.Accent : p.T.Fill);
                }
                p.Text(Lang.T("Sekarang"), Ink.Small(11f), p.T.Sub, Pad, baseY + 10, 80, StringAlignment.Near);
                p.TextRight(Lang.T("2 jam"), Ink.Small(11f), p.T.Sub, W - Pad, baseY + 10);
            }

            void DrawHours(WeatherNow w, float top)
            {
                var box = new RectangleF(Pad - 6, top, W - Pad * 2 + 12, HourH);
                p.Module(box, 16, p.T.Fill);
                int n = Math.Min(6, w.Hours.Count);
                float col = box.Width / n;
                for (int i = 0; i < n; i++)
                {
                    var hr = w.Hours[i];
                    float cx = box.X + col * i + col / 2;
                    p.TextCenter(i == 0 ? Lang.T("Kini") : Fmt.Hm(hr.Utc.ToLocalTime()), Ink.Small(11f), p.T.Sub, cx, top + 17);
                    DockWindow.WeatherIcon(p, hr.Code, cx, top + 40, 17f, p.T.Text);
                    p.TextCenter(hr.Temp + "°", Ink.Semi(12.5f), p.T.Text, cx, top + 62);
                }
            }

            void DrawDays(WeatherNow w, float top)
            {
                int n = Math.Min(5, w.Days.Count);
                if (n == 0) return;
                int lo = int.MaxValue, hi = int.MinValue;
                for (int i = 0; i < n; i++) { lo = Math.Min(lo, w.Days[i].Min); hi = Math.Max(hi, w.Days[i].Max); }
                // Day, icon, rain, low, bar, high
                float span = Math.Max(1, hi - lo), barL = W - Pad - 102, barR = W - Pad - 40;
                var today = DateTime.Today;
                for (int i = 0; i < n; i++)
                {
                    var d = w.Days[i];
                    float cy = top + i * DayH + DayH / 2;
                    string name = d.Date == today ? Lang.T("Hari ini") : d.Date == today.AddDays(1) ? Lang.T("Besok") : d.Date.ToString("dddd", Fmt.Id);
                    p.Text(name, Ink.Small(12.5f), p.T.Text, Pad + 4, cy, 84, StringAlignment.Near);
                    DockWindow.WeatherIcon(p, d.Code, Pad + 102, cy, 15f, p.T.Text);
                    if (d.Pop >= 30) p.Text(d.Pop + "%", Ink.Small(11f), p.T.Accent, Pad + 116, cy, 40, StringAlignment.Near);
                    p.TextRight(d.Min + "°", Ink.Small(12f), p.T.Sub, barL - 8, cy);
                    p.FillRound(new RectangleF(barL, cy - 2, barR - barL, 4), 2, p.T.Fill);
                    float a = barL + (barR - barL) * (d.Min - lo) / span, b = barL + (barR - barL) * (d.Max - lo) / span;
                    p.FillRound(new RectangleF(a, cy - 2, Math.Max(4, b - a), 4), 2, p.T.Accent);
                    p.Text(d.Max + "°", Ink.Semi(12f), p.T.Text, barR + 8, cy, 40, StringAlignment.Near);
                }
            }

            protected override void Clicked(string id, MouseButtons button)
            {
                if (button == MouseButtons.Left && id == "setup") { Done(); isle.OpenPrefs("system"); }
            }
        }
    }
}
