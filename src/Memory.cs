using System;
using System.Runtime.InteropServices;

namespace Tepi
{
    // Keeps Task Manager memory near the floor between trims
    partial class Island
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
        }

        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process, ref MemCounters c, uint size);

        const double TrimAboveMb = 9, HardCapMb = 14;

        // What Task Manager calls Memory
        static double PrivateMb()
        {
            var c = new MemCounters { cb = (uint)Marshal.SizeOf(typeof(MemCounters)) };
            if (!GetProcessMemoryInfo(Native.GetCurrentProcess(), ref c, c.cb)) return 0;
            return c.PrivateWorkingSetSize.ToUInt64() / 1048576.0;
        }

        // Each second: trim past 9 MB when calm; past 14 MB even mid-animation
        void MemoryWatch()
        {
            double since = (DateTime.UtcNow - lastTrimUtc).TotalSeconds;
            if (since < 6) return;
            double mb = PrivateMb();
            if ((!animating && mb > TrimAboveMb) || (since >= 8 && mb > HardCapMb)) Trim(true);
        }
    }
}
