using System;
using System.Runtime.InteropServices;
using WinTimer = System.Windows.Forms.Timer;

namespace Tepi
{
    // Leak guard and one trim at rest
    partial class Island
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
        }

        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process, ref MemCounters c, uint size);

        const double LeakMb = 150;
        // Quiet this long after motion, then trim
        const int RestMs = 3000;
        readonly WinTimer trimTimer = new WinTimer { Interval = RestMs };
        DateTime leakCheckUtc;
        bool trimWanted;

        // Private bytes, what the process really holds
        static double PrivateMb()
        {
            var c = new MemCounters { cb = (uint)Marshal.SizeOf(typeof(MemCounters)) };
            if (!GetProcessMemoryInfo(Native.GetCurrentProcess(), ref c, c.cb)) return 0;
            return c.PrivateUsage.ToUInt64() / 1048576.0;
        }

        // Per minute: collect past leak line
        void MemoryWatch()
        {
            if ((DateTime.UtcNow - leakCheckUtc).TotalSeconds < 60) return;
            leakCheckUtc = DateTime.UtcNow;
            double mb = PrivateMb();
            if (mb <= LeakMb) return;
            Log.Write("memory " + mb.ToString("0") + " MB private, collecting");
            Trim();
        }

        // Big view gone: trim once it rests
        void TrimSoon()
        {
            trimWanted = true;
            RestTimer();
        }

        // Motion ended: restart the rest countdown
        void RestTimer()
        {
            if (!trimWanted) return;
            trimTimer.Stop();
            trimTimer.Start();
        }

        void TrimAtRest()
        {
            // Busy again: wait for the next rest
            if (!trimWanted || animating || expanded || current == View.Panel || PrompterOn || dragId != null) return;
            if ((DateTime.UtcNow - lastMotionUtc).TotalMilliseconds < RestMs - 100) { RestTimer(); return; }
            trimWanted = false;
            Trim();
        }

        void Trim()
        {
            double t0 = clock.Elapsed.TotalMilliseconds;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            if (fpsLog) Log.Write("trim " + (clock.Elapsed.TotalMilliseconds - t0).ToString("0.0") + "ms");
        }
    }
}
