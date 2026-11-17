using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Prayer times from solar altitude, computed locally
    static class PrayTimes
    {
        public static readonly string[] Names = { "Subuh", "Terbit", "Dzuhur", "Ashar", "Maghrib", "Isya" };

        // Kemenag: Subuh 20°, Isya 18°, Ashar shadow 1x
        const double SubuhDip = 20, IsyaDip = 18, HorizonDip = 0.833, AsharShadow = 1;
        static readonly int[] Ihtiyat = { 2, -2, 2, 2, 2, 2 };

        public static DateTime[] For(DateTime day, double lat, double lng, double tz)
        {
            double jd0 = DayNumber(day) - lng / 360.0;
            double noon = Transit(jd0, 0.5);
            double[] local =
            {
                Cross(jd0, lat, -SubuhDip, -1),
                Cross(jd0, lat, -HorizonDip, -1),
                noon,
                Ashar(jd0, lat),
                Cross(jd0, lat, -HorizonDip, 1),
                Cross(jd0, lat, -IsyaDip, 1),
            };
            var times = new DateTime[6];
            for (int i = 0; i < 6; i++)
            {
                var t = day.Date.AddHours(local[i] + tz - lng / 15).AddMinutes(Ihtiyat[i]);
                // Next whole minute, like printed schedules
                long rest = t.Ticks % TimeSpan.TicksPerMinute;
                times[i] = rest == 0 ? t : t.AddTicks(TimeSpan.TicksPerMinute - rest);
            }
            return times;
        }

        // Julian day at 0h UT, Meeus chapter 7
        static double DayNumber(DateTime d)
        {
            int y = d.Year, m = d.Month;
            if (m < 3) { y--; m += 12; }
            int century = y / 100;
            int gregorian = 2 - century + century / 4;
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d.Day + gregorian - 1524.5;
        }

        const double ToRad = Math.PI / 180;

        static double Wrap(double v, double range)
        {
            double r = v % range;
            return r < 0 ? r + range : r;
        }

        // USNO approximate solar coordinates
        struct SunState { public double Decl, EqHours; }

        static SunState Sun(double jd)
        {
            double n = jd - 2451545.0;
            double anomaly = Wrap(357.529 + 0.98560028 * n, 360) * ToRad;
            double meanLong = Wrap(280.459 + 0.98564736 * n, 360);
            double eclLong = Wrap(meanLong + 1.915 * Math.Sin(anomaly) + 0.020 * Math.Sin(2 * anomaly), 360) * ToRad;
            double tilt = (23.439 - 0.00000036 * n) * ToRad;
            double rightAsc = Math.Atan2(Math.Cos(tilt) * Math.Sin(eclLong), Math.Cos(eclLong)) / ToRad / 15;
            return new SunState
            {
                Decl = Math.Asin(Math.Sin(tilt) * Math.Sin(eclLong)) / ToRad,
                EqHours = meanLong / 15 - Wrap(rightAsc, 24),
            };
        }

        // Local mean hour of solar noon
        static double Transit(double jd0, double fraction) { return Wrap(12 - Sun(jd0 + fraction).EqHours, 24); }

        // Hours from noon to that altitude
        static double HalfArc(double lat, double decl, double altitude)
        {
            double c = (Math.Sin(altitude * ToRad) - Math.Sin(decl * ToRad) * Math.Sin(lat * ToRad)) / (Math.Cos(decl * ToRad) * Math.Cos(lat * ToRad));
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) / ToRad / 15;
        }

        // Refined from noon until the time settles
        static double Cross(double jd0, double lat, double altitude, int side)
        {
            double hour = 12;
            for (int pass = 0; pass < 4; pass++)
            {
                var s = Sun(jd0 + hour / 24);
                hour = Transit(jd0, hour / 24) + side * HalfArc(lat, s.Decl, altitude);
            }
            return hour;
        }

        // Shadow equals object plus its noon shadow
        static double Ashar(double jd0, double lat)
        {
            double hour = 15;
            for (int pass = 0; pass < 4; pass++)
            {
                var s = Sun(jd0 + hour / 24);
                double altitude = Math.Atan(1 / (AsharShadow + Math.Tan(Math.Abs(lat - s.Decl) * ToRad))) / ToRad;
                hour = Transit(jd0, hour / 24) + HalfArc(lat, s.Decl, altitude);
            }
            return hour;
        }
    }

    // City presets: name, latitude, longitude, UTC offset
    static class Cities
    {
        public static readonly object[][] List =
        {
            new object[] { "Banda Aceh", 5.5483, 95.3238, 7 }, new object[] { "Medan", 3.5952, 98.6722, 7 }, new object[] { "Padang", -0.9471, 100.4172, 7 },
            new object[] { "Pekanbaru", 0.5071, 101.4478, 7 }, new object[] { "Palembang", -2.9761, 104.7754, 7 }, new object[] { "Bandar Lampung", -5.4292, 105.261, 7 },
            new object[] { "Serang", -6.12, 106.1503, 7 }, new object[] { "Tangerang", -6.1783, 106.6319, 7 }, new object[] { "Jakarta", -6.2088, 106.8456, 7 },
            new object[] { "Depok", -6.4025, 106.7942, 7 }, new object[] { "Bogor", -6.595, 106.8166, 7 }, new object[] { "Bekasi", -6.2383, 106.9756, 7 },
            new object[] { "Bandung", -6.9175, 107.6191, 7 }, new object[] { "Semarang", -6.9667, 110.4167, 7 }, new object[] { "Yogyakarta", -7.7956, 110.3695, 7 },
            new object[] { "Surakarta", -7.5755, 110.8243, 7 }, new object[] { "Surabaya", -7.2575, 112.7521, 7 }, new object[] { "Malang", -7.9666, 112.6326, 7 },
            new object[] { "Pontianak", -0.0263, 109.3425, 7 }, new object[] { "Banjarmasin", -3.3186, 114.5944, 8 }, new object[] { "Balikpapan", -1.2379, 116.8529, 8 },
            new object[] { "Samarinda", -0.5022, 117.1536, 8 }, new object[] { "Denpasar", -8.6705, 115.2126, 8 }, new object[] { "Mataram", -8.5833, 116.1167, 8 },
            new object[] { "Makassar", -5.1477, 119.4327, 8 }, new object[] { "Manado", 1.4748, 124.8421, 8 }, new object[] { "Kupang", -10.1772, 123.607, 8 },
            new object[] { "Ambon", -3.6954, 128.1814, 9 }, new object[] { "Jayapura", -2.5916, 140.669, 9 },
        };
    }

    enum RemindKind { None, Drink, Eyes, Stretch }

    // Pengingat: prayer, water, eyes, moving
    partial class Island
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        static double IdleSeconds()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            return (uint)(Environment.TickCount - (int)li.dwTime) / 1000.0;
        }

        DateTime[] prayToday;
        DateTime prayDay = DateTime.MinValue;
        int prayAnnounced = -1, prayWarned = -1;
        DateTime activeSinceUtc = DateTime.UtcNow, lastDrinkUtc = DateTime.UtcNow, lastEyesUtc = DateTime.UtcNow, lastStretchUtc = DateTime.UtcNow, snoozeUntilUtc = DateTime.MinValue;
        DateTime restUntilUtc = DateTime.MinValue;
        RemindKind remindShown;

        bool PrayOn { get { return settings.GetBool("pray", false) && PrayCity() != null; } }

        object[] PrayCity()
        {
            string want = settings.Get("pray.city", "");
            foreach (var c in Cities.List) if ((string)c[0] == want) return c;
            return null;
        }

        readonly string[] prayHm = new string[6];
        string prayTomorrowHm = "", prayFor = "", todayKey = "";
        DateTime prayTomorrow, todayKeyDay;

        // Today's times plus sunrise, cached daily
        DateTime[] Prayers()
        {
            var city = PrayCity();
            if (city == null) return null;
            string name = (string)city[0];
            if (prayToday == null || prayDay != DateTime.Today || !ReferenceEquals(name, prayFor))
            {
                double lat = Convert.ToDouble(city[1]), lng = Convert.ToDouble(city[2]), tz = Convert.ToDouble(city[3]);
                prayToday = PrayTimes.For(DateTime.Today, lat, lng, tz);
                prayTomorrow = PrayTimes.For(DateTime.Today.AddDays(1), lat, lng, tz)[0];
                for (int i = 0; i < 6; i++) prayHm[i] = Fmt.Hm(prayToday[i]);
                prayTomorrowHm = Fmt.Hm(prayTomorrow);
                prayDay = DateTime.Today;
                prayFor = name;
                prayAnnounced = prayWarned = -1;
            }
            return prayToday;
        }

        // Next prayer; after Isya, tomorrow's Subuh
        int NextPrayer(out DateTime at)
        {
            at = DateTime.MaxValue;
            var times = Prayers();
            if (times == null) return -1;
            var now = DateTime.Now;
            for (int i = 0; i < 6; i++)
                if (i != 1 && times[i] > now) { at = times[i]; return i; }
            at = prayTomorrow;
            return 0;
        }

        // Day stamp built once per day
        string TodayKey()
        {
            if (todayKeyDay != DateTime.Today)
            {
                todayKeyDay = DateTime.Today;
                todayKey = todayKeyDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return todayKey;
        }

        int DrinkCount()
        {
            if (settings.Get("drink.day", "") != TodayKey()) return 0;
            return settings.GetInt("drink.count", 0);
        }

        int DrinkGoal { get { return Math.Max(1, Math.Min(20, settings.GetInt("drink.goal", 8))); } }

        void Drank()
        {
            settings.Set("drink.day", TodayKey());
            settings.Set("drink.count", DrinkCount() + 1);
            settings.Save();
            lastDrinkUtc = DateTime.UtcNow;
            if (toast != null && toast.View == View.Remind) DismissToast();
            Kick();
        }

        void Snooze(int minutes)
        {
            snoozeUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
            if (toast != null && toast.View == View.Remind) DismissToast();
        }

        // Each clock tick
        bool RemindTick(DateTime nowUtc)
        {
            bool redraw = false;
            var now = DateTime.Now;
            // In use: input within 5 minutes
            double idle = IdleSeconds();
            if (idle > 300) { activeSinceUtc = nowUtc; lastEyesUtc = nowUtc; lastStretchUtc = nowUtc; }
            if (PrayOn)
            {
                var times = Prayers();
                int before = Math.Max(0, settings.GetInt("pray.before", 10));
                for (int i = 0; i < 6; i++)
                {
                    if (i == 1) continue;
                    if (before > 0 && prayWarned < i && now >= times[i].AddMinutes(-before) && now < times[i])
                    {
                        prayWarned = i;
                        redraw |= ShowWorship(i, false, settings.Get("sound.pray.before", ""));
                    }
                    if (prayAnnounced < i && now >= times[i] && now < times[i].AddMinutes(10))
                    {
                        prayAnnounced = prayWarned = i;
                        redraw |= ShowWorship(i, true, settings.Get("sound.pray", ""));
                    }
                }
            }
            redraw |= IbadahTick(now);
            // Nudges wait: away, fullscreen, or quiet
            if (idle > 120 || fullscreen || Quiet || PrompterOn || nowUtc < snoozeUntilUtc || toast != null) return redraw;
            int hour = now.Hour;
            if (settings.GetBool("drink", false) && hour >= 7 && hour < 22 && DrinkCount() < DrinkGoal &&
                (nowUtc - lastDrinkUtc).TotalMinutes >= Math.Max(15, settings.GetInt("drink.every", 60)))
                return Nudge(RemindKind.Drink, ref lastDrinkUtc);
            if (settings.GetBool("eyes", false) && (nowUtc - lastEyesUtc).TotalMinutes >= 20)
                return Nudge(RemindKind.Eyes, ref lastEyesUtc);
            if (settings.GetBool("stretch", false) && (nowUtc - lastStretchUtc).TotalMinutes >= Math.Max(30, settings.GetInt("stretch.every", 60)))
                return Nudge(RemindKind.Stretch, ref lastStretchUtc);
            return redraw;
        }

        bool Nudge(RemindKind kind, ref DateTime last)
        {
            last = DateTime.UtcNow;
            remindShown = kind;
            var t = ShowToast(View.Remind, 20, 2, true);
            if (t == null) return false;
            t.Data = kind;
            t.Hold = true;
            PlayRemind(settings.Get("sound." + WellKeys[(int)kind - 1], ""));
            if (kind == RemindKind.Eyes) restUntilUtc = DateTime.UtcNow.AddSeconds(20);
            if (!Anim.Reduced) topGap.Velocity += 80;
            return true;
        }

        // Compact views

        const char IcoPray = '';
        static readonly string[] Secs = MakeSecs();
        readonly Roller prayRoller = new Roller(), eyesRoller = new Roller();
        Action actCity, actPraySwitch, actDrinkPlus, actLater, actRemindOk, actRemindDone;
        readonly Action[] wellActs = new Action[3];
        Func<DateTime, double> prayLeftPaint, eyesLeftPaint;
        Action<DateTime> eyesRingPaint;
        static readonly string[] WellKeys = { "drink", "eyes", "stretch" }, WellIds = { "swdrink", "sweyes", "swstretch" };

        static string[] MakeSecs()
        {
            var s = new string[21];
            for (int i = 0; i < s.Length; i++) s[i] = i.ToString(CultureInfo.InvariantCulture);
            return s;
        }

        // Delegates built once, never per frame
        void RemindActs()
        {
            if (actCity != null) return;
            actCity = CityMenu;
            actPraySwitch = delegate
            {
                if (PrayCity() == null) { CityMenu(); return; }
                settings.Toggle("pray", false);
                Kick();
            };
            actDrinkPlus = Drank;
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                wellActs[i] = delegate { ToggleWell(k); };
            }
            actLater = delegate { Snooze(15); };
            actRemindOk = delegate
            {
                if (toast != null && toast.Data is RemindKind && (RemindKind)toast.Data == RemindKind.Drink) Drank();
                else DismissToast();
            };
            actRemindDone = DismissToast;
            prayLeftPaint = PaintPrayLeft;
            eyesLeftPaint = PaintEyesLeft;
            eyesRingPaint = PaintEyesRing;
        }

        void ToggleWell(int i)
        {
            string key = WellKeys[i];
            settings.Toggle(key, false);
            // Start counting from now, not from launch
            if (i == 0) lastDrinkUtc = DateTime.UtcNow;
            if (i == 1) lastEyesUtc = DateTime.UtcNow;
            if (i == 2) lastStretchUtc = DateTime.UtcNow;
            Kick();
        }

        // Leading glyph in a round tile
        void LeadTile(float cy, char glyph)
        {
            p.Module(new RectangleF(11, cy - 15, 30, 30), 15, p.T.Fill);
            p.Icon(glyph, 15f, p.T.Text, 26, cy);
        }

        int prayTitleKey = -1, prayTailKey = -1;
        string prayTitleText = "", prayTailText = "";

        float PrayWidth()
        {
            if (toast == null || p.G == null) return 280;
            return Clamp(p.Measure(PrayTitle(), Ink.Headline) + p.Measure(PrayTail(), Ink.Semi(13f)) + 52 + 44 + DoneW + 12, 300, 460);
        }

        string PrayTitle()
        {
            bool due = toast != null && toast.Flag;
            var custom = toast != null ? toast.Data as IbadahItem : null;
            if (custom != null) return due ? custom.Name : Lang.F("{0} sebentar lagi", custom.Name);
            int i = toast != null && toast.Data is int ? (int)toast.Data : 0;
            int key = i * 4 + (due ? 2 : 0) + (Lang.English ? 1 : 0);
            if (key != prayTitleKey)
            {
                prayTitleKey = key;
                string name = Lang.T(PrayTimes.Names[i]);
                prayTitleText = due ? Lang.F("Waktu {0}", name) : Lang.F("{0} sebentar lagi", name);
            }
            return prayTitleText;
        }

        // Time when due, else minutes left
        string PrayTail()
        {
            var custom = toast != null ? toast.Data as IbadahItem : null;
            if (custom != null)
            {
                if (toast.Flag) return custom.Hm;
                return Fmt.Left(custom.At(DateTime.Now) - DateTime.Now);
            }
            var times = Prayers();
            if (times == null || toast == null || !(toast.Data is int)) return "";
            int i = (int)toast.Data;
            if (toast.Flag) return prayHm[i];
            var left = times[i] - DateTime.Now;
            int key = i * 100000 + Math.Max(0, (int)left.TotalMinutes) * 2 + (Lang.English ? 1 : 0);
            if (key != prayTailKey) { prayTailKey = key; prayTailText = Fmt.Left(left); }
            return prayTailText;
        }

        void DrawPray(float w, float h, DateTime now)
        {
            if (toast == null || !(toast.Data is int || toast.Data is IbadahItem)) return;
            RemindActs();
            float cy = h / 2;
            LeadTile(cy, IcoPray);
            var done = new RectangleF(w - 14 - DoneW, cy - RemindPillH / 2, DoneW, RemindPillH);
            string tail = PrayTail();
            float tw = tail.Length > 0 ? p.Measure(tail, Ink.Semi(13f)) + 12 : 0;
            p.Text(PrayTitle(), Ink.Headline, p.T.Text, 52, cy, done.X - 12 - 52 - tw, StringAlignment.Near);
            if (tw > 0) p.TextRight(tail, Ink.Semi(13f), p.T.Sub, done.X - 12, cy);
            Pill(done, "prayDone", Lang.T("Selesai"), PillKind.Primary, actRemindDone);
        }

        const float RemindPillH = 30, LaterW = 66, DoneW = 72;

        float RemindWidth()
        {
            if (p.G == null) return 360;
            var k = remindShown;
            float text = Math.Max(p.Measure(RemindTitle(k), Ink.Headline), p.Measure(RemindDetail(k), Ink.Callout));
            float tail = k == RemindKind.Eyes ? 44 : LaterW + DoneW + 8 + 14;
            return Clamp(52 + text + 16 + tail, 300, 440);
        }

        string RemindTitle(RemindKind k)
        {
            if (k == RemindKind.Drink) return Lang.T("Waktunya minum");
            if (k == RemindKind.Eyes) return Lang.T("Istirahatkan mata");
            return Lang.T("Waktunya bergerak");
        }

        int drinkDetailKey = -1;
        string drinkDetail = "";

        string RemindDetail(RemindKind k)
        {
            if (k == RemindKind.Drink)
            {
                int key = DrinkCount() * 1000 + DrinkGoal * 2 + (Lang.English ? 1 : 0);
                if (key != drinkDetailKey) { drinkDetailKey = key; drinkDetail = Lang.F("{0} dari {1} gelas", DrinkCount(), DrinkGoal); }
                return drinkDetail;
            }
            if (k == RemindKind.Eyes) return Lang.T("pandang jauh 20 detik");
            return Lang.T("berdiri dan regangkan badan");
        }

        static char RemindGlyph(RemindKind k) { return k == RemindKind.Drink ? '' : k == RemindKind.Eyes ? '' : ''; }

        float remindCy, remindRight;

        void DrawRemind(float w, float h, DateTime now)
        {
            if (toast == null || !(toast.Data is RemindKind)) return;
            RemindActs();
            var k = (RemindKind)toast.Data;
            float cy = h / 2, tx = 52;
            remindCy = cy;
            if (k == RemindKind.Eyes)
            {
                AddHit(new RectangleF(0, 0, w, h), "remindDone", actRemindDone);
                // Ring and seconds count down alone
                p.Icon(RemindGlyph(k), 13f, p.T.Text, 26, cy);
                LiveRect(new RectangleF(11, cy - 15, 30, 30), 15f, eyesRingPaint);
                remindRight = w - 20;
                LiveRect(new RectangleF(w - 64, cy - 14, 46, 28), eyesLeftPaint);
                p.Text(RemindTitle(k), Ink.Headline, p.T.Text, tx, cy - 9, w - tx - 70, StringAlignment.Near);
                p.Text(RemindDetail(k), Ink.Callout, p.T.Sub, tx, cy + 10, w - tx - 70, StringAlignment.Near);
                return;
            }
            LeadTile(cy, RemindGlyph(k));
            var done = new RectangleF(w - 14 - DoneW, cy - RemindPillH / 2, DoneW, RemindPillH);
            var later = new RectangleF(done.X - 8 - LaterW, done.Y, LaterW, RemindPillH);
            float room = later.X - 12 - tx;
            p.Text(RemindTitle(k), Ink.Headline, p.T.Text, tx, cy - 9, room, StringAlignment.Near);
            p.Text(RemindDetail(k), Ink.Callout, p.T.Sub, tx, cy + 10, room, StringAlignment.Near);
            Pill(later, "remindLater", Lang.T("Nanti"), PillKind.Secondary, actLater);
            Pill(done, "remindOk", Lang.T("Sudah"), PillKind.Primary, actRemindOk);
        }

        void PaintEyesRing(DateTime t)
        {
            float left = (float)Math.Max(0, (restUntilUtc - t).TotalSeconds);
            Ring(26, remindCy, 13.5f, left / 20, p.T.Accent, 2.4f);
        }

        // Whole seconds roll, then wait
        double PaintEyesLeft(DateTime t)
        {
            double left = Math.Max(0, (restUntilUtc - t).TotalSeconds);
            int n = Math.Min(20, (int)Math.Ceiling(left));
            eyesRoller.Set(Secs[n]);
            eyesRoller.Draw(p, Ink.Display(17f), p.T.Text, remindRight, remindCy, StringAlignment.Far, t);
            if (eyesRoller.Moving(t)) return 1 / 60.0;
            if (n == 0) return 0;
            double wait = left - Math.Floor(left);
            return (wait < 0.001 ? 1 : wait) + 0.004;
        }

        // Page

        const float PrayCardH = 102, PrayEmptyH = 56, WellRowH = 40, WellPad = 4;

        float RemindHeight()
        {
            float pray = PrayCity() != null ? PrayCardH : PrayEmptyH;
            return Math.Min(MaxH, PanelTop + 2 + pray + Ink.ModuleGap + 3 * WellRowH + 2 * WellPad + Ink.ModuleInset);
        }

        void DrawRemindPage(float w, float h, DateTime now)
        {
            RemindActs();
            float x0 = Ink.ModuleInset, full = w - 2 * x0, y = PanelTop + 2, keep = p.Alpha, dy;
            bool city = PrayCity() != null;
            float ph = city ? PrayCardH : PrayEmptyH;
            p.Alpha = keep * RowIn(0, now, out dy);
            if (city) DrawPrayCard(new RectangleF(x0, y + dy, full, ph));
            else DrawPrayEmpty(new RectangleF(x0, y + dy, full, ph));
            y += ph + Ink.ModuleGap;
            p.Alpha = keep * RowIn(1, now, out dy);
            var well = new RectangleF(x0, y + dy, full, 3 * WellRowH + 2 * WellPad);
            Module(well, Ink.RadiusModule);
            for (int i = 0; i < 3; i++)
                DrawWellRow(new RectangleF(well.X + 6, well.Y + WellPad + i * WellRowH, well.Width - 12, WellRowH), i, i < 2);
            p.Alpha = keep;
        }

        // No city yet: one row, one action
        void DrawPrayEmpty(RectangleF r)
        {
            Module(r, Ink.RadiusModule);
            float cy = r.Y + r.Height / 2, pad = Ink.ModulePad;
            p.Icon(IcoPray, 16f, p.T.Text, r.X + pad + 8, cy);
            string label = Lang.T("Pilih kota");
            float bw = p.Measure(label, Ink.Semi(13f)) + 32;
            var pill = new RectangleF(r.Right - pad - bw, cy - 16, bw, 32);
            p.Text(Lang.T("Waktu sholat"), Ink.Headline, p.T.Text, r.X + pad + 28, cy, pill.X - 12 - r.X - pad - 28, StringAlignment.Near);
            Pill(pill, "prayCity", label, PillKind.Primary, actCity);
        }

        static readonly int[] PrayRow = { 0, 2, 3, 4, 5 };
        float prayLeftX, prayLeftY;
        int prayLeftKey = -1;
        string prayLeftText = "";
        DateTime prayNextUtc;

        void DrawPrayCard(RectangleF r)
        {
            Module(r, Ink.RadiusModule);
            var times = Prayers();
            if (times == null) return;
            DateTime nextAt;
            int next = NextPrayer(out nextAt);
            bool today = nextAt.Date == DateTime.Today;
            float pad = Ink.ModulePad, hy = r.Y + 27, x = r.X + pad;
            // Header right: city chip, then switch
            var sw = new RectangleF(r.Right - pad - 38, hy - 11, 38, 22);
            string name = (string)PrayCity()[0];
            var chipFont = Ink.Semi(12.5f);
            float cw = Math.Min(120, p.Measure(name, chipFont));
            var chip = new RectangleF(sw.X - 12 - cw - 38, hy - 14, cw + 38, 28);
            AddHit(chip, "prayCity", actCity);
            float s = Squish("prayCity", chip, 0.94f);
            p.Module(chip, 14, FillFor("prayCity"));
            p.Text(name, chipFont, p.T.Text, chip.X + 12, hy, cw + 2, StringAlignment.Near);
            p.Symbol(Sym.ChevronDown, chip.Right - 15, hy + 0.5f, 9f, p.T.Sub);
            Unsquish(chip, s);
            Switch(sw, "praySwitch", settings.GetBool("pray", false), actPraySwitch);
            // Hero: name, time, minutes left
            string hero = Lang.T(PrayTimes.Names[next]), at = today ? prayHm[next] : prayTomorrowHm;
            float room = chip.X - 12 - x, nw = Math.Min(room, p.Measure(hero, Ink.Title1)), tw = p.Measure(at, Ink.Title1);
            p.Text(hero, Ink.Title1, p.T.Text, x, hy, room, StringAlignment.Near);
            float lx = x + nw + 8;
            // Narrow panel: the row shows the time
            if (lx + tw + 10 + 76 <= chip.X - 12)
            {
                p.Text(at, Ink.Title1, p.T.Sub, lx, hy, tw + 2, StringAlignment.Near);
                lx += tw + 10;
            }
            prayNextUtc = nextAt.ToUniversalTime();
            prayLeftX = lx;
            prayLeftY = hy + 1.5f;
            LiveRect(new RectangleF(lx - 2, prayLeftY - 10, Math.Max(10, chip.X - 8 - lx), 20), prayLeftPaint);
            // Five times: past dims, next takes accent
            var nowLocal = DateTime.Now;
            float top = r.Y + 52, cellW = (r.Width - 16) / PrayRow.Length;
            for (int k = 0; k < PrayRow.Length; k++)
            {
                int i = PrayRow[k];
                float cx = r.X + 8 + cellW * (k + 0.5f);
                bool isNext = today && i == next, past = times[i] <= nowLocal;
                if (isNext) p.FillRound(new RectangleF(cx - cellW / 2 + 2, top, cellW - 4, 40), Ink.RadiusControl, Ink.A(p.T.Accent, 0.16f));
                Color ink = isNext ? p.T.Text : past ? p.T.Hint : p.T.Sub;
                p.TextCenter(Lang.T(PrayTimes.Names[i]), Ink.Caption, isNext ? p.T.Accent : ink, cx, top + 12);
                p.TextCenter(prayHm[i], Ink.Semi(13.5f), isNext ? p.T.Text : past ? p.T.Hint : p.T.Text, cx, top + 28);
            }
        }

        // Minutes left; rolls, then sleeps
        double PaintPrayLeft(DateTime t)
        {
            var left = prayNextUtc - t;
            if (left.Ticks <= 0) return 0;
            int key = (int)left.TotalMinutes * 2 + (Lang.English ? 1 : 0);
            if (key != prayLeftKey) { prayLeftKey = key; prayLeftText = Lang.F("dalam {0}", Fmt.Left(left)); }
            prayRoller.Set(prayLeftText);
            prayRoller.Draw(p, Ink.Callout, p.T.Sub, prayLeftX, prayLeftY, StringAlignment.Near, t);
            if (prayRoller.Moving(t)) return 1 / 60.0;
            return left.TotalSeconds - Math.Floor(left.TotalMinutes) * 60 + 0.02;
        }

        void CityMenu()
        {
            var m = new ContextMenuStrip();
            string cur = settings.Get("pray.city", "");
            foreach (var c in Cities.List)
            {
                string name = (string)c[0];
                m.Items.Add(new ToolStripMenuItem(name, null, delegate
                {
                    settings.Set("pray.city", name);
                    settings.Set("pray", true);
                    settings.Save();
                    prayToday = null;
                    Kick();
                }) { Checked = name == cur });
            }
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(MenuAt("prayCity"));
        }


        // Keyboard opens under the control
        Point MenuAt(string id)
        {
            if (keyboardMode)
                foreach (var h in hits)
                    if (h.Id == id) return PointToScreen(new Point((int)(h.Rect.X * scale), (int)(h.Rect.Bottom * scale)));
            return Control.MousePosition;
        }


        // Slim row: glyph, title, switch
        void DrawWellRow(RectangleF r, int i, bool separator)
        {
            float cy = r.Y + r.Height / 2, tx = r.X + 38;
            var kind = i == 0 ? RemindKind.Drink : i == 1 ? RemindKind.Eyes : RemindKind.Stretch;
            p.Icon(RemindGlyph(kind), 15f, p.T.Text, r.X + 18, cy);
            var sw = new RectangleF(r.Right - 10 - 38, cy - 11, 38, 22);
            float right = sw.X - 14;
            if (i == 0)
            {
                // +1, then glasses as dots
                var plus = new RectangleF(right - 40, cy - 13, 40, 26);
                Pill(plus, "drinkPlus", "+1", PillKind.Secondary, actDrinkPlus);
                right = plus.X - 12;
                right -= DrinkDots(right, cy) + 12;
            }
            Switch(sw, WellIds[i], settings.GetBool(WellKeys[i], false), wellActs[i]);
            string title = i == 0 ? Lang.T("Minum air") : i == 1 ? Lang.T("Istirahat mata 20-20-20") : Lang.T("Peregangan");
            p.Text(title, Ink.Body, p.T.Text, tx, cy, right - tx, StringAlignment.Near);
            if (separator) p.G.DrawLine(p.P(p.T.Line, 1f), tx, r.Bottom - 0.5f, r.Right - 12, r.Bottom - 0.5f);
        }

        // Dots up to 12, else a bar
        float DrinkDots(float right, float cy)
        {
            int goal = DrinkGoal, n = Math.Min(goal, DrinkCount());
            // The count in words, so the dots need no guessing
            var cf = Ink.SmallSemi(11f);
            string count = n + "/" + goal;
            float cw = p.Measure(count, cf);
            p.TextRight(count, cf, p.T.Sub, right, cy);
            right -= cw + 10;
            if (goal <= 12)
            {
                const float d = 7, gap = 4;
                float width = goal * d + (goal - 1) * gap, x = right - width;
                for (int k = 0; k < goal; k++)
                {
                    float cx = x + k * (d + gap) + d / 2;
                    if (k < n) p.Circle(cx, cy, d / 2, p.T.Accent);
                    else p.G.DrawEllipse(p.P(p.T.Hint, 1.2f), cx - d / 2 + 0.6f, cy - d / 2 + 0.6f, d - 1.2f, d - 1.2f);
                }
                return width + cw + 10;
            }
            var bar = new RectangleF(right - 96, cy - 2.5f, 96, 5);
            p.FillRound(bar, 2.5f, p.T.Quaternary);
            if (n > 0) p.FillRound(bar, 2.5f, p.Split(bar.X, bar.Width, bar.X + bar.Width * n / goal, p.T.Accent, Color.Transparent));
            return 96 + cw + 10;
        }
    }
}
