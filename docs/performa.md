# Performa

[← Kembali ke README](../README.md)

Ringan. Island tidak menggambar apa pun selama tidak ada yang bergerak; bagian kecil yang hidup (visualizer, lirik, spinner, timer) digambar ulang sendiri, tepat pada refresh layar. Diukur di laptop Intel Core i3-1115G4 pada 1 November 2026, bergantian dengan versi 2.1 memakai lagu dan lirik tiruan yang sama, 45 detik per putaran, median 3 putaran:

| Kondisi | 2.1 | 2.4 |
|---|---|---|
| Musik diputar, lirik di island | 3,8% CPU | 2,6% CPU |
| Panel terbuka selagi musik diputar | 0,7% CPU | 0,6% CPU |
| Memori saat panel terbuka | sekitar 21 MB | sekitar 22 MB |

Persen CPU dihitung terhadap satu core; Task Manager membaginya lagi dengan jumlah thread, jadi angka di sana lebih kecil. Sejak 2.2, bagian yang hidup digambar tepat pada refresh layar, tanpa frame kosong di antaranya, jadi musik sekitar sepertiga lebih ringan. Di 2.5, visualizer turun ke 24 kali per detik dan penangkap suaranya hanya bangun sekali per frame. Diukur 2 November 2026 dengan menghitung siklus CPU, 2.5 dan 2.4 berjalan berdampingan: musik dengan visualizer 7% lebih ringan, island diam dengan musik 10% lebih ringan, panel terbuka sama, memori selisih 1 MB. Sambungan HP mati sampai dinyalakan; selama menyala, Tepi hanya menunggu pesan dari HP.

## 2.6.3

Diukur 27 November 2026 di laptop yang sama, saat laptop juga dipakai bekerja. Tepi uji berjalan terpisah dari Tepi yang terpasang, dengan pengaturan sendiri, lagu dan wallpaper tiruan, dan semua jendelanya di luar layar. Tiap kondisi dibiarkan tenang 4 detik lalu diukur 8 detik.

2.6.2.1 dan 2.6.3 dijalankan bergantian dengan alat ukur yang sama, dua kali masing-masing (median 6 putaran; panel Musik 15 putaran):

| Kondisi | 2.6.2.1 | 2.6.3 |
|---|---|---|
| Island diam | 0,7% CPU | 0,7% CPU |
| Musik, lirik di island | 2,4% CPU | 2,4% CPU |
| Musik, panel Musik terbuka | 3,5% CPU | 3,9% CPU |
| Dock tanpa widget | 0,7% CPU | 0,4% CPU |

Selisih di panel Musik masih di dalam derau: rentang putarannya 2,5–6,6% untuk 2.6.2.1 dan 2,7–5,6% untuk 2.6.3.

Fitur baru, median 9 putaran dalam pengukuran terpisah. Baris pertama diukur ulang di putaran yang sama sebagai pembanding, jadi angkanya berbeda dari tabel di atas:

| Kondisi | CPU |
|---|---|
| Musik, lirik di island | 3,3% |
| Musik, jendela lirik terbuka, latar sampul album | 1,6% |
| Musik, jendela lirik terbuka, latar kaca | 1,8% |
| Dock tanpa widget | 0,8% |
| Dock dengan widget disk dan internet | 1,2% |
| Bar atas dan Dock | 1,2% |
| Bar atas dan Dock, musik diputar | 4,1% |

Selama jendela lirik terbuka, island berhenti menyapu lirik, jadi CPU saat musik justru turun. Disk dibaca sekali semenit (0,07 ms) dan kecepatan internet tiap 2 detik (0,14 ms). Kaca bar atas dan jendela lirik dipotong dari salinan wallpaper berukuran seperempat yang hanya dibuat ulang saat wallpaper berganti. Mode **Sembunyikan bar atas otomatis** memeriksa posisi kursor 10 kali per detik, sekitar 5 µs sekali periksa, dan hanya selama mode itu menyala.

| Memori (private working set) | MB |
|---|---|
| Island diam | 8 |
| Jendela lirik terbuka | 18–22 |
| Bar atas, Dock, dan widget | 11–28 |
| Tepi terpasang setelah 12 menit dipakai (bar atas, Dock, widget sistem) | 23 |

Exe 2.6.3 berukuran 1.976 KB, naik 93 KB dari 2.6.2.1.

## 2.6.2

Diukur 17 November 2026 di laptop yang sama, saat laptop juga menjalankan pekerjaan lain (terminal dan beberapa proses build), jadi angkanya lebih berisik daripada pengukuran sebelumnya. Tiap kondisi dibiarkan tenang 4 detik lalu diukur 8 detik; angka di bawah adalah median dari 3 putaran.

| Kondisi | CPU |
|---|---|
| Island diam | 0,6% (putaran: 0,8 · 0,2 · 0,6) |
| Timer fokus di island | 2,7% (1,4 · 2,9 · 2,7) |
| Panel terbuka, rata-rata 11 tab | 1,2% (1,2 · 0,7 · 1,5) |

| Memori (private working set) | MB |
|---|---|
| Diam, setelah panel ditutup | 6 |
| Diam, sesaat setelah start | 18 |
| Panel terbuka | 18–27 |

Membuka panel dan pindah tab rata-rata 3–6 ms per frame (`TEPI_FPS=1`). Saat pindah tab beruntun, frame terlama 14 ms; gambar pertama sebuah halaman bisa sampai 39 ms.

Di 2.6.2 island tidak lagi bereaksi pada gerakan kursor dan kursor teks di seluruh Windows: hanya perpindahan jendela utuh yang membangunkan kaca dan pemeriksaan Dock.

## Semua tampilan, keadaan tenang

Diukur 5 November 2026 dengan data asli (Kulia, lirik, naskah prompter): tiap tampilan diberi 4 detik untuk tenang, lalu CPU dihitung selama 8 detik.

| Kondisi | CPU |
|---|---|
| Island diam, mata mengikuti kursor | 0,2–1% |
| Mini | 0,2% |
| Panel terbuka, tab apa pun | 0–0,8% |
| Timer, stopwatch, atau fokus di island | 1,2–1,8% |
| Dikte sedang mendengar | 2,3% |

Membuka Pengaturan sekitar 120 ms, berpindah halaman 18–66 ms. Menyalin screenshot 4K kini hanya menahan island kurang dari 1 ms (sebelumnya 165 ms): thumbnail dan berkasnya dibuat di belakang.

Untuk mengukur sendiri, jalankan dengan `TEPI_FPS=1`: log mencatat fps, frame lambat beserta bagiannya, dan pembagian CPU antara thread UI dan kaca. Jangan pasang `TEPI_DUMP` saat mengukur, karena setiap frame yang bergerak ikut disimpan sebagai PNG dan angkanya jadi berlipat.
