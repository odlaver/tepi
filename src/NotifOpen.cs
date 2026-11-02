using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace Tepi
{
    // Click on a message: hand Windows' own toast click to the app
    static class NotifOpen
    {
        [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface INotificationActivationCallback
        {
            void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, uint count);
        }

        [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);

        static readonly Dictionary<string, Guid?> activators = new Dictionary<string, Guid?>();
        // Schemes a toast must not reach through Tepi
        static readonly string[] BlockedSchemes = { "file", "ms-msdt", "search-ms", "search", "shell", "javascript", "vbscript", "ms-officecmd", "ms-appinstaller" };

        // True when the app got the click itself
        public static bool Open(NotifItem item)
        {
            string launch, activation;
            if (!Payload(item.Id, out launch, out activation) || string.IsNullOrEmpty(launch)) return false;
            if (activation == "protocol") return OpenUri(launch);
            var clsid = ActivatorFor(item.AppId);
            if (clsid == null) return false;
            object obj = null;
            try
            {
                obj = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid.Value));
                // The app may take the foreground, as from the Action Center
                AllowSetForegroundWindow(-1);
                ((INotificationActivationCallback)obj).Activate(item.AppId, launch, IntPtr.Zero, 0);
                return true;
            }
            catch (Exception ex) { Log.Write("notif activate: " + ex.Message); return false; }
            finally { if (obj != null) Marshal.ReleaseComObject(obj); }
        }

        static bool OpenUri(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u) || Array.IndexOf(BlockedSchemes, u.Scheme.ToLowerInvariant()) >= 0) return false;
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); return true; }
            catch (Exception ex) { Log.Write("notif uri: " + ex.Message); return false; }
        }

        // launch and activationType of the stored toast
        static bool Payload(uint id, out string launch, out string activation)
        {
            launch = activation = null;
            IntPtr db;
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(Path.Combine(NotifSource.Dir, "wpndatabase.db") + "\0"), out db, 1, IntPtr.Zero) != 0)
            {
                if (db != IntPtr.Zero) sqlite3_close(db);
                return false;
            }
            try
            {
                sqlite3_busy_timeout(db, 300);
                IntPtr stmt;
                if (sqlite3_prepare16_v2(db, "select Payload from Notification where Id = ?", -1, out stmt, IntPtr.Zero) != 0) return false;
                try
                {
                    sqlite3_bind_int64(stmt, 1, id);
                    if (sqlite3_step(stmt) != 100) return false;
                    int n = sqlite3_column_bytes(stmt, 0);
                    if (n <= 0 || n > 256 * 1024) return false;
                    var bytes = new byte[n];
                    Marshal.Copy(sqlite3_column_blob(stmt, 0), bytes, 0, n);
                    var doc = new XmlDocument { XmlResolver = null };
                    doc.LoadXml(Encoding.UTF8.GetString(bytes));
                    var toast = doc.DocumentElement;
                    if (toast == null || toast.Name != "toast") return false;
                    launch = toast.GetAttribute("launch");
                    activation = toast.GetAttribute("activationType").ToLowerInvariant();
                    return true;
                }
                finally { sqlite3_finalize(stmt); }
            }
            catch (Exception ex) { Log.Write("notif payload: " + ex.Message); return false; }
            finally { sqlite3_close(db); }
        }

        // Win32 apps: registry; Store apps: their manifest
        static Guid? ActivatorFor(string aumid)
        {
            lock (activators)
            {
                Guid? found;
                if (activators.TryGetValue(aumid, out found)) return found;
                found = null;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + aumid))
                    {
                        Guid g;
                        if (k != null && Guid.TryParse(Convert.ToString(k.GetValue("CustomActivator")), out g)) found = g;
                    }
                    if (found == null && aumid.Contains("!")) found = FromManifest(aumid.Substring(0, aumid.IndexOf('!')));
                }
                catch (Exception ex) { Log.Write("notif activator: " + ex.Message); }
                activators[aumid] = found;
                return found;
            }
        }

        // Family Name_PublisherId; the main package ends in "__PublisherId"
        static Guid? FromManifest(string family)
        {
            int cut = family.LastIndexOf('_');
            if (cut <= 0) return null;
            string name = family.Substring(0, cut), publisher = family.Substring(cut + 1);
            using (var repo = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
            {
                if (repo == null) return null;
                foreach (var full in repo.GetSubKeyNames())
                {
                    if (!full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) || !full.EndsWith("__" + publisher, StringComparison.OrdinalIgnoreCase)) continue;
                    using (var pk = repo.OpenSubKey(full))
                    {
                        string root = pk != null ? Convert.ToString(pk.GetValue("PackageRootFolder")) : "";
                        string manifest = Path.Combine(root, "AppxManifest.xml");
                        if (root.Length == 0 || !File.Exists(manifest)) continue;
                        var m = Regex.Match(File.ReadAllText(manifest), "ToastActivatorCLSID=\"\\{?([0-9a-fA-F-]{36})\\}?\"");
                        Guid g;
                        if (m.Success && Guid.TryParse(m.Groups[1].Value, out g)) return g;
                    }
                }
            }
            return null;
        }
    }
}
