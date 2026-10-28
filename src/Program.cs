using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Agent hook bridge: no window, no mutex
            if (args.Length > 0 && args[0] == "--relay") return Relay.Run(args);
            if (args.Length > 0 && args[0] == "--notify") return NotifyCommand.Run(args);
            if (args.Length > 0 && args[0] == "--statusline") return StatusLine.Run(args);
            return RunIsland();
        }

        // Old copy may still be closing after an update
        static bool Take(Mutex m)
        {
            try { return m.WaitOne(20000); }
            catch (AbandonedMutexException) { return true; }
        }

        // Relay path never loads WinForms
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunIsland()
        {
            // After an update: wait for the old copy to quit
            int old = 0;
            bool after = Environment.GetCommandLineArgs().Length > 2 && Environment.GetCommandLineArgs()[1] == "--after" && int.TryParse(Environment.GetCommandLineArgs()[2], out old);
            if (after)
            {
                try { using (var prev = System.Diagnostics.Process.GetProcessById(old)) prev.WaitForExit(15000); }
                catch (ArgumentException) { }
            }
            bool created;
            using (var mutex = new Mutex(true, Settings.MutexName, out created))
            {
                if (!created && !(after && Take(mutex))) return 0;
                // Full collections must not block a frame
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { }
                Application.ThreadException += (s, e) => Log.Write("ui: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                // Opt in to .NET 4.8 accessibility, for screen reader notifications
                AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures", false);
                AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.2", false);
                AppContext.SetSwitch("Switch.UseLegacyAccessibilityFeatures.3", false);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Island());
            }
            return 0;
        }
    }
}
