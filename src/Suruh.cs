using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Recent voice and command activity
    class VoiceEntry
    {
        public string Kind = "", Text = "", Detail = "", Path;
        public DateTime Utc;
        public OneShot Reminder;
    }

    class OneShot
    {
        public DateTime At;
        public string Text = "";
    }

    // Tell Tepi what to do, in plain words
    partial class Island
    {
        const int VoiceKeep = 30, SuruhHotkey = 18;
        readonly List<VoiceEntry> voiceLog = new List<VoiceEntry>();
        readonly List<OneShot> oneShots = new List<OneShot>();
        bool suruhBusy;

        static string OneShotPath { get { return Path.Combine(Settings.Dir, "pengingat-sekali.txt"); } }

        void LogVoice(string kind, string text, string detail)
        {
            voiceLog.Insert(0, new VoiceEntry { Kind = kind, Text = text, Detail = detail, Utc = DateTime.UtcNow });
            while (voiceLog.Count > VoiceKeep) voiceLog.RemoveAt(voiceLog.Count - 1);
            Kick();
        }

        // One-off reminders survive a restart
        void LoadOneShots()
        {
            oneShots.Clear();
            try
            {
                if (!File.Exists(OneShotPath)) return;
                foreach (var line in File.ReadAllLines(OneShotPath, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    DateTime at;
                    if (tab > 0 && DateTime.TryParseExact(line.Substring(0, tab), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                        oneShots.Add(new OneShot { At = at, Text = line.Substring(tab + 1) });
                }
            }
            catch (Exception ex) { Log.Write("pengingat sekali: " + ex.Message); }
        }

        void SaveOneShots()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var o in oneShots) sb.Append(o.At.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)).Append('\t').Append(o.Text.Replace('\n', ' ')).Append('\n');
                Directory.CreateDirectory(Settings.Dir);
                AtomicFile.Write(OneShotPath, new UTF8Encoding(false).GetBytes(sb.ToString()));
            }
            catch (Exception ex) { Log.Write("simpan pengingat sekali: " + ex.Message); }
        }

        OneShot AddOneShot(DateTime at, string text)
        {
            var o = new OneShot { At = at, Text = text };
            oneShots.Add(o);
            oneShots.Sort((a, b) => a.At.CompareTo(b.At));
            SaveOneShots();
            Kick();
            return o;
        }

        void CancelOneShot(OneShot o)
        {
            oneShots.Remove(o);
            SaveOneShots();
            Kick();
        }

        // Clock tick: fire what's due
        void TickOneShots()
        {
            if (oneShots.Count == 0 || oneShots[0].At > DateTime.Now) return;
            var due = oneShots[0];
            oneShots.RemoveAt(0);
            SaveOneShots();
            var t = ShowToast(View.Info, 30, 3, true, true);
            if (t != null)
            {
                t.Glyph = Ink.IcoBell;
                t.Title = due.Text;
                t.Detail = Fmt.Hm(due.At);
                t.Hold = true;
                t.Data = new Action(delegate { });
                t.Page = PageVoice;
                PlayRemind("");
            }
            LogVoice("pengingat", due.Text, Lang.T("Diingatkan ") + Fmt.Hm(due.At));
        }

        // Typed or spoken: into the chat, then answered
        void RunSuruh(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (suruhBusy) { DictSay(Lang.T("Perintah sebelumnya masih jalan"), "", true); return; }
            LoadChat();
            var me = new ChatMsg { Who = "me", Text = text };
            AddChat(me, null);
            Answer(me);
        }

        // Offline first; else the AI, with the chat so far
        void Answer(ChatMsg me)
        {
            var done = TryOffline(me.Text);
            if (done != null) { SuruhDone(me, done); return; }
            if (!Ai.Ready(Ai.Text))
            {
                SuruhDone(me, new ChatMsg { Text = Lang.T("Belum paham perintahnya") + ". " + Lang.T("Coba: timer 25 menit, ingetin minum 30 menit lagi"), Fix = "ai" });
                return;
            }
            suruhBusy = true;
            dictState = DictState.Working;
            Kick();
            ChatChanged(null);
            var turns = ChatTurns(me);
            string context = SuruhContext();
            bool tasks = sources != null && (sources.Find("todoist") != null || sources.Find("notion") != null);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                var reply = AskSuruh(turns, context, tasks, out error);
                Post(delegate
                {
                    suruhBusy = false;
                    dictState = DictState.Off;
                    Kick();
                    if (reply == null) SuruhDone(me, new ChatMsg { Text = error ?? Lang.F("{0} tidak menjawab", Ai.Label(Ai.Text)), Fix = "retry" });
                    else SuruhDone(me, FromReply(reply));
                });
            });
        }

        // The failed reply goes; its question runs again
        void RetrySuruh(ChatMsg failed)
        {
            if (suruhBusy || chat == null) return;
            int at = chat.IndexOf(failed);
            ChatMsg me = null;
            for (int i = at - 1; i >= 0 && me == null; i--) if (chat[i].Mine) me = chat[i];
            if (me == null) return;
            chat.RemoveAt(at);
            SaveChat();
            ChatChanged(null);
            Answer(me);
        }

        void SuruhDone(ChatMsg me, ChatMsg reply)
        {
            string said = ChatSummary(reply);
            LogVoice("suruh", me.Text, Fmt.OneLine(said, 300));
            AddChat(reply, me);
            // The open chat shows and announces it
            if (ChatInFront) return;
            // Short: the full answer lives in the chat
            var t = ShowToast(View.Info, Math.Min(6, 3.5 + said.Length / 40.0), 3, false, true);
            if (t == null) return;
            t.Glyph = reply.Fix != null ? IcoFail : IcoDone;
            t.Title = Fmt.OneLine(said, 200);
            // The ask again, unless it says the same
            string asked = Fmt.OneLine(me.Text, 120);
            t.Detail = string.Equals(asked, t.Title, StringComparison.OrdinalIgnoreCase) ? "" : asked;
            t.Page = PageVoice;
            // A click carries on in the chat
            t.Data = new Action(OpenSuruh);
        }

        // Words from reply, chips from the rest
        ChatMsg FromReply(Ai.Reply reply)
        {
            var m = new ChatMsg();
            var said = new List<string>();
            foreach (var c in reply.Calls)
            {
                if (c.Key == "reply") { string t = Arg(c.Value, "text"); if (t.Length > 0) said.Add(t); continue; }
                var act = DoTool(c.Key, c.Value);
                if (act != null) m.Acts.Add(act);
            }
            // Text beside the tool calls is the answer too
            if (said.Count == 0 && reply.Text.Length > 0) said.Add(reply.Text);
            m.Text = Plain(string.Join("\n\n", said.ToArray()));
            if (m.Text.Length == 0 && m.Acts.Count == 0) m.Text = Lang.T("Tidak ada yang dikerjakan");
            return m;
        }

        static string Arg(Dictionary<string, object> args, string key)
        {
            object v;
            return args != null && args.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        // Models slip into Markdown; the chat is plain text
        static string Plain(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n");
            s = Regex.Replace(s, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
            s = Regex.Replace(s, @"(?m)^```[^\n]*\n?", "");
            s = Regex.Replace(s, @"`([^`\n]+)`", "$1");
            s = Regex.Replace(s, @"(?m)^#{1,6}\s+", "");
            s = Regex.Replace(s, @"(?m)^(\s*)[*-]\s+", "$1• ");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        static ChatMsg Said(string text) { return new ChatMsg { Text = text }; }

        static ChatMsg Did(char glyph, string text, OneShot reminder = null)
        {
            var m = new ChatMsg();
            m.Acts.Add(new ChatAct { Glyph = glyph, Text = text, Reminder = reminder });
            return m;
        }

        string SuruhContext()
        {
            var sb = new StringBuilder();
            if (mediaInfo.Present && mediaInfo.Title.Length > 0) sb.Append("Now playing: ").Append(mediaInfo.Title).Append(" by ").Append(mediaInfo.Artist).Append(". ");
            if (TimerOn) sb.Append("A timer is running. ");
            var task = SoonTask();
            if (task != null) sb.Append("Next deadline: ").Append(task.Title).Append(" at ").Append(task.DeadlineUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(". ");
            foreach (var o in oneShots) sb.Append("Reminder set: ").Append(o.Text).Append(" at ").Append(o.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(". ");
            return sb.ToString();
        }

        // Whitelisted actions only: no shell, no files
        ChatAct DoTool(string name, Dictionary<string, object> args)
        {
            Func<string, string> s = k => Arg(args, k);
            switch (name)
            {
                case "set_reminder":
                    DateTime at;
                    if (!DateTime.TryParseExact(s("at"), new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out at) || at <= DateTime.Now)
                        return new ChatAct { Glyph = IcoFail, Text = Lang.T("Waktu pengingat tidak jelas") };
                    string what = s("text").Length > 0 ? s("text") : Lang.T("Pengingat");
                    var o = AddOneShot(at, what);
                    return new ChatAct { Glyph = Ink.IcoBell, Text = ReminderSaid(at, what), Reminder = o };
                case "start_timer":
                    int min;
                    if (!int.TryParse(s("minutes"), out min) || min <= 0 || min > 600) return new ChatAct { Glyph = IcoFail, Text = Lang.T("Lama timer tidak jelas") };
                    if (s("focus") == "True" || s("focus") == "true") StartFocus(min); else StartTimer(TimerKind.Countdown, min);
                    return new ChatAct { Glyph = Ink.IcoTimer, Text = Lang.F("Timer {0} menit", min) };
                case "add_note":
                    if (s("text").Length == 0) return null;
                    SaveNote(s("text"), "rak");
                    return new ChatAct { Glyph = IcoNote, Text = Lang.F("Dicatat: {0}", s("text")) };
                case "add_task":
                    if (s("text").Length == 0 || sources == null) return null;
                    string to = sources.Find("todoist") != null ? "todoist" : "notion";
                    SaveNote(s("text"), to);
                    return new ChatAct { Glyph = Ink.IcoCalendar, Text = Lang.F("Tugas baru: {0}", s("text")) };
            }
            return null;
        }

        static object Fn(string name, string about, Dictionary<string, object> props, string[] required)
        {
            return new Dictionary<string, object>
            {
                { "type", "function" },
                { "function", new Dictionary<string, object> { { "name", name }, { "description", about }, { "parameters", new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "required", required } } } } },
            };
        }

        static Dictionary<string, object> P(string type, string about) { return new Dictionary<string, object> { { "type", type }, { "description", about } }; }

        // Worker: one round, the chat so far as context
        static Ai.Reply AskSuruh(List<KeyValuePair<string, string>> turns, string context, bool tasks, out string error)
        {
            var tools = new List<object>
            {
                Fn("set_reminder", "Remind the user once at a local date and time.", new Dictionary<string, object> { { "at", P("string", "Local time, yyyy-MM-ddTHH:mm") }, { "text", P("string", "What to remind, in the user's words") } }, new[] { "at", "text" }),
                Fn("start_timer", "Start a countdown timer.", new Dictionary<string, object> { { "minutes", P("integer", "Length in minutes") }, { "focus", P("boolean", "True for a focus session with a break after") } }, new[] { "minutes" }),
                Fn("add_note", "Save a quick note.", new Dictionary<string, object> { { "text", P("string", "Note text") } }, new[] { "text" }),
                Fn("reply", "Talk to the user: answers, follow-ups, or what the other tools cannot do.", new Dictionary<string, object> { { "text", P("string", "Plain text in the user's language") } }, new[] { "text" }),
            };
            if (tasks) tools.Add(Fn("add_task", "Add a to-do item.", new Dictionary<string, object> { { "text", P("string", "Task text") } }, new[] { "text" }));
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm dddd", CultureInfo.InvariantCulture);
            // Who made Tepi; the model vendor did not
            string maker = "Tepi was made by Reval (R. Revaldo Vabiansyah), an Indonesian developer. If asked who made or created you, answer that Reval made you. " +
                           "Never say you were made by OpenAI, Anthropic, Google, Meta, or another AI company. If asked which AI model answers, say it is " + Ai.Label(Ai.Text) + ", chosen in Tepi's settings. ";
            string system = "You are Tepi, a small assistant living in the user's Windows dynamic island, chatting in a small window. " + maker + "Local time now: " + now + ". " + context +
                            "Act through the tools; use reply for questions, chat, and anything the other tools cannot do. Earlier messages are this chat's history: follow up on them. " +
                            "Reply in the user's language. Keep it short: a sentence or two for quick asks, a few short paragraphs at most. Plain text, no Markdown.";
            return Ai.Ask(system, turns, tools.ToArray(), out error);
        }

        string ReminderSaid(DateTime at, string what)
        {
            string day = at.Date == DateTime.Today ? "" : at.Date == DateTime.Today.AddDays(1) ? Lang.T("besok ") : at.ToString("d MMM ", Lang.English ? CultureInfo.InvariantCulture : new CultureInfo("id-ID"));
            return Lang.F("Diingatkan {0}{1}: {2}", day, Fmt.Hm(at), what);
        }

        // Offline: timers, reminders, notes, sums
        ChatMsg TryOffline(string raw)
        {
            string text = raw.Trim().TrimEnd('.', '!', '?');
            string low = text.ToLowerInvariant();
            Match m;

            if (Regex.IsMatch(low, @"^(stop|hentikan|matikan|batalkan)\s+(timer|stopwatch|fokus)$"))
            {
                if (!TimerOn) return Said(Lang.T("Tidak ada timer yang jalan"));
                StopTimer();
                return Did(Ink.IcoTimer, Lang.T("Timer dihentikan"));
            }
            if (Regex.IsMatch(low, @"^(mulai\s+)?stopwatch$")) { StartTimer(TimerKind.Stopwatch, 0); return Did(Ink.IcoTimer, Lang.T("Stopwatch jalan")); }

            m = Regex.Match(low, @"^(mulai\s+)?(fokus|focus|pomodoro)(\s+(\d{1,3})\s*(menit|mnt|min|minutes?|m)?)?$");
            if (m.Success)
            {
                int min = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 25;
                if (min < 1 || min > 300) return null;
                StartFocus(min);
                return Did(Ink.IcoTimer, Lang.F("Fokus {0} menit", min));
            }

            m = Regex.Match(low, @"^(?:(?:set(?:el)?|pasang|mulai)\s+)?(?:timer|hitung mundur|countdown)\s+(.+)$");
            if (!m.Success) m = Regex.Match(low, @"^(.+?)\s+(?:timer|hitung mundur)$");
            TimeSpan span;
            if (m.Success && ParseSpan(m.Groups[1].Value, out span))
            {
                if (span.TotalSeconds < 5 || span.TotalHours > 10) return null;
                StartTimer(TimerKind.Countdown, span);
                return Did(Ink.IcoTimer, Lang.F("Timer {0}", SpanText(span)));
            }

            m = Regex.Match(text, @"^(catat|catatan|note|tulis)\s*:?\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string note = m.Groups[2].Value.Trim();
                SaveNote(note, "rak");
                return Did(IcoNote, Lang.F("Dicatat: {0}", note));
            }

            m = Regex.Match(text, @"^(ingetin|ingatin|ingatkan|ingetkan|pengingat|reminder|remind me( to)?)\b\s*(aku|saya|gue|gw|me)?\s*(untuk|buat|to)?\s*(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string rest = m.Groups[5].Value;
                string what;
                DateTime at;
                if (!ParseWhen(rest, out at, out what)) return null;
                if (what.Length == 0) what = Lang.T("Pengingat");
                what = char.ToUpper(what[0]) + what.Substring(1);
                var o = AddOneShot(at, what);
                return Did(Ink.IcoBell, ReminderSaid(at, what), o);
            }

            string converted;
            if (TryConvert(low, out converted)) return Said(converted);
            double result;
            if (TryCalc(low, out result)) return Said("= " + FormatNumber(result));
            return null;
        }

        static readonly Regex ClockSpan = new Regex(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");
        static readonly Regex PartSpan = new Regex(@"^(?:\s*(\d{1,4})\s*(jam|j|hours?|hrs?|h|menit|mnt|minutes?|mins?|m|detik|dtk|seconds?|secs?|s)(?![a-z])\s*(?:dan|and|,)?)+$");

        // "5:30", "1 jam 30 menit", "1j30m"
        static bool ParseSpan(string s, out TimeSpan span)
        {
            span = TimeSpan.Zero;
            s = s.Trim();
            var c = ClockSpan.Match(s);
            if (c.Success)
            {
                int a = int.Parse(c.Groups[1].Value), b = int.Parse(c.Groups[2].Value);
                if (b > 59) return false;
                if (c.Groups[3].Success)
                {
                    int sec = int.Parse(c.Groups[3].Value);
                    if (sec > 59) return false;
                    span = new TimeSpan(a, b, sec);
                }
                else span = new TimeSpan(0, a, b);
                return true;
            }
            var m = PartSpan.Match(s);
            if (!m.Success) return false;
            for (int i = 0; i < m.Groups[1].Captures.Count; i++)
            {
                int v = int.Parse(m.Groups[1].Captures[i].Value);
                char u = Unit(m.Groups[2].Captures[i].Value);
                span += u == 'h' ? TimeSpan.FromHours(v) : u == 's' ? TimeSpan.FromSeconds(v) : TimeSpan.FromMinutes(v);
            }
            return span > TimeSpan.Zero;
        }

        static char Unit(string u)
        {
            if (u.StartsWith("j") || u.StartsWith("h")) return 'h';
            if (u.StartsWith("d") || u.StartsWith("s")) return 's';
            return 'm';
        }

        static readonly Regex RelativeWhen = new Regex(@"\b(dalam|in)?\s*(\d{1,3})\s*(menit|mnt|min|minutes?|jam|hours?|detik|dtk|seconds?)\s*(lagi|later|dari sekarang|from now)?\b", RegexOptions.IgnoreCase);
        static readonly Regex ClockWhen = new Regex(@"\b(besok|tomorrow|lusa)?\s*(nanti\s+)?(jam|pukul|at)\s*(\d{1,2})(?:[.:](\d{2}))?\s*(pagi|siang|sore|malam|am|pm)?\b", RegexOptions.IgnoreCase);
        static readonly Regex DayOnly = new Regex(@"\b(besok|tomorrow)\b", RegexOptions.IgnoreCase);

        // "15 menit lagi", "besok jam 7 pagi"
        static bool ParseWhen(string rest, out DateTime at, out string what)
        {
            at = DateTime.MinValue;
            what = rest;
            var now = DateTime.Now;
            var m = RelativeWhen.Match(rest);
            if (m.Success)
            {
                int v = int.Parse(m.Groups[2].Value);
                char u = Unit(m.Groups[3].Value.ToLowerInvariant());
                at = u == 'h' ? now.AddHours(v) : u == 's' ? now.AddSeconds(Math.Max(30, v)) : now.AddMinutes(v);
                what = Tidy(rest.Remove(m.Index, m.Length));
                return v > 0;
            }
            m = ClockWhen.Match(rest);
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[4].Value), min = m.Groups[5].Success && m.Groups[5].Value.Length > 0 ? int.Parse(m.Groups[5].Value) : 0;
            string part = m.Groups[6].Value.ToLowerInvariant();
            if (h > 23 || min > 59) return false;
            if ((part == "sore" || part == "malam" || part == "pm") && h < 12) h += 12;
            else if (part == "siang" && h < 11) h += 12;
            else if ((part == "pagi" || part == "am") && h == 12) h = 0;
            string day = m.Groups[1].Value.ToLowerInvariant();
            var date = now.Date.AddDays(day == "lusa" ? 2 : day.Length > 0 ? 1 : 0);
            at = date.AddHours(h).AddMinutes(min);
            // "jam 3" after 3 a.m. means the afternoon
            if (day.Length == 0 && at <= now)
            {
                if (part.Length == 0 && h < 12 && date.AddHours(h + 12).AddMinutes(min) > now) at = date.AddHours(h + 12).AddMinutes(min);
                else at = at.AddDays(1);
            }
            what = Tidy(rest.Remove(m.Index, m.Length));
            return true;
        }

        static string Tidy(string s)
        {
            s = Regex.Replace(s, @"\s{2,}", " ").Trim().Trim(',', '.', ':', ';');
            s = Regex.Replace(s, @"^(untuk|buat|to|bahwa)\s+", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        // Unit kind and size in base units
        struct UnitDef { public char Kind; public double Size; public UnitDef(char k, double s) { Kind = k; Size = s; } }

        static readonly Dictionary<string, UnitDef> Units = BuildUnits();

        static Dictionary<string, UnitDef> BuildUnits()
        {
            var d = new Dictionary<string, UnitDef>();
            Action<char, double, string> add = (k, size, names) => { foreach (var n in names.Split(' ')) d[n] = new UnitDef(k, size); };
            // Length in metres
            add('L', 0.001, "mm milimeter millimeter millimeters");
            add('L', 0.01, "cm sentimeter centimeter centimeters");
            add('L', 1, "m meter meters metre metres");
            add('L', 1000, "km kilometer kilometers");
            add('L', 0.0254, "in inch inches inci");
            add('L', 0.3048, "ft foot feet kaki");
            add('L', 0.9144, "yd yard yards");
            add('L', 1609.344, "mi mil mile miles");
            // Mass in grams
            add('M', 0.001, "mg miligram milligram");
            add('M', 1, "g gr gram grams");
            add('M', 100, "ons");
            add('M', 1000, "kg kilo kilogram kilograms");
            add('M', 28.349523125, "oz ounce ounces");
            add('M', 453.59237, "lb lbs pound pounds");
            // Volume in litres
            add('V', 0.001, "ml mililiter milliliter");
            add('V', 1, "l liter litre liters litres");
            add('V', 3.785411784, "gal galon gallon gallons");
            // Data in bytes, Windows' 1024 steps
            add('D', 1, "byte bytes");
            add('D', 1024, "kb");
            add('D', 1048576, "mb");
            add('D', 1073741824, "gb");
            add('D', 1099511627776, "tb");
            // Time in seconds
            add('T', 1, "detik dtk sec secs second seconds");
            add('T', 60, "menit mnt min mins minute minutes");
            add('T', 3600, "jam hour hours");
            add('T', 86400, "hari day days");
            add('T', 604800, "minggu week weeks");
            // Speed in metres per second
            add('S', 1, "m/s mps");
            add('S', 1000.0 / 3600, "km/j km/jam km/h kmh kph");
            add('S', 1609.344 / 3600, "mph");
            // Temperature: scaled separately
            add('C', 0, "c °c celsius");
            add('C', 1, "f °f fahrenheit");
            add('C', 2, "k kelvin");
            return d;
        }

        static readonly Regex ConvertAsk = new Regex(@"^(?:berapa|konversi|convert|ubah)?\s*(-?[\d.,]+)\s*([a-z°/]+)\s+(?:ke|to|in|into|jadi|dalam)\s+([a-z°/]+)$");

        // "5 km ke mil", "72 f ke c"
        static bool TryConvert(string low, out string answer)
        {
            answer = null;
            var m = ConvertAsk.Match(low.Trim());
            if (!m.Success) return false;
            UnitDef from, to;
            double v;
            if (!Units.TryGetValue(m.Groups[2].Value, out from) || !Units.TryGetValue(m.Groups[3].Value, out to) || from.Kind != to.Kind) return false;
            if (!Num(m.Groups[1].Value.TrimStart('-'), out v)) return false;
            if (m.Groups[1].Value.StartsWith("-")) v = -v;
            double r;
            if (from.Kind == 'C')
            {
                double c = from.Size == 0 ? v : from.Size == 1 ? (v - 32) * 5 / 9 : v - 273.15;
                r = to.Size == 0 ? c : to.Size == 1 ? c * 9 / 5 + 32 : c + 273.15;
            }
            else r = v * from.Size / to.Size;
            answer = "= " + FormatNumber(Math.Round(r, Math.Abs(r) >= 100 ? 2 : 4)) + " " + m.Groups[3].Value;
            return true;
        }

        // Sums: "15% dari 240.000", "12 x 3,5"
        static bool TryCalc(string low, out double result)
        {
            result = 0;
            string s = Regex.Replace(low, @"^(berapa|hitung|hitungin|calc|what is|what's)\s+", "");
            s = s.Replace("berapa", "").Replace("=", "").Trim();
            var pct = Regex.Match(s, @"^([\d.,]+)\s*(%|persen|percent)\s*(dari|of)\s*([\d.,]+)$");
            if (pct.Success)
            {
                double a, b;
                if (!Num(pct.Groups[1].Value, out a) || !Num(pct.Groups[4].Value, out b)) return false;
                result = a / 100 * b;
                return true;
            }
            if (!Regex.IsMatch(s, @"^[\d.,\s+\-*/x×÷:()^%]+$") || !Regex.IsMatch(s, @"\d\s*[+\-*/x×÷:^]\s*[\d(]")) return false;
            var tokens = new List<string>();
            foreach (Match t in Regex.Matches(s, @"[\d.,]+|[+\-*/x×÷:()^%]")) tokens.Add(t.Value);
            int at = 0;
            try
            {
                result = Sum(tokens, ref at);
                return at == tokens.Count && !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch (Exception) { return false; }
        }

        static double Sum(List<string> t, ref int i)
        {
            double v = Product(t, ref i);
            while (i < t.Count && (t[i] == "+" || t[i] == "-")) { bool add = t[i++] == "+"; double r = Product(t, ref i); v = add ? v + r : v - r; }
            return v;
        }

        static double Product(List<string> t, ref int i)
        {
            double v = Power(t, ref i);
            while (i < t.Count && (t[i] == "*" || t[i] == "x" || t[i] == "×" || t[i] == "/" || t[i] == "÷" || t[i] == ":"))
            {
                string op = t[i++];
                double r = Power(t, ref i);
                v = op == "*" || op == "x" || op == "×" ? v * r : v / r;
            }
            return v;
        }

        static double Power(List<string> t, ref int i)
        {
            double v = Atom(t, ref i);
            if (i < t.Count && t[i] == "^") { i++; v = Math.Pow(v, Power(t, ref i)); }
            return v;
        }

        static double Atom(List<string> t, ref int i)
        {
            if (i >= t.Count) throw new FormatException();
            if (t[i] == "-") { i++; return -Atom(t, ref i); }
            if (t[i] == "(") { i++; double v = Sum(t, ref i); if (i >= t.Count || t[i] != ")") throw new FormatException(); i++; return Percent(t, ref i, v); }
            double n;
            if (!Num(t[i], out n)) throw new FormatException();
            i++;
            return Percent(t, ref i, n);
        }

        static double Percent(List<string> t, ref int i, double v)
        {
            if (i < t.Count && t[i] == "%") { i++; return v / 100; }
            return v;
        }

        // 240.000 and 2,5 the Indonesian way; 2.5 too
        static bool Num(string s, out double v)
        {
            s = s.Trim();
            if (Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+(,\d+)?$")) s = s.Replace(".", "").Replace(',', '.');
            else if (Regex.IsMatch(s, @"^\d{1,3}(,\d{3})+(\.\d+)?$")) s = s.Replace(",", "");
            else s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static string FormatNumber(double v)
        {
            var c = Lang.English ? CultureInfo.InvariantCulture : new CultureInfo("id-ID");
            return Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v).ToString("#,0", c) : v.ToString("#,0.####", c);
        }
    }
}
