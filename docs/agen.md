# Agent coding

[← Kembali ke README](../README.md)

Tepi memantau Claude Code, Codex, Gemini CLI, dan Antigravity: status sesi, rencana kerja, ringkasan saat selesai, dan kuota Claude. Permintaan izin dari Claude Code dan Codex bisa dijawab langsung dari island.

## Menghubungkan agent

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

- Claude Code mengirim event ke `http://127.0.0.1:<port>/hook`. Instalasi baru memakai port acak; instalasi lama tetap di 47821 sampai kamu menekan **Ganti port acak** di Pengaturan › Agen. Agent lain menjalankan `Tepi.exe --relay <agent>`, yang membaca port dari `settings.ini` dan meneruskan event ke alamat yang sama. Semua request memakai token acak yang dibuat saat Tepi pertama kali jalan. Request tanpa token yang benar, atau yang datang dari browser, ditolak.
- Tab **Agen** menampilkan durasi setiap sesi, jumlah berkas yang diubah, dan jumlah perintah yang dijalankan.
- Klik sesi di tab **Agen**, atau toast **Selesai**, untuk membuka jendela terminal sesi itu. Kalau beberapa sesi ada di tab berbeda dalam satu jendela Windows Terminal, yang dibuka jendelanya.
- Saat agent minta izin, island berbalik putih dan menahan permintaan itu sampai kamu memilih. Kalau tidak dijawab dalam 110 detik, atau kamu memilih **Jawab di terminal**, keputusan kembali ke prompt biasa di terminal.
- Kalau Tepi sedang tidak jalan, agent tetap bekerja seperti biasa tanpa pesan galat.
- Hook menyimpan lokasi `Tepi.exe`. Kalau exe dipindah, lepas lalu hubungkan lagi dari menu.
- Hook `Stop` dan `SessionEnd` Claude Code memakai `curl` lewat Git Bash, yang memang dibutuhkan Claude Code di Windows.

## Kuota Claude

Tepi membaca kuota Claude Code dari status line resminya, tanpa menyentuh token login. Saat Claude Code dihubungkan, Tepi memasang `"<Tepi.exe>" --statusline` sebagai status line di setiap profil. Kalau sudah ada status line lain, status line itu tetap jalan dan keluarannya tetap ditampilkan. Saat dilepas, nilai lamanya dikembalikan persis.

- Tab **Agen** menampilkan pemakaian jendela 5 jam dan 7 hari beserta jam resetnya. Ringkasan ikut menampilkannya saat pemakaian 70% ke atas.
- Peringatan muncul sekali per jendela di 80% dan 95%. Yang 95% tetap muncul walau Senyap atau Mini.
- Data baru ada untuk langganan Pro atau Max, setelah balasan pertama di sesi baru.
- Kalau Claude Code sudah dihubungkan sebelum versi 2.0, nyalakan **Pengaturan > Agen > Kuota Claude**.

## Menjawab izin

- **Izinkan** dan **Tolak** menjawab permintaan itu saja.
- **Selalu** mengizinkan permintaan itu, lalu mengizinkan otomatis permintaan yang persis sama di sesi yang sama. "Persis sama" dicocokkan lewat sidik SHA-256 atas seluruh isi permintaan, termasuk opsi seperti jalan di latar atau tanpa sandbox. Ingatan ini hilang saat sesi selesai.
- **Terminal** mengembalikan keputusan ke prompt di terminal dan membawa jendela terminalnya ke depan.
- Diff atau perintah yang lebih dari 10 baris bisa digulir di dalam kartu. **Izinkan** dan **Selalu** baru terbuka setelah digulir sampai akhir. Yang lebih dari 300 baris, atau teks perintah yang terlalu panjang untuk island, tetap dijawab di terminal.
- Opsi perintah yang mengubah cara jalannya (misalnya jalan di latar, batas waktu, tanpa sandbox) tampil di samping nama alat.
- Karakter tak terlihat dan pembalik arah teks (bidi, zero-width) tampil sebagai kode seperti `<U+202E>`, supaya isi perintah tidak bisa disamarkan.

## Chat ke agent

Di tab **Agen**, arahkan kursor ke sebuah sesi lalu klik **Chat**. Jendelanya menampilkan status sesi, rencana kerja, dan kuota Claude. Tulis prompt, tekan Enter (Shift+Enter untuk baris baru). Prompt yang pernah dikirim ada di **Riwayat** per proyek, dan prompt yang sering dipakai bisa disimpan sebagai **Templat** di `%APPDATA%\Tepi\templat-chat.txt`. Tepi membawa terminal sesi itu ke depan, lalu mengetik prompt dan menekan Enter. Kalau terminalnya tidak bisa dibawa ke depan, tidak ada yang diketik.

Kalau beberapa sesi berjalan di tab berbeda dalam satu jendela Windows Terminal, prompt masuk ke tab yang sedang aktif. Kotak chat mengingatkan hal ini. Tekan panah atas dan bawah di kotak chat untuk memanggil prompt yang pernah dikirim (hanya di memori, hilang saat Tepi ditutup).
