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
            public int Temp, Code, Feels = int.MinValue;
            // Minutes until rain, -1 when dry
            public int RainIn = -1;
            public bool Raining;
            public DateTime Utc;
            // Next two hours in 15-minute steps, mm
            public double[] Rain15 = new double[0];
            public readonly List<WeatherHour> Hours = new List<WeatherHour>();
            public readonly List<WeatherDay> Days = new List<WeatherDay>();
        }

        class WeatherHour { public DateTime Utc; public int Temp, Code, Pop; }

        class WeatherDay { public DateTime Date; public int Max, Min, Code, Pop; }

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
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=temperature_2m,weather_code,apparent_temperature&minutely_15=precipitation&forecast_minutely_15=8" +
                "&hourly=temperature_2m,weather_code,precipitation_probability&forecast_hours=12&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=5&timezone=auto",
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
            double feels = Json.Num(cur, "apparent_temperature", double.NaN);
            if (!double.IsNaN(feels)) w.Feels = (int)Math.Round(feels);
            // Times come in the city's own clock
            double offset = Json.Num(root, "utc_offset_seconds", 0);
            var m = Json.Obj(root, "minutely_15");
            var times = Json.Arr(m, "time");
            var rain = Json.Arr(m, "precipitation");
            int n = times != null && rain != null ? Math.Min(times.Length, rain.Length) : 0;
            w.Rain15 = new double[n];
            for (int i = 0; i < n; i++)
            {
                double mm = Convert.ToDouble(rain[i] ?? 0, CultureInfo.InvariantCulture);
                w.Rain15[i] = mm;
                DateTime at;
                if (w.RainIn >= 0 || mm < 0.2 || !CityTime(times[i], offset, out at)) continue;
                w.RainIn = Math.Max(0, (int)Math.Round((at - DateTime.UtcNow).TotalMinutes));
            }
            var hourly = Json.Obj(root, "hourly");
            var ht = Json.Arr(hourly, "time");
            var htemp = Json.Arr(hourly, "temperature_2m");
            var hcode = Json.Arr(hourly, "weather_code");
            var hpop = Json.Arr(hourly, "precipitation_probability");
            for (int i = 0; ht != null && htemp != null && hcode != null && i < Math.Min(ht.Length, Math.Min(htemp.Length, hcode.Length)); i++)
            {
                DateTime at;
                if (!CityTime(ht[i], offset, out at)) continue;
                w.Hours.Add(new WeatherHour { Utc = at, Temp = Whole(htemp[i]), Code = Whole(hcode[i]), Pop = hpop != null && i < hpop.Length ? Whole(hpop[i]) : -1 });
            }
            var daily = Json.Obj(root, "daily");
            var dt = Json.Arr(daily, "time");
            var dmax = Json.Arr(daily, "temperature_2m_max");
            var dmin = Json.Arr(daily, "temperature_2m_min");
            var dcode = Json.Arr(daily, "weather_code");
            var dpop = Json.Arr(daily, "precipitation_probability_max");
            for (int i = 0; dt != null && dmax != null && dmin != null && dcode != null && i < Math.Min(dt.Length, Math.Min(dmax.Length, Math.Min(dmin.Length, dcode.Length))); i++)
            {
                DateTime day;
                if (!DateTime.TryParseExact(Convert.ToString(dt[i], CultureInfo.InvariantCulture), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) continue;
                w.Days.Add(new WeatherDay { Date = day, Max = Whole(dmax[i]), Min = Whole(dmin[i]), Code = Whole(dcode[i]), Pop = dpop != null && i < dpop.Length ? Whole(dpop[i]) : -1 });
            }
            return w;
        }

        static bool CityTime(object text, double offsetSeconds, out DateTime utc)
        {
            DateTime local;
            utc = DateTime.MinValue;
            if (!DateTime.TryParse(Convert.ToString(text, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) return false;
            utc = DateTime.SpecifyKind(local.AddSeconds(-offsetSeconds), DateTimeKind.Utc);
            return true;
        }

        static int Whole(object v)
        {
            if (v == null) return 0;
            return (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture));
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
