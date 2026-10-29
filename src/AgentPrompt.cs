using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Type a prompt into an agent's terminal
    partial class Island
    {
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        const int PromptMax = 4000;

        bool SharesWindow(AgentSession s)
        {
            foreach (var other in sessions.Values)
                if (other != s && other.Window == s.Window && other.State != AgentState.Ended) return true;
            return false;
        }

        // One line, no hidden or control chars
        static string CleanPrompt(string text)
        {
            var clean = new StringBuilder((text ?? "").Length);
            foreach (char ch in Fmt.StripHidden(text)) clean.Append(char.IsControl(ch) ? ' ' : ch);
            string s = clean.ToString().Trim();
            return s.Length > PromptMax ? s.Substring(0, PromptMax) : s;
        }

        static string AgentKind(AgentSession s)
        {
            return s.Profile == "Codex" || s.Profile == "Gemini" || s.Profile == "Antigravity" ? s.Profile : "Claude";
        }

        // Claude session: its profile's quota
        QuotaInfo QuotaFor(AgentSession s)
        {
            if (AgentKind(s) != "Claude") return null;
            foreach (var q in quotas) if (q.Profile == s.Profile && QuotaShown(q)) return q;
            return null;
        }

        void AskAgent(AgentSession s)
        {
            expanded = false;
            Kick();
            string text;
            using (var f = new AgentPromptBox(this, s))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                text = CleanPrompt(f.Prompt);
            }
            if (text.Length == 0) return;
            if (!TerminalFinder.Focus(s.Window)) { ShowInfo(Ink.IcoTerminal, Lang.T("Terminal tidak bisa dibuka"), s.Project, true); return; }
            IntPtr want = GetAncestor(s.Window, 3);
            string project = s.Project;
            // Waiting happens off the UI thread
            ThreadPool.QueueUserWorkItem(delegate
            {
                string fail = TypeInto(want, text);
                if (fail != null) Post(() => ShowInfo(Ink.IcoTerminal, fail, project, true));
            });
        }

        // Worker: null when typed, else why not
        static string TypeInto(IntPtr want, string text)
        {
            bool front = false;
            for (int i = 0; i < 20 && !front; i++)
            {
                Thread.Sleep(40);
                front = GetAncestor(GetForegroundWindow(), 3) == want;
            }
            if (!front) return Lang.T("Terminal tidak bisa dibuka");
            // Held Shift/Ctrl/Alt/Enter would change the keystrokes
            for (int i = 0; i < 40 && AnyHeld(0x0D, 0x10, 0x11, 0x12, 0x5B, 0x5C); i++) Thread.Sleep(20);
            if (AnyHeld(0x10, 0x11, 0x12, 0x5B, 0x5C)) return Lang.T("Shift, Ctrl, atau Alt masih ditekan");
            // Focus can move during the wait
            if (GetAncestor(GetForegroundWindow(), 3) != want) return Lang.T("Terminal kehilangan fokus");

            var inputs = new INPUT[text.Length * 2 + 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = Key(0, text[i], 4);
                inputs[i * 2 + 1] = Key(0, text[i], 4 | 2);
            }
            inputs[text.Length * 2] = Key(0x0D, '\0', 0);
            inputs[text.Length * 2 + 1] = Key(0x0D, '\0', 2);
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent == inputs.Length) return null;
            Log.Write("send prompt: " + sent + "/" + inputs.Length + " err " + Marshal.GetLastWin32Error());
            return Lang.T("Gagal mengetik ke terminal");
        }

        static bool AnyHeld(params int[] keys)
        {
            foreach (int k in keys) if ((GetAsyncKeyState(k) & 0x8000) != 0) return true;
            return false;
        }

        static INPUT Key(ushort vk, char ch, uint flags)
        {
            return new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = ch, dwFlags = flags } };
        }

        // Composer for one agent session
        class AgentPromptBox : KitWindow
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

            class Sent { public string Text; public DateTime Utc; }

            // Sent prompts per project, memory only
            static readonly Dictionary<string, List<Sent>> history = new Dictionary<string, List<Sent>>(StringComparer.OrdinalIgnoreCase);
            const int HistoryMax = 20, ProjectsMax = 12, TemplateMax = 30, DesignW = 560;

            readonly Island isle;
            readonly AgentSession session;
            readonly bool shared;
            readonly ChatHeader header = new ChatHeader();
            readonly ScriptWell well = new ScriptWell();
            readonly Label target = new Label(), caution = new Label(), note = new Label();
            readonly Cue cue;
            readonly KitButton send = new KitButton(), keep = new KitButton();
            readonly KitSegmented tabs = new KitSegmented();
            readonly PromptList list = new PromptList();
            readonly KeyHints hints = new KeyHints();
            readonly List<string> templates = new List<string>();
            readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
            string termApp = "", termTitle = "", draft = "";
            int recall = -1, shape, sig;

            TextBox field { get { return well.Box; } }
            public string Prompt { get { return field.Text; } }

            static string TemplatesFile { get { return Path.Combine(Settings.Dir, "templat-chat.txt"); } }

            public AgentPromptBox(Island isle, AgentSession s)
            {
                this.isle = isle;
                session = s;
                shared = isle.SharesWindow(s);
                Text = Lang.T("Chat dengan agent");
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = MinimizeBox = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;

                header.Isle = isle;
                header.S = s;
                field.AccessibleName = Lang.T("Isi pesan");
                field.MaxLength = PromptMax;
                field.TextChanged += delegate { Typed(); well.FitBars(); };
                field.KeyDown += FieldKey;
                cue = new Cue(field) { Text = Lang.F("Pesan untuk {0}", AgentKind(s)) };
                field.AccessibleDescription = cue.Text;

                foreach (var l in new[] { target, caution, note })
                {
                    l.UseMnemonic = false;
                    l.AutoSize = false;
                    l.AutoEllipsis = true;
                    l.TextAlign = ContentAlignment.MiddleLeft;
                }
                note.TextAlign = ContentAlignment.MiddleRight;
                caution.Text = Lang.T("Jendela ini punya beberapa sesi. Teks masuk ke tab yang sedang aktif.");
                caution.Visible = shared;

                send.Text = Lang.T("Kirim");
                send.Primary = true;
                send.Click += delegate { Submit(); };
                keep.Text = Lang.T("Simpan templat");
                keep.Click += delegate { SaveTemplate(); };
                tabs.Items = new[] { Lang.T("Riwayat"), Lang.T("Templat") };
                tabs.AccessibleName = Lang.T("Riwayat dan templat");
                tabs.Changed += i => FillList();
                list.AccessibleName = Lang.T("Pesan tersimpan");
                list.Picked += Use;
                list.Removed += RemoveTemplate;

                hints.Pairs = new[] { new[] { "Enter", Lang.T("Kirim") }, new[] { "Shift Enter", Lang.T("Baris baru") } };
                hints.AccessibleName = "Enter: " + Lang.T("Kirim") + ". Shift+Enter: " + Lang.T("Baris baru") + ". Esc: " + Lang.T("Tutup") + ".";
                well.Controls.AddRange(new Control[] { hints, keep, send });
                field.TabIndex = 0;
                send.TabIndex = 1;
                keep.TabIndex = 2;
                Controls.AddRange(new Control[] { header, well, target, caution, tabs, note, list });
                header.TabStop = false;
                well.TabIndex = 0;
                tabs.TabIndex = 1;
                list.TabIndex = 2;
                LoadTemplates();
                FillList();
                Typed();
                termApp = TerminalApp(s.Window);
                Target();
                shape = header.Shape();
                sig = Signature();
                tick.Tick += delegate { Tick(); };
                ActiveControl = field;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Relayout();
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(area.X + (area.Width - Width) / 2, area.Y + Math.Max(0, (int)((area.Height - Height) * 0.42f)));
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Activate();
                field.Focus();
                tick.Start();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                tick.Stop();
                base.OnFormClosed(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) tick.Dispose();
                base.Dispose(disposing);
            }

            public override void Relayout()
            {
                float s = Ui;
                Func<float, int> u = v => (int)Math.Round(v * s);
                BackColor = KitInk.Window;
                ForeColor = KitInk.Text;
                Native.DarkFrame(Handle, !Ink.Light);
                int w = u(DesignW), m = u(24);

                header.Measure();
                header.SetBounds(0, 0, w, u(header.DesignH));
                well.BackColor = KitInk.Group;
                well.SetBounds(m, header.Bottom, w - 2 * m, u(136));
                well.Place(s);
                // Bottom strip: key hints, actions
                int strip = u(48), bh = u(30), by = well.Height - u(10) - bh;
                field.SetBounds(u(16), u(12), well.Width - u(22), well.Height - u(12) - strip);
                well.FitBars();
                send.Font = UiFont(13f);
                keep.Font = UiFont(12.5f);
                int sw = Math.Max(u(80), TextRenderer.MeasureText(send.Text, send.Font).Width + u(36));
                int kw = TextRenderer.MeasureText(keep.Text, keep.Font).Width + u(28);
                send.SetBounds(well.Width - u(10) - sw, by, sw, bh);
                keep.SetBounds(send.Left - u(6) - kw, by, kw, bh);
                hints.SetBounds(u(16), by, keep.Left - u(28), bh);

                // Which terminal receives it
                int y = well.Bottom + u(8);
                foreach (var l in new[] { target, caution, note })
                {
                    l.Font = UiFont(12f);
                    l.ForeColor = KitInk.Sub;
                    l.BackColor = KitInk.Window;
                }
                target.SetBounds(m + u(4), y, w - 2 * m - u(4), u(20));
                y += u(20);
                if (shared)
                {
                    caution.SetBounds(m + u(4), y, w - 2 * m - u(4), u(20));
                    y += u(20);
                }
                y += u(20);

                // History and templates
                tabs.SetBounds(m, y, u(200), u(28));
                note.SetBounds(tabs.Right + u(12), y, w - m - u(4) - tabs.Right - u(12), u(28));
                y += u(28) + u(10);
                list.SetBounds(m, y, w - 2 * m, u(PromptList.DesignH));
                ClientSize = new Size(w, list.Bottom + u(24));
                Invalidate(true);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                // Ctrl+Enter sends from anywhere
                if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Submit(); }
            }

            void FieldKey(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    Submit();
                    return;
                }
                if (e.KeyCode == Keys.A && e.Control) { e.SuppressKeyPress = true; field.SelectAll(); return; }
                if ((e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) || e.Modifiers != Keys.None) return;
                var mine = Mine();
                if (mine == null || mine.Count == 0) return;
                bool up = e.KeyCode == Keys.Up;
                // Recall only from first or last line
                int line = field.GetLineFromCharIndex(field.SelectionStart);
                if (up ? line > 0 : line < field.GetLineFromCharIndex(field.TextLength)) return;
                if (recall < 0)
                {
                    if (!up) return;
                    draft = field.Text;
                    recall = mine.Count;
                }
                e.SuppressKeyPress = true;
                e.Handled = true;
                recall = Math.Max(0, Math.Min(mine.Count, recall + (up ? -1 : 1)));
                field.Text = recall < mine.Count ? mine[recall].Text : draft;
                field.SelectionStart = field.TextLength;
                if (recall == mine.Count) recall = -1;
            }

            void Typed()
            {
                bool any = !string.IsNullOrWhiteSpace(field.Text);
                send.Enabled = any;
                keep.Enabled = any;
            }

            void Submit()
            {
                string text = CleanPrompt(field.Text);
                if (text.Length == 0) { field.Focus(); return; }
                Remember(session.Project, text, DateTime.UtcNow);
                DialogResult = DialogResult.OK;
                Close();
            }

            static void Remember(string project, string text, DateTime utc)
            {
                List<Sent> mine;
                if (!history.TryGetValue(project, out mine))
                {
                    // Few projects; the stalest goes
                    if (history.Count >= ProjectsMax)
                    {
                        string stale = null;
                        DateTime at = DateTime.MaxValue;
                        foreach (var kv in history)
                            if (kv.Value.Count == 0 || kv.Value[kv.Value.Count - 1].Utc < at) { stale = kv.Key; at = kv.Value.Count == 0 ? DateTime.MinValue : kv.Value[kv.Value.Count - 1].Utc; }
                        if (stale != null) history.Remove(stale);
                    }
                    mine = new List<Sent>();
                    history[project] = mine;
                }
                mine.RemoveAll(x => x.Text == text);
                mine.Add(new Sent { Text = text, Utc = utc });
                if (mine.Count > HistoryMax) mine.RemoveAt(0);
            }

            List<Sent> Mine()
            {
                List<Sent> mine;
                return history.TryGetValue(session.Project, out mine) ? mine : null;
            }

            void FillList()
            {
                list.Items.Clear();
                list.Trail.Clear();
                list.Removable = tabs.Selected == 1;
                if (tabs.Selected == 0)
                {
                    var mine = Mine();
                    if (mine != null)
                        for (int i = mine.Count - 1; i >= 0; i--) { list.Items.Add(mine[i].Text); list.Trail.Add(Fmt.AgoShort(mine[i].Utc)); }
                    list.Empty = Lang.T("Belum ada pesan untuk proyek ini");
                    note.Text = Lang.T("Hanya di memori sampai Tepi ditutup");
                }
                else
                {
                    foreach (var t in templates) { list.Items.Add(t); list.Trail.Add(""); }
                    list.Empty = Lang.T("Belum ada templat");
                    note.Text = Lang.T("Hanya yang kamu simpan, di perangkat ini");
                }
                list.AccessibleDescription = note.Text + (list.Removable ? ". " + Lang.T("Delete menghapus templat") : "");
                list.Reset();
            }

            void Use(int i)
            {
                if (i < 0 || i >= list.Items.Count) return;
                recall = -1;
                field.Text = list.Items[i];
                field.SelectionStart = field.TextLength;
                field.Focus();
            }

            // Saved only when the user asks
            void SaveTemplate()
            {
                string t = CleanPrompt(field.Text);
                if (t.Length == 0) { field.Focus(); return; }
                templates.Remove(t);
                templates.Insert(0, t);
                while (templates.Count > TemplateMax) templates.RemoveAt(templates.Count - 1);
                StoreTemplates();
                tabs.Selected = 1;
                FillList();
                field.Focus();
            }

            void RemoveTemplate(int i)
            {
                if (tabs.Selected != 1 || i < 0 || i >= templates.Count) return;
                templates.RemoveAt(i);
                StoreTemplates();
                FillList();
                if (list.Focused) list.Pick(Math.Min(i, templates.Count - 1));
            }

            void LoadTemplates()
            {
                templates.Clear();
                try
                {
                    var info = new FileInfo(TemplatesFile);
                    if (!info.Exists || info.Length > 512 * 1024) return;
                    foreach (var line in File.ReadAllLines(info.FullName, Encoding.UTF8))
                    {
                        string t = CleanPrompt(line);
                        if (t.Length > 0 && !templates.Contains(t)) templates.Add(t);
                        if (templates.Count >= TemplateMax) break;
                    }
                }
                catch (Exception ex) { Log.Write("chat templates: " + ex.Message); }
            }

            void StoreTemplates()
            {
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    File.WriteAllLines(TemplatesFile, templates, new UTF8Encoding(false));
                }
                catch (Exception ex) { Log.Write("chat templates save: " + ex.Message); }
            }

            // Friendly name of the terminal app
            static string TerminalApp(IntPtr h)
            {
                if (h == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return "";
                string name;
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
                catch (Exception) { return ""; }
                switch (name.ToLowerInvariant())
                {
                    case "windowsterminal": return "Windows Terminal";
                    case "code": return "VS Code";
                    case "cursor": return "Cursor";
                    case "windsurf": return "Windsurf";
                    case "cmd": case "conhost": case "openconsole": return "Command Prompt";
                    case "powershell": case "pwsh": return "PowerShell";
                    case "wezterm-gui": return "WezTerm";
                    case "alacritty": return "Alacritty";
                    case "mintty": return "Git Bash";
                    case "tabby": return "Tabby";
                    case "warp": return "Warp";
                }
                return name;
            }

            // Which window and tab receives it
            void Target()
            {
                string title = "";
                if (session.Window != IntPtr.Zero)
                {
                    var sb = new StringBuilder(256);
                    if (GetWindowText(session.Window, sb, sb.Capacity) > 0) title = Fmt.Visible(Fmt.OneLine(sb.ToString(), 80));
                }
                if (title == termTitle && target.Text.Length > 0) return;
                termTitle = title;
                ShowTarget();
            }

            void ShowTarget()
            {
                string where = termApp.Length > 0 ? Lang.F("Dikirim ke {0}", termApp) : Lang.T("Dikirim ke terminal sesi ini");
                target.Text = termTitle.Length > 0 && termTitle != termApp ? where + " · " + termTitle : where;
                target.AccessibleName = target.Text;
            }

            int Signature()
            {
                var s = session;
                int h = (int)s.State * 31 + (s.Failed ? 7 : 0);
                h = h * 31 + s.Files.Count;
                h = h * 31 + s.Commands;
                h = h * 31 + (s.Plan == null ? -1 : s.Plan.Count * 64 + PlanDone(s));
                h = h * 31 + PlanNow(s).GetHashCode();
                h = h * 31 + s.Tool.GetHashCode() + s.Activity.GetHashCode() + s.LastMessage.GetHashCode();
                h = h * 31 + isle.quotaVersion;
                h = h * 31 + (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute);
                return h;
            }

            // Once a second while open
            void Tick()
            {
                int now = Signature();
                if (now != sig)
                {
                    sig = now;
                    int sh = header.Shape();
                    if (sh != shape) { shape = sh; Relayout(); }
                    else { header.Measure(); header.Invalidate(); }
                }
                else if (session.State == AgentState.Working) header.Invalidate(Rectangle.Round(header.Clock));
                Target();
            }
        }

        // Project, state, plan and quota
        class ChatHeader : Control
        {
            public Island Isle;
            public AgentSession S;
            public float DesignH = 90;
            public RectangleF Clock;
            const float Head = 22, Tile = 48, QuotaH = 62;
            const TextFormatFlags Flat = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

            public ChatHeader()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                AccessibleRole = AccessibleRole.StaticText;
            }

            float Ui { get { var w = FindForm() as KitWindow; return w != null ? w.Ui : 1f; } }

            bool HasPlan { get { return S.Plan != null && S.Plan.Count > 0; } }

            // Layout changes only with these
            public int Shape()
            {
                return (HasPlan ? 1 : 0) | (NowLine(S).Length > 0 ? 2 : 0) | (Isle.QuotaFor(S) != null ? 4 : 0);
            }

            public void Measure()
            {
                float y = Head + Tile;
                bool now = NowLine(S).Length > 0;
                if (HasPlan) y += 20 + 5 + (now ? 8 + 18 : 0);
                else if (now) y += 14 + 18;
                if (Isle.QuotaFor(S) != null) y += 18 + QuotaH;
                DesignH = y + 20;
                AccessibleName = S.Project;
                AccessibleDescription = Spoken();
            }

            static string NowLine(AgentSession s)
            {
                if ((s.State == AgentState.Done || s.State == AgentState.Ended) && s.LastMessage.Length > 0) return s.LastMessage;
                string now = PlanNow(s);
                if (now.Length > 0) return now;
                return s.State == AgentState.Working || s.State == AgentState.Waiting ? AgentLine(s) ?? "" : "";
            }

            static string StateName(AgentSession s)
            {
                switch (s.State)
                {
                    case AgentState.Working: return Lang.T("Bekerja");
                    case AgentState.Waiting: return Lang.T("Menunggu izin");
                    case AgentState.Done: return s.Failed ? Lang.T("Gagal") : Lang.T("Selesai");
                    case AgentState.Ready: return Lang.T("Siap");
                }
                return Lang.T("Ditutup");
            }

            // Turn timer while working, else age
            static string StateTime(AgentSession s)
            {
                if (s.State == AgentState.Working)
                {
                    var t = DateTime.UtcNow - (s.TurnStartedUtc > DateTime.MinValue ? s.TurnStartedUtc : s.StartedUtc);
                    if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                    return t.TotalHours >= 1 ? (int)t.TotalHours + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : (int)t.TotalMinutes + ":" + t.Seconds.ToString("00");
                }
                if (s.State == AgentState.Done || s.State == AgentState.Ended) return Fmt.Ago(s.LastUtc);
                return "";
            }

            static string AgentLabel(AgentSession s)
            {
                string kind = AgentKind(s);
                return kind == "Claude" && s.Profile.Length > 0 ? kind + " · " + s.Profile : kind;
            }

            static string Stats(AgentSession s)
            {
                var parts = new List<string> { Lang.F("Sesi {0}", Duration(DateTime.UtcNow - s.StartedUtc)) };
                if (s.Files.Count > 0) parts.Add(s.Files.Count + Lang.T(" berkas"));
                if (s.Commands > 0) parts.Add(s.Commands + Lang.T(" perintah"));
                return string.Join(" · ", parts.ToArray());
            }

            // Whole header as one sentence for readers
            string Spoken()
            {
                var parts = new List<string> { AgentLabel(S), StateName(S) + (StateTime(S).Length > 0 ? " " + StateTime(S) : ""), Stats(S) };
                if (HasPlan) parts.Add(Lang.F("Rencana {0}", PlanCount(S)));
                string now = NowLine(S);
                if (now.Length > 0) parts.Add(now);
                var q = Isle.QuotaFor(S);
                if (q != null)
                {
                    Isle.QuotaTexts();
                    for (int i = 0; i < 3; i++)
                        if (q.W[i].Has) parts.Add(QuotaLabel(i) + " " + (q.W[i].Stale ? q.W[i].ResetText : q.W[i].PctText + ", " + q.W[i].ResetText));
                }
                return string.Join(". ", parts.ToArray());
            }

            static float Wide(Graphics g, string t, Font f)
            {
                return t.Length == 0 ? 0 : TextRenderer.MeasureText(g, t, f, new Size(int.MaxValue, int.MaxValue), Flat).Width;
            }

            // Left-aligned text on a centre line
            static float Put(Graphics g, string t, Font f, Color c, float x, float cy, float max)
            {
                if (t.Length == 0 || max <= 0) return x;
                float w = Math.Min(max, Wide(g, t, f));
                var r = new Rectangle((int)Math.Round(x), (int)Math.Round(cy - f.Height), (int)Math.Ceiling(w) + 1, f.Height * 2);
                TextRenderer.DrawText(g, t, f, r, c, Flat | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return x + w;
            }

            static void Round(Graphics g, RectangleF r, float radius, Color c)
            {
                using (var path = Ink.Round(r, radius))
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                KitInk.Smooth(g);
                g.Clear(KitInk.Window);
                float s = Ui, m = 24 * s, right = Width - m;

                // Agent tile, project, stats
                var tile = new RectangleF(m, Head * s, Tile * s, Tile * s);
                Round(g, tile, 13 * s, KitInk.Group);
                KitInk.Draw(g, Ink.Str(Ink.IcoTerminal), Ink.Icons(20 * s), KitInk.Text, tile, ContentAlignment.MiddleCenter);
                float tx = tile.Right + 14 * s, titleCy = tile.Y + 14 * s, lineCy = tile.Y + 37 * s;
                var small = Ink.Text(12.5f * s);
                string stats = Stats(S);
                float statsW = Wide(g, stats, small);
                Put(g, stats, small, KitInk.Sub, right - statsW, titleCy + 1 * s, statsW);
                Put(g, S.Project, Ink.Text(24f * s), KitInk.Text, tx, titleCy, right - statsW - 16 * s - tx);

                // Agent, live dot, state, time
                float x = Put(g, AgentLabel(S), small, KitInk.Sub, tx, lineCy, 200 * s) + 12 * s;
                Dot(g, x + 4 * s, lineCy, s);
                x += 14 * s;
                x = Put(g, StateName(S), Ink.Semi(12.5f * s), KitInk.Text, x, lineCy, right - x);
                string time = StateTime(S);
                if (time.Length > 0) x = Put(g, " · " + time, small, KitInk.Sub, x, lineCy, right - x);
                Clock = new RectangleF(tx, lineCy - 10 * s, right - tx, 20 * s);

                float y = (Head + Tile) * s;
                string line = NowLine(S);
                var lf = Ink.Text(13f * s);
                if (HasPlan)
                {
                    y += 20 * s;
                    var cf = Ink.Semi(12f * s);
                    string count = PlanCount(S);
                    float cw = Wide(g, count, cf);
                    PlanBar(g, new RectangleF(m, y, right - m - cw - 12 * s, 5 * s), s);
                    Put(g, count, cf, KitInk.Sub, right - cw, y + 2.5f * s, cw);
                    y += 5 * s;
                    if (line.Length > 0) { y += 8 * s; Put(g, line, lf, KitInk.Sub, m, y + 9 * s, right - m); y += 18 * s; }
                }
                else if (line.Length > 0) { y += 14 * s; Put(g, line, lf, KitInk.Sub, m, y + 9 * s, right - m); y += 18 * s; }

                var q = Isle.QuotaFor(S);
                if (q != null) QuotaCard(g, q, new RectangleF(m, y + 18 * s, right - m, QuotaH * s), s);
            }

            void Dot(Graphics g, float cx, float cy, float s)
            {
                Color c;
                switch (S.State)
                {
                    case AgentState.Working: c = KitInk.Accent; break;
                    case AgentState.Waiting: c = KitInk.Text; break;
                    case AgentState.Done: c = KitInk.Sub; break;
                    case AgentState.Ready:
                        using (var pen = new Pen(KitInk.Sub, 1.5f * s)) g.DrawEllipse(pen, cx - 3.5f * s, cy - 3.5f * s, 7 * s, 7 * s);
                        return;
                    default: c = KitInk.Faint; break;
                }
                // Live states get a soft halo
                if (S.State == AgentState.Working || S.State == AgentState.Waiting)
                    using (var b = new SolidBrush(Color.FromArgb(56, c))) g.FillEllipse(b, cx - 7 * s, cy - 7 * s, 14 * s, 14 * s);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 4 * s, cy - 4 * s, 8 * s, 8 * s);
            }

            // Segment per step, like the Agen page
            void PlanBar(Graphics g, RectangleF r, float s)
            {
                int n = S.Plan.Count;
                var rest = Ink.Mix(KitInk.Window, KitInk.Text, Ink.Light ? 0.12f : 0.16f);
                var live = Ink.Mix(rest, KitInk.Accent, 0.5f);
                if (n > 14)
                {
                    Round(g, r, r.Height / 2, rest);
                    Round(g, new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * PlanDone(S) / n), r.Height), r.Height / 2, KitInk.Accent);
                    return;
                }
                float gap = 3 * s, w = (r.Width - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++)
                {
                    int st = S.Plan[i].Status;
                    Round(g, new RectangleF(r.X + i * (w + gap), r.Y, w, r.Height), r.Height / 2, st == 2 ? KitInk.Accent : st == 1 ? live : rest);
                }
            }

            // Rings per window; model and freshness right
            void QuotaCard(Graphics g, QuotaInfo q, RectangleF card, float s)
            {
                Isle.QuotaTexts();
                Round(g, card, 12 * s, KitInk.Group);
                float cy = card.Y + card.Height / 2, x = card.X + 18 * s, top = cy - 9 * s, low = cy + 10 * s;
                var lf = Ink.Text(13f * s);
                var pf = Ink.Semi(13f * s);
                var rf = Ink.Text(11.5f * s);
                var track = Ink.Mix(KitInk.Group, KitInk.Text, Ink.Light ? 0.12f : 0.16f);
                for (int i = 0; i < 3; i++)
                {
                    var w = q.W[i];
                    if (!w.Has) continue;
                    float r = 11 * s, rx = x + r;
                    using (var pen = new Pen(track, 3 * s)) g.DrawEllipse(pen, rx - r, cy - r, r * 2, r * 2);
                    if (!w.Stale && w.Pct > 0)
                        using (var pen = new Pen(KitInk.Accent, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawArc(pen, rx - r, cy - r, r * 2, r * 2, -90, Math.Max(2f, 360f * Math.Min(1f, w.Pct / 100f)));
                    bool hot = !w.Stale && w.Pct >= 80;
                    float lx = x + 2 * r + 12 * s;
                    float ex = Put(g, QuotaLabel(i), lf, hot ? KitInk.Text : KitInk.Sub, lx, top, 120 * s);
                    if (!w.Stale) ex = Put(g, w.PctText, pf, KitInk.Text, ex + 6 * s, top, 60 * s);
                    float ex2 = Put(g, w.ResetText, rf, KitInk.Faint, lx, low, 160 * s);
                    x = Math.Max(ex, ex2) + 28 * s;
                }
                float edge = card.Right - 18 * s, room = edge - x;
                float mw = Wide(g, q.Model, Ink.Text(12f * s)), tw = Wide(g, q.Tail, rf);
                if (q.Model.Length > 0 && mw <= room) Put(g, q.Model, Ink.Text(12f * s), KitInk.Sub, edge - mw, top, mw);
                if (tw <= room) Put(g, q.Tail, rf, KitInk.Faint, edge - tw, low, tw);
            }
        }

        // Placeholder painted inside the edit
        class Cue : NativeWindow
        {
            struct RECT { public int Left, Top, Right, Bottom; }
            [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT r);
            [DllImport("user32.dll")] static extern bool HideCaret(IntPtr h);
            [DllImport("user32.dll")] static extern bool ShowCaret(IntPtr h);

            readonly TextBox box;
            public string Text = "";
            bool empty = true;

            public Cue(TextBox box)
            {
                this.box = box;
                box.HandleCreated += delegate { AssignHandle(box.Handle); };
                box.HandleDestroyed += delegate { ReleaseHandle(); };
                // Edits repaint only the changed run
                box.TextChanged += delegate { bool now = box.TextLength == 0; if (now != empty) { empty = now; box.Invalidate(); } };
                if (box.IsHandleCreated) AssignHandle(box.Handle);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                // WM_PAINT, and WM_PRINTCLIENT for snapshots
                if ((m.Msg != 0x000F && m.Msg != 0x0318) || box.TextLength > 0 || Text.Length == 0) return;
                var r = new RECT();
                SendMessage(Handle, 0x00B2, IntPtr.Zero, ref r);
                var rect = Rectangle.FromLTRB(r.Left, r.Top, Math.Max(r.Left + 1, r.Right), r.Top + box.Font.Height + 2);
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                if (m.Msg == 0x0318)
                {
                    using (var g = Graphics.FromHdc(m.WParam)) TextRenderer.DrawText(g, Text, box.Font, rect, KitInk.Faint, flags);
                    return;
                }
                HideCaret(Handle);
                using (var g = box.CreateGraphics()) TextRenderer.DrawText(g, Text, box.Font, rect, KitInk.Faint, flags);
                ShowCaret(Handle);
            }
        }

        // Key caps with short labels, inline
        class KeyHints : Control
        {
            public string[][] Pairs = new string[0][];

            public KeyHints()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                AccessibleRole = AccessibleRole.StaticText;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                KitInk.Smooth(g);
                g.Clear(KitInk.Under(this));
                var w = FindForm() as KitWindow;
                float s = w != null ? w.Ui : 1f, x = 0, h = 20 * s, y = (Height - h) / 2;
                var cf = Ink.Text(11f * s);
                var lf = Ink.Text(12f * s);
                var cap = Ink.Mix(KitInk.Group, KitInk.Text, Ink.Light ? 0.1f : 0.13f);
                foreach (var p in Pairs)
                {
                    float cw = Math.Max(h, TextRenderer.MeasureText(p[0], cf).Width + 4 * s);
                    float lw = TextRenderer.MeasureText(p[1], lf).Width;
                    // Only whole pairs; drop what won't fit
                    if (x + cw + 4 * s + lw > Width) break;
                    var r = new RectangleF(x, y, cw, h);
                    using (var path = Ink.Round(r, 6 * s))
                    using (var b = new SolidBrush(cap)) g.FillPath(b, path);
                    KitInk.Draw(g, p[0], cf, KitInk.Sub, r, ContentAlignment.MiddleCenter);
                    KitInk.Draw(g, p[1], lf, KitInk.Faint, new RectangleF(r.Right + 2 * s, 0, lw, Height), ContentAlignment.MiddleLeft);
                    x = r.Right + 2 * s + lw + 10 * s;
                }
            }
        }

        // Prompt rows: click or Enter to reuse
        class PromptList : KitControl
        {
            public readonly List<string> Items = new List<string>(), Trail = new List<string>();
            public bool Removable;
            public string Empty = "";
            public event Action<int> Picked, Removed;
            const int Rows = 4;
            const float RowH = 38;
            public const float DesignH = Rows * RowH + 8;
            int sel = -1, hover = -1, top;
            bool overX;

            public void Reset()
            {
                top = 0;
                hover = -1;
                sel = Items.Count > 0 && Focused ? 0 : -1;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }

            RectangleF Row(int slot) { float s = Ui; return new RectangleF(0, 4 * s + slot * RowH * s, Width, RowH * s); }

            RectangleF XBox(RectangleF r) { float s = Ui, d = 24 * s; return new RectangleF(r.Right - 12 * s - d, r.Y + (r.Height - d) / 2, d, d); }

            int At(Point p)
            {
                for (int k = 0; k < Rows && top + k < Items.Count; k++) if (Row(k).Contains(p)) return top + k;
                return -1;
            }

            public void Pick(int i)
            {
                if (Items.Count == 0) { sel = -1; Invalidate(); return; }
                sel = Math.Max(0, Math.Min(Items.Count - 1, i));
                if (sel < top) top = sel;
                else if (sel >= top + Rows) top = sel - Rows + 1;
                Invalidate();
                if (!Focused) return;
                AccessibilityNotifyClients(AccessibleEvents.Selection, sel);
                AccessibilityNotifyClients(AccessibleEvents.Focus, sel);
            }

            void Fire(Action<int> h, int i) { if (h != null && i >= 0 && i < Items.Count) h(i); }

            void ScrollBy(int d)
            {
                int t = Math.Max(0, Math.Min(Math.Max(0, Items.Count - Rows), top + d));
                if (t != top) { top = t; Invalidate(); }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int h = At(e.Location);
                bool x = h >= 0 && Removable && XBox(Row(h - top)).Contains(e.Location);
                if (h == hover && x == overX) return;
                hover = h;
                overX = x;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hover = -1;
                overX = false;
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                int i = At(e.Location);
                if (i < 0) return;
                if (Removable && XBox(Row(i - top)).Contains(e.Location)) Fire(Removed, i);
                else Fire(Picked, i);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                ScrollBy(e.Delta > 0 ? -1 : 1);
            }

            protected override bool IsInputKey(Keys key)
            {
                var k = key & Keys.KeyCode;
                return k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End || k == Keys.Enter || k == Keys.Delete || base.IsInputKey(key);
            }

            protected override void OnGotFocus(EventArgs e)
            {
                if (sel < 0 && Items.Count > 0) sel = 0;
                base.OnGotFocus(e);
                if (sel >= 0) AccessibilityNotifyClients(AccessibleEvents.Focus, sel);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (Items.Count == 0) return;
                switch (e.KeyCode)
                {
                    case Keys.Up: Pick(sel - 1); break;
                    case Keys.Down: Pick(sel + 1); break;
                    case Keys.Home: Pick(0); break;
                    case Keys.End: Pick(Items.Count - 1); break;
                    case Keys.Enter: case Keys.Space: Fire(Picked, sel); break;
                    case Keys.Delete: if (Removable) Fire(Removed, sel); break;
                    default: return;
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                KitInk.Smooth(g);
                g.Clear(KitInk.Under(this));
                float s = Ui;
                var box = new RectangleF(0, 0, Width - 1, Height - 1);
                Fill(g, box, 12 * s, KitInk.Group);
                var f = Ink.Text(13.5f * s);
                if (Items.Count == 0)
                {
                    KitInk.Draw(g, Empty, Ink.Text(13f * s), KitInk.Faint, box, ContentAlignment.MiddleCenter);
                    Ring(g, box, 12 * s);
                    return;
                }
                var tf = Ink.Text(12f * s);
                bool keys = Focused && !KitInk.ByMouse;
                using (var line = new Pen(KitInk.Line, 1f))
                    for (int k = 0; k < Rows && top + k < Items.Count; k++)
                    {
                        int i = top + k;
                        var r = Row(k);
                        bool lit = i == hover || (i == sel && keys);
                        bool litAbove = k > 0 && (i - 1 == hover || (i - 1 == sel && keys));
                        if (k > 0 && !lit && !litAbove) g.DrawLine(line, 16 * s, r.Y, Width - 16 * s, r.Y);
                        if (lit) Fill(g, new RectangleF(6 * s, r.Y + 2 * s, Width - 12 * s, r.Height - 4 * s), 9 * s, KitInk.Hover);
                        float end = Width - 16 * s;
                        if (Removable && lit)
                        {
                            // Remove mark on the lit row
                            var xb = XBox(r);
                            if (i == hover && overX) Fill(g, xb, xb.Height / 2, KitInk.Line);
                            float cx = xb.X + xb.Width / 2, cy = xb.Y + xb.Height / 2, a = 4 * s;
                            using (var pen = new Pen(i == hover && overX ? KitInk.Text : KitInk.Sub, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            {
                                g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
                                g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
                            }
                            end = xb.X - 8 * s;
                        }
                        else if (i < Trail.Count && Trail[i].Length > 0)
                        {
                            float tw = TextRenderer.MeasureText(Trail[i], tf).Width;
                            KitInk.Draw(g, Trail[i], tf, KitInk.Faint, new RectangleF(end - tw, r.Y, tw, r.Height), ContentAlignment.MiddleRight);
                            end -= tw + 10 * s;
                        }
                        KitInk.Draw(g, Items[i], f, KitInk.Text, new RectangleF(12 * s, r.Y, end - 12 * s, r.Height), ContentAlignment.MiddleLeft);
                    }
                if (Items.Count > Rows)
                {
                    // Slim position mark when it scrolls
                    float trackH = Height - 16 * s, h = Math.Max(16 * s, trackH * Rows / Items.Count);
                    float y = 8 * s + (trackH - h) * top / Math.Max(1, Items.Count - Rows);
                    Fill(g, new RectangleF(Width - 5 * s, y, 3 * s, h), 1.5f * s, KitInk.Line);
                }
                Ring(g, box, 12 * s);
            }

            protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

            class Access : ControlAccessibleObject
            {
                readonly PromptList owner;
                public Access(PromptList owner) : base(owner) { this.owner = owner; }
                public override AccessibleRole Role { get { return AccessibleRole.List; } }
                public override int GetChildCount() { return owner.Items.Count; }
                public override AccessibleObject GetChild(int i) { return i >= 0 && i < owner.Items.Count ? new Item(owner, i) : null; }
                public override AccessibleObject GetSelected() { return owner.sel >= 0 ? GetChild(owner.sel) : null; }
                public override AccessibleObject GetFocused() { return owner.Focused && owner.sel >= 0 ? GetChild(owner.sel) : null; }
                public override string Description { get { return owner.Items.Count == 0 ? owner.Empty : base.Description; } }
            }

            class Item : AccessibleObject
            {
                readonly PromptList owner;
                readonly int index;
                public Item(PromptList owner, int index) { this.owner = owner; this.index = index; }
                public override string Name { get { return index < owner.Items.Count ? owner.Items[index] : ""; } }
                public override string Description { get { return index < owner.Trail.Count && owner.Trail[index].Length > 0 ? owner.Trail[index] : null; } }
                public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
                public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
                public override AccessibleStates State
                {
                    get
                    {
                        var st = AccessibleStates.Selectable | AccessibleStates.Focusable;
                        if (index == owner.sel) st |= AccessibleStates.Selected | (owner.Focused ? AccessibleStates.Focused : 0);
                        if (index < owner.top || index >= owner.top + Rows) st |= AccessibleStates.Offscreen;
                        return st;
                    }
                }
                public override Rectangle Bounds
                {
                    get
                    {
                        int slot = Math.Max(0, Math.Min(Rows - 1, index - owner.top));
                        return owner.RectangleToScreen(Rectangle.Round(owner.Row(slot)));
                    }
                }
                public override string DefaultAction { get { return Lang.T("Pakai"); } }
                public override void DoDefaultAction() { owner.Fire(owner.Picked, index); }
            }
        }
    }
}
