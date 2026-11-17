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
                new[] { "Wi-Fi", "ms-settings:network-wifi", "wifi wi-fi jaringan network internet wlan nirkabel wireless sinyal signal" },
                new[] { "Bluetooth dan perangkat", "ms-settings:bluetooth", "bluetooth bt perangkat devices headset earbuds tws airpods mouse keyboard pairing sambung controller" },
                new[] { "Layar", "ms-settings:display", "layar display screen monitor resolusi resolution kecerahan brightness skala scale hdr refresh orientasi orientation proyeksi" },
                new[] { "Lampu malam", "ms-settings:nightlight", "lampu malam night light cahaya biru blue light mata" },
                new[] { "Suara", "ms-settings:sound", "suara sound audio bunyi speaker mikrofon microphone mic volume headphone headset output input" },
                new[] { "Notifikasi", "ms-settings:notifications", "notifikasi notif notifications jangan ganggu do not disturb fokus focus" },
                new[] { "Daya dan baterai", "ms-settings:powersleep", "baterai battery daya power tidur sleep hemat saver layar mati screen off cas charging hibernate" },
                new[] { "Penyimpanan", "ms-settings:storagesense", "penyimpanan storage disk ssd hdd ruang space bersihkan cleanup" },
                new[] { "Latar belakang", "ms-settings:personalization-background", "latar wallpaper background personalisasi personalization" },
                new[] { "Warna dan mode gelap", "ms-settings:colors", "warna colors gelap dark darkmode mode terang light aksen accent tema transparan transparency" },
                new[] { "Tema", "ms-settings:themes", "tema themes kursor cursor ikon desktop" },
                new[] { "Taskbar", "ms-settings:taskbar", "taskbar bilah tugas tray systray" },
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
                new[] { "Windows Update", "ms-settings:windowsupdate", "update pembaruan perbarui upgrade wu" },
                new[] { "Privasi dan keamanan", "ms-settings:privacy", "privasi privacy keamanan security izin permission" },
                new[] { "Kamera", "ms-settings:camera", "kamera camera webcam" },
                new[] { "Lokasi", "ms-settings:privacy-location", "lokasi location gps" },
                new[] { "Clipboard", "ms-settings:clipboard", "clipboard papan klip salin copy riwayat history" },
                new[] { "Multitasking", "ms-settings:multitasking", "multitasking snap jendela window alt tab desktop virtual" },
                new[] { "Aksesibilitas", "ms-settings:easeofaccess", "aksesibilitas accessibility a11y narator narrator perbesar magnifier teks text size" },
                new[] { "Font", "ms-settings:fonts", "font huruf" },
                new[] { "Mode game", "ms-settings:gaming-gamemode", "game gaming main" },
                new[] { "Pemulihan", "ms-settings:recovery", "pemulihan recovery reset ulang setel" },
                new[] { "Tentang perangkat", "ms-settings:about", "tentang about spesifikasi spek specs nama pc rename ram prosesor processor winver versi windows" },
                new[] { "Untuk pengembang", "ms-settings:developers", "pengembang developer dev" },
                // Tepi's own pages, by section id
                new[] { "Dasar", TepiPrefs + ":general", "umum general bahasa language mulai startup" },
                new[] { "Siapkan suara dan AI", TepiPrefs + ":siap", "siap setup suara ai model" },
                new[] { "HP", TepiPrefs + ":phone", "hp phone ponsel handphone android" },
                new[] { "Integrasi", TepiPrefs + ":integrasi", "integrasi integrations notion todoist google" },
                new[] { "Tema dan warna", TepiPrefs + ":look", "tema theme warna color aksen accent gelap dark terang light" },
                new[] { "Letak dan bentuk", TepiPrefs + ":island", "island letak posisi bentuk" },
                new[] { "Perilaku", TepiPrefs + ":behave", "perilaku behavior" },
                new[] { "Dock dan taskbar", TepiPrefs + ":dock", "dock taskbar start" },
                new[] { "Musik", TepiPrefs + ":music", "musik music lirik lyrics" },
                new[] { "Rak dan berkas", TepiPrefs + ":files", "rak shelf berkas files" },
                new[] { "Agen", TepiPrefs + ":agents", "agen agent claude codex" },
                new[] { "Dikte", TepiPrefs + ":dikte", "dikte dictation voice ketik suara" },
                new[] { "Pengingat", TepiPrefs + ":remind", "pengingat reminder alarm" },
                new[] { "Pintasan", TepiPrefs + ":keys", "pintasan shortcut hotkey tombol keys" },
            };

            static List<AppEntry> all;
            static bool allEnglish;

            // Prepared once per language
            public static List<AppEntry> All()
            {
                if (all != null && allEnglish == Lang.English) return all;
                var list = new List<AppEntry>();
                foreach (var page in Pages)
                {
                    bool own = page[1].StartsWith(TepiPrefs, StringComparison.Ordinal);
                    string name = Lang.T(page[0]);
                    list.Add(new AppEntry
                    {
                        Name = name,
                        Uri = page[1],
                        Sub = own ? "Tepi" : Lang.T("Pengaturan Windows"),
                        Target = own ? System.Windows.Forms.Application.ExecutablePath : ShellApp.AppsFolderOf(SettingsApp),
                        // Both languages find the page
                        Key = AppMatch.Prepare(name, page[2] + " " + page[0], false),
                    });
                }
                all = list;
                allEnglish = Lang.English;
                return all;
            }
        }
    }
}
