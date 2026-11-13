using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tepi
{
    // Low-level key hook on its own thread; the callback must stay tiny
    sealed class KeyThread
    {
        // True swallows the key
        public delegate bool Filter(uint vk, bool up);

        delegate IntPtr HookProc(int code, IntPtr msg, IntPtr info);

        [StructLayout(LayoutKind.Sequential)]
        struct KeyInfo { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr info);
        [DllImport("user32.dll")] static extern int GetMessage(out Native.MSG msg, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Native.MSG msg);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        const int RehookMsg = 0x8001, QuitMsg = 0x12;

        readonly Filter filter;
        readonly string name;
        Thread thread;
        uint threadId;
        IntPtr hook;
        HookProc proc;

        public KeyThread(string name, Filter filter) { this.name = name; this.filter = filter; }

        public bool Running { get { return thread != null; } }

        public void Start()
        {
            if (thread != null) return;
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate ()
            {
                threadId = GetCurrentThreadId();
                proc = OnKey;
                Hook();
                ready.Set();
                Native.MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.hwnd == IntPtr.Zero && m.message == RehookMsg) Hook();
                    else DispatchMessage(ref m);
                }
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }) { IsBackground = true, Name = name };
            thread.Start();
            ready.WaitOne(2000);
        }

        // Windows drops a hook that answers too slowly, without a word
        public void Rehook() { if (thread != null) PostThreadMessage(threadId, RehookMsg, IntPtr.Zero, IntPtr.Zero); }

        public void Stop()
        {
            if (thread == null) return;
            PostThreadMessage(threadId, QuitMsg, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1000);
            thread = null;
        }

        void Hook()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write(name + ": hook " + Marshal.GetLastWin32Error());
        }

        IntPtr OnKey(int code, IntPtr msg, IntPtr info)
        {
            if (code >= 0)
            {
                try
                {
                    var k = (KeyInfo)Marshal.PtrToStructure(info, typeof(KeyInfo));
                    // Our own Ctrl+V and dummy keys
                    if ((k.Flags & 0x10) == 0)
                    {
                        int m = msg.ToInt32();
                        if (filter(k.Vk, m == 0x0101 || m == 0x0105)) return (IntPtr)1;
                    }
                }
                catch (Exception ex) { Log.Write(name + ": " + ex.Message); }
            }
            return CallNextHookEx(hook, code, msg, info);
        }
    }
}
