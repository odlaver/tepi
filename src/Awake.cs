using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Keep awake: screen and system stay on
    partial class Island
    {
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        const uint EsContinuous = 0x80000000, EsSystem = 0x1, EsDisplay = 0x2;
        const char IcoAwake = '\uEC32';

        // MinValue off, MaxValue until turned off
        DateTime awakeUntilUtc = DateTime.MinValue;

        bool AwakeOn { get { return awakeUntilUtc != DateTime.MinValue; } }

        // UI thread holds the request for the app's life
        void SetAwake(TimeSpan span)
        {
            awakeUntilUtc = span == TimeSpan.Zero ? DateTime.MinValue : span == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow + span;
            SetThreadExecutionState(AwakeOn ? EsContinuous | EsSystem | EsDisplay : EsContinuous);
            Kick();
        }

        void ToggleAwake() { SetAwake(AwakeOn ? TimeSpan.Zero : TimeSpan.MaxValue); }

        // Called each second; true when the panel needs a redraw
        bool AwakeTick(DateTime nowUtc, bool minute)
        {
            if (!AwakeOn || awakeUntilUtc == DateTime.MaxValue) return false;
            if (nowUtc < awakeUntilUtc) return minute && expanded && page == PageControl;
            SetAwake(TimeSpan.Zero);
            ShowInfo(IcoAwake, Lang.T("Terjaga selesai"), Lang.T("Laptop boleh tidur lagi"));
            return true;
        }

        string AwakeLabel()
        {
            if (!AwakeOn || awakeUntilUtc == DateTime.MaxValue) return Lang.T("Terjaga");
            return Fmt.Left(awakeUntilUtc - DateTime.UtcNow);
        }

        void AwakeMenu()
        {
            var m = AwakeItems();
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(Control.MousePosition);
        }

        ContextMenuStrip AwakeItems()
        {
            var m = new ContextMenuStrip();
            foreach (var choice in new[] { new object[] { Lang.T("30 menit"), TimeSpan.FromMinutes(30) }, new object[] { Lang.T("1 jam"), TimeSpan.FromHours(1) },
                                           new object[] { Lang.T("2 jam"), TimeSpan.FromHours(2) }, new object[] { Lang.T("Sampai dimatikan"), TimeSpan.MaxValue } })
            {
                var span = (TimeSpan)choice[1];
                m.Items.Add((string)choice[0], null, delegate { SetAwake(span); });
            }
            if (AwakeOn)
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Matikan"), null, delegate { SetAwake(TimeSpan.Zero); });
            }
            return m;
        }
    }
}
