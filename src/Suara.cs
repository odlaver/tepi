using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Tepi
{
    // Suara tab: dictate, record, command, history
    partial class Island
    {
        const int PageVoice = 10;
        const float VoiceTileH = 66, VoiceRowH = 50;
        readonly Spring voiceScroll = new Spring(0, 300, 34, 0.2f);
        readonly List<VoiceEntry> transcripts = new List<VoiceEntry>();
        DateTime transcriptsReadUtc = DateTime.MinValue;

        float VoiceListTop { get { return PanelTop + 4 + VoiceTileH + 14; } }
        float VoiceListView(float h) { return h - VoiceListTop - 26 - 10; }

        // Pending first, then newest
        List<VoiceEntry> VoiceItems()
        {
            if ((DateTime.UtcNow - transcriptsReadUtc).TotalSeconds > 30) ReadTranscripts();
            var list = new List<VoiceEntry>();
            foreach (var o in oneShots) list.Add(new VoiceEntry { Kind = "nanti", Text = o.Text, Reminder = o, Utc = o.At.ToUniversalTime() });
            var rest = new List<VoiceEntry>(voiceLog);
            rest.AddRange(transcripts);
            rest.Sort((a, b) => b.Utc.CompareTo(a.Utc));
            list.AddRange(rest);
            return list;
        }

        // Five newest transcripts from disk
        void ReadTranscripts()
        {
            transcriptsReadUtc = DateTime.UtcNow;
            transcripts.Clear();
            try
            {
                if (!Directory.Exists(RapatDir)) return;
                var files = new DirectoryInfo(RapatDir).GetFiles("*.md");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 0; i < Math.Min(5, files.Length); i++)
                    transcripts.Add(new VoiceEntry { Kind = "rapat", Text = Path.GetFileNameWithoutExtension(files[i].Name), Detail = Lang.T("Transkrip rapat"), Path = files[i].FullName, Utc = files[i].LastWriteTimeUtc });
            }
            catch (Exception ex) { Log.Write("daftar transkrip: " + ex.Message); }
        }

        float VoicePageHeight()
        {
            int n = VoiceItems().Count;
            float list = n == 0 ? 52 : 26 + n * VoiceRowH;
            return Math.Min(MaxH, VoiceListTop + list + 10);
        }

        void DrawVoicePage(float w, float h, DateTime now)
        {
            float gap = 8, tw = (w - 32 - 2 * gap) / 3, y = PanelTop + 4;
            string keyName = settings.Get("dictate.key", "rctrl") == "ralt" ? Lang.T("Alt kanan") : Lang.T("Ctrl kanan");
            string missing = DictOn ? DictMissing() : null;
            VoiceTile(new RectangleF(16, y, tw, VoiceTileH), "vDict", Ink.IcoMic, Lang.T("Dikte"),
                !DictOn ? Lang.T("Nyalakan di Pengaturan") : missing ?? Lang.F("Tahan {0}", keyName), false,
                delegate { if (!DictOn || missing != null) OpenPrefs("dikte"); else BeginDictate(true); });
            string rec = RapatOn ? Lang.F("Merekam {0}", Clock((DateTime.UtcNow - rapatStartUtc).TotalSeconds)) : rapatWriting ? Lang.T("Menulis transkrip") : HotkeyText(RapatHotkey).Length > 0 ? HotkeyText(RapatHotkey) : Lang.T("Mulai rekam");
            VoiceTile(new RectangleF(16 + tw + gap, y, tw, VoiceTileH), "vRec", '', RapatOn ? Lang.T("Hentikan") : Lang.T("Rekam rapat"), rec, RapatOn,
                delegate { if (!rapatWriting) ToggleRapat(); });
            VoiceTile(new RectangleF(16 + 2 * (tw + gap), y, tw, VoiceTileH), "vAsk", '', Lang.T("Suruh Tepi"), Lang.F("Shift+{0}", keyName), false, OpenSuruh);
            DrawVoiceList(w, h);
        }

        void VoiceTile(RectangleF r, string id, char glyph, string title, string sub, bool live, Action act)
        {
            AddHit(r, id, act);
            bool hover = Hover(id), down = Pressed(id);
            if (down) r.Inflate(-1.5f, -1.5f);
            p.FillRound(r, 18, down ? p.T.FillPressed : hover ? p.T.FillHover : p.T.Fill);
            // Recording: a red dot, not the glyph
            if (live) p.FillRound(new RectangleF(r.X + 14, r.Y + 13, 12, 12), 6, p.T.Negative);
            else p.Icon(glyph, 15f, p.T.Text, r.X + 20, r.Y + 19);
            p.Text(title, Ink.Semi(13f), p.T.Text, r.X + 14, r.Y + 40, r.Width - 22, StringAlignment.Near);
            p.Text(sub, Ink.Small(11f), p.T.Sub, r.X + 14, r.Y + 55, r.Width - 22, StringAlignment.Near);
        }

        static char VoiceGlyph(string kind)
        {
            switch (kind)
            {
                case "dikte": return Ink.IcoMic;
                case "rapat": return '';
                case "nanti":
                case "pengingat": return Ink.IcoBell;
                default: return '';
            }
        }

        void DrawVoiceList(float w, float h)
        {
            float top = VoiceListTop;
            p.Text(Lang.T("Terakhir"), Ink.Semi(12f), p.T.Sub, 24, top + 8, 200, StringAlignment.Near);
            var items = VoiceItems();
            if (items.Count == 0)
            {
                p.Text(Lang.T("Dikte, perintah, pengingat, dan transkrip rapat muncul di sini"), Ink.Text(12.5f), p.T.Faint, 24, top + 36, w - 48, StringAlignment.Near);
                return;
            }
            var area = new RectangleF(10, top + 22, w - 20, VoiceListView(h));
            var st = g.Save();
            g.IntersectClip(area);
            float y = area.Y - voiceScroll.Value;
            for (int i = 0; i < items.Count; i++, y += VoiceRowH)
            {
                if (y + VoiceRowH < area.Top || y > area.Bottom) continue;
                var e = items[i];
                var row = new RectangleF(area.X + 4, y + 2, area.Width - 8, VoiceRowH - 4);
                string id = "voice" + i;
                if (Hover(id)) p.FillRound(row, 14, Pressed(id) ? p.T.FillHover : p.T.Fill);
                float rcy = row.Y + row.Height / 2;
                p.Icon(VoiceGlyph(e.Kind), 14f, e.Kind == "nanti" ? p.T.Text : p.T.Sub, row.X + 20, rcy);
                float tx = row.X + 42;
                p.Text(e.Text, Ink.Semi(12.5f), p.T.Text, tx, rcy - 8, row.Right - tx - 14, StringAlignment.Near);
                string detail = e.Kind == "nanti" ? Lang.F("Nanti {0}", ReminderWhen(e.Reminder.At)) : (e.Detail.Length > 0 ? e.Detail + " · " : "") + Fmt.Ago(e.Utc);
                p.Text(detail, Ink.Small(11f), p.T.Faint, tx, rcy + 9, row.Right - tx - 14, StringAlignment.Near);
                var pick = e;
                if (row.Top >= area.Top && row.Bottom <= area.Bottom) AddHit(row, id, delegate { VoiceOpen(pick); }, null, delegate { VoiceMenu(pick); });
            }
            g.Restore(st);
        }

        string ReminderWhen(DateTime at)
        {
            if (at.Date == DateTime.Today) return Fmt.Hm(at);
            if (at.Date == DateTime.Today.AddDays(1)) return Lang.T("besok ") + Fmt.Hm(at);
            return at.ToString("d MMM ", Lang.English ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo("id-ID")) + Fmt.Hm(at);
        }

        // Click: open a transcript, else copy
        void VoiceOpen(VoiceEntry e)
        {
            if (e.Kind == "rapat") { try { Process.Start(e.Path); } catch (Exception ex) { Log.Write("buka transkrip: " + ex.Message); } return; }
            if (e.Kind == "nanti") { VoiceMenu(e); return; }
            try { Clipboard.SetText(e.Kind == "suruh" && e.Detail.Length > 0 ? e.Detail : e.Text); }
            catch (Exception) { return; }
            ShowInfo('', Lang.T("Disalin"), "", true);
        }

        void VoiceMenu(VoiceEntry e)
        {
            var m = new ContextMenuStrip();
            if (e.Kind == "nanti") m.Items.Add(Lang.T("Batalkan pengingat"), null, delegate { CancelOneShot(e.Reminder); });
            else if (e.Kind == "rapat")
            {
                m.Items.Add(Lang.T("Buka"), null, delegate { VoiceOpen(e); });
                m.Items.Add(Lang.T("Tampilkan di folder"), null, delegate { try { Process.Start("explorer.exe", "/select,\"" + e.Path + "\""); } catch (Exception) { } });
            }
            else
            {
                m.Items.Add(Lang.T("Salin"), null, delegate { VoiceOpen(e); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Hapus dari daftar"), null, delegate { voiceLog.Remove(e); Kick(); });
            }
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(Cursor.Position);
        }

        void ClearVoiceLog()
        {
            Vanish(RowRects("voice", null), true);
            voiceScroll.Snap(0);
            voiceLog.Clear();
            Kick();
        }

        // 2.6: the new tab joins existing layouts
        void AddVoiceTab()
        {
            if (settings.GetBool("tabs.suara", false)) return;
            string list = settings.Get("panel.tabs", "");
            if (list.Length > 0 && Array.IndexOf(list.Split(','), "suara") < 0) settings.Set("panel.tabs", list + ",suara");
            settings.Set("tabs.suara", true);
            settings.Save();
        }
    }
}
