# Tepi

Dynamic island monokrom untuk Windows. Tepi duduk di tepi atas layar dan membesar saat ada yang terjadi: lagu yang sedang diputar beserta liriknya, agent Claude Code yang sedang bekerja atau minta izin, tugas kuliah yang mendekati tenggat, dan pesan yang masuk.

Native C# tanpa browser engine. File exe sekitar 190 KB, memori sekitar 6 sampai 14 MB.

## Fitur

| | |
|---|---|
| Ringkasan | Satu layar berisi lagu yang diputar, siapa saja yang mengirim pesan, dan tugas terdekat beserta tenggatnya. |
| Musik dan lirik | Semua pemutar yang muncul di kontrol media Windows: Spotify, YouTube dan YouTube Music di browser, Apple Music, Media Player, VLC, dan lainnya. Yang sedang berbunyi yang ditampilkan. Lirik tersinkron dari LRCLIB menyapu kata demi kata di island. Panel musik punya dua tata letak: teleprompter lirik atau kartu album dengan piringan hitam yang berputar. Visualizer membaca suara yang benar-benar diputar. |
| Agent Claude Code | Status setiap sesi (sedang bekerja, menunggu izin, selesai beserta ringkasan balasannya). Permintaan izin bisa dijawab **Izinkan** atau **Tolak** langsung dari island, tanpa membuka terminal. |
| Tugas Kulia | Tugas aktif dari [Kulia](https://kulia.live) dengan tenggatnya, pengingat 60 menit sebelum tenggat, dan tombol tandai selesai. |
| Pesan | WhatsApp, Telegram, Discord, Teams, Slack, email, dan aplikasi chat lain dari pusat notifikasi Windows. Di Ringkasan, pesan dikelompokkan per pengirim. |
| Aktivitas sistem | Volume, charger dan baterai, Wi-Fi, serta aplikasi yang memakai mikrofon atau kamera. |
| Tampilan | Efek kaca yang tembus samar ke layar di belakangnya, tema gelap atau terang, dan otomatis menyingkir saat ada aplikasi layar penuh. |

## Pasang

1. Unduh `Tepi.exe` dari halaman [Releases](../../releases).
2. Jalankan. Karena exe belum ditandatangani, Windows SmartScreen bisa memunculkan peringatan: pilih **More info** lalu **Run anyway**. Kalau ragu, build sendiri dari sumbernya (lihat [Build sendiri](#build-sendiri)).
3. Ikon Tepi muncul di system tray. Di Windows 11 ikonnya bisa tersembunyi di balik panah **^** di taskbar.

Syarat: Windows 10 versi 2004 atau lebih baru, atau Windows 11, 64-bit. .NET Framework 4.8 sudah bawaan Windows.

## Cara pakai

- **Arahkan kursor** ke island untuk membuka panel. Ikon di atas panel berpindah antara Ringkasan, Musik, Agen, Tugas, dan Pesan.
- **Scroll** di atas island untuk mengubah volume.
- **Klik kanan** island atau ikon tray untuk menu: Hubungkan Claude Code, Kulia, Efek kaca, Island terang, Lirik di island, Visualizer, Notifikasi pesan, Tampilkan isi pesan, Mulai bersama Windows.
- Di tab **Musik**, tombol di kanan atas mengganti tata letak antara lirik dan kartu album. Klik baris lirik untuk loncat ke bagian lagu itu.

### Menghubungkan Claude Code

1. Klik kanan ikon Tepi, pilih **Hubungkan Claude Code**.
2. Buka sesi Claude Code baru. Sesi yang sudah terbuka sebelumnya tidak ikut terpantau.

Tepi menambahkan hook ke `settings.json` di setiap profil Claude Code yang ditemukan (`%USERPROFILE%\.claude` dan `%USERPROFILE%\.claude-*`). Salinan aslinya disimpan sebagai `settings.json.bak-tepi`. Klik menu yang sama sekali lagi untuk melepasnya.

Cara kerjanya:

- Hook mengirim event ke `http://127.0.0.1:47821/hook` dengan token acak yang dibuat saat Tepi pertama kali jalan. Request tanpa token yang benar, atau yang datang dari browser, ditolak.
- Saat Claude minta izin, island berbalik putih dan menahan permintaan itu sampai kamu memilih. Kalau tidak dijawab dalam 110 detik, atau kamu memilih **Jawab di terminal**, keputusan kembali ke prompt biasa di terminal.
- Kalau Tepi sedang tidak jalan, Claude Code tetap bekerja seperti biasa tanpa pesan galat.
- Hook `Stop` dan `SessionEnd` memakai `curl` lewat Git Bash, yang memang dibutuhkan Claude Code di Windows.

### Notifikasi pesan

- Saat pertama jalan, izinkan Tepi membaca notifikasi. Kalau terlewat: **Pengaturan Windows > Privasi & keamanan > Notifikasi**.
- Tepi hanya membaca notifikasi yang ditampilkan Windows. Chat yang kamu mute di WhatsApp tidak memunculkan notifikasi, jadi tidak ikut tampil.
- Supaya tidak muncul dobel dengan popup Windows: **Pengaturan > Sistem > Notifikasi**, pilih aplikasinya, lalu matikan **Tampilkan banner notifikasi** dan biarkan notifikasi tetap masuk ke pusat notifikasi.
- Isi pesan tidak disimpan ke disk dan tidak dikirim ke mana pun.

### Kulia

1. Buka tab **Tugas** (atau klik kanan ikon Tepi, lalu **Kulia > Masuk ke Kulia**).
2. Isi email yang kamu pakai di [kulia.live](https://kulia.live).
3. Masukkan kode 6 angka yang dikirim ke email itu.

Selesai. Tugas aktifmu langsung tampil dan diperbarui tiap 10 menit. Kalau belum punya akun, daftar dulu di kulia.live.

Sesi login disimpan terenkripsi dengan DPAPI Windows dan hanya bisa dibuka oleh akun Windows-mu. Untuk keluar: klik kanan ikon Tepi, lalu **Kulia > Keluar dari Kulia**.

## Privasi

- Ke LRCLIB hanya terkirim judul, artis, dan durasi lagu. Lirik yang ditemukan disimpan di `%APPDATA%\Tepi\lyrics` supaya tidak diambil ulang.
- Data Kulia hanya diambil dari server Kulia dengan akunmu sendiri.
- Efek kaca membaca layar di belakang island untuk membuat latar buramnya. Karena itu island tidak ikut terekam saat screenshot atau screen share. Matikan **Efek kaca** di menu kalau island perlu ikut terekam.
- Semua pengaturan dan cache ada di `%APPDATA%\Tepi`.

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
| `visualizer` | `1` | Visualizer musik |
| `music.view` | `auto` | `auto`, `lyrics`, atau `album` |
| `notif` | `1` | Notifikasi pesan |
| `notif.content` | `1` | Tampilkan isi pesan |
| `agent.port` | `47821` | Port server hook |
| `agent.wait` | `110` | Detik menahan permintaan izin |
| `kulia.url`, `kulia.key` | server kulia.live | Arahkan ke instance Kulia lain |

Variabel lingkungan untuk pengembangan:

- `TEPI_FPS=1` mencatat fps, biaya tiap bagian frame, dan frame yang lambat ke `tepi.log`.
- `TEPI_DUMP=<folder>` menyimpan frame yang dirender, karena efek kaca tidak terlihat di screenshot.
- `TEPI_SCALE=<angka>` memaksa skala tampilan.

## Lisensi

[MIT](LICENSE)
