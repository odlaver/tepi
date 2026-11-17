using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Tepi
{
    // One command, spoken and typed
    sealed class VoiceCmd
    {
        public string Key;
        // Always needs "Tepi"
        public bool Wake;
        public float Need = 0.65f;
        // English phrases for the offline recognizer
        public string[] Say;
        // Typed or transcribed, Indonesian or English
        public Regex Typed;
        public Func<Match, int> Arg;
        // Spoken without a number
        public int Default = 1;
    }

    sealed class CmdDone
    {
        public char Glyph;
        public string Text = "", Detail = "";
        public bool Ok = true;
        // The volume HUD already answers
        public bool Quiet;
        // Toast click opens the fix
        public Action Fix;
    }

    // Every command Tepi runs without the AI
    static class Perintah
    {
        const string Song = @"(?:\s+(?:the\s+)?(?:music|song|track|musik|lagu|lagunya|musiknya))?";
        const string Times = @"(?:\s+(?<n>\d{1,2}|one|two|three|four|five|twice|satu|dua|tiga|empat|lima)(?:\s*(?:x|kali|times|lagu|songs?|tracks?))?)?";
        const string Bit = @"(?<bit>\s+(?:dikit|sedikit|a bit|a little))?";
        const string On = @"(?:nyalakan|nyalain|hidupkan|hidupin|aktifkan|aktifin|turn on|switch on|enable)";
        const string Off = @"(?:matikan|matiin|nonaktifkan|nonaktifin|turn off|switch off|disable)";
        const string Mic = @"(?:the\s+|my\s+)?(?:mic|mik|mikrofon|microphone)(?:nya)?";
        const string Quiet = @"(?:mode senyap|senyap|jangan ganggu|do not disturb|dnd|quiet mode)";

        static Regex R(string pattern) { return new Regex(pattern, RegexOptions.Compiled); }

        // "nyalakan wifi", "wifi on", "turn off bluetooth"
        static Regex Switch(string noun, bool on)
        {
            return on ? R(@"^(?:" + On + @"\s+(?:the\s+)?" + noun + @"(?:nya)?|" + noun + @"\s+(?:on|nyala|hidup|aktif))$")
                      : R(@"^(?:" + Off + @"\s+(?:the\s+)?" + noun + @"(?:nya)?|" + noun + @"\s+(?:off|mati|nonaktif))$");
        }

        static int Count(Match m)
        {
            var g = m.Groups["n"];
            if (!g.Success) return 1;
            int n;
            if (int.TryParse(g.Value, out n)) return Math.Max(1, Math.Min(10, n));
            switch (g.Value)
            {
                case "two": case "twice": case "dua": return 2;
                case "three": case "tiga": return 3;
                case "four": case "empat": return 4;
                case "five": case "lima": return 5;
            }
            return 1;
        }

        static int Step(Match m) { return m.Groups["bit"].Success ? 5 : 10; }
        static int Percent(Match m) { return Math.Min(100, int.Parse(m.Groups["p"].Value)); }

        public static readonly VoiceCmd[] All =
        {
            new VoiceCmd { Key = "play", Say = new[] { "play", "resume", "play music", "turn on music", "start music", "resume music", "play the music", "turn on the music", "start the music", "resume the music", "play the song", "music on" },
                Typed = R(@"^(?:play|resume|putar|puter|lanjut|lanjutkan|lanjutin|mainkan|(?:play|resume|putar|puter|nyalakan|nyalain|lanjutkan|lanjutin|mainkan|start|turn on)" + Song + @")$") },
            new VoiceCmd { Key = "pause", Say = new[] { "pause", "stop", "pause it", "stop it", "pause music", "stop music", "turn off music", "pause the music", "stop the music", "turn off the music", "pause the song", "stop the song", "music off" },
                Typed = R(@"^(?:pause|stop|jeda|berhenti|(?:pause|stop|jeda|hentikan|matikan|matiin|stopin|pausein)" + Song + @")$") },
            new VoiceCmd { Key = "next", Say = new[] { "next", "skip", "next song", "next track", "skip song", "skip this song", "skip track", "skip it" },
                Typed = R(@"^(?:next|skip|maju|ganti|lewati|skipin)" + Song + Times + @"$|^(?:lagu|song)\s+(?:berikutnya|selanjutnya|next)" + Times + "$"), Arg = Count },
            new VoiceCmd { Key = "prev", Say = new[] { "previous", "back", "go back", "previous song", "previous track", "last song", "go back one song", "back one song" },
                Typed = R(@"^(?:previous|prev|back|go back|balik|mundur|kembali)" + Song + Times + @"$|^(?:lagu|song)\s+(?:sebelumnya|tadi|previous)" + Times + @"$|^(?:balik|kembali)(?:\s+ke)?\s+lagu\s+(?:tadi|sebelumnya)$"), Arg = Count },
            new VoiceCmd { Key = "volup", Default = 10, Say = new[] { "louder", "volume up", "turn it up", "turn up the volume", "turn the volume up", "increase volume", "increase the volume" },
                Typed = R(@"^(?:volume up|louder|turn (?:it|the volume) up|turn up the volume|increase (?:the )?volume|(?:naikkan|naikin|keraskan|kerasin|gedein|besarkan|besarin|gedekan)\s+(?:volume|suara|volumenya|suaranya)|(?:volume|suara)\s+(?:naik|naikin|gedein|kerasin|keraskan))" + Bit + "$"), Arg = Step },
            new VoiceCmd { Key = "voldown", Default = 10, Say = new[] { "quieter", "softer", "volume down", "turn it down", "turn down the volume", "turn the volume down", "lower the volume", "decrease volume", "decrease the volume" },
                Typed = R(@"^(?:volume down|quieter|softer|turn (?:it|the volume) down|turn down the volume|lower (?:the )?volume|decrease (?:the )?volume|(?:turunkan|turunin|kecilkan|kecilin|pelankan|pelanin)\s+(?:volume|suara|volumenya|suaranya)|(?:volume|suara)\s+(?:turun|turunin|kecilin|kecilkan|pelanin))" + Bit + "$"), Arg = Step },
            new VoiceCmd { Key = "vol", Typed = R(@"^(?:set\s+)?(?:the\s+)?(?:volume|vol|suara|volumenya|suaranya)(?:\s+(?:to|ke|jadi|di|at))?\s+(?<p>\d{1,3})\s*(?:%|percent|persen)?$"), Arg = Percent },
            new VoiceCmd { Key = "volmax", Say = new[] { "max volume", "full volume", "volume max", "volume full" },
                Typed = R(@"^(?:max volume|full volume|(?:volume|suara)\s+(?:max|full|maksimal|penuh))$") },
            new VoiceCmd { Key = "mute", Say = new[] { "mute", "mute it", "mute the sound", "mute volume", "sound off" },
                Typed = R(@"^(?:mute|bisukan|(?:mute|matikan|matiin)\s+(?:the\s+)?(?:sound|volume|suara|suaranya))$") },
            new VoiceCmd { Key = "unmute", Say = new[] { "unmute", "unmute it", "unmute the sound", "sound on" },
                Typed = R(@"^(?:unmute|(?:unmute|nyalakan|nyalain)\s+(?:the\s+)?(?:sound|volume|suara|suaranya))$") },

            new VoiceCmd { Key = "wifi.on", Wake = true, Say = new[] { "turn on wifi", "turn on the wifi", "wifi on", "enable wifi" }, Typed = Switch(@"wi[- ]?fi", true) },
            new VoiceCmd { Key = "wifi.off", Wake = true, Need = 0.7f, Say = new[] { "turn off wifi", "turn off the wifi", "wifi off", "disable wifi" }, Typed = Switch(@"wi[- ]?fi", false) },
            new VoiceCmd { Key = "bt.on", Wake = true, Say = new[] { "turn on bluetooth", "bluetooth on", "enable bluetooth" }, Typed = Switch(@"(?:bluetooth|blutut)", true) },
            new VoiceCmd { Key = "bt.off", Wake = true, Say = new[] { "turn off bluetooth", "bluetooth off", "disable bluetooth" }, Typed = Switch(@"(?:bluetooth|blutut)", false) },
            new VoiceCmd { Key = "dark.on", Wake = true, Need = 0.68f, Say = new[] { "dark mode", "dark mode on", "turn on dark mode", "switch to dark mode" },
                Typed = R(@"^(?:(?:" + On + @"|pakai|ganti ke|pindah ke|switch to|use)\s+)?(?:the\s+)?(?:mode gelap|dark mode|tema gelap)(?:\s+(?:on|nyala))?$") },
            new VoiceCmd { Key = "dark.off", Wake = true, Need = 0.68f, Say = new[] { "light mode", "dark mode off", "turn off dark mode", "switch to light mode" },
                Typed = R(@"^(?:(?:" + On + @"|pakai|ganti ke|pindah ke|switch to|use)\s+)?(?:the\s+)?(?:mode terang|light mode|tema terang)$|^" + Off + @"\s+(?:the\s+)?(?:mode gelap|dark mode)$|^(?:mode gelap|dark mode)\s+(?:off|mati)$") },
            new VoiceCmd { Key = "bright.up", Default = 10, Wake = true, Say = new[] { "brighter", "brightness up", "increase brightness", "increase the brightness", "turn up the brightness" },
                Typed = R(@"^(?:(?:naikkan|naikin|tambah|terangkan|terangin|increase|turn up)\s+(?:the\s+)?(?:kecerahan(?: layar)?|brightness|layar)(?:nya)?|(?:kecerahan|brightness)\s+(?:naik|up)|brighter|(?:layar\s+)?lebih terang)" + Bit + "$"), Arg = Step },
            new VoiceCmd { Key = "bright.down", Default = 10, Wake = true, Say = new[] { "dimmer", "brightness down", "decrease brightness", "decrease the brightness", "turn down the brightness" },
                Typed = R(@"^(?:(?:turunkan|turunin|kurangi|redupkan|redupin|decrease|turn down|lower)\s+(?:the\s+)?(?:kecerahan(?: layar)?|brightness|layar)(?:nya)?|(?:kecerahan|brightness)\s+(?:turun|down)|dimmer|(?:layar\s+)?lebih redup)" + Bit + "$"), Arg = Step },
            new VoiceCmd { Key = "bright", Wake = true, Typed = R(@"^(?:set\s+)?(?:the\s+)?(?:kecerahan(?: layar)?|brightness)(?:nya)?(?:\s+(?:to|ke|jadi|di|at))?\s+(?<p>\d{1,3})\s*(?:%|percent|persen)?$"), Arg = Percent },
            new VoiceCmd { Key = "mic.off", Wake = true, Need = 0.7f, Say = new[] { "mute the microphone", "mute microphone", "mute my microphone", "microphone off" },
                Typed = R(@"^(?:" + Off + @"|bisukan|mute)\s+" + Mic + @"$|^(?:mic|mikrofon|microphone)\s+(?:off|mati)$") },
            // Voice can't hear while the mic is off
            new VoiceCmd { Key = "mic.on", Wake = true, Typed = R(@"^(?:" + On + @"|unmute)\s+" + Mic + @"$|^(?:mic|mikrofon|microphone)\s+(?:on|nyala)$") },
            new VoiceCmd { Key = "lock", Wake = true, Need = 0.72f, Say = new[] { "lock the screen", "lock screen", "lock the computer", "lock my computer", "lock the laptop" },
                Typed = R(@"^(?:kunci|lock)\s+(?:the\s+|my\s+)?(?:layar|laptop|komputer|pc|screen|computer)(?:nya)?$") },
            new VoiceCmd { Key = "quiet.on", Wake = true, Say = new[] { "do not disturb", "do not disturb on", "turn on do not disturb", "quiet mode" },
                Typed = R(@"^(?:" + On + @"\s+)?" + Quiet + @"(?:\s+(?:on|nyala))?$") },
            new VoiceCmd { Key = "quiet.off", Wake = true, Say = new[] { "turn off do not disturb", "do not disturb off", "quiet mode off" },
                Typed = R(@"^" + Off + @"\s+(?:the\s+)?" + Quiet + @"$|^" + Quiet + @"\s+(?:off|mati)$") },
            new VoiceCmd { Key = "awake.on", Wake = true, Say = new[] { "keep awake", "stay awake", "keep the laptop awake", "keep the computer awake" },
                Typed = R(@"^(?:(?:" + On + @"|tetap|keep)\s+)?(?:terjaga|awake)$|^(?:jangan tidur|stay awake|keep (?:the )?(?:laptop|computer|pc) awake)$") },
            new VoiceCmd { Key = "awake.off", Wake = true, Say = new[] { "stop keeping awake", "let it sleep", "allow sleep" },
                Typed = R(@"^" + Off + @"\s+(?:terjaga|keep awake)$|^(?:laptop\s+)?boleh tidur$|^(?:let it sleep|allow sleep|stop keeping awake)$") },
            // Typed timers parse richer text in Suruh
            new VoiceCmd { Key = "timer.stop", Wake = true, Say = new[] { "stop the timer", "stop timer", "cancel the timer", "cancel timer" } },
            new VoiceCmd { Key = "focus", Wake = true, Say = new[] { "start focus", "focus mode", "start a focus session", "start focusing" } },
            new VoiceCmd { Key = "timer", Wake = true },
            new VoiceCmd { Key = "open", Wake = true },
        };

        public static VoiceCmd Find(string key)
        {
            foreach (var c in All) if (c.Key == key) return c;
            return null;
        }

        static readonly Regex Polite = new Regex(@"^(?:(?:hey|hai|halo|oke|okay|ok)\s+)?(?:tepi\b[\s,]*)?(?:(?:tolong|please|coba)\s+)?|\s+(?:dong|ya|deh|please|sekarang|now|dulu)$", RegexOptions.IgnoreCase);

        // No punctuation or politeness; titles keep case
        public static string Plain(string raw, bool keepCase = false)
        {
            raw = raw ?? "";
            string s = Regex.Replace(keepCase ? raw : raw.ToLowerInvariant(), @"[.,!?]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return Polite.Replace(s, "").Trim();
        }

        // Typed or transcribed text to a command
        public static VoiceCmd Match(string raw, out int n)
        {
            n = 1;
            string s = Plain(raw);
            if (s.Length == 0) return null;
            foreach (var c in All)
            {
                if (c.Typed == null) continue;
                var m = c.Typed.Match(s);
                if (!m.Success) continue;
                n = c.Arg != null ? c.Arg(m) : c.Default;
                return c;
            }
            return null;
        }
    }

    partial class Island
    {
        static string Spoken(string raw) { return Perintah.Plain(raw); }

        // AI tool actions to command keys
        static readonly Dictionary<string, string> DeviceTools = new Dictionary<string, string>
        {
            { "wifi_on", "wifi.on" }, { "wifi_off", "wifi.off" }, { "bluetooth_on", "bt.on" }, { "bluetooth_off", "bt.off" },
            { "dark_mode_on", "dark.on" }, { "dark_mode_off", "dark.off" },
            { "brightness_up", "bright.up" }, { "brightness_down", "bright.down" }, { "brightness_set", "bright" },
            { "mic_mute", "mic.off" }, { "mic_unmute", "mic.on" }, { "lock_screen", "lock" },
            { "quiet_on", "quiet.on" }, { "quiet_off", "quiet.off" }, { "keep_awake_on", "awake.on" }, { "keep_awake_off", "awake.off" },
        };

        static CmdDone Done(char glyph, string text, string detail = "") { return new CmdDone { Glyph = glyph, Text = text, Detail = detail ?? "" }; }
        static CmdDone Failed(string text) { return new CmdDone { Glyph = IcoFail, Text = text, Ok = false }; }

        // Runs a command, whoever asked
        CmdDone RunCmd(string key, int n, string arg = null)
        {
            char glyph;
            switch (key)
            {
                case "play":
                case "pause":
                case "next":
                case "prev":
                    string said = DoMedia(key, n, out glyph);
                    return said == null ? Failed(Lang.T("Tidak ada pemutar musik")) : Done(glyph, said, key == "play" || key == "pause" ? mediaInfo.Title : "");
                case "volup": return Volume(VolumeTo(vol + n / 100f));
                case "voldown": return Volume(VolumeTo(vol - n / 100f));
                case "vol": return Volume(VolumeTo(n / 100f));
                case "volmax": return Volume(VolumeTo(1));
                case "mute": return Volume(MuteTo(true));
                case "unmute": return Volume(MuteTo(false));
                case "wifi.on": return Radio(true, true);
                case "wifi.off": return Radio(true, false);
                case "bt.on": return Radio(false, true);
                case "bt.off": return Radio(false, false);
                case "dark.on":
                case "dark.off":
                    bool dark = key == "dark.on";
                    if (winDark != dark) ToggleWindowsDark();
                    return Done(IcoDarkMode, dark ? Lang.T("Mode gelap") : Lang.T("Mode terang"));
                case "bright.up": return BrightTo(brightLevel + n);
                case "bright.down": return BrightTo(brightLevel - n);
                case "bright": return BrightTo(n);
                case "mic.off":
                case "mic.on":
                    if (!micPresent) return Failed(Lang.T("Tidak ada mikrofon"));
                    volume.SetMicMute(key == "mic.off");
                    return Done(key == "mic.off" ? IcoMicOff : Ink.IcoMic, key == "mic.off" ? Lang.T("Mikrofon mati") : Lang.T("Mikrofon nyala"));
                case "lock":
                    SessionPower.Lock();
                    return Done(Ink.IcoLock, Lang.T("Layar dikunci"));
                case "quiet.on":
                case "quiet.off":
                    settings.Set("dnd", key == "quiet.on");
                    settings.Save();
                    Kick();
                    return Done(Ink.IcoQuiet, key == "quiet.on" ? Lang.T("Senyap nyala") : Lang.T("Senyap mati"));
                case "awake.on":
                    SetAwake(TimeSpan.MaxValue);
                    return Done(IcoAwake, Lang.T("Laptop tetap terjaga"));
                case "awake.off":
                    SetAwake(TimeSpan.Zero);
                    return Done(IcoAwake, Lang.T("Laptop boleh tidur lagi"));
                case "timer":
                    if (n < 1 || n > 600) return Failed(Lang.T("Lama timer tidak jelas"));
                    StartTimer(TimerKind.Countdown, n);
                    return Done(Ink.IcoTimer, Lang.F("Timer {0} menit", n));
                case "focus":
                    StartFocus(25);
                    return Done(Ink.IcoTimer, Lang.F("Fokus {0} menit", 25));
                case "timer.stop":
                    if (!TimerOn) return Failed(Lang.T("Tidak ada timer yang jalan"));
                    StopTimer();
                    return Done(Ink.IcoTimer, Lang.T("Timer dihentikan"));
                case "open":
                    var act = OpenApp(arg ?? "");
                    return new CmdDone { Glyph = act.Glyph, Text = act.Text, Ok = act.Glyph != IcoFail };
            }
            return null;
        }

        static CmdDone Volume(string said) { return new CmdDone { Glyph = Ink.IcoVol2, Text = said, Quiet = true }; }

        CmdDone Radio(bool wifi, bool on)
        {
            if (wifi ? !radios.HasWifi : !radios.HasBluetooth) return Failed(wifi ? Lang.T("Tidak ada Wi-Fi") : Lang.T("Tidak ada Bluetooth"));
            if (radios.Denied)
            {
                var no = Failed(Lang.T("Windows tidak mengizinkan Tepi mengatur radio"));
                no.Detail = Lang.T("Klik untuk membuka izinnya");
                no.Fix = delegate { OpenSettings("ms-settings:privacy-radios"); };
                return no;
            }
            radios.Set(wifi, on);
            return Done(wifi ? Ink.IcoWifi : Ink.IcoBluetooth, wifi ? (on ? Lang.T("Wi-Fi dinyalakan") : Lang.T("Wi-Fi dimatikan")) : (on ? Lang.T("Bluetooth dinyalakan") : Lang.T("Bluetooth dimatikan")));
        }

        CmdDone BrightTo(int pct)
        {
            if (brightLevel < 0) return Failed(Lang.T("Kecerahan layar ini tidak bisa diatur"));
            pct = Math.Max(0, Math.Min(100, pct));
            brightLevel = pct;
            brightBar.Snap(pct / 100f);
            brightness.Set(pct);
            Kick();
            return Done(Ink.IcoSun, Lang.F("Kecerahan {0}%", pct));
        }

        // Typed or transcribed; null if none
        ChatMsg TryCommand(string raw)
        {
            int n;
            var c = Perintah.Match(raw, out n);
            if (c == null) return null;
            var done = RunCmd(c.Key, n);
            if (done == null) return null;
            return done.Ok ? Did(done.Glyph, done.Text) : Said(done.Text);
        }
    }
}
