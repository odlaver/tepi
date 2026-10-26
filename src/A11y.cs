using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Screen reader announcements and capture visibility
    partial class Island
    {
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint ini);

        bool ShowInCapture { get { return settings.GetBool("capture.show", false); } }

        static bool ScreenReaderOn()
        {
            bool on = false;
            try { SystemParametersInfo(0x0046, 0, ref on, 0); } catch (Exception) { }
            return on;
        }

        // After the view settles, so toast text is filled
        void AnnounceView(View v)
        {
            if (!settings.GetBool("a11y.announce", true) || !ScreenReaderOn()) return;
            Post(() =>
            {
                string text = Describe(v);
                if (text.Length == 0) return;
                try
                {
                    AccessibilityObject.RaiseAutomationNotification(
                        System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                        v == View.Approve ? System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent
                                          : System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent,
                        text);
                }
                catch (Exception ex) { Log.Write("announce: " + ex.Message); }
            });
        }

        string Describe(View v)
        {
            switch (v)
            {
                case View.Approve:
                    return Lang.T("Agent minta izin. Buka tab Agen atau jawab di terminal.");
                case View.Info:
                case View.Notif:
                case View.Done:
                case View.Deadline:
                case View.Power:
                case View.Network:
                case View.Privacy:
                    if (toast == null) return "";
                    return toast.Detail.Length > 0 ? toast.Title + ". " + toast.Detail : toast.Title;
                case View.Class:
                    var c = ClassNow();
                    return c == null ? "" : Lang.F("Kelas berlangsung: {0}", c.Title);
                default:
                    return "";
            }
        }
    }
}
