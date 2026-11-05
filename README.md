<p align="center">
  <img src="https://odlaver.github.io/tepi/assets/img/readme-banner.jpg" alt="Tepi, dynamic island untuk Windows dan Android">
</p>

<p align="center">
  <a href="https://github.com/odlaver/tepi/releases/latest/download/Tepi.exe"><b>Unduh untuk Windows</b></a>
  ·
  <a href="https://github.com/odlaver/tepi-android/releases/latest"><b>Unduh untuk Android</b></a>
  ·
  <a href="https://odlaver.github.io/tepi/">Situs</a>
  ·
  <a href="https://github.com/odlaver/tepi/releases">Semua rilis</a>
</p>

# Tepi

Tepi duduk tenang di tepi atas layar, lalu membesar saat ada yang terjadi: lagu dan liriknya, agent coding yang minta izin, tugas yang mendekati tenggat, pesan masuk, timer, sampai waktu sholat. Tampilannya Liquid Glass dengan gerak pegas. Exe-nya 905 KB, native C# tanpa browser engine, dalam bahasa Indonesia dan Inggris.

Sekarang juga ada [Tepi Android](https://github.com/odlaver/tepi-android): island di sekitar kamera depan HP, yang nyambung ke laptop cukup dengan memindai QR.

## Baru di 2.6

- **Dikte**: tahan Ctrl kanan, bicara, lalu lepas. Teksnya ditempel di aplikasi apa pun, lewat Groq atau offline.
- **Suruh Tepi**: "ingetin minum 30 menit lagi", "timer 5:30", atau "5 km ke mil", tanpa internet.
- **Transkrip rapat** tanpa bot: suaramu dan lawan bicara dipisah, lengkap dengan ringkasan.
- **Tab Bicara**, **garis cahaya** untuk progres, dan **Samarkan** untuk menutupi data di screenshot.
- **Salin teks dari layar**, **tur** untuk pengguna baru, dan **Apa yang baru** setiap pembaruan.
- Menyalin screenshot 4K tidak lagi membuat island tersendat.

Catatan lengkapnya ada di [halaman rilis](https://github.com/odlaver/tepi/releases/latest).

## Isinya

| | |
|---|---|
| **Musik dan lirik** | Spotify, YouTube, Apple Music, dan pemutar lain. Lirik tersinkron menyapu kata demi kata, visualizer membaca suara yang benar-benar diputar. |
| **Agent coding** | Claude Code, Codex, Gemini CLI, dan Antigravity. Status sesi, rencana kerja, kuota Claude, dan izin yang bisa dijawab dari island. [Selengkapnya](docs/agen.md) |
| **Kuliah** | Tugas dan jadwal dari [Kulia](https://kulia.live), Notion, Todoist, dan kalender iCal. [Selengkapnya](docs/integrasi.md) |
| **Pesan** | WhatsApp, Telegram, Discord, Teams, Slack, dan email dari pusat notifikasi Windows. |
| **Dikte dan rapat** | Tahan Ctrl kanan, bicara, lepas: teksnya ditempel di aplikasi mana pun, dalam bahasa Indonesia yang bercampur istilah Inggris. Kamus istilah, snippet, lewat Groq atau offline. Rekam panggilan tanpa bot jadi transkrip Kamu dan Lawan bicara, plus ringkasan dan to-do. Suruh Tepi memasang pengingat, timer, dan catatan dari kalimat biasa. [Selengkapnya](docs/fitur.md) |
| **HP** | Kode OTP, notifikasi, baterai, dan kirim berkas dua arah dengan Tepi Android. [Selengkapnya](docs/hp.md) |
| **Rak dan Ubah format** | Seret berkas ke island untuk disimpan sementara, atau ubah ke JPG, PDF, MP3, DOCX, dan lainnya. |
| **Kontrol** | Volume per aplikasi, kecerahan, Wi-Fi, Bluetooth, mikrofon, mode gelap, laptop tetap terjaga, dan timer. |
| **Pengingat** | Sholat (dihitung di laptop, tanpa internet), cuaca dan hujan, minum, istirahat mata, peregangan. |
| **Mini** | Sliver kecil yang tembus klik, dengan dua mata yang berkedip dan berekspresi. |
| **Lainnya** | [Teleprompter](docs/teleprompter.md), cermin kamera, catatan kilat, preview screenshot, mode rapat, flashdisk, [skrip](docs/skrip.md), dan navigasi keyboard penuh. |

Daftar fitur lengkap dan cara pakainya ada di [Fitur dan cara pakai](docs/fitur.md).

## Pasang

1. Unduh [**Tepi.exe**](https://github.com/odlaver/tepi/releases/latest/download/Tepi.exe), lalu jalankan.
2. Kalau Windows SmartScreen memperingatkan, pilih **More info** lalu **Run anyway**. Exe belum ditandatangani; [cara memastikan exe-nya asli](docs/privasi-keamanan.md#tanda-tangan-kode).
3. Ikon Tepi muncul di system tray. Arahkan kursor ke island di tepi atas layar untuk membuka panel.

Syarat: Windows 10 versi 2004 ke atas atau Windows 11, 64-bit. Tepi memberi tahu sendiri saat ada versi baru: klik kanan ikon Tepi, lalu **Perbarui**.

Untuk HP: pasang APK dari [tepi-android](https://github.com/odlaver/tepi-android/releases/latest) (Android 10 ke atas), lalu ikuti [langkah menyambungkan](docs/hp.md#menyambungkan).

## Dokumentasi

| Topik | Isi |
|---|---|
| [Fitur dan cara pakai](docs/fitur.md) | Semua fitur, gestur, menu, dan navigasi keyboard |
| [Pengaturan](docs/pengaturan.md) | Isi tiap halaman Pengaturan dan semua kunci `settings.ini` |
| [Tepi Android dan HP](docs/hp.md) | Apa saja yang lewat sambungan HP, dan cara memasangkan |
| [Agent coding](docs/agen.md) | Menghubungkan agent, menjawab izin, chat ke sesi, kuota Claude |
| [Integrasi](docs/integrasi.md) | Kulia, Notion, Todoist, kalender iCal, dan notifikasi pesan |
| [Paket tema](docs/paket-tema.md) | Warna, lambang, maskot, sapaan, dan suara buatan sendiri |
| [Teleprompter](docs/teleprompter.md) | Naskah di tepi atas layar, dekat kamera |
| [Skrip](docs/skrip.md) | `Tepi.exe --notify` dan `--run` dari terminal |
| [Performa](docs/performa.md) | Hasil ukur CPU dan memori |
| [Privasi dan keamanan](docs/privasi-keamanan.md) | Layanan luar yang dihubungi, model keamanan, tanda tangan kode |

Paket tema juga bisa dibuat di [pembuat paket](https://odlaver.github.io/tepi/pack/) tanpa menyentuh berkas.

## Build sendiri

Tidak perlu memasang SDK. Compiler C# untuk .NET Framework 4.8 sudah ada di Windows:

```
build.cmd
```

Hasilnya `Tepi.exe` di folder yang sama. Variabel lingkungan untuk pengembangan ada di [Pengaturan](docs/pengaturan.md#pengaturan-lanjutan).

## Keamanan

Menemukan celah? Laporkan secara privat lewat tab **Security › Report a vulnerability** di repo ini, jangan lewat issue publik.

## Lisensi

[MIT](LICENSE), dibuat oleh R. Revaldo Vabiansyah ([@odlaver](https://github.com/odlaver)).
