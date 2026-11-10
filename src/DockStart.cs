using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tepi
{
    // Lock, sleep, sign out, restart, shut down
    static class SessionPower
    {
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);
        [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

        public static void Lock() { LockWorkStation(); }
        public static void Sleep() { SetSuspendState(false, false, false); }
        public static void SignOut() { ExitWindowsEx(0, 0); }
        public static void Restart() { Run("/r /t 0"); }
        public static void ShutDown() { Run("/s /t 0"); }

        // Apps still get their chance to save or object
        static void Run(string args)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { Log.Write("power: " + ex.Message); }
        }
    }

    partial class Island
    {
        // An app, a pin or a recent file on Tepi Start
        sealed class AppEntry
        {
            public string Name = "", Id = "", Target = "", Sub = "";
            // Settings pages open by address; Target is only their icon
            public string Uri = "";
            public bool File, Asked;
            public DateTime When;
            public Bitmap Icon;
        }

        // Start: Tepi's own launcher above the dock, in the dock's glass.
        // Pinned, recent, all apps; takes focus for typing, closes when focus leaves.
        sealed class StartCard : GlassSheet
        {
            static List<AppEntry> catalog;
            static DateTime catalogUtc = DateTime.MinValue;
            static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            static readonly object iconGate = new object();
            static bool importing;

            float scroll, allTop, contentH;
            Bitmap avatar;
            string query = "", userName = "";
            int sel, hot = -1;
            bool loading;
            List<AppEntry> pins = new List<AppEntry>(), recent = new List<AppEntry>();
            readonly List<AppEntry> results = new List<AppEntry>();
            List<AppEntry> settingHits = new List<AppEntry>();
            float settingsTop;

            // Everything on the card this frame that can be picked
            sealed class Tile { public AppEntry Entry; public RectangleF Rect; public bool Row, Pin; }
            readonly List<Tile> tiles = new List<Tile>();
            Thread iconThread;
            readonly Queue<AppEntry> iconQueue = new Queue<AppEntry>();
            readonly AutoResetEvent iconWake = new AutoResetEvent(false);
            volatile bool iconRun = true;
            int iconPx = 48;

            const float Pad = 20, CellW = 100, CellH = 90, RowH = 56, Head = 34, BodyTop = 72, Foot = 64;
            const int Cols = 6;
            float ViewH { get { return H - BodyTop - Foot; } }

            [DllImport("secur32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserNameEx(int format, StringBuilder name, ref int size);
            [DllImport("shell32.dll", EntryPoint = "#261", CharSet = CharSet.Unicode)] static extern int SHGetUserPicturePath(string user, uint flags, StringBuilder path, int len);

            public StartCard(Island isle, DockWindow dock) : base(isle, dock, "start", "Tepi Start")
            {
                W = 640;
                H = 720;
            }

            protected override void Teardown()
            {
                iconRun = false;
                iconWake.Set();
                if (avatar != null) avatar.Dispose();
            }

            protected override void Opening(Rectangle mon, int barTop)
            {
                iconPx = Math.Max(32, (int)Math.Ceiling(40 * scale));
                userName = UserName();
                avatar = Avatar();
                iconThread = new Thread(IconLoop) { IsBackground = true, Name = "start icons" };
                iconThread.SetApartmentState(ApartmentState.STA);
                iconThread.Start();
                LoadCatalog();
                LoadPins();
                LoadRecent();
            }

            static string UserName()
            {
                var sb = new StringBuilder(256);
                int size = sb.Capacity;
                if (GetUserNameEx(3, sb, ref size) && sb.Length > 0) return sb.ToString();
                return Environment.UserName;
            }

            // The account picture Windows shows, when there is one
            static Bitmap Avatar()
            {
                try
                {
                    // Shell's readable copy; the originals are locked to the system
                    var sb = new StringBuilder(520);
                    if (SHGetUserPicturePath(null, 0x80000000, sb, sb.Capacity) == 0 && System.IO.File.Exists(sb.ToString()))
                        using (var img = Image.FromFile(sb.ToString())) return new Bitmap(img, 96, 96);
                }
                catch (Exception) { }
                try
                {
                    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid))
                    {
                        string file = k == null ? null : (k.GetValue("Image96") ?? k.GetValue("Image192")) as string;
                        if (file != null && System.IO.File.Exists(file)) using (var img = Image.FromFile(file)) return new Bitmap(img, 96, 96);
                    }
                }
                catch (Exception) { }
                return null;
            }

            // All apps, read off the UI thread; kept for ten minutes
            void LoadCatalog()
            {
                if (catalog != null && (DateTime.UtcNow - catalogUtc).TotalMinutes < 10) return;
                loading = catalog == null;
                Background(delegate
                {
                    var list = new List<AppEntry>();
                    foreach (var kv in ShellApp.AllApps()) list.Add(new AppEntry { Name = kv.Key, Id = kv.Value, Target = ShellApp.AppsFolderOf(kv.Value) });
                    list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                    Post(delegate
                    {
                        catalog = list;
                        catalogUtc = DateTime.UtcNow;
                        loading = false;
                        LoadPins();
                        Filter();
                        Render();
                    });
                });
            }

            void Background(ThreadStart work)
            {
                var t = new Thread(delegate () { try { work(); } catch (Exception ex) { Log.Write("start: " + ex.Message); } }) { IsBackground = true, Name = "start load" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }

            void Post(Action a)
            {
                try { if (!IsDisposed) BeginInvoke(a); } catch (Exception) { }
            }

            // Pins live in Tepi's settings; the first time they come from Windows' Start
            void LoadPins()
            {
                string saved = isle.settings.Get("start.pins", null);
                if (saved == null)
                {
                    if (!importing) ImportPins();
                    return;
                }
                var list = new List<AppEntry>();
                foreach (var part in saved.Split('|'))
                {
                    string t = part.Trim();
                    if (t.Length == 0) continue;
                    var e = EntryFor(t);
                    if (e != null) list.Add(e);
                }
                pins = list;
            }

            AppEntry EntryFor(string target)
            {
                if (ShellApp.IsAppsFolder(target))
                {
                    string id = ShellApp.AumidOf(target);
                    var known = catalog != null ? catalog.Find(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
                    // Not installed any more: no pin
                    if (catalog != null && known == null) return null;
                    return new AppEntry { Name = known != null ? known.Name : id, Id = id, Target = target };
                }
                if (!System.IO.File.Exists(target)) return null;
                string name = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(target) : AppWatch.ExeName(target);
                return new AppEntry { Name = name, Target = target };
            }

            void ImportPins()
            {
                importing = true;
                Background(delegate
                {
                    var list = new List<string>();
                    string file = Path.Combine(Path.GetTempPath(), "tepi-start-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"Export-StartLayout -Path '" + file + "'\"") { UseShellExecute = false, CreateNoWindow = true };
                        using (var pr = Process.Start(psi)) pr.WaitForExit(20000);
                        if (System.IO.File.Exists(file))
                        {
                            var root = Json.Parse(System.IO.File.ReadAllText(file));
                            foreach (var item in Json.Arr(root, "pinnedList") ?? new object[0])
                            {
                                string link = Json.Str(item, "desktopAppLink"), pack = Json.Str(item, "packagedAppId"), desk = Json.Str(item, "desktopAppId");
                                if (!string.IsNullOrEmpty(link)) { link = Environment.ExpandEnvironmentVariables(link); if (System.IO.File.Exists(link)) list.Add(link); }
                                else if (!string.IsNullOrEmpty(pack)) list.Add(ShellApp.AppsFolderOf(pack));
                                else if (!string.IsNullOrEmpty(desk)) list.Add(ShellApp.AppsFolderOf(desk));
                            }
                        }
                    }
                    finally { try { System.IO.File.Delete(file); } catch (Exception) { } }
                    Post(delegate
                    {
                        importing = false;
                        SavePins(list);
                    });
                });
            }

            void SavePins(List<string> targets)
            {
                isle.settings.Set("start.pins", string.Join("|", targets.ToArray()));
                isle.settings.Save();
                LoadPins();
                Filter();
                Render();
            }

            List<string> PinTargets()
            {
                var list = new List<string>();
                foreach (var part in isle.settings.Get("start.pins", "").Split('|')) if (part.Trim().Length > 0) list.Add(part.Trim());
                return list;
            }

            // Recent files and newly added apps, newest first
            void LoadRecent()
            {
                Background(delegate
                {
                    var list = new List<AppEntry>();
                    try
                    {
                        var dir = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.Recent));
                        var links = new List<FileInfo>(dir.GetFiles("*.lnk"));
                        links.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                        int looked = 0;
                        foreach (var f in links)
                        {
                            if (list.Count >= 6 || ++looked > 40) break;
                            string target = ShellApp.ReadLink(f.FullName).Target;
                            if (target.Length == 0 || !System.IO.File.Exists(target)) continue;
                            list.Add(new AppEntry { Name = Path.GetFileName(target), Target = target, File = true, When = f.LastWriteTimeUtc, Sub = Fmt.Ago(f.LastWriteTimeUtc) });
                        }
                    }
                    catch (Exception ex) { Log.Write("start recent: " + ex.Message); }
                    var cutoff = DateTime.UtcNow.AddDays(-14);
                    var added = new List<AppEntry>();
                    foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
                    {
                        try
                        {
                            foreach (var path in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                            {
                                var made = System.IO.File.GetCreationTimeUtc(path);
                                string name = Path.GetFileNameWithoutExtension(path);
                                if (made < cutoff || Extra(name)) continue;
                                added.Add(new AppEntry { Name = name, Target = path, When = made, Sub = Lang.T("Baru ditambahkan") });
                            }
                        }
                        catch (Exception) { }
                    }
                    // New apps leave room for files
                    added.Sort((a, b) => b.When.CompareTo(a.When));
                    if (list.Count > 0 && added.Count > 3) added.RemoveRange(3, added.Count - 3);
                    list.AddRange(added);
                    list.Sort((a, b) => b.When.CompareTo(a.When));
                    if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
                    Post(delegate { recent = list; Render(); });
                });
            }

            // Uninstallers, help and readme links that installers drop next to the app
            static bool Extra(string name)
            {
                string n = name.ToLowerInvariant();
                foreach (var w in new[] { "uninstall", "hapus instal", "help", "bantuan", "readme", "read me", "manual", "documentation", "release notes", "website", "license" })
                    if (n.Contains(w)) return true;
                return false;
            }

            void IconLoop()
            {
                while (iconRun)
                {
                    AppEntry e = null;
                    lock (iconGate) if (iconQueue.Count > 0) e = iconQueue.Dequeue();
                    if (e == null) { iconWake.WaitOne(500); continue; }
                    Bitmap bmp;
                    lock (iconGate) iconCache.TryGetValue(e.Target, out bmp);
                    if (bmp == null)
                    {
                        bmp = ShellApp.Icon(e.Target, iconPx);
                        lock (iconGate) if (bmp != null) iconCache[e.Target] = bmp;
                    }
                    lock (iconGate) e.Icon = bmp;
                    Post(Render);
                }
            }

            Bitmap IconOf(AppEntry e)
            {
                lock (iconGate)
                {
                    if (e.Icon != null) return e.Icon;
                    Bitmap cached;
                    if (iconCache.TryGetValue(e.Target, out cached)) { e.Icon = cached; return cached; }
                }
                if (!e.Asked)
                {
                    e.Asked = true;
                    lock (iconGate) iconQueue.Enqueue(e);
                    iconWake.Set();
                }
                return null;
            }

            // Name starts with the query first, then a word that does, then anywhere
            void Filter()
            {
                results.Clear();
                string q = query.Trim();
                settingHits = SettingsPages.Find(q, 8);
                if (q.Length == 0 || catalog == null) return;
                var ranked = new List<KeyValuePair<int, AppEntry>>();
                foreach (var e in catalog)
                {
                    int at = e.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
                    if (at < 0) continue;
                    int rank = at == 0 ? 0 : char.IsWhiteSpace(e.Name[at - 1]) || e.Name[at - 1] == '-' ? 1 : 2;
                    ranked.Add(new KeyValuePair<int, AppEntry>(rank, e));
                }
                ranked.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : string.Compare(a.Value.Name, b.Value.Name, StringComparison.CurrentCultureIgnoreCase));
                foreach (var r in ranked) results.Add(r.Value);
            }

            float recentTop;

            // Where every tile sits, in content coordinates (before scrolling)
            float Arrange()
            {
                tiles.Clear();
                float x0 = (W - Cols * CellW) / 2, y = Head;
                if (query.Trim().Length > 0)
                {
                    // Apps, then the settings pages that match
                    y = results.Count > 0 ? Grid(results, x0, y, false) : y + 44;
                    if (settingHits.Count == 0) return y;
                    settingsTop = y + 10;
                    y = settingsTop + Head;
                    for (int i = 0; i < settingHits.Count; i++)
                        tiles.Add(new Tile { Entry = settingHits[i], Rect = new RectangleF(x0 + (i % 2) * (Cols * CellW / 2), y + (i / 2) * RowH, Cols * CellW / 2, RowH), Row = true });
                    return y + ((settingHits.Count + 1) / 2) * RowH + 8;
                }
                y = Grid(pins, x0, y, true);
                if (pins.Count == 0) y += 44;
                recentTop = y + 10;
                y = recentTop + Head;
                for (int i = 0; i < recent.Count; i++)
                    tiles.Add(new Tile { Entry = recent[i], Rect = new RectangleF(x0 + (i % 2) * (Cols * CellW / 2), y + (i / 2) * RowH, Cols * CellW / 2, RowH), Row = true });
                y += Math.Max(1, (recent.Count + 1) / 2) * RowH + 10;
                allTop = y;
                y += Head;
                if (catalog != null) y = Grid(catalog, x0, y, false);
                return y + 8;
            }

            float Grid(List<AppEntry> list, float x0, float y, bool pin)
            {
                for (int i = 0; i < list.Count; i++)
                    tiles.Add(new Tile { Entry = list[i], Rect = new RectangleF(x0 + (i % Cols) * CellW, y + (i / Cols) * CellH, CellW, CellH), Pin = pin });
                return y + ((list.Count + Cols - 1) / Cols) * CellH;
            }

            protected override void Backdrop(RectangleF r) { Shelf(r, Foot); }

            protected override void Draw()
            {
                contentH = Arrange();
                scroll = Math.Max(0, Math.Min(Math.Max(0, contentH - ViewH), scroll));
                DrawSearch();
                DrawContent();
                DrawFoot();
            }

            void DrawSearch()
            {
                var box = new RectangleF(Pad, 18, W - Pad * 2, 40);
                p.FillRound(box, 12, p.T.Fill);
                p.StrokeRound(box, 12, p.T.Line, 1f);
                p.Icon((char)0xE721, 15f, p.T.Sub, box.X + 22, box.Y + box.Height / 2);
                float tx = box.X + 42, cy = box.Y + box.Height / 2;
                var f = Ink.Semi(14f);
                if (query.Length == 0) p.Text(Lang.T("Cari aplikasi dan pengaturan"), f, p.T.Sub, tx, cy, box.Width - 56, StringAlignment.Near);
                else p.Text(query, f, p.T.Text, tx, cy, box.Width - 56, StringAlignment.Near);
                float caret = tx + (query.Length > 0 ? Math.Min(box.Width - 60, p.Measure(query, f)) + 1 : 0);
                g.DrawLine(p.P(p.T.Text, 1.4f), caret, cy - 9, caret, cy + 9);
            }

            void Header(string title, float y, string action, string id)
            {
                float cy = BodyTop - scroll + y - Head / 2;
                if (cy < BodyTop - Head || cy > H - Foot + Head) return;
                p.Text(title, Ink.Semi(14f), p.T.Text, Pad + 8, cy, 300, StringAlignment.Near);
                if (action == null) return;
                var f = Ink.Small(12f);
                float w = p.Measure(action, f) + 32;
                var b = new RectangleF(W - Pad - w, cy - 13, w, 26);
                hits.Add(new KeyValuePair<RectangleF, string>(b, id));
                p.FillRound(b, 8, hotId == id ? p.T.FillHover : p.T.Fill);
                p.Text(action, f, p.T.Text, b.X + 10, cy, w - 26, StringAlignment.Near);
                p.Icon((char)0xE76C, 9f, p.T.Sub, b.Right - 13, cy);
            }

            void DrawContent()
            {
                var st = g.Save();
                g.SetClip(new RectangleF(0, BodyTop - 2, W, ViewH + 2));
                if (query.Trim().Length > 0)
                {
                    Header(Lang.T("Aplikasi"), Head, null, null);
                    if (loading) Note(Lang.T("Memuat aplikasi…"), Head + 22);
                    else if (results.Count == 0) Note(Lang.F("Tidak ada aplikasi bernama \"{0}\"", query.Trim()), Head + 22);
                    if (settingHits.Count > 0) Header(Lang.T("Pengaturan"), settingsTop + Head, null, null);
                }
                else
                {
                    Header(Lang.T("Disematkan"), Head, Lang.T("Semua aplikasi"), "toall");
                    if (pins.Count == 0) Note(importing ? Lang.T("Memuat…") : Lang.T("Klik kanan aplikasi, lalu Sematkan ke Start"), Head + 22);
                    Header(Lang.T("Terbaru"), recentTop + Head, null, null);
                    if (recent.Count == 0) Note(Lang.T("Belum ada berkas terbaru"), recentTop + Head + RowH / 2);
                    Header(Lang.T("Semua aplikasi"), allTop + Head, null, null);
                    if (loading) Note(Lang.T("Memuat aplikasi…"), allTop + Head + 40);
                }
                for (int i = 0; i < tiles.Count; i++)
                {
                    var t = tiles[i];
                    var r = new RectangleF(t.Rect.X, t.Rect.Y + BodyTop - scroll, t.Rect.Width, t.Rect.Height);
                    if (r.Bottom < BodyTop || r.Top > H - Foot) continue;
                    hits.Add(new KeyValuePair<RectangleF, string>(r, "t" + i));
                    bool on = hot == i || (hot < 0 && sel == i);
                    if (on) p.FillRound(Inset(r, 3), 12, pressId == "t" + i ? p.T.FillPressed : hot == i ? p.T.FillHover : p.T.Fill);
                    var icon = IconOf(t.Entry);
                    if (t.Row)
                    {
                        var ir = new RectangleF(r.X + 14, r.Y + (RowH - 32) / 2, 32, 32);
                        if (icon != null) g.DrawImage(icon, ir); else p.FillRound(ir, 8, p.T.Fill);
                        p.Text(t.Entry.Name, Ink.Semi(12.5f), p.T.Text, r.X + 58, r.Y + RowH / 2 - 8, r.Width - 70, StringAlignment.Near);
                        p.Text(t.Entry.Sub, Ink.Small(11f), p.T.Sub, r.X + 58, r.Y + RowH / 2 + 9, r.Width - 70, StringAlignment.Near);
                    }
                    else
                    {
                        var ir = new RectangleF(r.X + (CellW - 40) / 2, r.Y + 12, 40, 40);
                        if (icon != null) g.DrawImage(icon, ir); else p.FillRound(ir, 10, p.T.Fill);
                        p.Text(t.Entry.Name, Ink.Small(11.5f), p.T.Text, r.X + CellW / 2, r.Y + 68, CellW - 10, StringAlignment.Center);
                    }
                }
                g.Restore(st);
                if (contentH > ViewH)
                {
                    float thumb = Math.Max(28, ViewH * ViewH / contentH), y = BodyTop + (ViewH - thumb) * scroll / Math.Max(1, contentH - ViewH);
                    p.FillRound(new RectangleF(W - 7, y, 3, thumb), 1.5f, p.T.Line);
                }
            }

            void Note(string text, float y)
            {
                p.TextCenter(text, Ink.Small(12f), p.T.Sub, W / 2, BodyTop - scroll + y);
            }

            static RectangleF Inset(RectangleF r, float d) { return new RectangleF(r.X + d, r.Y + d, r.Width - d * 2, r.Height - d * 2); }

            void DrawFoot()
            {
                float cy = H - Foot / 2;
                // Who is signed in; their menu has account and session actions
                var me = new RectangleF(Pad - 6, cy - 21, Math.Min(280, p.Measure(userName, Ink.Semi(13f)) + 70), 42);
                hits.Add(new KeyValuePair<RectangleF, string>(me, "me"));
                if (hotId == "me") p.FillRound(me, 12, p.T.FillHover);
                var face = new RectangleF(me.X + 8, cy - 16, 32, 32);
                if (avatar != null)
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddEllipse(face);
                        var s = g.Save();
                        g.SetClip(gp, CombineMode.Intersect);
                        g.DrawImage(avatar, face);
                        g.Restore(s);
                    }
                else
                {
                    p.Circle(face.X + 16, cy, 16, p.T.FillHover);
                    p.Icon((char)0xE77B, 14f, p.T.Sub, face.X + 16, cy);
                }
                p.Text(userName, Ink.Semi(13f), p.T.Text, face.Right + 12, cy, me.Right - face.Right - 18, StringAlignment.Near);
                if (query.Trim().Length > 0)
                {
                    var files = new RectangleF(me.Right + 8, cy - 16, 210, 32);
                    hits.Add(new KeyValuePair<RectangleF, string>(files, "files"));
                    if (hotId == "files") p.FillRound(files, 10, p.T.FillHover);
                    p.Icon((char)0xE8B7, 13f, p.T.Sub, files.X + 16, cy);
                    p.Text(Lang.F("Cari \"{0}\" di berkas", query.Trim()), Ink.Small(12f), p.T.Text, files.X + 32, cy, files.Width - 38, StringAlignment.Near);
                }
                var power = new RectangleF(W - Pad - 34, cy - 19, 38, 38);
                var gear = new RectangleF(power.X - 42, cy - 19, 38, 38);
                hits.Add(new KeyValuePair<RectangleF, string>(gear, "gear"));
                hits.Add(new KeyValuePair<RectangleF, string>(power, "power"));
                if (hotId == "gear") p.FillRound(gear, 10, p.T.FillHover);
                if (hotId == "power") p.FillRound(power, 10, p.T.FillHover);
                p.Icon((char)0xE713, 15f, hotId == "gear" ? p.T.Text : p.T.Sub, gear.X + 19, cy);
                p.Icon((char)0xE7E8, 15f, hotId == "power" ? p.T.Text : p.T.Sub, power.X + 19, cy);
                if (hotId == "gear") Tip(gear, Lang.T("Pengaturan Windows"));
                if (hotId == "power") Tip(power, Lang.T("Daya"));
            }

            static int TileOf(string id)
            {
                int i;
                return id != null && id.Length > 1 && id[0] == 't' && int.TryParse(id.Substring(1), out i) ? i : -1;
            }

            protected override void HoverMoved(string id) { hot = TileOf(id); }

            protected override void Clicked(string id, MouseButtons button)
            {
                int t = TileOf(id);
                if (t >= 0 && t < tiles.Count)
                {
                    if (button == MouseButtons.Right) TileMenu(tiles[t]);
                    else if (button == MouseButtons.Left) Launch(tiles[t].Entry, false);
                    return;
                }
                switch (id)
                {
                    case "toall": scroll = allTop; Render(); break;
                    case "files": Open("search-ms:query=" + Uri.EscapeDataString(query.Trim()), null); Done(); break;
                    case "gear": Open("ms-settings:", null); Done(); break;
                    case "power": PowerMenu(); break;
                    case "me": AccountMenu(); break;
                }
            }

            protected override void Wheel(string id, int delta)
            {
                scroll -= Math.Sign(delta) * CellH;
                hot = -1;
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys key)
            {
                switch (key)
                {
                    case Keys.Escape:
                        if (query.Length > 0) { SetQuery(""); return true; }
                        Done();
                        return true;
                    case Keys.Enter:
                        if (sel >= 0 && sel < tiles.Count) Launch(tiles[sel].Entry, false);
                        else if (query.Trim().Length > 0) { Open("search-ms:query=" + Uri.EscapeDataString(query.Trim()), null); Done(); }
                        return true;
                    case Keys.Control | Keys.Shift | Keys.Enter:
                        if (sel >= 0 && sel < tiles.Count) Launch(tiles[sel].Entry, true);
                        return true;
                    case Keys.Left: Pick(-1, 0); return true;
                    case Keys.Right: Pick(1, 0); return true;
                    case Keys.Up: Pick(0, -1); return true;
                    case Keys.Down: Pick(0, 1); return true;
                    case Keys.Back:
                        if (query.Length > 0) SetQuery(query.Substring(0, query.Length - 1));
                        return true;
                    case Keys.Control | Keys.Back:
                        SetQuery("");
                        return true;
                    case Keys.Control | Keys.V:
                        try { if (Clipboard.ContainsText()) SetQuery(query + Clipboard.GetText().Replace("\r", " ").Replace("\n", " ")); } catch (Exception) { }
                        return true;
                }
                return base.ProcessCmdKey(ref msg, key);
            }

            protected override void OnKeyPress(KeyPressEventArgs e)
            {
                base.OnKeyPress(e);
                if (e.KeyChar < ' ') return;
                SetQuery(query + e.KeyChar);
                e.Handled = true;
            }

            void SetQuery(string q)
            {
                query = q.Length > 80 ? q.Substring(0, 80) : q;
                sel = 0;
                scroll = 0;
                hot = -1;
                Filter();
                Render();
            }

            // Arrows: sideways in reading order, up and down to the nearest tile in that row
            void Pick(int dx, int dy)
            {
                if (tiles.Count == 0) return;
                sel = Math.Max(0, Math.Min(tiles.Count - 1, sel));
                if (dx != 0) sel = Math.Max(0, Math.Min(tiles.Count - 1, sel + dx));
                else
                {
                    var from = tiles[sel].Rect;
                    float cx = from.X + from.Width / 2;
                    int best = -1;
                    float bestRow = float.MaxValue, bestDx = float.MaxValue;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        var r = tiles[i].Rect;
                        float rowGap = dy > 0 ? r.Y - from.Y : from.Y - r.Y;
                        if (rowGap <= 1) continue;
                        float d = Math.Abs(r.X + r.Width / 2 - cx);
                        if (rowGap < bestRow - 1 || (Math.Abs(rowGap - bestRow) <= 1 && d < bestDx)) { best = i; bestRow = rowGap; bestDx = d; }
                    }
                    if (best >= 0) sel = best;
                }
                var t = tiles[sel].Rect;
                if (t.Y - Head < scroll) scroll = Math.Max(0, t.Y - Head);
                else if (t.Bottom > scroll + ViewH) scroll = t.Bottom - ViewH + 8;
                hot = -1;
                Render();
            }

            void Launch(AppEntry e, bool admin)
            {
                if (e.Uri == SettingsPages.TepiPrefs) { Done(); isle.OpenPrefs(); return; }
                if (e.Uri.Length > 0) Open(e.Uri, null);
                else ShellApp.Launch(e.Target, admin);
                Done();
            }

            // A pin the dock can match: the exe for desktop apps, the app id otherwise
            static string DockTarget(AppEntry e)
            {
                if (e.Id.Length == 0) return e.Target;
                string exe = ShellApp.ExpandKnown(e.Id);
                if (exe != e.Id && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(exe)) return exe;
                return e.Target;
            }

            void TileMenu(Tile t)
            {
                var e = t.Entry;
                var m = new ContextMenuStrip();
                m.Items.Add(new ToolStripMenuItem(e.Name) { Enabled = false });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Buka"), null, delegate { Launch(e, false); });
                if (e.Uri.Length > 0) { ShowMenu(m); return; }
                if (e.File)
                {
                    m.Items.Add(Lang.T("Buka lokasi berkas"), null, delegate { Open("explorer.exe", "/select,\"" + e.Target + "\""); Done(); });
                    ShowMenu(m);
                    return;
                }
                if (!e.Target.Contains("!")) m.Items.Add(Lang.T("Jalankan sebagai administrator"), null, delegate { Launch(e, true); });
                var targets = PinTargets();
                bool pinned = targets.Exists(x => string.Equals(x, e.Target, StringComparison.OrdinalIgnoreCase));
                if (pinned)
                {
                    m.Items.Add(Lang.T("Lepas dari Start"), null, delegate { targets.RemoveAll(x => string.Equals(x, e.Target, StringComparison.OrdinalIgnoreCase)); SavePins(targets); });
                    if (t.Pin) m.Items.Add(Lang.T("Pindah ke depan"), null, delegate { targets.RemoveAll(x => string.Equals(x, e.Target, StringComparison.OrdinalIgnoreCase)); targets.Insert(0, e.Target); SavePins(targets); });
                }
                else m.Items.Add(Lang.T("Sematkan ke Start"), null, delegate { targets.Add(e.Target); SavePins(targets); });
                string dt = DockTarget(e);
                if (dock.IsPinned(dt)) m.Items.Add(Lang.T("Lepas dari Dock"), null, delegate { dock.SetPinned(dt, false); });
                else m.Items.Add(Lang.T("Sematkan di Dock"), null, delegate { dock.SetPinned(dt, true); });
                ShowMenu(m);
            }

            void AccountMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Ubah pengaturan akun"), null, delegate { Open("ms-settings:yourinfo", null); Done(); });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Kunci"), null, delegate { Done(); SessionPower.Lock(); });
                m.Items.Add(Lang.T("Keluar"), null, delegate { Done(); SessionPower.SignOut(); });
                ShowMenu(m);
            }

            void PowerMenu()
            {
                var m = new ContextMenuStrip();
                m.Items.Add(Lang.T("Tidur"), null, delegate { Done(); SessionPower.Sleep(); });
                m.Items.Add(Lang.T("Mulai ulang"), null, delegate { Done(); SessionPower.Restart(); });
                m.Items.Add(Lang.T("Matikan"), null, delegate { Done(); SessionPower.ShutDown(); });
                ShowMenu(m);
            }
        }
    }
}
