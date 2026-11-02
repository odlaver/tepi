# Privasi dan keamanan

[← Kembali ke README](../README.md)

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
- Rak hanya menyimpan jalur berkas, di memori, dan hilang saat Tepi ditutup. Kalau **Simpan isi rak** dinyalakan, jalurnya disimpan di `shelf.txt` di folder data.
- **Teks** di preview screenshot dibaca oleh OCR bawaan Windows di laptop ini; tombolnya hanya muncul kalau Windows punya bahasa OCR yang terpasang.
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

- Setiap exe rilis dibangun dari repo ini oleh [GitHub Actions](../.github/workflows/build.yml).
- SHA-256 tertulis di catatan setiap rilis, dan GitHub mencatat digest yang sama untuk berkasnya. Cek dengan `Get-FileHash Tepi.exe` di PowerShell.
- Atau build sendiri dari sumbernya (lihat [Build sendiri](../README.md#build-sendiri)).

Tepi tidak mengirim data apa pun ke luar kecuali ke layanan di bagian [Privasi](privasi-keamanan.md#privasi), dan semuanya bisa dimatikan.
