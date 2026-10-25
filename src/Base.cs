using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Tepi
{
    static class Log
    {
        static readonly object sync = new object();

        public static string FilePath { get { return Path.Combine(Settings.Dir, "tepi.log"); } }

        public static void Write(string message)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 512 * 1024) info.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // key=value store in %APPDATA%\Tepi\settings.ini
    class Settings
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tepi"); }
        }

        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public Settings() { Load(); }

        public void Load()
        {
            values.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim().TrimStart('﻿');
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var keys = new List<string>(values.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder();
                foreach (var k in keys) sb.Append(k).Append('=').Append(values[k]).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
        }

        public bool Has(string key) { return values.ContainsKey(key) && values[key].Length > 0; }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        public int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string v = Get(key, "");
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public void Set(string key, object value)
        {
            if (value is bool) value = (bool)value ? "1" : "0";
            values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public void Toggle(string key, bool fallback)
        {
            Set(key, !GetBool(key, fallback));
            Save();
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Tepi") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("Tepi", "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                    else k.DeleteValue("Tepi", false);
                }
            }
        }
    }

    // Damped spring; K/D retuned for jelly
    class Spring
    {
        public float Value, Velocity, Target, K, D;
        readonly float epsilon;

        public Spring(float value, float k, float d, float epsilon)
        {
            Value = Target = value;
            K = k;
            D = d;
            this.epsilon = epsilon;
        }

        public void Step(float dt)
        {
            if (Settled) { Value = Target; Velocity = 0; return; }
            int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.002f));
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                Velocity += (-K * (Value - Target) - D * Velocity) * h;
                Value += Velocity * h;
            }
        }

        public bool Settled { get { return Math.Abs(Value - Target) < epsilon && Math.Abs(Velocity) < epsilon * 6; } }

        public void Snap(float v) { Value = Target = v; Velocity = 0; }
    }

    static class Ease
    {
        public static float Clamp01(float t) { return t < 0 ? 0 : t > 1 ? 1 : t; }
        public static float OutCubic(float t) { t = Clamp01(t); float u = 1 - t; return 1 - u * u * u; }
        public static float InOutCubic(float t) { t = Clamp01(t); return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2; }
        public static float OutExpo(float t) { t = Clamp01(t); return t >= 1 ? 1 : 1 - (float)Math.Pow(2, -10 * t); }

        public static float OutBack(float t)
        {
            t = Clamp01(t);
            const float c1 = 1.5f, c3 = c1 + 1;
            float u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }

        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
    }

    // Loose JSON access over JavaScriptSerializer output
    static class Json
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(text);
        }

        public static string Encode(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(value);
        }

        public static Dictionary<string, object> Obj(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static object[] Arr(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new object[0];
            var a = v as object[];
            if (a != null) return a;
            var list = v as ArrayList;
            return list != null ? list.ToArray() : new object[0];
        }

        public static string Str(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(object o, string key, double fallback)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        public static DateTime Time(object o, string key)
        {
            DateTime t;
            string s = Str(o, key);
            if (s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return DateTime.MinValue;
        }
    }

    class WebResult
    {
        public int Status;
        public string Body = "";
        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    static class Web
    {
        static Web()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)(3072 | 12288); }
            catch (Exception) { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
        }

        // Status 0: no response at all
        public static WebResult Send(string method, string url, Dictionary<string, string> headers, string body, int timeoutMs)
        {
            var result = new WebResult();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Tepi/1.1 (+https://github.com/odlaver/tepi)";
            if (headers != null)
                foreach (var h in headers)
                {
                    if (h.Key == "Content-Type") req.ContentType = h.Value;
                    else req.Headers[h.Key] = h.Value;
                }
            try
            {
                if (body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    if (req.ContentType == null) req.ContentType = "application/json";
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    result.Status = (int)resp.StatusCode;
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) result.Body = sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http == null) { result.Status = 0; result.Body = ex.Message; return result; }
                result.Status = (int)http.StatusCode;
                try { using (var sr = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) result.Body = sr.ReadToEnd(); }
                catch (Exception) { }
            }
            // Dropped mid-body and the like: treat as no response
            catch (Exception ex) { result.Status = 0; result.Body = ex.Message; }
            return result;
        }
    }

    // DPAPI, bound to the current Windows user
    static class Vault
    {
        public static void Save(string path, string secret)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, data);
        }

        public static string Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Log.Write("vault: " + ex.Message); return null; }
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Write("vault delete: " + ex.Message); }
        }
    }

    // TEPI_FPS=1: per-section frame cost
    static class Prof
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("TEPI_FPS") == "1";
        static readonly Dictionary<string, double> sums = new Dictionary<string, double>();
        static readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        public static double T() { return On ? watch.Elapsed.TotalMilliseconds : 0; }

        static readonly Dictionary<string, double> frame = new Dictionary<string, double>();

        public static void Add(string key, double since)
        {
            if (!On) return;
            double took = watch.Elapsed.TotalMilliseconds - since, v;
            sums.TryGetValue(key, out v);
            sums[key] = v + took;
            frame.TryGetValue(key, out v);
            frame[key] = v + took;
        }

        // One frame's breakdown, if slow
        public static string EndFrame(double ms)
        {
            string slow = null;
            if (On && ms > 12)
            {
                var sb = new StringBuilder();
                foreach (var kv in frame) sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("0.0")).Append(' ');
                slow = sb.ToString();
            }
            frame.Clear();
            return slow;
        }

        public static string Dump(int frames)
        {
            var sb = new StringBuilder();
            foreach (var kv in sums) sb.Append(kv.Key).Append('=').Append((kv.Value / Math.Max(1, frames)).ToString("0.00")).Append(' ');
            sums.Clear();
            return sb.ToString();
        }
    }

    static class Fmt
    {
        public static readonly CultureInfo Id = new CultureInfo("id-ID");

        public static string Hm(DateTime local) { return local.ToString("HH.mm", CultureInfo.InvariantCulture); }

        public static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 45) return "baru saja";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " mnt lalu";
            if (d.TotalHours < 24) return (int)d.TotalHours + " jam lalu";
            return (int)d.TotalDays + " hari lalu";
        }

        public static string Clock(double ms)
        {
            int s = (int)Math.Max(0, ms / 1000);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // Compact countdown: 2j 14m, 48m, 3h
        public static string Left(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 24) return (int)t.TotalHours + "j " + t.Minutes + "m";
            return (int)t.TotalDays + "h";
        }

        public static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }
}
