# Paket tema

[← Kembali ke README](../README.md)

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
