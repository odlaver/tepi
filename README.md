# Tepi

Dynamic island untuk Windows. Tepi duduk di tepi atas layar dan membesar saat ada yang terjadi: lagu yang sedang diputar beserta liriknya, agent coding (Claude Code, Codex, Gemini CLI, Antigravity) yang sedang bekerja atau minta izin, tugas kuliah yang mendekati tenggat, pesan yang masuk, timer, dan pengingat sholat atau minum.

Tampilannya mengikuti bahasa Liquid Glass: kaca jernih yang menyesuaikan terang latar di belakangnya, sudut kontinu seperti iOS, dan gerak pegas. Monokrom secara bawaan; paket tema memberi warna. Native C# tanpa browser engine, exe sekitar 826 KB. Tampilan tersedia dalam bahasa Indonesia dan Inggris.

Ringan. Island tidak menggambar apa pun selama tidak ada yang bergerak; bagian kecil yang hidup (visualizer, lirik, spinner, timer) digambar ulang sendiri, tepat pada refresh layar. Diukur di laptop Intel Core i3-1115G4 pada 1 November 2026, bergantian dengan versi 2.1 memakai lagu dan lirik tiruan yang sama, 45 detik per putaran, median 3 putaran:

| Kondisi | 2.1 | 2.4 |
|---|---|---|
| Musik diputar, lirik di island | 3,8% CPU | 2,6% CPU |
| Panel terbuka selagi musik diputar | 0,7% CPU | 0,6% CPU |
| Memori saat panel terbuka | sekitar 21 MB | sekitar 22 MB |

Persen CPU dihitung terhadap satu core; Task Manager membaginya lagi dengan jumlah thread, jadi angka di sana lebih kecil. Sejak 2.2, visualizer dan lirik digambar 30 kali per detik tepat pada refresh layar, tanpa frame kosong di antaranya, jadi musik sekitar sepertiga lebih ringan. Sambungan HP mati sampai dinyalakan; selama menyala, Tepi hanya menunggu pesan dari HP.

## Fitur

