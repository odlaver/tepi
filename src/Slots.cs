using System;
using System.Globalization;

namespace Tepi
{
    // Idle side slots: clock, date, battery, weather
    partial class Island
    {
        public static readonly string[] SlotIds = { "", "clock", "date", "battery", "weather" };
        public static readonly string[] SlotNames = { "Kosong", "Jam", "Tanggal", "Baterai", "Cuaca" };

        string SlotText(string key)
        {
            switch (settings.Get(key, ""))
            {
                case "clock": return Fmt.Hm(DateTime.Now);
                case "date": return DateTime.Now.ToString("ddd d", Lang.English ? CultureInfo.InvariantCulture : new CultureInfo("id-ID"));
                case "battery": return hasBattery ? battery + "%" : "";
                case "weather": return weather != null ? weather.Temp + "°" : "";
            }
            return "";
        }

        float SlotWidth(string key)
        {
            string t = SlotText(key);
            if (t.Length == 0) return 0;
            float m = p.G == null ? t.Length * 7.5f : p.Measure(t, Ink.Semi(12f));
            return m + 22;
        }

        // Clock and date need a minute tick
        bool SlotsTimed()
        {
            string l = settings.Get("idle.left", ""), r = settings.Get("idle.right", "");
            return l == "clock" || l == "date" || r == "clock" || r == "date";
        }
    }
}
