# Pengaturan

[← Kembali ke README](../README.md)

Klik ikon gear di pojok kanan atas panel, atau klik kanan ikon Tepi lalu **Pengaturan**. Jendelanya punya kolom cari dan bagian di samping; setiap perubahan langsung berlaku tanpa perlu menyimpan.

- **Umum**: bahasa, format jam (24 jam, atau 12 jam dengan am/pm; jam besar di island tanpa am/pm), mulai bersama Windows, cek versi baru, folder data, ekspor dan impor pengaturan (token agent, port, pasangan HP, dan sesi Kulia tidak pernah ikut; impor hanya menerima kunci yang dikenal Tepi), serta setel ulang pengaturan dengan integrasi, agen, dan HP tetap tersambung.
- **Tampilan**: tema (gelap, terang, atau otomatis mengikuti mode aplikasi Windows), material (Liquid, Frost, Hitam), aksen (termasuk dari sampul album: warna paling mencolok di sampul lagu yang diputar, diterangkan atau digelapkan sampai terbaca di island), warna sendiri (pilih latar, teks, dan aksen dari preset, ambil dari layar, atau warna bebas; warna lain diturunkan dari ketiganya seperti di pembuat paket, kontras teks ditampilkan, dan hasilnya bisa disimpan jadi paket tema), paket tema, suara sapaan paket.
- **Island**: mode (Normal, Mini, Otomatis), notifikasi saat Mini, mata, mata mengikuti kursor, buka panel dengan arahkan kursor atau klik beserta jedanya, aplikasi yang membuat island menyingkir atau menahan notifikasi saat jendelanya di depan, menempel di tepi atas, sisakan ruang di tepi atas, garis cahaya, posisi, lebar panel, layar. Panel terbuka sebentar selama diatur supaya hasilnya terlihat.
- **Tab panel**: gaya tab (otomatis, ikon, titik), tab yang terbuka pertama, serta tab yang tampil beserta urutannya (seret pegangan di kiri ikon, atau fokuskan lalu tekan panah atas dan bawah).
- **Musik**: lirik di island, lirik online, visualizer.
- **Pesan**: notifikasi pesan dan isinya, aplikasi chat tambahan, pengirim prioritas (nama atau kata yang tetap muncul saat Senyap dan rapat).
- **Sistem**: HUD Caps Lock, Num Lock dan Scroll Lock, bahasa input; pengingat cabut charger di 80, 85, atau 90%, flashdisk; kelas yang berlangsung, progres unduhan, mode rapat.
- **Rak dan berkas**: rak berkas, simpan isi rak, kosongkan rak otomatis; ubah format, riwayat clipboard, preview screenshot.
- **Agen**: hubungkan atau lepas tiap agent, kuota Claude, suara saat agen minta izin atau selesai dan saat `--run` selesai (suara bawaan Windows, diam saat Senyap atau layar penuh), port server hook (termasuk **Ganti port acak**), dan berapa lama izin ditahan.
- **Dikte**: nyalakan dikte, tombol yang ditahan (Ctrl kanan atau Alt kanan), bahasa ucapan, mesin (Groq atau offline) beserta key Groq dan unduhan model offline, kamus dan snippet, buang kata pengisi, perintah baris baru, gaya per aplikasi, kecilkan musik, rekam rapat, ringkasan dan to-do, folder transkrip, serta hitungan kata.
- **HP**: hubungkan HP lewat kode pasangan, ganti HP, serta pilih apakah notifikasi pesan dan kode OTP dari HP ditampilkan.
- **Integrasi**: Kulia, Notion, Todoist, dan kalender iCal untuk tab Tugas dan Jadwal.
- **Pengingat**: sholat (kota dan menit sebelum), cuaca dan hujan, minum (selang dan target), istirahat mata, peregangan, lama istirahat fokus, tahan notifikasi saat fokus.
- **Aksesibilitas**: tampil di rekaman layar, umumkan ke pembaca layar, kurangi gerak, gerak (Tenang tanpa pantulan, Bawaan, atau Lincah), Senyap, dan Senyap ikut Jangan ganggu Windows.
- **Pintasan**: ganti atau kosongkan setiap pintasan, termasuk pintasan teleprompter. Klik pintasannya lalu tekan kombinasi baru (Backspace mengosongkan, Esc batal). Kombinasi yang sudah dipakai aplikasi lain ditolak saat itu juga, dan saat Tepi dibuka island memberi tahu kalau ada pintasan yang direbut aplikasi lain. **Kembalikan** memasang lagi semua pintasan bawaan. Di keyboard yang punya AltGr (misalnya Jerman atau Polandia), AltGr tetap mengetik karakternya walau kombinasinya sama dengan pintasan Ctrl+Alt Tepi.
- **Tentang**: versi, cek pembaruan, **Apa yang baru**, ulangi **Tur Tepi**, **Kirim saran** (ide fitur atau laporan masalah, langsung dari Tepi tanpa login), dan info pembuat.

