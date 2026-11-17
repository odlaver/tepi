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
            if (expanded && !pointerOver) expanded = false;
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
                    // A toast goes first; a second Esc leaves
                    if (!expanded && toast != null && Swipeable(current)) { DismissToast(); return true; }
                    ExitKeyboard();
                    return true;
                case Keys.Z:
                    if ((keyData & Keys.Control) != 0 && pending != null) { UndoPending(); return true; }
                    break;
                case Keys.Delete:
                case Keys.Back:
                    if (!expanded && toast != null && Swipeable(current)) { Fling(1); return true; }
                    break;
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
            var hit = hits.Find(h => h.Id == id);
            if (hit != null) RunHit(hit);
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
                case "undo": return Lang.T("Urungkan");
                case "allow": return Lang.T("Izinkan");
                case "deny": return Lang.T("Tolak");
                case "always": return Lang.T("Selalu");
                case "term": return Lang.T("Terminal");
                case "toMini": return Lang.T("Mini");
                case "prefsOpen": return Lang.T("Pengaturan");
                case "voiceFolder": return Lang.T("Folder rekaman");
                case "clearVoice": return Lang.T("Kosongkan");
                case "prayCity": return Lang.T("Pilih kota");
                case "praySwitch": return Lang.T("Pengingat sholat");
                case "drinkPlus": return Lang.T("Minum air");
                case "prev": return Lang.T("Lagu sebelumnya");
                case "next": return Lang.T("Lagu berikutnya");
                case "play": return Lang.T(mediaInfo.Playing ? "Jeda" : "Putar");
                case "shuffle": return Lang.T(mediaInfo.Shuffle ? "Acak nyala" : "Acak mati");
                case "repeat": return Lang.T(mediaInfo.Repeat == 1 ? "Ulang satu lagu" : mediaInfo.Repeat == 2 ? "Ulang semua" : "Ulang mati");
            }
            if (id.StartsWith("send", StringComparison.Ordinal)) return Lang.T("Chat");
            if (id.StartsWith("chip", StringComparison.Ordinal)) return id.Length > 4 && id[4] != '*' ? id.Substring(4) : Lang.T("Semua");
            return "";
        }

        void DrawFocusRing()
        {
            if (!keyboardMode || focusId == null) { ringId = null; return; }
            foreach (var h in hits)
            {
                if (h.Id != focusId) continue;
                var r = h.Rect;
                r.Inflate(2, 2);
                // Ring glides from the last target
                bool snap = ringId == null || Anim.Reduced;
                ringId = focusId;
                ring.X.Target = r.X; ring.Y.Target = r.Y; ring.W.Target = r.Width; ring.H.Target = r.Height;
                if (snap) { ring.X.Snap(r.X); ring.Y.Snap(r.Y); ring.W.Snap(r.Width); ring.H.Snap(r.Height); }
                var at = new System.Drawing.RectangleF(ring.X.Value, ring.Y.Value, ring.W.Value, ring.H.Value);
                p.StrokeRound(at, Math.Min(12, at.Height / 2), p.T.Accent, 2f);
                return;
            }
        }

        readonly CardGlide ring = MakeRing();
        string ringId;

        static CardGlide MakeRing()
        {
            var m = new CardGlide();
            foreach (var s in new[] { m.X, m.Y, m.W, m.H }) Springs.Tune(s, 0.18f, 1f);
            return m;
        }

        void StepRing(float dt) { ring.X.Step(dt); ring.Y.Step(dt); ring.W.Step(dt); ring.H.Step(dt); }

        bool RingMoving() { return ringId != null && !ring.Settled; }
    }
}
