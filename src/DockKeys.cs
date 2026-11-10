using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Win keys open Tepi Start and cards
    sealed class StartKey
    {
        public event Action<string> Pressed;

        // Holds the focus on an empty desktop
        public IntPtr KeeperHandle { get { var k = keeper; return k != null ? k.Handle : IntPtr.Zero; } }

        // Hook thread: hook only, never blocked
        Thread hookThread, helpThread;
        uint hookId, helpId;
        IntPtr hook;
        HookProc proc;
        volatile bool winDown;
        bool used, passed;
        int eaten;
        long winUpTicks, keyTicks;

        // Helper thread: focus keeper, Start watcher, log
        Keeper keeper;
        IntPtr desk, watch, lastFront;
        Native.WinEventProc watchProc;
        bool kept, parked, handedBack;
        string where = "";

        // Shell surfaces where Windows drops simulated keys
        static readonly string[] ShellHosts = { "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "ShellHost", "LockApp" };

        const int VkLWin = 0x5B, VkRWin = 0x5C, VkEsc = 0x1B, VkShift = 0x10, VkCtrl = 0x11, VkAlt = 0x12;
        // Unassigned key; hides lone Win
        const ushort Mask = 0xE8;
        const int KeepMsg = 0x8001, WinUpMsg = 0x8002, RehookMsg = 0x8003, UnkeepTimer = 1, ParkTimer = 2;
        // Marks the keys we send ourselves
        static readonly IntPtr Mine = (IntPtr)0x54455049;

        [StructLayout(LayoutKind.Sequential)]
        struct KeyInfo { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        struct GuiInfo { public int Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int L, T, R, B; }

        delegate IntPtr HookProc(int code, IntPtr msg, IntPtr info);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr info);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, Native.WinEventProc fn, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern int GetMessage(out Native.MSG msg, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Native.MSG msg);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
        const int NoActivate = 0x08000000;
        [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);
        [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder path, ref int size);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int len, out int ret);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        // Invisible focus holder. The desktop hides its keys from
        // every app, so Tepi holds the focus while it shows.
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

            // Not picked when other windows close
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
            if (hookThread != null) return;
            helpThread = Loop("start helper", delegate
            {
                helpId = GetCurrentThreadId();
                try { keeper = new Keeper { Due = OnTimer, Key = OnKeeperKey }; }
                catch (Exception ex) { Log.Write("start key: keeper " + ex.Message); }
                // Windows Start appears: Tepi's replaces it
                watchProc = OnForeground;
                watch = SetWinEventHook(3, 3, IntPtr.Zero, watchProc, 0, 0, 0);
                if (keeper != null && IsDesktop(GetForegroundWindow())) keeper.Later(ParkTimer, 350);
            }, OnHelpMessage, delegate
            {
                if (watch != IntPtr.Zero) UnhookWinEvent(watch);
                watch = IntPtr.Zero;
                // Leave the desktop as it was
                if (keeper != null && GetForegroundWindow() == keeper.Handle && IsWindow(desk)) SetForegroundWindow(desk);
                if (keeper != null) keeper.DestroyHandle();
            });
            hookThread = Loop("start key", delegate
            {
                hookId = GetCurrentThreadId();
                proc = OnKey;
                Hook();
            }, delegate (Native.MSG m) { if (m.message == RehookMsg) Hook(); }, delegate
            {
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            });
        }

        public void Stop()
        {
            if (hookThread == null) return;
            PostThreadMessage(hookId, 0x12, IntPtr.Zero, IntPtr.Zero);
            PostThreadMessage(helpId, 0x12, IntPtr.Zero, IntPtr.Zero);
            hookThread.Join(1000);
            helpThread.Join(1000);
            hookThread = helpThread = null;
        }

        // A thread with its own message loop
        static Thread Loop(string name, Action begin, Action<Native.MSG> own, Action end)
        {
            var ready = new ManualResetEvent(false);
            var t = new Thread(delegate ()
            {
                begin();
                ready.Set();
                Native.MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.hwnd == IntPtr.Zero && m.message >= 0x8000) own(m);
                    else DispatchMessage(ref m);
                }
                end();
            }) { IsBackground = true, Name = name };
            t.Start();
            ready.WaitOne(2000);
            return t;
        }

        void Hook()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write("start key: hook " + Marshal.GetLastWin32Error());
        }

        IntPtr OnKey(int code, IntPtr msg, IntPtr info)
        {
            if (code < 0) return CallNextHookEx(hook, code, msg, info);
            var k = (KeyInfo)Marshal.PtrToStructure(info, typeof(KeyInfo));
            int m = msg.ToInt32();
            bool down = m == 0x100 || m == 0x104, up = m == 0x101 || m == 0x105;
            bool win = k.Vk == VkLWin || k.Vk == VkRWin;
            if (k.Extra == Mine) return CallNextHookEx(hook, code, msg, info);
            Interlocked.Exchange(ref keyTicks, DateTime.UtcNow.Ticks);
            // Held key repeating: already handled
            if (down && eaten != 0 && k.Vk == eaten) return (IntPtr)1;
            if (win && down)
            {
                if (!winDown)
                {
                    winDown = true;
                    used = passed = false;
                    PostThreadMessage(helpId, KeepMsg, IntPtr.Zero, IntPtr.Zero);
                }
            }
            else if (win && up && winDown)
            {
                winDown = false;
                // Mask first; release always passes through
                if (!passed) Tap(Mask);
                if (!used) Raise("start");
                Interlocked.Exchange(ref winUpTicks, DateTime.UtcNow.Ticks);
                PostThreadMessage(helpId, WinUpMsg, IntPtr.Zero, IntPtr.Zero);
            }
            else if (winDown && down)
            {
                used = true;
                // Plain Win+letter only; Win+Shift+S stays
                string what = Held(VkShift) || Held(VkCtrl) || Held(VkAlt) ? null : Letter(k.Vk);
                if (what != null)
                {
                    eaten = (int)k.Vk;
                    Raise(what);
                    return (IntPtr)1;
                }
                passed = true;
            }
            else if (!winDown && down && k.Vk == VkEsc && Held(VkCtrl) && !Held(VkShift) && !Held(VkAlt))
            {
                eaten = VkEsc;
                Raise("start");
                return (IntPtr)1;
            }
            else if (eaten != 0 && up && k.Vk == eaten)
            {
                eaten = 0;
                return (IntPtr)1;
            }
            return CallNextHookEx(hook, code, msg, info);
        }

        void OnHelpMessage(Native.MSG m)
        {
            if (m.message == KeepMsg) Keep();
            else if (m.message == WinUpMsg && kept) keeper.Later(UnkeepTimer, 600);
        }

        void OnTimer(int id)
        {
            if (id == UnkeepTimer) Unkeep();
            else if (id == ParkTimer) Park();
        }

        static bool IsDesktop(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            if (h == GetShellWindow()) return true;
            string cls = ClassOf(h);
            return cls == "Progman" || cls == "WorkerW";
        }

        // Empty desktop in front: Tepi holds the focus so Win reaches it
        void Park()
        {
            var fg = GetForegroundWindow();
            if (keeper == null || handedBack || !IsDesktop(fg)) return;
            // Menu, drag or rename on the desktop: wait
            uint pid, tid = GetWindowThreadProcessId(fg, out pid);
            var gi = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            bool busy = GetGUIThreadInfo(tid, ref gi) && ((gi.Flags & 0x1E) != 0 || gi.Capture != IntPtr.Zero || gi.Caret != IntPtr.Zero);
            if (busy || Held(0x01) || Held(0x02)) { keeper.Later(ParkTimer, 500); return; }
            desk = fg;
            keeper.Take();
            parked = GetForegroundWindow() == keeper.Handle;
        }

        // Any other key while parked: the desktop gets focus back, and the key
        bool OnKeeperKey(int msg, IntPtr w, IntPtr l)
        {
            int vk = w.ToInt32();
            if (!parked || vk == VkLWin || vk == VkRWin || vk == Mask || Held(VkLWin) || Held(VkRWin)) return false;
            parked = false;
            handedBack = true;
            if (!IsWindow(desk)) return false;
            SetForegroundWindow(desk);
            keeper.Rest();
            if (vk == VkShift || vk == VkCtrl || vk == VkAlt) return true;
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

        // Shell surfaces that drop simulated keys
        bool Shielded(IntPtr h)
        {
            if (h == IntPtr.Zero) { where = "none"; return true; }
            string cls = ClassOf(h), exe = ProcessName(h);
            where = exe + "/" + cls;
            if (h == GetShellWindow()) return true;
            if (string.Equals(exe, "explorer", StringComparison.OrdinalIgnoreCase)) return cls != "CabinetWClass" && cls != "#32770";
            foreach (var s in ShellHosts) if (string.Equals(exe, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string ClassOf(IntPtr h)
        {
            var c = new StringBuilder(64);
            GetClassName(h, c, 64);
            return c.ToString();
        }

        static string ProcessName(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            IntPtr p = OpenProcess(0x1000, false, pid);
            if (p == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(520);
                int size = sb.Capacity;
                return QueryFullProcessImageName(p, 0, sb, ref size) ? System.IO.Path.GetFileNameWithoutExtension(sb.ToString()) : "";
            }
            finally { CloseHandle(p); }
        }

        // Admin windows hide their keys from Tepi
        static bool Elevated(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            IntPtr p = OpenProcess(0x1000, false, pid), token;
            if (p == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(p, 0x8, out token)) return true;
                int on, ret;
                bool ok = GetTokenInformation(token, 20, out on, 4, out ret);
                CloseHandle(token);
                return ok && on != 0;
            }
            finally { CloseHandle(p); }
        }

        static double Since(ref long ticks)
        {
            long t = Interlocked.Read(ref ticks);
            return t == 0 ? double.PositiveInfinity : (DateTime.UtcNow.Ticks - t) / (double)TimeSpan.TicksPerSecond;
        }

        void Keep()
        {
            // Already released, or already holding
            if (!winDown || kept || parked || keeper == null) return;
            desk = GetForegroundWindow();
            if (!Shielded(desk)) return;
            keeper.Take();
            kept = true;
        }

        void OnForeground(IntPtr h, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || hwnd == IntPtr.Zero) return;
            if (keeper != null && hwnd == keeper.Handle)
            {
                if (IsDesktop(desk)) parked = true;
                return;
            }
            string exe = ProcessName(hwnd);
            if (string.Equals(exe, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) || string.Equals(exe, "SearchHost", StringComparison.OrdinalIgnoreCase))
            {
                StartCameUp();
                return;
            }
            lastFront = hwnd;
            parked = false;
            if (IsDesktop(hwnd))
            {
                if (keeper != null && !handedBack) keeper.Later(ParkTimer, 350);
            }
            else
            {
                handedBack = false;
                if (keeper != null && !kept) keeper.Rest();
            }
        }

        // Windows Start came up: steal focus so it closes, then open Tepi's
        void StartCameUp()
        {
            if (keeper != null)
            {
                if (!kept) desk = lastFront;
                keeper.Take();
                kept = true;
                keeper.Later(UnkeepTimer, 800);
            }
            Raise("search");
            bool keyed = Since(ref winUpTicks) < 1.5;
            string front = lastFront == IntPtr.Zero ? "none" : ProcessName(lastFront) + "/" + ClassOf(lastFront) + (Elevated(lastFront) ? " (admin)" : "");
            double quiet = Since(ref keyTicks);
            Log.Write("start key: Windows Start came up; front " + front + (keyed ? ", hook saw Win" : ", hook missed Win")
                      + ", last key " + (double.IsInfinity(quiet) ? "never" : (int)quiet + "s ago"));
            // Hook missed it: reinstall
            if (!keyed) PostThreadMessage(hookId, RehookMsg, IntPtr.Zero, IntPtr.Zero);
        }

        void Unkeep()
        {
            if (!kept) return;
            kept = false;
            if (GetForegroundWindow() != keeper.Handle) { if (!parked) keeper.Rest(); return; }
            // Focus unused: the desktop stays parked, anything else gets it back
            if (IsDesktop(desk)) { parked = true; return; }
            if (IsWindow(desk)) SetForegroundWindow(desk);
            keeper.Rest();
        }

        static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        static string Letter(uint vk)
        {
            switch (vk)
            {
                case 0x53: return "search";
                case 0x41: return "quick";
                case 0x4E: return "cal";
                case 0x57: return "weather";
                case 0x58: return "menu";
                case 0x54: return "dockkeys";
            }
            return null;
        }

        static void Tap(ushort vk)
        {
            var keys = new[]
            {
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwExtraInfo = Mine } },
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 2, dwExtraInfo = Mine } },
            };
            SendInput(2, keys, Marshal.SizeOf(typeof(INPUT)));
        }

        void Raise(string what)
        {
            var h = Pressed;
            if (h != null) try { h(what); } catch (Exception ex) { Log.Write("start key: " + ex.Message); }
        }
    }
}
