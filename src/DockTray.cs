using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Tepi
{
    // One notification area icon, owned by its app
    sealed class TrayIcon
    {
        public IntPtr Hwnd, Hicon;
        public uint Uid, Callback, Version;
        public Guid Guid;
        public string Tip = "", Exe = "";
        public bool Hidden, ShowTip;
        public int Pid;
        public long Stamp;
        // UI thread: drawn copy, its stamp, and the spot on screen
        public Bitmap Bmp;
        public long BmpStamp = -1;
        public Rectangle Spot;

        public string Key { get { return Guid != Guid.Empty ? Guid.ToString() : Hwnd.ToInt64() + ":" + Uid; } }
    }

    // Takes Explorer's place for tray icons while the dock replaces the taskbar.
    // Every message still reaches Explorer, so nothing is lost when Tepi quits.
    sealed class TrayHost
    {
        delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string cls, IntPtr inst);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out Native.MSG msg, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Native.MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Native.MSG msg);
        [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, int flags, int ms, out IntPtr result);
        [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, int ms, IntPtr proc);
        [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        const uint WM_COPYDATA = 0x4A, WM_TIMER = 0x113, WM_DESTROY = 0x2;
        const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_STATE = 8, NIF_GUID = 0x20;
        // Shell_NotifyIcon payload: signature, message, 32-bit NOTIFYICONDATA
        const int TrayDataSize = 964, Signature = 0x34753423;

        public static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        static readonly uint StopMsg = RegisterWindowMessage("TepiTrayStop");

        public event Action Changed;
        readonly object gate = new object();
        readonly List<TrayIcon> icons = new List<TrayIcon>();
        Thread thread;
        IntPtr hwnd, notify, explorer;
        WndProc proc;
        long stamp;
        Rectangle anchor;
        DateTime sentUtc = DateTime.MinValue, lostUtc = DateTime.MinValue, sweepUtc = DateTime.MinValue;
        readonly ManualResetEvent ready = new ManualResetEvent(false);

        public bool Running { get { return thread != null; } }

        // Our own TaskbarCreated echo, not an Explorer restart
        public bool JustBroadcast { get { return (DateTime.UtcNow - sentUtc).TotalSeconds < 3; } }

        public void Start(Rectangle area)
        {
            if (thread != null) return;
            anchor = area;
            ready.Reset();
            thread = new Thread(Run) { IsBackground = true, Name = "tray" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.WaitOne(2000);
        }

        public void Stop()
        {
            var t = thread;
            if (t == null) return;
            thread = null;
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, StopMsg, IntPtr.Zero, IntPtr.Zero);
            t.Join(2000);
            // Apps add their icons back to Explorer's tray
            Broadcast();
        }

        // Tray area on screen: apps place their flyouts from it
        public void SetArea(Rectangle area)
        {
            if (area == anchor) return;
            anchor = area;
            var h = hwnd;
            if (h != IntPtr.Zero) PostMessage(h, StopMsg, (IntPtr)1, IntPtr.Zero);
        }

        public List<TrayIcon> Snapshot()
        {
            lock (gate) return new List<TrayIcon>(icons);
        }

        // UI thread: a drawable copy of the current icon
        public Bitmap Picture(TrayIcon ic)
        {
            lock (gate)
            {
                if (ic.BmpStamp == ic.Stamp) return ic.Bmp;
                if (ic.Bmp != null) { ic.Bmp.Dispose(); ic.Bmp = null; }
                ic.BmpStamp = ic.Stamp;
                if (ic.Hicon == IntPtr.Zero) return null;
                try { using (var i = Icon.FromHandle(ic.Hicon)) ic.Bmp = i.ToBitmap(); }
                catch (Exception) { ic.Bmp = null; }
                return ic.Bmp;
            }
        }

        // An icon whose app died without saying so
        public bool Gone(TrayIcon ic)
        {
            if (IsWindow(ic.Hwnd)) return false;
            lock (gate) { icons.Remove(ic); Free(ic); }
            Raise();
            return true;
        }

        void Run()
        {
            try
            {
                proc = WindowProc;
                IntPtr inst = GetModuleHandle(null);
                var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)), style = 8, lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, lpszClassName = "Shell_TrayWnd" };
                RegisterClassEx(ref wc);
                var wn = new WNDCLASSEX { cbSize = wc.cbSize, lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"), hInstance = inst, lpszClassName = "TrayNotifyWnd" };
                RegisterClassEx(ref wn);
                var a = anchor;
                // Never shown; topmost so FindWindow meets it before Explorer's
                hwnd = CreateWindowEx(0x8 | 0x80, "Shell_TrayWnd", "", unchecked((int)0x80000000) | 0x06000000, a.X, a.Y, Math.Max(1, a.Width), Math.Max(1, a.Height), IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) { Log.Write("tray: window failed " + Marshal.GetLastWin32Error()); return; }
                notify = CreateWindowEx(0, "TrayNotifyWnd", null, 0x40000000 | 0x06000000, 0, 0, Math.Max(1, a.Width), Math.Max(1, a.Height), hwnd, IntPtr.Zero, inst, IntPtr.Zero);
                explorer = FindExplorer();
                Topmost();
                SetTimer(hwnd, (IntPtr)1, 250, IntPtr.Zero);
                ready.Set();
                Broadcast();
                Native.MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex) { Log.Write("tray: " + ex); }
            finally
            {
                if (hwnd != IntPtr.Zero) { KillTimer(hwnd, (IntPtr)1); DestroyWindow(hwnd); }
                hwnd = notify = IntPtr.Zero;
                IntPtr inst = GetModuleHandle(null);
                UnregisterClass("TrayNotifyWnd", inst);
                UnregisterClass("Shell_TrayWnd", inst);
                lock (gate) { foreach (var ic in icons) Free(ic); icons.Clear(); }
                ready.Set();
            }
        }

        IntPtr WindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (msg == WM_COPYDATA)
                {
                    var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(l, typeof(COPYDATASTRUCT));
                    long kind = cds.dwData.ToInt64();
                    if (kind == 1) return Notify(msg, w, l, cds);
                    IntPtr spot;
                    if (kind == 3 && IconSpot(cds, out spot)) return spot;
                    return Forward(msg, w, l);
                }
                if (msg == StopMsg)
                {
                    if (w == IntPtr.Zero) DestroyWindow(h);
                    else Place();
                    return IntPtr.Zero;
                }
                if (msg == WM_TIMER) { Guard(); return IntPtr.Zero; }
                // Closed only by Tepi itself, never by a stray WM_CLOSE
                if (msg == 0x10) return IntPtr.Zero;
                if (msg == WM_DESTROY) { if (h == hwnd) PostQuitMessage(0); return IntPtr.Zero; }
                // Explorer's own traffic goes on to Explorer
                if (msg >= 0x400 || msg == 0x111 || msg == 0x1C) return Forward(msg, w, l);
            }
            catch (Exception ex) { Log.Write("tray proc: " + ex.Message); }
            return DefWindowProc(h, msg, w, l);
        }

        void Place()
        {
            var a = anchor;
            if (hwnd == IntPtr.Zero) return;
            MoveWindow(hwnd, a.X, a.Y, Math.Max(1, a.Width), Math.Max(1, a.Height), false);
            if (notify != IntPtr.Zero) MoveWindow(notify, 0, 0, Math.Max(1, a.Width), Math.Max(1, a.Height), false);
        }

        IntPtr FindExplorer()
        {
            for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
                if (h != hwnd) return h;
            return IntPtr.Zero;
        }

        IntPtr Forward(uint msg, IntPtr w, IntPtr l)
        {
            if (explorer == IntPtr.Zero || !IsWindow(explorer)) explorer = FindExplorer();
            if (explorer == IntPtr.Zero) return IntPtr.Zero;
            // Explorer posts this one to itself
            if (msg == 0x574) { PostMessage(explorer, msg, w, l); return IntPtr.Zero; }
            IntPtr result;
            SendMessageTimeout(explorer, msg, w, l, 2, 3000, out result);
            return result;
        }

        void Topmost() { Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); }

        // Explorer can rise above us; take the spot back and ask apps again
        void Guard()
        {
            var now = DateTime.UtcNow;
            if (FindWindow("Shell_TrayWnd", null) != hwnd)
            {
                Topmost();
                if (lostUtc == DateTime.MinValue) lostUtc = now;
            }
            else if (lostUtc != DateTime.MinValue && (now - lostUtc).TotalSeconds > 1)
            {
                lostUtc = DateTime.MinValue;
                if ((now - sentUtc).TotalSeconds > 60) Broadcast();
            }
            if ((now - sweepUtc).TotalSeconds > 5)
            {
                sweepUtc = now;
                bool dropped = false;
                lock (gate)
                    for (int i = icons.Count - 1; i >= 0; i--)
                        if (!IsWindow(icons[i].Hwnd)) { Free(icons[i]); icons.RemoveAt(i); dropped = true; }
                if (dropped) Raise();
            }
        }

        // After an Explorer restart its tray sits on top again
        public void Resync()
        {
            var h = hwnd;
            if (h == IntPtr.Zero) return;
            lostUtc = DateTime.UtcNow.AddSeconds(-2);
            sentUtc = DateTime.MinValue;
        }

        void Broadcast()
        {
            sentUtc = DateTime.UtcNow;
            SendNotifyMessage((IntPtr)0xFFFF, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
        }

        static string Str(byte[] b, int at, int chars)
        {
            int n = 0;
            while (n < chars && at + n * 2 + 1 < b.Length && (b[at + n * 2] != 0 || b[at + n * 2 + 1] != 0)) n++;
            return Encoding.Unicode.GetString(b, at, n * 2);
        }

        // Handles are 32-bit on the wire; sign-extend them
        static IntPtr Handle32(byte[] b, int at) { return new IntPtr(BitConverter.ToInt32(b, at)); }

        IntPtr Notify(uint msg, IntPtr w, IntPtr l, COPYDATASTRUCT cds)
        {
            int n = Math.Min(cds.cbData, TrayDataSize);
            if (n < 32 || cds.lpData == IntPtr.Zero) return Forward(msg, w, l);
            var b = new byte[TrayDataSize];
            Marshal.Copy(cds.lpData, b, 0, n);
            if (BitConverter.ToInt32(b, 0) != Signature) return Forward(msg, w, l);
            uint message = BitConverter.ToUInt32(b, 4), flags = BitConverter.ToUInt32(b, 20);
            IntPtr owner = Handle32(b, 12), hicon = Handle32(b, 28);
            uint uid = BitConverter.ToUInt32(b, 16), callback = BitConverter.ToUInt32(b, 24);
            string tip = Str(b, 32, 128);
            uint state = BitConverter.ToUInt32(b, 288), mask = BitConverter.ToUInt32(b, 292), version = BitConverter.ToUInt32(b, 808);
            var guidBytes = new byte[16];
            Array.Copy(b, 944, guidBytes, 0, 16);
            var guid = n >= 960 ? new Guid(guidBytes) : Guid.Empty;
            bool changed;
            bool ok = Apply(message, owner, uid, flags, callback, hicon, tip, state, mask, version, guid, out changed);
            Forward(msg, w, l);
            if (changed) Raise();
            return ok ? (IntPtr)1 : IntPtr.Zero;
        }

        bool Apply(uint message, IntPtr owner, uint uid, uint flags, uint callback, IntPtr hicon, string tip, uint state, uint mask, uint version, Guid guid, out bool changed)
        {
            changed = false;
            lock (gate)
            {
                bool byGuid = (flags & NIF_GUID) != 0 && guid != Guid.Empty;
                int i = icons.FindIndex(x => byGuid ? x.Guid == guid : x.Hwnd == owner && x.Uid == uid);
                switch (message)
                {
                    case NIM_ADD:
                    case NIM_MODIFY:
                        bool fresh = i < 0;
                        TrayIcon ic;
                        if (fresh)
                        {
                            if (owner == IntPtr.Zero || !IsWindow(owner)) return false;
                            ic = new TrayIcon { Hwnd = owner, Uid = uid };
                            GetWindowThreadProcessId(owner, out ic.Pid);
                            ic.Exe = ShellApp.ProcessPath(ic.Pid);
                            icons.Add(ic);
                        }
                        else ic = icons[i];
                        if ((flags & NIF_MESSAGE) != 0) ic.Callback = callback;
                        if ((flags & NIF_ICON) != 0)
                        {
                            // The app may destroy its handle as soon as we return
                            IntPtr copy = hicon != IntPtr.Zero ? CopyIcon(hicon) : IntPtr.Zero;
                            if (ic.Hicon != IntPtr.Zero) Native.DestroyIcon(ic.Hicon);
                            ic.Hicon = copy;
                        }
                        if ((flags & NIF_TIP) != 0) ic.Tip = tip;
                        // Version 4 hides the plain tooltip unless asked
                        if ((flags & 0x80) != 0) ic.ShowTip = true;
                        else if ((flags & NIF_TIP) != 0) ic.ShowTip = false;
                        if ((flags & NIF_STATE) != 0 && (mask & 1) != 0) ic.Hidden = (state & 1) != 0;
                        if (byGuid) ic.Guid = guid;
                        ic.Stamp = ++stamp;
                        changed = true;
                        // Modify for an icon we never had: kept, but the app hears no
                        return !(fresh && message == NIM_MODIFY);
                    case NIM_DELETE:
                        if (i < 0) return false;
                        Free(icons[i]);
                        icons.RemoveAt(i);
                        changed = true;
                        return true;
                    case NIM_SETVERSION:
                        if (i < 0 || version > 4) return false;
                        icons[i].Version = version;
                        return true;
                    case NIM_SETFOCUS:
                        return true;
                }
            }
            return false;
        }

        // Shell_NotifyIconGetRect: corners of the icon as we drew it
        bool IconSpot(COPYDATASTRUCT cds, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (cds.cbData < 40 || cds.lpData == IntPtr.Zero) return false;
            var b = new byte[40];
            Marshal.Copy(cds.lpData, b, 0, 40);
            int which = BitConverter.ToInt32(b, 4);
            IntPtr owner = Handle32(b, 16);
            uint uid = BitConverter.ToUInt32(b, 20);
            var gb = new byte[16];
            Array.Copy(b, 24, gb, 0, 16);
            var guid = new Guid(gb);
            Rectangle r = Rectangle.Empty;
            lock (gate)
                foreach (var ic in icons)
                    if ((guid != Guid.Empty && ic.Guid == guid) || (ic.Hwnd == owner && ic.Uid == uid)) { r = ic.Spot; break; }
            if (r.IsEmpty) r = anchor;
            if (r.IsEmpty) return false;
            int x = which == 2 ? r.Right : r.Left, y = which == 2 ? r.Bottom : r.Top;
            result = (IntPtr)((y << 16) | (x & 0xFFFF));
            return true;
        }

        void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        static void Free(TrayIcon ic)
        {
            if (ic.Hicon != IntPtr.Zero) { Native.DestroyIcon(ic.Hicon); ic.Hicon = IntPtr.Zero; }
        }

        // Mouse traffic in the shape each icon version expects
        public static void Send(TrayIcon ic, uint mouse, Point at)
        {
            if (ic.Callback == 0) return;
            long w, l;
            if (ic.Version > 3) { w = (uint)((at.Y << 16) | (at.X & 0xFFFF)); l = mouse | ((long)ic.Uid << 16); }
            else { w = ic.Uid; l = mouse; }
            SendNotifyMessage(ic.Hwnd, ic.Callback, new IntPtr(w), new IntPtr(l & 0xFFFFFFFFL));
        }

        public static void Down(TrayIcon ic, uint mouse, Point at)
        {
            // The app's menu needs the foreground; we hold it for this click
            AllowSetForegroundWindow(ic.Pid);
            Send(ic, mouse, at);
        }
    }

    // Which icons Windows shows on the taskbar, per Settings › Taskbar
    static class TrayPromo
    {
        sealed class Entry { public string Exe = ""; public long Uid = -1; public int Promoted = -1; }
        static List<Entry> entries = new List<Entry>();
        static DateTime readUtc = DateTime.MinValue;

        public static void Invalidate() { readUtc = DateTime.MinValue; }

        public static bool Promoted(TrayIcon ic)
        {
            if ((DateTime.UtcNow - readUtc).TotalSeconds > 10) Read();
            Entry any = null;
            foreach (var e in entries)
            {
                if (!string.Equals(e.Exe, ic.Exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Uid >= 0 && e.Uid == ic.Uid) return e.Promoted == 1;
                if (any == null) any = e;
            }
            return any != null && any.Promoted == 1;
        }

        static void Read()
        {
            readUtc = DateTime.UtcNow;
            var list = new List<Entry>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                    if (root != null)
                        foreach (var name in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                var e = new Entry { Exe = ShellApp.ExpandKnown(k.GetValue("ExecutablePath") as string ?? "") };
                                object uid = k.GetValue("UID"), pro = k.GetValue("IsPromoted");
                                if (uid is int) e.Uid = (uint)(int)uid;
                                if (pro is int) e.Promoted = (int)pro;
                                list.Add(e);
                            }
            }
            catch (Exception ex) { Log.Write("tray promo: " + ex.Message); }
            entries = list;
        }
    }
}
