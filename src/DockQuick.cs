using System;
using System.Collections.Generic;
using System.Drawing;
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
            RectangleF volRect, brightRect;
            // Wi-Fi list, inside the same card
            bool wifiView, scanning;
            List<Wifi.Net> nets = new List<Wifi.Net>();
            Wifi.Net asking;
            string password = "", status;
            int first;

            const float Pad = 20, ToggleD = 48, Foot = 56, RowH = 50, ListTop = 56;

            public QuickSheet(Island isle, DockWindow dock) : base(isle, dock, "quick", "Tepi Quick")
            {
                W = 360;
                // Radios change on other threads
                refresh.Tick += delegate { Render(); };
            }

            bool HasBright { get { return isle.brightLevel >= 0; } }

            protected override void Opening(Rectangle mon, int barTop)
            {
                H = 196 + 48 + 40 + (HasBright ? 56 : 0) + Foot + 12;
                isle.volume.RefreshDevices();
                refresh.Start();
            }

            protected override void Teardown() { refresh.Stop(); refresh.Dispose(); }

            protected override void Backdrop(RectangleF r) { Shelf(r, Foot); }

            protected override void Draw()
            {
                if (wifiView) { DrawWifi(); return; }
                var net = isle.net.Current;
                var radios = isle.radios;
                float c0 = W / 6, c1 = W / 2, c2 = W * 5 / 6, y0 = 50, y1 = 132;
                // Joined but not online says so, not just the name
                string wifi = net.Wifi && net.Any && net.Captive ? Lang.T("Perlu login") : net.Wifi && net.Any && !net.Online ? Lang.T("Tanpa internet")
                            : net.Wifi && net.Name.Length > 0 ? net.Name : Lang.T("Wi-Fi");
                Toggle(c0, y0, ToggleD, Ink.IcoWifi, radios.WifiOn, radios.HasWifi, Clip(wifi), "wifi", "wifiList");
                Toggle(c1, y0, ToggleD, Ink.IcoBluetooth, radios.BluetoothOn, radios.HasBluetooth, Lang.T("Bluetooth"), "bt", "btMore");
                Toggle(c2, y0, ToggleD, Ink.IcoQuiet, isle.QuietSwitch, true, Lang.T("Senyap"), "quiet");
                Toggle(c0, y1, ToggleD, IcoDarkMode, isle.winDark, true, Lang.T("Gelap"), "dark");
                Toggle(c1, y1, ToggleD, IcoAwake, isle.AwakeOn, true, isle.AwakeLabel(), "awake", "awakeMore");
                Toggle(c2, y1, ToggleD, isle.micMuted ? IcoMicOff : Ink.IcoMic, isle.micPresent && !isle.micMuted, isle.micPresent, Lang.T("Mikrofon"), "mic");

                float y = 196;
                volRect = new RectangleF(Pad, y, W - Pad * 2, 48);
                char volGlyph = isle.muted ? Ink.IcoMute : isle.vol < 0.01f ? Ink.IcoVol0 : isle.vol < 0.34f ? Ink.IcoVol1 : isle.vol < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3;
                Slider(volRect, "vol", isle.muted ? 0 : isle.vol, volGlyph, Lang.T("Suara"), Ink.Percent((int)Math.Round(isle.vol * 100)), "mute");
                y += 52;
                DrawDevice(new RectangleF(Pad, y, W - Pad * 2, 34));
                y += 40;
                if (HasBright)
                {
                    brightRect = new RectangleF(Pad, y, W - Pad * 2, 48);
                    Slider(brightRect, "bright", isle.brightLevel / 100f, Ink.IcoSun, Lang.T("Layar"), Ink.Percent(isle.brightLevel), null);
                }
                DrawFoot();
            }

            static string Clip(string s) { return s.Length > 12 ? s.Substring(0, 11) + "…" : s; }

            // Where the sound goes; click to switch
            void DrawDevice(RectangleF r)
            {
                AudioDevice cur = null;
                foreach (var d in isle.audioDevices) if (d.Id == isle.audioDefault) cur = d;
                Hit(r, "device");
                if (hotId == "device") p.FillRound(r, 10, pressId == "device" ? p.T.FillPressed : p.T.FillHover);
                float cy = r.Y + r.Height / 2;
                p.Icon(DeviceGlyph(cur != null ? cur.Form : 1), 14f, p.T.Sub, r.X + 25, cy);
                p.Text(cur != null ? cur.Name : Lang.T("Perangkat suara"), Ink.Small(12f), p.T.Text, r.X + 46, cy, r.Width - 80, StringAlignment.Near);
                p.Icon((char)0xE70D, 10f, p.T.Sub, r.Right - 18, cy);
            }

            void DrawFoot()
            {
                float cy = H - Foot / 2;
                if (isle.hasBattery)
                {
                    bool low = isle.battery <= 20 && !isle.onAc;
                    p.Battery(Pad + 4, cy, isle.battery / 100f, low ? p.T.Negative : p.T.Text, isle.onAc);
                    p.Text(Ink.Percent(isle.battery), Ink.Semi(13f), low ? p.T.Negative : p.T.Text, Pad + 40, cy, 60, StringAlignment.Near);
                    p.Text(isle.onAc ? Lang.T("mengisi daya") : Lang.T("Baterai"), Ink.Small(11.5f), p.T.Sub, Pad + 84, cy, 120, StringAlignment.Near);
                }
                Button(new RectangleF(W - Pad - 38, cy - 19, 38, 38), "gear", (char)0xE713, Lang.T("Pengaturan Windows"));
                Button(new RectangleF(W - Pad - 80, cy - 19, 38, 38), "tepi", (char)0xE8A9, Lang.T("Pengaturan Tepi"));
            }

            // Networks nearby; saved ones join on a click, new ones ask a password
            void DrawWifi()
            {
                Button(new RectangleF(Pad - 8, 12, 34, 34), "back", (char)0xE72B, Lang.T("Kembali"));
                Caption(Lang.T("Wi-Fi"), Pad + 34, 29);
                if (scanning) p.Text(Lang.T("Mencari…"), Ink.Small(11.5f), p.T.Sub, W - Pad - 120, 29, 80, StringAlignment.Far);
                Button(new RectangleF(W - Pad - 30, 12, 34, 34), "rescan", (char)0xE72C, Lang.T("Cari ulang"));
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
                    string id = "w" + i;
                    Hit(new RectangleF(row.X, row.Y, row.Width, RowH), id);
                    if (open) p.Module(row, 14, p.T.Fill);
                    else if (hotId == id) p.FillRound(row, 14, pressId == id ? p.T.FillPressed : p.T.Fill);
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
                    if (n.Connected && (hotId == id || hotId == "off" + i))
                    {
                        var off = new RectangleF(row.Right - 96, cy - 14, 86, 28);
                        Hit(off, "off" + i);
                        p.FillRound(off, 14, hotId == "off" + i ? p.T.FillHover : p.T.Fill);
                        p.TextCenter(Lang.T("Putuskan"), Ink.Small(11.5f), p.T.Text, off.X + off.Width / 2, cy);
                    }
                    if (open) DrawPassword(new RectangleF(row.X + 12, y + RowH, row.Width - 24, 38));
                    y += row.Height + 2;
                }
                g.Restore(st);
                if (status != null) p.Text(status, Ink.Small(11.5f), p.T.Sub, Pad + 4, H - Foot / 2, W - Pad * 2 - 120, StringAlignment.Near);
                var link = new RectangleF(W - Pad - 120, H - Foot / 2 - 15, 124, 30);
                Hit(link, "wifiSettings");
                if (hotId == "wifiSettings") p.FillRound(link, 15, p.T.FillHover);
                p.TextCenter(Lang.T("Pengaturan Wi-Fi"), Ink.Small(11.5f), p.T.Text, link.X + link.Width / 2, link.Y + 15);
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
                p.FillRound(join, box.Height / 2, ready ? (hotId == "join" ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f) : p.T.Accent) : p.T.Fill);
                p.TextCenter(Lang.T("Sambungkan"), Ink.Semi(12f), ready ? p.T.OnAccent : p.T.Faint, join.X + join.Width / 2, cy);
            }

            void OpenWifi()
            {
                wifiView = true;
                asking = null;
                status = null;
                first = 0;
                Rescan();
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
                if (id == "vol") { isle.setVol(SliderValue(volRect, at)); return true; }
                if (id == "bright" && HasBright) { isle.setBright(SliderValue(brightRect, at)); return true; }
                return false;
            }

            protected override void Wheel(string id, int delta)
            {
                if (wifiView) { first -= Math.Sign(delta); return; }
                float step = delta > 0 ? 0.02f : -0.02f;
                if (id == "bright" && HasBright) isle.setBright(Ease.Clamp01(isle.brightLevel / 100f + step * 2));
                else isle.setVol(Ease.Clamp01(isle.vol + step));
            }

            protected override void Clicked(string id, MouseButtons button)
            {
                if (wifiView) { WifiClicked(id, button); return; }
                if (button == MouseButtons.Right)
                {
                    if (id == "wifi") OpenWifi();
                    else if (id == "bt") BluetoothMenu();
                    else if (id == "vol" || id == "device") SettingsMenu(Lang.T("Pengaturan suara"), "ms-settings:sound");
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
                    case "back": wifiView = false; asking = null; break;
                    case "rescan": Rescan(); break;
                    case "join": if (asking != null && password.Length >= 8) Join(asking, password); break;
                    case "wifiSettings": Open("ms-settings:network-wifi", null); Done(); break;
                }
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
                    else if (key == Keys.Escape) { wifiView = false; Render(); return true; }
                }
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
