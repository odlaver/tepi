using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // One message in the Suruh Tepi chat
    class ChatMsg
    {
        // "me" or "tepi"
        public string Who = "tepi";
        public string Text = "";
        public DateTime Utc = DateTime.UtcNow;
        public readonly List<ChatAct> Acts = new List<ChatAct>();
        // Failed reply: "retry", or "ai" when none is set up
        public string Fix;
        public bool Mine { get { return Who == "me"; } }
    }

    // Something Tepi did, shown as a chip
    class ChatAct
    {
        public char Glyph;
        public string Text = "";
        // Pending reminder: the chip can cancel it
        public OneShot Reminder;
        public bool Undone;
    }

    // Suruh Tepi as a chat: kept on disk, shown in its own window
    partial class Island
    {
        const int ChatKeep = 200, ChatContext = 16;
        const char IcoNote = '', IcoFail = '', IcoDone = '', IcoCopy = '', IcoSend = '';
        List<ChatMsg> chat;
        // Cleared chat, back with Undo for a moment
        List<ChatMsg> chatCleared;
        ChatWindow chatWin;
        // Spot and unsent text, for this run
        Rectangle chatBounds;
        string chatDraft = "";

        static string ChatPath { get { return Path.Combine(Settings.Dir, "obrolan.json"); } }

        void OpenSuruh()
        {
            expanded = false;
            Kick();
            LoadChat();
            if (chatWin == null || chatWin.IsDisposed)
            {
                chatWin = new ChatWindow(this);
                chatWin.FormClosed += delegate { chatWin = null; chatCleared = null; };
                chatWin.Show();
            }
            else if (chatWin.WindowState == FormWindowState.Minimized) chatWin.WindowState = FormWindowState.Normal;
            // Above the app in front, then focused
            chatWin.TopMost = true;
            chatWin.TopMost = false;
            chatWin.Activate();
            Native.SetForegroundWindow(chatWin.Handle);
            chatWin.FocusInput();
        }

        // Read once, on first use
        void LoadChat()
        {
            if (chat != null) return;
            chat = new List<ChatMsg>();
            try
            {
                if (!File.Exists(ChatPath)) return;
                var all = Json.Parse(File.ReadAllText(ChatPath, Encoding.UTF8)) as object[];
                if (all == null) return;
                foreach (var o in all)
                {
                    DateTime utc;
                    if (!DateTime.TryParse(Json.Str(o, "utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc)) utc = DateTime.UtcNow;
                    var m = new ChatMsg { Who = Json.Str(o, "who") == "me" ? "me" : "tepi", Text = Json.Str(o, "text"), Utc = utc.ToUniversalTime() };
                    string fix = Json.Str(o, "fix");
                    if (fix.Length > 0) m.Fix = fix;
                    foreach (var a in Json.Arr(o, "acts"))
                        m.Acts.Add(new ChatAct
                        {
                            Glyph = (char)(int)Json.Num(a, "g", IcoDone),
                            Text = Json.Str(a, "t"),
                            Undone = Json.Str(a, "undone") == "True",
                            Reminder = FindOneShot(Json.Str(a, "at"), Json.Str(a, "what")),
                        });
                    chat.Add(m);
                }
            }
            catch (Exception ex) { Log.Write("obrolan: " + ex.Message); }
        }

        // A saved chip finds its reminder again
        OneShot FindOneShot(string at, string what)
        {
            if (at.Length == 0) return null;
            foreach (var o in oneShots)
                if (o.Text == what && o.At.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) == at) return o;
            return null;
        }

        void SaveChat()
        {
            try
            {
                while (chat.Count > ChatKeep) chat.RemoveAt(0);
                var all = new List<object>();
                foreach (var m in chat)
                {
                    var d = new Dictionary<string, object> { { "who", m.Who }, { "text", m.Text }, { "utc", m.Utc.ToString("o", CultureInfo.InvariantCulture) } };
                    if (m.Fix != null) d["fix"] = m.Fix;
                    if (m.Acts.Count > 0)
                    {
                        var acts = new List<object>();
                        foreach (var a in m.Acts)
                        {
                            var ad = new Dictionary<string, object> { { "g", (int)a.Glyph }, { "t", a.Text } };
                            if (a.Undone) ad["undone"] = true;
                            if (a.Reminder != null)
                            {
                                ad["at"] = a.Reminder.At.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                                ad["what"] = a.Reminder.Text;
                            }
                            acts.Add(ad);
                        }
                        d["acts"] = acts;
                    }
                    all.Add(d);
                }
                Directory.CreateDirectory(Settings.Dir);
                AtomicFile.Write(ChatPath, new UTF8Encoding(false).GetBytes(Json.Encode(all)));
            }
            catch (Exception ex) { Log.Write("simpan obrolan: " + ex.Message); }
        }

        // Replies follow their question
        void AddChat(ChatMsg m, ChatMsg after)
        {
            LoadChat();
            if (after == null) chat.Add(m);
            else
            {
                int at = chat.IndexOf(after);
                // Its question was cleared away
                if (at < 0) return;
                chat.Insert(at + 1, m);
            }
            SaveChat();
            ChatChanged(m);
        }

        void ChatChanged(ChatMsg added)
        {
            if (chatWin != null && !chatWin.IsDisposed) chatWin.Changed(added);
        }

        // AI turns up to this ask; failed replies left out
        List<KeyValuePair<string, string>> ChatTurns(ChatMsg last)
        {
            var turns = new List<KeyValuePair<string, string>>();
            int end = chat.IndexOf(last);
            for (int i = Math.Max(0, end - ChatContext + 1); i <= end; i++)
            {
                var m = chat[i];
                if (m.Fix != null) continue;
                string role = m.Mine ? "user" : "assistant", text = m.Mine ? m.Text : Recap(m);
                if (text.Length == 0) continue;
                int n = turns.Count;
                // Same speaker twice: one turn
                if (n > 0 && turns[n - 1].Key == role) turns[n - 1] = new KeyValuePair<string, string>(role, turns[n - 1].Value + "\n" + text);
                else turns.Add(new KeyValuePair<string, string>(role, text));
            }
            return turns;
        }

        // Tepi's turn for the AI: words, then what it did
        static string Recap(ChatMsg m)
        {
            var sb = new StringBuilder(m.Text);
            foreach (var a in m.Acts) sb.Append(sb.Length > 0 ? "\n" : "").Append('(').Append(a.Text).Append(a.Undone ? ", cancelled" : "").Append(')');
            return sb.ToString();
        }

        // One line for toasts and the Voice list
        static string ChatSummary(ChatMsg m)
        {
            if (m.Text.Length > 0) return m.Text;
            var parts = new List<string>();
            foreach (var a in m.Acts) parts.Add(a.Text);
            return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : Lang.T("Tidak ada yang dikerjakan");
        }

        bool ChatInFront { get { return chatWin != null && !chatWin.IsDisposed && Form.ActiveForm == chatWin; } }

        string LastAsk()
        {
            if (chat == null) return null;
            for (int i = chat.Count - 1; i >= 0; i--) if (chat[i].Mine) return chat[i].Text;
            return null;
        }

        void ClearChat()
        {
            if (chat == null || chat.Count == 0 || suruhBusy) return;
            chatCleared = chat;
            chat = new List<ChatMsg>();
            SaveChat();
            ChatChanged(null);
        }

        void RestoreChat()
        {
            if (chatCleared == null) return;
            // Anything said since stays after
            chatCleared.AddRange(chat);
            chat = chatCleared;
            chatCleared = null;
            SaveChat();
            ChatChanged(null);
        }

        bool CanUndo(ChatAct a) { return a.Reminder != null && !a.Undone && oneShots.Contains(a.Reminder); }

        void UndoAct(ChatAct a)
        {
            if (!CanUndo(a)) return;
            CancelOneShot(a.Reminder);
            a.Undone = true;
            SaveChat();
            ChatChanged(null);
        }

        string ChatStatus()
        {
            if (suruhBusy) return Lang.T("Sedang menjawab…");
            var p = Ai.Text;
            return Ai.Ready(p) ? Lang.F("Jawaban dari {0}", Ai.Label(p)) : Lang.T("Tanpa layanan AI");
        }

        // Beside the island: under it, or next to a side one
        Point ChatPlace(Size size)
        {
            var area = Screen.FromHandle(Handle).WorkingArea;
            int m = (int)(16 * scale), x, y = area.Y + (int)(64 * scale);
            switch (PlaceNow)
            {
                case PlaceTopLeft: x = area.X + m; break;
                case PlaceTopRight: x = area.Right - size.Width - m; break;
                case PlaceLeft: x = area.X + (int)(84 * scale); y = area.Y + (area.Height - size.Height) / 3; break;
                case PlaceRight: x = area.Right - size.Width - (int)(84 * scale); y = area.Y + (area.Height - size.Height) / 3; break;
                default: x = area.X + (area.Width - size.Width) / 2; break;
            }
            return new Point(Math.Max(area.X, Math.Min(area.Right - size.Width, x)), Math.Max(area.Y, Math.Min(area.Bottom - size.Height, y)));
        }

        // Tepi's face: a dark disc, two eyes
        static void DrawFace(Graphics g, RectangleF r, float look)
        {
            using (var b = new SolidBrush(Color.FromArgb(255, 18, 18, 20))) g.FillEllipse(b, r);
            // Rim keeps it apart from a dark window
            using (var pen = new Pen(KitInk.Line, 1f)) g.DrawEllipse(pen, r);
            float d = r.Width, ew = d * 0.15f, eh = d * 0.27f, gap = d * 0.2f;
            float cx = r.X + d / 2 + look * d * 0.08f, cy = r.Y + d / 2;
            using (var b = new SolidBrush(Color.White))
                foreach (float x in new[] { cx - gap / 2 - ew, cx + gap / 2 })
                    using (var path = Ink.Round(new RectangleF(x, cy - eh / 2, ew, eh), ew / 2)) g.FillPath(b, path);
        }

        // Rounded box, each corner its own radius
        static GraphicsPath Corners(RectangleF r, float tl, float tr, float br, float bl)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, tl * 2, tl * 2, 180, 90);
            p.AddArc(r.Right - tr * 2, r.Y, tr * 2, tr * 2, 270, 90);
            p.AddArc(r.Right - br * 2, r.Bottom - br * 2, br * 2, br * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - bl * 2, bl * 2, bl * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Tepi's bubbles: a step off the window
        static Color TepiFill { get { return Ink.Light ? KitInk.Hover : Ink.Mix(KitInk.Group, KitInk.Line, 0.4f); } }

        // Multi-line box with its own placeholder
        class ChatInput : KitText
        {
            public string Cue = "";
            [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

            public ChatInput()
            {
                Multiline = true;
                AcceptsReturn = true;
                WordWrap = true;
                BorderStyle = BorderStyle.None;
                ScrollBars = ScrollBars.None;
            }

            // Margins at zero: the placeholder sits where text starts
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                SendMessage(Handle, 0x00D3, (IntPtr)3, IntPtr.Zero);
            }

            public int LineCount() { return IsHandleCreated ? Math.Max(1, (int)SendMessage(Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero)) : 1; }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                // Multi-line boxes skip Ctrl+A on their own
                if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.SuppressKeyPress = true; return; }
                base.OnKeyDown(e);
            }

            protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (TextLength <= 1) Invalidate(); }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F || TextLength > 0 || Cue.Length == 0) return;
                using (var g = Graphics.FromHwnd(Handle))
                    TextRenderer.DrawText(g, Cue, Font, new Point(0, 0), KitInk.Sub, BackColor, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        // Rounded well around the text box
        class ChatWell : Panel
        {
            public bool Lit;

            public ChatWell()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                KitInk.Smooth(g);
                g.Clear(KitInk.Window);
                var w = FindForm() as KitWindow;
                float s = w != null ? w.Ui : 1;
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (var path = Ink.Round(r, Math.Min(20 * s, r.Height / 2)))
                {
                    using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                    // Focus: a stronger rim
                    using (var pen = new Pen(Lit ? KitInk.Sub : KitInk.Line, Math.Max(1f, 1.2f * s))) g.DrawPath(pen, path);
                }
            }
        }

        // Suruh Tepi window: header, chat, text box
        class ChatWindow : KitWindow
        {
            const float HeadDip = 62;
            readonly Island island;
            readonly ChatList list;
            readonly ChatWell well = new ChatWell();
            readonly ChatInput input = new ChatInput();
            readonly KitButton send = new KitButton { Primary = true };
            readonly KitButton fresh = new KitButton();
            readonly Timer pulse = new Timer { Interval = 40 };
            int lines = 1;
            // Busy animation clock
            public float Phase;

            public ChatWindow(Island island)
            {
                this.island = island;
                list = new ChatList(island, this);
                Text = Lang.T("Suruh Tepi");
                StartPosition = FormStartPosition.Manual;
                input.Text = island.chatDraft;
                input.KeyDown += InputKey;
                input.TextChanged += delegate { Fit(); Ready(); };
                input.GotFocus += delegate { well.Lit = true; well.Invalidate(); };
                input.LostFocus += delegate { well.Lit = false; well.Invalidate(); };
                send.Click += delegate { Send(); };
                fresh.Click += delegate { list.Clear(); };
                pulse.Tick += delegate
                {
                    Phase += 0.3f;
                    list.Pulse();
                    Invalidate(Rectangle.Round(FaceRect()));
                };
                well.Controls.Add(input);
                well.Controls.Add(send);
                Controls.Add(list);
                Controls.Add(well);
                Controls.Add(fresh);
                // Type first, send next, read after
                well.TabIndex = 0;
                list.TabIndex = 1;
                fresh.TabIndex = 2;
                FormClosing += delegate
                {
                    island.chatDraft = input.Text;
                    if (WindowState == FormWindowState.Normal) island.chatBounds = Bounds;
                };
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                MinimumSize = new Size((int)(340 * Ui), (int)(400 * Ui));
                bool seen = false;
                foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(island.chatBounds)) seen = true;
                if (seen) Bounds = island.chatBounds;
                else
                {
                    var area = Screen.FromHandle(island.Handle).WorkingArea;
                    int chromeW = Width - ClientSize.Width, chromeH = Height - ClientSize.Height;
                    var size = new Size(Math.Min((int)(420 * Ui) + chromeW, area.Width - 40), Math.Min((int)(600 * Ui) + chromeH, area.Height - 40));
                    Bounds = new Rectangle(island.ChatPlace(size), size);
                }
                Relayout();
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Fit();
                list.ToEnd();
                FocusInput();
            }

            // Settings may have changed meanwhile
            protected override void OnActivated(EventArgs e)
            {
                base.OnActivated(e);
                if (!IsHandleCreated) return;
                list.Relayout();
                Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(HeadDip * Ui)));
            }

            protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Place(); }

            protected override void OnFormClosed(FormClosedEventArgs e) { pulse.Stop(); base.OnFormClosed(e); }

            public void FocusInput()
            {
                if (!input.CanFocus) return;
                input.Focus();
                input.SelectionStart = input.TextLength;
            }

            public override void Relayout()
            {
                float s = Ui;
                BackColor = KitInk.Window;
                Native.DarkFrame(Handle, !Ink.Light);
                Text = Lang.T("Suruh Tepi");
                well.BackColor = KitInk.Group;
                input.Font = Ink.Text(14f * s);
                input.BackColor = KitInk.Group;
                input.ForeColor = KitInk.Text;
                input.Cue = Lang.T("Tanya atau suruh Tepi…");
                input.AccessibleName = Lang.T("Suruh Tepi");
                send.Font = Ink.Icons(14f * s);
                send.Text = Ink.Str(IcoSend);
                send.AccessibleName = Lang.T("Kirim");
                fresh.Font = Ink.Text(12.5f * s);
                fresh.Text = Lang.T("Obrolan baru");
                list.AccessibleName = Lang.T("Obrolan");
                lines = Math.Max(1, Math.Min(6, input.LineCount()));
                Place();
                list.Relayout();
                Ready();
                Invalidate(true);
            }

            void Place()
            {
                float s = Ui;
                int w = ClientSize.Width, h = ClientSize.Height;
                int lineH = input.Font.Height, inputH = lines * lineH + 2;
                int wellH = inputH + (int)(28 * s);
                int fw = TextRenderer.MeasureText(fresh.Text, fresh.Font).Width + (int)(26 * s);
                fresh.SetBounds(w - (int)(16 * s) - fw, (int)(16 * s), fw, (int)(30 * s));
                well.SetBounds((int)(12 * s), h - (int)(12 * s) - wellH, w - (int)(24 * s), wellH);
                int head = (int)(HeadDip * s);
                list.SetBounds(0, head, w, Math.Max(10, well.Top - (int)(6 * s) - head));
                // Send: centred on one line, low as text grows
                int d = (int)(32 * s), low = Math.Max(2, (lineH + 2 + (int)(28 * s) - d) / 2);
                send.SetBounds(well.Width - low - (int)(2 * s) - d, wellH - low - d, d, d);
                int ix = (int)(18 * s);
                input.SetBounds(ix, (int)(14 * s), Math.Max(10, send.Left - (int)(8 * s) - ix), inputH);
                Invalidate();
            }

            // The box grows to six lines
            void Fit()
            {
                int n = Math.Max(1, Math.Min(6, input.LineCount()));
                if (n == lines) return;
                lines = n;
                Place();
            }

            void Ready()
            {
                send.Enabled = input.Text.Trim().Length > 0 && !island.suruhBusy;
                fresh.Enabled = island.chat != null && island.chat.Count > 0 && !island.suruhBusy;
                if (island.suruhBusy && !Anim.Reduced) pulse.Start();
                else pulse.Stop();
            }

            // From the island: new message or busy changed
            public void Changed(ChatMsg added)
            {
                list.Reflow(added);
                Ready();
                Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(HeadDip * Ui)));
                if (added != null && !added.Mine && Form.ActiveForm == this) Say(ChatSummary(added));
            }

            // Readers hear replies while the chat is open
            void Say(string text)
            {
                if (!island.settings.GetBool("a11y.announce", true) || !ScreenReaderOn()) return;
                try
                {
                    AccessibilityObject.RaiseAutomationNotification(
                        System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                        System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent, text);
                }
                catch (Exception ex) { Log.Write("obrolan announce: " + ex.Message); }
            }

            void InputKey(object sender, KeyEventArgs e)
            {
                // Enter sends; Shift+Enter starts a new line
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    Send();
                    return;
                }
                // Up in an empty box: the last ask, like a terminal
                if (e.KeyCode == Keys.Up && input.TextLength == 0)
                {
                    string last = island.LastAsk();
                    if (last == null) return;
                    e.SuppressKeyPress = true;
                    input.Text = last;
                    input.SelectionStart = input.TextLength;
                }
            }

            void Send()
            {
                string text = input.Text.Trim();
                if (text.Length == 0 || island.suruhBusy) return;
                input.Clear();
                island.chatDraft = "";
                island.RunSuruh(text);
                FocusInput();
            }

            // An idea or a past ask, ready to edit
            public void Suggest(string text)
            {
                input.Text = text;
                FocusInput();
            }

            RectangleF FaceRect() { float s = Ui; return new RectangleF(16 * s, 15 * s, 32 * s, 32 * s); }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(KitInk.Window);
                KitInk.Smooth(g);
                float s = Ui, head = HeadDip * s;
                var face = FaceRect();
                DrawFace(g, face, island.suruhBusy && !Anim.Reduced ? (float)Math.Sin(Phase * 0.45f) : 0);
                float tx = face.Right + 10 * s, tw = Math.Max(10, fresh.Left - 10 * s - tx);
                KitInk.Draw(g, "Tepi", Ink.Semi(14.5f * s), KitInk.Text, new RectangleF(tx, 12 * s, tw, 20 * s), ContentAlignment.MiddleLeft);
                KitInk.Draw(g, island.ChatStatus(), Ink.Text(12f * s), KitInk.Sub, new RectangleF(tx, 31 * s, tw, 18 * s), ContentAlignment.MiddleLeft);
                using (var pen = new Pen(KitInk.Line, 1f)) g.DrawLine(pen, 0, head - 1, ClientSize.Width, head - 1);
            }

            protected override void Dispose(bool disposing) { if (disposing) pulse.Dispose(); base.Dispose(disposing); }
        }

        // Messages, chips, typing dots; scrolls on its own
        class ChatList : KitControl
        {
            const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
            const TextFormatFlags Single = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            static readonly string[][] Ideas =
            {
                new[] { "Ingetin minum 30 menit lagi", "" }, new[] { "Timer 25 menit", "" },
                new[] { "15% dari 240.000", "" }, new[] { "Catat: beli kopi", "" },
            };
            readonly Island island;
            readonly ChatWindow win;
            readonly List<Item> items = new List<Item>();
            readonly List<Tap> taps = new List<Tap>();
            readonly Timer glide = new Timer { Interval = 15 };
            readonly Timer tick = new Timer { Interval = 1200 };
            readonly Timer undoEnd = new Timer { Interval = 8000 };
            float scroll, target, contentH;
            // Follows new messages until scrolled up
            bool pinned = true;
            int laidW = -1, laidH = -1, focus = -1, hoverItem = -1;
            string hot, pressed;
            // Thumb drag: pointer offset inside it
            float grab = -1;
            ChatMsg copied;
            RectangleF emptyFace, titleRect, noteRect;
            string emptyNote = "";

            class Item
            {
                public ChatMsg Msg;
                public string Stamp;
                public bool Typing;
                public RectangleF Box, Bubble, Face;
                public readonly List<RectangleF> Chips = new List<RectangleF>();
            }

            class Tap
            {
                public string Id, Label = "";
                public char Glyph;
                public RectangleF Rect;
                public Action Act;
                // Copy shows only over its own message
                public int Item = -1;
                // Undo banner: fixed, not scrolled
                public bool Fixed;
            }

            public ChatList(Island island, ChatWindow win)
            {
                this.island = island;
                this.win = win;
                glide.Tick += delegate
                {
                    scroll += (target - scroll) * 0.28f;
                    if (Math.Abs(target - scroll) < 0.6f) { scroll = target; glide.Stop(); }
                    Track(PointToClient(Cursor.Position));
                    Invalidate();
                };
                tick.Tick += delegate { tick.Stop(); copied = null; Invalidate(); };
                undoEnd.Tick += delegate { undoEnd.Stop(); island.chatCleared = null; Reflow(null); };
            }

            List<ChatMsg> Chat { get { return island.chat ?? new List<ChatMsg>(); } }
            bool Empty { get { return Chat.Count == 0 && !island.suruhBusy; } }
            Font MsgFont { get { return Ink.Text(14f * Ui); } }
            Font ChipFont { get { return Ink.Text(13f * Ui); } }
            Font ButtonFont { get { return Ink.Text(12.5f * Ui); } }
            float MaxScroll { get { return Math.Max(0, contentH - Height); } }

            RectangleF On(RectangleF r) { return new RectangleF(r.X, r.Y - scroll, r.Width, r.Height); }

            // Layout

            void Arrange()
            {
                items.Clear();
                taps.Clear();
                laidW = Width;
                laidH = Height;
                if (Empty) LayoutEmpty();
                else LayoutChat();
                LayoutBanner();
            }

            void LayoutChat()
            {
                float s = Ui, w = Width, pad = 16 * s, lead = pad + 38 * s;
                float maxW = Math.Max(140 * s, Math.Min(w * 0.82f, w - lead - pad - 36 * s));
                float y = 10 * s;
                ChatMsg prev = null;
                foreach (var m in Chat)
                {
                    // A stamp when the day turns or after a quiet half hour
                    if (prev == null || m.Utc.ToLocalTime().Date != prev.Utc.ToLocalTime().Date || (m.Utc - prev.Utc).TotalMinutes > 30)
                    {
                        if (prev != null) y += 8 * s;
                        items.Add(new Item { Stamp = StampText(m.Utc), Box = new RectangleF(0, y, w, 26 * s) });
                        y += 30 * s;
                    }
                    else y += (prev.Mine == m.Mine ? 6 : 14) * s;
                    int at = items.Count;
                    var it = new Item { Msg = m };
                    float top = y;
                    var msg = m;
                    if (m.Text.Length > 0)
                    {
                        float inset = m.Fix != null ? 22 * s : 0;
                        var size = TextRenderer.MeasureText(m.Text, MsgFont, new Size((int)(maxW - 28 * s - inset), int.MaxValue), Wrap);
                        float bw = size.Width + 28 * s + inset, bh = size.Height + 18 * s;
                        it.Bubble = new RectangleF(m.Mine ? w - pad - bw : lead, y, bw, bh);
                        float cy = y + Math.Min(bh, 38 * s) / 2;
                        var copy = new RectangleF(m.Mine ? it.Bubble.X - 34 * s : it.Bubble.Right + 6 * s, cy - 14 * s, 28 * s, 28 * s);
                        taps.Add(new Tap { Id = "copy" + at, Glyph = IcoCopy, Rect = copy, Item = at, Act = delegate { Copy(msg); } });
                        y += bh;
                    }
                    for (int k = 0; k < m.Acts.Count; k++)
                    {
                        var a = m.Acts[k];
                        if (y > top) y += 6 * s;
                        float tw = Math.Min(maxW - 64 * s, TextRenderer.MeasureText(a.Text, ChipFont, Size.Empty, Single).Width);
                        var chip = new RectangleF(0, y, tw + 54 * s, 32 * s);
                        chip.X = m.Mine ? w - pad - chip.Width : lead;
                        it.Chips.Add(chip);
                        if (island.CanUndo(a))
                        {
                            var act = a;
                            string label = Lang.T("Batalkan");
                            float uw = TextRenderer.MeasureText(label, ButtonFont).Width + 22 * s;
                            taps.Add(new Tap { Id = "undo" + at + "." + k, Label = label, Rect = new RectangleF(chip.Right + 6 * s, y + 3 * s, uw, 26 * s), Act = delegate { island.UndoAct(act); } });
                        }
                        y += 32 * s;
                    }
                    if (m.Fix != null)
                    {
                        y += 8 * s;
                        float x = lead;
                        if (m.Fix == "retry") x = AddButton("retry" + at, Lang.T("Coba lagi"), x, y, delegate { island.RetrySuruh(msg); }) + 6 * s;
                        AddButton("ai" + at, Lang.T("Atur AI"), x, y, delegate { island.OpenPrefs("ai"); });
                        y += 28 * s;
                    }
                    if (!m.Mine) it.Face = new RectangleF(pad, top, 28 * s, 28 * s);
                    it.Box = new RectangleF(0, top, w, y - top);
                    items.Add(it);
                    prev = m;
                }
                if (island.suruhBusy)
                {
                    y += 14 * s;
                    items.Add(new Item { Typing = true, Bubble = new RectangleF(lead, y, 62 * s, 38 * s), Face = new RectangleF(pad, y, 28 * s, 28 * s), Box = new RectangleF(0, y, w, 38 * s) });
                    y += 38 * s;
                }
                contentH = y + 18 * s + (island.chatCleared != null ? 60 * s : 0);
            }

            // Pill button; returns its right edge
            float AddButton(string id, string label, float x, float y, Action act)
            {
                float bw = TextRenderer.MeasureText(label, ButtonFont).Width + 24 * Ui;
                taps.Add(new Tap { Id = id, Label = label, Rect = new RectangleF(x, y, bw, 28 * Ui), Act = act });
                return x + bw;
            }

            // Empty: a face, what Tepi can do, ideas to try
            void LayoutEmpty()
            {
                float s = Ui, w = Width, cx = w / 2;
                bool ai = Ai.Ready(Ai.Text);
                float y = Math.Max(20 * s, Height * 0.12f);
                emptyFace = new RectangleF(cx - 30 * s, y, 60 * s, 60 * s);
                y += 76 * s;
                titleRect = new RectangleF(16 * s, y, w - 32 * s, 26 * s);
                y += 32 * s;
                emptyNote = ai ? Lang.T("Tanya apa saja, atau suruh pasang pengingat, timer, dan catatan.") : Lang.T("Tanpa layanan AI, Tepi paham timer, pengingat, catatan, dan hitungan.");
                float nw = Math.Min(w - 48 * s, 340 * s);
                var ns = TextRenderer.MeasureText(emptyNote, ChipFont, new Size((int)nw, int.MaxValue), Wrap | TextFormatFlags.HorizontalCenter);
                noteRect = new RectangleF(cx - nw / 2, y, nw, ns.Height + 2);
                y += ns.Height + 20 * s;
                if (!ai)
                {
                    string label = Lang.T("Sambungkan AI");
                    float bw = TextRenderer.MeasureText(label, ButtonFont).Width + 28 * s;
                    taps.Add(new Tap { Id = "connect", Label = label, Rect = new RectangleF(cx - bw / 2, y, bw, 30 * s), Act = delegate { island.OpenPrefs("ai"); } });
                    y += 48 * s;
                }
                // Ideas wrap into centred rows
                var row = new List<Tap>();
                float rowW = 0, gap = 8 * s, avail = w - 32 * s;
                for (int i = 0; i < Ideas.Length; i++)
                {
                    string label = Lang.T(Ideas[i][0]);
                    float iw = TextRenderer.MeasureText(label, ChipFont, Size.Empty, Single).Width + 44 * s;
                    if (row.Count > 0 && rowW + gap + iw > avail) { y = PlaceRow(row, rowW, y, cx); row.Clear(); rowW = 0; }
                    rowW += (row.Count > 0 ? gap : 0) + iw;
                    var tap = new Tap { Id = "idea" + i, Label = label, Glyph = Ideas[i][1][0], Rect = new RectangleF(0, 0, iw, 34 * s) };
                    tap.Act = delegate { win.Suggest(tap.Label); };
                    row.Add(tap);
                    taps.Add(tap);
                }
                if (row.Count > 0) y = PlaceRow(row, rowW, y, cx);
                contentH = y + 16 * s;
            }

            float PlaceRow(List<Tap> row, float rowW, float y, float cx)
            {
                float x = cx - rowW / 2;
                foreach (var t in row)
                {
                    t.Rect = new RectangleF(x, y, t.Rect.Width, t.Rect.Height);
                    x += t.Rect.Width + 8 * Ui;
                }
                return y + 42 * Ui;
            }

            RectangleF BannerRect() { float s = Ui; return new RectangleF(16 * s, Height - 58 * s, Width - 32 * s, 46 * s); }

            void LayoutBanner()
            {
                if (island.chatCleared == null) return;
                float s = Ui;
                var bar = BannerRect();
                string label = Lang.T("Urungkan");
                float bw = TextRenderer.MeasureText(label, ButtonFont).Width + 24 * s;
                taps.Add(new Tap { Id = "restore", Label = label, Fixed = true, Rect = new RectangleF(bar.Right - 9 * s - bw, bar.Y + (bar.Height - 28 * s) / 2, bw, 28 * s), Act = delegate { undoEnd.Stop(); island.RestoreChat(); } });
            }

            static string StampText(DateTime utc)
            {
                var t = utc.ToLocalTime();
                string day = t.Date == DateTime.Today ? Lang.T("Hari ini") : t.Date == DateTime.Today.AddDays(-1) ? Lang.T("Kemarin")
                    : t.ToString("d MMM", Lang.English ? CultureInfo.InvariantCulture : new CultureInfo("id-ID"));
                return day + " " + Fmt.Hm(t);
            }

            // Updates from the window

            public void Reflow(ChatMsg added)
            {
                bool stick = pinned || island.suruhBusy || (added != null && added.Mine);
                Arrange();
                ScrollTo(stick ? MaxScroll : target, added != null);
                if (focus >= items.Count || (focus >= 0 && items[focus].Msg == null)) focus = -1;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }

            public void Relayout()
            {
                bool end = pinned;
                Arrange();
                ScrollTo(end ? MaxScroll : target, false);
            }

            public void ToEnd() { if (laidW != Width) Arrange(); ScrollTo(MaxScroll, false); }

            public void Clear()
            {
                island.ClearChat();
                focus = -1;
                undoEnd.Stop();
                undoEnd.Start();
                Reflow(null);
            }

            public void Pulse()
            {
                foreach (var it in items)
                {
                    if (!it.Typing) continue;
                    var r = On(RectangleF.Union(it.Face, it.Bubble));
                    r.Inflate(4, 4);
                    Invalidate(Rectangle.Round(r));
                }
            }

            protected override void OnResize(EventArgs e)
            {
                bool end = pinned;
                base.OnResize(e);
                if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
                Arrange();
                ScrollTo(end ? MaxScroll : target, false);
            }

            void ScrollTo(float v, bool smooth)
            {
                target = Math.Max(0, Math.Min(MaxScroll, v));
                pinned = target >= MaxScroll - 4 * Ui;
                if (!smooth || Anim.Reduced || !Visible) { scroll = target; glide.Stop(); Invalidate(); return; }
                if (!glide.Enabled) glide.Start();
            }

            void Reveal(RectangleF box)
            {
                float s = Ui;
                if (box.Top < target + 8 * s) ScrollTo(box.Top - 8 * s, true);
                else if (box.Bottom > target + Height - 8 * s) ScrollTo(box.Bottom - Height + 8 * s, true);
            }

            // Painting

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                KitInk.Smooth(g);
                g.Clear(KitInk.Window);
                if (laidW != Width || laidH != Height) Arrange();
                if (Empty) DrawEmpty(g);
                else
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        if (it.Box.Bottom - scroll < -40 * Ui || it.Box.Top - scroll > Height + 40 * Ui) continue;
                        DrawItem(g, it, i);
                    }
                foreach (var t in taps) if (!t.Fixed && Shown(t)) DrawTap(g, t);
                DrawThumb(g);
                DrawBanner(g);
            }

            void DrawEmpty(Graphics g)
            {
                float s = Ui;
                DrawFace(g, On(emptyFace), 0);
                KitInk.Draw(g, Lang.T("Mau Tepi bantu apa?"), Ink.Semi(17f * s), KitInk.Text, On(titleRect), ContentAlignment.MiddleCenter);
                TextRenderer.DrawText(g, emptyNote, ChipFont, Rectangle.Round(On(noteRect)), KitInk.Sub, Wrap | TextFormatFlags.HorizontalCenter);
            }

            void DrawItem(Graphics g, Item it, int i)
            {
                float s = Ui;
                if (it.Stamp != null) { KitInk.Draw(g, it.Stamp, Ink.Small(11.5f * s), KitInk.Sub, On(it.Box), ContentAlignment.MiddleCenter); return; }
                if (!it.Face.IsEmpty) DrawFace(g, On(it.Face), it.Typing && !Anim.Reduced ? (float)Math.Sin(win.Phase * 0.45f) : 0);
                if (it.Typing) { DrawTyping(g, On(it.Bubble)); return; }
                var m = it.Msg;
                if (!it.Bubble.IsEmpty)
                {
                    var b = On(it.Bubble);
                    float r = Math.Min(17 * s, b.Height / 2), tail = 6 * s;
                    // The corner nearest the speaker stays sharp
                    using (var path = m.Mine ? Corners(b, r, r, tail, r) : Corners(b, tail, r, r, r))
                    using (var br = new SolidBrush(m.Mine ? KitInk.Accent : TepiFill)) g.FillPath(br, path);
                    float inset = m.Fix != null ? 22 * s : 0;
                    if (m.Fix != null) KitInk.Draw(g, Ink.Str(IcoFail), Ink.Icons(13f * s), KitInk.Sub, new RectangleF(b.X + 11 * s, b.Y, 20 * s, Math.Min(b.Height, 38 * s)), ContentAlignment.MiddleCenter);
                    var tr = new RectangleF(b.X + 14 * s + inset, b.Y + 9 * s, b.Width - 28 * s - inset + 2, b.Height - 18 * s + 2);
                    TextRenderer.DrawText(g, m.Text, MsgFont, Rectangle.Round(tr), m.Mine ? KitInk.OnAccent : KitInk.Text, Wrap);
                }
                for (int k = 0; k < it.Chips.Count && k < m.Acts.Count; k++) DrawChip(g, On(it.Chips[k]), m.Acts[k]);
                if (i == focus && Focused && ShowFocusCues && !KitInk.ByMouse)
                {
                    var first = On(!it.Bubble.IsEmpty ? it.Bubble : it.Chips.Count > 0 ? it.Chips[0] : it.Box);
                    first.Inflate(3 * s, 3 * s);
                    using (var path = Ink.Round(first, Math.Min(20 * s, first.Height / 2)))
                    using (var pen = new Pen(KitInk.Text, 2f)) g.DrawPath(pen, path);
                }
            }

            void DrawChip(Graphics g, RectangleF c, ChatAct a)
            {
                float s = Ui;
                using (var path = Ink.Round(c, c.Height / 2))
                using (var pen = new Pen(KitInk.Line, Math.Max(1f, 1.2f * s))) g.DrawPath(pen, path);
                Color ink = a.Undone ? KitInk.Sub : KitInk.Text;
                KitInk.Draw(g, Ink.Str(a.Glyph), Ink.Icons(14f * s), ink, new RectangleF(c.X + 10 * s, c.Y, 22 * s, c.Height), ContentAlignment.MiddleCenter);
                var tr = new RectangleF(c.X + 38 * s, c.Y, c.Width - 48 * s, c.Height);
                KitInk.Draw(g, a.Text, ChipFont, ink, tr, ContentAlignment.MiddleLeft);
                // Cancelled: struck through, not only greyed
                if (!a.Undone) return;
                float tw = Math.Min(tr.Width, TextRenderer.MeasureText(a.Text, ChipFont, Size.Empty, Single).Width);
                using (var pen = new Pen(ink, Math.Max(1f, s))) g.DrawLine(pen, tr.X + 3 * s, c.Y + c.Height / 2, tr.X + tw - 3 * s, c.Y + c.Height / 2);
            }

            void DrawTyping(Graphics g, RectangleF b)
            {
                float s = Ui, r = Math.Min(17 * s, b.Height / 2);
                var fill = TepiFill;
                using (var path = Corners(b, 6 * s, r, r, r)) using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                for (int k = 0; k < 3; k++)
                {
                    float a = Anim.Reduced ? 0.6f : 0.3f + 0.7f * Math.Max(0, (float)Math.Sin(win.Phase - k * 0.8f));
                    float cx = b.X + b.Width / 2 + (k - 1) * 11 * s, cy = b.Y + b.Height / 2;
                    using (var br = new SolidBrush(Ink.Mix(fill, KitInk.Text, a))) g.FillEllipse(br, cx - 3.5f * s, cy - 3.5f * s, 7 * s, 7 * s);
                }
            }

            // Copy shows over its message, or its keyboard focus
            bool Shown(Tap t)
            {
                if (t.Item < 0) return true;
                return t.Item == hoverItem || (t.Item == focus && Focused) || (copied != null && t.Item < items.Count && items[t.Item].Msg == copied);
            }

            void DrawTap(Graphics g, Tap t)
            {
                float s = Ui;
                var r = t.Fixed ? t.Rect : On(t.Rect);
                bool over = hot == t.Id, down = over && pressed == t.Id;
                if (t.Item >= 0)
                {
                    bool done = copied != null && t.Item < items.Count && items[t.Item].Msg == copied;
                    if (over) using (var b = new SolidBrush(down ? KitInk.Line : KitInk.Hover)) g.FillEllipse(b, r);
                    KitInk.Draw(g, Ink.Str(done ? IcoDone : t.Glyph), Ink.Icons(13f * s), over || done ? KitInk.Text : KitInk.Sub, r, ContentAlignment.MiddleCenter);
                    return;
                }
                Color fill = down ? KitInk.Line : over ? KitInk.Hover : t.Glyph != '\0' ? KitInk.Window : KitInk.Group;
                using (var path = Ink.Round(r, r.Height / 2))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var pen = new Pen(KitInk.Line, Math.Max(1f, 1.2f * s))) g.DrawPath(pen, path);
                }
                if (t.Glyph == '\0') { KitInk.Draw(g, t.Label, ButtonFont, KitInk.Text, r, ContentAlignment.MiddleCenter); return; }
                KitInk.Draw(g, Ink.Str(t.Glyph), Ink.Icons(13f * s), KitInk.Sub, new RectangleF(r.X + 10 * s, r.Y, 20 * s, r.Height), ContentAlignment.MiddleCenter);
                KitInk.Draw(g, t.Label, ChipFont, KitInk.Text, new RectangleF(r.X + 32 * s, r.Y, r.Width - 38 * s, r.Height), ContentAlignment.MiddleLeft);
            }

            RectangleF Thumb()
            {
                float s = Ui;
                if (contentH <= Height + 1) return RectangleF.Empty;
                float h = Math.Max(30 * s, Height * Height / contentH);
                float y = (Height - h) * scroll / Math.Max(1, contentH - Height);
                float wide = hot == "thumb" || grab >= 0 ? 7 * s : 4 * s;
                return new RectangleF(Width - wide - 3 * s, y, wide, h);
            }

            void DrawThumb(Graphics g)
            {
                var t = Thumb();
                if (t.IsEmpty) return;
                using (var path = Ink.Round(t, t.Width / 2))
                using (var b = new SolidBrush(hot == "thumb" || grab >= 0 ? KitInk.Sub : KitInk.Faint)) g.FillPath(b, path);
            }

            void DrawBanner(Graphics g)
            {
                if (island.chatCleared == null) return;
                float s = Ui;
                var bar = BannerRect();
                using (var path = Ink.Round(bar, 14 * s))
                {
                    using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                    using (var pen = new Pen(KitInk.Line, 1f)) g.DrawPath(pen, path);
                }
                KitInk.Draw(g, Lang.T("Obrolan lama dihapus"), ChipFont, KitInk.Text, new RectangleF(bar.X + 16 * s, bar.Y, bar.Width - 140 * s, bar.Height), ContentAlignment.MiddleLeft);
                foreach (var t in taps) if (t.Fixed) DrawTap(g, t);
            }

            // Pointer

            Tap TapAt(Point pt)
            {
                bool underBanner = island.chatCleared != null && BannerRect().Contains(pt);
                for (int i = taps.Count - 1; i >= 0; i--)
                {
                    var t = taps[i];
                    if (!(t.Fixed ? t.Rect : On(t.Rect)).Contains(pt)) continue;
                    if (t.Fixed) return t;
                    if (underBanner || !Shown(t) && t.Item != ItemAt(pt)) continue;
                    return t;
                }
                return null;
            }

            int ItemAt(Point pt)
            {
                for (int i = 0; i < items.Count; i++) if (items[i].Msg != null && On(items[i].Box).Contains(pt)) return i;
                return -1;
            }

            bool InStrip(Point pt) { return pt.X >= Width - 14 * Ui && !Thumb().IsEmpty; }

            void Track(Point pt)
            {
                var t = TapAt(pt);
                string id = t != null ? t.Id : InStrip(pt) ? "thumb" : null;
                int item = ItemAt(pt);
                if (id == hot && item == hoverItem) return;
                hot = id;
                hoverItem = item;
                Cursor = t != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (grab >= 0) { DragTo(e.Y); return; }
                Track(e.Location);
            }

            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = null; hoverItem = -1; Invalidate(); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                if (InStrip(e.Location))
                {
                    var th = Thumb();
                    // Off the thumb: centre it there first
                    if (!th.Contains(new PointF(th.X + th.Width / 2, e.Y))) { grab = th.Height / 2; DragTo(e.Y); }
                    grab = e.Y - Thumb().Y;
                    Capture = true;
                    Invalidate();
                    return;
                }
                var t = TapAt(e.Location);
                pressed = t != null ? t.Id : null;
                Invalidate();
            }

            void DragTo(int y)
            {
                var th = Thumb();
                float room = Height - th.Height;
                if (room > 0) ScrollTo((y - grab) / room * MaxScroll, false);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (grab >= 0) { grab = -1; Capture = false; Invalidate(); return; }
                if (e.Button == MouseButtons.Right)
                {
                    int item = ItemAt(e.Location);
                    if (item >= 0) { focus = item; Invalidate(); Menu(items[item].Msg, e.Location); }
                    return;
                }
                if (e.Button != MouseButtons.Left) return;
                var t = TapAt(e.Location);
                bool hit = t != null && t.Id == pressed;
                pressed = null;
                // Typing carries on in the box
                win.FocusInput();
                if (hit) t.Act();
                Invalidate();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                ScrollTo(target - e.Delta / 120f * 64 * Ui, true);
            }

            // Keyboard: arrows walk messages, menu holds the rest

            protected override bool IsInputKey(Keys k)
            {
                switch (k & Keys.KeyCode)
                {
                    case Keys.Up:
                    case Keys.Down:
                    case Keys.Home:
                    case Keys.End:
                    case Keys.PageUp:
                    case Keys.PageDown:
                    case Keys.Enter:
                        return true;
                }
                return base.IsInputKey(k);
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                if (focus < 0) focus = Next(items.Count, -1);
                // A click focuses too; only Tab jumps to a message
                if (focus >= 0 && !KitInk.ByMouse && Control.MouseButtons == MouseButtons.None) FocusItem(focus);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                var msg = focus >= 0 && focus < items.Count ? items[focus].Msg : null;
                switch (e.KeyCode)
                {
                    case Keys.Up: FocusItem(Next(focus < 0 ? items.Count : focus, -1)); break;
                    case Keys.Down: FocusItem(Next(focus, 1)); break;
                    case Keys.Home: FocusItem(Next(-1, 1)); break;
                    case Keys.End: FocusItem(Next(items.Count, -1)); break;
                    case Keys.PageUp: ScrollTo(target - Height * 0.85f, true); break;
                    case Keys.PageDown: ScrollTo(target + Height * 0.85f, true); break;
                    case Keys.Enter:
                    case Keys.Apps:
                        if (msg == null) return;
                        Menu(msg, MenuSpot());
                        break;
                    case Keys.F10:
                        if (!e.Shift || msg == null) return;
                        Menu(msg, MenuSpot());
                        break;
                    case Keys.C:
                        if (!e.Control || msg == null) return;
                        Copy(msg);
                        break;
                    default: return;
                }
                e.Handled = true;
            }

            int Next(int from, int dir)
            {
                for (int i = from + dir; i >= 0 && i < items.Count; i += dir) if (items[i].Msg != null) return i;
                return -1;
            }

            void FocusItem(int i)
            {
                if (i < 0) return;
                focus = i;
                Reveal(items[i].Box);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Focus, Child(i));
                AccessibilityNotifyClients(AccessibleEvents.Selection, Child(i));
            }

            Point MenuSpot()
            {
                if (focus < 0) return new Point(Width / 2, Height / 2);
                var it = items[focus];
                var r = On(!it.Bubble.IsEmpty ? it.Bubble : it.Box);
                return new Point((int)(r.X + 12 * Ui), (int)Math.Max(0, Math.Min(Height - 4, r.Bottom)));
            }

            void Menu(ChatMsg m, Point at)
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add(Lang.T("Salin"), null, delegate { Copy(m); });
                if (m.Mine) menu.Items.Add(Lang.T("Pakai lagi"), null, delegate { win.Suggest(m.Text); });
                if (m.Fix == "retry") menu.Items.Add(Lang.T("Coba lagi"), null, delegate { island.RetrySuruh(m); });
                foreach (var a in m.Acts)
                {
                    if (!island.CanUndo(a)) continue;
                    var act = a;
                    menu.Items.Add(Lang.T("Batalkan pengingat"), null, delegate { island.UndoAct(act); });
                }
                MenuLook.Apply(menu);
                menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
                menu.Show(this, at);
            }

            void Copy(ChatMsg m)
            {
                var sb = new StringBuilder(m.Text);
                foreach (var a in m.Acts) sb.Append(sb.Length > 0 ? Environment.NewLine : "").Append(a.Text);
                if (sb.Length == 0) return;
                try { Clipboard.SetText(sb.ToString()); }
                catch (Exception) { return; }
                copied = m;
                tick.Stop();
                tick.Start();
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { glide.Dispose(); tick.Dispose(); undoEnd.Dispose(); }
                base.Dispose(disposing);
            }

            // Readers: one list item per message

            static string Spoken(ChatMsg m)
            {
                var parts = new List<string>();
                if (m.Text.Length > 0) parts.Add(m.Text);
                foreach (var a in m.Acts) parts.Add(a.Undone ? a.Text + " (" + Lang.T("dibatalkan") + ")" : a.Text);
                return (m.Mine ? Lang.T("Kamu") : "Tepi") + ": " + string.Join(". ", parts.ToArray());
            }

            int Child(int item)
            {
                int n = -1;
                for (int i = 0; i <= item && i < items.Count; i++) if (items[i].Msg != null) n++;
                return n;
            }

            int ItemOfChild(int child)
            {
                int n = -1;
                for (int i = 0; i < items.Count; i++) if (items[i].Msg != null && ++n == child) return i;
                return -1;
            }

            protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

            class Access : ControlAccessibleObject
            {
                readonly ChatList owner;
                public Access(ChatList owner) : base(owner) { this.owner = owner; }
                public override AccessibleRole Role { get { return AccessibleRole.List; } }
                public override int GetChildCount() { return owner.Child(owner.items.Count - 1) + 1; }
                public override AccessibleObject GetChild(int i) { int it = owner.ItemOfChild(i); return it < 0 ? null : new Entry(owner, it); }
                public override AccessibleObject GetFocused() { return owner.Focused && owner.focus >= 0 ? new Entry(owner, owner.focus) : null; }
                public override AccessibleObject GetSelected() { return GetFocused(); }
            }

            class Entry : AccessibleObject
            {
                readonly ChatList owner;
                readonly int item;
                public Entry(ChatList owner, int item) { this.owner = owner; this.item = item; }
                ChatMsg Msg { get { return item < owner.items.Count ? owner.items[item].Msg : null; } }
                public override string Name { get { var m = Msg; return m == null ? "" : Spoken(m); } }
                public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
                public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
                public override AccessibleStates State
                {
                    get { return AccessibleStates.Focusable | AccessibleStates.Selectable | (owner.Focused && owner.focus == item ? AccessibleStates.Focused | AccessibleStates.Selected : 0); }
                }
                public override Rectangle Bounds
                {
                    get { return item < owner.items.Count ? owner.RectangleToScreen(Rectangle.Round(owner.On(owner.items[item].Box))) : Rectangle.Empty; }
                }
                public override string DefaultAction { get { return Lang.T("Salin"); } }
                public override void DoDefaultAction() { var m = Msg; if (m != null) owner.Copy(m); }
            }
        }
    }
}
