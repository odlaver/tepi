using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // Screen share: hide island, mask messages
    partial class Island
    {
        bool shareAuto;
        DateTime shareScanUtc = DateTime.MinValue;

        [DllImport("user32.dll", EntryPoint = "EnumWindows")] static extern bool ShareWindows(EnumProc f, IntPtr l);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] static extern int ShareText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] static extern int ShareClass(IntPtr h, StringBuilder s, int n);

        // auto, on, off
        string ShareMode { get { return settings.Get("share.mode", "auto"); } }

        bool Sharing { get { string m = ShareMode; return m == "on" || (m == "auto" && shareAuto); } }

        // Window titles each app shows while sharing
        static readonly string[] ShareTitles =
        {
            "is sharing your screen", "is sharing a window", "is sharing this tab", "sedang berbagi layar", "sedang membagikan layar",
            "sharing control bar", "bilah kontrol berbagi", "you are screen sharing", "share toolbar", "anda sedang berbagi",
        };
        static readonly string[] ShareClasses = { "ZPFloatToolbarClass", "ZPContentShareToolbarWndClass" };

        void ApplyShare()
        {
            if (ShareMode != "auto") shareAuto = false;
            ApplyGlass();
            Kick();
        }

        // Each second; scans only during calls
        void TickShare(DateTime now)
        {
            if (ShareMode != "auto") return;
            bool call = micSinceUtc != DateTime.MinValue;
            if (!call)
            {
                if (shareAuto) { shareAuto = false; ApplyGlass(); Kick(); }
                return;
            }
            if ((now - shareScanUtc).TotalSeconds < 2) return;
            shareScanUtc = now;
            bool found = ScanShare();
            if (found == shareAuto) return;
            shareAuto = found;
            ApplyGlass();
            if (found) ShowInfo('\uE7F4', Lang.T("Berbagi layar terdeteksi"), Lang.T("Isi pesan disamarkan, island tidak ikut terekam"), true);
            Kick();
        }

        static bool ScanShare()
        {
            bool hit = false;
            var title = new StringBuilder(256);
            var cls = new StringBuilder(128);
            ShareWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                cls.Length = 0;
                ShareClass(h, cls, cls.Capacity);
                string c = cls.ToString();
                foreach (var s in ShareClasses) if (c == s) { hit = true; return false; }
                title.Length = 0;
                if (ShareText(h, title, title.Capacity) == 0) return true;
                string t = title.ToString().ToLowerInvariant();
                foreach (var s in ShareTitles) if (t.Contains(s)) { hit = true; return false; }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        string NotifTitle(NotifItem item)
        {
            if (Sharing) return item.AppName;
            return item.Title.Length > 0 ? item.Title : item.AppName;
        }
    }
}
