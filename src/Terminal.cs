using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // Window hosting an agent, found from its hook connection
    static class TerminalFinder
    {
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        static readonly object sync = new object();
        static readonly Dictionary<int, IntPtr> byPid = new Dictionary<int, IntPtr>();

        public static IntPtr ForConnection(IPEndPoint client, int serverPort)
        {
            int pid = PidOf(client.Port, serverPort);
            if (pid <= 0) return IntPtr.Zero;
            lock (sync)
            {
                IntPtr h;
                if (byPid.TryGetValue(pid, out h) && IsWindow(h)) return h;
                h = WindowFor(pid);
                if (byPid.Count > 64) byPid.Clear();
                byPid[pid] = h;
                return h;
            }
        }

        // Loopback client port -> owning process (MIB_TCPROW_OWNER_PID rows)
        static int PidOf(int clientPort, int serverPort)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return 0;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = new IntPtr(buf.ToInt64() + 4 + i * 24);
                    if (Port(Marshal.ReadInt32(row, 8)) == clientPort && Port(Marshal.ReadInt32(row, 16)) == serverPort)
                        return Marshal.ReadInt32(row, 20);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return 0;
        }

        static int Port(int raw) { return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }

        // Classic console first, else the nearest ancestor with a window
        static IntPtr WindowFor(int pid)
        {
            FreeConsole();
            if (AttachConsole((uint)pid))
            {
                IntPtr c = GetConsoleWindow();
                FreeConsole();
                if (c != IntPtr.Zero)
                {
                    var cls = new StringBuilder(64);
                    GetClassName(c, cls, 64);
                    // ConPTY: the stand-in window is owned by the real terminal
                    IntPtr owner = cls.ToString() == "PseudoConsoleWindow" ? GetWindow(c, 4) : c;
                    if (owner != IntPtr.Zero && IsWindowVisible(owner)) return owner;
                }
            }
            var parents = ParentMap();
            int self = Process.GetCurrentProcess().Id, cur = pid;
            for (int depth = 0; depth < 16 && cur > 4; depth++)
            {
                try
                {
                    using (var p = Process.GetProcessById(cur))
                    {
                        string name = p.ProcessName.ToLowerInvariant();
                        if (name == "explorer" || cur == self) break;
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && IsWindowVisible(h)) return h;
                    }
                }
                catch (Exception) { }
                uint parent;
                if (!parents.TryGetValue(cur, out parent) || parent == cur) break;
                cur = (int)parent;
            }
            return IntPtr.Zero;
        }

        static Dictionary<int, uint> ParentMap()
        {
            var map = new Dictionary<int, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (Process32FirstW(snap, ref e))
                    do map[(int)e.th32ProcessID] = e.th32ParentProcessID; while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return map;
        }

        public static bool Focus(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, 9);
            SwitchToThisWindow(h, true);
            return true;
        }
    }
}
