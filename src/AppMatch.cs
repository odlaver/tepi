using System;
using System.Collections.Generic;

namespace Tepi
{
    // Name prepared once, scored on every keystroke
    sealed class MatchKey
    {
        public string Lower = "", Flat = "", Initials = "";
        public int[] Starts = new int[0];
        // Settings keywords, lowered
        public string[] Words = new string[0];
        // Short names it answers to
        public string[] Alias = new string[0];
    }

    // Typed query, split once per keystroke
    sealed class MatchQuery
    {
        public readonly string Lower, Flat;
        public readonly string[] Tokens;

        public MatchQuery(string text)
        {
            Lower = (text ?? "").Trim().ToLowerInvariant();
            Flat = AppMatch.Flatten(Lower);
            var parts = Lower.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            Tokens = parts.Length > 1 ? parts : new string[0];
        }

        public bool Empty { get { return Flat.Length == 0; } }
    }

    // Start search ranking, Spotlight style
    static class AppMatch
    {
        public const int Exact = 1000, AliasHit = 950, Prefix = 900, InitialsHit = 850, InitialsPrefix = 820;
        public const int KeywordHit = 840, AliasPrefix = 780, WordStart = 760, KeywordPrefix = 740, Tokens = 700;
        public const int Inside = 600, Typo = 500, Loose = 380;
        // Above this a row earns "Terbaik"
        public const int Strong = 460;

        // Short names people type for common apps
        // Alias keys, then flattened names they open
        static readonly string[][] Aliases =
        {
            new[] { "wa", "whatsapp" },
            new[] { "tg tele", "telegram" },
            new[] { "dc", "discord" },
            new[] { "vsc vscode code vskode", "visualstudiocode" },
            new[] { "ps", "photoshop", "powershell" },
            new[] { "cmd", "commandprompt", "promptperintah" },
            new[] { "ppt", "powerpoint" },
            new[] { "xl xls", "excel" },
            new[] { "yt", "youtube" },
            new[] { "spot", "spotify" },
            new[] { "ss snip", "snippingtool", "alatpemotong" },
            new[] { "calc kalku", "calculator", "kalkulator" },
            new[] { "set pengaturan setelan settings setting", "settings", "pengaturan" },
            new[] { "tm taskmgr", "taskmanager", "pengelolatugas" },
            new[] { "ig", "instagram" },
            new[] { "fe explorer penjelajah", "fileexplorer", "penjelajahfile" },
            new[] { "term wt", "terminal", "windowsterminal" },
            new[] { "ff", "firefox", "mozillafirefox" },
            new[] { "gc", "googlechrome", "chrome" },
        };

        [ThreadStatic] static int[] row;

