using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Global shortcuts: one table, remappable
    partial class Island
    {
        public class HotkeyDef
        {
            public int Id;
            public string Key, Name, Fallback;
            // Teleprompter keys live only while it runs
            public bool Prompter;
        }

        public static readonly HotkeyDef[] Hotkeys =
        {
            Def(HideHotkey, "key.hide", "Sembunyikan island", "Ctrl+Alt+H", false),
            Def(KeysHotkey, "key.nav", "Navigasi keyboard", "Ctrl+Alt+I", false),
            Def(MiniHotkey, "key.mini", "Mini", "Ctrl+Alt+K", false),
            Def(MicHotkey, "key.mic", "Mikrofon", "Ctrl+Alt+M", false),
            Def(NoteHotkey, "key.note", "Catatan kilat", "Ctrl+Alt+N", false),
            Def(MirrorHotkey, "key.mirror", "Cermin kamera", "Ctrl+Alt+C", false),
            // No default: free combos vary per PC
            Def(PickerHotkey, "key.picker", "Ambil warna", "", false),
            Def(ScreenTextHotkey, "key.screentext", "Salin teks dari layar", "", false),
            Def(DictPasteHotkey, "key.dictate.paste", "Tempel dikte terakhir", "", false),
            Def(RapatHotkey, "key.rapat", "Rekam rapat", "Ctrl+Alt+T", false),
            Def(SuruhHotkey, "key.suruh", "Suruh Tepi", "", false),
            Def(FixHotkey, "key.fix", "Perbaiki teks terpilih", "", false),
            Def(TranslateHotkey, "key.translate", "Terjemahkan teks terpilih", "", false),
            Def(PrompterHotkey, "key.prompter.pause", "Jeda dan lanjut", "Ctrl+Alt+Space", true),
            Def(PrompterRestart, "key.prompter.restart", "Ulang dari awal", "Ctrl+Alt+R", true),
            Def(PrompterBack, "key.prompter.back", "Mundur 5 detik", "Ctrl+Alt+Left", true),
            Def(PrompterAhead, "key.prompter.ahead", "Maju 5 detik", "Ctrl+Alt+Right", true),
            Def(PrompterFaster, "key.prompter.faster", "Lebih cepat", "Ctrl+Alt+=", true),
            Def(PrompterSlower, "key.prompter.slower", "Lebih lambat", "Ctrl+Alt+-", true),
            Def(PrompterSetupKey, "key.prompter.setup", "Buka jendela teleprompter", "Ctrl+Alt+P", true),
            Def(PrompterStop, "key.prompter.stop", "Hentikan", "Ctrl+Alt+X", true),
        };

        const uint ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;
        // Stored for a cleared shortcut
        const string HotkeyOff = "none";

        readonly HashSet<int> hotkeysOn = new HashSet<int>(), hotkeysTaken = new HashSet<int>();
        bool hotkeysHeld;

        static HotkeyDef Def(int id, string key, string name, string fallback, bool prompter)
        {
            return new HotkeyDef { Id = id, Key = key, Name = name, Fallback = fallback, Prompter = prompter };
        }

        static HotkeyDef HotkeyById(int id)
        {
            foreach (var d in Hotkeys) if (d.Id == id) return d;
            return null;
        }

        static readonly string[][] KeyNames =
        {
            new[] { "Space", "Space" }, new[] { "Left", "Left" }, new[] { "Right", "Right" }, new[] { "Up", "Up" }, new[] { "Down", "Down" },
            new[] { "Home", "Home" }, new[] { "End", "End" }, new[] { "PageUp", "PageUp" }, new[] { "PageDown", "Next" }, new[] { "Insert", "Insert" },
            new[] { "Delete", "Delete" }, new[] { "=", "Oemplus" }, new[] { "-", "OemMinus" }, new[] { ",", "Oemcomma" }, new[] { ".", "OemPeriod" },
            new[] { "/", "OemQuestion" }, new[] { ";", "OemSemicolon" }, new[] { "'", "OemQuotes" }, new[] { "[", "OemOpenBrackets" },
            new[] { "]", "OemCloseBrackets" }, new[] { "\\", "OemPipe" }, new[] { "`", "Oemtilde" },
        };

        // "Ctrl+Alt+H"; the key is the last part
        public static bool ParseCombo(string text, out uint mods, out Keys key)
        {
            mods = 0;
            key = Keys.None;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string t = parts[i].Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") mods |= ModCtrl;
                else if (t == "alt") mods |= ModAlt;
                else if (t == "shift") mods |= ModShift;
                else if (t == "win") mods |= ModWin;
                else return false;
            }
            string keyPart = parts[parts.Length - 1].Trim();
            foreach (var n in KeyNames)
                if (string.Equals(n[0], keyPart, StringComparison.OrdinalIgnoreCase)) { key = (Keys)Enum.Parse(typeof(Keys), n[1]); break; }
            if (key == Keys.None && keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]) && keyPart[0] < 128) key = (Keys)char.ToUpperInvariant(keyPart[0]);
            Keys named;
            if (key == Keys.None && keyPart.Length > 1 && !char.IsDigit(keyPart[0]) && Enum.TryParse(keyPart, true, out named)) key = named;
            // Modifier keys alone are not a key
            if ((key & Keys.Modifiers) != 0 || key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey || key == Keys.LWin || key == Keys.RWin) return false;
            // Bare keys would eat typing
            return key != Keys.None && (mods & (ModCtrl | ModAlt | ModWin)) != 0;
        }

        public static string ComboText(uint mods, Keys key)
        {
            var parts = new List<string>();
            if ((mods & ModWin) != 0) parts.Add("Win");
            if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
            if ((mods & ModAlt) != 0) parts.Add("Alt");
            if ((mods & ModShift) != 0) parts.Add("Shift");
            string name = null;
            foreach (var n in KeyNames) if ((Keys)Enum.Parse(typeof(Keys), n[1]) == key) { name = n[0]; break; }
            // Letter and digit keys equal their ASCII
            if (name == null) name = (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ? ((char)key).ToString() : key.ToString();
            parts.Add(name);
            return string.Join("+", parts.ToArray());
        }

        // Same shortcut in any spelling
        static string Canonical(string combo)
        {
            uint m;
            Keys k;
            return ParseCombo(combo, out m, out k) ? ComboText(m, k) : "";
        }

        // Current shortcut, empty when cleared
        public string HotkeyText(int id)
        {
            var d = HotkeyById(id);
            if (d == null) return "";
            string v = settings.Get(d.Key, d.Fallback).Trim();
            return v == HotkeyOff ? "" : Canonical(v);
        }

        public bool HotkeyTaken(int id) { return hotkeysTaken.Contains(id); }

        bool RegisterOne(HotkeyDef d)
        {
            if (hotkeysHeld || hotkeysOn.Contains(d.Id)) return true;
            uint mods;
            Keys key;
            hotkeysTaken.Remove(d.Id);
            if (!ParseCombo(HotkeyText(d.Id), out mods, out key)) return true;
            if (RegisterHotKey(Handle, d.Id, mods | ModNoRepeat, (uint)key)) { hotkeysOn.Add(d.Id); return true; }
            hotkeysTaken.Add(d.Id);
            Log.Write("hotkey " + HotkeyText(d.Id) + " taken");
            return false;
        }

        void UnregisterOne(HotkeyDef d)
        {
            if (hotkeysOn.Remove(d.Id)) UnregisterHotKey(Handle, d.Id);
        }

        void StartHotkeys()
        {
            foreach (var d in Hotkeys) if (!d.Prompter) RegisterOne(d);
            if (hotkeysTaken.Count == 0) return;
            // After the greeting settles
            var later = new Timer { Interval = 4000 };
            later.Tick += delegate { later.Stop(); later.Dispose(); WarnTakenHotkeys(); };
            later.Start();
        }

        void WarnTakenHotkeys()
        {
            var names = new List<string>();
            foreach (var d in Hotkeys) if (hotkeysTaken.Contains(d.Id)) names.Add(HotkeyText(d.Id));
            if (names.Count == 0) return;
            var t = ShowToast(View.Info, 6, 2, false, true);
            if (t == null) return;
            t.Glyph = '\uE765';
            t.Title = names.Count == 1 ? Lang.F("{0} dipakai aplikasi lain", names[0]) : Lang.F("{0} pintasan dipakai aplikasi lain", names.Count);
            t.Detail = Lang.T("Ganti di Pengaturan");
            t.Data = (Action)delegate { OpenPrefs("keys"); };
        }

        void PrompterHotkeys(bool on)
        {
            foreach (var d in Hotkeys)
            {
                if (!d.Prompter) continue;
                if (on) RegisterOne(d);
                else UnregisterOne(d);
            }
        }

        // Teleprompter window: caps, shared modifiers in the caption
        void PrompterKeys(PrompterSetup f)
        {
            var spec = new[]
            {
                new object[] { "Jeda dan lanjut", PrompterHotkey }, new object[] { "Ulang dari awal", PrompterRestart },
                new object[] { "Mundur atau maju 5 detik", PrompterBack, PrompterAhead }, new object[] { "Lebih cepat atau lambat", PrompterFaster, PrompterSlower },
                new object[] { "Buka jendela ini", PrompterSetupKey }, new object[] { "Hentikan", PrompterStop },
            };
            string shared = null;
            bool same = true;
            foreach (var row in spec)
                for (int i = 1; i < row.Length; i++)
                {
                    string c = HotkeyText((int)row[i]);
                    if (c.Length == 0) continue;
                    string mods = c.Substring(0, c.LastIndexOf('+') + 1);
                    if (shared == null) shared = mods;
                    else if (shared != mods) same = false;
                }
            if (shared == null) same = false;
            var rows = new string[spec.Length][];
            for (int r = 0; r < spec.Length; r++)
            {
                rows[r] = new string[spec[r].Length];
                rows[r][0] = Lang.T((string)spec[r][0]);
                for (int i = 1; i < spec[r].Length; i++)
                {
                    string c = HotkeyText((int)spec[r][i]);
                    rows[r][i] = c.Length == 0 ? Lang.T("mati") : (same ? "" : c.Substring(0, c.LastIndexOf('+') + 1)) + CapName(c.Substring(c.LastIndexOf('+') + 1));
                }
            }
            f.SetKeys(same ? Lang.F("Pintasan, dengan {0}", shared.TrimEnd('+')) : Lang.T("Pintasan"), same ? shared : "", rows);
        }

        static string CapName(string key)
        {
            if (key == "Space") return Lang.T("Spasi");
            if (key == "Left") return "←";
            if (key == "Right") return "→";
            if (key == "-") return "−";
            return key;
        }

        // Recording in Pengaturan: keys must reach the window
        public void HoldHotkeys(bool hold)
        {
            if (hold == hotkeysHeld) return;
            if (hold)
            {
                foreach (var d in Hotkeys) UnregisterOne(d);
                hotkeysHeld = true;
                return;
            }
            hotkeysHeld = false;
            foreach (var d in Hotkeys) if (!d.Prompter || PrompterOn) RegisterOne(d);
        }

        // All at once: swaps can't block each other
        public void ResetHotkeys()
        {
            foreach (var d in Hotkeys) { UnregisterOne(d); settings.Set(d.Key, d.Fallback); }
            settings.Save();
            hotkeysTaken.Clear();
            foreach (var d in Hotkeys) if (!d.Prompter || PrompterOn) RegisterOne(d);
        }

        // Null when saved, otherwise the reason
        public string SetHotkey(int id, string combo)
        {
            var d = HotkeyById(id);
            if (d == null) return "";
            string canon = combo.Length == 0 ? "" : Canonical(combo);
            if (combo.Length > 0 && canon.Length == 0) return Lang.T("Pakai Ctrl, Alt, atau Win dengan satu tombol");
            if (canon.Length > 0)
                foreach (var o in Hotkeys)
                    if (o.Id != id && HotkeyText(o.Id) == canon) return Lang.F("Sudah dipakai untuk {0}", Lang.T(o.Name));
            // Probe now so a clash shows at once
            if (canon.Length > 0 && canon != HotkeyText(id))
            {
                uint mods;
                Keys key;
                ParseCombo(canon, out mods, out key);
                bool wasOn = hotkeysOn.Contains(id);
                if (wasOn) UnregisterOne(d);
                bool free = RegisterHotKey(Handle, id, mods | ModNoRepeat, (uint)key);
                if (free) UnregisterHotKey(Handle, id);
                if (!free)
                {
                    if (wasOn) RegisterOne(d);
                    return Lang.T("Dipakai aplikasi lain");
                }
            }
            UnregisterOne(d);
            settings.Set(d.Key, canon.Length == 0 ? HotkeyOff : canon == Canonical(d.Fallback) ? d.Fallback : canon);
            settings.Save();
            hotkeysTaken.Remove(id);
            if (!d.Prompter || PrompterOn) RegisterOne(d);
            return null;
        }
    }

    // Click, then press the new shortcut
    class KitHotkey : KitButton
    {
        public Func<string, bool> Commit;
        public Action Begin, End;
        string combo = "";
        bool recording;

        [DllImport("user32.dll")] static extern short GetKeyState(int key);

        public string Combo
        {
            get { return combo; }
            set { combo = value ?? ""; if (!recording) Text = Shown(); }
        }

        string Shown() { return combo.Length > 0 ? combo : Lang.T("Belum diatur"); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (recording) return;
            recording = true;
            Text = Lang.T("Tekan pintasan");
            if (Begin != null) Begin();
            Invalidate();
        }

        void Stop()
        {
            if (!recording) return;
            recording = false;
            if (End != null) End();
            Text = Shown();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e) { Stop(); base.OnLostFocus(e); }
        protected override void OnHandleDestroyed(EventArgs e) { Stop(); base.OnHandleDestroyed(e); }
        protected override bool IsInputKey(Keys keyData) { return recording || base.IsInputKey(keyData); }

        // Also sees Alt combos, which skip KeyDown
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!recording) return base.ProcessCmdKey(ref msg, keyData);
            Keys code = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if (code == Keys.Escape && mods == Keys.None) { Stop(); return true; }
            if (code == Keys.ControlKey || code == Keys.Menu || code == Keys.ShiftKey || code == Keys.LWin || code == Keys.RWin) return true;
            string next;
            if ((code == Keys.Back || code == Keys.Delete) && mods == Keys.None) next = "";
            else
            {
                uint m = 0;
                if ((mods & Keys.Control) != 0) m |= 2;
                if ((mods & Keys.Alt) != 0) m |= 1;
                if ((mods & Keys.Shift) != 0) m |= 4;
                if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) m |= 8;
                next = Island.ComboText(m, code);
            }
            if (Commit != null) Commit(next);
            Stop();
            return true;
        }
    }
}
