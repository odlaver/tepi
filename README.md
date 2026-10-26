# Tepi

Dynamic island monokrom untuk Windows. Tepi duduk di tepi atas layar dan membesar saat ada yang terjadi: lagu yang sedang diputar beserta liriknya, agent coding (Claude Code, Codex, Gemini CLI, Antigravity) yang sedang bekerja atau minta izin, tugas kuliah yang mendekati tenggat, dan pesan yang masuk.

Native C# tanpa browser engine. File exe sekitar 300 KB, memori sekitar 6 sampai 14 MB. Tampilan tersedia dalam bahasa Indonesia dan Inggris.

## Fitur

| | |
|---|---|
| Ringkasan | Satu layar berisi lagu yang diputar, siapa saja yang mengirim pesan, dan tugas terdekat beserta tenggatnya. |
| Musik dan lirik | Semua pemutar yang muncul di kontrol media Windows: Spotify, YouTube dan YouTube Music di browser, Apple Music, Media Player, VLC, dan lainnya. Yang sedang berbunyi yang ditampilkan. Lirik tersinkron dari LRCLIB menyapu kata demi kata di island. Panel musik punya dua tata letak: teleprompter lirik atau kartu album dengan piringan hitam yang berputar. Visualizer membaca suara yang benar-benar diputar. |
| Agent coding | Claude Code, Codex, Gemini CLI, dan Antigravity. Status setiap sesi (sedang bekerja, menunggu izin, selesai beserta ringkasan balasannya). Permintaan izin dari Claude Code dan Codex bisa dijawab **Izinkan** atau **Tolak** langsung dari island, tanpa membuka terminal. Prompt singkat bisa dikirim ke terminal sesi dari tab Agen. |
| Tugas Kulia | Tugas aktif dari [Kulia](https://kulia.live) dengan tenggatnya, pengingat 60 menit sebelum tenggat, dan tombol tandai selesai. |
| Jadwal kuliah | Kelas tujuh hari ke depan dari Kulia. Kartu di atas menunjukkan kelas berikutnya dan berapa menit lagi, deretan hari untuk memilih tanggal, lalu satu baris per kelas berisi jam, mata kuliah, dan ruang. Kelas batal dicoret, ujian ditandai, kelas daring punya tombol **Buka kelas**. Selama kelas berjalan, island menampilkannya beserta sisa waktunya. Pengingat muncul 15 menit sebelum kelas. |
| Pesan | WhatsApp, Telegram, Discord, Teams, Slack, email, dan aplikasi chat lain dari pusat notifikasi Windows. Di Ringkasan, pesan dikelompokkan per pengirim. |
| Clipboard | 25 teks terakhir yang kamu salin, klik untuk menyalinnya lagi. Hanya di memori, dan mati sampai kamu menyalakannya. |
| Unduhan | Unduhan di Chrome, Edge, Firefox, Brave, dan Opera tampil di island beserta ukuran dan kecepatannya. Klik toast **Unduhan selesai** untuk membuka foldernya. |
| Teleprompter | Tempel naskah, dan island menggulirkannya pelan di tepi atas layar, dekat kamera. Kecepatan dihitung dalam kata per menit. Ada hitung mundur, mode cermin untuk teleprompter kaca, penanda `[jeda]`, dan sisa waktu baca. Naskah tidak ikut terekam saat screen share atau rekam layar. |
| Fokus | Timer 25 atau 50 menit, lalu istirahat. |
| Skrip | Skrip apa pun bisa memunculkan toast atau bilah progres di island lewat `Tepi.exe --notify`. |
| Aktivitas sistem | Volume, charger dan baterai, Wi-Fi, serta aplikasi yang memakai mikrofon atau kamera. |
| Tampilan | Menempel di tepi atas layar atau mengambang, efek kaca, tema gelap atau terang, warna aksen, posisi kiri sampai kanan, lebar panel, serta tab mana yang tampil dan urutannya. Otomatis menyingkir saat ada aplikasi layar penuh. |
| Aksesibilitas | Toast dan permintaan izin dibacakan oleh pembaca layar (Narrator, NVDA). Island bisa dipilih ikut terekam atau tidak saat screenshot dan rekam layar. |

## Pasang

1. Unduh [**Tepi.exe**](https://github.com/odlaver/tepi/raw/main/Tepi.exe) (langsung terunduh sekali klik), atau dari halaman [Releases](../../releases).
2. Jalankan. Karena exe belum ditandatangani, Windows SmartScreen bisa memunculkan peringatan: pilih **More info** lalu **Run anyway**. Kalau ragu, build sendiri dari sumbernya (lihat [Build sendiri](#build-sendiri)).
3. Ikon Tepi muncul di system tray. Di Windows 11 ikonnya bisa tersembunyi di balik panah **^** di taskbar.

Syarat: Windows 10 versi 2004 atau lebih baru, atau Windows 11, 64-bit. .NET Framework 4.8 sudah bawaan Windows.

Tepi memberi tahu sendiri kalau ada versi baru. Klik kanan ikon Tepi, lalu **Perbarui ke Tepi x.y.z**: Tepi mengunduh exe baru dari halaman rilis repo ini, mencocokkan SHA-256-nya, lalu membuka ulang dirinya. Kalau mau dikabari lewat email juga, klik **Watch › Custom › Releases** di repo ini.

## Cara pakai

- **Arahkan kursor** ke island untuk membuka panel. Ikon di atas panel berpindah antara Ringkasan, Musik, Agen, Tugas, Pesan, Jadwal, dan Clipboard.
- **Scroll** di atas island untuk mengubah volume.
- **Klik kanan** island atau ikon tray untuk menu: Sembunyikan island, Fokus, Teleprompter, Hubungkan agen, Kulia, Pengaturan, Buka folder data.
- **Fokus** di menu tray memulai timer 25 atau 50 menit, lalu istirahat 5 menit. Timer tampil di island, atau sebagai cincin di samping pil musik.
- **Ctrl+Alt+H** menyembunyikan atau memunculkan island. Kartu izin agent tetap muncul walau island disembunyikan.
- Di tab **Musik**, tombol di kanan atas mengganti tata letak antara lirik dan kartu album. Klik baris lirik untuk loncat ke bagian lagu itu.
- Di tab **Jadwal**, klik hari di deretan tengah untuk melihat kelas tanggal itu. Kelas daring yang punya tautan bisa diklik untuk membukanya.
- Di tab **Clipboard**, klik teks untuk menyalinnya lagi. Tombol tempat sampah di kanan atas menghapus semuanya.

### Pengaturan

Klik kanan ikon Tepi, lalu **Pengaturan**. Setiap perubahan langsung berlaku tanpa perlu menyimpan.

- **Tampilan**: bahasa (Indonesia atau English), tema, warna aksen, monitor, efek kaca, dan menempel di tepi atas.
- **Tata letak**: posisi island dari kiri sampai kanan, dan lebar panel 440 sampai 560 px. Panel terbuka sebentar selama diatur supaya hasilnya terlihat.
- **Aksesibilitas**: pilih apakah island ikut terekam saat screenshot, rekam layar, dan screen share, serta apakah toast dibacakan pembaca layar.
- **Tab panel**: centang tab yang mau ditampilkan, urutkan dengan **Naik** dan **Turun**, lalu pilih tab yang terbuka pertama.
- **Island**: lirik, lirik online, visualizer, notifikasi pesan, isi pesan, kelas yang berlangsung, progres unduhan, riwayat clipboard, mulai bersama Windows, dan lama istirahat fokus.

Island yang ikut terekam tidak bisa memakai efek kaca, karena efek kaca membaca layar di belakangnya dan akan ikut membaca dirinya sendiri. Selama opsi itu menyala, latarnya polos.

### Teleprompter

1. Klik kanan ikon Tepi, lalu **Teleprompter**.
2. Tempel naskah, atur kecepatan (kata per menit) dan ukuran huruf, nyalakan **Cermin** kalau memakai teleprompter kaca, lalu **Mulai** (atau Ctrl+Enter).

Naskah mulai setelah hitung mundur 3 detik. Selama naskah berjalan:

- Klik island, atau tekan **Ctrl+Alt+Spasi** dari aplikasi mana pun, untuk jeda dan lanjut. Pintasan ini hanya terpakai selama teleprompter berjalan.
- Tulis `[jeda]` di naskah untuk berhenti otomatis di titik itu. Baris itu ditandai titik di kiri.
- Scroll di atas island untuk mundur atau maju beberapa baris.
- Sisa waktu baca tampil di kanan atas.
- Arahkan kursor ke island untuk tombol pelan, jeda, cepat, dan tutup.
- Panel dan toast tidak muncul supaya naskah tidak tertutup. Kartu izin agent tetap muncul, dan pengingat kelas ditunda sampai teleprompter ditutup.
- Naskah terakhir disimpan di `%APPDATA%\Tepi\prompter.txt` dan terisi lagi saat Teleprompter dibuka.

### Kirim prompt ke agent

Di tab **Agen**, arahkan kursor ke sebuah sesi lalu klik **Kirim**. Tulis prompt, tekan Enter. Tepi membawa terminal sesi itu ke depan, lalu mengetik prompt dan menekan Enter. Kalau terminalnya tidak bisa dibawa ke depan, tidak ada yang diketik.

Kalau beberapa sesi berjalan di tab berbeda dalam satu jendela Windows Terminal, prompt masuk ke tab yang sedang aktif. Kotak kirim mengingatkan hal ini.

### Skrip

```
Tepi.exe --notify "Backup selesai" "12 berkas"
Tepi.exe --notify "Build" --progress 40 --id build
```

Yang pertama memunculkan toast. Yang kedua menampilkan bilah progres dengan nama `build`. Kirim angka baru dengan `--id` yang sama untuk memperbaruinya, dan `100` untuk menutupnya. Bilah yang tidak diperbarui selama 2 menit hilang sendiri.

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

- Claude Code mengirim event ke `http://127.0.0.1:47821/hook`. Agent lain menjalankan `Tepi.exe --relay <agent>`, yang meneruskan event ke alamat yang sama. Semua request memakai token acak yang dibuat saat Tepi pertama kali jalan. Request tanpa token yang benar, atau yang datang dari browser, ditolak.
- Tab **Agen** menampilkan durasi setiap sesi, jumlah berkas yang diubah, dan jumlah perintah yang dijalankan.
- Klik sesi di tab **Agen**, atau toast **Selesai**, untuk membuka jendela terminal sesi itu. Kalau beberapa sesi ada di tab berbeda dalam satu jendela Windows Terminal, yang dibuka jendelanya.
- Saat agent minta izin, island berbalik putih dan menahan permintaan itu sampai kamu memilih. Kalau tidak dijawab dalam 110 detik, atau kamu memilih **Jawab di terminal**, keputusan kembali ke prompt biasa di terminal.
- Kalau Tepi sedang tidak jalan, agent tetap bekerja seperti biasa tanpa pesan galat.
- Hook menyimpan lokasi `Tepi.exe`. Kalau exe dipindah, lepas lalu hubungkan lagi dari menu.
- Hook `Stop` dan `SessionEnd` Claude Code memakai `curl` lewat Git Bash, yang memang dibutuhkan Claude Code di Windows.

### Notifikasi pesan

- Saat pertama jalan, izinkan Tepi membaca notifikasi. Kalau terlewat: **Pengaturan Windows > Privasi & keamanan > Notifikasi**.
- Tepi hanya membaca notifikasi yang ditampilkan Windows. Chat yang kamu mute di WhatsApp tidak memunculkan notifikasi, jadi tidak ikut tampil.
- Supaya tidak muncul dobel dengan popup Windows: **Pengaturan > Sistem > Notifikasi**, pilih aplikasinya, lalu matikan **Tampilkan banner notifikasi**.
- Pesan tetap ada di Tepi sampai kamu membuka aplikasinya dari Tepi atau menghapusnya, termasuk dari aplikasi yang tidak menyimpan notifikasi di pusat notifikasi. Pesan seperti ini hilang kalau Tepi ditutup.
- Supaya pesan WhatsApp ikut hilang saat sudah dibaca di HP atau WhatsApp Web, nyalakan **Pengaturan > Sistem > Notifikasi > WhatsApp > Tampilkan notifikasi di pusat notifikasi**. Tanpa itu, Windows membuang notifikasinya sebelum WhatsApp sempat menariknya.
- Pesan yang dikirim ulang aplikasi dengan isi yang persis sama hanya muncul sekali.
- Kalau pesan WhatsApp tidak pernah muncul sama sekali, cek **Pengaturan > Sistem > Notifikasi > WhatsApp** dan pastikan notifikasinya menyala.
- Isi pesan tidak disimpan ke disk dan tidak dikirim ke mana pun.

### Kulia

1. Buka tab **Tugas** (atau klik kanan ikon Tepi, lalu **Kulia > Masuk ke Kulia**).
2. Isi email yang kamu pakai di [kulia.live](https://kulia.live).
3. Masukkan kode yang dikirim ke email itu.

Selesai. Tugas aktif dan jadwal kelasmu langsung tampil, lalu diperbarui tiap 10 menit. Kalau belum punya akun, daftar dulu di kulia.live.

Sesi login disimpan terenkripsi dengan DPAPI Windows dan hanya bisa dibuka oleh akun Windows-mu. Untuk keluar: klik kanan ikon Tepi, lalu **Kulia > Keluar dari Kulia**.

## Privasi

Tepi hanya menghubungi tiga layanan luar, dan masing-masing bisa dimatikan:

| Layanan | Data yang dikirim | Kapan | Cara mematikan |
|---|---|---|---|
| [LRCLIB](https://lrclib.net) | Judul, artis, dan durasi lagu | Saat lagu baru diputar | **Pengaturan > Cari lirik online** |
| [Kulia](https://kulia.live) | Email, kode masuk, dan permintaan daftar tugas serta jadwalmu | Hanya kalau kamu masuk ke Kulia | Menu tray > **Kulia > Keluar dari Kulia** |
| GitHub | Permintaan versi rilis terbaru. `Tepi.exe` baru hanya diunduh saat kamu klik **Perbarui**. | Sekali sehari | `update.check=0` di `settings.ini` |

- Isi pesan, data agent, dan isi layar tidak pernah dikirim ke mana pun. Server hook hanya bisa dijangkau dari komputer ini.
- Lirik yang ditemukan disimpan di `%APPDATA%\Tepi\lyrics` supaya tidak diambil ulang.
- Efek kaca membaca layar di belakang island untuk membuat latar buramnya. Karena itu island tidak ikut terekam saat screenshot atau screen share. Nyalakan **Tampil di rekaman layar** di Pengaturan kalau island perlu ikut terekam. Selama teleprompter berjalan, island selalu disembunyikan dari rekaman.
- Naskah teleprompter hanya disimpan di komputer ini.
- Riwayat clipboard hanya ada di memori dan hilang saat Tepi ditutup atau fiturnya dimatikan. Teks yang ditandai rahasia oleh pengelola kata sandi tidak dicatat.
- Progres unduhan hanya membaca nama dan ukuran berkas sementara di folder Downloads.
- Semua pengaturan dan cache ada di `%APPDATA%\Tepi`.

## Keamanan

- Server hook hanya mendengarkan di `127.0.0.1`, jadi tidak bisa dijangkau dari jaringan. Setiap request wajib membawa token acak 48 karakter, dan request yang datang dari browser ditolak.
- Kartu izin menampilkan perintah atau isi perubahan berkas secara utuh. **Izinkan** terkunci kalau isinya lebih dari 10 baris (jawab di terminal untuk membacanya utuh), dan juga selama 0,8 detik pertama setelah kartu muncul supaya klik yang kebetulan mendarat di sana tidak ikut mengizinkan.
- Kalau Tepi tidak jalan atau tidak menjawab, agent kembali ke prompt izinnya sendiri. Tepi tidak pernah mengizinkan apa pun tanpa klikmu.
- Tautan tugas Kulia hanya dibuka kalau alamatnya `http` atau `https`.
- Tidak ada pembaruan diam-diam. Exe hanya berubah saat kamu klik **Perbarui ke Tepi x.y.z**. Tepi hanya menerima `Tepi.exe` dari alamat rilis repo ini, mengunduhnya lewat HTTPS, lalu mencocokkan SHA-256-nya dengan digest yang dicatat GitHub untuk berkas itu. Kalau tidak cocok, exe lama tidak disentuh dan halaman rilis dibuka. Exe lama disimpan sebagai `Tepi.old.exe` sampai Tepi baru jalan, lalu dihapus. Kalau folder exe tidak bisa ditulis (misalnya di Program Files), Tepi membuka halaman rilis.
- `Tepi.exe --notify` dan `/notify` dijaga token yang sama dengan hook agent. Teks dari skrip dibersihkan dari karakter kontrol, dipotong panjangnya, dan dibatasi satu toast per setengah detik.
- **Kirim** di tab Agen hanya mengetik setelah Windows memastikan terminal sesi itu sudah di depan.
- Menemukan celah? Laporkan secara privat lewat tab **Security > Report a vulnerability** di repo ini, jangan lewat issue publik.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org). The application to SignPath Foundation is in progress; signed releases will say so in their release notes.

- Committers and reviewers: [odlaver](https://github.com/odlaver)
- Approvers: [odlaver](https://github.com/odlaver)
- Builds: every release binary is built from this repository by [GitHub Actions](.github/workflows/build.yml).

Privacy policy: this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it, except the three services listed under [Privasi](#privasi), each of which can be turned off.

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
| `glass` | `1` | Efek kaca |
| `theme` | `gelap` | `gelap` atau `terang` |
| `lyrics` | `1` | Lirik tersinkron di island |
| `lyrics.online` | `1` | Cari lirik di LRCLIB |
| `visualizer` | `1` | Visualizer musik |
| `music.view` | `auto` | `auto`, `lyrics`, atau `album` |
| `notif` | `1` | Notifikasi pesan |
| `notif.content` | `1` | Tampilkan isi pesan |
| `agent.port` | `47821` | Port server hook |
| `agent.wait` | `110` | Detik menahan permintaan izin |
| `update.check` | `1` | Cek versi baru sekali sehari |
| `accent` | kosong | Warna aksen, misalnya `#4C8DFF`; kosong untuk monokrom |
| `monitor` | kosong | Monitor tempat island tampil; kosong untuk monitor utama |
| `focus.break` | `5` | Menit istirahat setelah fokus |
| `dock` | `1` | Menempel di tepi atas; `0` untuk mengambang |
| `position` | `0` | Geser island, `-40` (kiri) sampai `40` (kanan), persen dari ruang kosong |
| `panel.width` | `488` | Lebar panel, `440` sampai `560` |
| `tabs` | `0,1,2,3,4,5,6` | Tab yang tampil dan urutannya: 0 Ringkasan, 1 Musik, 2 Agen, 3 Tugas, 4 Pesan, 5 Jadwal, 6 Clipboard |
| `panel.page` | `-1` | Tab yang terbuka pertama; `-1` untuk tab pertama di urutan |
| `prompter.wpm` | `140` | Kecepatan teleprompter, kata per menit |
| `prompter.size` | `19` | Ukuran huruf teleprompter, px |
| `prompter.mirror` | `0` | Teleprompter dicerminkan |
| `lang` | `id` | `id` atau `en` |
| `capture.show` | `0` | Island ikut terekam saat screenshot dan rekam layar (tanpa efek kaca) |
| `a11y.announce` | `1` | Bacakan toast ke pembaca layar yang sedang aktif |
| `clipboard` | `0` | Riwayat clipboard |
| `downloads` | `1` | Progres unduhan |
| `class.island` | `1` | Kelas yang sedang berjalan tampil di island |
| `kulia.url`, `kulia.key` | server kulia.live | Arahkan ke instance Kulia lain |

Variabel lingkungan untuk pengembangan:

- `TEPI_FPS=1` mencatat fps, biaya tiap bagian frame, dan frame yang lambat ke `tepi.log`.
- `TEPI_DUMP=<folder>` menyimpan frame yang dirender, karena efek kaca tidak terlihat di screenshot.
- `TEPI_SCALE=<angka>` memaksa skala tampilan.
- `TEPI_HOME=<folder>` memasang dan melepas hook di folder home lain, untuk menguji tanpa menyentuh konfigurasi agent yang asli.

## Lisensi

[MIT](LICENSE)
