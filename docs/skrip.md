# Skrip

[← Kembali ke README](../README.md)

```
Tepi.exe --notify "Backup selesai" "12 berkas"
Tepi.exe --notify "Build" --progress 40 --id build
Tepi.exe --run "npm run build"
```

Yang pertama memunculkan toast. Yang kedua menampilkan bilah progres dengan nama `build`. Kirim angka baru dengan `--id` yang sama untuk memperbaruinya, dan `100` untuk menutupnya. Bilah yang tidak diperbarui selama 2 menit hilang sendiri.

`--run` menjalankan perintah di terminal itu sendiri lewat `cmd /c`, outputnya tetap di terminal. Selama berjalan, island menampilkan perintah dan lamanya; setelah selesai, island bilang berapa lama atau kode galatnya. Island hanya menerima kabar mulai dan selesai, tidak pernah menjalankan apa pun yang dikirim ke server hook. Di PowerShell dan cmd, Windows tidak menunggu program jendela seperti Tepi, jadi pakai `start /wait Tepi.exe --run "..."` di cmd atau `Start-Process -Wait -NoNewWindow Tepi.exe '--run','...'` di PowerShell kalau perintah berikutnya harus menunggu.

Perintah ini memakai server hook dan token yang sama dengan agent, jadi hanya program di komputer ini yang bisa memakainya.
