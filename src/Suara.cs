using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Suara tab: dictate, record, command, history
    partial class Island
    {
        const int PageVoice = 10;
        const float VoiceTileH = 72, VoiceRowH = 50;
        readonly Spring voiceScroll = new Spring(0, 300, 34, 0.2f);
        readonly List<VoiceEntry> transcripts = new List<VoiceEntry>();
        DateTime transcriptsReadUtc = DateTime.MinValue;
        // Row opened to its full text
        VoiceEntry voiceOpen;

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

        // Answer for commands; what was said for dictation
        static string VoiceFull(VoiceEntry e) { return e.Kind == "suruh" && e.Detail.Length > 0 ? e.Detail : e.Text; }

        // Only log entries open; they keep their identity
        bool VoiceExpands(VoiceEntry e) { return voiceLog.Contains(e); }

        float VoiceTextW(float w) { return w - 20 - 8 - 42 - 16; }

        float VoiceRowHeight(VoiceEntry e, float w)
        {
            if (!ReferenceEquals(e, voiceOpen) || p.G == null) return VoiceRowH;
            float text = Math.Min(160, p.WrapHeight(VoiceFull(e), Ink.Text(12.5f), VoiceTextW(w)));
            return VoiceRowH + text + 28;
        }

        float VoiceContentH(List<VoiceEntry> items, float w)
        {
            float h = 0;
            foreach (var e in items) h += VoiceRowHeight(e, w);
            return h;
        }

        float VoicePageHeight()
        {
            var items = VoiceItems();
            float list = items.Count == 0 ? 52 : 26 + VoiceContentH(items, PanelW);
            return Math.Min(MaxH, VoiceListTop + list + 10);
        }

        void DrawVoicePage(float w, float h, DateTime now)
        {
            float gap = 8, tw = (w - 32 - 2 * gap) / 3, y = PanelTop + 4;
            string keyName = settings.Get("dictate.key", "rctrl") == "ralt" ? Lang.T("Alt kanan") : Lang.T("Ctrl kanan");
            string missing = DictOn ? DictMissing() : null;
            VoiceTile(new RectangleF(16, y, tw, VoiceTileH), "vDict", Ink.IcoMic, Lang.T("Dikte"),
                !DictOn ? Lang.T("Nyalakan di Pengaturan") : missing ?? Lang.F("Tahan {0}", keyName), false,
                delegate { if (!DictOn) OpenPrefs("dikte"); else if (missing != null) OpenPrefs("ai"); else BeginDictate(true); });
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
            // Centred: mark, name, hint
            float cx = r.X + r.Width / 2;
            // Recording: a red dot, not the glyph
            if (live) p.FillRound(new RectangleF(cx - 6, r.Y + 14, 12, 12), 6, p.T.Negative);
            else p.Icon(glyph, 16f, p.T.Text, cx, r.Y + 20);
            p.Text(title, Ink.Semi(13f), p.T.Text, cx, r.Y + 43, r.Width - 16, StringAlignment.Center);
            p.Text(sub, Ink.Small(11f), p.T.Sub, cx, r.Y + 59, r.Width - 16, StringAlignment.Center);
        }

        static char VoiceGlyph(string kind)
        {
            switch (kind)
            {
                case "dikte": return Ink.IcoMic;
                case "rapat": return '';
                case "nanti":
                case "pengingat": return Ink.IcoBell;
                default: return '';
            }
        }

        // What it was, where from
        string VoiceDetail(VoiceEntry e)
        {
            switch (e.Kind)
            {
                case "nanti": return Lang.F("Nanti {0}", ReminderWhen(e.Reminder.At));
                case "dikte": return e.Detail.Length > 0 ? Lang.T("Dikte") + " · " + e.Detail : Lang.T("Dikte");
                case "suruh": return e.Detail;
            }
            return e.Detail;
        }

        void DrawVoiceList(float w, float h)
        {
            float top = VoiceListTop;
            p.Text(Lang.T("Riwayat"), Ink.Semi(12f), p.T.Sub, 24, top + 8, 200, StringAlignment.Near);
            // Trash by the title; the folder sits beside the gear
            if (voiceLog.Count > 0) VoiceClear(new RectangleF(w - 18 - 30, top - 5, 30, 26));
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
            for (int i = 0; i < items.Count; i++)
            {
                var e = items[i];
                float rowH = VoiceRowHeight(e, w);
                float rowY = y;
                y += rowH;
                if (rowY + rowH < area.Top || rowY > area.Bottom) continue;
                DrawVoiceRow(e, i, new RectangleF(area.X + 4, rowY + 2, area.Width - 8, rowH - 4), area);
            }
            g.Restore(st);
        }

        void DrawVoiceRow(VoiceEntry e, int i, RectangleF row, RectangleF area)
        {
            string id = "voice" + i, del = "voiceDel" + i;
            bool open = ReferenceEquals(e, voiceOpen), hover = Hover(id) || Hover(del);
            if (open || hover) p.FillRound(row, 14, Pressed(id) ? p.T.FillHover : p.T.Fill);
            float rcy = row.Y + VoiceRowH / 2 - 2;
            p.Icon(VoiceGlyph(e.Kind), 14f, e.Kind == "nanti" ? p.T.Text : p.T.Sub, row.X + 20, rcy);
            float tx = row.X + 42, right = row.Right - 14;
            var del0 = new RectangleF(row.Right - 36, rcy - 14, 28, 28);
            // Hover trades the time for a delete button
            if (hover && !open)
            {
                p.Circle(del0.X + 14, del0.Y + 14, 12, Hover(del) ? p.T.FillHover : p.T.Fill);
                p.Cross(del0.X + 14, del0.Y + 14, 10, p.T.Sub);
                right = del0.X - 6;
            }
            else if (!open) p.TextRight(Fmt.Ago(e.Utc), Ink.Small(11f), p.T.Faint, right, rcy - 8);
            float titleRight = hover || open ? right : right - 64;
            // Open: the full text replaces the one-line detail
            string title = open && e.Kind == "dikte" ? VoiceDetail(e) : e.Text;
            p.Text(title, Ink.Semi(12.5f), p.T.Text, tx, rcy - 8, titleRight - tx, StringAlignment.Near);
            if (!open) p.Text(VoiceDetail(e), Ink.Small(11f), e.Kind == "suruh" ? p.T.Sub : p.T.Faint, tx, rcy + 9, right - tx, StringAlignment.Near);
            var pick = e;
            bool whole = row.Top >= area.Top && row.Bottom <= area.Bottom;
            if (open)
            {
                // Full text, then copy and delete
                float textH = row.Height + 4 - VoiceRowH - 28;
                p.TextBox(VoiceFull(e), Ink.Text(12.5f), p.T.Text, new RectangleF(tx, row.Y + 34, VoiceTextW(area.Width + 20), textH));
                float by = row.Bottom - 30;
                var copy = new RectangleF(tx - 6, by, 70, 24);
                var rm = new RectangleF(copy.Right + 6, by, 70, 24);
                VoiceAction(copy, "voiceCopy" + i, Lang.T("Salin"), delegate { VoiceCopy(pick); });
                VoiceAction(rm, "voiceRm" + i, Lang.T("Hapus"), delegate { VoiceDelete(pick, row); });
                p.TextRight(Fmt.Ago(e.Utc), Ink.Small(11f), p.T.Faint, row.Right - 14, by + 12);
            }
            if (!whole) return;
            AddHit(row, id, delegate { VoiceOpen(pick); }, null, delegate { VoiceMenu(pick); });
            // Added after the row: wins inside it
            if (hover && !open) AddHit(del0, del, delegate { VoiceDelete(pick, row); });
        }

        // Rows fly into this trash when cleared
        void VoiceClear(RectangleF r)
        {
            AddHit(r, "clearVoice", ClearVoiceLog);
            bool hover = Hover("clearVoice");
            if (hover) p.FillRound(r, r.Height / 2, Pressed("clearVoice") ? p.T.FillHover : p.T.Fill);
            var c = CanvasRect(r);
            trashPt = new PointF(c.X + c.Width / 2, c.Y + c.Height / 2);
            float s = TrashScale(DateTime.UtcNow);
            p.Icon('\uE74D', 12.5f * s, hover || s > 1.01f ? p.T.Text : p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        void VoiceAction(RectangleF r, string id, string label, Action act)
        {
            AddHit(r, id, act);
            p.FillRound(r, 12, Pressed(id) ? p.T.FillPressed : Hover(id) ? p.T.FillHover : Ink.Mix(p.T.Fill, p.T.FillHover, 0.5f));
            p.TextCenter(label, Ink.Semi(11.5f), p.T.Text, r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        string ReminderWhen(DateTime at)
        {
            if (at.Date == DateTime.Today) return Fmt.Hm(at);
            if (at.Date == DateTime.Today.AddDays(1)) return Lang.T("besok ") + Fmt.Hm(at);
            return at.ToString("d MMM ", Lang.English ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo("id-ID")) + Fmt.Hm(at);
        }

        // Click: open text, a transcript, the chat, or the reminder menu
        void VoiceOpen(VoiceEntry e)
        {
            if (e.Kind == "rapat") { try { Process.Start(e.Path); } catch (Exception ex) { Log.Write("buka transkrip: " + ex.Message); } return; }
            if (e.Kind == "nanti") { VoiceMenu(e); return; }
            if (e.Kind == "suruh") { OpenSuruh(); return; }
            if (!VoiceExpands(e)) { VoiceCopy(e); return; }
            voiceOpen = ReferenceEquals(voiceOpen, e) ? null : e;
            Kick();
        }

        void VoiceCopy(VoiceEntry e)
        {
            try { Clipboard.SetText(VoiceFull(e)); }
            catch (Exception) { return; }
            ShowInfo('', Lang.T("Disalin"), "", true);
        }

        // Log rows go; reminders cancel; transcripts to the Recycle Bin
        void VoiceDelete(VoiceEntry e, RectangleF row)
        {
            if (e.Kind == "nanti") { CancelOneShot(e.Reminder); return; }
            if (e.Kind == "rapat")
            {
                if (!Recycle(e.Path)) { ShowInfo('', Lang.T("Transkrip tidak bisa dihapus"), Path.GetFileName(e.Path), true); return; }
                transcriptsReadUtc = DateTime.MinValue;
            }
            else voiceLog.Remove(e);
            if (ReferenceEquals(voiceOpen, e)) voiceOpen = null;
            Vanish(new[] { CanvasRect(row) }, false);
            Kick();
        }

        void VoiceMenu(VoiceEntry e)
        {
            var m = new ContextMenuStrip();
            if (e.Kind == "nanti") m.Items.Add(Lang.T("Batalkan pengingat"), null, delegate { CancelOneShot(e.Reminder); });
            else if (e.Kind == "rapat")
            {
                m.Items.Add(Lang.T("Buka"), null, delegate { VoiceOpen(e); });
                m.Items.Add(Lang.T("Tampilkan di folder"), null, delegate { try { Process.Start("explorer.exe", "/select,\"" + e.Path + "\""); } catch (Exception) { } });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Pindahkan ke Recycle Bin"), null, delegate { VoiceDelete(e, RectangleF.Empty); });
            }
            else
            {
                m.Items.Add(Lang.T("Salin"), null, delegate { VoiceCopy(e); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Hapus dari daftar"), null, delegate { VoiceDelete(e, RectangleF.Empty); });
            }
            StyleMenu(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(Cursor.Position);
        }

        void ClearVoiceLog()
        {
            Vanish(RowRects("voice", "voiceDel"), true);
            voiceScroll.Snap(0);
            voiceLog.Clear();
            voiceOpen = null;
            Kick();
        }

        void OpenRapatFolder()
        {
            try
            {
                Directory.CreateDirectory(RapatDir);
                Process.Start("explorer.exe", "\"" + RapatDir + "\"");
            }
            catch (Exception ex) { Log.Write("folder rekaman: " + ex.Message); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShFileOp
        {
            public IntPtr Hwnd;
            public uint Func;
            public string From, To;
            public ushort Flags;
            public bool Aborted;
            public IntPtr Mappings;
            public string Title;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref ShFileOp op);

        // Recycle Bin, so a slip can be undone
        static bool Recycle(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            // FO_DELETE; ALLOWUNDO, NOCONFIRMATION, SILENT, NOERRORUI
            var op = new ShFileOp { Func = 3, From = path + "\0\0", Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 };
            return SHFileOperation(ref op) == 0 && !op.Aborted;
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
