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
        // Colour work done once per item and theme
        public Color Brand, BrandInk;
        public bool BrandSet;
        public int InkKey;
    }

    // Notification centre; change key gates re-listing
    static class NotifSource
    {
        // Short names (4 or less) match whole words
        static readonly string[] ChatApps = { "discord", "instagram", "line", "messenger", "outlook", "phone link", "signal", "slack", "teams",
                                              "telegram", "thunderbird", "whatsapp", "windowscommunicationsapps", "yourphone", "zoom" };
        static readonly string[] Browsers = { "brave", "chrome", "firefox", "msedge", "opera", "vivaldi" };
        // Web apps that toast through a browser
        static readonly string[] ChatSites = { "discord", "instagram", "mail.google", "messenger", "outlook", "slack", "telegram", "whatsapp" };
        static readonly char[] WordBreaks = { ' ', '.', '_', '!', '-', '\\', '/' };
        static readonly Dictionary<string, Bitmap> logoByApp = new Dictionary<string, Bitmap>();
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");

        public static UserNotificationListenerAccessStatus Access() { return UserNotificationListener.Current.GetAccessStatus(); }

        public static void RequestAccess(Action done)
        {
            UserNotificationListener.Current.RequestAccessAsync().Completed = (op, status) => done();
        }

        // Toasts in the centre, newest first
        public static List<NotifItem> Read()
        {
            var toasts = WinRt.Get(UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast));
            var items = new List<NotifItem>(toasts.Count);
            foreach (var n in toasts) items.Add(Describe(n));
            items.Sort(NewerFirst);
            return items;
        }

        static int NewerFirst(NotifItem x, NotifItem y) { return y.Time.CompareTo(x.Time); }

        static NotifItem Describe(UserNotification n)
        {
            var item = new NotifItem { Id = n.Id, Time = n.CreationTime.UtcDateTime };
            // Some desktop apps carry no display info
            try
            {
                item.AppId = n.AppInfo.AppUserModelId ?? "";
                item.AppName = n.AppInfo.DisplayInfo.DisplayName ?? "";
            }
            catch (Exception) { }
            if (item.AppName == "") item.AppName = item.AppId;
            var lines = TextOf(n);
            if (lines.Count > 0) item.Title = lines[0];
            if (lines.Count > 1) item.Body = string.Join(" · ", lines.ToArray(), 1, lines.Count - 1);
            item.Chat = IsChat(item.AppId + " " + item.AppName, string.Join(" ", lines));
            item.Logo = LogoFor(item.AppId, n.AppInfo);
            return item;
        }

        // Non-empty text elements, one line each
        static List<string> TextOf(UserNotification n)
        {
            var lines = new List<string>();
            try
            {
                var generic = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (generic == null) return lines;
                foreach (var el in generic.GetTextElements())
                {
                    string one = Fmt.OneLine(el.Text, 300);
                    if (one != "") lines.Add(one);
                }
            }
            catch (Exception ex) { Log.Write("notif text: " + ex.Message); }
            return lines;
        }

        public static void Remove(uint id) { UserNotificationListener.Current.RemoveNotification(id); }

        // Off: Windows drops it after ~6 s
        public static bool Kept(string appId)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + appId))
                    return k == null || Convert.ToInt32(k.GetValue("ShowInActionCenter", 1)) != 0;
            }
            catch (Exception) { return true; }
        }

        // notif.apps, lowercased
        public static volatile string[] ExtraApps = new string[0];

        // Chat app by id alone; browsers carry more than chat
        public static bool ChatAppId(string appId)
        {
            return !AnyIn(appId.ToLowerInvariant(), Browsers) && IsChat(appId, "");
        }

        static bool IsChat(string app, string text)
        {
            string low = app.ToLowerInvariant();
            if (NamesChatApp(low) || AnyIn(low, ExtraApps)) return true;
            return AnyIn(low, Browsers) && AnyIn(text.ToLowerInvariant(), ChatSites);
        }

        // "line" must not match "online"
        static bool NamesChatApp(string low)
        {
            string[] words = null;
            foreach (var name in ChatApps)
            {
                if (name.Length > 4)
                {
                    if (low.Contains(name)) return true;
                    continue;
                }
                if (words == null) words = low.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries);
                if (Array.IndexOf(words, name) >= 0) return true;
            }
            return false;
        }

        static bool AnyIn(string s, string[] parts)
        {
            foreach (var part in parts)
                if (s.Contains(part)) return true;
            return false;
        }

        // Windows logo, else Dock icon; cached
        static Bitmap LogoFor(string appId, AppInfo info)
        {
            Bitmap logo;
            lock (logoByApp)
                if (logoByApp.TryGetValue(appId, out logo)) return logo;
            logo = StoreLogo(info) ?? ShellLogo(appId);
            lock (logoByApp) logoByApp[appId] = logo;
            return logo;
        }

        static Bitmap StoreLogo(AppInfo info)
        {
            try
            {
                var bytes = WinRt.ReadAll(info.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)), 2 * 1024 * 1024 - 1);
                if (bytes == null) return null;
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                    return Ink.Square(img, 48);
            }
            catch (Exception) { return null; }
        }

        static Bitmap ShellLogo(string appId)
        {
            if (appId.Length == 0) return null;
            try
            {
                using (var icon = ShellApp.Icon(ShellApp.AppsFolderOf(appId), 64))
                    return icon != null ? Ink.Square(icon, 48) : null;
            }
            catch (Exception) { return null; }
        }

        static readonly Dictionary<Bitmap, Bitmap> drained = new Dictionary<Bitmap, Bitmap>();

        // Monochrome copy, for when app colours are off
        public static Bitmap Drained(Bitmap logo)
        {
            if (logo == null) return null;
            lock (drained)
            {
                Bitmap d;
                if (!drained.TryGetValue(logo, out d)) { d = Ink.Drain(logo); drained[logo] = d; }
                return d;
            }
        }

        [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
        const int OpenReadOnly = 1, RowReady = 100;
        const string ToastStats = "SELECT COUNT(Id), IFNULL(MAX(Id), 0) FROM Notification WHERE Type = 'toast'";
        static IntPtr db;

        // Changes on toast add/remove; null if unreadable
        public static string ChangeKey()
        {
            try
            {
                if (db == IntPtr.Zero) db = OpenDb();
                if (db == IntPtr.Zero) return null;
                IntPtr stmt;
                if (sqlite3_prepare16_v2(db, ToastStats, -1, out stmt, IntPtr.Zero) != 0)
                {
                    // File replaced: reopen next time
                    sqlite3_close(db);
                    db = IntPtr.Zero;
                    return null;
                }
                try
                {
                    if (sqlite3_step(stmt) != RowReady) return null;
                    return sqlite3_column_int64(stmt, 0) + ":" + sqlite3_column_int64(stmt, 1);
                }
                finally { sqlite3_finalize(stmt); }
            }
            catch (Exception ex) { Log.Write("notif key: " + ex.Message); return null; }
        }

        static IntPtr OpenDb()
        {
            IntPtr h;
            var path = Encoding.UTF8.GetBytes(Path.Combine(Dir, "wpndatabase.db") + "\0");
            if (sqlite3_open_v2(path, out h, OpenReadOnly, IntPtr.Zero) == 0)
            {
                sqlite3_busy_timeout(h, 300);
                return h;
            }
            if (h != IntPtr.Zero) sqlite3_close(h);
            return IntPtr.Zero;
        }
    }

    // DB key polled each second (~8 µs)
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
            var t = new Thread(Prof.Wrap(Run)) { IsBackground = true, Name = "notif" };
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
                if (wake.WaitOne(watcher != null ? 10000 : 1000)) Thread.Sleep(350);
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
