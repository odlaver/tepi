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
            return RunIsland();
        }

        // Relay path never loads WinForms
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunIsland()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\TepiIsland", out created))
            {
                if (!created) return 0;
                // Full collections must not block a frame
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { }
                Application.ThreadException += (s, e) => Log.Write("ui: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Island());
            }
            return 0;
        }
    }
}
