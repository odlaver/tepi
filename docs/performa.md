# Performa

[← Kembali ke README](../README.md)

Ringan. Island tidak menggambar apa pun selama tidak ada yang bergerak; bagian kecil yang hidup (visualizer, lirik, spinner, timer) digambar ulang sendiri, tepat pada refresh layar. Diukur di laptop Intel Core i3-1115G4 pada 1 November 2026, bergantian dengan versi 2.1 memakai lagu dan lirik tiruan yang sama, 45 detik per putaran, median 3 putaran:

| Kondisi | 2.1 | 2.4 |
|---|---|---|
| Musik diputar, lirik di island | 3,8% CPU | 2,6% CPU |
| Panel terbuka selagi musik diputar | 0,7% CPU | 0,6% CPU |
| Memori saat panel terbuka | sekitar 21 MB | sekitar 22 MB |

Persen CPU dihitung terhadap satu core; Task Manager membaginya lagi dengan jumlah thread, jadi angka di sana lebih kecil. Sejak 2.2, bagian yang hidup digambar tepat pada refresh layar, tanpa frame kosong di antaranya, jadi musik sekitar sepertiga lebih ringan. Di 2.5, visualizer turun ke 24 kali per detik dan penangkap suaranya hanya bangun sekali per frame. Diukur 2 November 2026 dengan menghitung siklus CPU, 2.5 dan 2.4 berjalan berdampingan: musik dengan visualizer 7% lebih ringan, island diam dengan musik 10% lebih ringan, panel terbuka sama, memori selisih 1 MB. Sambungan HP mati sampai dinyalakan; selama menyala, Tepi hanya menunggu pesan dari HP.
