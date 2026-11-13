using System;
using System.Collections.Generic;
using System.Drawing;

namespace Tepi
{
    // Messages: one chip per app, tap to show only that app
    partial class Island
    {
        string notifApp;
        readonly List<NotifItem> shownNotifs = new List<NotifItem>();
        readonly List<string> chipApps = new List<string>();
        const float ChipRow = 34;

        List<NotifItem> ShownNotifs()
        {
            if (notifApp == null) return notifs;
            shownNotifs.Clear();
            foreach (var n in notifs) if (n.AppName == notifApp) shownNotifs.Add(n);
            // The app's last message went: back to all
            if (shownNotifs.Count == 0) { notifApp = null; return notifs; }
            return shownNotifs;
        }

        // Apps by their newest message, at most four
        List<string> ChipApps()
        {
            chipApps.Clear();
            foreach (var n in notifs)
                if (n.AppName.Length > 0 && !chipApps.Contains(n.AppName))
                {
                    chipApps.Add(n.AppName);
                    if (chipApps.Count == 4) break;
                }
            return chipApps;
        }

        float NotifChipsH() { return ChipApps().Count >= 2 ? ChipRow : 0; }

        float DrawNotifChips(float w)
        {
            var apps = ChipApps();
            if (apps.Count < 2) { notifApp = null; return 0; }
            var f = Ink.Semi(11.5f);
            float x = 18, y = PanelTop + 4, ch = 26;
            Chip(ref x, y, ch, w, f, null, Lang.T("Semua"), Color.Empty);
            foreach (var app in apps)
            {
                var item = notifs.Find(n => n.AppName == app);
                if (!Chip(ref x, y, ch, w, f, app, app, BrandInk(item))) break;
            }
            return ChipRow;
        }

        // False when it no longer fits
        bool Chip(ref float x, float y, float h, float w, Font f, string app, string label, Color brand)
        {
            float tw = Math.Min(110, p.Measure(label, f)), cw = tw + (brand.IsEmpty ? 22 : 34);
            if (x + cw > w - 18) return false;
            var r = new RectangleF(x, y, cw, h);
            string id = "chip" + (app ?? "*");
            bool on = notifApp == app;
            string pick = app;
            AddHit(r, id, delegate { notifApp = pick; notifScroll.Snap(0); Kick(); });
            float hk = HoverK(id);
            if (on) p.FillRound(r, h / 2, p.T.FillHover);
            else if (hk > 0 || Pressed(id)) p.FillRound(r, h / 2, SoftFill(id));
            p.StrokeRound(r, h / 2, on ? Ink.A(p.T.Text, 0.4f) : p.T.Line, 1f);
            float tx = r.X + 11;
            if (!brand.IsEmpty) { p.Circle(tx + 4, y + h / 2, 4f, brand); tx += 12; }
            p.Text(label, f, on ? p.T.Text : SoftInk(id), tx, y + h / 2, tw + 2, StringAlignment.Near);
            x += cw + 6;
            return true;
        }
    }
}