        public static string Flatten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static MatchKey Prepare(string name, string keywords, bool aliases)
        {
            name = name ?? "";
            var k = new MatchKey { Lower = name.ToLowerInvariant(), Flat = Flatten(name) };
            var starts = new List<int>();
            var ini = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c)) continue;
                char prev = i > 0 ? name[i - 1] : ' ';
                // Word edge, camelCase hump, digit run
                bool start = !char.IsLetterOrDigit(prev) || (char.IsUpper(c) && char.IsLower(prev)) || (char.IsDigit(c) != char.IsDigit(prev));
                if (!start) continue;
                starts.Add(i);
                ini.Append(char.ToLowerInvariant(c));
            }
            k.Starts = starts.ToArray();
            k.Initials = ini.ToString();
            if (!string.IsNullOrEmpty(keywords)) k.Words = keywords.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (aliases) k.Alias = AliasesOf(k.Flat);
            return k;
        }

        static readonly string[] Vendors = { "microsoft", "windows", "adobe", "google", "mozilla" };

        static string[] AliasesOf(string flat)
        {
            if (flat.Length == 0) return new string[0];
            // "Adobe Photoshop 2025" is still Photoshop
            string bare = flat;
            foreach (var v in Vendors) if (bare.Length > v.Length && bare.StartsWith(v, StringComparison.Ordinal)) { bare = bare.Substring(v.Length); break; }
            var found = new List<string>();
            foreach (var r in Aliases)
                for (int i = 1; i < r.Length; i++)
                {
                    bool hit = flat == r[i] || bare == r[i] || (r[i].Length >= 5 && bare.StartsWith(r[i], StringComparison.Ordinal));
                    if (!hit) continue;
                    foreach (var key in r[0].Split(' ')) found.Add(i - 1 + ":" + key);
                    break;
                }
            return found.ToArray();
        }

        // 0: no match; higher is better
        public static int Score(MatchQuery q, MatchKey k)
        {
            string qf = q.Flat, ql = q.Lower;
            if (qf.Length == 0 || k.Flat.Length == 0) return 0;
            if (k.Flat == qf) return Exact;
            int best = 0;
            for (int i = 0; i < k.Alias.Length; i++)
            {
                string a = k.Alias[i];
                int colon = a.IndexOf(':'), rank = a[0] - '0';
                int len = a.Length - colon - 1;
                if (len == qf.Length && string.CompareOrdinal(a, colon + 1, qf, 0, len) == 0) return AliasHit - rank * 4;
                if (qf.Length >= 2 && len > qf.Length && string.CompareOrdinal(a, colon + 1, qf, 0, qf.Length) == 0) best = AliasPrefix;
            }
            if (k.Lower.StartsWith(ql, StringComparison.Ordinal) || k.Flat.StartsWith(qf, StringComparison.Ordinal))
                return Prefix - Math.Min(40, k.Flat.Length - qf.Length);
            if (qf.Length >= 2 && k.Initials.Length >= qf.Length && k.Initials.StartsWith(qf, StringComparison.Ordinal))
                return k.Initials.Length == qf.Length ? InitialsHit : InitialsPrefix - Math.Min(20, k.Initials.Length - qf.Length);
            if (best > 0) return best;
            int kw = Keyword(q, k);
            if (kw == KeywordHit) return kw;
            for (int w = 1; w < k.Starts.Length; w++)
                if (string.CompareOrdinal(k.Lower, k.Starts[w], ql, 0, ql.Length) == 0) return WordStart - Math.Min(30, w * 4);
            if (kw >= KeywordPrefix) return kw;
            if (q.Tokens.Length > 1 && AllTokens(q.Tokens, k)) return Tokens;
            // "wa" inside "software" is noise
            if (qf.Length >= 3)
            {
                int at = k.Lower.IndexOf(ql, StringComparison.Ordinal);
                if (at < 0) at = k.Flat.IndexOf(qf, StringComparison.Ordinal);
                if (at >= 0) return Inside - Math.Min(50, at);
            }
            if (qf.Length >= 4)
            {
                int d = Near(qf, k);
                if (d <= (qf.Length >= 7 ? 2 : 1)) return Math.Max(kw, Typo - (d - 1) * 60);
            }
            if (kw > 0) return kw;
            return qf.Length >= 3 ? Subsequence(qf, k) : 0;
        }

        // Settings keywords: exact, prefix, near typo
        static int Keyword(MatchQuery q, MatchKey k)
        {
            if (k.Words.Length == 0) return 0;
            string ql = q.Lower;
            int best = 0;
            foreach (var w in k.Words)
            {
                if (w == ql) return KeywordHit;
                if (ql.Length >= 2 && w.StartsWith(ql, StringComparison.Ordinal)) best = Math.Max(best, KeywordPrefix);
                else if (ql.Length >= 4 && w.Length >= 4 && best < Typo && Distance(ql, w, 2) <= 1) best = Typo - 20;
            }
            if (best == 0 && q.Tokens.Length > 1)
            {
                // "night light", "mode gelap"
                bool all = true;
                foreach (var t in q.Tokens)
                {
                    bool one = k.Lower.Contains(t);
                    foreach (var w in k.Words) if (!one && w.StartsWith(t, StringComparison.Ordinal)) one = true;
                    if (!one) { all = false; break; }
                }
                if (all) best = Tokens - 10;
            }
            return best;
        }

        // Each typed word starts a name word
        static bool AllTokens(string[] tokens, MatchKey k)
        {
            foreach (var t in tokens)
            {
                bool one = false;
                foreach (int s in k.Starts) if (string.CompareOrdinal(k.Lower, s, t, 0, t.Length) == 0) { one = true; break; }
                if (!one) return false;
            }
            return true;
        }

        // Fewest edits to a word start
        static int Near(string qf, MatchKey k)
        {
            int best = 9;
            for (int w = 0; w < k.Starts.Length && best > 0; w++)
            {
                int s = k.Starts[w], e = s;
                while (e < k.Lower.Length && char.IsLetterOrDigit(k.Lower[e])) e++;
                int len = e - s;
                if (len < 3) continue;
                for (int take = qf.Length - 1; take <= qf.Length + 1; take++)
                    if (take >= 3 && take <= len) best = Math.Min(best, Distance(qf, k.Lower, s, take, 2));
            }
            for (int take = qf.Length - 1; take <= qf.Length + 1 && best > 0; take++)
                if (take >= 3 && take <= k.Flat.Length) best = Math.Min(best, Distance(qf, k.Flat, 0, take, 2));
            return best;
        }

        // Letters in order from a word start
        static int Subsequence(string qf, MatchKey k)
        {
            string s = k.Lower;
            if (k.Flat.Length < qf.Length) return 0;
            for (int w = 0; w < k.Starts.Length; w++)
            {
                if (k.Initials[w] != qf[0]) continue;
                int j = 0, gaps = 0, last = -1;
                for (int i = k.Starts[w]; i < s.Length && j < qf.Length; i++)
                {
                    if (s[i] != qf[j]) continue;
                    if (last >= 0 && i != last + 1)
                    {
                        // Jump to a word, or skip one letter
                        bool word = Array.BinarySearch(k.Starts, i) >= 0;
                        if (!word && i - last > 2) break;
                        gaps++;
                    }
                    last = i;
                    j++;
                }
                if (j < qf.Length || gaps > (qf.Length - 1) / 3) continue;
                return Loose - gaps * 12 - Math.Min(30, k.Flat.Length - qf.Length) - w * 4;
            }
            return 0;
        }

        public static int Distance(string a, string b, int cap) { return Distance(a, b, 0, b.Length, cap); }

        // Levenshtein on a slice, capped
        public static int Distance(string a, string b, int from, int len, int cap)
        {
            if (Math.Abs(a.Length - len) > cap) return cap + 1;
            var r = row;
            if (r == null || r.Length < len + 1) row = r = new int[Math.Max(64, len + 1)];
            for (int j = 0; j <= len; j++) r[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int prev = r[0], low = i;
                r[0] = i;
                for (int j = 1; j <= len; j++)
                {
                    int keep = r[j];
                    int v = Math.Min(Math.Min(r[j] + 1, r[j - 1] + 1), prev + (a[i - 1] == b[from + j - 1] ? 0 : 1));
                    r[j] = v;
                    prev = keep;
                    if (v < low) low = v;
                }
                if (low > cap) return cap + 1;
            }
            return r[len];
        }
    }
}
