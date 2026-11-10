using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tepi
{
    // One banner per message, whatever the source
    partial class Island
    {
        class SeenMsg
        {
            public string Family, Source, Sender, Text;
            public DateTime Utc;
            // Each copy pairs with one other
            public bool Paired;
        }

        const double OnceWindowSec = 180;
        readonly List<SeenMsg> seenMsgs = new List<SeenMsg>();

        // App, Android package, or Phone Link text
        static readonly string[][] Families =
        {
            new[] { "whatsapp", "whatsapp" }, new[] { "telegram", "telegram" }, new[] { "discord", "discord" }, new[] { "slack", "slack" },
            new[] { "teams", "teams" }, new[] { "signal", "signal" }, new[] { "messenger", "messenger" }, new[] { "orca", "messenger" },
            new[] { "instagram", "instagram" }, new[] { "jp.naver.line", "line" }, new[] { "outlook", "outlook" }, new[] { "gmail", "gmail" },
            new[] { "mail.google", "gmail" }, new[] { "android.gm", "gmail" }, new[] { "zoom", "zoom" }, new[] { "thunderbird", "thunderbird" },
        };

        static string FamilyOf(NotifItem n)
        {
            string app = (n.AppId + " " + n.AppName).ToLowerInvariant();
            bool relay = app.Contains("yourphone") || app.Contains("phone link") || app.Contains("crossdevice");
            // Phone Link names the app inline
            string hay = relay ? (n.Title + " " + n.Body + " " + app).ToLowerInvariant() : app;
            foreach (var f in Families) if (hay.Contains(f[0])) return f[1];
            return relay ? "phone" : app;
        }

        static string SourceOf(NotifItem n)
        {
            if (n.AppId.StartsWith("phone:")) return "tepi-hp";
            string app = (n.AppId + " " + n.AppName).ToLowerInvariant();
            return app.Contains("yourphone") || app.Contains("crossdevice") ? "phone-link" : n.AppId;
        }

        static readonly Regex Counted = new Regex(@"\s*\(\d+\s+\w+\)$");

        // Letters and digits only, one space
        static string Norm(string s)
        {
            s = Counted.Replace(s ?? "", "");
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); sb.Append(c); space = false; }
                else space = true;
            }
            return sb.ToString();
        }

        static readonly Regex SummaryLine = new Regex(@"^\d+\s+(new\s+)?(messages?|pesan|chats?|obrolan)(\s+baru)?(\s+(from|dari)\s+\d+\s+\w+)?$");

        // "3 new messages" and kin
        static bool IsSummary(NotifItem n) { return SummaryLine.IsMatch(Norm(n.Body)) || SummaryLine.IsMatch(Norm(n.Title)); }

        static bool SameText(string a, string b)
        {
            if (a == b) return a.Length > 0;
            // Group copy: "Sender: text" vs "text"
            return Math.Min(a.Length, b.Length) >= 6 && (a.EndsWith(" " + b) || b.EndsWith(" " + a));
        }

        static bool SameSender(string a, string b, string text)
        {
            if (a == b || a.Length == 0 || b.Length == 0) return true;
            if (a.Contains(b) || b.Contains(a)) return true;
            // Long identical text is proof enough
            return text.Length >= 24;
        }

        // Already shown from another source
        bool SeenElsewhere(NotifItem n)
        {
            var now = DateTime.UtcNow;
            seenMsgs.RemoveAll(s => (now - s.Utc).TotalSeconds > OnceWindowSec);
            string fam = FamilyOf(n), src = SourceOf(n), sender = Norm(n.Title), text = Norm(n.Body);
            foreach (var s in seenMsgs)
                if (!s.Paired && s.Source != src && (s.Family == fam || s.Family == "phone" || fam == "phone") && SameText(s.Text, text) && SameSender(s.Sender, sender, text))
                {
                    s.Paired = true;
                    return true;
                }
            return false;
        }

        void Remember(NotifItem n, bool paired)
        {
            seenMsgs.Add(new SeenMsg { Family = FamilyOf(n), Source = SourceOf(n), Sender = Norm(n.Title), Text = Norm(n.Body), Utc = DateTime.UtcNow, Paired = paired });
            if (seenMsgs.Count > 200) seenMsgs.RemoveRange(0, seenMsgs.Count - 200);
        }

        // The phone row this desktop copy replaces
        static int PhoneTwin(List<NotifItem> list, NotifItem n)
        {
            string fam = FamilyOf(n), sender = Norm(n.Title), text = Norm(n.Body);
            for (int i = 0; i < list.Count; i++)
            {
                var v = list[i];
                if (!v.AppId.StartsWith("phone:")) continue;
                string vf = FamilyOf(v);
                if ((vf == fam || vf == "phone" || fam == "phone") && SameText(Norm(v.Body), text) && SameSender(Norm(v.Title), sender, text)) return i;
            }
            return -1;
        }

        // auto: Tepi pops only where Windows won't
        string BannerMode { get { return settings.Get("notif.banner", "auto"); } }

        // Read-only; Settings › Notifications writes these
        public static bool WindowsBanner(string appId)
        {
            if (appId.Length == 0 || appId.StartsWith("phone:")) return false;
            try
            {
                using (var push = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
                    if (push != null && Convert.ToInt32(push.GetValue("ToastEnabled", 1)) == 0) return false;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + appId))
                {
                    if (k == null) return true;
                    if (Convert.ToInt32(k.GetValue("Enabled", 1)) == 0) return false;
                    return Convert.ToInt32(k.GetValue("ShowBanner", 1)) != 0;
                }
            }
            catch (Exception) { return true; }
        }

        const string NotifSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\";

        // Tepi first: Windows banners off where Tepi shows the same; the center keeps them
        int MakeTepiPrimary()
        {
            var muted = new List<string>(settings.Get("notif.muted", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            int count = 0;
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(NotifSettingsKey.TrimEnd('\\')))
                    if (root != null)
                        foreach (var id in root.GetSubKeyNames())
                        {
                            // Screenshots: only while Tepi previews them
                            bool shots = id.StartsWith("Microsoft.ScreenSketch", StringComparison.OrdinalIgnoreCase) && ShotsOn;
                            if (!shots && !NotifSource.ChatAppId(id)) continue;
                            if (!WindowsBanner(id)) continue;
                            using (var k = Registry.CurrentUser.CreateSubKey(NotifSettingsKey + id)) k.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
                            if (!muted.Contains(id)) muted.Add(id);
                            count++;
                        }
            }
            catch (Exception ex) { Log.Write("tepi utama: " + ex.Message); }
            settings.Set("notif.banner", "tepi");
            settings.Set("notif.muted", string.Join("|", muted.ToArray()));
            settings.Save();
            return count;
        }

        // Undo: banners back for what Tepi turned off
        void RestoreWindowsBanners()
        {
            foreach (var id in settings.Get("notif.muted", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                try { using (var k = Registry.CurrentUser.CreateSubKey(NotifSettingsKey + id)) k.SetValue("ShowBanner", 1, RegistryValueKind.DWord); }
                catch (Exception ex) { Log.Write("banner kembali: " + ex.Message); }
            settings.Set("notif.muted", "");
            settings.Set("notif.banner", "auto");
            settings.Save();
        }

        // Ringing, not just a message
        static bool IsCall(NotifItem n)
        {
            string t = (n.Title + " " + n.Body).ToLowerInvariant();
            foreach (var k in CallWords) if (t.Contains(k)) return true;
            return false;
        }

        static readonly string[] CallWords = { "incoming call", "incoming voice call", "incoming video call", "is calling", "calling you", "panggilan masuk", "panggilan suara masuk", "panggilan video masuk", "sedang menelepon", "menelepon anda", "menelepon kamu" };

        // Decides the pop; the list still updates
        bool ShouldPop(NotifItem n)
        {
            if (IsSummary(n)) return false;
            if (BannerMode == "tepi") return true;
            return !WindowsBanner(n.AppId);
        }

        // Chat apps whose Windows banner still shows
        public List<string> DoubledApps()
        {
            var names = new List<string>();
            foreach (var n in notifs)
                if (!n.AppId.StartsWith("phone:") && WindowsBanner(n.AppId) && !names.Contains(n.AppName)) names.Add(n.AppName);
            return names;
        }
    }
}
