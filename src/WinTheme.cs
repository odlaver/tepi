using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace Tepi
{
    // Windows light or dark: read and flip
    partial class Island
    {
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const char IcoDarkMode = '\uE7A1';

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        bool winDark = ReadWindowsDark();

        static bool ReadWindowsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                    return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch (Exception) { return false; }
        }

        void ToggleWindowsDark()
        {
            bool dark = !winDark;
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
                {
                    k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                    k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log.Write("dark mode: " + ex.Message); return; }
            winDark = dark;
            ApplyThemeMode();
            Kick();
            // Hung windows can stall a broadcast
            ThreadPool.QueueUserWorkItem(delegate
            {
                UIntPtr r;
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out r);
            });
        }

        // WM_SETTINGCHANGE "ImmersiveColorSet"
        void OnColorSetChanged()
        {
            bool dark = ReadWindowsDark();
            if (dark == winDark) return;
            winDark = dark;
            ApplyThemeMode();
            Kick();
        }
    }
}
