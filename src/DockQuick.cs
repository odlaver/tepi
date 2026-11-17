using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        // Quick settings card (Win+A)
        sealed class QuickSheet : GlassSheet
        {
            readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 400 };
            RectangleF volTrack, brightTrack;
            // Wi-Fi list, inside the same card
            bool wifiView, scanning;
            List<Wifi.Net> nets = new List<Wifi.Net>();
            Wifi.Net asking;
            string password = "", status;
            int first;
            // Keyboard focus; ring shows after a key
            string focusId;
            bool keyMode;

            const float Pad = Ink.Sp16, Gap = Ink.Sp8, TileH = 76, SegW = 30, SliderH = 40, Foot = 52, RowH = 50, ListTop = 60;

            public QuickSheet(Island isle, DockWindow dock) : base(isle, dock, "quick", "Tepi Quick")
            {
                W = 360;
                // Radios change on other threads; redraw only on change
                refresh.Tick += delegate
                {
                    string s = Sig();
                    if (s == drawnSig) return;
                    drawnSig = s;
                    Render();
                };
            }

            string drawnSig;

            // Everything outside the card that it shows
            string Sig()
            {
                var n = isle.net.Current;
                var r = isle.radios;
                return string.Concat(n.Wifi, n.Any, n.Captive, n.Online, n.Name, r.WifiOn, r.HasWifi, r.BluetoothOn, r.HasBluetooth, isle.btBattery.Count, isle.QuietSwitch, isle.winDark,
                    isle.AwakeOn, isle.AwakeLabel(), isle.micMuted, isle.micPresent, isle.muted, (int)Math.Round(isle.vol * 100), isle.audioDefault, isle.audioDevices.Count,
                    isle.brightLevel, isle.battery, isle.onAc, Ink.Epoch);
            }

            bool HasBright { get { return isle.brightLevel >= 0; } }

            float TileW { get { return (W - Pad * 2 - Gap * 2) / 3; } }
            float SlidersTop { get { return Pad + TileH * 2 + Gap + Ink.Sp16; } }

            protected override void Opening(Rectangle mon, int barTop)
            {
                H = SlidersTop + (HasBright ? SliderH + 4 : 0) + SliderH + 14 + Foot;
                isle.volume.RefreshDevices();
                refresh.Start();
            }

            protected override void Teardown() { refresh.Stop(); refresh.Dispose(); }

            protected override void Backdrop(RectangleF r)
            {
                Shelf(r, Foot);
                // Glass shelf gets the hairline too
                if (dock.Glass) g.DrawLine(p.P(p.T.Line, 1f), r.X, H - Foot, r.Right, H - Foot);
            }

            // Wi-Fi list slides in from the side it lives on
            bool drawnWifi;
            DateTime viewUtc = DateTime.MinValue;

            protected override bool Animating(float dt) { return (DateTime.UtcNow - viewUtc).TotalSeconds < 0.24; }

            protected override void Draw()
            {
                if (wifiView != drawnWifi)
                {
                    drawnWifi = wifiView;
                    viewUtc = Anim.Reduced ? DateTime.MinValue : DateTime.UtcNow;
                    Animate();
                }
                float k = Ease.OutCubic((float)(DateTime.UtcNow - viewUtc).TotalSeconds / 0.22f), keep = p.Alpha;
                var st = g.Save();
                g.TranslateTransform((wifiView ? 1 : -1) * 24 * (1 - k), 0);
                p.Alpha = keep * k;
                try
                {
                    if (wifiView) DrawWifi(); else DrawMain();
                    DrawFocus();
                }
                finally { p.Alpha = keep; g.Restore(st); }
            }

            void DrawMain()
            {
                var radios = isle.radios;
                float tw = TileW, y0 = Pad, y1 = Pad + TileH + Gap, c0 = Pad, c1 = c0 + tw + Gap, c2 = c1 + tw + Gap;
                Tile(new RectangleF(c0, y0, tw, TileH), "wifi", "wifiList", Lang.T("Pilih jaringan"), Ink.IcoWifi, radios.WifiOn, radios.HasWifi, Lang.T("Wi-Fi"), WifiState());
                Tile(new RectangleF(c1, y0, tw, TileH), "bt", "btMore", Lang.T("Lainnya"), Ink.IcoBluetooth, radios.BluetoothOn, radios.HasBluetooth, Lang.T("Bluetooth"), BluetoothState());
                Tile(new RectangleF(c2, y0, tw, TileH), "quiet", null, null, Ink.IcoQuiet, isle.QuietSwitch, true, Lang.T("Senyap"), OnOff(isle.QuietSwitch));
                Tile(new RectangleF(c0, y1, tw, TileH), "dark", null, null, IcoDarkMode, isle.winDark, true, Lang.T("Gelap"), OnOff(isle.winDark));
                Tile(new RectangleF(c1, y1, tw, TileH), "awake", "awakeMore", Lang.T("Lainnya"), IcoAwake, isle.AwakeOn, true, Lang.T("Terjaga"), AwakeState());
                Tile(new RectangleF(c2, y1, tw, TileH), "mic", null, null, isle.micMuted ? IcoMicOff : Ink.IcoMic, isle.micPresent && !isle.micMuted, isle.micPresent, Lang.T("Mikrofon"),
                    isle.micPresent ? OnOff(!isle.micMuted) : Lang.T("Tidak tersedia"));

                float y = SlidersTop;
                if (HasBright)
                {
                    brightTrack = SliderRow(y, "bright", isle.brightLevel / 100f, Ink.IcoSun, null, null, Ink.Percent(isle.brightLevel), false);
                    y += SliderH + 4;
                }
                char volGlyph = isle.muted ? Ink.IcoMute : isle.vol < 0.01f ? Ink.IcoVol0 : isle.vol < 0.34f ? Ink.IcoVol1 : isle.vol < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3;
                volTrack = SliderRow(y, "vol", isle.muted ? 0 : isle.vol, volGlyph, "mute", isle.muted ? Lang.T("Bunyikan") : Lang.T("Bisukan"), Ink.Percent((int)Math.Round(isle.vol * 100)), true);
                DrawFoot();
            }

            static string OnOff(bool on) { return on ? Lang.T("Nyala") : Lang.T("Mati"); }

            string WifiState()
            {
                var radios = isle.radios;
                if (!radios.HasWifi) return Lang.T("Tidak tersedia");
                if (!radios.WifiOn) return Lang.T("Mati");
                string name = isle.WifiLabel();
                return name != Lang.T("Wi-Fi") ? name : Lang.T("Belum tersambung");
            }

            string BluetoothState()
            {
                var radios = isle.radios;
                if (!radios.HasBluetooth) return Lang.T("Tidak tersedia");
                if (!radios.BluetoothOn) return Lang.T("Mati");
                if (isle.btBattery.Count > 1) return Lang.F("{0} perangkat", isle.btBattery.Count);
                foreach (var kv in isle.btBattery) return kv.Key;
                return Lang.T("Nyala");
            }

            string AwakeState()
            {
                if (!isle.AwakeOn) return Lang.T("Mati");
                return isle.awakeUntilUtc == DateTime.MaxValue ? Lang.T("Nyala") : isle.AwakeLabel();
            }

            // Windows 11 tile; split adds a › segment
            void Tile(RectangleF r, string id, string more, string moreTip, char glyph, bool on, bool available, string label, string state)
            {
                bool split = more != null && available;
                float segX = split ? r.Right - SegW : r.Right;
                var main = RectangleF.FromLTRB(r.X, r.Y, segX, r.Bottom);
                var seg = RectangleF.FromLTRB(segX, r.Y, r.Right, r.Bottom);
                if (available) Hit(main, id);
                if (split)
                {
                    Hit(seg, more);
                    if (hotId == more) Tip(seg, moreTip);
                }
                float ok = OnK(id, on && available);
                float s = Squish(r, Math.Min(PressScale(id, 0.96f), PressScale(more, 0.96f)));
                TilePart(r, main, id, ok, available);
                if (split) TilePart(r, seg, more, ok, true);
                Color ink = available ? Ink.Mix(p.T.Text, p.T.OnAccent, ok) : p.T.Quaternary;
                if (split)
                {
                    float sk = HotK(more);
                    g.DrawLine(p.P(Ink.Mix(p.T.Line, Ink.A(p.T.OnAccent, 0.35f), ok), 1f), segX, r.Y + 14, segX, r.Bottom - 14);
                    p.Symbol(Sym.ChevronRight, segX + SegW / 2, r.Y + r.Height / 2, 10f, Ink.Mix(Ink.Mix(p.T.Sub, p.T.Text, sk), p.T.OnAccent, ok));
                }
                float room = main.Width - 12 - 6;
                p.Icon(glyph, 16f, ink, r.X + 20, r.Y + 22);
                p.Text(label, Ink.Semi(12.5f), available ? ink : p.T.Faint, r.X + 12, r.Bottom - 31, room, StringAlignment.Near);
                p.Text(state, Ink.Caption, available ? Ink.Mix(p.T.Sub, Ink.A(p.T.OnAccent, 0.8f), ok) : p.T.Faint, r.X + 12, r.Bottom - 15, room, StringAlignment.Near);
                Unsquish(r, s);
            }

            // One side's fill, clipped from the whole tile
            void TilePart(RectangleF tile, RectangleF area, string id, float ok, bool available)
            {
                float hk = available ? HotK(id) : 0;
                bool down = available && pressId == id && hotId == id;
                Color off = !available ? Ink.A(p.T.Fill, 0.5f) : down ? p.T.FillPressed : Ink.Mix(p.T.Fill, p.T.FillHover, hk);
                Color lit = down ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.22f) : Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f * hk);
                var st = g.Save();
                g.SetClip(area, CombineMode.Intersect);
                if (ok < 0.999f) p.Module(tile, Ink.RadiusControl, off);
                if (ok > 0.001f) p.FillRound(tile, Ink.RadiusControl, Ink.A(lit, ok));
                g.Restore(st);
            }

            // Icon, track, percent, optional ›; returns the track
            RectangleF SliderRow(float y, string id, float value, char glyph, string iconId, string iconTip, string tail, bool devices)
            {
                float cy = y + SliderH / 2;
                var icon = new RectangleF(Pad - 2, cy - 18, 36, 36);
                if (iconId != null) Button(icon, iconId, glyph, iconTip, 16f);
                else p.Icon(glyph, 16f, p.T.Text, icon.X + icon.Width / 2, cy);
                var chev = new RectangleF(W - Pad - 32, cy - 16, 32, 32);
                float tailRight = chev.X - 4, x0 = icon.Right + 10, x1 = tailRight - 36 - 12;
                var track = new RectangleF(x0, cy - 2, x1 - x0, 4);
                Hit(RectangleF.FromLTRB(x0 - 10, y, x1 + 10, y + SliderH), id);
                bool held = pressId == id;
                float hk = held ? 1 : HotK(id), dk = OnK("held:" + id, held);
                float v = Ease.Clamp01(Eased(id, Ease.Clamp01(value), held && dragged)), at = x0 + track.Width * v;
                // Track thickens under the pointer
                float th = 4 + 2 * hk;
                p.FillRound(new RectangleF(x0, cy - th / 2, track.Width, th), th / 2, p.T.Quaternary);
                if (at > x0 + 0.5f) p.FillRound(RectangleF.FromLTRB(x0, cy - th / 2, Math.Max(at, x0 + th), cy + th / 2), th / 2, p.T.Accent);
                float ro = 9 + hk;
                p.Circle(at, cy, ro, Ink.Over(p.T.FillHover, Color.FromArgb(255, p.T.Bg)));
                g.DrawEllipse(p.P(p.T.Line, 1f), at - ro, cy - ro, ro * 2, ro * 2);
                p.Circle(at, cy, 5 + 1.5f * hk - 2f * dk, p.T.Accent);
                p.Text(tail, Ink.Callout, p.T.Sub, tailRight, cy, 40, StringAlignment.Far);
                if (devices) Button(chev, "device", '', DeviceName(), 12f);
                return track;
            }

            string DeviceName()
            {
                foreach (var d in isle.audioDevices) if (d.Id == isle.audioDefault) return d.Name;
                return Lang.T("Perangkat output");
            }

            void DrawFoot()
            {
                float cy = H - Foot / 2;
                if (isle.hasBattery)
                {
                    bool low = isle.battery <= 20 && !isle.onAc;
                    Color c = low ? p.T.Negative : p.T.Text;
                    p.Battery(Pad + 2, cy, isle.battery / 100f, c, isle.onAc);
                    p.Text(Ink.Percent(isle.battery), Ink.Semi(12.5f), c, Pad + 40, cy, 60, StringAlignment.Near);
                }
                Button(new RectangleF(W - Pad - 34, cy - 17, 36, 36), "gear", (char)0xE713, Lang.T("Pengaturan Windows"));
                Button(new RectangleF(W - Pad - 74, cy - 17, 36, 36), "tepi", (char)0xE8A9, Lang.T("Pengaturan Tepi"));
            }

            // Two-tone ring: reads on accent and on glass
            void DrawFocus()
            {
                if (!keyMode || focusId == null) return;
                for (int i = hits.Count - 1; i >= 0; i--)
                {
                    if (hits[i].Value != focusId) continue;
                    var r = hits[i].Key;
                    p.StrokeRound(RectangleF.Inflate(r, 1, 1), Ink.RadiusControl + 1, Color.FromArgb(255, p.T.Bg), 1.5f);
                    p.StrokeRound(RectangleF.Inflate(r, 3, 3), Ink.RadiusControl + 3, p.T.Text, 2f);
                    return;
                }
            }

            // Networks nearby; saved ones join on a click, new ones ask a password
            void DrawWifi()
            {
                Button(new RectangleF(Pad - 6, 14, 36, 36), "back", (char)0xE72B, Lang.T("Kembali"));
                p.Text(Lang.T("Wi-Fi"), Ink.Headline, p.T.Text, Pad + 38, 32, 120, StringAlignment.Near);
                if (scanning) p.Text(Lang.T("Mencari…"), Ink.Caption, p.T.Sub, W - Pad - 40, 32, 100, StringAlignment.Far);
                Button(new RectangleF(W - Pad - 30, 14, 36, 36), "rescan", (char)0xE72C, Lang.T("Cari ulang"));
                float bottom = H - Foot - 6;
                if (!isle.radios.WifiOn) Note(Lang.T("Wi-Fi mati"));
                else if (nets.Count == 0) Note(scanning ? Lang.T("Mencari jaringan…") : Lang.T("Tidak ada jaringan"));
                var st = g.Save();
                g.SetClip(new RectangleF(0, ListTop, W, bottom - ListTop));
                float y = ListTop;
                first = Math.Max(0, Math.Min(first, Math.Max(0, nets.Count - 1)));
                for (int i = first; i < nets.Count && y < bottom; i++)
                {
                    var n = nets[i];
                    bool open = asking == n;
                    var row = new RectangleF(Pad - 8, y, W - Pad * 2 + 16, open ? RowH + 50 : RowH);
                    var head = new RectangleF(row.X, row.Y, row.Width, RowH);
                    string id = "w" + i, offId = "off" + i;
                    Hit(head, id);
                    float hk = HotK(id), s = Squish(id, head, 0.98f);
                    if (open) p.Module(row, Ink.RadiusControl, p.T.Fill);
                    else if (hk > 0 || pressId == id) p.FillRound(row, Ink.RadiusControl, pressId == id ? p.T.FillPressed : Ink.A(p.T.Fill, hk));
                    float cy = y + RowH / 2;
                    char bars = n.Signal < 25 ? (char)0xE872 : n.Signal < 50 ? (char)0xE873 : n.Signal < 75 ? (char)0xE874 : (char)0xE701;
                    // Missing arcs stay faintly there
                    if (n.Signal < 75) p.Icon((char)0xE701, 16f, Ink.A(p.T.Text, 0.22f), row.X + 24, cy);
                    p.Icon(bars, 16f, p.T.Text, row.X + 24, cy);
                    if (n.Secured && !n.Open) p.Icon((char)0xE72E, 8f, p.T.Sub, row.X + 32, cy + 6);
                    p.Text(n.Ssid, Ink.Semi(13f), p.T.Text, row.X + 46, cy - 8, row.Width - 140, StringAlignment.Near);
                    var now = isle.net.Current;
                    string sub = n.Connected && now.Captive ? Lang.T("Perlu login, klik untuk masuk") : n.Connected && now.Any && !now.Online ? Lang.T("Tersambung, tanpa internet")
                               : n.Connected ? Lang.T("Tersambung") : n.Known ? Lang.T("Tersimpan") : n.Open ? Lang.T("Terbuka") : n.Personal ? Lang.T("Butuh kata sandi") : Lang.T("Masuk lewat Pengaturan");
                    p.Text(sub, Ink.Small(11f), n.Connected ? p.T.Accent : p.T.Sub, row.X + 46, cy + 9, row.Width - 140, StringAlignment.Near);
                    Unsquish(head, s);
                    if (n.Connected && (hotId == id || hotId == offId || focusId == id || focusId == offId))
                    {
                        var off = new RectangleF(row.Right - 96, cy - 14, 86, 28);
                        Hit(off, offId);
                        float ok = HotK(offId), os = Squish(offId, off, 0.94f);
                        p.FillRound(off, 14, pressId == offId ? p.T.FillPressed : Ink.Mix(p.T.Fill, p.T.FillHover, ok));
                        p.TextCenter(Lang.T("Putuskan"), Ink.Small(11.5f), p.T.Text, off.X + off.Width / 2, cy);
                        Unsquish(off, os);
                    }
                    if (open) DrawPassword(new RectangleF(row.X + 12, y + RowH, row.Width - 24, 38));
                    y += row.Height + 2;
                }
                g.Restore(st);
                if (status != null) p.Text(status, Ink.Small(11.5f), p.T.Sub, Pad + 4, H - Foot / 2, W - Pad * 2 - 120, StringAlignment.Near);
                var link = new RectangleF(W - Pad - 120, H - Foot / 2 - 15, 124, 30);
                Hit(link, "wifiSettings");
                float lk = HotK("wifiSettings"), ls = Squish("wifiSettings", link, 0.94f);
                if (lk > 0 || pressId == "wifiSettings") p.FillRound(link, 15, pressId == "wifiSettings" ? p.T.FillPressed : Ink.A(p.T.FillHover, lk));
                p.TextCenter(Lang.T("Pengaturan Wi-Fi"), Ink.Small(11.5f), p.T.Text, link.X + link.Width / 2, link.Y + 15);
                Unsquish(link, ls);
            }

            void Note(string text) { p.Text(text, Ink.Small(12f), p.T.Sub, Pad + 4, ListTop + 24, W - Pad * 2, StringAlignment.Near); }

            void DrawPassword(RectangleF r)
            {
                var box = new RectangleF(r.X, r.Y, r.Width - 108, r.Height - 4);
                p.FillRound(box, 10, p.T.FillHover);
                p.StrokeRound(box, 10, p.T.Line, 1f);
                float cy = box.Y + box.Height / 2;
                var f = Ink.Semi(13f);
                string shown = password.Length == 0 ? null : new string('•', password.Length);
                if (shown == null) p.Text(Lang.T("Kata sandi"), Ink.Small(12f), p.T.Sub, box.X + 12, cy, box.Width - 20, StringAlignment.Near);
                else p.Text(shown, f, p.T.Text, box.X + 12, cy, box.Width - 20, StringAlignment.Near);
                float caret = box.X + 12 + (shown != null ? Math.Min(box.Width - 24, p.Measure(shown, f)) + 1 : -2);
                g.DrawLine(p.P(p.T.Text, 1.4f), caret, cy - 8, caret, cy + 8);
                var join = new RectangleF(box.Right + 8, box.Y, 100, box.Height);
                bool ready = password.Length >= 8;
                if (ready) Hit(join, "join");
                float s = Squish("join", join, 0.94f);
                p.FillRound(join, box.Height / 2, ready ? Ink.Mix(p.T.Accent, p.T.OnAccent, pressId == "join" ? 0.22f : 0.1f * HotK("join")) : p.T.Fill);
                p.TextCenter(Lang.T("Sambungkan"), Ink.Semi(12f), ready ? p.T.OnAccent : p.T.Faint, join.X + join.Width / 2, cy);
                Unsquish(join, s);
            }

            void OpenWifi()
            {
                wifiView = true;
                asking = null;
                status = null;
                first = 0;
                focusId = keyMode ? "back" : null;
                Rescan();
            }

            void CloseWifi()
            {
                wifiView = false;
                asking = null;
                focusId = keyMode ? "wifiList" : null;
            }

            // A quick list now, the fresh scan a few seconds later
            void Rescan()
            {
                if (scanning) return;
                scanning = true;
                var t = new Thread(delegate ()
                {
                    List<Wifi.Net> quick = null, full = null;
                    try
                    {
                        quick = Wifi.List();
                        Post(delegate { nets = quick; Render(); });
                        Wifi.Scan();
                        Thread.Sleep(3500);
                        full = Wifi.List();
                    }
                    catch (Exception ex) { Log.Write("wifi list: " + ex.Message); }
                    Post(delegate { if (full != null) nets = full; scanning = false; Render(); });
                }) { IsBackground = true, Name = "wifi scan" };
                t.Start();
            }

            void Join(Wifi.Net n, string pass)
            {
                status = Lang.F("Menyambungkan ke {0}…", n.Ssid);
                asking = null;
                password = "";
                Render();
                var t = new Thread(delegate ()
                {
                    string err = null;
                    try { err = Wifi.Connect(n, pass); }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != null) Log.Write("wifi join: " + err);
                    Thread.Sleep(err == null ? 4000 : 0);
                    List<Wifi.Net> list = null;
                    try { list = Wifi.List(); } catch (Exception) { }
                    bool ok = list != null && list.Exists(x => x.Ssid == n.Ssid && x.Connected);
                    Post(delegate
                    {
                        if (list != null) nets = list;
                        status = ok ? Lang.F("Tersambung ke {0}", n.Ssid) : Lang.F("Gagal menyambung ke {0}", n.Ssid);
                        Render();
                    });
                }) { IsBackground = true, Name = "wifi join" };
                t.Start();
            }

            void Post(Action a)
            {
                try { if (!IsDisposed) BeginInvoke(a); } catch (Exception) { }
            }

            // Password field opens inside the list
            void ShowRow(int i)
            {
                float room = H - Foot - 6 - ListTop - (RowH + 50);
                first = Math.Max(first, i - Math.Max(0, (int)Math.Floor(room / (RowH + 2))));
            }

            static int IndexOf(string id, string tag)
            {
                int i;
                return id != null && id.StartsWith(tag, StringComparison.Ordinal) && int.TryParse(id.Substring(tag.Length), out i) ? i : -1;
            }

            protected override bool Drag(string id, PointF at)
            {
                if (wifiView) return false;
                if (id == "vol") { isle.setVol(SliderValue(volTrack, at)); return true; }
                if (id == "bright" && HasBright) { isle.setBright(SliderValue(brightTrack, at)); return true; }
                return false;
            }

            int wheelAcc;

            protected override void Wheel(string id, int delta)
            {
                if (wifiView)
                {
                    wheelAcc += delta;
                    int steps = wheelAcc / 120;
                    wheelAcc -= steps * 120;
                    first -= steps;
                    return;
                }
                float step = 0.02f * delta / 120f;
                if (id == "bright" && HasBright) isle.setBright(Ease.Clamp01(isle.brightLevel / 100f + step * 2));
                else isle.setVol(Ease.Clamp01(isle.vol + step));
            }

            // Click eases the level; a drag follows tight
            bool dragged;

            protected override void OnMouseDown(MouseEventArgs e)
            {
                keyMode = false;
                dragged = false;
                base.OnMouseDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (pressId != null && e.Button == MouseButtons.Left) dragged = true;
                base.OnMouseMove(e);
            }

            protected override void Clicked(string id, MouseButtons button)
            {
                if (wifiView) { WifiClicked(id, button); return; }
                if (button == MouseButtons.Right)
                {
                    if (id == "wifi" || id == "wifiList") OpenWifi();
                    else if (id == "bt" || id == "btMore") BluetoothMenu();
                    else if (id == "awake" || id == "awakeMore") ShowMenu(isle.AwakeItems());
                    else if (id == "vol" || id == "mute" || id == "device") SettingsMenu(Lang.T("Pengaturan suara"), "ms-settings:sound");
                    return;
                }
                if (button != MouseButtons.Left) return;
                switch (id)
                {
                    case "wifi": isle.actWifi(); break;
                    case "wifiList": OpenWifi(); break;
                    case "bt": isle.actBt(); break;
                    case "btMore": BluetoothMenu(); break;
                    case "quiet": isle.actQuiet(); break;
                    case "dark": isle.actDark(); break;
                    case "awake": isle.actAwake(); break;
                    case "awakeMore": ShowMenu(isle.AwakeItems()); break;
                    case "mic": isle.actMic(); break;
                    case "mute": isle.actMute(); break;
                    case "device": DeviceMenu(); break;
                    case "gear": Open("ms-settings:", null); Done(); break;
                    case "tepi": Done(); isle.OpenPrefs("dock"); break;
                }
            }

            void WifiClicked(string id, MouseButtons button)
            {
                if (button != MouseButtons.Left) return;
                int i = IndexOf(id, "w"), off = IndexOf(id, "off");
                if (off >= 0 && off < nets.Count)
                {
                    var n = nets[off];
                    new Thread(delegate () { Wifi.Disconnect(n); Thread.Sleep(1500); Post(Rescan); }) { IsBackground = true }.Start();
                    status = Lang.F("Memutus {0}…", n.Ssid);
                    return;
                }
                if (i >= 0 && i < nets.Count)
                {
                    var n = nets[i];
                    // Hotel or campus login page
                    if (n.Connected && isle.net.Current.Captive) { Open("http://www.msftconnecttest.com/redirect", null); Done(); return; }
                    if (n.Connected) return;
                    if (n.Known || n.Open) Join(n, null);
                    else if (n.Personal) { asking = asking == n ? null : n; password = ""; if (asking != null) ShowRow(i); }
                    else { Open("ms-settings:network-wifi", null); Done(); }
                    return;
                }
                switch (id)
                {
                    case "back": CloseWifi(); break;
                    case "rescan": Rescan(); break;
                    case "join": if (asking != null && password.Length >= 8) Join(asking, password); break;
                    case "wifiSettings": Open("ms-settings:network-wifi", null); Done(); break;
                }
            }

            // Tab follows drawing order
            List<string> FocusOrder()
            {
                var order = new List<string>();
                if (wifiView)
                {
                    order.Add("back");
                    order.Add("rescan");
                    for (int i = 0; i < nets.Count; i++)
                    {
                        order.Add("w" + i);
                        if (nets[i].Connected) order.Add("off" + i);
                        if (asking == nets[i] && password.Length >= 8) order.Add("join");
                    }
                    order.Add("wifiSettings");
                    return order;
                }
                foreach (var h in hits) if (!order.Contains(h.Value)) order.Add(h.Value);
                return order;
            }

            void MoveFocus(int dir)
            {
                var order = FocusOrder();
                if (order.Count == 0) return;
                int i = order.IndexOf(focusId);
                i = i < 0 ? (dir > 0 ? 0 : order.Count - 1) : (i + dir + order.Count) % order.Count;
                focusId = order[i];
                keyMode = true;
                int row = Math.Max(IndexOf(focusId, "w"), IndexOf(focusId, "off"));
                if (wifiView && row >= 0)
                {
                    int fit = Math.Max(1, (int)((H - Foot - 6 - ListTop) / (RowH + 2)));
                    if (row < first) first = row;
                    else if (row >= first + fit) first = row - fit + 1;
                }
                Render();
            }

            // Keys act on the focused control
            void Activate(MouseButtons button)
            {
                if (focusId == null) return;
                for (int i = hits.Count - 1; i >= 0; i--)
                    if (hits[i].Value == focusId)
                    {
                        var r = hits[i].Key;
                        menuAt = PointToScreen(new Point((int)(r.X * scale), (int)(r.Bottom * scale)));
                        break;
                    }
                Clicked(focusId, button);
                menuAt = null;
                if (!IsDisposed) Render();
            }

            bool Key(Keys key)
            {
                bool typing = wifiView && asking != null;
                bool slider = !wifiView && (focusId == "vol" || focusId == "bright");
                switch (key)
                {
                    case Keys.Tab: MoveFocus(1); return true;
                    case Keys.Shift | Keys.Tab: MoveFocus(-1); return true;
                    case Keys.Right:
                    case Keys.Up:
                        if (slider) { Wheel(focusId, 120); Render(); } else MoveFocus(1);
                        return true;
                    case Keys.Left:
                    case Keys.Down:
                        if (slider) { Wheel(focusId, -120); Render(); } else MoveFocus(-1);
                        return true;
                    case Keys.Enter: if (typing || focusId == null) return false; Activate(MouseButtons.Left); return true;
                    case Keys.Space: if (typing || focusId == null) return false; Activate(MouseButtons.Left); return true;
                    case Keys.Apps:
                    case Keys.Shift | Keys.F10: Activate(MouseButtons.Right); return true;
                }
                return false;
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys key)
            {
                if (Leaving) return true;
                if (wifiView)
                {
                    if (asking != null)
                    {
                        switch (key)
                        {
                            case Keys.Escape: asking = null; password = ""; Render(); return true;
                            case Keys.Enter: if (password.Length >= 8) Join(asking, password); return true;
                            case Keys.Back: if (password.Length > 0) password = password.Substring(0, password.Length - 1); Render(); return true;
                            case Keys.Control | Keys.V:
                                try { if (Clipboard.ContainsText()) password = (password + Clipboard.GetText().Trim()); } catch (Exception) { }
                                if (password.Length > 63) password = password.Substring(0, 63);
                                Render();
                                return true;
                        }
                    }
                    else if (key == Keys.Escape) { CloseWifi(); Render(); return true; }
                }
                if (Key(key)) return true;
                return base.ProcessCmdKey(ref msg, key);
            }

            protected override void OnKeyPress(KeyPressEventArgs e)
            {
                base.OnKeyPress(e);
                if (Leaving || !wifiView || asking == null || e.KeyChar < ' ' || password.Length >= 63) return;
                password += e.KeyChar;
                e.Handled = true;
                Render();
            }

            // Paired devices with their battery, then settings
            void BluetoothMenu()
            {
                var m = new ContextMenuStrip();
                foreach (var kv in isle.btBattery)
                    m.Items.Add(new ToolStripMenuItem(kv.Key + "  ·  " + Ink.Percent(kv.Value)) { Enabled = false });
                if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Tambah perangkat"), null, delegate { Open("ms-settings:connecteddevices", null); Done(); });
                m.Items.Add(Lang.T("Pengaturan Bluetooth"), null, delegate { Open("ms-settings:bluetooth", null); Done(); });
                ShowMenu(m);
            }

            void SettingsMenu(string label, string uri)
            {
                var m = new ContextMenuStrip();
                m.Items.Add(label, null, delegate { Open(uri, null); Done(); });
                ShowMenu(m);
            }

            void DeviceMenu()
            {
                var m = new ContextMenuStrip();
                foreach (var d in isle.audioDevices)
                {
                    string id = d.Id;
                    m.Items.Add(new ToolStripMenuItem(d.Name, null, delegate { isle.volume.SetDefault(id); }) { Checked = d.Id == isle.audioDefault });
                }
                if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Pengaturan suara"), null, delegate { Open("ms-settings:sound", null); Done(); });
                ShowMenu(m);
            }
        }
    }
}
