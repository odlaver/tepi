using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Tepi
{
    // Controls side by side in one row
    class KitPair : Panel
    {
        readonly float ui;

        public KitPair(float ui, params Control[] parts)
        {
            this.ui = ui;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            int x = 0, h = 0;
            foreach (var c in parts)
            {
                c.Left = x;
                x += c.Width + (int)(8 * ui);
                h = Math.Max(h, c.Height);
                Controls.Add(c);
            }
            foreach (var c in parts) c.Top = (h - c.Height) / 2;
            Size = new Size(Math.Max(0, x - (int)(8 * ui)), h);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(KitInk.Under(this)); }
    }

    partial class Island
    {
        partial class PrefsWindow
        {
            // -2 closed, -1 new, else editing index
            int ibadahEdit = -2;
            IbadahItem ibadahDraft;

            static readonly int[] DayMasks = { IbadahItem.Daily, IbadahItem.Weekdays, IbadahItem.Weekend, 1, 2, 4, 8, 16, 32, 64 };
            static readonly int[] BeforeSteps = { 0, 5, 10, 15, 30 };

            // Popup + Coba; empty value = fallback label
            Control SoundField(string current, string emptyLabel, Action<string> changed)
            {
                var items = new List<string> { emptyLabel, Lang.T("Tanpa suara"), Lang.T("Pilih berkas…") };
                int at = 0;
                if (current == SoundNone) at = 1;
                else if (current.Length > 0)
                {
                    items.Add(Path.GetFileNameWithoutExtension(current));
                    at = 3;
                }
                var pop = Popup(items.ToArray(), at, i =>
                {
                    if (i == 0) changed("");
                    else if (i == 1) changed(SoundNone);
                    else if (i == 2)
                    {
                        using (var d = new OpenFileDialog { Filter = Lang.T("Audio") + "|*.wav;*.mp3;*.wma;*.m4a|*.*|*.*", RestoreDirectory = true })
                            if (d.ShowDialog(this) == DialogResult.OK && d.FileName.IndexOf('|') < 0) changed(d.FileName);
                        BeginInvoke(new Action(Relayout));
                    }
                });
                pop.Width = Math.Min(pop.Width, (int)(170 * Ui));
                // Test plays even under Senyap
                var test = Button(Lang.T("Coba"), false, delegate { island.PlayRemind(current, true); });
                test.AccessibleName = Lang.T("Coba suara");
                return new KitPair(Ui, pop, test);
            }

            Control SoundSetting(string key, string emptyLabel)
            {
                return SoundField(settings.Get(key, ""), emptyLabel, v => { settings.Set(key, v); settings.Save(); });
            }

            void IbadahRows(Action<KitRow> add, Action<string> cap)
            {
                var list = island.Ibadah();
                cap(Lang.T("Ibadah sendiri"));
                for (int i = 0; i < list.Count; i++)
                {
                    int k = i;
                    var it = list[i];
                    var edit = Button(Lang.T("Ubah"), false, delegate { ibadahEdit = k; ibadahDraft = Copy(list[k]); Relayout(); });
                    var del = Button(Lang.T("Hapus"), false, delegate
                    {
                        var next = new List<IbadahItem>(list);
                        next.RemoveAt(k);
                        island.SaveIbadah(next);
                        if (ibadahEdit == k) ibadahEdit = -2;
                        Relayout();
                    });
                    del.AccessibleName = Lang.F("Hapus {0}", it.Name);
                    edit.AccessibleName = Lang.F("Ubah {0}", it.Name);
                    var row = Row(it.Name, it.Hm + " · " + it.DaysText(), new KitPair(Ui, edit, del));
                    row.Glyph = '\uE787';
                    add(row);
                }
                if (ibadahEdit == -2)
                {
                    if (list.Count < IbadahMax)
                        add(Row(Lang.T("Tambah ibadah"), Lang.T("Doa, kebaktian, meditasi, atau waktu lain"), Button(Lang.T("Tambah"), false, delegate
                        {
                            ibadahEdit = -1;
                            ibadahDraft = new IbadahItem { Minute = 6 * 60 };
                            Relayout();
                        })));
                    return;
                }
                IbadahForm(add, cap, list);
            }

            static IbadahItem Copy(IbadahItem a) { return new IbadahItem { Name = a.Name, Minute = a.Minute, Days = a.Days, Before = a.Before, Sound = a.Sound }; }

            void IbadahForm(Action<KitRow> add, Action<string> cap, List<IbadahItem> list)
            {
                var d = ibadahDraft;
                cap(ibadahEdit == -1 ? Lang.T("Ibadah baru") : Lang.F("Ubah {0}", d.Name));
                var name = new KitField(Ui, false, Lang.T("Contoh: Doa pagi"), 40);
                name.Box.Text = d.Name;
                name.Box.TextChanged += delegate { d.Name = name.Box.Text; };
                add(Row(Lang.T("Nama"), null, name));
                var time = new KitField(Ui, false, "06.00", 5);
                time.Width = (int)(90 * Ui);
                time.Box.Text = Fmt2(d.Minute);
                time.Box.TextChanged += delegate { int m; if (ParseHm(time.Box.Text, out m)) d.Minute = m; };
                var timeRow = Row(Lang.T("Jam"), Lang.T("Format 24 jam, misalnya 18.30"), time);
                add(timeRow);
                int day = Array.IndexOf(DayMasks, d.Days);
                var dayNames = new List<string> { Lang.T("Setiap hari"), Lang.T("Senin sampai Jumat"), Lang.T("Sabtu dan Minggu") };
                foreach (var n in IbadahItem.DayNames) dayNames.Add(Lang.F("Tiap {0}", Lang.T(n)));
                add(Row(Lang.T("Hari"), null, Popup(dayNames.ToArray(), Math.Max(0, day), i => d.Days = DayMasks[i])));
                var befores = new List<string>();
                foreach (int b in BeforeSteps) befores.Add(b == 0 ? Lang.T("tidak") : b + Lang.T(" menit"));
                add(Row(Lang.T("Ingatkan sebelum"), null, Popup(befores.ToArray(), Math.Max(0, Array.IndexOf(BeforeSteps, d.Before)), i => d.Before = BeforeSteps[i])));
                add(Row(Lang.T("Suara"), null, SoundField(d.Sound, Lang.T("Bawaan"), v => d.Sound = v)));
                var cancel = Button(Lang.T("Batal"), false, delegate { ibadahEdit = -2; Relayout(); });
                var save = Button(Lang.T("Simpan"), true, delegate
                {
                    int minute;
                    string clean = d.Name.Replace("|", " ").Trim();
                    if (clean.Length == 0) { name.Box.Focus(); return; }
                    if (!ParseHm(time.Box.Text, out minute))
                    {
                        timeRow.Detail = Lang.T("Jam belum benar, contoh 05.30");
                        timeRow.Invalidate();
                        time.Box.Focus();
                        return;
                    }
                    d.Name = clean;
                    d.Minute = minute;
                    var next = new List<IbadahItem>(list);
                    if (ibadahEdit >= 0 && ibadahEdit < next.Count) next[ibadahEdit] = d; else next.Add(d);
                    island.SaveIbadah(next);
                    ibadahEdit = -2;
                    Relayout();
                });
                var actions = Row("", null, new KitPair(Ui, cancel, save));
                actions.Glyph = '\0';
                add(actions);
            }

            static string Fmt2(int minute) { return (minute / 60).ToString("00", CultureInfo.InvariantCulture) + "." + (minute % 60).ToString("00", CultureInfo.InvariantCulture); }

            // 5.30, 05:30, 0530, 18
            static bool ParseHm(string s, out int minute)
            {
                minute = 0;
                s = (s ?? "").Trim().Replace(':', '.');
                int h, m = 0;
                string[] parts = s.Split('.');
                if (parts.Length == 1 && s.Length == 4) parts = new[] { s.Substring(0, 2), s.Substring(2) };
                if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out h)) return false;
                if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out m)) return false;
                if (h > 23 || m > 59) return false;
                minute = h * 60 + m;
                return true;
            }
        }
    }
}
