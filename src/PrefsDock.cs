using System;
using System.Collections.Generic;

namespace Tepi
{
    partial class Island
    {
        void SetDockChoice(string key, string value)
        {
            settings.Set(key, value);
            settings.Save();
            ApplyDock();
        }

        partial class PrefsWindow
        {
            // Dock page: on and off, look, behaviour, apps, widgets
            void DockRows(Action<string> cap, Action<KitRow> add)
            {
                bool on = settings.GetBool("dockbar.on", false), replacing = settings.GetBool("dockbar.replace", false);
                var dock = Row(Lang.T("Dock"), Lang.T("Aplikasi, widget, dan jam di tepi bawah layar"), Switch("dockbar.on", false, v => { island.SetFlag("dockbar.on", v); Relayout(); }));
                dock.Glyph = (char)0xE8A9;
                add(dock);
                var replace = Switch("dockbar.replace", false, v => { island.SetFlag("dockbar.replace", v); Relayout(); });
                replace.Enabled = on;
                var replaceRow = Row(Lang.T("Ganti taskbar Windows"), Lang.T("Start, tray, dan jam pindah ke dock; taskbar kembali saat dimatikan"), replace);
                replaceRow.Glyph = (char)0xE75B;
                add(replaceRow);

                cap(Lang.T("Tampilan"));
                string style = settings.Get("dockbar.style", "float");
                var styleSeg = Seg(new[] { Lang.T("Mengambang"), Lang.T("Selebar layar") }, style == "full" ? 1 : 0, i => island.SetDockChoice("dockbar.style", i == 1 ? "full" : "float"));
                styleSeg.Enabled = on;
                add(Row(Lang.T("Bentuk"), null, styleSeg));
                string size = settings.Get("dockbar.size", "m");
                var sizeSeg = Seg(new[] { Lang.T("Kecil"), Lang.T("Sedang"), Lang.T("Besar") }, size == "s" ? 0 : size == "l" ? 2 : 1, i => island.SetDockChoice("dockbar.size", i == 0 ? "s" : i == 2 ? "l" : "m"));
                sizeSeg.Enabled = on;
                add(Row(Lang.T("Ukuran ikon"), null, sizeSeg));
                int mag = settings.GetBool("dockbar.magnify", true) ? settings.GetInt("dockbar.mag", 150) : 100;
                var magSlider = Slider(100, 200, 10, mag, v => v <= 100 ? Lang.T("Mati") : v + "%", v =>
                {
                    settings.Set("dockbar.magnify", v > 100);
                    island.SetDockChoice("dockbar.mag", v.ToString());
                });
                magSlider.Enabled = on;
                var magRow = Row(Lang.T("Perbesar saat disorot"), Lang.T("Ikon membesar seperti gelombang mengikuti kursor"), magSlider);
                magRow.Glyph = (char)0xE8A3;
                add(magRow);

                cap(Lang.T("Perilaku"));
                string hide = settings.Get("dockbar.hide", "never");
                var hideSeg = Seg(new[] { Lang.T("Selalu tampil"), Lang.T("Otomatis"), Lang.T("Saat tertutup") }, hide == "auto" ? 1 : hide == "smart" ? 2 : 0, i =>
                {
                    island.SetDockChoice("dockbar.hide", i == 1 ? "auto" : i == 2 ? "smart" : "never");
                    Relayout();
                });
                hideSeg.Enabled = on;
                add(Row(Lang.T("Sembunyikan dock"), Lang.T("Muncul lagi saat kursor menyentuh tepi bawah"), hideSeg));
                var onDesk = Switch("dockbar.desktop", true, null);
                onDesk.Enabled = on && hide == "auto";
                add(Row(Lang.T("Tetap tampil di desktop kosong"), Lang.T("Saat sembunyi otomatis dan tidak ada jendela di depan"), onDesk));
                var capture = Switch("dockbar.capture", false, null);
                capture.Enabled = on;
                var captureRow = Row(Lang.T("Tampil di rekaman layar"), Lang.T("Ikut screenshot dan screen share, tanpa efek kaca"), capture);
                captureRow.Glyph = (char)0xE722;
                add(captureRow);
                var reserve = Switch("dockbar.reserve", true, null);
                reserve.Enabled = on && !replacing && hide == "never";
                add(Row(Lang.T("Sisakan ruang untuk dock"), replacing ? Lang.T("Selalu, selama menggantikan taskbar") : Lang.T("Jendela yang dimaksimalkan berhenti di atas dock"), reserve));
                var previews = Switch("dockbar.previews", true, null);
                previews.Enabled = on;
                var previewRow = Row(Lang.T("Pratinjau jendela"), Lang.T("Isi jendela langsung, saat kursor di atas aplikasi yang terbuka"), previews);
                previewRow.Glyph = (char)0xE7C4;
                add(previewRow);
                var badges = Switch("dockbar.badges", true, null);
                badges.Enabled = on;
                var badgeRow = Row(Lang.T("Angka notifikasi"), Lang.T("Jumlah notifikasi Windows di ikon aplikasinya"), badges);
                badgeRow.Glyph = (char)0xEA8F;
                add(badgeRow);
                if (replacing)
                {
                    var search = Switch("dockbar.search", true, null);
                    search.Enabled = on;
                    add(Row(Lang.T("Tombol cari"), null, search));
                    var view = Switch("dockbar.taskview", false, null);
                    view.Enabled = on;
                    add(Row(Lang.T("Tombol semua jendela"), Lang.T("Task View, sama dengan Win+Tab"), view));
                    var winkey = Switch("dockbar.winkey", true, null);
                    winkey.Enabled = on;
                    var winkeyRow = Row(Lang.T("Tombol Windows membuka Tepi Start"), Lang.T("Juga Win+S, Win+A, Win+N, Win+W, Win+X, dan Ctrl+Esc; pintasan Win lain tetap jalan"), winkey);
                    winkeyRow.Glyph = (char)0xE765;
                    add(winkeyRow);
                }

                cap(Lang.T("Widget"));
                string[] notes = { "Jam dan tanggal; klik membuka Ringkasan", "Sampul, judul, dan tombol putar", "Pemakaian tertinggi dan agen yang bekerja", "Saat timer berjalan", "Butuh kota di Pengaturan › Sistem", "Hanya di laptop", "CPU dan RAM, dibaca tiap 3 detik" };
                char[] glyphs = { (char)0xE823, (char)0xE8D6, (char)0xEC4A, (char)0xE916, (char)0xE753, (char)0xE83F, (char)0xE9D9 };
                for (int i = 0; i < DockWidgetIds.Length; i++)
                {
                    // Clock and battery sit in the tray corner while replacing
                    if (replacing && (DockWidgetIds[i] == "clock" || DockWidgetIds[i] == "battery")) continue;
                    var ws = Switch("dockbar.w." + DockWidgetIds[i], DockWidgetIds[i] != "system", null);
                    ws.Enabled = on;
                    var wr = Row(Lang.T(DockWidgetNames[i]), Lang.T(notes[i]), ws);
                    wr.Glyph = glyphs[i];
                    add(wr);
                }

                cap(Lang.T("Aplikasi tersemat"));
                var pins = island.DockPins();
                var clear = Button(Lang.T("Lepas semua"), false, delegate { island.SetDockPins(new List<string>()); Relayout(); });
                clear.Enabled = pins.Count > 0;
                var pinRow = Row(Lang.T("Aplikasi tersemat"), pins.Count == 0 ? Lang.T("Klik kanan aplikasi yang terbuka di dock, lalu Sematkan") : Lang.F("{0} aplikasi; seret ikon untuk mengurutkan, seret ke atas untuk melepas", pins.Count), clear);
                pinRow.Glyph = (char)0xE718;
                add(pinRow);
                var again = Button(Lang.T("Ambil"), false, delegate { island.ImportTaskbarPins(true); Relayout(); });
                var againRow = Row(Lang.T("Dari taskbar Windows"), Lang.T("Salin aplikasi yang tersemat di taskbar, dengan urutannya"), again);
                againRow.Glyph = (char)0xE896;
                add(againRow);
            }
        }
    }
}
