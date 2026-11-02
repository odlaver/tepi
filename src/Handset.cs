using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // One file moving between laptop and phone
    class Transfer
    {
        public string Name = "";
        public long Got, Size;
        public bool Incoming, Done, Failed;
        public string Path;
        public DateTime AtUtc = DateTime.UtcNow;
    }

    // Phone side of the island: battery, codes, files
    partial class Island
    {
        const char IcoPhone = '', IcoKey = '', IcoFile = '';
        int phoneBattery = -1;
        bool phoneCharging, phoneLowShown;
        string otpCode = "", otpApp = "";
        DateTime otpUtc = DateTime.MinValue;
        uint otpClipSeq;
        readonly List<Transfer> transfers = new List<Transfer>();
        readonly Dictionary<string, Bitmap> phoneLogos = new Dictionary<string, Bitmap>();
        readonly Dictionary<string, uint> phoneNotifIds = new Dictionary<string, uint>();
        DateTime moveUtc;
        bool otpCopied;

        bool PhonePaired { get { return phone != null && phone.IsPaired; } }

        void WirePhone()
        {
            // Test copies keep files to themselves
            phone.Inbox = Path.Combine(Settings.TestMode ? Settings.Dir : DownloadsFolder() ?? Settings.Dir, "Tepi");
            phone.Pushed += req => Post(() => OnPhonePush(req));
            phone.Received += (path, name) => Post(() =>
            {
                Finish(name, true, path);
                AddToShelf(new[] { path });
                ShowInfo(IcoPhone, Lang.T("Berkas dari HP"), name, true);
            });
            phone.Sent += name => Post(() => { Finish(name, false, null); ShowInfo(IcoPhone, Lang.T("Terkirim ke HP"), name, true); });
            phone.Dropped += name => Post(() =>
            {
                var t = transfers.Find(x => x.Name == name && x.Incoming && !x.Done);
                if (t != null) { t.Failed = true; t.Done = true; Kick(); }
            });
            phone.Moved += (name, got, size, inbound) =>
            {
                // Progress: a few frames a second
                if (got < size && (DateTime.UtcNow - moveUtc).TotalMilliseconds < 200) return;
                moveUtc = DateTime.UtcNow;
                Post(() => Moved(name, got, size, inbound, null));
            };
        }

        void Moved(string name, long got, long size, bool inbound, string path)
        {
            var t = transfers.Find(x => x.Name == name && x.Incoming == inbound && !x.Done);
            if (t == null)
            {
                if (size == 0) size = 1;
                t = new Transfer { Name = name, Incoming = inbound, Size = size };
                transfers.Insert(0, t);
                if (transfers.Count > 3) transfers.RemoveAt(transfers.Count - 1);
            }
            if (size == 0) { if (path != null) t.Path = path; return; }
            t.Got = got;
            t.Size = size;
            t.AtUtc = DateTime.UtcNow;
            if (path != null) t.Path = path;
            if (expanded && page == PagePhone) Kick();
        }

        // Only the server says done
        void Finish(string name, bool inbound, string path)
        {
            Moved(name, 0, 0, inbound, path);
            var t = transfers.Find(x => x.Name == name && x.Incoming == inbound && !x.Done);
            t.Done = true;
            t.Got = t.Size;
            Kick();
        }

        void OnPhonePush(Dictionary<string, object> req)
        {
            var bat = Json.Obj(req, "battery");
            if (bat != null) PhoneBattery((int)Json.Num(bat, "level", -1), Json.Str(bat, "charging") == "True");
            var otp = Json.Obj(req, "otp");
            if (otp != null && settings.GetBool("phone.otp", true)) ShowOtp(Json.Str(otp, "code"), Json.Str(otp, "app"));
            var n = Json.Obj(req, "notif");
            if (n != null && settings.GetBool("phone.notifs", true)) AddPhoneNotif(n);
            string gone = Json.Str(req, "removed");
            if (gone.Length > 0) RemovePhoneNotif(gone);
            Kick();
        }

        void PhoneBattery(int level, bool charging)
        {
            if (level < 0 || level > 100) return;
            phoneBattery = level;
            phoneCharging = charging;
            if (charging || level > 20) phoneLowShown = false;
            else if (level <= 15 && !phoneLowShown)
            {
                phoneLowShown = true;
                ShowInfo(IcoPhone, Lang.F("Baterai HP {0}%", level), phone.Device, true);
            }
        }

        // Codes only: digits, nothing else
        void ShowOtp(string code, string app)
        {
            if (code.Length < 4 || code.Length > 8) return;
            foreach (char c in code) if (c < '0' || c > '9') return;
            otpCode = code;
            otpApp = Fmt.OneLine(app, 40);
            otpUtc = DateTime.UtcNow;
            otpCopied = false;
            ShowToast(View.Otp, 20, 3, true);
        }

        bool OtpFresh { get { return otpCode.Length > 0 && (DateTime.UtcNow - otpUtc).TotalMinutes < 10; } }

        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

        // Kept out of history and cloud; wiped after a minute
        void CopyOtp()
        {
            if (!OtpFresh) return;
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, otpCode);
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[0]));
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
                Clipboard.SetDataObject(data, true);
                otpClipSeq = GetClipboardSequenceNumber();
                otpCopied = true;
                uint seq = otpClipSeq;
                var wipe = new Timer { Interval = 60000 };
                wipe.Tick += delegate
                {
                    wipe.Dispose();
                    try { if (GetClipboardSequenceNumber() == seq) Clipboard.Clear(); } catch (Exception ex) { Log.Write("otp wipe: " + ex.Message); }
                };
                wipe.Start();
            }
            catch (Exception ex) { Log.Write("otp copy: " + ex.Message); }
            Kick();
        }

        float OtpWidth() { return p.G == null ? 300 : Clamp(p.Measure(Lang.F("Kode dari {0}", otpApp), Ink.Text(12f)) + 200, 280, 380); }

        void DrawOtp(float w, float h)
        {
            float cy = h / 2;
            p.Circle(31, cy, 18, p.T.Fill);
            p.Icon(IcoKey, 15f, p.T.Text, 31, cy);
            float x = 60, pw = 72;
            var pill = new RectangleF(w - 16 - pw, cy - 15, pw, 30);
            p.Text(Masked(otpCode), Ink.Display(18f), p.T.Text, x, cy - 9, pill.X - x - 10, StringAlignment.Near);
            p.Text(Lang.F("Kode dari {0}", otpApp), Ink.Text(12f), p.T.Sub, x, cy + 12, pill.X - x - 10, StringAlignment.Near);
            Pill(pill, "otpCopy", otpCopied ? Lang.T("Tersalin") : Lang.T("Salin"), otpCopied ? PillKind.Secondary : PillKind.Primary, CopyOtp);
        }

        // Last two digits only; copy has all
        static string Masked(string code)
        {
            return new string('•', Math.Max(0, code.Length - 2)) + code.Substring(Math.Max(0, code.Length - 2));
        }

        void AddPhoneNotif(Dictionary<string, object> n)
        {
            if (!NotifOn) return;
            string key = Json.Str(n, "key"), pkg = Json.Str(n, "pkg");
            if (key.Length == 0 || pkg.Length == 0) return;
            string icon = Json.Str(n, "icon");
            Bitmap logo;
            if (!phoneLogos.TryGetValue(pkg, out logo) && icon.Length > 0 && icon.Length < 200000)
            {
                try
                {
                    using (var ms = new MemoryStream(Convert.FromBase64String(icon)))
                    using (var raw = new Bitmap(ms)) logo = new Bitmap(raw, 64, 64);
                    if (phoneLogos.Count < 64) phoneLogos[pkg] = logo;
                }
                catch (Exception ex) { Log.Write("phone icon: " + ex.Message); logo = null; }
            }
            uint id;
            if (!phoneNotifIds.TryGetValue(key, out id))
            {
                // High bit: never a Windows id
                id = 0x80000000u | (uint)(phoneNotifIds.Count + 1);
                phoneNotifIds[key] = id;
            }
            var item = new NotifItem
            {
                Id = id, AppId = "phone:" + pkg, AppName = Fmt.OneLine(Json.Str(n, "app"), 40), Title = Fmt.OneLine(Json.Str(n, "title"), 120),
                Body = Fmt.OneLine(Json.Str(n, "text"), 400), Time = DateTime.Now, Chat = true, Held = true, Logo = logo
            };
            notifs.RemoveAll(x => x.Id == id || NotifKey(x) == NotifKey(item));
            notifs.Insert(0, item);
            if (notifs.Count > 40) notifs.RemoveRange(40, notifs.Count - 40);
            if (expanded && page == PageMessages) unread = 0; else unread++;
            if (Native.DoNotDisturb()) return;
            bool burst = toast != null && toast.View == View.Notif;
            var t = ShowToast(View.Notif, 5.5, IsVip(item) ? 3 : 2, true);
            if (t == null) return;
            notifBurst = burst ? notifBurst + 1 : 1;
            t.Data = item;
        }

        void RemovePhoneNotif(string key)
        {
            uint id;
            if (!phoneNotifIds.TryGetValue(key, out id)) return;
            notifs.RemoveAll(x => x.Id == id);
            unread = Math.Min(unread, notifs.Count);
        }

        void SendToPhone(string path)
        {
            if (!PhonePaired) return;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (Exception) { }
            if (size > PhoneLink.FileMax) { ShowInfo(IcoPhone, Lang.T("Berkas lebih dari 500 MB"), Path.GetFileName(path), true); return; }
            if (!phone.OfferFile(path)) { ShowInfo(IcoPhone, Lang.T("Berkas tidak bisa dikirim"), Path.GetFileName(path), true); return; }
            Moved(Path.GetFileName(path), 0, Math.Max(1, size), false, path);
            if (!phone.Online) ShowInfo(IcoPhone, Lang.T("Menunggu HP"), Path.GetFileName(path), true);
        }

        void PickForPhone()
        {
            using (var d = new OpenFileDialog { Title = Lang.T("Kirim ke HP"), Multiselect = true })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                foreach (var f in d.FileNames) SendToPhone(f);
            }
        }

        // Page

        const float PhoneCardH = 76, OtpCardH = 66, XferHeadH = 52, XferRowH = 40;

        float PhoneHeight()
        {
            if (!PhonePaired) return 252;
            float h = PanelTop + 2 + PhoneCardH + Ink.ModuleGap;
            if (OtpFresh) h += OtpCardH + Ink.ModuleGap;
            return Math.Min(MaxH, h + XferHeadH + transfers.Count * XferRowH + (transfers.Count > 0 ? 6 : 0) + Ink.ModuleInset);
        }

        void DrawPhonePage(float w, float h, DateTime now)
        {
            if (!PhonePaired)
            {
                float by = Empty(w, IcoPhone, Lang.T("HP belum tersambung"), Lang.T("Tepi di Android, satu Wi-Fi dengan laptop"));
                Button(new RectangleF(w / 2 - 70, by, 140, 34), "phoneLink", Lang.T("Hubungkan"), false, delegate { OpenPrefs("phone"); });
                return;
            }
            float x0 = Ink.ModuleInset, full = w - 2 * x0, y = PanelTop + 2, keep = p.Alpha, dy;
            p.Alpha = keep * RowIn(0, now, out dy);
            DrawPhoneCard(new RectangleF(x0, y + dy, full, PhoneCardH));
            y += PhoneCardH + Ink.ModuleGap;
            int row = 1;
            if (OtpFresh)
            {
                p.Alpha = keep * RowIn(row++, now, out dy);
                DrawOtpCard(new RectangleF(x0, y + dy, full, OtpCardH));
                y += OtpCardH + Ink.ModuleGap;
            }
            p.Alpha = keep * RowIn(row, now, out dy);
            float fh = XferHeadH + transfers.Count * XferRowH + (transfers.Count > 0 ? 6 : 0);
            DrawFilesCard(new RectangleF(x0, y + dy, full, Math.Min(fh, h - y - Ink.ModuleInset)));
            p.Alpha = keep;
        }

        void DrawPhoneCard(RectangleF r)
        {
            Module(r, Ink.RadiusModule);
            float pad = Ink.ModulePad, cy = r.Y + r.Height / 2;
            p.Circle(r.X + pad + 20, cy, 20, p.T.FillHover);
            p.Icon(IcoPhone, 16f, p.T.Text, r.X + pad + 20, cy);
            float x = r.X + pad + 52, right = r.Right - pad;
            bool online = phone.Online;
            string status = online ? Lang.T("Terhubung") : phone.SeenUtc == DateTime.MinValue ? Lang.T("Belum terlihat") : Lang.F("Terakhir terlihat {0}", Fmt.Ago(phone.SeenUtc));
            float bw = 0;
            if (phoneBattery >= 0)
            {
                string pct = phoneBattery + "%";
                var pf = Ink.Display(20f);
                bw = p.Measure(pct, pf) + 36;
                p.TextRight(pct, pf, p.T.Text, right, cy);
                DrawPhoneBattery(right - bw + 2, cy);
            }
            p.Text(phone.Device.Length > 0 ? phone.Device : Lang.T("HP"), Ink.Semi(14f), p.T.Text, x, cy - 10, right - bw - x - 10, StringAlignment.Near);
            if (online) p.Circle(x + 3.5f, cy + 11, 3.5f, p.T.Positive);
            float sx = online ? x + 13 : x;
            string sub = phoneCharging && online ? status + Lang.T(" · mengisi") : status;
            p.Text(sub, Ink.Text(12f), p.T.Sub, sx, cy + 11, right - bw - sx - 10, StringAlignment.Near);
        }

        // Small battery: shell, nub, fill
        void DrawPhoneBattery(float x, float cy)
        {
            var shell = new RectangleF(x, cy - 6, 22, 12);
            p.StrokeRound(shell, 3.5f, p.T.Sub, 1.3f);
            p.FillRound(new RectangleF(shell.Right + 1, cy - 2.5f, 2, 5), 1, p.T.Sub);
            Color c = phoneCharging ? p.T.Positive : phoneBattery <= 15 ? p.T.Negative : p.T.Text;
            float fw = Math.Max(2, (shell.Width - 4) * phoneBattery / 100f);
            p.FillRound(new RectangleF(shell.X + 2, shell.Y + 2, fw, shell.Height - 4), 1.8f, c);
        }

        void DrawOtpCard(RectangleF r)
        {
            Module(r, Ink.RadiusModule);
            float pad = Ink.ModulePad, cy = r.Y + r.Height / 2, pw = 76;
            var pill = new RectangleF(r.Right - pad - pw, cy - 16, pw, 32);
            p.Text(Lang.F("Kode dari {0}", otpApp), Ink.Small(11.5f), p.T.Sub, r.X + pad, cy - 12, pill.X - r.X - pad - 10, StringAlignment.Near);
            p.Text(Masked(otpCode), Ink.Display(20f), p.T.Text, r.X + pad, cy + 9, pill.X - r.X - pad - 10, StringAlignment.Near);
            Pill(pill, "otpCopyCard", otpCopied ? Lang.T("Tersalin") : Lang.T("Salin"), PillKind.Secondary, CopyOtp);
        }

        void DrawFilesCard(RectangleF r)
        {
            Module(r, Ink.RadiusModule);
            float pad = Ink.ModulePad, hy = r.Y + XferHeadH / 2;
            string label = Lang.T("Kirim berkas");
            float bw = p.Measure(label, Ink.Semi(13f)) + 32;
            var pill = new RectangleF(r.Right - pad - bw, hy - 15, bw, 30);
            p.Text(Lang.T("Berkas"), Ink.Headline, p.T.Text, r.X + pad, hy - 8, pill.X - r.X - pad - 10, StringAlignment.Near);
            p.Text(Lang.T("Dari HP masuk ke Downloads\\Tepi dan Rak"), Ink.Small(11f), p.T.Sub, r.X + pad, hy + 9, pill.X - r.X - pad - 10, StringAlignment.Near);
            Pill(pill, "phoneSend", label, PillKind.Secondary, delegate { BeginInvoke(new Action(PickForPhone)); });
            for (int i = 0; i < transfers.Count; i++)
            {
                var t = transfers[i];
                float ry = r.Y + XferHeadH + i * XferRowH, cy = ry + XferRowH / 2;
                if (ry + XferRowH > r.Bottom + 1) break;
                p.Icon(t.Incoming ? '' : '', 13f, p.T.Sub, r.X + pad + 8, cy);
                float tx = r.X + pad + 26, right = r.Right - pad;
                string state = t.Failed ? Lang.T("Gagal") : t.Done ? (t.Incoming ? Lang.T("Di Rak") : Lang.T("Terkirim")) : Fmt.Bytes(t.Got) + " / " + Fmt.Bytes(t.Size);
                float sw = Math.Min(130, p.Measure(state, Ink.Small(11f))) + 4;
                p.TextRight(state, Ink.Small(11f), t.Failed ? p.T.Negative : p.T.Sub, right, cy - (t.Done ? 0 : 5));
                p.Text(t.Name, Ink.Text(12.5f), p.T.Text, tx, cy - (t.Done ? 0 : 5), right - sw - tx - 8, StringAlignment.Near);
                if (t.Done) { var tr = t; if (tr.Path != null) AddHit(new RectangleF(r.X + 6, ry, r.Width - 12, XferRowH), "xfer" + i, delegate { RevealShelfItem(new ShelfItem { Path = tr.Path }); }); continue; }
                // Thin progress line under the name
                var bar = new RectangleF(tx, cy + 9, right - tx, 3);
                p.FillRound(bar, 1.5f, p.T.Quaternary);
                p.FillRound(new RectangleF(bar.X, bar.Y, Math.Max(3, bar.Width * Math.Min(1f, t.Size > 0 ? (float)t.Got / t.Size : 0)), bar.Height), 1.5f, p.T.Accent);
            }
        }
    }
}
