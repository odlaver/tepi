# Teleprompter

[← Kembali ke README](../README.md)

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