Island yang ikut terekam tidak bisa memakai efek kaca, karena kaca membaca layar di belakangnya dan akan ikut membaca dirinya sendiri. Selama opsi itu menyala, latarnya polos.

## Pengaturan lanjutan

Semua kunci di `%APPDATA%\Tepi\settings.ini` (ubah saat Tepi tidak berjalan):

| Kunci | Bawaan | Fungsi |
|---|---|---|
| `material` | `liquid` | `liquid`, `frost`, atau `solid` (hitam pekat). Kunci lama `glass=0` dibaca sebagai `solid` |
| `theme` | `gelap` | `gelap`, `terang`, atau `otomatis` (mengikuti mode aplikasi Windows) |
| `lyrics` | `1` | Lirik tersinkron di island |
| `lyrics.online` | `1` | Cari lirik di LRCLIB dan NetEase |
| `visualizer` | `1` | Visualizer musik |
| `music.view` | `auto` | `auto`, `lyrics`, atau `album` |
| `notif` | `1` | Notifikasi pesan |
| `notif.content` | `1` | Tampilkan isi pesan |
| `notif.apps` | kosong | Aplikasi chat tambahan, dipisah koma, dicocokkan dengan nama aplikasi |
| `notif.vip` | kosong | Nama pengirim atau kata, dipisah koma; pesan yang memuatnya tetap muncul saat Senyap |
| `agent.port` | acak | Port server hook; instalasi lama memakai `47821` |
| `agent.wait` | `110` | Detik menahan permintaan izin |
| `sound.agent` | `0` | Suara saat agen minta izin atau selesai, dan saat `--run` selesai |
| `update.check` | `1` | Cek versi baru sekali sehari |
| `accent` | kosong | Warna aksen, misalnya `#4C8DFF`; kosong untuk monokrom; `album` mengambil warna dari sampul lagu yang diputar |
| `monitor` | kosong | Monitor tempat island tampil; kosong untuk monitor utama |
| `focus.break` | `5` | Menit istirahat setelah fokus |
| `dock` | `1` | Menempel di tepi atas; `0` untuk mengambang |
| `position` | `0` | Geser island, `-40` (kiri) sampai `40` (kanan), persen dari ruang kosong |
| `panel.width` | `488` | Lebar panel, `440` sampai `560` |
| `panel.tabs` | semua | Tab yang tampil dan urutannya: `home`, `music`, `agents`, `class`, `messages`, `shelf`, `control`, `remind`, `convert`, `phone`. Kunci lama `tabs` dipindahkan sekali |
| `panel.first` | kosong | Tab yang terbuka pertama, misalnya `music`; kosong untuk tab pertama di urutan |
| `panel.open`, `panel.delay` | `hover`, `330` | Panel terbuka saat kursor diam di island (`hover`) atau saat diklik (`click`); jeda hover dalam ms, `150` sampai `800` |
| `island.except` | kosong | Aturan per aplikasi, dipisah koma: `exe:hide:Nama` menyembunyikan island, `exe:quiet:Nama` menahan notifikasi biasa selama jendela aplikasi itu di depan |
| `tabs.style` | `auto` | `auto` (titik bila lebih dari tujuh tab), `icons`, atau `dots` |
| `mini` | `auto` | `normal`, `mini`, atau `auto` (Mini saat tepi atas tertutup atau setelah diam semenit) |
| `mini.notif` | `1` | Notifikasi pesan tetap muncul saat Mini |
| `island.eyes`, `island.follow` | `1`, `1` | Mata di sliver Mini, dan mata mengikuti kursor |
| `agent.quota` | `1` | Pasang status line Tepi untuk kuota Claude saat Claude Code dihubungkan |
| `prompter.wpm` | `140` | Kecepatan teleprompter, kata per menit |
| `prompter.size` | `19` | Ukuran huruf teleprompter, px |
| `prompter.mirror` | `0` | Teleprompter dicerminkan |
| `prompter.width`, `prompter.height`, `prompter.bg` | `560`, `150`, `55` | Ukuran teleprompter dalam px (diubah dengan menyeret tepinya) dan kepekatan latar dalam persen |
| `lang` | `id` | `id` atau `en` |
| `clock.24` | `1` | `1` jam 24 (14.30), `0` jam 12 (2.30 pm) |
| `capture.show` | `0` | Island ikut terekam saat screenshot dan rekam layar (tanpa efek kaca) |
| `a11y.announce` | `1` | Bacakan toast ke pembaca layar yang sedang aktif |
| `clipboard` | `0` | Riwayat clipboard |
| `downloads` | `1` | Progres unduhan |
| `class.island` | `1` | Kelas yang sedang berjalan tampil di island |
| `shelf` | `1` | Rak berkas |
| `shelf.keep`, `shelf.expire` | `0`, `0` | Simpan jalur rak di `shelf.txt`; kosongkan otomatis setelah `24` atau `72` jam |
| `convert` | `1` | Kotak **Ubah format** saat berkas diseret ke island |
| `shots` | `1` | Preview screenshot |
| `drives` | `1` | Flashdisk dicolok dan dikeluarkan |
| `meeting` | `1` | Mode rapat |
| `keys.caps`, `keys.num`, `keys.lang` | `1`, `0`, `1` | HUD Caps Lock, HUD Num Lock dan Scroll Lock, HUD bahasa input |
| `battery.unplug` | `0` | Ingatkan cabut charger di `80`, `85`, atau `90` persen; `0` mati |
| `note.to` | `rak` | Tujuan catatan kilat: `rak`, `todoist`, atau `notion` |
| `dnd` | `0` | Senyap: hanya izin, tenggat, sholat, dan timer yang muncul |
| `dnd.follow` | `0` | Senyap ikut menyala selama Jangan ganggu Windows aktif |
| `motion.reduce` | `auto` | `auto` mengikuti efek animasi Windows, `1` selalu tenang, `0` selalu penuh |
| `motion.feel` | `bawaan` | `tenang` (lebih lambat, tanpa pantulan), `bawaan`, atau `lincah` (lebih cepat, pantulan lebih besar). `motion.reduce` tetap menang |
| `pray`, `pray.city`, `pray.before` | `0`, kosong, `10` | Pengingat sholat, kota, menit sebelum |
| `drink`, `drink.every`, `drink.goal` | `0`, `60`, `8` | Pengingat minum, selang menit, target gelas |
| `eyes` | `0` | Istirahat mata 20-20-20 |
| `stretch`, `stretch.every` | `0`, `60` | Peregangan dan selangnya |
| `theme.pack` | kosong | Nama folder paket tema di `%APPDATA%\Tepi\themes` |
| `theme.voice` | `1` | Suara sapaan paket tema |
| `look.custom`, `look.bg`, `look.text`, `look.accent` | `0`, kosong | Warna sendiri dalam `#RRGGBB`, ditimpakan di atas paket tema |
| `weather` | `0` | Cuaca dan peringatan hujan, memakai kota `pray.city` |
| `phone`, `phone.notifs`, `phone.otp` | `0`, `1`, `1` | Sambungan HP, notifikasi pesan dari HP, kode OTP dari HP |
| `phone.port` | acak | Port sambungan HP, dipilih saat pertama dinyalakan |
| `key.hide`, `key.nav`, `key.mini`, `key.mic`, `key.note`, `key.mirror`, `key.picker` | `Ctrl+Alt+H`, `I`, `K`, `M`, `N`, `C`, kosong | Pintasan global, misalnya `Ctrl+Shift+F10` atau `Win+Alt+H`; `none` mematikannya |
| `key.prompter.pause`, `.restart`, `.back`, `.ahead`, `.faster`, `.slower`, `.setup`, `.stop` | `Ctrl+Alt+Space`, `R`, `Left`, `Right`, `=`, `-`, `P`, `X` | Pintasan teleprompter, hanya terpakai selama naskah berjalan |
| `kulia.url`, `kulia.key` | server kulia.live | Arahkan ke instance Kulia lain |
| `dictate` | `0` | Dikte. Hook keyboard hanya terpasang selama ini menyala |
| `dictate.key` | `rctrl` | Tombol yang ditahan: `rctrl` (Ctrl kanan) atau `ralt` (Alt kanan) |
| `dictate.engine` | `groq` | `groq` atau `lokal` (whisper.cpp di laptop ini) |
| `dictate.lang` | `id` | `id`, `en`, atau `auto`. Bawaan mengikuti bahasa Tepi |
| `dictate.model` | `seimbang` | Model offline: `cepat` (base), `seimbang` (small), `akurat` (large-v3-turbo) |
| `dictate.fillers`, `dictate.commands`, `dictate.style`, `dictate.duck` | `1` | Buang kata pengisi, perintah baris baru, gaya per aplikasi, kecilkan musik |
| `rapat.summary` | `1` | Ringkasan dan to-do di transkrip rapat, lewat Groq |
| `edge.light` | `1` | Garis cahaya progres dan kilau pesan di tepi island |
| `reserve.top` | `0` | Sisakan ruang di tepi atas; jendela yang dimaksimalkan mulai di bawah island |
| `focus.hold` | `1` | Tahan notifikasi biasa selama sesi fokus, lalu rangkum |
| `key.suruh` | kosong | Pintasan untuk membuka kotak Suruh Tepi |
| `whatsnew.seen` | versi terpasang | Versi terakhir yang ringkasannya sudah tampil; kosongkan untuk melihatnya lagi saat Pengaturan dibuka |
| `tour.done` | `0` | Tur pengguna baru sudah selesai atau ditutup |
| `key.rapat`, `key.dictate.paste` | `Ctrl+Alt+T`, kosong | Mulai atau hentikan rekaman rapat; tempel dikte terakhir |

Variabel lingkungan untuk pengembangan:

- `TEPI_FPS=1` mencatat fps, biaya tiap bagian frame, dan frame yang lambat ke `tepi.log`.
- `TEPI_DUMP=<folder>` menyimpan frame yang dirender, karena efek kaca tidak terlihat di screenshot.
- `TEPI_SCALE=<angka>` memaksa skala tampilan.
- `TEPI_HOME=<folder>` memasang dan melepas hook di folder home lain, untuk menguji tanpa menyentuh konfigurasi agent yang asli.
- `TEPI_DIR=<folder>` menjalankan salinan uji dengan data sendiri, berdampingan dengan Tepi yang sedang dipakai. Salinan uji tidak menulis registry autostart, tidak mengecek pembaruan, dan memasang hook di `<folder>\home`.
- `build.cmd <keluaran.exe>` membangun ke lokasi lain, supaya exe yang sedang berjalan tidak tersentuh.