| | |
|---|---|
| Ringkasan | Kartu-kartu yang diurutkan menurut yang paling mendesak: lagu yang diputar beserta kontrolnya, agen yang bekerja, kelas berikutnya atau tenggat terdekat, pesan, timer, dan isi rak. Kalau tidak ada apa-apa, yang tampil jam besar. |
| Musik dan lirik | Semua pemutar yang muncul di kontrol media Windows: Spotify, YouTube dan YouTube Music di browser, Apple Music, Media Player, VLC, dan lainnya. Yang sedang berbunyi yang ditampilkan. Judul video YouTube seperti "No Doubt - Don't Speak (Official Music Video)" diurai dulu jadi artis dan lagu sebelum liriknya dicari; video tanpa artis, seperti klip lokal, tidak dicarikan lirik. Lirik tersinkron dari LRCLIB (cadangannya NetEase) menyapu kata demi kata di island. Panel musik punya dua tata letak: teleprompter lirik atau kartu album dengan piringan hitam yang berputar. Visualizer membaca suara yang benar-benar diputar. |
| Mini dan mata | Island bisa menyusut jadi sliver kecil di tepi atas yang tembus klik, jadi tidak menghalangi tab browser. Mode **Otomatis** (bawaan) menyusut saat jendela di depan menutupi tepi atas atau setelah island diam semenit. Sliver punya dua mata yang berkedip, mengikuti kursor, dan berekspresi. Pemberitahuan penting tetap muncul. |
| Agent coding | Claude Code, Codex, Gemini CLI, dan Antigravity. Status setiap sesi (sedang bekerja, menunggu izin, selesai beserta ringkasan balasannya). Rencana kerja agen (TodoWrite di Claude Code, `update_plan` di Codex, `write_todos` di Gemini) tampil sebagai bar bersegmen, hitungan seperti `3/7`, dan daftar langkah yang bisa dibuka. Permintaan izin dari Claude Code dan Codex bisa dijawab **Izinkan**, **Selalu**, atau **Tolak** langsung dari island; perubahan berkas tampil sebagai diff berwarna (baris tambah hijau, baris hapus merah) yang bisa digulir sampai habis sebelum diizinkan. Prompt singkat bisa dikirim ke terminal sesi dari tab Agen. Kuota Claude (jendela 5 jam dan 7 hari) tampil sebagai satu baris di tab Agen beserta jam resetnya, dengan peringatan di 80% dan 95%. |
| Kuliah | Tab berisi dua segmen. **Tugas**: tugas aktif dari [Kulia](https://kulia.live), Notion, dan Todoist dengan tenggatnya, pengingat 60 menit sebelum tenggat, dan tombol tandai selesai. **Jadwal**: kelas dan acara dari Kulia serta kalender iCal (Google, Outlook, iCloud, dan lainnya) tujuh hari ke depan; kartu kelas berikutnya, deretan hari, lalu satu baris per kelas. Kelas batal dicoret, ujian ditandai, kelas daring punya tombol **Buka kelas**. Selama kelas berjalan, island menampilkannya beserta sisa waktunya. |
| Pesan | WhatsApp, Telegram, Discord, Teams, Slack, email, dan aplikasi chat lain dari pusat notifikasi Windows. Di Ringkasan, pesan dikelompokkan per pengirim. |
| Preview screenshot | Setelah Win+Shift+S, PrtSc, atau menyalin gambar, thumbnail muncul di island beserta ukurannya. Seret thumbnail ke aplikasi lain, taruh di Rak, atau buka. Kalau Snipping Tool sudah menyimpannya di folder Screenshots, berkas itu yang dipakai; kalau tidak, Tepi menyimpan salinan sementara yang dihapus saat Tepi ditutup. |
| Rak | Tab berisi dua segmen. **Berkas**: seret berkas ke island (juga ke sliver Mini) lalu lepas di kotak **Taruh di rak**, atau klik kotak **Tambah** untuk memilih dari folder; panel langsung membuka Rak; berkas di rak bisa diseret lagi ke aplikasi lain (disalin, tidak dipindah), dibuka, atau ditampilkan di foldernya. Yang disimpan hanya jalurnya, di memori. **Clipboard**: 25 teks terakhir yang kamu salin, klik untuk menyalinnya lagi; hanya di memori dan mati sampai kamu menyalakannya. |
| Ubah format | Seret berkas ke island lalu lepas di kotak **Ubah format**, atau pilih berkas dari tab Ubah. Island berubah jadi kotak berisi format yang cocok dengan berkasnya: gambar ke JPG, PNG, WEBP, BMP, TIFF, GIF, ICO, atau PDF (beberapa gambar bisa digabung jadi satu PDF); halaman PDF ke PNG atau JPG; video ke MP4, WEBM, GIF, atau MP3; audio ke MP3, M4A, WAV, FLAC, atau OGG; dokumen Office ke PDF, DOCX, ODT, TXT, XLSX, atau CSV; Markdown dan HTML ke DOCX, HTML, atau PDF; berkas apa pun ke ZIP, dan ZIP bisa diekstrak. Hasilnya disimpan di samping berkas asli, masuk ke Rak, dan tercatat di tab Ubah. Gambar, PDF, dan ZIP memakai Windows. Audio, video, dan WEBP butuh [ffmpeg](https://ffmpeg.org), dokumen Office butuh [LibreOffice](https://www.libreoffice.org), Markdown butuh [Pandoc](https://pandoc.org). Tepi memakai yang sudah terpasang dan tidak mengunduh apa pun; format yang alatnya belum ada tampil redup. |
| HP | Sambungkan Tepi di Android lewat Wi-Fi yang sama. Tab HP menampilkan nama HP, status, dan baterainya. Kode OTP dari SMS, email, aplikasi bank, dan aplikasi lain muncul di island dalam bentuk tersamar; tombol **Salin** menyalin kode utuh, lalu kode itu dihapus dari clipboard semenit kemudian. Notifikasi pesan dari HP masuk ke Pesan. Berkas dari menu bagikan Android (**Kirim ke laptop**) masuk ke `Downloads\Tepi` dan Rak; berkas di laptop dikirim ke HP lewat **Kirim berkas** di tab HP atau klik kanan berkas di Rak. Permintaan izin agen juga bisa dijawab dari HP. |
| Kontrol | Slider volume dan kecerahan layar, pilihan perangkat output beserta volume tiap aplikasi yang sedang bersuara (Spotify, browser, Zoom, dan lainnya), tombol Wi-Fi, Bluetooth, Mikrofon, Senyap, Mini, sembunyikan island, Gelap (mode gelap Windows untuk aplikasi dan taskbar), dan Terjaga (laptop dan layar tidak tidur; klik kanan untuk 30 menit, 1 jam, atau 2 jam, dan selama menyala ada cangkir kecil di island), serta timer. Saat headset atau speaker jadi output, island menyebut namanya beserta baterainya kalau perangkat Bluetooth itu melaporkannya ke Windows, dan memberi tahu saat baterainya tinggal 15%; tombol kecerahan di keyboard memunculkan HUD seperti volume. |
| Cuaca | Suhu dan kondisi cuaca kota pilihanmu di bawah jam Ringkasan, plus peringatan "Hujan sekitar 30 menit lagi" sebelum hujan turun. Data dari Open-Meteo, mati sampai kamu menyalakannya. |
| Pengingat | Jadwal sholat lima waktu dihitung di laptop ini dari kota yang dipilih (metode Kemenag, tanpa internet), dengan pengingat beberapa menit sebelumnya dan saat waktunya. Pengingat minum dengan target gelas harian, istirahat mata 20-20-20, dan peregangan; semuanya menunggu selama kamu tidak di depan laptop, layar penuh, atau Senyap. |
| Flashdisk | Saat flashdisk dicolok, island menampilkan nama, huruf drive, dan sisa ruangnya, dengan tombol **Buka** dan **Keluarkan**. Setelah dikeluarkan, island bilang **Aman dicabut**; kalau masih dipakai aplikasi lain, island juga memberi tahu. |
| Unduhan | Unduhan di Chrome, Edge, Firefox, Brave, dan Opera tampil di island beserta ukuran dan kecepatannya. Klik toast **Unduhan selesai** untuk membuka foldernya. |
| Teleprompter | Tempel naskah, dan island menggulirkannya pelan di tepi atas layar, dekat kamera. Kecepatan dihitung dalam kata per menit. Selama berjalan, teleprompter tembus klik dan dikendalikan lewat pintasan keyboard. Saat dijeda, tepinya bisa diseret untuk mengubah ukuran. Ada hitung mundur, mode cermin untuk teleprompter kaca, penanda `[jeda]`, sisa waktu baca, beberapa naskah tersimpan, dan impor dari `.txt` atau `.md`. Naskah tidak ikut terekam saat screen share atau rekam layar. |
| Timer | Fokus 25 atau 50 menit lalu istirahat, timer menit bebas yang bisa dijeda dan ditambah semenit, serta stopwatch dengan putaran. Angka besar di island, atau cincin di samping pil musik. |
| Skrip | Skrip apa pun bisa memunculkan toast atau bilah progres di island lewat `Tepi.exe --notify`, dan perintah panjang tampil di island beserta hasilnya lewat `Tepi.exe --run`. |
| Mode rapat | Saat sebuah aplikasi memakai mikrofon lebih dari 20 detik (Zoom, Meet di browser, Teams, Discord), island menampilkan nama aplikasinya dan lama panggilan. Klik untuk mematikan atau menyalakan mikrofon. Selama itu notifikasi biasa ditahan seperti saat Senyap. |
| Aktivitas sistem | Volume, charger dan baterai, Wi-Fi (termasuk **Wi-Fi butuh login** di jaringan kampus atau hotel: klik toast-nya untuk membuka halaman login, lalu **Internet kembali** setelah berhasil), aplikasi yang memakai mikrofon atau kamera, Caps Lock, serta bahasa input saat layout keyboard berganti (misalnya EN ke ID). Selama Caps Lock nyala, penandanya tetap tampil: tombol kecil di island (atau di gelembung samping saat musik diputar) dan panah kecil di sliver Mini. Num Lock ikut ditandai kalau HUD-nya dinyalakan. Status tombol dibaca langsung, tanpa hook keyboard. |
| Tampilan | Material Liquid Glass, Frost, atau Hitam pekat; tema gelap atau terang; warna aksen; menempel di tepi atas atau mengambang; posisi, lebar panel, serta tab mana yang tampil. Bila tab lebih dari tujuh, ikonnya berganti titik dan nama tab tampil sebagai judul. Otomatis menyingkir saat ada aplikasi layar penuh, dan bisa diatur menyingkir atau menahan notifikasi saat aplikasi tertentu di depan. |
| Aksesibilitas | Seluruh island bisa dipakai dengan keyboard (Ctrl+Alt+I). Toast, tab, tombol, dan permintaan izin dibacakan oleh pembaca layar (Narrator, NVDA); kontrol di jendela Pengaturan punya nama, nilai, dan status untuk pembaca layar. Mengikuti setelan Windows "efek animasi": tanpa pantulan dan efek terbang bila dimatikan. Island bisa dipilih ikut terekam atau tidak saat screenshot dan rekam layar. |

## Pasang

1. Unduh [**Tepi.exe**](https://github.com/odlaver/tepi/raw/main/Tepi.exe) (langsung terunduh sekali klik), atau dari halaman [Releases](../../releases).
2. Jalankan. Karena exe belum ditandatangani, Windows SmartScreen bisa memunculkan peringatan: pilih **More info** lalu **Run anyway**. Kalau ragu, build sendiri dari sumbernya (lihat [Build sendiri](#build-sendiri)).
3. Ikon Tepi muncul di system tray. Di Windows 11 ikonnya bisa tersembunyi di balik panah **^** di taskbar.

Syarat: Windows 10 versi 2004 atau lebih baru, atau Windows 11, 64-bit. .NET Framework 4.8 sudah bawaan Windows.

Tepi memberi tahu sendiri kalau ada versi baru, termasuk build baru dengan nomor versi yang sama: exe yang sedang jalan dicocokkan SHA-256-nya dengan berkas rilis di GitHub. Klik kanan ikon Tepi, lalu **Perbarui**, atau buka **Pengaturan › Tentang › Cek pembaruan**. Tepi mengunduh exe baru dari halaman rilis repo ini, mencocokkan SHA-256-nya, lalu membuka ulang dirinya. Kalau mau dikabari lewat email juga, klik **Watch › Custom › Releases** di repo ini.

## Cara pakai

- **Arahkan kursor** ke island untuk membuka panel, atau klik island kalau **Pengaturan › Island › Buka panel** diganti ke Klik. Tab di atas panel: Ringkasan, Musik, Agen, Kuliah, Pesan, Rak, Kontrol, Pengingat, dan Ubah, plus HP selama sambungan HP menyala.
- **Scroll** saat panel terbuka untuk pindah tab. Daftar yang lebih panjang dari panel tetap tergulir; scroll di atas deretan tab, atau geser ke samping di touchpad, selalu pindah tab. Saat panel tertutup, scroll di atas island mengubah volume.
- **Seret berkas** ke island, lalu lepas di kotak **Taruh di rak** atau **Ubah format**. Saat island sedang Mini, tahan berkas sebentar di dekat sliver sampai island muncul.
- **Mini**: klik panah **^** di pojok kiri atas panel, tekan **Ctrl+Alt+K**, atau pakai tile Mini di Kontrol. Untuk memunculkan island dari Mini, diamkan kursor sebentar di tepi atas tepat di atas sliver.
- **Klik kanan** island atau ikon tray untuk menu: Mini, Sembunyikan island, Navigasi keyboard, Timer, Catatan kilat, Cermin kamera, Teleprompter, Hubungkan agen, Kulia, Pengaturan, Buka folder data. Klik kanan berkas di Rak untuk Buka, Tampilkan di folder, Salin jalur, Kirim ke HP (kalau HP tersambung), atau Keluarkan dari rak.
- **Cermin kamera**: tekan **Ctrl+Alt+C** atau pilih dari menu tray untuk melihat dirimu di island sebelum kelas atau rapat. Klik island untuk menutupnya; kamera dilepas otomatis setelah 2 menit. Cermin ini tidak ikut terekam selama efek kaca aktif.
- **Catatan kilat**: tekan **Ctrl+Alt+N**, tulis satu baris, lalu Enter. Catatan masuk ke `catatan.md` di folder data dan tampil di Rak, atau jadi tugas baru di Todoist atau Notion kalau sudah tersambung.
- **Mikrofon**: tekan **Ctrl+Alt+M** atau pakai tile Mikrofon di Kontrol untuk mematikan atau menyalakan mikrofon di seluruh sistem. Kalau ada aplikasi yang mendengarkan selagi mikrofon mati, ikon mikrofon di island berubah merah dan dicoret.
- **Timer** ada di tab Kontrol dan di menu tray: Fokus 25 atau 50 menit lalu istirahat, timer 5, 10, atau 15 menit, dan stopwatch.
- **Hapus semua** di Pesan, Clipboard, atau Rak menyedot isinya ke tombol tempat sampah; menghapus satu item memecahnya jadi serpihan.
- **Sembunyikan island** sepenuhnya: tekan **Ctrl+Alt+H** atau pakai tile Sembunyikan di Kontrol. Untuk memunculkannya lagi, klik ikon Tepi di tray atau tekan Ctrl+Alt+H. Kartu izin agent tetap muncul walau island disembunyikan.
- Di tab **Musik**, tombol di kanan atas mengganti tata letak antara lirik dan kartu album. Klik baris lirik untuk loncat ke bagian lagu itu, atau seret bilah progres. Tombol acak dan ulang (semua, lalu satu lagu) ada di bawah bilah progres, kalau pemutarnya mendukung, misalnya Spotify. Klik sampul untuk membawa jendela pemutarnya ke depan; klik kanan sampul untuk memilih pemutar yang ditampilkan kalau ada lebih dari satu (pilihan berlaku sampai pemutar itu ditutup). Lirik terlalu cepat atau telat? Klik kanan lirik untuk menggesernya 0,5 detik; geseran disimpan per lagu di `%APPDATA%\Tepi\lyrics\offsets.txt`.
- Di **Kuliah › Jadwal**, klik hari di deretan tengah untuk melihat kelas tanggal itu. Kelas daring yang punya tautan bisa diklik untuk membukanya.
- Di **Rak › Clipboard**, klik teks untuk menyalinnya lagi.

### Pengaturan

Klik ikon gear di pojok kanan atas panel, atau klik kanan ikon Tepi lalu **Pengaturan**. Jendelanya punya kolom cari dan bagian di samping; setiap perubahan langsung berlaku tanpa perlu menyimpan.

- **Umum**: bahasa, format jam (24 jam, atau 12 jam dengan am/pm; jam besar di island tanpa am/pm), mulai bersama Windows, cek versi baru, folder data.
- **Tampilan**: tema (gelap, terang, atau otomatis mengikuti mode aplikasi Windows), material (Liquid, Frost, Hitam), aksen (termasuk dari sampul album: warna paling mencolok di sampul lagu yang diputar, diterangkan atau digelapkan sampai terbaca di island), warna sendiri (pilih latar, teks, dan aksen; warna lain diturunkan dari ketiganya seperti di pembuat paket, kontras teks ditampilkan, dan hasilnya bisa disimpan jadi paket tema), paket tema, suara sapaan paket.
- **Island**: mode (Normal, Mini, Otomatis), notifikasi saat Mini, mata, mata mengikuti kursor, buka panel dengan arahkan kursor atau klik beserta jedanya, aplikasi yang membuat island menyingkir atau menahan notifikasi saat jendelanya di depan, menempel di tepi atas, posisi, lebar panel, layar, gaya tab (otomatis, ikon, titik), tab yang terbuka pertama, serta tab yang tampil beserta urutannya (seret pegangan di kiri ikon, atau fokuskan lalu tekan panah atas dan bawah). Panel terbuka sebentar selama diatur supaya hasilnya terlihat.
- **Aktivitas**: lirik, lirik online, visualizer, notifikasi pesan dan isinya, kelas yang berlangsung, progres unduhan, riwayat clipboard, pengingat cabut charger di 80, 85, atau 90%, flashdisk, preview screenshot, rak berkas, ubah format, lama istirahat fokus, HUD Caps Lock, Num Lock dan Scroll Lock, bahasa input, serta mode rapat.
- **Agen**: hubungkan atau lepas tiap agent, kuota Claude, port server hook (termasuk **Ganti port acak**), dan berapa lama izin ditahan.
- **HP**: hubungkan HP lewat kode pasangan, ganti HP, serta pilih apakah notifikasi pesan dan kode OTP dari HP ditampilkan.
- **Integrasi**: Kulia, Notion, Todoist, dan kalender iCal untuk tab Tugas dan Jadwal.
- **Pengingat**: sholat (kota dan menit sebelum), cuaca dan hujan, minum (selang dan target), istirahat mata, peregangan.
- **Aksesibilitas**: tampil di rekaman layar, umumkan ke pembaca layar, kurangi gerak, gerak (Tenang tanpa pantulan, Bawaan, atau Lincah), dan Senyap.
- **Pintasan**: ganti atau kosongkan setiap pintasan, termasuk pintasan teleprompter. Klik pintasannya lalu tekan kombinasi baru (Backspace mengosongkan, Esc batal). Kombinasi yang sudah dipakai aplikasi lain ditolak saat itu juga, dan saat Tepi dibuka island memberi tahu kalau ada pintasan yang direbut aplikasi lain. **Kembalikan** memasang lagi semua pintasan bawaan.

Island yang ikut terekam tidak bisa memakai efek kaca, karena kaca membaca layar di belakangnya dan akan ikut membaca dirinya sendiri. Selama opsi itu menyala, latarnya polos.

### Paket tema

Warna island, lambang di samping jam, maskot di pojok panel, pegangan bar progres, dan sapaan saat Tepi dibuka bisa diganti dengan paket tema. Buat folder di `%APPDATA%\Tepi\themes\<nama>\`, lalu pilih di **Pengaturan > Tampilan > Paket tema**.

Untuk warna saja, tidak perlu paket: **Pengaturan > Tampilan > Warna sendiri**. Paket lengkap dengan gambar, sapaan, dan suara bisa dibuat di [pembuat paket](https://odlaver.github.io/tepi/pack/).

| Berkas | Isi |
|---|---|
| `theme.ini` | Warna dalam `#RRGGBB` atau `#AARRGGBB`. Kunci untuk island: `bg`, `text`, `sub`, `faint`, `fill`, `fillHover`, `line`, `accent`, `onAccent`, `progress`, serta `positive` dan `negative` untuk baris diff. Awali dengan `card.` untuk kartu terang seperti kartu izin, misalnya `card.bg`. |
| `emblem*.png` | Gambar persegi (`emblem.png`, `emblem01.png`, dan seterusnya), dipotong bulat dan ditampilkan di samping jam dan di sapaan. Kalau ada lebih dari satu, Tepi memilih acak saat dibuka dan setiap panel dibuka. Opsional. |
| `corner*.png` | Maskot berlatar transparan yang duduk di pojok kanan bawah panel, sebagian di luar panel. Kalau ada lebih dari satu, dipilih acak setiap panel dibuka. Opsional. |
| `knob.png` | Pegangan bar progres musik dan teleprompter. Ujung kanan gambar menunjuk posisi saat ini, jadi pakai gambar melebar berlatar transparan. Opsional. |
| `greetings.txt` | Satu sapaan per baris, dipilih acak saat Tepi dibuka. Baris di bawah `[pagi]`, `[siang]`, `[sore]`, atau `[malam]` hanya dipakai pada waktunya. Baris tanpa judul dipakai kapan saja. Opsional. |
| `voice\` | Suara `.wav` yang diputar sekali saat sapaan muncul. Taruh di `voice\pagi\`, `voice\siang\`, `voice\sore\`, atau `voice\malam\` untuk waktu tertentu, atau langsung di `voice\` untuk kapan saja. Bisa dimatikan di **Pengaturan > Tampilan > Suara sapaan paket tema**. Opsional. |

Lambang dan maskot bisa bergerak. Simpan frame-nya berjajar ke samping dalam satu PNG dan beri nama `nama@N.png`, dengan N jumlah frame, misalnya `emblem20@36.png`. Maskot bergerak saat panel dibuka. Lambang bergerak saat sapaan muncul dan sesekali saat island diam. Setiap animasi berhenti sendiri setelah beberapa putaran. Maskot berikutnya disiapkan di latar supaya panel tidak tersendat saat dibuka. Gambar paket dibatasi 8 MB per berkas dan 8 juta piksel; yang lebih besar dilewati.

Pilihan tambahan di `theme.ini`:

| Kunci | Isi |
|---|---|
| `anim.fps` | Kecepatan animasi dalam frame per detik. Bawaan 12. |
| `anim.seconds` | Lama animasi diputar setiap kali muncul, dibulatkan ke putaran utuh. Bawaan 3. |
| `anim.idle` | Jeda dalam detik sebelum lambang bergerak lagi saat island diam. Bawaan 0, artinya mati. |
| `effects` | `water`: gelembung naik saat panel dibuka dan saat lambang bergerak, serta riak di sekitar lambang saat sapaan. |
| `knob.height` | Tinggi `knob.png` di bar progres, dalam piksel. Bawaan 15. |
| `knob.anchor` | Baris gambar knob yang menempel di bar, dari 0 (atas) sampai 1 (bawah). Bawaan 0.5. Berguna untuk gambar yang bilahnya tidak di tengah. |
| `eyes` | `0` mematikan mata di island untuk paket ini. |
| `knob.x` | Kolom gambar knob yang berada tepat di posisi lagu, dari 0 (kiri) sampai 1 (kanan). Bawaan 1, ujung kanan. Selama knob dipakai, titik bulat di bar hanya muncul saat kursor di atas bar. |

Contoh `theme.ini`:

```
bg=#0E1734
text=#EEF4FF
sub=#B4C6EE
accent=#6EC3FF
onAccent=#0B1430
card.bg=#EEF4FF
card.text=#0E1734
```

Paket tema hanya dibaca dari komputermu sendiri. Kalau memakai gambar karakter atau karya orang lain, simpan untuk pemakaian pribadi. Suara sapaan tidak diputar saat layar penuh atau mode jangan ganggu Windows.

### Navigasi keyboard

Tekan **Ctrl+Alt+I** dari aplikasi mana pun. Panel terbuka dan island menerima keyboard:

| Tombol | Fungsi |
|---|---|
| Tab, Shift+Tab | Pindah ke tombol berikutnya atau sebelumnya. Tombol yang dipilih diberi garis tepi. |
| Kiri, Kanan | Pindah tab panel, atau pindah segmen saat tombol segmen dipilih |
| 1 sampai 9 | Langsung ke tab ke-n |
| Atas, Bawah | Gulir daftar, diff di kartu izin, atau naskah teleprompter |
| Enter, Spasi | Tekan tombol yang dipilih |
| Esc | Keluar, fokus kembali ke jendela sebelumnya |

Kartu izin agent juga bisa dijawab dengan cara ini. Jeda 0,8 detik sebelum **Izinkan** bisa ditekan tetap berlaku. Kalau pembaca layar aktif, nama tab dan tombol dibacakan.

### Teleprompter

1. Klik kanan ikon Tepi, lalu **Teleprompter**.
2. Pilih naskah tersimpan, tempel naskah baru, atau klik **Impor** untuk membuka berkas `.txt` atau `.md`. Format Markdown (judul, tebal, tautan, blok kode) dibuang, tinggal teks yang dibaca.
3. Atur kecepatan (60 sampai 400 kata per menit), ukuran huruf, dan seberapa pekat latarnya (15% tembus pandang sampai 100% pekat). Nyalakan **Cermin** kalau memakai teleprompter kaca, lalu **Mulai** (atau Ctrl+Enter).

**Simpan** menyimpan naskah dengan nama baris pertamanya, atau menimpa naskah yang sedang dipilih. **Hapus** menghapus naskah yang dipilih. Naskah tersimpan ada di `%APPDATA%\Tepi\naskah`.

Naskah mulai setelah hitung mundur 3 detik. Selama naskah berjalan:

- Island tembus klik, jadi apa pun di bawahnya tetap bisa diklik. Kendalikan dari aplikasi mana pun:

  | Pintasan | Fungsi |
  |---|---|
  | Ctrl+Alt+Spasi | Jeda dan lanjut |
  | Ctrl+Alt+R | Ulang dari awal |
  | Ctrl+Alt+Kiri, Kanan | Mundur atau maju 5 detik |
  | Ctrl+Alt+=, Ctrl+Alt+- | Lebih cepat atau lebih lambat |
  | Ctrl+Alt+P | Buka jendela teleprompter, naskah tetap di tempatnya |
  | Ctrl+Alt+X | Hentikan |

  Pintasan ini hanya terpakai selama teleprompter terbuka, bisa diganti di **Pengaturan › Pintasan**, dan semuanya juga ada di submenu **Teleprompter** di menu tray.
- Saat dijeda, island bisa diklik: seret tepi kiri atau kanan untuk mengubah lebar, tepi bawah untuk tinggi, atau sudut bawah untuk keduanya. Posisi baca tidak hilang.
- Tulis `[jeda]` di naskah untuk berhenti otomatis di titik itu. Baris itu ditandai titik di kiri.
- Sisa waktu baca tampil di kanan atas.
- Panel dan toast tidak muncul supaya naskah tidak tertutup. Kartu izin agent tetap muncul, dan pengingat kelas ditunda sampai teleprompter ditutup.
- Naskah terakhir disimpan di `%APPDATA%\Tepi\prompter.txt` dan terisi lagi saat Teleprompter dibuka.

### Chat ke agent

Di tab **Agen**, arahkan kursor ke sebuah sesi lalu klik **Chat**. Jendelanya menampilkan status sesi, rencana kerja, dan kuota Claude. Tulis prompt, tekan Enter (Shift+Enter untuk baris baru). Prompt yang pernah dikirim ada di **Riwayat** per proyek, dan prompt yang sering dipakai bisa disimpan sebagai **Templat** di `%APPDATA%\Tepi\templat-chat.txt`. Tepi membawa terminal sesi itu ke depan, lalu mengetik prompt dan menekan Enter. Kalau terminalnya tidak bisa dibawa ke depan, tidak ada yang diketik.

Kalau beberapa sesi berjalan di tab berbeda dalam satu jendela Windows Terminal, prompt masuk ke tab yang sedang aktif. Kotak chat mengingatkan hal ini. Tekan panah atas dan bawah di kotak chat untuk memanggil prompt yang pernah dikirim (hanya di memori, hilang saat Tepi ditutup).

### Menjawab izin

- **Izinkan** dan **Tolak** menjawab permintaan itu saja.
- **Selalu** mengizinkan permintaan itu, lalu mengizinkan otomatis permintaan yang persis sama di sesi yang sama. "Persis sama" dicocokkan lewat sidik SHA-256 atas seluruh isi permintaan, termasuk opsi seperti jalan di latar atau tanpa sandbox. Ingatan ini hilang saat sesi selesai.
- **Terminal** mengembalikan keputusan ke prompt di terminal dan membawa jendela terminalnya ke depan.
- Diff atau perintah yang lebih dari 10 baris bisa digulir di dalam kartu. **Izinkan** dan **Selalu** baru terbuka setelah digulir sampai akhir. Yang lebih dari 300 baris, atau teks perintah yang terlalu panjang untuk island, tetap dijawab di terminal.
- Opsi perintah yang mengubah cara jalannya (misalnya jalan di latar, batas waktu, tanpa sandbox) tampil di samping nama alat.
- Karakter tak terlihat dan pembalik arah teks (bidi, zero-width) tampil sebagai kode seperti `<U+202E>`, supaya isi perintah tidak bisa disamarkan.

### Skrip

```
Tepi.exe --notify "Backup selesai" "12 berkas"
Tepi.exe --notify "Build" --progress 40 --id build
Tepi.exe --run "npm run build"
```

Yang pertama memunculkan toast. Yang kedua menampilkan bilah progres dengan nama `build`. Kirim angka baru dengan `--id` yang sama untuk memperbaruinya, dan `100` untuk menutupnya. Bilah yang tidak diperbarui selama 2 menit hilang sendiri.

`--run` menjalankan perintah di terminal itu sendiri lewat `cmd /c`, outputnya tetap di terminal. Selama berjalan, island menampilkan perintah dan lamanya; setelah selesai, island bilang berapa lama atau kode galatnya. Island hanya menerima kabar mulai dan selesai, tidak pernah menjalankan apa pun yang dikirim ke server hook. Di PowerShell dan cmd, Windows tidak menunggu program jendela seperti Tepi, jadi pakai `start /wait Tepi.exe --run "..."` di cmd atau `Start-Process -Wait -NoNewWindow Tepi.exe '--run','...'` di PowerShell kalau perintah berikutnya harus menunggu.

Perintah ini memakai server hook dan token yang sama dengan agent, jadi hanya program di komputer ini yang bisa memakainya.

### Menghubungkan agent

1. Klik kanan ikon Tepi, pilih **Hubungkan agen**, lalu pilih agentnya. Agent yang tidak ada di komputer ditandai *tidak ditemukan*.
2. Buka sesi baru di agent itu. Sesi yang sudah terbuka sebelumnya tidak ikut terpantau.

Klik nama yang sama sekali lagi untuk melepasnya. Sebelum mengubah berkas, Tepi menyimpan salinannya dengan akhiran `.bak-tepi`. Hook dan pengaturan lain di berkas itu tidak disentuh.

| Agent | Yang bisa dilakukan Tepi | Berkas yang diubah | Catatan |
|---|---|---|---|
| Claude Code | Pantau, izinkan, tolak | `settings.json` di setiap profil (`%USERPROFILE%\.claude` dan `%USERPROFILE%\.claude-*`) | |
| Codex | Pantau, izinkan, tolak | `%USERPROFILE%\.codex\hooks.json` (atau `%CODEX_HOME%`) | Codex menjalankan hook baru setelah dipercayai: buka `/hooks` di Codex sekali. |
| Gemini CLI | Pantau | `%USERPROFILE%\.gemini\settings.json` | Hook Gemini tidak bisa menyetujui izin, jadi izin tetap dijawab di terminal. Hook hanya jalan di folder yang dipercayai. |
| Antigravity | Pantau | `%USERPROFILE%\.gemini\config\hooks.json` | Berlaku untuk app, CLI, dan IDE. Izin tetap dijawab di Antigravity. |

Cara kerjanya:

- Claude Code mengirim event ke `http://127.0.0.1:<port>/hook`. Instalasi baru memakai port acak; instalasi lama tetap di 47821 sampai kamu menekan **Ganti port acak** di Pengaturan › Agen. Agent lain menjalankan `Tepi.exe --relay <agent>`, yang membaca port dari `settings.ini` dan meneruskan event ke alamat yang sama. Semua request memakai token acak yang dibuat saat Tepi pertama kali jalan. Request tanpa token yang benar, atau yang datang dari browser, ditolak.
- Tab **Agen** menampilkan durasi setiap sesi, jumlah berkas yang diubah, dan jumlah perintah yang dijalankan.
- Klik sesi di tab **Agen**, atau toast **Selesai**, untuk membuka jendela terminal sesi itu. Kalau beberapa sesi ada di tab berbeda dalam satu jendela Windows Terminal, yang dibuka jendelanya.
- Saat agent minta izin, island berbalik putih dan menahan permintaan itu sampai kamu memilih. Kalau tidak dijawab dalam 110 detik, atau kamu memilih **Jawab di terminal**, keputusan kembali ke prompt biasa di terminal.
- Kalau Tepi sedang tidak jalan, agent tetap bekerja seperti biasa tanpa pesan galat.
- Hook menyimpan lokasi `Tepi.exe`. Kalau exe dipindah, lepas lalu hubungkan lagi dari menu.
- Hook `Stop` dan `SessionEnd` Claude Code memakai `curl` lewat Git Bash, yang memang dibutuhkan Claude Code di Windows.

### Kuota Claude

Tepi membaca kuota Claude Code dari status line resminya, tanpa menyentuh token login. Saat Claude Code dihubungkan, Tepi memasang `"<Tepi.exe>" --statusline` sebagai status line di setiap profil. Kalau sudah ada status line lain, status line itu tetap jalan dan keluarannya tetap ditampilkan. Saat dilepas, nilai lamanya dikembalikan persis.

- Tab **Agen** menampilkan pemakaian jendela 5 jam dan 7 hari beserta jam resetnya. Ringkasan ikut menampilkannya saat pemakaian 70% ke atas.
- Peringatan muncul sekali per jendela di 80% dan 95%. Yang 95% tetap muncul walau Senyap atau Mini.
- Data baru ada untuk langganan Pro atau Max, setelah balasan pertama di sesi baru.
- Kalau Claude Code sudah dihubungkan sebelum versi 2.0, nyalakan **Pengaturan > Agen > Kuota Claude**.

### Integrasi

Buka **Pengaturan > Integrasi**:

- **Notion**: buat integrasi internal di [notion.so/my-integrations](https://www.notion.so/my-integrations), salin tokennya, lalu bagikan database tugasmu ke integrasi itu (di Notion: **••• > Koneksi**). Pilih database, kolom tanggal, dan kolom selesai. Tugas bisa ditandai selesai dari Tepi kalau integrasinya diberi izin mengubah isi.
- **Todoist**: tempel token API dari **Pengaturan > Integrasi > Developer** di Todoist. Tugas aktif yang punya tenggat ikut tampil dan bisa ditutup dari Tepi.
- **Kalender**: tempel tautan iCal (`https` atau `webcal`). Di Google Calendar, pakai **Secret address in iCal format** dari pengaturan kalender. Outlook, iCloud, dan aplikasi lain yang punya tautan iCal juga bisa. Paling banyak 12 kalender.

Data diperbarui tiap 10 menit, dan saat tab Tugas atau Jadwal dibuka. Token dan tautan kalender disimpan terenkripsi dengan DPAPI Windows dan tidak pernah ditulis ke log.

### Notifikasi pesan

- Saat pertama jalan, izinkan Tepi membaca notifikasi. Kalau terlewat: **Pengaturan Windows > Privasi & keamanan > Notifikasi**.
- Tepi hanya membaca notifikasi yang ditampilkan Windows. Chat yang kamu mute di WhatsApp tidak memunculkan notifikasi, jadi tidak ikut tampil.
- Supaya tidak muncul dobel dengan popup Windows: **Pengaturan > Sistem > Notifikasi**, pilih aplikasinya, lalu matikan **Tampilkan banner notifikasi**.
- Pesan tetap ada di Tepi sampai kamu membuka aplikasinya dari Tepi atau menghapusnya, termasuk dari aplikasi yang tidak menyimpan notifikasi di pusat notifikasi. Pesan seperti ini hilang kalau Tepi ditutup.
- Supaya pesan WhatsApp ikut hilang saat sudah dibaca di HP atau WhatsApp Web, nyalakan **Pengaturan > Sistem > Notifikasi > WhatsApp > Tampilkan notifikasi di pusat notifikasi**. Tanpa itu, Windows membuang notifikasinya sebelum WhatsApp sempat menariknya.
- Pesan yang dikirim ulang aplikasi dengan isi yang persis sama hanya muncul sekali.
- Kalau pesan WhatsApp tidak pernah muncul sama sekali, cek **Pengaturan > Sistem > Notifikasi > WhatsApp** dan pastikan notifikasinya menyala.
- Isi pesan tidak disimpan ke disk dan tidak dikirim ke mana pun.

### Sambungan HP

1. Di HP: pasang APK Tepi Android dari [tepi-android/releases](https://github.com/odlaver/tepi-android/releases).
2. Di laptop: **Pengaturan > HP**, nyalakan **Hubungkan HP**, lalu klik **Buat kode**.
3. Di HP: Tepi > **Integrasi** > **Laptop**, ketik alamat dan kode 12 karakter yang muncul di laptop.
4. Windows Firewall akan bertanya sekali; izinkan untuk jaringan privat.

Kode berlaku 5 menit dan hangus setelah lima kali salah. Tab HP muncul sendiri setelah sambungan dinyalakan.

### Kulia

1. Buka tab **Tugas** (atau klik kanan ikon Tepi, lalu **Kulia > Masuk ke Kulia**).
2. Isi email yang kamu pakai di [kulia.live](https://kulia.live).
3. Masukkan kode yang dikirim ke email itu.

Selesai. Tugas aktif dan jadwal kelasmu langsung tampil, lalu diperbarui tiap 10 menit. Kalau belum punya akun, daftar dulu di kulia.live.

Sesi login disimpan terenkripsi dengan DPAPI Windows dan hanya bisa dibuka oleh akun Windows-mu. Untuk keluar: klik kanan ikon Tepi, lalu **Kulia > Keluar dari Kulia**.

## Privasi

Tepi hanya menghubungi layanan luar berikut, dan masing-masing bisa dimatikan. Notion, Todoist, dan kalender hanya dihubungi kalau kamu menghubungkannya sendiri:

| Layanan | Data yang dikirim | Kapan | Cara mematikan |
|---|---|---|---|
| [LRCLIB](https://lrclib.net) | Judul, artis, dan durasi lagu | Saat lagu baru diputar | **Pengaturan > Cari lirik online** |
| [NetEase Cloud Music](https://music.163.com) | Judul dan artis lagu | Saat LRCLIB sibuk atau tidak punya lirik bersinkron | **Pengaturan > Cari lirik online** |
| [Kulia](https://kulia.live) | Email, kode masuk, dan permintaan daftar tugas serta jadwalmu | Hanya kalau kamu masuk ke Kulia | Menu tray > **Kulia > Keluar dari Kulia** |
| [Notion](https://www.notion.so) | Token integrasi dan permintaan isi database yang kamu bagikan | Hanya kalau kamu menghubungkan Notion | **Pengaturan > Integrasi > Lepas** |
| [Todoist](https://todoist.com) | Token API dan permintaan daftar tugas aktif | Hanya kalau kamu menghubungkan Todoist | **Pengaturan > Integrasi > Lepas** |
| Kalender iCal | Permintaan isi kalender ke alamat yang kamu tempel | Hanya kalau kamu menambah kalender | **Pengaturan > Integrasi**, hapus kalendernya |
| [Open-Meteo](https://open-meteo.com) | Koordinat kota yang kamu pilih di Pengingat | Tiap 30 menit, hanya kalau **Cuaca dan hujan** dinyalakan | **Pengaturan > Pengingat > Cuaca dan hujan** |
| GitHub | Permintaan rilis terbaru beserta SHA-256 `Tepi.exe`. Exe baru hanya diunduh saat kamu klik **Perbarui**. | Sekali sehari, dan saat **Cek pembaruan** diklik | `update.check=0` di `settings.ini` |

- Isi pesan, data agent, dan isi layar tidak pernah dikirim ke layanan luar. Server hook hanya bisa dijangkau dari komputer ini.
- Sambungan HP hanya berjalan di jaringan lokal, antara laptop dan HP yang kamu pasangkan, tanpa server perantara. Yang lewat: baterai HP, notifikasi pesan (isinya mengikuti setelan isi pesan di HP), kode OTP beserta nama aplikasinya, berkas yang kamu kirim, dan permintaan izin agen. Teks notifikasi lain tidak pernah dikirim.
- Lirik yang ditemukan disimpan di `%APPDATA%\Tepi\lyrics` supaya tidak diambil ulang.
- Efek kaca membaca layar di belakang island untuk membuat latar buramnya. Karena itu island tidak ikut terekam saat screenshot atau screen share. Nyalakan **Tampil di rekaman layar** di Pengaturan kalau island perlu ikut terekam. Selama teleprompter berjalan, island selalu disembunyikan dari rekaman.
- Naskah teleprompter hanya disimpan di komputer ini.
- Riwayat clipboard hanya ada di memori dan hilang saat Tepi ditutup atau fiturnya dimatikan. Teks yang ditandai rahasia oleh pengelola kata sandi tidak dicatat.
- Progres unduhan hanya membaca nama dan ukuran berkas sementara di folder Downloads.
- Rak hanya menyimpan jalur berkas, di memori, dan hilang saat Tepi ditutup.
- Ubah format berjalan di laptop ini. Berkasmu tidak dikirim ke mana pun; ffmpeg, LibreOffice, dan Pandoc dijalankan dari yang sudah terpasang. Riwayat di tab Ubah hanya ada di memori.
- Jadwal sholat dihitung di laptop ini. Pengingat minum, mata, dan peregangan hanya membaca kapan terakhir ada input keyboard atau mouse, untuk tahu kamu sedang di depan laptop.
- Kecerahan, perangkat audio, Wi-Fi, dan Bluetooth diatur lewat API Windows di laptop ini.
- Semua pengaturan dan cache ada di `%APPDATA%\Tepi`.

## Keamanan

- Server hook hanya mendengarkan di `127.0.0.1`, jadi tidak bisa dijangkau dari jaringan, dan memegang portnya secara eksklusif. Setiap request wajib membawa token acak 48 karakter, dan request yang datang dari browser ditolak. Token diperiksa sebelum isi request dibaca, koneksi serentak dibatasi, dan request yang tidak selesai dalam 10 detik diputus.
- Port acak per instalasi membuat pengguna Windows lain di komputer yang sama sulit menebak dan menyerobot port saat Tepi tidak berjalan. Model ancamannya: program lain yang berjalan sebagai akunmu sendiri tetap bisa mengubah config agent secara langsung, jadi Tepi tidak berusaha melindungi dari itu.
- Kartu izin menampilkan perintah atau isi perubahan berkas secara utuh. **Izinkan** terkunci sampai isi yang lebih dari 10 baris digulir habis, terkunci permanen bila teksnya terlalu panjang untuk island, dan juga selama 0,8 detik pertama setelah kartu muncul supaya klik yang kebetulan mendarat di sana tidak ikut mengizinkan.
- Kalau Tepi tidak jalan atau tidak menjawab, agent kembali ke prompt izinnya sendiri. Tepi tidak pernah mengizinkan apa pun tanpa klikmu.
- Tautan tugas Kulia hanya dibuka kalau alamatnya `http` atau `https`.
- Tidak ada pembaruan diam-diam. Exe hanya berubah saat kamu klik **Perbarui**. Tepi hanya menerima `Tepi.exe` dari alamat rilis repo ini, mengunduhnya lewat HTTPS, lalu mencocokkan SHA-256-nya dengan digest yang dicatat GitHub untuk berkas itu. Kalau tidak cocok, exe lama tidak disentuh dan halaman rilis dibuka. Exe lama disimpan sebagai `Tepi.old.exe` sampai Tepi baru jalan, lalu dihapus. Kalau folder exe tidak bisa ditulis (misalnya di Program Files), Tepi membuka halaman rilis. Begitu rilis Tepi ditandatangani (Authenticode), exe baru juga wajib bertanda tangan sah dari penerbit yang sama dengan exe yang sedang jalan.
- `Tepi.exe --notify` dan `/notify` dijaga token yang sama dengan hook agent. Teks dari skrip dibersihkan dari karakter kontrol, dipotong panjangnya, dan dibatasi satu toast per setengah detik.
- **Chat** di tab Agen hanya mengetik setelah Windows memastikan terminal sesi itu sudah di depan, dan memeriksanya lagi tepat sebelum mengetik.
- `settings.ini` dan sesi Kulia ditulis lewat berkas sementara lalu ditukar, jadi tidak pernah setengah tertulis.
- Rak menolak jalur jaringan (UNC) dan berkas pintasan (`.lnk`, `.url`, `.scf`, `.library-ms`, `.searchConnector-ms`), karena ikonnya bisa memicu koneksi keluar.
- Sambungan HP mati sampai kamu menyalakannya. Kunci dibuat dari kode pasangan (PBKDF2-SHA256, 60.000 putaran) dan disimpan dengan DPAPI. Setiap pesan dienkripsi AES-256 dan ditandatangani HMAC-SHA256 beserta waktu dan nonce, jadi pesan yang diubah atau diputar ulang ditolak. Berkas dari HP disimpan dengan nama yang dibersihkan dan ditandai sebagai unduhan, sehingga SmartScreen tetap memeriksanya sebelum dijalankan. Tepi tidak pernah menjalankan apa pun yang datang dari HP.
- Menemukan celah? Laporkan secara privat lewat tab **Security > Report a vulnerability** di repo ini, jangan lewat issue publik.

## Tanda tangan kode

Rilis Tepi belum ditandatangani, karena itu SmartScreen bisa memperingatkan saat pertama dijalankan. Cara memastikan exe yang kamu pegang asli:

- Setiap exe rilis dibangun dari repo ini oleh [GitHub Actions](.github/workflows/build.yml).
- SHA-256 tertulis di catatan setiap rilis, dan GitHub mencatat digest yang sama untuk berkasnya. Cek dengan `Get-FileHash Tepi.exe` di PowerShell.
- Atau build sendiri dari sumbernya (lihat [Build sendiri](#build-sendiri)).

Tepi tidak mengirim data apa pun ke luar kecuali ke layanan di bagian [Privasi](#privasi), dan semuanya bisa dimatikan.

## Build sendiri

Tidak perlu memasang SDK. Compiler C# untuk .NET Framework 4.8 sudah ada di Windows:

```
build.cmd
```

Hasilnya `Tepi.exe` di folder yang sama.

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
| `agent.port` | acak | Port server hook; instalasi lama memakai `47821` |
| `agent.wait` | `110` | Detik menahan permintaan izin |
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
| `convert` | `1` | Kotak **Ubah format** saat berkas diseret ke island |
| `shots` | `1` | Preview screenshot |
| `drives` | `1` | Flashdisk dicolok dan dikeluarkan |
| `meeting` | `1` | Mode rapat |
| `keys.caps`, `keys.num`, `keys.lang` | `1`, `0`, `1` | HUD Caps Lock, HUD Num Lock dan Scroll Lock, HUD bahasa input |
| `battery.unplug` | `0` | Ingatkan cabut charger di `80`, `85`, atau `90` persen; `0` mati |
| `note.to` | `rak` | Tujuan catatan kilat: `rak`, `todoist`, atau `notion` |
| `dnd` | `0` | Senyap: hanya izin, tenggat, sholat, dan timer yang muncul |
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
| `key.hide`, `key.nav`, `key.mini`, `key.mic`, `key.note`, `key.mirror` | `Ctrl+Alt+H`, `I`, `K`, `M`, `N`, `C` | Pintasan global, misalnya `Ctrl+Shift+F10` atau `Win+Alt+H`; `none` mematikannya |
| `key.prompter.pause`, `.restart`, `.back`, `.ahead`, `.faster`, `.slower`, `.setup`, `.stop` | `Ctrl+Alt+Space`, `R`, `Left`, `Right`, `=`, `-`, `P`, `X` | Pintasan teleprompter, hanya terpakai selama naskah berjalan |
| `kulia.url`, `kulia.key` | server kulia.live | Arahkan ke instance Kulia lain |

Variabel lingkungan untuk pengembangan:

- `TEPI_FPS=1` mencatat fps, biaya tiap bagian frame, dan frame yang lambat ke `tepi.log`.
- `TEPI_DUMP=<folder>` menyimpan frame yang dirender, karena efek kaca tidak terlihat di screenshot.
- `TEPI_SCALE=<angka>` memaksa skala tampilan.
- `TEPI_HOME=<folder>` memasang dan melepas hook di folder home lain, untuk menguji tanpa menyentuh konfigurasi agent yang asli.
- `TEPI_DIR=<folder>` menjalankan salinan uji dengan data sendiri, berdampingan dengan Tepi yang sedang dipakai. Salinan uji tidak menulis registry autostart, tidak mengecek pembaruan, dan memasang hook di `<folder>\home`.
- `build.cmd <keluaran.exe>` membangun ke lokasi lain, supaya exe yang sedang berjalan tidak tersentuh.

## Lisensi

[MIT](LICENSE), dibuat oleh R. Revaldo Vabiansyah ([@odlaver](https://github.com/odlaver)).
