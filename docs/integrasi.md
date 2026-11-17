# Kulia, Notion, Todoist, kalender, Spotify, dan pesan

[← Kembali ke README](../README.md)

## Kulia

1. Buka tab **Tugas** (atau klik kanan ikon Tepi, lalu **Kulia > Masuk ke Kulia**).
2. Isi email yang kamu pakai di [kulia.live](https://kulia.live).
3. Masukkan kode yang dikirim ke email itu.

Selesai. Tugas aktif dan jadwal kelasmu langsung tampil, lalu diperbarui tiap 10 menit. Kalau belum punya akun, daftar dulu di kulia.live.

Sesi login disimpan terenkripsi dengan DPAPI Windows dan hanya bisa dibuka oleh akun Windows-mu. Untuk keluar: klik kanan ikon Tepi, lalu **Kulia > Keluar dari Kulia**.

## Integrasi

Buka **Pengaturan > Integrasi**:

- **Notion**: buat integrasi internal di [notion.so/my-integrations](https://www.notion.so/my-integrations) (tombol **Buat token Notion** di Tepi membukanya), salin tokennya, lalu bagikan database tugasmu ke integrasi itu (di Notion: **••• > Koneksi**). Pilih database, kolom tanggal, dan kolom selesai. Tugas bisa ditandai selesai dari Tepi kalau integrasinya diberi izin mengubah isi.
- **Todoist**: tempel token API dari **Pengaturan > Integrasi > Developer** di Todoist (**Ambil token Todoist** di Tepi membuka halaman itu). Tugas aktif yang punya tenggat ikut tampil dan bisa ditutup dari Tepi.
- **Kalender**: tempel tautan iCal (`https` atau `webcal`). Di Google Calendar, pakai **Secret address in iCal format** dari pengaturan kalender. Outlook, iCloud, dan aplikasi lain yang punya tautan iCal juga bisa. Paling banyak 12 kalender.

Data diperbarui tiap 10 menit, dan saat tab Tugas atau Jadwal dibuka. Token dan tautan kalender disimpan terenkripsi dengan DPAPI Windows dan tidak pernah ditulis ke log.

## Spotify

Tanpa pengaturan apa pun, "putar Evaluasi dari Hindia" memutar hasil teratas YouTube di browser bawaanmu. Sambungkan Spotify kalau ingin lagunya main di aplikasi Spotify. Butuh Spotify **Premium**: Spotify hanya mengizinkan aplikasi lain memutar lagu untuk akun Premium, dan pemilik app developer juga wajib Premium. Tanpa Premium, "putar Evaluasi di Spotify" hanya membuka hasil pencarian Spotify.

1. Buka [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) dengan akun Spotify-mu, lalu **Create app**.
2. Isi nama dan deskripsi bebas. Di **Redirect URIs**, isi `http://127.0.0.1/callback` (tanpa port, bukan `localhost`). Centang **Web API**, lalu simpan.
3. Salin **Client ID** dari halaman app itu.
4. Di Tepi, buka **Pengaturan > Integrasi > Spotify**, tempel Client ID, lalu **Masuk**. Browser terbuka; setujui izinnya.

App developer Spotify dibatasi 5 pengguna, jadi tiap orang sebaiknya membuat app sendiri. Tepi meminta izin mengatur pemutaran, melihat perangkat yang sedang memutar, dan membaca jenis akunmu. Kalau aplikasi Spotify di laptop belum terbuka, Tepi membukanya lalu memutar di sana. Login disimpan terenkripsi dengan DPAPI Windows; **Lepas** menghapusnya.

## Notifikasi pesan

- Saat pertama jalan, izinkan Tepi membaca notifikasi. Kalau terlewat: **Pengaturan Windows > Privasi & keamanan > Notifikasi**.
- Tepi hanya membaca notifikasi yang ditampilkan Windows. Chat yang kamu mute di WhatsApp tidak memunculkan notifikasi, jadi tidak ikut tampil.
- Supaya tidak muncul dobel dengan popup Windows: **Pengaturan > Sistem > Notifikasi**, pilih aplikasinya, lalu matikan **Tampilkan banner notifikasi**.
- Pesan tetap ada di Tepi sampai kamu membuka aplikasinya dari Tepi atau menghapusnya, termasuk dari aplikasi yang tidak menyimpan notifikasi di pusat notifikasi. Pesan seperti ini hilang kalau Tepi ditutup.
- Supaya pesan WhatsApp ikut hilang saat sudah dibaca di HP atau WhatsApp Web, nyalakan **Pengaturan > Sistem > Notifikasi > WhatsApp > Tampilkan notifikasi di pusat notifikasi**. Tanpa itu, Windows membuang notifikasinya sebelum WhatsApp sempat menariknya.
- Pesan yang dikirim ulang aplikasi dengan isi yang persis sama hanya muncul sekali.
- Kalau pesan WhatsApp tidak pernah muncul sama sekali, cek **Pengaturan > Sistem > Notifikasi > WhatsApp** dan pastikan notifikasinya menyala.
- Isi pesan tidak disimpan ke disk dan tidak dikirim ke mana pun.
