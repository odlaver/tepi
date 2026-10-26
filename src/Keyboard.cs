using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Keyboard control: Ctrl+Alt+I focuses the island
    partial class Island
    {
        const int KeysHotkey = 3;
        bool keyboardMode;
        string focusId;
        IntPtr keyboardReturn;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        void EnterKeyboard()
        {
            if (keyboardMode) { ExitKeyboard(); return; }
            if (hiddenByUser) ToggleHidden();
            keyboardReturn = GetForegroundWindow();
            keyboardMode = true;
            focusId = null;
            if (!expanded && approvals.Count == 0 && !PrompterOn) Expand(DefaultPage(false));
            // The hotkey grants foreground rights, so this succeeds
            SetForegroundWindow(Handle);
            Kick();
            Announce(Lang.T("Navigasi keyboard. Tab untuk pindah, Enter untuk memilih, Esc untuk keluar."));
        }

        void ExitKeyboard()
        {
            if (!keyboardMode) return;
            keyboardMode = false;
            focusId = null;
            if (expanded && !pointerInside) expanded = false;
            if (keyboardReturn != IntPtr.Zero) SetForegroundWindow(keyboardReturn);
            keyboardReturn = IntPtr.Zero;
            Kick();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Focus moved elsewhere: leave without stealing it back
            if (keyboardMode) { keyboardReturn = IntPtr.Zero; ExitKeyboard(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!keyboardMode) return base.ProcessCmdKey(ref msg, keyData);
            Keys key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0;
            switch (key)
            {
                case Keys.Escape:
                    ExitKeyboard();
                    return true;
                case Keys.Tab:
                    MoveFocus(shift ? -1 : 1);
                    return true;
                case Keys.Left:
                case Keys.Right:
                    if (current == View.Panel) StepTab(key == Keys.Left ? -1 : 1);
                    else MoveFocus(key == Keys.Left ? -1 : 1);
                    return true;
                case Keys.Up:
                case Keys.Down:
                    int delta = key == Keys.Up ? 120 : -120;
                    if (current == View.Approve) ScrollApproval(delta);
                    else if (current == View.Prompter) ScrollPrompter(delta);
                    else if (expanded) ScrollPanel(delta);
                    return true;
                case Keys.Enter:
                case Keys.Space:
                    if (focusId == null && current == View.Prompter) TogglePrompter();
                    else Press(focusId);
                    return true;
            }
            if (key >= Keys.D1 && key <= Keys.D9 && current == View.Panel)
            {
                int i = key - Keys.D1;
                if (i < tabs.Length) { SetPage(tabs[i], false); focusId = null; Announce(Lang.T(PageNames[tabs[i]])); Kick(); }
            }
            return true;
        }

        void MoveFocus(int step)
        {
            if (hits.Count == 0) return;
            int at = hits.FindIndex(h => h.Id == focusId);
            at = at < 0 ? (step > 0 ? 0 : hits.Count - 1) : (at + step + hits.Count) % hits.Count;
            focusId = hits[at].Id;
            Announce(Label(focusId));
            Kick();
        }

        void StepTab(int step)
        {
            int at = Array.IndexOf(tabs, page);
            int next = (Math.Max(0, at) + step + tabs.Length) % tabs.Length;
            SetPage(tabs[next], false);
            focusId = null;
            Announce(Lang.T(PageNames[tabs[next]]));
            Kick();
        }

        void Press(string id)
        {
            if (id == null) return;
            foreach (var h in hits)
            {
                if (h.Id != id) continue;
                try { h.Act(); } catch (Exception ex) { Log.Write("action " + id + ": " + ex.Message); }
                break;
            }
            Kick();
        }

        // Spoken names for the common buttons
        string Label(string id)
        {
            if (id == null) return "";
            if (id.StartsWith("tab", StringComparison.Ordinal))
            {
                int pg;
                if (int.TryParse(id.Substring(3), out pg) && pg >= 0 && pg < PageNames.Length) return Lang.T(PageNames[pg]);
            }
            switch (id)
            {
                case "allow": return Lang.T("Izinkan");
                case "deny": return Lang.T("Tolak");
                case "always": return Lang.T("Selalu");
                case "term": return Lang.T("Terminal");
                case "hideIsland": return Lang.T("Sembunyikan island");
            }
            if (id.StartsWith("send", StringComparison.Ordinal)) return Lang.T("Kirim");
            return "";
        }

        void DrawFocusRing()
        {
            if (!keyboardMode || focusId == null) return;
            foreach (var h in hits)
            {
                if (h.Id != focusId) continue;
                var r = h.Rect;
                r.Inflate(2, 2);
                p.StrokeRound(r, Math.Min(12, r.Height / 2), p.T.Accent, 2f);
                return;
            }
        }
    }
}
