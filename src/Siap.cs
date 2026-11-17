using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tepi
{
    // Test: TEPI_SIM fakes missing pieces
    static class Sim
    {
        static readonly string flags = "," + (Environment.GetEnvironmentVariable("TEPI_SIM") ?? "").ToLowerInvariant().Replace(" ", "") + ",";
        public static bool Has(string flag) { return flags.Contains("," + flag + ","); }
    }

    // What voice and AI need
    static class Needs
    {
        const string MicConsent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        // Device or desktop-apps switch only
        public static bool MicDenied()
        {
            if (Sim.Has("micdeny")) return true;
            return Denied(Registry.LocalMachine, MicConsent) || Denied(Registry.CurrentUser, MicConsent + @"\NonPackaged");
        }

        static bool Denied(RegistryKey root, string path)
        {
            try
            {
                using (var key = root.OpenSubKey(path))
                    return key != null && string.Equals(key.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // VC++ runtime DLLs whisper.cpp loads
        static readonly string[] VcDlls = { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll" };
        public const string VcUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        public static bool VcMissing()
        {
            if (Sim.Has("novc")) return true;
            string sys = Environment.SystemDirectory;
            foreach (var d in VcDlls) if (!File.Exists(Path.Combine(sys, d))) return true;
            return false;
        }

        // en-US speech, as Windows features
        public const string SpeechPacks = "Language.Basic~~~en-US~0.0.1.0", SpeechRecognizer = "Language.Speech~~~en-US~0.0.1.0";
        public const string OllamaUrl = "https://ollama.com/download", OllamaPull = "ollama pull llama3.2";
    }

    partial class Island
    {
        // Elevated install; outlives the settings window
        volatile bool speechInstalling;
        string speechInstallNote;

        void OpenSiap() { OpenPrefs("siap"); }

        void OpenMicPrivacy() { OpenSettings("ms-settings:privacy-microphone"); }

        // Usually Windows privacy says no
        void MicFailSay()
        {
            if (Needs.MicDenied()) DictSay(Lang.T("Windows menolak akses mikrofon"), Lang.T("Klik untuk membuka izinnya"), true, OpenMicPrivacy);
            else DictSay(Lang.T("Mikrofon tidak bisa dibuka"), Lang.T("Cek izin mikrofon di Windows"), true, OpenMicPrivacy);
        }

        // Errors the setup page can fix
        Action SpeechFix(string error)
        {
            if (string.IsNullOrEmpty(error)) return null;
            if (error == DictMissing() || Speech.IsSetupError(error)) return OpenSiap;
            var p = Ai.Stt;
            if (p != null && error == Lang.F("Key {0} ditolak", Ai.Label(p))) return OpenSiap;
            return null;
        }

        void InstallSpeechPack(Action done)
        {
            if (speechInstalling) return;
            speechInstalling = true;
            speechInstallNote = Lang.T("Memasang, bisa beberapa menit");
            string cmd = "Add-WindowsCapability -Online -Name " + Needs.SpeechPacks + " | Out-Null; Add-WindowsCapability -Online -Name " + Needs.SpeechRecognizer + " | Out-Null";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string note;
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + cmd + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        note = p.ExitCode != 0 ? Lang.T("Gagal memasang. Coba lewat Pengaturan Windows")
                            : Hear.Available ? null : Lang.T("Terpasang, tapi belum terbaca. Mulai ulang Windows lalu cek lagi");
                        Log.Write("pasang ucapan: keluar " + p.ExitCode);
                    }
                }
                // 1223: the UAC prompt was declined
                catch (Win32Exception ex) { note = ex.NativeErrorCode == 1223 ? Lang.T("Dibatalkan") : Lang.T("Gagal memasang. Coba lewat Pengaturan Windows"); Log.Write("pasang ucapan: " + ex.NativeErrorCode); }
                catch (Exception ex) { note = Lang.T("Gagal memasang. Coba lewat Pengaturan Windows"); Log.Write("pasang ucapan: " + ex.Message); }
                Post(delegate
                {
                    speechInstalling = false;
                    speechInstallNote = note;
                    if (note == null)
                    {
                        hearStale = true;
                        ApplyHear();
                        DictSay(Lang.T("Pengenal ucapan Inggris terpasang"), HearOn ? Lang.T("Dengarkan terus mulai jalan") : Lang.T("Dengarkan terus siap dinyalakan"), true);
                    }
                    if (done != null) done();
                });
            });
        }

        partial class PrefsWindow
        {
            // 0 Groq, 1 offline, 2 Ollama, 3 other
            int siapPath = -1;
            string siapOther = "openai";

            // Rebuild only when status changed
            string siapSig;

            string SiapSig()
            {
                Func<bool, string> b = v => v ? "1" : "0";
                return string.Join("|", new[]
                {
                    b(island.micPresent), b(island.micMuted), b(Needs.MicDenied()), b(Needs.VcMissing()), b(Hear.Available),
                    Ai.SttId, Ai.Text.Id, island.DictMissing() ?? "", b(Ai.Ready(Ai.Text)), b(island.speechInstalling),
                });
            }

            // Settings › Siapkan suara dan AI
            void SiapRows(Action<KitRow> add, Action<string> cap)
            {
                siapSig = SiapSig();
                cap(Lang.T("Yang dibutuhkan"));
                add(MicStatus());
                bool sttOk = SttStatus(add);
                bool textOk = TextStatus(add);
                add(RecognizerStatus());

                if (sttOk && textOk)
                {
                    cap(Lang.T("Semua siap"));
                    string key = settings.Get("dictate.key", "rctrl") == "ralt" ? Lang.T("Alt kanan") : Lang.T("Ctrl kanan");
                    bool dictOn = settings.GetBool("dictate", false);
                    var tryRow = Row(dictOn ? Lang.F("Coba tahan {0}, bicara, lalu lepas", key) : Lang.T("Nyalakan dikte untuk mulai"),
                        Lang.T("Layanan dan model bisa diganti di halaman AI"),
                        dictOn ? Button(Lang.T("Buka AI"), false, delegate { Go("ai"); }) : Button(Lang.T("Buka Dikte"), true, delegate { Go("dikte"); }));
                    tryRow.Glyph = '';
                    add(tryRow);
                    return;
                }

                cap(Lang.T("Siapkan dikte dan Suruh Tepi"));
                if (siapPath < 0) siapPath = Ai.SttId == Ai.Offline && Speech.LastSetupError == null ? 1 : 0;
                var paths = Row(Lang.T("Pilih cara"), PathNote(siapPath), Seg(new[] { "Groq", Lang.T("Offline"), "Ollama", Lang.T("Lainnya") }, siapPath, i => { siapPath = i; Rebuild(); }));
                paths.Glyph = '';
                add(paths);
                if (siapPath == 0) GroqSteps(add, sttOk, textOk);
                else if (siapPath == 1) OfflineSteps(add, textOk);
                else if (siapPath == 2) OllamaSteps(add, sttOk);
                else OtherSteps(add, sttOk, textOk);
            }

            static string PathNote(int path)
            {
                switch (path)
                {
                    case 0: return Lang.T("Satu key gratis untuk dikte dan teks");
                    case 1: return Lang.T("Dikte tanpa internet, teks butuh layanan lain");
                    case 2: return Lang.T("Teks dari model di laptop ini");
                    default: return Lang.T("OpenAI, Claude, Gemini, dengan key milikmu");
                }
            }

            KitRow MicStatus()
            {
                KitRow r;
                if (!island.micPresent) r = Row(Lang.T("Mikrofon"), Lang.T("Tidak ada mikrofon yang tersambung"), null);
                else if (Needs.MicDenied()) r = Row(Lang.T("Mikrofon"), Lang.T("Windows menolak akses mikrofon untuk aplikasi desktop"), Button(Lang.T("Buka izin"), true, island.OpenMicPrivacy));
                else if (island.micMuted) r = Row(Lang.T("Mikrofon"), Lang.T("Sedang dimatikan"), Button(Lang.T("Nyalakan"), true, delegate { island.ToggleMic(); Rebuild(); }));
                else r = Row(Lang.T("Mikrofon"), Lang.T("Siap"), null);
                r.Glyph = '';
                return r;
            }

            bool SttStatus(Action<KitRow> add)
            {
                KitRow r;
                bool ok = false;
                if (Ai.SttId == Ai.Offline)
                {
                    var m = Speech.Model(settings.Get("dictate.model", "seimbang"));
                    if (Needs.VcMissing()) r = Row(Lang.T("Mesin dikte"), Lang.T("Offline butuh Visual C++ runtime dari Microsoft"), Button(Lang.T("Unduh runtime"), true, delegate { OpenUrl(Needs.VcUrl); }));
                    else if (!Speech.LocalReady(m)) r = Row(Lang.T("Mesin dikte"), Lang.T("Model offline belum diunduh"), null);
                    else if (Speech.LastSetupError != null) r = Row(Lang.T("Mesin dikte"), Speech.LastSetupError + ". " + Lang.T("Pakai Groq gratis untuk dikte."), null);
                    else { r = Row(Lang.T("Mesin dikte"), Lang.F("Offline, model {0}", Lang.T(m.Name)), null); ok = true; }
                }
                else
                {
                    string missing = Ai.SttMissing();
                    var p = Ai.Stt;
                    r = Row(Lang.T("Mesin dikte"), missing ?? Lang.F("{0}, key tersimpan", p != null ? Ai.Label(p) : "?"), null);
                    ok = missing == null;
                }
                r.Glyph = '';
                add(r);
                return ok;
            }

            bool TextStatus(Action<KitRow> add)
            {
                var p = Ai.Text;
                KitRow r = null;
                bool ok = Ai.Ready(p);
                if (!ok) r = Row(Lang.T("Layanan teks"), p.Id == "custom" ? Lang.T("Alamat server belum diisi") : Lang.F("Key {0} belum diisi", Ai.Label(p)), null);
                else if (p.NoKey) r = Row(Lang.T("Layanan teks"), Lang.F("{0}, dicek saat dipakai", Ai.Label(p)), TestButton(p, () => r));
                else r = Row(Lang.T("Layanan teks"), Lang.F("{0}, key tersimpan", Ai.Label(p)), null);
                r.Glyph = '';
                add(r);
                return ok;
            }

            KitRow RecognizerStatus()
            {
                KitRow r;
                string title = Lang.T("Pengenal ucapan Inggris");
                if (Hear.Available) r = Row(title, Lang.T("Ada, dipakai Dengarkan terus"), null);
                else if (island.speechInstalling) r = Row(title, island.speechInstallNote ?? "", null);
                else
                {
                    string note = island.speechInstallNote ?? Lang.T("Belum terpasang. Hanya dipakai Dengarkan terus.");
                    r = Row(title, note, Pair(Button(Lang.T("Pengaturan Windows"), false, delegate { island.OpenSettings("ms-settings:regionlanguage"); }),
                        Button(Lang.T("Pasang"), true, delegate { island.InstallSpeechPack(Rebuild); Rebuild(); })));
                }
                r.Glyph = '';
                return r;
            }

            // Groq covers both: one key
            void GroqSteps(Action<KitRow> add, bool sttOk, bool textOk)
            {
                var groq = Ai.Find("groq");
                var open = Row(Lang.T("1. Buat key gratis di Groq"), Lang.T("Masuk dengan Google atau email, lalu tekan Create API Key"), Button(Lang.T("Buka console.groq.com"), false, delegate { OpenUrl(groq.KeyUrl); }));
                open.Glyph = '';
                add(open);
                if (Ai.Key(groq).Length > 0)
                {
                    var use = Row(Lang.T("2. Key Groq sudah tersimpan"), Lang.T("Pakai untuk yang belum siap"), Button(Lang.T("Pakai Groq"), true, delegate { UseFor(groq, sttOk, textOk); Rebuild(); }));
                    use.Glyph = '';
                    add(use);
                    return;
                }
                add(KeyRow(groq, Lang.T("2. Tempel key-nya di sini"), Lang.T("Diawali gsk_. Rekaman dikte dikirim ke Groq."), delegate { UseFor(groq, sttOk, textOk); }));
            }

            void OfflineSteps(Action<KitRow> add, bool textOk)
            {
                bool vc = Needs.VcMissing();
                var runtime = Row(Lang.T("1. Visual C++ runtime"), vc ? Lang.T("Dibutuhkan whisper.cpp. Pasang, lalu kembali ke halaman ini.") : Lang.T("Sudah terpasang"),
                    vc ? Button(Lang.T("Unduh runtime"), true, delegate { OpenUrl(Needs.VcUrl); }) : null);
                runtime.Glyph = vc ? '' : '';
                add(runtime);
                OfflineRows(add, false, delegate
                {
                    settings.Set("ai.stt", Ai.Offline);
                    settings.Set("dictate.engine", Ai.Offline);
                    settings.Save();
                });
                if (!textOk)
                {
                    var note = Row(Lang.T("Suruh Tepi dan ringkasan rapat"), Lang.T("Tetap butuh layanan teks: pilih Groq gratis atau Ollama di atas"), null);
                    note.Glyph = '';
                    add(note);
                }
            }

            void OllamaSteps(Action<KitRow> add, bool sttOk)
            {
                var ollama = Ai.Find("ollama");
                var install = Row(Lang.T("1. Pasang Ollama"), Lang.T("Gratis, menjalankan model AI di laptop ini"), Button(Lang.T("Buka ollama.com"), false, delegate { OpenUrl(Needs.OllamaUrl); }));
                install.Glyph = '';
                add(install);
                var pull = Row(Lang.T("2. Unduh satu model"), Lang.F("Buka Terminal, jalankan {0}. Sekitar 2 GB.", Needs.OllamaPull), Button(Lang.T("Salin perintah"), false, delegate
                {
                    try { Clipboard.SetText(Needs.OllamaPull); } catch (Exception ex) { Log.Write("salin ollama: " + ex.Message); }
                }));
                pull.Glyph = '';
                add(pull);
                KitRow link = null;
                KitButton go = null;
                go = Button(Lang.T("Tes dan pakai"), true, delegate
                {
                    go.Enabled = false;
                    Say(link, Lang.T("Memeriksa…"));
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err, model = "";
                        try { err = Ai.Check(ollama, ""); if (err == null) model = Ai.Model(ollama, ""); }
                        catch (Exception ex) { err = Lang.F("{0} tidak jalan di laptop ini", Ai.Label(ollama)); Log.Write("ollama tes: " + ex.GetType().Name); }
                        if (err == null && model.Length == 0) err = Lang.T("Belum ada model. Jalankan perintah di langkah 2.");
                        island.Post(delegate
                        {
                            if (err == null) { settings.Set("ai.text", "ollama"); settings.Save(); Rebuild(); return; }
                            if (go.IsDisposed) return;
                            go.Enabled = true;
                            Say(link, err);
                        });
                    });
                });
                link = Row(Lang.T("3. Sambungkan"), Lang.T("Buka aplikasi Ollama dulu, lalu tes"), go);
                link.Glyph = '';
                add(link);
                if (!sttOk)
                {
                    var note = Row(Lang.T("Dikte"), Lang.T("Ollama tidak mengubah suara jadi teks: pilih Groq gratis atau Offline untuk dikte"), null);
                    note.Glyph = '';
                    add(note);
                }
            }

            void OtherSteps(Action<KitRow> add, bool sttOk, bool textOk)
            {
                var list = new List<AiProvider>();
                foreach (var p in Ai.Providers) if (!p.NoKey && p.Id != "groq" && p.Id != "custom") list.Add(p);
                var chosen = Ai.Find(siapOther);
                if (chosen == null || !list.Contains(chosen)) chosen = list[0];
                var pick = Row(Lang.T("1. Pilih layanan"), chosen.Stt ? Lang.T("Bisa untuk dikte dan teks") : Lang.T("Hanya untuk teks, dikte tetap butuh Groq atau Offline"),
                    Popup(list.ConvertAll(Ai.Label).ToArray(), list.IndexOf(chosen), i => { siapOther = list[i].Id; Rebuild(); }));
                pick.Glyph = '';
                add(pick);
                var open = Row(Lang.F("2. Buat key {0}", Ai.Label(chosen)), new Uri(chosen.KeyUrl).Host, Button(Lang.T("Buka"), false, delegate { OpenUrl(chosen.KeyUrl); }));
                open.Glyph = '';
                add(open);
                if (Ai.Key(chosen).Length > 0)
                {
                    var use = Row(Lang.F("3. Key {0} sudah tersimpan", Ai.Label(chosen)), Lang.T("Pakai untuk yang belum siap"), Button(Lang.F("Pakai {0}", Ai.Label(chosen)), true, delegate { UseFor(chosen, sttOk, textOk); Rebuild(); }));
                    use.Glyph = '';
                    add(use);
                    return;
                }
                add(KeyRow(chosen, Lang.T("3. Tempel key-nya di sini"), Lang.T("Tepi mengeceknya dulu sebelum menyimpan"), delegate { UseFor(chosen, sttOk, textOk); }));
            }

            // Switch only what is not working yet
            void UseFor(AiProvider p, bool sttOk, bool textOk)
            {
                if (!textOk) settings.Set("ai.text", p.Id);
                if (!sttOk && p.Stt)
                {
                    settings.Set("ai.stt", p.Id);
                    settings.Set("dictate.engine", "groq");
                }
                settings.Save();
            }
        }
    }
}
