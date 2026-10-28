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

        static string lastMessage;
        static DateTime lastUtc;
        static int repeats;

        public static void Write(string message)
        {
            try
            {
                lock (sync)
                {
                    // Repeated lines log once a minute
                    var now = DateTime.UtcNow;
                    if (message == lastMessage && (now - lastUtc).TotalSeconds < 60) { repeats++; return; }
                    Directory.CreateDirectory(Settings.Dir);
                    var info = new FileInfo(FilePath);
                    // Keep one older log file
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        string old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    var sb = new StringBuilder();
                    if (repeats > 0) sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")).Append("(previous line ").Append(repeats).Append(" more times)").Append(Environment.NewLine);
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ")).Append(message).Append(Environment.NewLine);
                    File.AppendAllText(FilePath, sb.ToString());
                    lastMessage = message;
                    lastUtc = now;
                    repeats = 0;
                }
            }
            catch { }
        }
    }

    // key=value store in %APPDATA%\Tepi\settings.ini
    class Settings
    {
        // TEPI_DIR: isolated copy for test runs
        static readonly string testDir = Environment.GetEnvironmentVariable("TEPI_DIR");
        public static bool TestMode { get { return !string.IsNullOrEmpty(testDir); } }

        public static readonly string Dir = TestMode ? Path.GetFullPath(testDir) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tepi");

        // Agent config home; tests get a fake
        public static string Home
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("TEPI_HOME");
                if (!string.IsNullOrEmpty(home)) return home;
                return TestMode ? Path.Combine(Dir, "home") : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        public static bool FakeHome { get { return TestMode || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEPI_HOME")); } }

        // Test copies run beside the real island
        public static string MutexName
        {
            get { return TestMode ? @"Local\TepiIsland-" + (Dir.ToLowerInvariant().GetHashCode() & 0x7fffffff) : @"Local\TepiIsland"; }
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
                // Shared read; saves may replace it
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string raw;
                    while ((raw = sr.ReadLine()) != null)
                    {
                        var line = raw.Trim().TrimStart('﻿');
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
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
                AtomicFile.Write(FilePath, new UTF8Encoding(false).GetBytes(sb.ToString()));
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
                if (Settings.TestMode) { Log.Write("test mode: autostart untouched"); return; }
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

    // Reduced motion: Windows setting or Tepi's
    static class Anim
    {
        public static bool Reduced;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

        // motion.reduce: auto, 1, 0
        public static void Read(Settings settings)
        {
            string v = settings.Get("motion.reduce", "auto");
            if (v == "1") { Reduced = true; return; }
            if (v == "0") { Reduced = false; return; }
            bool on;
            Reduced = SystemParametersInfo(0x1042, 0, out on, 0) && !on;
        }
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

        // Sorted keys: same request, same text
        public static string Canonical(object value)
        {
            var sb = new StringBuilder();
            Canon(value, sb);
            return sb.ToString();
        }

        static void Canon(object o, StringBuilder sb)
        {
            var d = o as Dictionary<string, object>;
            if (d != null)
            {
                var keys = new List<string>(d.Keys);
                keys.Sort(StringComparer.Ordinal);
                sb.Append('{');
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Encode(keys[i])).Append(':');
                    Canon(d[keys[i]], sb);
                }
                sb.Append('}');
                return;
            }
            var list = o as ArrayList;
            var arr = o as object[] ?? (list != null ? list.ToArray() : null);
            if (arr != null)
            {
                sb.Append('[');
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    Canon(arr[i], sb);
                }
                sb.Append(']');
                return;
            }
            sb.Append(Encode(o));
        }

        public static string Sha256(string text)
        {
            using (var h = SHA256.Create())
            {
                var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(64);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
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
            // Objects and arrays: no type names
            if (v is ICollection) return "";
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
            // TLS 1.2+ only, same as Fetch
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch (Exception) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
        }

        // Status 0: no response at all
        public static WebResult Send(string method, string url, Dictionary<string, string> headers, string body, int timeoutMs)
        {
            var result = new WebResult();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Tepi/" + Island.AppVersion + " (+https://github.com/odlaver/tepi)";
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

    // Temp file then swap: no half-written files
    static class AtomicFile
    {
        public static void Write(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            for (int i = 0; ; i++)
            {
                try { File.Replace(tmp, path, null); return; }
                catch (IOException)
                {
                    if (i >= 2) break;
                    System.Threading.Thread.Sleep(15);
                }
            }
            // Replace refused (odd file system): plain overwrite
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
    }

    // DPAPI, bound to the current Windows user
    static class Vault
    {
        public static void Save(string path, string secret)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            AtomicFile.Write(path, data);
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
            try
            {
                if (File.Exists(path)) File.Delete(path);
                // A crashed swap leaves its copy
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
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
        static readonly CultureInfo idCulture = new CultureInfo("id-ID"), enCulture = new CultureInfo("en-US");
        // Dates follow the UI language
        public static CultureInfo Id { get { return Lang.English ? enCulture : idCulture; } }

        public static string Bytes(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0", Id) + " KB";
            if (n < 1024L * 1024 * 1024) return (n / 1048576.0).ToString(n < 100L * 1048576 ? "0.0" : "0", Id) + " MB";
            return (n / 1073741824.0).ToString("0.00", Id) + " GB";
        }

        public static string Hm(DateTime local) { return local.ToString("HH.mm", CultureInfo.InvariantCulture); }

        public static string Ago(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 45) return Lang.T("baru saja");
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + Lang.T(" mnt lalu");
            if (d.TotalHours < 24) return (int)d.TotalHours + Lang.T(" jam lalu");
            return (int)d.TotalDays + Lang.T(" hari lalu");
        }

        // Age without "ago": 5 min, 3 h
        public static string AgoShort(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 45) return Lang.T("baru saja");
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + Lang.T(" mnt");
            if (d.TotalHours < 24) return (int)d.TotalHours + Lang.T(" jam");
            return (int)d.TotalDays + Lang.T(" hari");
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
            if (t.TotalHours < 24) return (int)t.TotalHours + (Lang.English ? "h " : "j ") + t.Minutes + "m";
            return (int)t.TotalDays + (Lang.English ? "d" : "h");
        }

        // Bidi, zero-width, control: shown, never obeyed
        public static string Visible(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool hidden = Hidden(c);
                if (!hidden && c != '\r') { if (sb != null) sb.Append(c); continue; }
                if (sb == null) sb = new StringBuilder(s, 0, i, s.Length + 16);
                if (hidden) sb.Append("<U+").Append(((int)c).ToString("X4")).Append('>');
            }
            return sb == null ? s : sb.ToString();
        }

        // Same set, dropped silently
        public static string StripHidden(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!Hidden(c)) sb.Append(c);
            return sb.ToString();
        }

        static bool Hidden(char c)
        {
            return (c < 0x20 && c != '\n' && c != '\t' && c != '\r') || (c >= 0x7F && c <= 0x9F) || c == 0x00AD || c == 0x061C || c == 0x180E ||
                   (c >= 0x200B && c <= 0x200F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2060 && c <= 0x2069) || c == 0xFEFF;
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
