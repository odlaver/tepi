using System;
using System.Collections.Generic;

namespace Tepi
{
    partial class Island
    {
        // Settings pages Tepi Start can find, by the words people type
        static class SettingsPages
        {
            const string SettingsApp = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
            public const string TepiPrefs = "tepi:prefs";

            // Name, address, search words (Indonesian and English)
            static readonly string[][] Pages =
            {
                new[] { "Pengaturan Tepi", TepiPrefs, "tepi island dock pengaturan tepi preferensi" },
                new[] { "Wi-Fi", "ms-settings:network-wifi", "wifi wi-fi jaringan network internet wlan nirkabel wireless" },
                new[] { "Bluetooth dan perangkat", "ms-settings:bluetooth", "bluetooth perangkat devices headset earbuds tws mouse keyboard pairing sambung" },
                new[] { "Layar", "ms-settings:display", "layar display monitor resolusi resolution kecerahan brightness skala scale hdr refresh" },
                new[] { "Lampu malam", "ms-settings:nightlight", "lampu malam night light cahaya biru blue light mata" },
                new[] { "Suara", "ms-settings:sound", "suara sound audio speaker mikrofon microphone volume headphone output input" },
                new[] { "Notifikasi", "ms-settings:notifications", "notifikasi notifications jangan ganggu do not disturb fokus focus" },
                new[] { "Daya dan baterai", "ms-settings:powersleep", "baterai battery daya power tidur sleep hemat saver layar mati screen off" },
                new[] { "Penyimpanan", "ms-settings:storagesense", "penyimpanan storage disk ruang space bersihkan cleanup" },
                new[] { "Latar belakang", "ms-settings:personalization-background", "latar wallpaper background personalisasi personalization" },
                new[] { "Warna dan mode gelap", "ms-settings:colors", "warna colors gelap dark mode terang light aksen accent tema" },
                new[] { "Tema", "ms-settings:themes", "tema themes kursor cursor ikon desktop" },
                new[] { "Taskbar", "ms-settings:taskbar", "taskbar bilah tugas tray" },
                new[] { "Aplikasi terpasang", "ms-settings:appsfeatures", "aplikasi apps terpasang installed uninstall hapus copot program" },
                new[] { "Aplikasi bawaan", "ms-settings:defaultapps", "bawaan default browser pdf buka dengan open with" },
                new[] { "Aplikasi saat startup", "ms-settings:startupapps", "startup mulai otomatis login masuk" },
                new[] { "Info akun", "ms-settings:yourinfo", "akun account profil profile foto picture microsoft" },
                new[] { "Opsi masuk", "ms-settings:signinoptions", "masuk sign in kata sandi password pin windows hello sidik jari fingerprint wajah face" },
                new[] { "Tanggal dan waktu", "ms-settings:dateandtime", "tanggal date waktu time jam clock zona waktu timezone" },
                new[] { "Bahasa dan wilayah", "ms-settings:regionlanguage", "bahasa language wilayah region format" },
                new[] { "Mengetik", "ms-settings:typing", "mengetik typing keyboard koreksi otomatis autocorrect" },
                new[] { "Mouse", "ms-settings:mousetouchpad", "mouse kursor pointer gulir scroll" },
                new[] { "Touchpad", "ms-settings:devices-touchpad", "touchpad gestur gesture ketuk tap" },
                new[] { "Printer dan pemindai", "ms-settings:printers", "printer pemindai scanner cetak print" },
                new[] { "Mode pesawat", "ms-settings:network-airplanemode", "pesawat airplane flight" },
                new[] { "Hotspot seluler", "ms-settings:network-mobilehotspot", "hotspot tethering berbagi internet share" },
                new[] { "VPN", "ms-settings:network-vpn", "vpn" },
                new[] { "Proxy", "ms-settings:network-proxy", "proxy proksi" },
                new[] { "Ethernet", "ms-settings:network-ethernet", "ethernet lan kabel cable" },
                new[] { "Windows Update", "ms-settings:windowsupdate", "update pembaruan perbarui upgrade" },
                new[] { "Privasi dan keamanan", "ms-settings:privacy", "privasi privacy keamanan security izin permission" },
                new[] { "Kamera", "ms-settings:camera", "kamera camera webcam" },
                new[] { "Lokasi", "ms-settings:privacy-location", "lokasi location gps" },
                new[] { "Clipboard", "ms-settings:clipboard", "clipboard papan klip salin copy riwayat history" },
                new[] { "Multitasking", "ms-settings:multitasking", "multitasking snap jendela window alt tab desktop virtual" },
                new[] { "Aksesibilitas", "ms-settings:easeofaccess", "aksesibilitas accessibility narator narrator perbesar magnifier teks text size" },
                new[] { "Font", "ms-settings:fonts", "font huruf" },
                new[] { "Mode game", "ms-settings:gaming-gamemode", "game gaming main" },
                new[] { "Pemulihan", "ms-settings:recovery", "pemulihan recovery reset ulang setel" },
                new[] { "Tentang perangkat", "ms-settings:about", "tentang about spesifikasi specs nama pc rename ram prosesor processor" },
                new[] { "Untuk pengembang", "ms-settings:developers", "pengembang developer dev" },
            };

            public static List<AppEntry> Find(string query, int max)
            {
                string q = query.Trim().ToLowerInvariant();
                var ranked = new List<KeyValuePair<int, AppEntry>>();
                if (q.Length == 0) return new List<AppEntry>();
                foreach (var page in Pages)
                {
                    string name = Lang.T(page[0]), lower = name.ToLowerInvariant(), words = " " + page[2] + " " + page[0].ToLowerInvariant();
                    // Name first, then a word that starts with it, then anywhere
                    int rank = lower.StartsWith(q, StringComparison.Ordinal) ? 0 : words.Contains(" " + q) ? 1 : lower.Contains(q) || words.Contains(q) ? 2 : -1;
                    if (rank < 0) continue;
                    bool own = page[1] == TepiPrefs;
                    ranked.Add(new KeyValuePair<int, AppEntry>(rank, new AppEntry
                    {
                        Name = name,
                        Uri = page[1],
                        Sub = own ? "Tepi" : Lang.T("Pengaturan Windows"),
                        Target = own ? System.Windows.Forms.Application.ExecutablePath : ShellApp.AppsFolderOf(SettingsApp),
                    }));
                }
                ranked.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : string.Compare(a.Value.Name, b.Value.Name, StringComparison.CurrentCultureIgnoreCase));
                var list = new List<AppEntry>();
                foreach (var r in ranked) { if (list.Count >= max) break; list.Add(r.Value); }
                return list;
            }
        }
    }
}
