using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Removable drives: plugged in, ejected
    partial class Island
    {
        class DriveCard
        {
            public char Letter;
            public string Label = "";
            public long Free, Total;
            public bool Ejecting, Ejected;
        }

        const char IcoDrive = '';
        const float EjectW = 84;
        char ejectPending;
        DateTime ejectUtc;

        bool DrivesOn { get { return settings.GetBool("drives", true); } }

        // WM_DEVICECHANGE, volumes only
        void OnDeviceChange(IntPtr wParam, IntPtr lParam)
        {
            long ev = wParam.ToInt64();
            if ((ev != 0x8000 && ev != 0x8004) || lParam == IntPtr.Zero) return;
            if (Marshal.ReadInt32(lParam, 4) != 2) return;
            uint mask = (uint)Marshal.ReadInt32(lParam, 12);
            // Network shares aren't drives you plug
            if ((Marshal.ReadInt16(lParam, 16) & 2) != 0) return;
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                char letter = (char)('A' + i);
                if (ev == 0x8000) DriveArrived(letter);
                else DriveLeft(letter);
            }
        }

        void DriveArrived(char letter)
        {
            if (!DrivesOn) return;
            // Size reads can stall on slow sticks
            ThreadPool.QueueUserWorkItem(delegate
            {
                DriveCard card = null;
                try
                {
                    var d = new DriveInfo(letter + ":\\");
                    if (d.DriveType != DriveType.Removable || !d.IsReady) return;
                    card = new DriveCard { Letter = letter, Label = d.VolumeLabel, Free = d.AvailableFreeSpace, Total = d.TotalSize };
                }
                catch (Exception ex) { Log.Write("drive: " + ex.Message); }
                if (card == null) return;
                Post(() =>
                {
                    var t = ShowToast(View.Drive, 8, 2, true);
                    if (t != null) { t.Data = card; t.Flag = false; }
                });
            });
        }

        void DriveLeft(char letter)
        {
            var card = toast != null ? toast.Data as DriveCard : null;
            bool ours = ejectPending == letter && (DateTime.UtcNow - ejectUtc).TotalSeconds < 30;
            if (ours)
            {
                ejectPending = '\0';
                if (card == null || card.Letter != letter) card = new DriveCard { Letter = letter };
                card.Ejecting = false;
                card.Ejected = true;
                var t = ShowToast(View.Drive, 3, 2, false, true);
                if (t != null) { t.Data = card; t.Flag = false; }
                return;
            }
            if (card != null && card.Letter == letter) DismissToast();
        }

        void EjectDrive(DriveCard card)
        {
            if (card.Ejecting || card.Ejected) return;
            card.Ejecting = true;
            ejectPending = card.Letter;
            ejectUtc = DateTime.UtcNow;
            // Shell's eject keeps Windows' own warnings
            var th = new Thread(delegate ()
            {
                try
                {
                    var shellType = Type.GetTypeFromProgID("Shell.Application");
                    object shell = Activator.CreateInstance(shellType);
                    object drives = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { 17 });
                    object item = drives.GetType().InvokeMember("ParseName", System.Reflection.BindingFlags.InvokeMethod, null, drives, new object[] { card.Letter + ":\\" });
                    if (item != null) item.GetType().InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, item, new object[] { "Eject" });
                    Marshal.ReleaseComObject(shell);
                }
                catch (Exception ex) { Log.Write("eject: " + ex.Message); }
            }) { IsBackground = true, Name = "eject" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            // Still there: something holds it
            var check = new System.Windows.Forms.Timer { Interval = 6000 };
            check.Tick += delegate
            {
                check.Stop();
                check.Dispose();
                if (ejectPending != card.Letter) return;
                ejectPending = '\0';
                card.Ejecting = false;
                var t = ShowToast(View.Drive, 4, 2, true);
                if (t != null) { t.Data = card; t.Flag = true; }
            };
            check.Start();
            Kick();
        }

        void OpenDrive(DriveCard card)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", card.Letter + ":\\"); }
            catch (Exception ex) { Log.Write("drive open: " + ex.Message); }
            DismissToast();
        }

        string DriveTitle(DriveCard c)
        {
            string name = c.Label.Length > 0 ? c.Label : Lang.T("Flashdisk");
            return name + " (" + c.Letter + ":)";
        }

        string DriveDetail(DriveCard c)
        {
            if (c.Ejected) return Lang.T("Aman dicabut");
            if (c.Ejecting) return Lang.T("Mengeluarkan…");
            if (toast != null && toast.Flag) return Lang.T("Masih dipakai aplikasi lain");
            return Lang.F("{0} kosong dari {1}", Fmt.Bytes(c.Free), Fmt.Bytes(c.Total));
        }

        float DriveWidth()
        {
            var c = toast != null ? toast.Data as DriveCard : null;
            if (c == null || p.G == null) return 360;
            if (c.Ejected) return Clamp(p.Measure(DriveTitle(c), Ink.Semi(13.5f)) + 90, 240, 380);
            return 400;
        }

        void DrawDrive(float w, float h)
        {
            var c = toast != null ? toast.Data as DriveCard : null;
            if (c == null) return;
            float cy = h / 2;
            if (c.Ejected)
            {
                p.Circle(30, cy, 13, p.T.Text);
                p.Check(30, cy, 12, p.T.Bg, 1f);
            }
            else
            {
                p.Circle(30, cy, 16, p.T.Fill);
                p.Icon(IcoDrive, 15f, p.T.Text, 30, cy);
            }
            float x = c.Ejected ? 54 : 58;
            float bx = w - 16 - EjectW - 6 - 52;
            float right = c.Ejected ? w - 20 : bx - 8;
            p.Text(DriveTitle(c), Ink.Semi(13.5f), p.T.Text, x, cy - 10, right - x, StringAlignment.Near);
            p.Text(DriveDetail(c), Ink.Text(12.5f), p.T.Sub, x, cy + 11, right - x, StringAlignment.Near);
            if (c.Ejected) return;
            Pill(new RectangleF(bx, cy - 15, 52, 30), "driveOpen", Lang.T("Buka"), PillKind.Secondary, delegate { OpenDrive(c); }, !c.Ejecting);
            Pill(new RectangleF(bx + 58, cy - 15, EjectW, 30), "driveEject", Lang.T("Keluarkan"), PillKind.Secondary, delegate { EjectDrive(c); }, !c.Ejecting);
        }
    }
}
