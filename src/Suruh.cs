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
        SuruhBox suruhBox;
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
                PlayRemind("");
            }
            LogVoice("pengingat", due.Text, Lang.T("Diingatkan ") + Fmt.Hm(due.At));
        }

        // Command box under the island
        void OpenSuruh()
        {
            if (suruhBox != null && !suruhBox.IsDisposed) { suruhBox.Activate(); return; }
            expanded = false;
            Kick();
            suruhBox = new SuruhBox(text => RunSuruh(text, false), !string.IsNullOrEmpty(Speech.GroqKey()));
            var area = Screen.FromHandle(Handle).WorkingArea;
            suruhBox.StartPosition = FormStartPosition.Manual;
            suruhBox.Show();
            suruhBox.Location = new Point(area.X + (area.Width - suruhBox.Width) / 2, area.Y + (int)(70 * scale));
            Native.SetForegroundWindow(suruhBox.Handle);
        }

        void RunSuruh(string text, bool spoken)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            string done = TryOffline(text);
            if (done != null) { SuruhDone(text, done); return; }
            string key = Speech.GroqKey();
            if (string.IsNullOrEmpty(key))
            {
                DictSay(Lang.T("Belum paham perintahnya"), Lang.T("Coba: timer 25 menit, ingetin minum 30 menit lagi"), true);
                LogVoice("suruh", text, Lang.T("Belum paham"));
                return;
            }
            if (suruhBusy) { DictSay(Lang.T("Perintah sebelumnya masih jalan"), "", true); return; }
            suruhBusy = true;
            dictState = DictState.Working;
            Kick();
            string context = SuruhContext();
            bool tasks = sources != null && (sources.Find("todoist") != null || sources.Find("notion") != null);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                var calls = AskSuruh(key, text, context, tasks, out error);
                Post(delegate
                {
                    suruhBusy = false;
                    dictState = DictState.Off;
                    Kick();
                    if (calls == null) { DictSay(error ?? Lang.T("Groq tidak menjawab"), "", true); LogVoice("suruh", text, error ?? ""); return; }
                    var said = new List<string>();
                    foreach (var c in calls)
                    {
                        string r = DoTool(c.Key, c.Value);
                        if (r != null) said.Add(r);
                    }
                    SuruhDone(text, said.Count > 0 ? string.Join(" · ", said.ToArray()) : Lang.T("Tidak ada yang dikerjakan"));
                });
            });
        }

        void SuruhDone(string asked, string result)
        {
            LogVoice("suruh", asked, result);
            var t = ShowToast(View.Info, 4, 3, false, true);
            if (t == null) return;
            t.Glyph = '';
            t.Title = result;
            t.Detail = "";
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
        string DoTool(string name, Dictionary<string, object> args)
        {
            Func<string, string> s = k => { object v; return args != null && args.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; };
            switch (name)
            {
                case "set_reminder":
                    DateTime at;
                    if (!DateTime.TryParseExact(s("at"), new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out at) || at <= DateTime.Now) return Lang.T("Waktu pengingat tidak jelas");
                    string what = s("text").Length > 0 ? s("text") : Lang.T("Pengingat");
                    AddOneShot(at, what);
                    return ReminderSaid(at, what);
                case "start_timer":
                    int min;
                    if (!int.TryParse(s("minutes"), out min) || min <= 0 || min > 600) return Lang.T("Lama timer tidak jelas");
                    if (s("focus") == "True" || s("focus") == "true") StartFocus(min); else StartTimer(TimerKind.Countdown, min);
                    return Lang.F("Timer {0} menit", min);
                case "add_note":
                    if (s("text").Length == 0) return null;
                    SaveNote(s("text"), "rak");
                    return Lang.T("Dicatat");
                case "add_task":
                    if (s("text").Length == 0 || sources == null) return null;
                    string to = sources.Find("todoist") != null ? "todoist" : "notion";
                    SaveNote(s("text"), to);
                    return Lang.F("Tugas baru: {0}", s("text"));
                case "reply":
                    return s("text").Length > 0 ? s("text") : null;
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

        // Worker: one round of tool calls
        static List<KeyValuePair<string, Dictionary<string, object>>> AskSuruh(string key, string text, string context, bool tasks, out string error)
        {
            var tools = new List<object>
            {
                Fn("set_reminder", "Remind the user once at a local date and time.", new Dictionary<string, object> { { "at", P("string", "Local time, yyyy-MM-ddTHH:mm") }, { "text", P("string", "What to remind, in the user's words") } }, new[] { "at", "text" }),
                Fn("start_timer", "Start a countdown timer.", new Dictionary<string, object> { { "minutes", P("integer", "Length in minutes") }, { "focus", P("boolean", "True for a focus session with a break after") } }, new[] { "minutes" }),
                Fn("add_note", "Save a quick note.", new Dictionary<string, object> { { "text", P("string", "Note text") } }, new[] { "text" }),
                Fn("reply", "Answer a question or explain what you cannot do, one short sentence.", new Dictionary<string, object> { { "text", P("string", "Answer in the user's language") } }, new[] { "text" }),
            };
            if (tasks) tools.Add(Fn("add_task", "Add a to-do item.", new Dictionary<string, object> { { "text", P("string", "Task text") } }, new[] { "text" }));
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm dddd", CultureInfo.InvariantCulture);
            var payload = new Dictionary<string, object>
            {
                { "model", Speech.ChatModel(key) },
                { "temperature", 0.1 },
                { "tool_choice", "auto" },
                { "tools", tools.ToArray() },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", "You are Tepi, a small assistant living in the user's Windows dynamic island. Local time now: " + now + ". " + context + "Use the tools to act. Always call at least one tool; use reply for questions or anything the tools cannot do. Reply in the user's language, briefly." } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", text } },
                    }
                },
            };
            var msg = Speech.GroqMessage(key, payload, out error);
            if (msg == null) return null;
            var calls = new List<KeyValuePair<string, Dictionary<string, object>>>();
            object tc;
            if (msg.TryGetValue("tool_calls", out tc) && tc is object[])
                foreach (var o in (object[])tc)
                {
                    var call = o as Dictionary<string, object>;
                    var fn = call != null && call.ContainsKey("function") ? call["function"] as Dictionary<string, object> : null;
                    if (fn == null) continue;
                    Dictionary<string, object> args = null;
                    try { args = Json.Parse(fn.ContainsKey("arguments") ? fn["arguments"] as string : "{}") as Dictionary<string, object>; } catch (Exception) { }
                    calls.Add(new KeyValuePair<string, Dictionary<string, object>>(fn["name"] as string ?? "", args));
                }
            object content;
            if (calls.Count == 0 && msg.TryGetValue("content", out content) && content is string && ((string)content).Trim().Length > 0)
                calls.Add(new KeyValuePair<string, Dictionary<string, object>>("reply", new Dictionary<string, object> { { "text", ((string)content).Trim() } }));
            return calls;
        }

        string ReminderSaid(DateTime at, string what)
        {
            string day = at.Date == DateTime.Today ? "" : at.Date == DateTime.Today.AddDays(1) ? Lang.T("besok ") : at.ToString("d MMM ", Lang.English ? CultureInfo.InvariantCulture : new CultureInfo("id-ID"));
            return Lang.F("Diingatkan {0}{1}: {2}", day, Fmt.Hm(at), what);
        }

        // Offline: timers, reminders, notes, sums
        string TryOffline(string raw)
        {
            string text = raw.Trim().TrimEnd('.', '!', '?');
            string low = text.ToLowerInvariant();
            Match m;

            if (Regex.IsMatch(low, @"^(stop|hentikan|matikan|batalkan)\s+(timer|stopwatch|fokus)$")) { if (!TimerOn) return Lang.T("Tidak ada timer yang jalan"); StopTimer(); return Lang.T("Timer dihentikan"); }
            if (Regex.IsMatch(low, @"^(mulai\s+)?stopwatch$")) { StartTimer(TimerKind.Stopwatch, 0); return Lang.T("Stopwatch jalan"); }

            m = Regex.Match(low, @"^(mulai\s+)?(fokus|focus|pomodoro)(\s+(\d{1,3})\s*(menit|mnt|min|minutes?|m)?)?$");
            if (m.Success)
            {
                int min = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 25;
                if (min < 1 || min > 300) return null;
                StartFocus(min);
                return Lang.F("Fokus {0} menit", min);
            }

            m = Regex.Match(low, @"^(set(el)?\s+|pasang\s+|mulai\s+)?(timer|hitung mundur|countdown)\s+(\d{1,3})\s*(menit|mnt|min|minutes?|m|jam|hours?|h|j|detik|dtk|seconds?|s)$");
            if (!m.Success) m = Regex.Match(low, @"^(\d{1,3})\s*(menit|mnt|min|minutes?|jam|hours?|detik|dtk|seconds?)\s+(timer|hitung mundur)$");
            if (m.Success)
            {
                string n = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? m.Groups[4].Value : m.Groups[1].Value;
                string unit = m.Groups[5].Success && m.Groups[5].Value.Length > 0 ? m.Groups[5].Value : m.Groups[2].Value;
                int v = int.Parse(n), min = Unit(unit) == 'h' ? v * 60 : Unit(unit) == 's' ? Math.Max(1, (v + 59) / 60) : v;
                if (min < 1 || min > 600) return null;
                StartTimer(TimerKind.Countdown, min);
                return Lang.F("Timer {0} menit", min);
            }

            m = Regex.Match(text, @"^(catat|catatan|note|tulis)\s*:?\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) { SaveNote(m.Groups[2].Value.Trim(), "rak"); return Lang.T("Dicatat"); }

            m = Regex.Match(text, @"^(ingetin|ingatin|ingatkan|ingetkan|pengingat|reminder|remind me( to)?)\b\s*(aku|saya|gue|gw|me)?\s*(untuk|buat|to)?\s*(.*)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string rest = m.Groups[5].Value;
                string what;
                DateTime at;
                if (!ParseWhen(rest, out at, out what)) return null;
                if (what.Length == 0) what = Lang.T("Pengingat");
                what = char.ToUpper(what[0]) + what.Substring(1);
                AddOneShot(at, what);
                return ReminderSaid(at, what);
            }

            double result;
            if (TryCalc(low, out result)) return "= " + FormatNumber(result);
            return null;
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

        // Frameless box, like the quick note
        class SuruhBox : KitWindow
        {
            readonly KitText box = new KitText();
            readonly Label hint = new Label();
            readonly Action<string> run;
            readonly bool online;

            public SuruhBox(Action<string> run, bool online)
            {
                this.run = run;
                this.online = online;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                Text = Lang.T("Suruh Tepi");
                box.BorderStyle = BorderStyle.None;
                box.KeyDown += (s, e) =>
                {
                    if (e.KeyCode != Keys.Enter) return;
                    e.SuppressKeyPress = true;
                    string t = box.Text;
                    Close();
                    run(t);
                };
                hint.AutoSize = false;
                hint.UseMnemonic = false;
                Controls.Add(box);
                Controls.Add(hint);
                Deactivate += delegate { if (!IsDisposed) Close(); };
            }

            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Relayout();
            }

            protected override void OnShown(EventArgs e) { base.OnShown(e); box.Focus(); }

            public override void Relayout()
            {
                float s = Ui;
                BackColor = KitInk.Window;
                ClientSize = new Size((int)(460 * s), (int)(100 * s));
                box.Font = UiFont(15f);
                box.BackColor = KitInk.Group;
                box.ForeColor = KitInk.Text;
                box.SetBounds((int)(30 * s), (int)(22 * s), ClientSize.Width - (int)(60 * s), (int)(24 * s));
                Native.SetCue(box.Handle, Lang.T("Suruh Tepi, misalnya: ingetin minum 30 menit lagi"));
                hint.Font = UiFont(12f);
                hint.ForeColor = KitInk.Sub;
                hint.BackColor = KitInk.Window;
                hint.Text = online ? Lang.T("Enter menjalankan. Timer, pengingat, catatan, dan hitungan jalan tanpa internet.") : Lang.T("Enter menjalankan. Tanpa key Groq: timer, pengingat, catatan, hitungan.");
                hint.SetBounds((int)(20 * s), ClientSize.Height - (int)(36 * s), ClientSize.Width - (int)(40 * s), (int)(22 * s));
                Invalidate();
            }

            // Text well with a quiet focus ring
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(KitInk.Window);
                KitInk.Smooth(g);
                float s = Ui;
                var well = new RectangleF(16 * s, 12 * s, ClientSize.Width - 32 * s, 44 * s);
                using (var path = Ink.Round(well, 12 * s))
                {
                    using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                    using (var pen = new Pen(KitInk.Faint, 1.2f)) g.DrawPath(pen, path);
                }
            }
        }
    }
}
