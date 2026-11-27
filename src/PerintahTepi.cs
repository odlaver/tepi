using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Tepi
{
    // Tepi's own features by word: "tepi buka pengaturan dock"
    partial class Island
    {
        const string TOpen = @"(?:buka|bukain|bukakan|tampilkan|tampilin|munculkan|munculin|lihat|liat|pergi ke|ke|open|show|go to)";
        const string TClose = @"(?:tutup|tutupin|sembunyikan|sembunyiin|umpetin|close|hide)";
        const string TOn = @"(?:nyalakan|nyalain|hidupkan|hidupin|aktifkan|aktifin|pakai|pake|ganti ke|pindah ke|turn on|enable|use|switch to)";
        const string TOff = @"(?:matikan|matiin|nonaktifkan|nonaktifin|turn off|disable)";

        static readonly Regex TepiWord = new Regex(@"\b(?:di|ke|in|on)?\s*tepi(?:nya)?\b", RegexOptions.IgnoreCase);
        static readonly Regex TepiPolite = new Regex(@"^(?:(?:hey|hai|halo|oke|okay|ok)\s+)?(?:(?:tolong|please|coba|bisa)\s+)?|\s+(?:dong|ya|deh|please|sekarang|now|dulu|yuk|aja)$");

        // Prefs sections by the words people use
        static readonly string[][] PrefsWords =
        {
            new[] { "dock", "dock", "taskbar", "bar atas", "top bar" },
            new[] { "music", "musik", "lirik", "lagu", "visualizer", "music", "lyrics" },
            new[] { "look", "tema", "warna", "kaca", "material", "theme", "color" },
            new[] { "packs", "paket tema", "theme pack" },
            new[] { "ai", "ai", "model" },
            new[] { "siap", "siapkan", "suara dan ai" },
            new[] { "dikte", "dikte", "dictation", "dictate" },
            new[] { "keys", "pintasan", "shortcut", "hotkey", "keyboard" },
            new[] { "messages", "pesan", "notifikasi", "notif", "messages", "notifications" },
            new[] { "phone", "hp", "ponsel", "android", "phone" },
            new[] { "integrasi", "integrasi", "kulia", "notion", "todoist", "kalender", "ical", "integrations" },
            new[] { "remind", "pengingat", "sholat", "salat", "cuaca", "minum", "reminders" },
            new[] { "agents", "agen", "agent", "claude", "codex" },
            new[] { "about", "tentang", "versi", "about", "version" },
            new[] { "access", "aksesibilitas", "accessibility", "senyap" },
            new[] { "files", "rak", "berkas", "clipboard", "screenshot", "files", "shelf" },
            new[] { "island", "letak", "bentuk", "posisi", "position" },
            new[] { "behave", "perilaku", "behavior" },
            new[] { "idle", "saat diam", "idle" },
            new[] { "home", "ringkasan", "summary" },
            new[] { "tabs", "tab panel", "tab" },
            new[] { "system", "sistem", "system", "hud" },
            new[] { "general", "dasar", "umum", "bahasa", "jam", "pembaruan", "update", "general", "language" },
        };

        // Panel tabs; the last flag: safe without "tepi"
        static readonly object[][] PageWords =
        {
            new object[] { PageShelf, true, "rak", "shelf" },
            new object[] { PageHome, true, "ringkasan", "summary" },
            new object[] { PageMusic, false, "musik", "music", "lagu" },
            new object[] { PageAgents, true, "agen", "agents" },
            new object[] { PageClass, false, "kuliah", "jadwal", "class", "schedule" },
            new object[] { PageMessages, false, "pesan", "messages" },
            new object[] { PageControl, false, "kontrol", "control" },
            new object[] { PageRemind, false, "pengingat", "reminders" },
            new object[] { PagePhone, false, "hp", "phone" },
            new object[] { PageConvert, false, "ubah format", "konversi", "convert" },
            new object[] { PageVoice, false, "bicara", "riwayat suara", "voice" },
        };

        static bool Has(string s, string words) { return Regex.IsMatch(s, @"\b(?:" + words + @")\b"); }

        static string Section(string s)
        {
            foreach (var row in PrefsWords)
                for (int i = 1; i < row.Length; i++)
                    if (Has(s, Regex.Escape(row[i]))) return row[0];
            return null;
        }

        // What a sentence asks of Tepi, as "what:detail"; null when it isn't about Tepi
        internal static string TepiIntent(string raw)
        {
            string s = Regex.Replace((raw ?? "").ToLowerInvariant(), @"[.,!?]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            bool named = TepiWord.IsMatch(s);
            s = Regex.Replace(TepiWord.Replace(s, " "), @"\s+", " ").Trim();
            s = TepiPolite.Replace(s, "").Trim();
            s = TepiPolite.Replace(s, "").Trim();
            bool open = Has(s, TOpen), close = Has(s, TClose), on = Has(s, TOn), off = Has(s, TOff);

            if (Has(s, @"jendela lirik|lirik (?:melayang|terpisah|pip)|lyrics? window|picture in picture|pip") || (Has(s, @"lirik(?:nya)?|lyrics?") && Has(s, @"jendela|window")))
                return close || off || Has(s, @"kembalikan|balikin|return") ? "lyric:off" : "lyric:on";

            // Lyrics sent home to the island
            if (Has(s, @"lirik(?:nya)?|lyrics?") && Has(s, @"island") && Has(s, @"kembalikan|balikin|pindahkan|pindahin|return|back|ke")) return "lyric:off";

            if ((named || Has(s, @"suruh")) && Has(s, @"chat|obrolan|ngobrol|kolom chat|suruh|percakapan|conversation|ai") && !close && !Has(s, @"pengaturan|setting|settings|setelan")) return "chat";

            if (Has(s, @"pengaturan|setting|settings|setelan|preferensi|preferences|opsi|options"))
            {
                // "pengaturan windows" means Windows'
                if (Has(s, @"windows|sistem windows|bluetooth|wi-?fi|display|layar|suara|sound")) return null;
                string sec = Section(Regex.Replace(s, @"\b(?:pengaturan|setting|settings|setelan|preferensi|preferences|opsi|options|buka|open|fitur)\b", " "));
                // Without "tepi", plain settings stay Windows'
                if (!named && (sec == null || Array.IndexOf(OwnSections, sec) < 0)) return null;
                return "prefs:" + sec;
            }

            if (Has(s, @"bar atas|top bar|menu bar|tampilan mac|ala mac|mac style"))
            {
                if (Has(s, @"sembunyi(?:kan)? otomatis|auto ?hide")) return off || Has(s, @"jangan") ? "barhide:off" : "barhide:on";
                return close || off ? "bar:off" : "bar:on";
            }
            if (Has(s, @"tampilan biasa|normal view|tampilan normal")) return "bar:off";

            if (Has(s, @"dock") && (on || off || open || close)) return on || open ? "dock:on" : "dock:off";

            if (Has(s, @"mode mini|mini mode|mini")) return off || close || Has(s, @"normal|biasa") ? "mini:off" : "mini:on";

            if (Has(s, @"teleprompter|prompter") && !close) return "prompter";

            foreach (var row in PageWords)
                for (int i = 2; i < row.Length; i++)
                {
                    if (!Has(s, Regex.Escape((string)row[i]))) continue;
                    if (!(named || (bool)row[1] || Has(s, @"tab|panel"))) break;
                    if (!(open || Has(s, @"tab|panel") || s == (string)row[i])) break;
                    return "tab:" + (int)row[0];
                }

            if (Has(s, @"panel")) return close ? "panel:off" : "panel:on";

            if (named && Regex.IsMatch(s, @"^(?:" + TClose + @"|sembunyi|minggir)(?:\s+(?:island|diri))?$")) return "island:off";
            if (named && Regex.IsMatch(s, @"^(?:" + TOpen + @"|muncul|balik)(?:\s+(?:island|lagi))?$")) return "island:on";
            return null;
        }

        // Set while a sentence that began with "Tepi" runs
        bool tepiCalled;

        // Sections only Tepi has: safe without "tepi"
        static readonly string[] OwnSections = { "dock", "dikte", "agents", "files", "home", "integrasi", "packs", "siap" };

        ChatMsg TryTepi(string raw)
        {
            string intent = TepiIntent(raw);
            return intent == null ? null : RunTepi(intent);
        }

        ChatMsg RunTepi(string intent)
        {
            int colon = intent.IndexOf(':');
            string what = colon < 0 ? intent : intent.Substring(0, colon), arg = colon < 0 ? "" : intent.Substring(colon + 1);
            bool yes = arg == "on";
            switch (what)
            {
                case "lyric":
                    if (yes && !mediaInfo.Present) return Said(Lang.T("Jendela lirik muncul saat ada lagu diputar"));
                    if (yes != LyricWinWanted) ToggleLyricWin();
                    return Did(Ink.IcoOpen, yes ? Lang.T("Jendela lirik dibuka") : Lang.T("Lirik kembali ke island"));
                case "chat":
                    OpenSuruh();
                    return Did(Ink.IcoOpen, Lang.T("Chat Suruh Tepi dibuka"));
                case "prefs":
                    if (arg.Length == 0) OpenPrefs(); else OpenPrefs(arg);
                    return Did(Ink.IcoOpen, arg.Length == 0 ? Lang.T("Pengaturan Tepi dibuka") : Lang.F("Pengaturan Tepi › {0}", SectionName(arg)));
                case "barhide":
                    if (!TopBarOn) SetDesk(true);
                    SetBarHide(yes);
                    return Did(Ink.IcoOpen, yes ? Lang.T("Bar atas sembunyi otomatis") : Lang.T("Bar atas selalu tampil"));
                case "bar":
                    SetDesk(yes);
                    return Did(Ink.IcoOpen, yes ? Lang.T("Tampilan bar atas") : Lang.T("Tampilan biasa"));
                case "dock":
                    SetFlag("dockbar.on", yes);
                    return Did(Ink.IcoOpen, yes ? Lang.T("Dock dinyalakan") : Lang.T("Dock dimatikan"));
                case "mini":
                    if (yes != MiniOn()) ToggleMini();
                    return Did(Ink.IcoHide, yes ? Lang.T("Mode Mini") : Lang.T("Island ukuran biasa"));
                case "prompter":
                    OpenPrompter();
                    return Did(Ink.IcoOpen, Lang.T("Teleprompter dibuka"));
                case "tab":
                    int pg;
                    if (!int.TryParse(arg, out pg) || pg < 0 || pg >= PageNames.Length) return null;
                    if (hiddenByUser) ToggleHidden();
                    OpenFromDock(pg);
                    return Did(Ink.IcoOpen, Lang.F("Tab {0} dibuka", Lang.T(PageNames[pg])));
                case "panel":
                    if (!yes) { expanded = false; Kick(); return Did(Ink.IcoHide, Lang.T("Panel ditutup")); }
                    if (hiddenByUser) ToggleHidden();
                    OpenFromDock(PageHome);
                    return Did(Ink.IcoOpen, Lang.T("Panel dibuka"));
                case "island":
                    if (yes == hiddenByUser) ToggleHidden();
                    return Did(yes ? Ink.IcoOpen : Ink.IcoHide, yes ? Lang.T("Island tampil lagi") : Lang.T("Island disembunyikan"));
            }
            return null;
        }

        static string SectionName(string id)
        {
            switch (id)
            {
                case "dock": return Lang.T("Dock dan taskbar");
                case "music": return Lang.T("Musik");
                case "look": return Lang.T("Tema dan warna");
                case "packs": return Lang.T("Paket tema");
                case "ai": return "AI";
                case "siap": return Lang.T("Siapkan suara dan AI");
                case "dikte": return Lang.T("Dikte");
                case "keys": return Lang.T("Pintasan");
                case "messages": return Lang.T("Pesan");
                case "phone": return Lang.T("HP");
                case "integrasi": return Lang.T("Integrasi");
                case "remind": return Lang.T("Pengingat");
                case "agents": return Lang.T("Agen");
                case "about": return Lang.T("Tentang");
                case "access": return Lang.T("Aksesibilitas");
                case "files": return Lang.T("Rak dan berkas");
                case "island": return Lang.T("Letak dan bentuk");
                case "behave": return Lang.T("Perilaku");
                case "idle": return Lang.T("Saat diam");
                case "home": return Lang.T("Ringkasan");
                case "tabs": return Lang.T("Tab panel");
                case "system": return Lang.T("Sistem");
            }
            return Lang.T("Dasar");
        }

        // AI path: the same actions as one tool
        static readonly string[] TepiActions = { "open_settings", "open_chat", "lyric_window_open", "lyric_window_close", "top_bar_on", "top_bar_off", "top_bar_autohide_on", "top_bar_autohide_off", "dock_on", "dock_off", "mini_on", "mini_off", "open_tab", "close_panel", "hide_island", "show_island", "open_teleprompter" };
        static readonly string[] TepiSections = { "general", "siap", "ai", "phone", "integrasi", "look", "packs", "island", "behave", "idle", "home", "tabs", "dock", "music", "messages", "system", "files", "agents", "dikte", "remind", "access", "keys", "about" };
        static readonly string[] TepiTabs = { "home", "music", "agents", "class", "messages", "shelf", "control", "remind", "phone", "convert", "voice" };

        ChatAct DoTepiTool(string action, string section, string tab)
        {
            string intent;
            switch (action)
            {
                case "open_settings": intent = "prefs:" + (Array.IndexOf(TepiSections, section) >= 0 ? section : ""); break;
                case "open_chat": intent = "chat"; break;
                case "lyric_window_open": intent = "lyric:on"; break;
                case "lyric_window_close": intent = "lyric:off"; break;
                case "top_bar_on": intent = "bar:on"; break;
                case "top_bar_off": intent = "bar:off"; break;
                case "top_bar_autohide_on": intent = "barhide:on"; break;
                case "top_bar_autohide_off": intent = "barhide:off"; break;
                case "dock_on": intent = "dock:on"; break;
                case "dock_off": intent = "dock:off"; break;
                case "mini_on": intent = "mini:on"; break;
                case "mini_off": intent = "mini:off"; break;
                case "open_tab": intent = Array.IndexOf(TepiTabs, tab) >= 0 ? "tab:" + Array.IndexOf(TepiTabs, tab) : null; break;
                case "close_panel": intent = "panel:off"; break;
                case "hide_island": intent = "island:off"; break;
                case "show_island": intent = "island:on"; break;
                case "open_teleprompter": intent = "prompter"; break;
                default: intent = null; break;
            }
            var msg = intent == null ? null : RunTepi(intent);
            if (msg == null) return null;
            return msg.Acts.Count > 0 ? msg.Acts[0] : new ChatAct { Glyph = IcoFail, Text = msg.Text };
        }
    }
}
