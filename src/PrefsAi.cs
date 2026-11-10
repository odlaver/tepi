using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Settings › AI: text and speech services
    partial class Island
    {
        partial class PrefsWindow
        {
            void AiRows(Action<KitRow> add, Action<string> cap)
            {
                var text = Ai.Text;
                var all = new List<AiProvider>(Ai.Providers);
                var names = all.ConvertAll(Ai.Label).ToArray();
                cap(Lang.T("Teks"));
                add(Row(Lang.T("Layanan teks"), Lang.T("Ringkasan rapat, Suruh Tepi, dan perbaiki teks terpilih"),
                    Popup(names, all.IndexOf(text), i => { settings.Set("ai.text", all[i].Id); settings.Save(); Rebuild(); })));
                ServiceRows(add, text, true);

                cap(Lang.T("Suara ke teks"));
                var stt = new List<string>();
                var sttIds = new List<string>();
                foreach (var p in Ai.Providers) if (p.Stt) { stt.Add(Ai.Label(p)); sttIds.Add(p.Id); }
                stt.Add(Lang.T("Offline"));
                sttIds.Add(Ai.Offline);
                string sttId = Ai.SttId;
                var sp = Ai.Stt;
                string sttNote = sttId == Ai.Offline ? Lang.T("Offline: rekaman tidak keluar dari laptop ini")
                    : Lang.F("{0}: rekaman dikirim ke {0}, tidak disimpan Tepi", sp != null ? Ai.Label(sp) : "?");
                add(Row(Lang.T("Mesin suara"), sttNote, Popup(stt.ToArray(), Math.Max(0, sttIds.IndexOf(sttId)), i =>
                {
                    settings.Set("ai.stt", sttIds[i]);
                    // Older builds read this key
                    settings.Set("dictate.engine", sttIds[i] == Ai.Offline ? Ai.Offline : "groq");
                    settings.Save();
                    Rebuild();
                })));
                if (sp != null && sttId != Ai.Offline && sp != text) ServiceRows(add, sp, false);
                if (sp != null && sttId != Ai.Offline && sp.Id == "custom")
                    add(ModelRow(sp, "ai.custom.stt", Lang.T("Model suara"), sp.SttModel));
                OfflineRows(add, sttId != Ai.Offline);

                cap(Lang.T("Privasi"));
                add(Row(Lang.T("Yang dikirim"), Lang.T("Hanya teks dan rekaman yang kamu minta diproses, langsung ke layanan pilihanmu"), null));
            }

            // Address, key, model for one service
            void ServiceRows(Action<KitRow> add, AiProvider p, bool withModel)
            {
                if (p.Id == "custom") add(UrlRow());
                if (p.NoKey)
                {
                    KitRow local = null;
                    local = Row(Lang.F("Server {0}", Ai.Label(p)), Lang.F("Di laptop ini, tanpa internet dan key · {0}", new Uri(p.Base).Authority), TestButton(p, () => local));
                    local.Glyph = '\uE774';
                    add(local);
                }
                else if (Ai.Key(p).Length > 0)
                {
                    KitRow saved = null;
                    saved = Row(Lang.F("Key {0}", Ai.Label(p)), Lang.T("Tersimpan, terenkripsi untuk akun Windows ini"),
                        Pair(TestButton(p, () => saved), Button(Lang.T("Lepas"), false, delegate { Ai.DropKey(p); Rebuild(); })));
                    saved.Glyph = '\uE8D7';
                    add(saved);
                }
                else KeyRows(add, p);
                if (withModel) add(ModelRow(p, "ai." + p.Id + ".model", Lang.T("Model"), Ai.DefaultModel(p)));
            }

            KitButton TestButton(AiProvider p, Func<KitRow> row)
            {
                KitButton b = null;
                b = Button(Lang.T("Tes"), false, delegate
                {
                    b.Enabled = false;
                    Say(row(), Lang.T("Memeriksa…"));
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err;
                        try { err = Ai.Check(p, Ai.Key(p)); }
                        catch (Exception ex) { err = Lang.F("Tidak tersambung ke {0}", Ai.Label(p)); Log.Write("ai tes: " + ex.GetType().Name); }
                        island.Post(delegate
                        {
                            if (b.IsDisposed) return;
                            b.Enabled = true;
                            Say(row(), err ?? Lang.F("Tersambung, model {0}", Ai.Model(p, Ai.Key(p))));
                        });
                    });
                });
                return b;
            }

            void KeyRows(Action<KitRow> add, AiProvider p)
            {
                var field = new KitField(Ui, true, p.KeyHint ?? "…", 200);
                KitRow row = null;
                KitButton go = null;
                go = Button(Lang.T("Simpan"), true, delegate
                {
                    string key = field.Box.Text.Trim();
                    // Custom servers may run keyless
                    if (key.Length == 0 && p.Id != "custom") { Say(row, Lang.T("Key kosong")); field.Box.Focus(); return; }
                    go.Enabled = field.Enabled = false;
                    Say(row, Lang.T("Memeriksa key…"));
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err;
                        try { err = Ai.Check(p, key); }
                        catch (Exception ex) { err = Lang.F("Tidak tersambung ke {0}", Ai.Label(p)); Log.Write("ai cek: " + ex.GetType().Name); }
                        island.Post(delegate
                        {
                            if (err == null) { if (key.Length > 0) Ai.SaveKey(p, key); else Ai.DropKey(p); Rebuild(); return; }
                            if (go.IsDisposed) return;
                            go.Enabled = field.Enabled = true;
                            Say(row, err);
                        });
                    });
                });
                field.Submit = delegate { go.PerformClick(); };
                row = Row(Lang.F("Key {0}", Ai.Label(p)), p.Id == "groq" ? Lang.T("Gratis dari console.groq.com") : Lang.T("Disimpan terenkripsi di laptop ini"), Pair(field, go));
                row.Glyph = '\uE8D7';
                add(row);
                if (!string.IsNullOrEmpty(p.KeyUrl))
                {
                    var link = Row(Lang.F("Buat key {0}", Ai.Label(p)), new Uri(p.KeyUrl).Host, Button(Lang.T("Buka"), false, delegate { OpenUrl(p.KeyUrl); }));
                    link.Glyph = '\uE774';
                    add(link);
                }
            }

            KitRow UrlRow()
            {
                var f = new KitField(Ui, false, "https://…/v1", 300);
                f.Box.Text = settings.Get("ai.custom.url", "");
                var row = Row(Lang.T("Alamat server"), Lang.T("Server yang mengikuti format OpenAI, Enter menyimpan"), f);
                row.Glyph = '\uE774';
                Action save = delegate
                {
                    string v = f.Box.Text.Trim();
                    // Plain http only for this laptop
                    if (v.Length > 0 && !v.StartsWith("https://") && !v.StartsWith("http://localhost") && !v.StartsWith("http://127.0.0.1")) { Say(row, Lang.T("Pakai https, atau http untuk localhost")); return; }
                    if (v == settings.Get("ai.custom.url", "")) return;
                    settings.Set("ai.custom.url", v);
                    settings.Save();
                    Ai.Forget(Ai.Find("custom"));
                    Say(row, Lang.T("Tersimpan"));
                };
                f.Submit = save;
                f.Box.LostFocus += delegate { save(); };
                return row;
            }

            KitRow ModelRow(AiProvider p, string key, string title, string cue)
            {
                var f = new KitField(Ui, false, cue, 120);
                f.Box.Text = settings.Get(key, "");
                var row = Row(title, Lang.T("Kosong: pilihan Tepi"), f);
                row.Glyph = '\uE8F1';
                Action save = delegate
                {
                    string v = f.Box.Text.Trim();
                    if (v == settings.Get(key, "")) return;
                    settings.Set(key, v);
                    settings.Save();
                    Say(row, Lang.T("Tersimpan"));
                };
                f.Submit = save;
                f.Box.LostFocus += delegate { save(); };
                return row;
            }
        }
    }
}
