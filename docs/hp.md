# Tepi Android dan sambungan HP

[← Kembali ke README](../README.md)

Tepi Android adalah dynamic island di sekitar kamera depan, untuk HP yang tidak punya island bawaan: lagu dan lirik, pesan dengan balas cepat, panggilan, unduhan, navigasi Maps, timer, waktu sholat, Agenda dari Kulia, Todoist, Notion, atau iCal, dan Mode bank. Kode sumber dan APK-nya ada di [odlaver/tepi-android](https://github.com/odlaver/tepi-android).

Setelah tersambung ke Tepi di laptop:

- Kode OTP dari SMS, email, aplikasi bank, dan aplikasi lain muncul di island dalam bentuk tersamar. **Salin** menyalin kode utuh, lalu kode itu dihapus dari clipboard semenit kemudian.
- Notifikasi pesan dari HP masuk ke Pesan di laptop.
- Berkas dari HP (menu Bagikan › **Kirim ke laptop**, tombol **Pilih berkas** di Integrasi › Laptop, atau aksi **Kirim ke laptop** di Rak) masuk ke `Downloads\Tepi` dan Rak. Berkas di laptop dikirim ke HP lewat **Kirim berkas** di tab HP atau klik kanan berkas di Rak.
- Permintaan izin agen bisa dijawab dari HP.
- Tab HP menampilkan nama HP, status, dan baterainya.

## Menyambungkan

1. Di HP: pasang APK Tepi Android dari [tepi-android/releases](https://github.com/odlaver/tepi-android/releases).
2. Di laptop: **Pengaturan > HP**, nyalakan **Hubungkan HP**, lalu klik **Buat kode**.
3. Di HP: Tepi > **Integrasi** > **Laptop** > **Pindai QR**, arahkan kamera ke kode QR di laptop, lalu ketuk **Sambungkan**. Bisa juga dipindai dari aplikasi kamera HP. Tanpa kamera, ketik alamat dan kode 12 karakter yang muncul di laptop.
4. Windows Firewall akan bertanya sekali; izinkan untuk jaringan privat.

Kode berlaku 5 menit dan hangus setelah lima kali salah. Tab HP muncul sendiri setelah sambungan dinyalakan. Selama kode QR tampil, jendela Pengaturan tidak ikut terekam saat screen share.

## Keamanan

Sambungan HP mati sampai dinyalakan, hanya berjalan di jaringan lokal tanpa server perantara, dan Tepi tidak pernah menjalankan apa pun yang datang dari HP. Rinciannya ada di [Privasi dan keamanan](privasi-keamanan.md).
