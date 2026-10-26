# Tepi

Dynamic island monokrom untuk Windows. Tepi duduk di tepi atas layar dan membesar saat ada yang terjadi: lagu yang sedang diputar beserta liriknya, agent coding (Claude Code, Codex, Gemini CLI, Antigravity) yang sedang bekerja atau minta izin, tugas kuliah yang mendekati tenggat, dan pesan yang masuk.

Native C# tanpa browser engine. File exe sekitar 200 KB, memori sekitar 6 sampai 14 MB.

## Fitur

| | |
|---|---|
| Ringkasan | Satu layar berisi lagu yang diputar, siapa saja yang mengirim pesan, dan tugas terdekat beserta tenggatnya. |
| Musik dan lirik | Semua pemutar yang muncul di kontrol media Windows: Spotify, YouTube dan YouTube Music di browser, Apple Music, Media Player, VLC, dan lainnya. Yang sedang berbunyi yang ditampilkan. Lirik tersinkron dari LRCLIB menyapu kata demi kata di island. Panel musik punya dua tata letak: teleprompter lirik atau kartu album dengan piringan hitam yang berputar. Visualizer membaca suara yang benar-benar diputar. |
| Agent coding | Claude Code, Codex, Gemini CLI, dan Antigravity. Status setiap sesi (sedang bekerja, menunggu izin, selesai beserta ringkasan balasannya). Permintaan izin dari Claude Code dan Codex bisa dijawab **Izinkan** atau **Tolak** langsung dari island, tanpa membuka terminal. |
| Tugas Kulia | Tugas aktif dari [Kulia](https://kulia.live) dengan tenggatnya, pengingat 60 menit sebelum tenggat, dan tombol tandai selesai. |
| Pesan | WhatsApp, Telegram, Discord, Teams, Slack, email, dan aplikasi chat lain dari pusat notifikasi Windows. Di Ringkasan, pesan dikelompokkan per pengirim. |
| Aktivitas sistem | Volume, charger dan baterai, Wi-Fi, serta aplikasi yang memakai mikrofon atau kamera. |
| Tampilan | Efek kaca yang tembus samar ke layar di belakangnya, tema gelap atau terang, dan otomatis menyingkir saat ada aplikasi layar penuh. |

## Pasang

1. Unduh [**Tepi.exe**](https://github.com/odlaver/tepi/raw/main/Tepi.exe) (langsung terunduh sekali klik), atau dari halaman [Releases](../../releases).
2. Jalankan. Karena exe belum ditandatangani, Windows SmartScreen bisa memunculkan peringatan: pilih **More info** lalu **Run anyway**. Kalau ragu, build sendiri dari sumbernya (lihat [Build sendiri](#build-sendiri)).
3. Ikon Tepi muncul di system tray. Di Windows 11 ikonnya bisa tersembunyi di balik panah **^** di taskbar.

Syarat: Windows 10 versi 2004 atau lebih baru, atau Windows 11, 64-bit. .NET Framework 4.8 sudah bawaan Windows.

Tepi memberi tahu sendiri kalau ada versi baru. Kalau mau dikabari lewat email juga, klik **Watch › Custom › Releases** di repo ini.

## Cara pakai

- **Arahkan kursor** ke island untuk membuka panel. Ikon di atas panel berpindah antara Ringkasan, Musik, Agen, Tugas, dan Pesan.
- **Scroll** di atas island untuk mengubah volume.
- **Klik kanan** island atau ikon tray untuk menu: Sembunyikan island, Fokus, Hubungkan agen, Kulia, Efek kaca, Island terang, Warna aksen, Lirik di island, Cari lirik online, Visualizer, Notifikasi pesan, Tampilkan isi pesan, Layar (kalau monitornya lebih dari satu), Mulai bersama Windows.
- **Fokus** di menu tray memulai timer 25 atau 50 menit, lalu istirahat 5 menit. Timer tampil di island, atau sebagai cincin di samping pil musik.
- **Warna aksen** mewarnai tab aktif, visualizer, dan bilah progres. Pilih **Monokrom** untuk kembali hitam-putih.
- **Ctrl+Alt+H** menyembunyikan atau memunculkan island. Kartu izin agent tetap muncul walau island disembunyikan.
- Di tab **Musik**, tombol di kanan atas mengganti tata letak antara lirik dan kartu album. Klik baris lirik untuk loncat ke bagian lagu itu.

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
3. Masukkan kode 6 angka yang dikirim ke email itu.

Selesai. Tugas aktifmu langsung tampil dan diperbarui tiap 10 menit. Kalau belum punya akun, daftar dulu di kulia.live.

Sesi login disimpan terenkripsi dengan DPAPI Windows dan hanya bisa dibuka oleh akun Windows-mu. Untuk keluar: klik kanan ikon Tepi, lalu **Kulia > Keluar dari Kulia**.

## Privasi

Tepi hanya menghubungi tiga layanan luar, dan masing-masing bisa dimatikan:

| Layanan | Data yang dikirim | Kapan | Cara mematikan |
|---|---|---|---|
| [LRCLIB](https://lrclib.net) | Judul, artis, dan durasi lagu | Saat lagu baru diputar | Menu tray > **Cari lirik online** |
| [Kulia](https://kulia.live) | Email, kode masuk, dan permintaan daftar tugasmu | Hanya kalau kamu masuk ke Kulia | Menu tray > **Kulia > Keluar dari Kulia** |
| GitHub API | Permintaan versi rilis terbaru | Sekali sehari | `update.check=0` di `settings.ini` |

- Isi pesan, data agent, dan isi layar tidak pernah dikirim ke mana pun. Server hook hanya bisa dijangkau dari komputer ini.
- Lirik yang ditemukan disimpan di `%APPDATA%\Tepi\lyrics` supaya tidak diambil ulang.
- Efek kaca membaca layar di belakang island untuk membuat latar buramnya. Karena itu island tidak ikut terekam saat screenshot atau screen share. Matikan **Efek kaca** di menu kalau island perlu ikut terekam.
- Semua pengaturan dan cache ada di `%APPDATA%\Tepi`.

## Keamanan

- Server hook hanya mendengarkan di `127.0.0.1`, jadi tidak bisa dijangkau dari jaringan. Setiap request wajib membawa token acak 48 karakter, dan request yang datang dari browser ditolak.
- Kartu izin menampilkan perintah atau isi perubahan berkas secara utuh. **Izinkan** terkunci kalau isinya lebih dari 10 baris (jawab di terminal untuk membacanya utuh), dan juga selama 0,8 detik pertama setelah kartu muncul supaya klik yang kebetulan mendarat di sana tidak ikut mengizinkan.
- Kalau Tepi tidak jalan atau tidak menjawab, agent kembali ke prompt izinnya sendiri. Tepi tidak pernah mengizinkan apa pun tanpa klikmu.
- Tautan tugas Kulia hanya dibuka kalau alamatnya `http` atau `https`.
- Tidak ada pembaruan otomatis. Kalau ada versi baru, Tepi hanya memberi tahu dan menampilkan **Unduh Tepi x.y.z** di menu tray, yang membuka halaman rilis di repo ini. Exe hanya berubah kalau kamu mengunduh rilis baru. Cocokkan SHA-256 yang tertulis di halaman rilis, atau build sendiri dari sumbernya.
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
| `kulia.url`, `kulia.key` | server kulia.live | Arahkan ke instance Kulia lain |

Variabel lingkungan untuk pengembangan:

- `TEPI_FPS=1` mencatat fps, biaya tiap bagian frame, dan frame yang lambat ke `tepi.log`.
- `TEPI_DUMP=<folder>` menyimpan frame yang dirender, karena efek kaca tidak terlihat di screenshot.
- `TEPI_SCALE=<angka>` memaksa skala tampilan.
- `TEPI_HOME=<folder>` memasang dan melepas hook di folder home lain, untuk menguji tanpa menyentuh konfigurasi agent yang asli.

## Lisensi

[MIT](LICENSE)
