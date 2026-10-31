using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Tepi
{
    // Open-Meteo, for the prayer city
    partial class Island
    {
        class WeatherNow
        {
            public int Temp, Code;
            // Minutes until rain, -1 when dry
            public int RainIn = -1;
            public bool Raining;
            public DateTime Utc;
        }

        WeatherNow weather;
        DateTime weatherAskedUtc = DateTime.MinValue;
        bool weatherBusy, rainWarned;

        bool WeatherOn { get { return settings.GetBool("weather", false) && PrayCity() != null; } }

        void TickWeather()
        {
            if (!WeatherOn) { weather = null; return; }
            if (dormant || weatherBusy || (DateTime.UtcNow - weatherAskedUtc).TotalMinutes < 30) return;
            weatherAskedUtc = DateTime.UtcNow;
            weatherBusy = true;
            var c = PrayCity();
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=temperature_2m,weather_code&minutely_15=precipitation&forecast_minutely_15=8&timezone=GMT",
                Convert.ToDouble(c[1]), Convert.ToDouble(c[2]));
            ThreadPool.QueueUserWorkItem(delegate
            {
                WeatherNow w = null;
                try
                {
                    var r = Fetch.Send("GET", url, null, null, 64 * 1024, 15000);
                    if (r.Ok) w = ParseWeather(Json.Parse(r.Text));
                    else Log.Write("weather: " + r.Status);
                }
                catch (Exception ex) { Log.Write("weather: " + ex.GetType().Name); }
                Post(() => { weatherBusy = false; OnWeather(w); });
            });
        }

        static WeatherNow ParseWeather(object root)
        {
            var cur = Json.Obj(root, "current");
            if (cur == null) return null;
            var w = new WeatherNow { Temp = (int)Math.Round(Json.Num(cur, "temperature_2m", 0)), Code = (int)Json.Num(cur, "weather_code", 0), Utc = DateTime.UtcNow };
            w.Raining = IsRain(w.Code);
            var m = Json.Obj(root, "minutely_15");
            var times = Json.Arr(m, "time");
            var rain = Json.Arr(m, "precipitation");
            for (int i = 0; times != null && rain != null && i < Math.Min(times.Length, rain.Length); i++)
            {
                double mm = Convert.ToDouble(rain[i], CultureInfo.InvariantCulture);
                DateTime at;
                if (mm < 0.2 || !DateTime.TryParse(Convert.ToString(times[i], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) continue;
                w.RainIn = Math.Max(0, (int)Math.Round((at - DateTime.UtcNow).TotalMinutes));
                break;
            }
            return w;
        }

        static bool IsRain(int code) { return (code >= 51 && code <= 67) || (code >= 80 && code <= 82) || code >= 95; }

        void OnWeather(WeatherNow w)
        {
            if (w == null) return;
            weather = w;
            // One heads-up per wet spell
            if (w.Raining || w.RainIn < 0) { if (w.RainIn < 0 && !w.Raining) rainWarned = false; }
            else if (!rainWarned && w.RainIn <= 120)
            {
                rainWarned = true;
                var t = ShowToast(View.Info, 6, 2, false);
                if (t != null)
                {
                    t.Glyph = '';
                    t.Title = w.RainIn < 15 ? Lang.T("Sebentar lagi hujan") : Lang.F("Hujan sekitar {0} menit lagi", (w.RainIn + 7) / 15 * 15);
                    t.Detail = (string)PrayCity()[0];
                }
            }
            if (expanded && page == PageHome) Kick();
        }

        static string WeatherWord(int code)
        {
            if (code == 0) return Lang.T("Cerah");
            if (code <= 2) return Lang.T("Cerah berawan");
            if (code == 3) return Lang.T("Berawan");
            if (code == 45 || code == 48) return Lang.T("Berkabut");
            if (code >= 51 && code <= 57) return Lang.T("Gerimis");
            if (code >= 95) return Lang.T("Badai petir");
            if (IsRain(code)) return Lang.T("Hujan");
            return Lang.T("Berawan");
        }

        // "29° Berawan", for under the clock
        string WeatherLine()
        {
            var w = weather;
            if (w == null || (DateTime.UtcNow - w.Utc).TotalHours > 3) return null;
            return w.Temp + "° " + WeatherWord(w.Code);
        }
    }
}
