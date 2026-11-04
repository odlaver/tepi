using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tepi
{
    // Custom worship time, any faith
    class IbadahItem
    {
        public string Name = "";
        public int Minute;
        // Bit 0 Monday .. bit 6 Sunday
        public int Days = 127;
        public int Before;
        public string Sound = "";
        // Last day warned and announced
        public DateTime Warned = DateTime.MinValue, Announced = DateTime.MinValue;

        public const int Daily = 127, Weekdays = 31, Weekend = 96;

        public static int DayBit(DayOfWeek d) { return 1 << (((int)d + 6) % 7); }

        public bool On(DateTime day) { return (Days & DayBit(day.DayOfWeek)) != 0; }

        public DateTime At(DateTime day) { return day.Date.AddMinutes(Minute); }

        public string Hm { get { return Fmt.Hm(DateTime.Today.AddMinutes(Minute)); } }

        // name|minute|days|before|sound; no '|' in names or paths
        public string Pack()
        {
            return Name.Replace("|", " ") + "|" + Minute.ToString(CultureInfo.InvariantCulture) + "|" + Days.ToString(CultureInfo.InvariantCulture) + "|" + Before.ToString(CultureInfo.InvariantCulture) + "|" + Sound;
        }

        public static IbadahItem Unpack(string s)
        {
            var f = s.Split('|');
            if (f.Length < 5 || f[0].Trim().Length == 0) return null;
            int minute, days, before;
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute) || minute < 0 || minute >= 1440) return null;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || (days & 127) == 0) days = Daily;
            int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out before);
            return new IbadahItem { Name = f[0].Trim(), Minute = minute, Days = days & 127, Before = Math.Max(0, Math.Min(60, before)), Sound = f[4] };
        }

        public string DaysText()
        {
            if (Days == Daily) return Lang.T("Setiap hari");
            if (Days == Weekdays) return Lang.T("Senin sampai Jumat");
            if (Days == Weekend) return Lang.T("Sabtu dan Minggu");
            var names = new List<string>();
            for (int i = 0; i < 7; i++) if ((Days & (1 << i)) != 0) names.Add(Lang.T(DayNames[i]));
            return string.Join(", ", names);
        }

        public static readonly string[] DayNames = { "Senin", "Selasa", "Rabu", "Kamis", "Jumat", "Sabtu", "Minggu" };
    }

    partial class Island
    {
        const int IbadahMax = 12;
        List<IbadahItem> ibadah;

        internal List<IbadahItem> Ibadah()
        {
            if (ibadah != null) return ibadah;
            ibadah = new List<IbadahItem>();
            int n = Math.Min(IbadahMax, settings.GetInt("ibadah.count", 0));
            for (int i = 0; i < n; i++)
            {
                var it = IbadahItem.Unpack(settings.Get("ibadah." + i, ""));
                if (it != null) ibadah.Add(it);
            }
            return ibadah;
        }

        internal void SaveIbadah(List<IbadahItem> list)
        {
            int old = settings.GetInt("ibadah.count", 0);
            for (int i = 0; i < old; i++) settings.Remove("ibadah." + i);
            for (int i = 0; i < list.Count && i < IbadahMax; i++) settings.Set("ibadah." + i, list[i].Pack());
            settings.Set("ibadah.count", Math.Min(IbadahMax, list.Count));
            settings.Save();
            // Edited today: do not ring twice
            var now = DateTime.Now;
            foreach (var it in list)
            {
                if (now >= it.At(now)) it.Announced = it.Warned = now.Date;
            }
            ibadah = list;
        }

        // Each clock tick, like prayers
        bool IbadahTick(DateTime now)
        {
            bool redraw = false;
            foreach (var it in Ibadah())
            {
                if (!it.On(now)) continue;
                var at = it.At(now);
                if (it.Before > 0 && it.Warned != now.Date && now >= at.AddMinutes(-it.Before) && now < at)
                {
                    it.Warned = now.Date;
                    redraw |= ShowWorship(it, false, it.Sound.Length > 0 && it.Sound != SoundNone ? "" : it.Sound);
                }
                if (it.Announced != now.Date && now >= at && now < at.AddMinutes(10))
                {
                    it.Announced = it.Warned = now.Date;
                    redraw |= ShowWorship(it, true, it.Sound);
                }
            }
            return redraw;
        }

        // Prayer index or custom item; due holds until closed
        bool ShowWorship(object what, bool due, string sound)
        {
            var t = ShowToast(View.Pray, due ? 600 : 20, 3, true, true);
            if (t == null) return false;
            t.Data = what;
            t.Flag = due;
            t.Hold = true;
            PlayRemind(sound);
            return true;
        }
    }
}
