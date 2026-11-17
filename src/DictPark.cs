using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // An empty desktop hides every key from other apps, Tepi's hook too.
    // While it is in front, an invisible window holds the focus instead.
    sealed class DesktopPark
    {
        readonly Func<bool> yield;
        Thread thread;
        uint threadId;
        Keeper keeper;
        IntPtr desk, watch;
        Native.WinEventProc watchProc;
        bool parked, handedBack;

        const int ParkTimer = 2, NoActivate = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        struct GuiInfo { public int Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        struct LastInput { public uint Size, Time; }

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, Native.WinEventProc fn, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern int GetMessage(out Native.MSG msg, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Native.MSG msg);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);
        [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        public DesktopPark(Func<bool> yield) { this.yield = yield; }

        public bool Running { get { return thread != null; } }

        // The window that holds the focus, to tell it from a real target
        public IntPtr KeeperHandle { get { var k = keeper; return k != null ? k.Handle : IntPtr.Zero; } }

        sealed class Keeper : NativeWindow
        {
            public Action<int> Due;
            public Func<int, IntPtr, IntPtr, bool> Key;

            // Always shown: hiding an active window hands focus elsewhere
            public Keeper()
            {
                CreateHandle(new CreateParams { Style = unchecked((int)0x80000000), ExStyle = 0x80 | 0x80000 | 0x8 | NoActivate, X = -32000, Y = -32000, Width = 1, Height = 1 });
                SetLayeredWindowAttributes(Handle, 0, 1, 2);
                ShowWindow(Handle, 8);
            }

            public void Take()
            {
                SetWindowLong(Handle, -20, GetWindowLong(Handle, -20) & ~NoActivate);
                if (!SetForegroundWindow(Handle)) ShellApp.Activate(Handle);
            }

            public void Rest() { SetWindowLong(Handle, -20, GetWindowLong(Handle, -20) | NoActivate); }

            public void Later(int id, uint ms) { SetTimer(Handle, (IntPtr)id, ms, IntPtr.Zero); }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x113:
                        KillTimer(Handle, m.WParam);
                        if (Due != null) Due(m.WParam.ToInt32());
                        return;
                    case 0x100:
                    case 0x104:
                        if (Key != null && Key(m.Msg, m.WParam, m.LParam)) return;
                        break;
                    // Alt+F4 must never close it
                    case 0x10:
                        return;
                    case 0x112:
                        if ((m.WParam.ToInt64() & 0xFFF0) == 0xF060) return;
                        break;
                }
                base.WndProc(ref m);
            }
        }

        public void Start()
        {
            if (thread != null) return;
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate ()
            {
                threadId = GetCurrentThreadId();
                try { keeper = new Keeper { Due = OnTimer, Key = OnKeeperKey }; }
                catch (Exception ex) { Log.Write("dikte desktop: " + ex.Message); }
                watchProc = OnForeground;
                watch = SetWinEventHook(3, 3, IntPtr.Zero, watchProc, 0, 0, 0);
                if (keeper != null && IsDesktop(GetForegroundWindow())) keeper.Later(ParkTimer, 350);
                ready.Set();
                Native.MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref m);
                if (watch != IntPtr.Zero) UnhookWinEvent(watch);
                watch = IntPtr.Zero;
                // Leave the desktop as it was
                if (keeper != null)
                {
                    if (GetForegroundWindow() == keeper.Handle && IsWindow(desk)) SetForegroundWindow(desk);
                    keeper.DestroyHandle();
                    keeper = null;
                }
            }) { IsBackground = true, Name = "dikte desktop" };
            thread.Start();
            ready.WaitOne(2000);
        }

        public void Stop()
        {
            if (thread == null) return;
            PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1500);
            thread = null;
        }

        static bool IsDesktop(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            if (h == GetShellWindow()) return true;
            string cls = ClassOf(h);
            return cls == "Progman" || cls == "WorkerW";
        }

        static string ClassOf(IntPtr h)
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            return c.ToString();
        }

        static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        static double IdleSeconds()
        {
            var li = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            return GetLastInputInfo(ref li) ? ((uint)Environment.TickCount - li.Time) / 1000.0 : 0;
        }

        void OnForeground(IntPtr h, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || hwnd == IntPtr.Zero || keeper == null) return;
            if (hwnd == keeper.Handle)
            {
                if (IsDesktop(desk)) parked = true;
                return;
            }
            parked = false;
            if (IsDesktop(hwnd))
            {
                if (!handedBack) keeper.Later(ParkTimer, 350);
                else keeper.Later(ParkTimer, 1000);
            }
            else
            {
                handedBack = false;
                keeper.Rest();
            }
        }

        void OnTimer(int id)
        {
            if (id == ParkTimer) Park();
        }

        // Empty desktop in front: Tepi holds the focus so the keys reach it
        void Park()
        {
            var fg = GetForegroundWindow();
            if (keeper == null || fg == keeper.Handle || !IsDesktop(fg)) return;
            if (yield != null && yield()) return;
            if (handedBack)
            {
                // Typed on the desktop a moment ago: leave it be until it goes quiet
                if (IdleSeconds() < 2) { keeper.Later(ParkTimer, 1000); return; }
                handedBack = false;
            }
            // Menu, drag or rename on the desktop: wait
            uint pid, tid = GetWindowThreadProcessId(fg, out pid);
            var gi = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            bool busy = GetGUIThreadInfo(tid, ref gi) && ((gi.Flags & 0x1E) != 0 || gi.Capture != IntPtr.Zero || gi.Caret != IntPtr.Zero);
            if (busy || Held(0x01) || Held(0x02)) { keeper.Later(ParkTimer, 500); return; }
            desk = fg;
            keeper.Take();
            parked = GetForegroundWindow() == keeper.Handle;
            if (logged++ < 20) Log.Write("dikte desktop: fokus " + (parked ? "dipegang Tepi" : "gagal dipegang") + " (" + ClassOf(fg) + ")");
        }

        int logged;

        // Shift and the right Ctrl/Alt start dictation, so they stay here
        static bool Stays(int vk, bool ext) { return vk == 0x10 || (ext && (vk == 0x11 || vk == 0x12)) || vk == 0x5B || vk == 0x5C || vk == 0x14; }

        // Typing on the desktop: it gets the focus back, and the key
        bool OnKeeperKey(int msg, IntPtr w, IntPtr l)
        {
            int vk = w.ToInt32();
            bool ext = ((l.ToInt64() >> 24) & 1) != 0;
            if (!parked || Stays(vk, ext)) return false;
            parked = false;
            handedBack = true;
            if (!IsWindow(desk)) return false;
            SetForegroundWindow(desk);
            keeper.Rest();
            // Left Ctrl/Alt alone: nothing to forward
            if (vk == 0x11 || vk == 0x12) return true;
            uint pid, tid = GetWindowThreadProcessId(desk, out pid);
            var gi = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            IntPtr target = GetGUIThreadInfo(tid, ref gi) && gi.Focus != IntPtr.Zero ? gi.Focus : DesktopList(desk);
            PostMessage(target, msg, w, l);
            return true;
        }

        static IntPtr DesktopList(IntPtr top)
        {
            var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            var list = view != IntPtr.Zero ? FindWindowEx(view, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
            return list != IntPtr.Zero ? list : top;
        }
    }
}
