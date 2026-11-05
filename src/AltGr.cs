using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // AltGr is Ctrl+Alt: give typed characters back
    partial class Island
    {
        [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr hkl);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int size, uint flags, IntPtr hkl);

        // True: the hotkey was a character
        bool GiveBackAltGr(IntPtr lParam)
        {
            long l = lParam.ToInt64();
            uint mods = (uint)(l & 0xFFFF);
            int vk = (int)((l >> 16) & 0xFFFF);
            if ((mods & (ModCtrl | ModAlt)) != (ModCtrl | ModAlt) || (mods & ModWin) != 0) return false;
            // Right Alt held: AltGr, not a shortcut
            if ((GetAsyncKeyState(0xA5) & 0x8000) == 0) return false;
            uint pid;
            var hkl = GetKeyboardLayout(WindowPid(GetForegroundWindow(), out pid));
            string ch = AltGrChar(vk, (mods & ModShift) != 0, hkl);
            if (ch == null) return false;
            var keys = new INPUT[ch.Length * 2];
            for (int i = 0; i < ch.Length; i++)
            {
                keys[i * 2] = Key(0, ch[i], 4);
                keys[i * 2 + 1] = Key(0, ch[i], 4 | 2);
            }
            SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT)));
            return true;
        }

        // What AltGr+key prints on this layout
        internal static string AltGrChar(int vk, bool shift, IntPtr hkl)
        {
            var state = new byte[256];
            state[0x11] = state[0xA2] = state[0x12] = state[0xA5] = 0x80;
            if (shift) state[0x10] = state[0xA0] = 0x80;
            var sb = new StringBuilder(8);
            uint scan = MapVirtualKeyEx((uint)vk, 0, hkl);
            // Flag 4: leave the dead-key buffer alone
            int n = ToUnicodeEx((uint)vk, scan, state, sb, sb.Capacity, 4, hkl);
            if (n != 1 || sb[0] < ' ') return null;
            return sb.ToString(0, 1);
        }
    }
}
