using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Tepi
{
    // What Start opened, how often, how lately
    static class StartUsage
    {
        sealed class Use { public int Count; public DateTime Last; }

        static Dictionary<string, Use> uses;
        // Query typed, then the pick it led to
        static Dictionary<string, KeyValuePair<DateTime, string>> picks;

        static string FilePath { get { return Path.Combine(Settings.Dir, "start-usage.txt"); } }

        static void Load()
        {
            if (uses != null) return;
            uses = new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase);
            picks = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = line.Split('\t');
                    long ticks;
                    if (f.Length != 4 || !long.TryParse(f[2], out ticks)) continue;
                    var when = new DateTime(ticks, DateTimeKind.Utc);
                    int n;
                    if (f[0] == "q") picks[f[1]] = new KeyValuePair<DateTime, string>(when, f[3]);
                    else if (int.TryParse(f[1], out n)) uses[f[3]] = new Use { Count = n, Last = when };
                }
            }
            catch (Exception ex) { Log.Write("start usage: " + ex.Message); }
        }

        // Frequent and recent rise; weekly decay
        public static int Boost(string key)
        {
            Load();
            Use u;
            if (key.Length == 0 || !uses.TryGetValue(key, out u)) return 0;
            double days = Math.Max(0, (DateTime.UtcNow - u.Last).TotalDays);
            double fresh = 1 / (1 + days / 7);
            return (int)Math.Min(160, 60 * Math.Log(1 + u.Count, 2) * (0.35 + 0.65 * fresh));
        }

        public static string PickFor(string query)
        {
            Load();
            KeyValuePair<DateTime, string> p;
            return query.Length > 0 && picks.TryGetValue(query, out p) ? p.Value : null;
        }

        public static void Record(string key, string query)
        {
            if (key.Length == 0) return;
            Load();
            Use u;
            if (!uses.TryGetValue(key, out u)) uses[key] = u = new Use();
            u.Count++;
            u.Last = DateTime.UtcNow;
            if (query.Length > 0 && query.Length <= 40) picks[query] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, key);
            Save();
        }

        static void Save()
        {
            var keep = new List<KeyValuePair<string, Use>>(uses);
            keep.Sort((a, b) => Boost(b.Key).CompareTo(Boost(a.Key)));
            var recent = new List<KeyValuePair<string, KeyValuePair<DateTime, string>>>(picks);
            recent.Sort((a, b) => b.Value.Key.CompareTo(a.Value.Key));
            var sb = new StringBuilder();
            for (int i = 0; i < keep.Count && i < 300; i++)
                sb.Append("u\t").Append(keep[i].Value.Count).Append('\t').Append(keep[i].Value.Last.Ticks).Append('\t').Append(Clean(keep[i].Key)).Append('\n');
            for (int i = 0; i < recent.Count && i < 200; i++)
                sb.Append("q\t").Append(Clean(recent[i].Key)).Append('\t').Append(recent[i].Value.Key.Ticks).Append('\t').Append(Clean(recent[i].Value.Value)).Append('\n');
            try { AtomicFile.Write(FilePath, Encoding.UTF8.GetBytes(sb.ToString())); }
            catch (Exception ex) { Log.Write("start usage save: " + ex.Message); }
        }

        static string Clean(string s) { return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }
    }

    sealed class FoundFile
    {
        public string Path = "", Name = "";
        public DateTime Modified;
        public bool Folder;
    }

    // Windows Search index, asked off the UI thread
    sealed class IndexSearch : IDisposable
    {
        // Null list: index missing or failed
        readonly Action<int, List<FoundFile>> done;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        string want;
        int wantGen;
        volatile bool run = true;
        Thread thread;

        public const int DebounceMs = 150;

        public IndexSearch(Action<int, List<FoundFile>> done) { this.done = done; }

        public void Ask(string query, int gen)
        {
            lock (gate)
            {
                want = query;
                wantGen = gen;
                if (thread == null)
                {
                    thread = new Thread(Loop) { IsBackground = true, Name = "start index" };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }
            wake.Set();
        }

        public void Dispose()
        {
            run = false;
            wake.Set();
        }

        void Loop()
        {
            object conn = null;
            bool broken = false;
            try
            {
                while (run)
                {
                    wake.WaitOne();
                    // Typing still going: wait for a pause
                    while (run && wake.WaitOne(DebounceMs)) { }
                    if (!run) break;
                    string q;
                    int gen;
                    lock (gate) { q = want; gen = wantGen; want = null; }
                    if (q == null) continue;
                    List<FoundFile> found = null;
                    bool stale = false;
                    if (!broken)
                        try
                        {
                            if (conn == null) conn = Connect();
                            found = Find(conn, q);
                        }
                        catch (StaleException) { stale = true; }
                        catch (Exception ex)
                        {
                            Log.Write("start index: " + ex.Message);
                            // No provider: stop; failed query retries
                            if (conn == null) broken = true;
                            else Release(ref conn);
                        }
                    if (stale) continue;
                    lock (gate) if (want != null) continue;
                    if (run) done(gen, found);
                }
            }
            finally { Release(ref conn); wake.Dispose(); }
        }

        sealed class StaleException : Exception { }

        static object Connect()
        {
            var t = Type.GetTypeFromProgID("ADODB.Connection");
            if (t == null) throw new InvalidOperationException("ADODB missing");
            object conn = Activator.CreateInstance(t);
            Set(conn, "CommandTimeout", 3);
            Call(conn, "Open", "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            return conn;
        }

        // Word starts first, then anywhere
        List<FoundFile> Find(object conn, string query)
        {
            var list = new List<FoundFile>();
            string term = Term(query);
            if (term.Length == 0) return list;
            var words = term.Split(' ');
            var contains = new StringBuilder();
            foreach (var w in words) contains.Append(contains.Length > 0 ? " AND " : "").Append('"').Append(w).Append("*\"");
            string head = "SELECT TOP 25 System.ItemPathDisplay, System.DateModified, System.ItemType FROM SystemIndex WHERE (" + Scopes() + ")"
                + " AND System.ItemPathDisplay NOT LIKE '%\\.%' AND System.ItemPathDisplay NOT LIKE '%\\node_modules\\%' AND ";
            const string tail = " ORDER BY System.DateModified DESC";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Read(conn, head + "CONTAINS(System.FileName, '" + contains + "')" + tail, list, seen);
            lock (gate) if (want != null) throw new StaleException();
            if (list.Count < 25) Read(conn, head + "System.FileName LIKE '%" + term + "%'" + tail, list, seen);
            return list;
        }

        static string scopes;

        // User folders; profile root holds caches
        static string Scopes()
        {
            if (scopes != null) return scopes;
            var dirs = new List<string>();
            foreach (var f in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos })
                dirs.Add(Environment.GetFolderPath(f));
            dirs.Add(ShellApp.ExpandKnown("{374DE290-123F-4565-9164-39C4925E467B}"));
            dirs.Add(Environment.GetEnvironmentVariable("OneDrive"));
            var keep = new List<string>();
            foreach (var d in dirs)
            {
                if (string.IsNullOrEmpty(d) || d[0] == '{' || !Directory.Exists(d)) continue;
                string root = d.TrimEnd('\\') + "\\";
                bool inside = false;
                foreach (var o in dirs)
                    if (!string.IsNullOrEmpty(o) && o != d && root.StartsWith(o.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) inside = true;
                if (!inside && !keep.Contains(d)) keep.Add(d);
            }
            var sb = new StringBuilder();
            foreach (var d in keep) sb.Append(sb.Length > 0 ? " OR " : "").Append("SCOPE='file:").Append(d.Replace("'", "''")).Append('\'');
            return scopes = sb.Length > 0 ? sb.ToString() : "SCOPE='file:" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace("'", "''") + "'";
        }

        // Quotes and wildcards would break the SQL
        static string Term(string q)
        {
            var sb = new StringBuilder();
            foreach (char c in q.Trim())
            {
                if (c == '\'' || c == '"' || c == '%' || c == '_' || c == '[' || c == ']' || c == '*' || char.IsControl(c)) continue;
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        static void Read(object conn, string sql, List<FoundFile> into, HashSet<string> seen)
        {
            object rs = null;
            try
            {
                rs = Call(conn, "Execute", sql);
                if ((bool)Get(rs, "EOF")) return;
                var rows = Call(rs, "GetRows") as object[,];
                if (rows == null) return;
                for (int i = 0; i < rows.GetLength(1); i++)
                {
                    string path = rows[0, i] as string;
                    if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                    var when = rows[1, i] is DateTime ? DateTime.SpecifyKind((DateTime)rows[1, i], DateTimeKind.Utc) : DateTime.MinValue;
                    into.Add(new FoundFile { Path = path, Name = System.IO.Path.GetFileName(path), Modified = when, Folder = (rows[2, i] as string) == "Directory" });
                }
            }
            finally
            {
                if (rs != null)
                {
                    try { Call(rs, "Close"); } catch (Exception) { }
                    Marshal.ReleaseComObject(rs);
                }
            }
        }

        static void Release(ref object conn)
        {
            if (conn == null) return;
            try { Call(conn, "Close"); } catch (Exception) { }
            try { Marshal.ReleaseComObject(conn); } catch (Exception) { }
            conn = null;
        }

        static object Call(object o, string name, params object[] args) { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
        static object Get(object o, string name) { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
        static void Set(object o, string name, object v) { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { v }); }
    }
}
