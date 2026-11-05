using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Tepi
{
    // Dictation vocabulary: terms, replacements, snippets
    class Words
    {
        public readonly List<string> Terms = new List<string>();
        public readonly List<KeyValuePair<string, string>> Swaps = new List<KeyValuePair<string, string>>();
        public readonly List<KeyValuePair<string, string>> Snippets = new List<KeyValuePair<string, string>>();
        List<KeyValuePair<Regex, string>> termRx, swapRx;

        public static string FilePath { get { return Path.Combine(Settings.Dir, "dikte.txt"); } }

        public static Words Load()
        {
            var w = new Words();
            try { if (File.Exists(FilePath)) w.Parse(File.ReadAllText(FilePath, Encoding.UTF8)); }
            catch (Exception ex) { Log.Write("dikte.txt: " + ex.Message); }
            return w;
        }

        // Sections [kamus], [ganti], [snippet]
        public void Parse(string text)
        {
            Terms.Clear(); Swaps.Clear(); Snippets.Clear();
            string section = "kamus";
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant(); continue; }
                if (section == "kamus") { if (!Terms.Contains(line)) Terms.Add(line); continue; }
                int at = line.IndexOf("=>", StringComparison.Ordinal);
                if (at <= 0) continue;
                string from = line.Substring(0, at).Trim(), to = line.Substring(at + 2).Trim();
                if (from.Length == 0) continue;
                if (section == "ganti") Swaps.Add(new KeyValuePair<string, string>(from, to));
                else if (section == "snippet") Snippets.Add(new KeyValuePair<string, string>(from, to.Replace("\\n", "\n")));
            }
            termRx = swapRx = null;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("[kamus]\n");
            foreach (var t in Terms) sb.Append(t).Append('\n');
            sb.Append("\n[ganti]\n");
            foreach (var s in Swaps) sb.Append(s.Key).Append(" => ").Append(s.Value).Append('\n');
            sb.Append("\n[snippet]\n");
            foreach (var s in Snippets) sb.Append(s.Key).Append(" => ").Append(s.Value.Replace("\r", "").Replace("\n", "\\n")).Append('\n');
            return sb.ToString();
        }

        public void Save() { AtomicFile.Write(FilePath, Encoding.UTF8.GetBytes(Serialize())); }

        // Whisper's prompt biases spelling; ~200 tokens max
        public string Prompt()
        {
            if (Terms.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var t in Terms)
            {
                if (sb.Length + t.Length > 600) break;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(t);
            }
            return sb.Append('.').ToString();
        }

        public enum Style { Plain, Chat, Terminal }

        static readonly string[] ChatApps = { "whatsapp", "whatsapp.root", "telegram", "discord", "slack", "ms-teams", "teams", "line", "signal", "messenger", "zalo" };
        static readonly string[] TerminalApps = { "windowsterminal", "cmd", "powershell", "pwsh", "conhost", "wezterm-gui", "alacritty", "mintty", "wt", "openconsole", "tabby", "hyper", "warp" };

        public static Style StyleFor(string process)
        {
            string p = (process ?? "").ToLowerInvariant();
            if (Array.IndexOf(TerminalApps, p) >= 0) return Style.Terminal;
            if (Array.IndexOf(ChatApps, p) >= 0) return Style.Chat;
            return Style.Plain;
        }

        // Silence makes Whisper invent these
        static readonly string[] Phantoms =
        {
            "terima kasih", "terima kasih sudah menonton", "terima kasih telah menonton", "terima kasih telah menyaksikan", "sampai jumpa",
            "sampai jumpa di video berikutnya", "thank you", "thanks for watching", "thank you for watching", "you", "bye", "subtitle by", ".",
        };

        static readonly Regex Fillers = new Regex(@"(?:,\s*)?(?<![\p{L}\p{N}])(e+h+m*|e+m+|h+m+|u+h+m*|u+m+|a+n+u+|apa namanya)(?![\p{L}\p{N}]),?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Drops a leading comma, keeps periods
        static readonly Regex NewPara = new Regex(@",?\s*\b(paragraf baru|new paragraph)\b[,.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex NewLine = new Regex(@",?\s*\b(baris baru|new line)\b[,.]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
        static readonly Regex SpaceBeforeMark = new Regex(@"\s+([,.!?;:])", RegexOptions.Compiled);

        static string Key(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim(); }

        public static bool Phantom(string text)
        {
            string k = Key(text);
            if (k.Length == 0) return true;
            foreach (var p in Phantoms) if (Key(p) == k) return true;
            return false;
        }

        public string Apply(string text, Style style, bool dropFillers, bool commands)
        {
            text = (text ?? "").Trim();
            if (Phantom(text)) return "";
            // Whole dictation equals a trigger: the snippet
            string spoken = Key(text);
            foreach (var s in Snippets) if (Key(s.Key) == spoken) return s.Value;
            bool upper = char.IsUpper(text[0]);
            if (dropFillers) text = Fillers.Replace(text, " ");
            if (commands)
            {
                text = NewPara.Replace(text, "\n\n");
                text = NewLine.Replace(text, "\n");
            }
            foreach (var r in SwapRules()) text = r.Key.Replace(text, r.Value);
            foreach (var r in TermRules()) text = r.Key.Replace(text, r.Value);
            text = SpaceBeforeMark.Replace(Spaces.Replace(text, " "), "$1").Trim();
            // Dropped filler took the capital with it
            if (upper && text.Length > 0 && char.IsLower(text[0])) text = char.ToUpper(text[0]) + text.Substring(1);
            // One-sentence chat or prompt: no full stop
            if (style != Style.Plain && text.EndsWith(".") && !text.EndsWith("..") && text.IndexOf('.') == text.Length - 1) text = text.Substring(0, text.Length - 1);
            // Enter in a terminal runs the line
            if (style == Style.Terminal) text = text.Replace("\r", "").Replace("\n", " ");
            return text;
        }

        List<KeyValuePair<Regex, string>> SwapRules()
        {
            if (swapRx != null) return swapRx;
            swapRx = new List<KeyValuePair<Regex, string>>();
            foreach (var s in Swaps)
                swapRx.Add(new KeyValuePair<Regex, string>(new Regex(@"(?<![\p{L}\p{N}])" + Loose(s.Key) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase), s.Value.Replace("$", "$$")));
            return swapRx;
        }

        // "useEffect" also matches "use effect"
        List<KeyValuePair<Regex, string>> TermRules()
        {
            if (termRx != null) return termRx;
            termRx = new List<KeyValuePair<Regex, string>>();
            foreach (var t in Terms)
                termRx.Add(new KeyValuePair<Regex, string>(new Regex(@"(?<![\p{L}\p{N}])" + Loose(t) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase), t.Replace("$", "$$")));
            return termRx;
        }

        // Spaces, dashes, dots between word parts optional
        static string Loose(string phrase)
        {
            var sb = new StringBuilder();
            string s = phrase.Trim();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) || c == '-' || c == '.' || c == '_') { if (sb.Length > 0 && !sb.ToString().EndsWith(@"[\s\-._]*")) sb.Append(@"[\s\-._]*"); continue; }
                bool boundary = i > 0 && ((char.IsUpper(c) && char.IsLower(s[i - 1])) || (char.IsDigit(c) != char.IsDigit(s[i - 1]) && char.IsLetterOrDigit(s[i - 1])));
                if (boundary) sb.Append(@"[\s\-._]*");
                sb.Append(Regex.Escape(c.ToString()));
            }
            return sb.ToString();
        }

        public static int CountWords(string text)
        {
            int n = 0;
            bool inWord = false;
            foreach (char c in text ?? "")
            {
                bool w = char.IsLetterOrDigit(c);
                if (w && !inWord) n++;
                inWord = w;
            }
            return n;
        }
    }
}
