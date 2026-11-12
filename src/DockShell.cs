using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Tepi
{
    // Shell lookups for the dock: icons, app ids, shortcuts
    static class ShellApp
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct PKey
        {
            public Guid Fmt;
            public int Pid;
            public PKey(string fmt, int pid) { Fmt = new Guid(fmt); Pid = pid; }
        }

        // Full 24 bytes: GetValue writes the whole union
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PVar
        {
            [FieldOffset(0)] public ushort Vt;
            [FieldOffset(8)] public IntPtr Ptr;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PKey key);
            [PreserveSig] int GetValue(ref PKey key, out PVar value);
            [PreserveSig] int SetValue(ref PKey key, ref PVar value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IImageFactory
        {
            [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr bitmap);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid handler, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(int form, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEnumShellItems
        {
            [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
            [PreserveSig] int Skip(uint count);
            [PreserveSig] int Reset();
            [PreserveSig] int Clone(out IEnumShellItems copy);
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, int flags);
            [PreserveSig] int GetIDList(out IntPtr pidl);
            [PreserveSig] int SetIDList(IntPtr pidl);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            [PreserveSig] int GetHotkey(out short key);
            [PreserveSig] int SetHotkey(short key);
            [PreserveSig] int GetShowCmd(out int cmd);
            [PreserveSig] int SetShowCmd(int cmd);
            [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
            [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            [PreserveSig] int Resolve(IntPtr hwnd, int flags);
            [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkObject { }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int Type, Width, Height, WidthBytes; public short Planes, BitsPixel; public IntPtr Bits; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid iid, out IImageFactory item);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid iid, out IShellItem item);
        [DllImport("shell32.dll", PreserveSig = true)]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PVar pv);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int size, out int needed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr obj, int size, out BITMAP bmp);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref Native.BITMAPINFOHEADER info, uint usage);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetKnownFolderPath(ref Guid id, int flags, IntPtr token, out IntPtr path);

        public static readonly PKey AppId = new PKey("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
        const string AppsFolder = @"shell:AppsFolder\";

        public static bool IsAppsFolder(string target) { return target.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase); }
        public static string AppsFolderOf(string aumid) { return AppsFolder + aumid; }
        public static string AumidOf(string target) { return IsAppsFolder(target) ? target.Substring(AppsFolder.Length) : ""; }

        // Explicit window id; empty when the app never set one
        public static string WindowAppId(IntPtr hwnd)
        {
            IPropStore store = null;
            try
            {
                var iid = typeof(IPropStore).GUID;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return "";
                return ReadString(store, AppId);
            }
            catch (Exception) { return ""; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        public static void SetWindowAppId(IntPtr hwnd, string id)
        {
            IPropStore store = null;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                var iid = typeof(IPropStore).GUID;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return;
                var key = AppId;
                ptr = Marshal.StringToCoTaskMemUni(id ?? "");
                var pv = new PVar { Vt = 31, Ptr = ptr };
                if (store.SetValue(ref key, ref pv) == 0) store.Commit();
            }
            catch (Exception) { }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        static string ReadString(IPropStore store, PKey key)
        {
            PVar v;
            if (store.GetValue(ref key, out v) != 0) return "";
            try { return v.Vt == 31 && v.Ptr != IntPtr.Zero ? Marshal.PtrToStringUni(v.Ptr) ?? "" : ""; }
            finally { PropVariantClear(ref v); }
        }

        // Packaged processes carry their app id
        public static string ProcessAppId(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                int len = 256;
                var sb = new StringBuilder(len);
                return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        // Elevated token: a process we may look at but not drive
        public static bool Elevated(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(h, 0x0008, out token)) return false;
                try
                {
                    int elevated, needed;
                    return GetTokenInformation(token, 20, out elevated, 4, out needed) && elevated != 0;
                }
                finally { CloseHandle(token); }
            }
            catch (Exception) { return false; }
            finally { CloseHandle(h); }
        }

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(520);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        // Shortcut target, arguments, icon and its app id
        public sealed class Link { public string Target = "", Args = "", AppId = "", IconPath = ""; public int IconIndex; }

        public static Link ReadLink(string lnk)
        {
            var result = new Link();
            object o = null;
            try
            {
                o = new ShellLinkObject();
                ((IPersistFile)o).Load(lnk, 0);
                var sl = (IShellLinkW)o;
                var sb = new StringBuilder(520);
                if (sl.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) == 0) result.Target = sb.ToString();
                sb.Length = 0;
                if (sl.GetArguments(sb, sb.Capacity) == 0) result.Args = sb.ToString();
                sb.Length = 0;
                int index;
                if (sl.GetIconLocation(sb, sb.Capacity, out index) == 0) { result.IconPath = sb.ToString(); result.IconIndex = index; }
                var store = o as IPropStore;
                if (store != null) result.AppId = ReadString(store, AppId);
            }
            catch (Exception ex) { Log.Write("dock link " + Path.GetFileName(lnk) + ": " + ex.Message); }
            finally { if (o != null) Marshal.ReleaseComObject(o); }
            return result;
        }

        // Start menu entry for the running exe: Search and All apps list Tepi with its icon
        public static void EnsureStartEntry()
        {
            try
            {
                // Test runs and copies in temp folders leave the menu alone
                if (Environment.GetEnvironmentVariable("TEPI_DIR") != null) return;
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                if (exe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tepi.lnk");
                if (File.Exists(lnk) && string.Equals(ReadLink(lnk).Target, exe, StringComparison.OrdinalIgnoreCase)) return;
                object o = new ShellLinkObject();
                try
                {
                    var sl = (IShellLinkW)o;
                    sl.SetPath(exe);
                    sl.SetWorkingDirectory(Path.GetDirectoryName(exe));
                    sl.SetDescription("Tepi");
                    sl.SetIconLocation(exe, 0);
                    ((IPersistFile)o).Save(lnk, true);
                }
                finally { Marshal.ReleaseComObject(o); }
            }
            catch (Exception ex) { Log.Write("start entry: " + ex.Message); }
        }

        // Start menu name of an item
        public static string DisplayName(string item)
        {
            IShellItem si = null;
            IntPtr name = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(item, IntPtr.Zero, ref iid, out si) != 0 || si == null) return "";
                if (si.GetDisplayName(0, out name) != 0 || name == IntPtr.Zero) return "";
                return Marshal.PtrToStringUni(name) ?? "";
            }
            catch (Exception) { return ""; }
            finally
            {
                if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name);
                if (si != null) Marshal.ReleaseComObject(si);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, int n, int flags);

        // Every app Start lists under All apps: name and its AppsFolder id
        public static List<KeyValuePair<string, string>> AllApps()
        {
            var list = new List<KeyValuePair<string, string>>();
            IShellItem folder = null;
            IEnumShellItems items = null;
            IntPtr raw = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out folder) != 0 || folder == null) return list;
                var enumItems = new Guid("94f60519-2850-4924-aa5a-d15e84868039");
                var eid = typeof(IEnumShellItems).GUID;
                if (folder.BindToHandler(IntPtr.Zero, ref enumItems, ref eid, out raw) != 0 || raw == IntPtr.Zero) return list;
                items = (IEnumShellItems)Marshal.GetObjectForIUnknown(raw);
                IShellItem item;
                uint got;
                while (items.Next(1, out item, out got) == 0 && got == 1 && item != null)
                {
                    try
                    {
                        string name = NameOf(item, 0), id = NameOf(item, unchecked((int)0x80018001));
                        if (name.Length > 0 && id.Length > 0) list.Add(new KeyValuePair<string, string>(name, id));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            catch (Exception ex) { Log.Write("dock all apps: " + ex.Message); }
            finally
            {
                if (items != null) Marshal.ReleaseComObject(items);
                if (raw != IntPtr.Zero) Marshal.Release(raw);
                if (folder != null) Marshal.ReleaseComObject(folder);
            }
            return list;
        }

        static string NameOf(IShellItem item, int form)
        {
            IntPtr p;
            if (item.GetDisplayName(form, out p) != 0 || p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // Exe, shortcut or shell:AppsFolder item at px
        public static Bitmap Icon(string item, int px)
        {
            if (string.IsNullOrEmpty(item)) return null;
            // Files first: straight from their icon resource, so no shell
            // overlay (compressed, cloud) lands on top
            if (item.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var b = LinkIcon(item, px);
                if (b != null) return b;
            }
            else if ((item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || item.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) && File.Exists(item))
            {
                var b = FileIcon(item, 0, px);
                if (b != null) return b;
            }
            IImageFactory f = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                var iid = typeof(IImageFactory).GUID;
                if (SHCreateItemFromParsingName(item, IntPtr.Zero, ref iid, out f) != 0 || f == null) return null;
                // Icon only: no document thumbnails
                if (f.GetImage(new Native.SIZE(px, px), 0x4, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                return FromHbitmap(hbmp);
            }
            catch (Exception) { return null; }
            finally
            {
                if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        // A shortcut's own icon, else its target's
        static Bitmap LinkIcon(string lnk, int px)
        {
            var l = ReadLink(lnk);
            if (l.IconPath.Length > 0)
            {
                var b = FileIcon(Environment.ExpandEnvironmentVariables(l.IconPath), l.IconIndex, px);
                if (b != null) return b;
            }
            return l.Target.Length > 0 && File.Exists(l.Target) ? FileIcon(l.Target, 0, px) : null;
        }

        static Bitmap FileIcon(string file, int index, int px)
        {
            if (!File.Exists(file)) return null;
            var arr = new IntPtr[1];
            var ids = new int[1];
            if (PrivateExtractIcons(file, index, px, px, arr, ids, 1, 0) <= 0 || arr[0] == IntPtr.Zero) return null;
            try
            {
                using (var ic = System.Drawing.Icon.FromHandle(arr[0]))
                using (var raw = ic.ToBitmap())
                {
                    var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(raw, 0, 0);
                    return bmp;
                }
            }
            catch (Exception) { return null; }
            finally { Native.DestroyIcon(arr[0]); }
        }

        // Shell bitmaps keep alpha; FromHbitmap would drop it
        static Bitmap FromHbitmap(IntPtr hbmp)
        {
            BITMAP bm;
            if (GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0 || bm.Width <= 0 || bm.Height <= 0) return null;
            int w = bm.Width, h = bm.Height;
            var info = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            int lines = GetDIBits(dc, hbmp, 0, (uint)h, px, ref info, 0);
            Native.ReleaseDC(IntPtr.Zero, dc);
            if (lines == 0) return null;
            bool alpha = false, straight = false;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) alpha = true;
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a) straight = true;
            }
            // No alpha at all: an opaque picture
            if (!alpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var bmp = new Bitmap(w, h, alpha && !straight ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            if (bmp.PixelFormat == PixelFormat.Format32bppPArgb) return bmp;
            // Draw paths expect premultiplied
            var pre = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(pre)) g.DrawImageUnscaled(bmp, 0, 0);
            bmp.Dispose();
            return pre;
        }

        // {known folder}\rest, as NotifyIconSettings stores paths
        public static string ExpandKnown(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '{') return path ?? "";
            int close = path.IndexOf('}');
            if (close < 0) return path;
            Guid id;
            if (!Guid.TryParse(path.Substring(0, close + 1), out id)) return path;
            IntPtr p;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) != 0) return path;
            try { return Marshal.PtrToStringUni(p) + path.Substring(close + 1); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // Pins: shortcut, exe, or shell:AppsFolder\id
        public static void Launch(string target, bool admin)
        {
            if (string.IsNullOrEmpty(target)) return;
            try
            {
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                if (admin) psi.Verb = "runas";
                if (!IsAppsFolder(target) && File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
                // The dock never takes focus, so pass ours on: the new window opens in front
                AllowSetForegroundWindow(-1);
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223: the UAC prompt was cancelled
                if (ex.NativeErrorCode != 1223) Log.Write("dock launch " + target + ": " + ex.Message);
            }
            catch (Exception ex) { Log.Write("dock launch " + target + ": " + ex.Message); }
        }

        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        // A click on the dock counts as our input, so focus is ours to give
        public static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindowAsync(h, 9);
            if (SetForegroundWindow(h)) return;
            // Foreground lock: join the front window's input for a moment
            uint mine = GetCurrentThreadId(), theirs = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            bool joined = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
            BringWindowToTop(h);
            bool ok = SetForegroundWindow(h);
            if (joined) AttachThreadInput(mine, theirs, false);
            if (!ok) SwitchToThisWindow(h, true);
        }

        // True when the window belongs to a process we may not drive: UIPI drops every
        // ShowWindow and SetForegroundWindow we send it, so its window stays put
        public static bool OutOfReach(IntPtr h)
        {
            if (!IsIconic(h)) return false;
            uint pid = GetWindowThreadProcessId(h, IntPtr.Zero);
            return pid != 0 && Elevated((int)pid) && !Elevated(GetCurrentProcessId());
        }

        // The app owns a window we cannot move: starting it again raises that same window
        public static void Relaunch(IntPtr h)
        {
            uint pid = GetWindowThreadProcessId(h, IntPtr.Zero);
            string exe = ProcessPath((int)pid);
            if (exe.Length == 0) return;
            try
            {
                AllowSetForegroundWindow(-1);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
            catch (Exception ex) { Log.Write("relaunch " + exe + ": " + ex.Message); }
        }

        public static void Close(IntPtr h) { PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); }
    }
}
