using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Tepi
{
    // Kontrol tab: sound, screen, radios, quiet, timers
    partial class Island
    {
        readonly BrightnessService brightness = new BrightnessService();
        readonly RadioService radios = new RadioService();
        List<AudioDevice> audioDevices = new List<AudioDevice>();
        string audioDefault = "";
        bool devicesOpen;
        DateTime devicesUtc = DateTime.MinValue, audioToastUtc = DateTime.MinValue, startedUtc = DateTime.UtcNow;
        int brightLevel = -1;
        readonly Spring brightBar = new Spring(0.5f, 320, 30, 0.001f);
        readonly Roller brightRoller = new Roller();
        readonly Dictionary<string, Spring> switches = new Dictionary<string, Spring>();

        // Quiet mode: only urgent toasts pass
        bool Quiet { get { return settings.GetBool("dnd", false); } }

        void StartControls()
        {
            brightness.Changed += (level, own) => Post(() => OnBrightness(level, own));
            radios.Changed += () => Post(Kick);
            volume.DevicesChanged += (list, def) => Post(() => { audioDevices = list; audioDefault = def; Kick(); });
            volume.DefaultChanged += d => Post(() => OnAudioDevice(d));
            brightness.Start();
            radios.Start();
        }

        void OnBrightness(int level, bool own)
        {
            bool first = brightLevel < 0;
            brightLevel = level;
            brightBar.Target = level / 100f;
            brightRoller.Set(level.ToString());
            if (first) { brightBar.Snap(level / 100f); return; }
            // Brightness keys: HUD like volume
            if (!own && !expanded) ShowToast(View.Brightness, 1.5, 1, false, true);
            Kick();
        }

        AudioDevice audioShown;

        void OnAudioDevice(AudioDevice d)
        {
            // Chip follows the new default
            audioDefault = d.Id;
            volume.RefreshDevices();
            // Startup and resume reshuffle devices quietly
            if ((DateTime.UtcNow - startedUtc).TotalSeconds < 8 || (DateTime.UtcNow - audioToastUtc).TotalSeconds < 2) return;
            audioToastUtc = DateTime.UtcNow;
            audioShown = d;
            var t = ShowToast(View.AudioDevice, 2.6, 2, false, true);
            if (t != null) t.Data = d;
            Kick();
        }

        static char DeviceGlyph(int form)
        {
            switch (form)
            {
                case 3: return '';  // headphones
                case 5: return '';  // headset
                case 9: return '';  // display
                default: return ''; // speakers
            }
        }

        AudioDevice DefaultDevice()
        {
            foreach (var d in audioDevices) if (d.Id == audioDefault) return d;
            return null;
        }

        string DefaultDeviceName()
        {
            var d = DefaultDevice();
            return d != null ? d.Name : "";
        }

        char VolumeGlyph() { return muted ? Ink.IcoMute : vol < 0.01f ? Ink.IcoVol0 : vol < 0.34f ? Ink.IcoVol1 : vol < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3; }

        // Page

        // Grid: three slider rows beside the radios
        const float CtlRowH = 56, CtlTimerH = 100, CtlDeviceRow = 40, CtlToggleD = 50;
        const float CtlGridH = 3 * CtlRowH + 2 * Ink.ModuleGap;
        static readonly string[] DevIds = { "dev0", "dev1", "dev2", "dev3", "dev4" }, ChipIds = { "chip0", "chip1", "chip2", "chip3", "chip4" };
        static readonly string[] ChipNames = { "Fokus 25", "5 mnt", "10 mnt", "15 mnt", "Stopwatch" };
        // Delegates built once, never per frame
        Action actMute, actDevices, actWifi, actBt, actQuiet, actHide, actVolStep, actBrightStep, actTmPause, actTmAdd, actTmLap, actTmStop;
        Action<float> setVol, setBright;
        Action[] chipActs, devActs = new Action[0];
        List<AudioDevice> devActsFor;
        // Timer text redone once a second
        int tmShownS = -1, tmLabelKey = -1, tmLapCount = -1;
        string tmLabel = "", tmLap = "";
        // Live ring and countdown read these
        Action<DateTime> ctlRingPaint;
        Func<DateTime, double> ctlClockPaint;
        float ctlRingX, ctlRingY, ctlClockX, ctlClockY;
        Color ctlRingC, ctlClockC;
        // Own roller: shell Moving ignores it
        readonly Roller ctlRoller = new Roller();
        // Device name split once per change
        string devSplitFor, devTitle = "", devSub = "";

        void ControlActs()
        {
            if (actMute != null) return;
            actMute = delegate { volume.SetMute(!muted); };
            actDevices = delegate { devicesOpen = !devicesOpen; devicesUtc = DateTime.UtcNow; if (devicesOpen) volume.RefreshDevices(); Kick(); };
            actWifi = delegate { if (radios.Denied) OpenSettings("ms-settings:network-wifi"); else radios.Toggle(true); };
            actBt = delegate { if (radios.Denied) OpenSettings("ms-settings:bluetooth"); else radios.Toggle(false); };
            actQuiet = delegate { settings.Toggle("dnd", false); Kick(); };
            actHide = delegate { ToggleHidden(); };
            setVol = delegate (float f)
            {
                vol = f;
                volBar.Target = f;
                volBar.Snap(f);
                volume.SetLatest(f);
            };
            setBright = delegate (float f)
            {
                brightLevel = (int)Math.Round(f * 100);
                brightBar.Snap(f);
                brightness.Set(brightLevel);
            };
            actVolStep = delegate { setVol(StepUp(vol)); };
            actBrightStep = delegate { setBright(StepUp(brightBar.Target)); };
            actTmPause = delegate { ToggleTimerPause(); };
            actTmAdd = delegate { AddTimerMinute(); };
            actTmLap = delegate { Lap(); };
            actTmStop = delegate { StopTimer(); };
            chipActs = new Action[]
            {
                delegate { StartTimer(TimerKind.Focus, 25); },
                delegate { StartTimer(TimerKind.Countdown, 5); },
                delegate { StartTimer(TimerKind.Countdown, 10); },
                delegate { StartTimer(TimerKind.Countdown, 15); },
                delegate { StartTimer(TimerKind.Stopwatch, 0); },
            };
            ctlRingPaint = t => Ring(ctlRingX, ctlRingY, 24f, TimerFraction(), ctlRingC, 4f);
            ctlClockPaint = t =>
            {
                int secs = (int)(timerKind == TimerKind.Stopwatch ? Elapsed().TotalSeconds : Math.Ceiling(Remaining().TotalSeconds));
                if (secs != tmShownS) { tmShownS = secs; ctlRoller.Set(TimerClock()); }
                ctlRoller.Draw(p, Ink.Hero, ctlClockC, ctlClockX, ctlClockY, StringAlignment.Near, t);
                if (ctlRoller.Moving(t)) return 1 / 60.0;
                return TimerOn && !timerPaused ? TimerWait(t) : 0;
            };
        }

        // Enter: next tenth, wraps at full
        static float StepUp(float v) { return v >= 0.995f ? 0 : Math.Min(1, (float)Math.Floor(v * 10 + 1.0001f) / 10); }

        void DeviceActs()
        {
            if (ReferenceEquals(devActsFor, audioDevices)) return;
            devActsFor = audioDevices;
            devActs = new Action[Math.Min(5, audioDevices.Count)];
            for (int i = 0; i < devActs.Length; i++)
            {
                string devId = audioDevices[i].Id;
                devActs[i] = delegate { volume.SetDefault(devId); devicesOpen = false; devicesUtc = DateTime.UtcNow; Kick(); };
            }
        }

        float DeviceListH()
        {
            int n = Math.Min(5, audioDevices.Count);
            return n == 0 ? 56 : n * CtlDeviceRow + 12;
        }

        float ControlHeight()
        {
            float h = PanelTop + 2 + (devicesOpen ? CtlRowH + Ink.ModuleGap + DeviceListH() : CtlGridH + Ink.ModuleGap + CtlTimerH);
            return Math.Min(MaxH, h + Ink.ModuleInset);
        }

        DateTime devicesAskedUtc = DateTime.MinValue;

        // Control Center: radios, sliders, output, timer
        void DrawControlPage(float w, float h, DateTime now)
        {
            ControlActs();
            // Chip needs the device name
            if (audioDevices.Count == 0 && (now - devicesAskedUtc).TotalSeconds > 30) { devicesAskedUtc = now; volume.RefreshDevices(); }
            float x0 = Ink.ModuleInset, full = w - 2 * x0, y = PanelTop + 2, keep = p.Alpha;
            // Output list swaps in for the grid
            float k = Ease.OutCubic((float)((now - devicesUtc).TotalSeconds / 0.3));
            bool hold = hitsOn;
            if (k < 1)
            {
                hitsOn = false;
                p.Alpha = keep * (1 - Ease.Clamp01(k * 1.6f));
                if (devicesOpen) DrawControlGrid(x0, y, full, now, false);
                else DrawDeviceMode(x0, y, full);
                hitsOn = hold;
            }
            p.Alpha = keep * Ease.Clamp01(k * 1.2f);
            if (k < 0.7f) hitsOn = false;
            float rise = Anim.Reduced ? 0 : (1 - k) * 8;
            if (devicesOpen) DrawDeviceMode(x0, y + rise, full);
            else DrawControlGrid(x0, y + rise, full, now, k >= 1);
            hitsOn = hold;
            p.Alpha = keep;
        }

        // Radios left; brightness, sound, output right
        void DrawControlGrid(float x0, float y, float full, DateTime now, bool live)
        {
            float gap = Ink.ModuleGap, col = (full - gap) / 2, x1 = x0 + col + gap, keep = p.Alpha, dy, ry = y;
            p.Alpha = keep * RowIn(0, now, out dy);
            DrawToggles(new RectangleF(x0, y + dy, col, CtlGridH));
            if (brightness.Supported)
            {
                p.Alpha = keep * RowIn(1, now, out dy);
                BigSlider(new RectangleF(x1, ry + dy, col, CtlRowH), "ctlBright", brightBar.Value, Ink.IcoSun, Lang.T("Layar"),
                          Ink.Percent(Math.Max(0, brightLevel)), setBright, actBrightStep, null, null);
                ry += CtlRowH + gap;
            }
            // Sound takes the brightness row when absent
            float sh = CtlGridH - CtlRowH - gap - (ry - y);
            p.Alpha = keep * RowIn(2, now, out dy);
            SoundSlider(new RectangleF(x1, ry + dy, col, sh));
            p.Alpha = keep * RowIn(3, now, out dy);
            DeviceChip(new RectangleF(x1, y + CtlGridH - CtlRowH + dy, col, CtlRowH));
            p.Alpha = keep * RowIn(4, now, out dy);
            DrawTimerModule(new RectangleF(x0, y + CtlGridH + gap + dy, full, CtlTimerH), now, live);
            p.Alpha = keep;
        }

        // Sound and output on top, list below
        void DrawDeviceMode(float x0, float y, float full)
        {
            float gap = Ink.ModuleGap, col = (full - gap) / 2;
            SoundSlider(new RectangleF(x0, y, col, CtlRowH));
            DeviceChip(new RectangleF(x0 + col + gap, y, col, CtlRowH));
            DrawDeviceList(new RectangleF(x0, y + CtlRowH + gap, full, DeviceListH()));
        }

        void SoundSlider(RectangleF r)
        {
            BigSlider(r, "ctlVol", muted ? 0 : volBar.Value, VolumeGlyph(), Lang.T("Suara"),
                      muted ? Lang.T("Bisu") : Ink.Percent((int)Math.Round(vol * 100)), setVol, actVolStep, "ctlMute", actMute);
        }

        // "Speakers (Realtek Audio)" as two lines
        void SplitDevice(string name)
        {
            if (name == devSplitFor) return;
            devSplitFor = name;
            int i = name.IndexOf(" (", StringComparison.Ordinal);
            if (i > 0 && name.EndsWith(")", StringComparison.Ordinal) && name.Length - i > 3)
            {
                devTitle = name.Substring(0, i);
                devSub = name.Substring(i + 2, name.Length - i - 3);
            }
            else { devTitle = name; devSub = ""; }
        }

        // Output device; opens the list
        void DeviceChip(RectangleF r)
        {
            var dev = DefaultDevice();
            SplitDevice(dev != null ? dev.Name : Lang.T("Perangkat output"));
            AddHit(r, "ctlDevices", actDevices);
            float s = Squish("ctlDevices", r, 0.97f), cy = r.Y + r.Height / 2, tx = r.X + 46, room = r.Width - 46 - 38;
            p.Module(r, Ink.RadiusModule, devicesOpen && !Pressed("ctlDevices") ? p.T.FillHover : FillFor("ctlDevices"));
            p.Icon(DeviceGlyph(dev != null ? dev.Form : 1), 17f, p.T.Text, r.X + 25, cy);
            if (devSub.Length == 0) p.Text(devTitle, Ink.Semi(13.5f), p.T.Text, tx, cy, room, StringAlignment.Near);
            else
            {
                p.Text(devTitle, Ink.Semi(13.5f), p.T.Text, tx, cy - 8, room, StringAlignment.Near);
                p.Text(devSub, Ink.Caption, p.T.Sub, tx, cy + 9, room, StringAlignment.Near);
            }
            p.Symbol(devicesOpen ? Sym.ChevronUp : Sym.ChevronDown, r.Right - 22, cy, 11f, p.T.Sub);
            Unsquish(r, s);
        }

        void DrawDeviceList(RectangleF r)
        {
            DeviceActs();
            Module(r, Ink.RadiusModule);
            int n = Math.Min(devActs.Length, Math.Min(5, audioDevices.Count));
            if (n == 0)
            {
                p.TextCenter(Lang.T("Tidak ada perangkat output"), Ink.Callout, p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
                return;
            }
            for (int i = 0; i < n; i++)
            {
                var d = audioDevices[i];
                var row = new RectangleF(r.X + 6, r.Y + 6 + i * CtlDeviceRow, r.Width - 12, CtlDeviceRow);
                ListRow(row, DeviceGlyph(d.Form), d.Name, null, null, d.Id == audioDefault, DevIds[i], devActs[i], i < n - 1);
            }
        }

        // Radios, quiet, mini and hide
        void DrawToggles(RectangleF r)
        {
            Module(r, Ink.RadiusModule);
            const int n = 5;
            float cx, cy;
            ToggleSlot(r, 0, n, out cx, out cy);
            CircleToggle(cx, cy, CtlToggleD, Ink.IcoWifi, radios.WifiOn, radios.HasWifi, Lang.T("Wi-Fi"), "tileWifi", actWifi);
            ToggleSlot(r, 1, n, out cx, out cy);
            CircleToggle(cx, cy, CtlToggleD, Ink.IcoBluetooth, radios.BluetoothOn, radios.HasBluetooth, Lang.T("Bluetooth"), "tileBt", actBt);
            ToggleSlot(r, 2, n, out cx, out cy);
            CircleToggle(cx, cy, CtlToggleD, Ink.IcoQuiet, Quiet, true, Lang.T("Senyap"), "tileQuiet", actQuiet);
            ToggleSlot(r, 3, n, out cx, out cy);
            CircleToggle(cx, cy, CtlToggleD, IcoMini, MiniWanted(), true, Lang.T("Mini"), "tileMini", MiniAct());
            ToggleSlot(r, 4, n, out cx, out cy);
            CircleToggle(cx, cy, CtlToggleD, Ink.IcoHide, false, true, Lang.T("Sembunyikan"), "tileHide", actHide);
        }

        // 2x2 to four, 3x2 to six
        static void ToggleSlot(RectangleF r, int i, int n, out float cx, out float cy)
        {
            const float d = CtlToggleD, block = CtlToggleD + 17;
            int cols = n <= 4 ? 2 : 3, rows = (n + cols - 1) / cols, row = i / cols, inRow = Math.Min(cols, n - row * cols);
            float gx = (r.Width - cols * d) / (cols + 1), gy = (r.Height - rows * block) / (rows + 1);
            // Short last row stays centred
            float start = r.X + (r.Width - inRow * d - (inRow - 1) * gx) / 2;
            cx = start + (i % cols) * (d + gx) + d / 2;
            cy = r.Y + gy * (row + 1) + block * row + d / 2;
        }

        void OpenSettings(string uri)
        {
            try { Process.Start(uri); } catch (Exception ex) { Log.Write("settings: " + ex.Message); }
        }

        // Timer: presets idle, countdown when running
        void DrawTimerModule(RectangleF r, DateTime now, bool live)
        {
            Module(r, Ink.RadiusModule);
            float pad = Ink.ModulePad;
            if (!TimerOn)
            {
                p.Icon(Ink.IcoTimer, 15f, p.T.Sub, r.X + 25, r.Y + 26);
                p.Text(Lang.T("Timer"), Ink.Headline, p.T.Text, r.X + 46, r.Y + 26, 200, StringAlignment.Near);
                // Presets share the width evenly
                int n = ChipNames.Length;
                float gap = 8, cw = (r.Width - 2 * pad - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++)
                    Pill(new RectangleF(r.X + pad + i * (cw + gap), r.Bottom - pad - 32, cw, 32), ChipIds[i], Lang.T(ChipNames[i]), PillKind.Secondary, chipActs[i]);
                return;
            }
            float cy = r.Y + r.Height / 2;
            ctlRingX = r.X + pad + 26;
            ctlRingY = cy;
            ctlRingC = timerPaused ? p.T.Faint : timerKind == TimerKind.Stopwatch || timerKind == TimerKind.Break ? p.T.Sub : p.T.Accent;
            if (live) LiveRect(new RectangleF(ctlRingX - 27, cy - 27, 54, 54), timerPaused ? 0 : TickFps, ctlRingPaint);
            else ctlRingPaint(now);
            int key = (int)timerKind * 2 + (timerPaused ? 1 : 0);
            if (key != tmLabelKey) { tmLabelKey = key; tmLabel = timerPaused ? TimerLabel() + Lang.T(" · dijeda") : TimerLabel(); }
            float tx = ctlRingX + 24 + pad;
            p.Text(tmLabel, Ink.Callout, p.T.Sub, tx, cy - 19, 220, StringAlignment.Near);
            if (laps.Count > 0)
            {
                if (laps.Count != tmLapCount) { tmLapCount = laps.Count; tmLap = Lang.F("Putaran {0} · {1}", laps.Count, FormatSpan(laps[0])); }
                float lw = p.Measure(tmLabel, Ink.Callout);
                p.Text(tmLap, Ink.Caption, p.T.Faint, tx + lw + 10, cy - 19, 160, StringAlignment.Near);
            }
            // Countdown redraws alone each second
            ctlClockX = tx - 1;
            ctlClockY = cy + 10;
            ctlClockC = timerPaused ? p.T.Sub : p.T.Text;
            if (live) LiveRect(new RectangleF(tx - 3, ctlClockY - 28, 180, 56), ctlClockPaint);
            else ctlClockPaint(now);
            // Right to left; pause carries accent
            float bx = r.Right - pad - 20;
            IconButton(bx, cy, 40, "tmStop", Sym.Stop, actTmStop, false);
            bx -= 48;
            if (timerKind == TimerKind.Stopwatch) IconButton(bx, cy, 40, "tmLap", '', actTmLap, false);
            else IconButton(bx, cy, 40, "tmAdd", Sym.Plus, actTmAdd, false);
            bx -= 50;
            IconButton(bx, cy, 44, "tmPause", timerPaused ? Sym.Play : Sym.Pause, actTmPause, true);
        }

        static string FormatSpan(TimeSpan t) { return ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00") + "." + (t.Milliseconds / 100); }

        // Small secondary pill
        void Chip(RectangleF r, string id, string label, Action act) { Pill(r, id, label, PillKind.Secondary, act); }

        // iOS switch with a sliding knob
        void Switch(RectangleF r, string id, bool on, Action toggle)
        {
            Spring s;
            if (!switches.TryGetValue(id, out s)) { s = new Spring(on ? 1 : 0, 520, 30, 0.004f); switches[id] = s; }
            s.Target = on ? 1 : 0;
            AddHit(RectangleF.Inflate(r, 4, 4), id, toggle);
            var track = new RectangleF(r.X, r.Y + r.Height / 2 - 11, 38, 22);
            p.FillRound(track, 11, Ink.Mix(p.T.Quaternary, p.T.Accent, s.Value));
            float k = Ease.Clamp01(s.Value);
            p.Circle(track.X + 11 + k * 16, track.Y + 11, Pressed(id) ? 8.2f : 9, Ink.Mix(p.T.Text, p.T.OnAccent, k));
        }

        bool SwitchesMoving()
        {
            foreach (var s in switches.Values) if (!s.Settled) return true;
            return PartsMoving() || (DateTime.UtcNow - devicesUtc).TotalSeconds < 0.32;
        }

        void StepSwitches(float dt)
        {
            foreach (var s in switches.Values) s.Step(dt);
            brightBar.Step(dt);
            StepParts(dt);
        }

        // Compact HUDs

        void DrawBrightness(float w, float h, DateTime now) { LevelHud(w, h, Ink.IcoSun, brightBar.Value, brightRoller, now); }

        float AudioDeviceWidth()
        {
            var d = toast != null ? toast.Data as AudioDevice : null;
            if (d == null || p.G == null) return 280;
            return Clamp(p.Measure(d.Name, Ink.Headline) + 90, 240, 440);
        }

        void DrawAudioDevice(float w, float h)
        {
            var d = toast != null ? toast.Data as AudioDevice : null;
            if (d == null) return;
            float cy = h / 2;
            p.Module(new RectangleF(11, cy - 15, 30, 30), 15, p.T.Fill);
            p.Icon(DeviceGlyph(d.Form), 15f, p.T.Text, 26, cy);
            p.Text(d.Name, Ink.Headline, p.T.Text, 52, cy - 8, w - 52 - 20, StringAlignment.Near);
            p.Text(Lang.T("Suara keluar di sini"), Ink.Caption, p.T.Sub, 52, cy + 10, w - 52 - 20, StringAlignment.Near);
        }
    }
}
