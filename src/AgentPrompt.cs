using System;
using System.Drawing;
using System.Runtime.InteropServices;
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

        void AskAgent(AgentSession s)
        {
            expanded = false;
            Kick();
            string text;
            using (var f = new AgentPromptBox(s.Project, SharesWindow(s)))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                text = f.Prompt;
            }
            if (!SendToAgent(s, text)) ShowInfo(Ink.IcoTerminal, Lang.T("Terminal tidak bisa dibuka"), s.Project, true);
        }

        bool SendToAgent(AgentSession s, string text)
        {
            text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length == 0) return true;
            if (text.Length > PromptMax) text = text.Substring(0, PromptMax);
            if (!TerminalFinder.Focus(s.Window)) return false;
            // Type only once the terminal is in front
            IntPtr want = GetAncestor(s.Window, 3);
            bool front = false;
            for (int i = 0; i < 20 && !front; i++)
            {
                Thread.Sleep(40);
                front = GetAncestor(GetForegroundWindow(), 3) == want;
            }
            if (!front) return false;
            for (int i = 0; i < 25 && (GetAsyncKeyState(0x0D) & 0x8000) != 0; i++) Thread.Sleep(20);

            var inputs = new INPUT[text.Length * 2 + 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = Key(0, text[i], 4);
                inputs[i * 2 + 1] = Key(0, text[i], 4 | 2);
            }
            inputs[text.Length * 2] = Key(0x0D, '\0', 0);
            inputs[text.Length * 2 + 1] = Key(0x0D, '\0', 2);
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length) Log.Write("send prompt: " + sent + "/" + inputs.Length + " err " + Marshal.GetLastWin32Error());
            return sent == inputs.Length;
        }

        static INPUT Key(ushort vk, char ch, uint flags)
        {
            return new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = ch, dwFlags = flags } };
        }
    }

    // One line to an agent
    class AgentPromptBox : Form
    {
        readonly TextBox field = new TextBox();
        public string Prompt { get { return field.Text; } }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public AgentPromptBox(string project, bool sharedWindow)
        {
            Text = Lang.T("Kirim ke agent");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, sharedWindow ? 196 : 164);
            ShowInTaskbar = true;
            TopMost = true;
            var bg = Ink.Light ? Color.FromArgb(245, 245, 245) : Color.FromArgb(10, 10, 10);
            var fg = Ink.Light ? Color.FromArgb(10, 10, 10) : Color.FromArgb(244, 244, 244);
            var sub = Ink.Light ? Color.FromArgb(88, 88, 88) : Color.FromArgb(160, 160, 160);
            BackColor = bg;
            ForeColor = fg;
            Font = Ink.Text(14f);

            var heading = new Label { Text = project, Font = Ink.Display(18f), AutoSize = false };
            heading.SetBounds(24, 18, 432, 30);
            field.SetBounds(24, 58, 432, 30);
            field.BorderStyle = BorderStyle.FixedSingle;
            field.BackColor = Ink.Light ? Color.White : Color.FromArgb(24, 24, 24);
            field.ForeColor = fg;
            field.Font = Ink.Text(15f);
            field.MaxLength = 4000;
            int y = 102;
            var controls = new System.Collections.Generic.List<Control> { heading, field };
            if (sharedWindow)
            {
                var warn = new Label { Text = Lang.T("Jendela ini punya beberapa sesi. Teks masuk ke tab yang sedang aktif."), ForeColor = sub, AutoSize = false };
                warn.SetBounds(24, y, 432, 40);
                controls.Add(warn);
                y += 36;
            }
            var send = new Button { Text = Lang.T("Kirim"), FlatStyle = FlatStyle.Flat, BackColor = fg, ForeColor = bg, Font = Ink.Semi(14f), Cursor = Cursors.Hand };
            send.FlatAppearance.BorderSize = 0;
            send.SetBounds(316, y + 8, 140, 34);
            send.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(field.Text)) { field.Focus(); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            controls.Add(send);
            AcceptButton = send;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Controls.AddRange(controls.ToArray());
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            field.Focus();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = Ink.Light ? 0 : 1;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch (Exception) { }
        }
    }
}
