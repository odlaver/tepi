using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Tepi
{
    class NotifItem
    {
        public uint Id;
        public string AppId = "", AppName = "", Title = "", Body = "";
        public DateTime Time;
        public bool Chat, Held;
        public Bitmap Logo;
    }

    // Notification centre; change key gates re-listing
    static class NotifSource
    {
        static readonly string[] ChatApps = { "whatsapp", "telegram", "teams", "discord", "slack", "signal", "messenger", "instagram",
                                              "yourphone", "phone link", "outlook", "windowscommunicationsapps", "thunderbird", "zoom", "line" };
        static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
        static readonly string[] ChatSites = { "whatsapp", "telegram", "discord", "slack", "messenger", "instagram", "mail.google", "outlook" };
        static readonly Dictionary<string, Bitmap> logos = new Dictionary<string, Bitmap>();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");

        public static UserNotificationListenerAccessStatus Access() { return UserNotificationListener.Current.GetAccessStatus(); }

        public static void RequestAccess(Action done)
        {
            var op = UserNotificationListener.Current.RequestAccessAsync();
            op.Completed = delegate { done(); };
        }

        public static List<NotifItem> Read()
        {
            var list = new List<NotifItem>();
            foreach (var n in MediaService.Await(UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast)))
            {
                var item = new NotifItem { Id = n.Id, Time = n.CreationTime.UtcDateTime };
                try
                {
                    item.AppId = n.AppInfo.AppUserModelId ?? "";
                    item.AppName = n.AppInfo.DisplayInfo.DisplayName ?? "";
                }
                catch (Exception) { }
                if (item.AppName.Length == 0) item.AppName = item.AppId;
                var texts = new List<string>();
                try
                {
                    var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding != null)
                        foreach (var t in binding.GetTextElements())
                        {
                            string s = Fmt.OneLine(t.Text, 300);
                            if (s.Length > 0) texts.Add(s);
                        }
                }
                catch (Exception ex) { Log.Write("notif text: " + ex.Message); }
                if (texts.Count > 0) item.Title = texts[0];
                if (texts.Count > 1) item.Body = string.Join(" · ", texts.GetRange(1, texts.Count - 1));
                item.Chat = IsChat(item.AppId + " " + item.AppName, string.Join(" ", texts));
                item.Logo = LogoFor(item.AppId, n.AppInfo);
                list.Add(item);
            }
            list.Sort((a, b) => b.Time.CompareTo(a.Time));
            return list;
        }

        public static void Remove(uint id) { UserNotificationListener.Current.RemoveNotification(id); }

        // Off: Windows drops the toast after ~6 s
        public static bool Kept(string appId)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + appId))
                    return k == null || Convert.ToInt32(k.GetValue("ShowInActionCenter", 1)) != 0;
            }
            catch (Exception) { return true; }
        }

        static bool IsChat(string app, string text)
        {
            app = app.ToLowerInvariant();
            foreach (var c in ChatApps) if (app.Contains(c)) return true;
            bool browser = false;
            foreach (var b in Browsers) if (app.Contains(b)) browser = true;
            if (!browser) return false;
            text = text.ToLowerInvariant();
            foreach (var s in ChatSites) if (text.Contains(s)) return true;
            return false;
        }

        static Bitmap LogoFor(string appId, AppInfo info)
        {
            lock (logos)
            {
                Bitmap cached;
                if (logos.TryGetValue(appId, out cached)) return cached;
            }
            Bitmap logo = null;
            try
            {
                var reference = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                using (var stream = MediaService.Await(reference.OpenReadAsync()))
                {
                    ulong size = stream.Size;
                    if (size > 0 && size < 2 * 1024 * 1024)
                        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                        {
                            uint n = MediaService.Await(reader.LoadAsync((uint)size));
                            var bytes = new byte[n];
                            reader.ReadBytes(bytes);
                            using (var ms = new MemoryStream(bytes))
                            using (var src = Image.FromStream(ms))
                                logo = Ink.Grayscale(src, 48);
                        }
                }
            }
            catch (Exception) { logo = null; }
            lock (logos) logos[appId] = logo;
            return logo;
        }

        [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
        static IntPtr db;

        // Changes on toast add/remove; null if unreadable
        public static string ChangeKey()
        {
            try
            {
                if (db == IntPtr.Zero)
                {
                    IntPtr h;
                    if (sqlite3_open_v2(Encoding.UTF8.GetBytes(Path.Combine(Dir, "wpndatabase.db") + "\0"), out h, 1, IntPtr.Zero) != 0)
                    {
                        if (h != IntPtr.Zero) sqlite3_close(h);
                        return null;
                    }
                    sqlite3_busy_timeout(h, 300);
                    db = h;
                }
                IntPtr stmt;
                if (sqlite3_prepare16_v2(db, "select count(*), coalesce(max(Id), 0) from Notification where Type = 'toast'", -1, out stmt, IntPtr.Zero) != 0)
                {
                    sqlite3_close(db);
                    db = IntPtr.Zero;
                    return null;
                }
                try
                {
                    if (sqlite3_step(stmt) != 100) return null;
                    return sqlite3_column_int64(stmt, 0) + ":" + sqlite3_column_int64(stmt, 1);
                }
                finally { sqlite3_finalize(stmt); }
            }
            catch (Exception ex) { Log.Write("notif key: " + ex.Message); return null; }
        }
    }

    // Folder events miss DB writes; key checked each second (~8 µs)
    class NotifWatcher
    {
        public event Action<UserNotificationListenerAccessStatus, List<NotifItem>> Updated;

        readonly AutoResetEvent wake = new AutoResetEvent(true);
        FileSystemWatcher watcher;
        string lastKey;
        volatile bool force = true, enabled = true;

        public void Start()
        {
            try
            {
                if (NotifSource.Access() == UserNotificationListenerAccessStatus.Unspecified)
                    NotifSource.RequestAccess(delegate { Refresh(); });
            }
            catch (Exception ex) { Log.Write("notif access: " + ex.Message); }
            if (Directory.Exists(NotifSource.Dir))
            {
                try
                {
                    watcher = new FileSystemWatcher(NotifSource.Dir, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
                    FileSystemEventHandler h = delegate { wake.Set(); };
                    watcher.Changed += h;
                    watcher.Created += h;
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) { Log.Write("notif watch: " + ex.Message); }
            }
            var t = new Thread(Run) { IsBackground = true, Name = "notif" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public bool Enabled { set { enabled = value; if (value) Refresh(); } }

        public void Refresh() { force = true; wake.Set(); }

        void Run()
        {
            DateTime nextFull = DateTime.MinValue;
            while (true)
            {
                if (wake.WaitOne(1000)) Thread.Sleep(350);
                if (!enabled) continue;
                try
                {
                    string key = NotifSource.ChangeKey();
                    bool changed = key == null ? DateTime.UtcNow >= nextFull : key != lastKey;
                    if (!force && !changed) continue;
                    force = false;
                    var access = NotifSource.Access();
                    List<NotifItem> list = access == UserNotificationListenerAccessStatus.Allowed ? NotifSource.Read() : null;
                    // Listing rewrites the DB; keep prior key
                    lastKey = key;
                    nextFull = DateTime.UtcNow.AddSeconds(20);
                    var h = Updated;
                    if (h != null) h(access, list);
                }
                catch (Exception ex) { Log.Write("notif read: " + ex.Message); }
            }
        }
    }
}
