using System;
using System.Collections.Generic;

namespace Tepi
{
    // notif.apps and notif.vip, comma separated
    partial class Island
    {
        string[] notifVip = new string[0];

        static string[] CommaList(string raw)
        {
            var list = new List<string>();
            foreach (var part in (raw ?? "").Split(','))
            {
                string s = part.Trim().ToLowerInvariant();
                if (s.Length > 0 && !list.Contains(s)) list.Add(s);
            }
            return list.ToArray();
        }

        void ApplyNotifLists()
        {
            NotifSource.ExtraApps = CommaList(settings.Get("notif.apps", ""));
            notifVip = CommaList(settings.Get("notif.vip", ""));
        }

        // Priority senders pass Senyap and calls
        bool IsVip(NotifItem n)
        {
            if (notifVip.Length == 0) return false;
            string text = (n.Title + " " + n.Body).ToLowerInvariant();
            foreach (var v in notifVip) if (text.Contains(v)) return true;
            return false;
        }
    }
}
