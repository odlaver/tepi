using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
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
            public bool File, Folder, Asked, Extra;
            // Born: arrived from the index, fades in
            public DateTime When, IconUtc, Born;
            public Bitmap Icon;
            // Search: folder shown under a file, ranking
            public string Where = "", IconKey;
            public MatchKey Key;
            public int Score;
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

            float scroll, scrollTo, allTop, contentH;
            Bitmap avatar;
            string query = "", userName = "";
            int sel, hot = -1;
            bool loading;
            List<AppEntry> pins = new List<AppEntry>(), recent = new List<AppEntry>();

            // Search: best match, then apps, files, settings
            static readonly string[] Filters = { "Semua", "Aplikasi", "Berkas", "Pengaturan" };
            MatchQuery mq = new MatchQuery("");
            int filter;
            AppEntry best;
            readonly List<AppEntry> results = new List<AppEntry>(), fileHits = new List<AppEntry>(), settingHits = new List<AppEntry>();
            readonly List<AppEntry> fileCands = new List<AppEntry>();
            List<AppEntry> indexed = new List<AppEntry>();
            readonly Dictionary<string, AppEntry> fileSeen = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            IndexSearch index;
            // One shot: the index gets 1.5 s
            readonly System.Windows.Forms.Timer fileWait = new System.Windows.Forms.Timer { Interval = IndexSearch.DebounceMs + 1500 };
            int fileGen, listSig;
            bool filesPending, filesFailed, selSnap = true, fileSnap;
            AppEntry keepSel, selShown;
            DateTime listUtc = DateTime.MinValue, fileUtc = DateTime.MinValue;
            float filesTop;
            sealed class Label { public float Y; public string Text; public bool Files; }
            readonly List<Label> labels = new List<Label>();
            string note;
            float noteY;
            readonly float[] chipX0 = new float[4], chipW = new float[4];

            // Highlight glide, filter pill, files reveal
            readonly Spring selX = new Spring(0, 1, 1, 0.05f), selY = new Spring(0, 1, 1, 0.05f), selW = new Spring(0, 1, 1, 0.05f), selH = new Spring(0, 1, 1, 0.05f);
            readonly Spring selVis = new Spring(1, 1, 1, 0.004f), pill = new Spring(0, 1, 1, 0.002f), fileH = new Spring(0, 1, 1, 0.05f), searchK = new Spring(0, 1, 1, 0.002f);
            readonly Spring[] motion;

            // Everything on the card this frame that can be picked
            // Group: -1 browse, 0 best, 1-3 sections
            sealed class Tile { public AppEntry Entry; public RectangleF Rect; public bool Row, Pin; public int Group = -1; }
            readonly List<Tile> tiles = new List<Tile>();
            Thread iconThread;
            readonly Queue<AppEntry> iconQueue = new Queue<AppEntry>();
            readonly AutoResetEvent iconWake = new AutoResetEvent(false);
            volatile bool iconRun = true;
            int iconPx = 48;

            const float Pad = 20, CellW = 100, CellH = 90, RowH = 56, Head = 34, Foot = 64;
            const float ChipsH = 36, SecHead = 30, SRow = 48, BestH = 60;
            const int Cols = 6;
            // Chip row slides in while searching
            float BodyTop { get { return 72 + ChipsH * Ease.Clamp01(searchK.Value); } }
            float ViewH { get { return H - BodyTop - Foot; } }
            bool Searching { get { return query.Trim().Length > 0; } }

            [DllImport("secur32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserNameEx(int format, StringBuilder name, ref int size);
            [DllImport("shell32.dll", EntryPoint = "#261", CharSet = CharSet.Unicode)] static extern int SHGetUserPicturePath(string user, uint flags, StringBuilder path, int len);

            public StartCard(Island isle, DockWindow dock) : base(isle, dock, "start", "Tepi Start")
            {
                W = 640;
                H = 720;
                motion = new[] { selX, selY, selW, selH, selVis, pill, fileH, searchK };
                foreach (var s in new[] { selX, selY, selW, selH }) Springs.Tune(s, 0.16f, 1f);
                Springs.Tune(selVis, 0.12f, 1f);
                Springs.Tune(pill, Springs.SnappyResponse, Springs.SnappyDamping);
                Springs.Tune(fileH, 0.22f, 1f);
                Springs.Tune(searchK, 0.2f, 1f);
                fileWait.Tick += delegate { fileWait.Stop(); FilesTimedOut(); };
            }

            protected override void Teardown()
            {
                iconRun = false;
                iconWake.Set();
                fileWait.Stop();
                fileWait.Dispose();
                if (index != null) index.Dispose();
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
                index = new IndexSearch((gen, found) => Post(() => FilesArrived(gen, found)));
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
                            list.Add(new AppEntry { Name = Path.GetFileName(target), Target = target, File = true, When = f.LastWriteTimeUtc, Sub = Fmt.Ago(f.LastWriteTimeUtc), Where = FolderLabel(target) });
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
                    Post(delegate
                    {
                        recent = list;
                        recentLoaded = true;
                        if (Searching) Filter();
                        // Room reserved at open shrinks or grows smoothly
                        recentRows.Target = Math.Max(1, (list.Count + 1) / 2);
                        if (Anim.Reduced) recentRows.Snap(recentRows.Target);
                        Animate();
                    });
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
                    lock (iconGate) iconCache.TryGetValue(e.IconKey, out bmp);
                    if (bmp == null)
                    {
                        bmp = ShellApp.Icon(e.Target, iconPx);
                        lock (iconGate) if (bmp != null) iconCache[e.IconKey] = bmp;
                    }
                    lock (iconGate) { e.Icon = bmp; e.IconUtc = Anim.Reduced ? DateTime.MinValue : DateTime.UtcNow; }
                    iconUtc = DateTime.UtcNow;
                    Post(Animate);
                }
            }

            // Documents share their type's icon
            static readonly string[] OwnIcons = { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };

            static string IconKeyOf(AppEntry e)
            {
                if (e.IconKey != null) return e.IconKey;
                string k = e.Target;
                if (e.File && !e.Folder)
                {
                    string ext = Path.GetExtension(e.Target).ToLowerInvariant();
                    if (ext.Length > 1 && Array.IndexOf(OwnIcons, ext) < 0) k = "type:" + ext;
                }
                return e.IconKey = k;
            }

            Bitmap IconOf(AppEntry e)
            {
                string key = IconKeyOf(e);
                lock (iconGate)
                {
                    if (e.Icon != null) return e.Icon;
                    Bitmap cached;
                    if (iconCache.TryGetValue(key, out cached)) { e.Icon = cached; return cached; }
                }
                if (!e.Asked)
                {
                    e.Asked = true;
                    lock (iconGate) iconQueue.Enqueue(e);
                    iconWake.Set();
                }
                return null;
            }

            // Rank sources, lift out best match
            void Filter()
            {
                results.Clear();
                fileHits.Clear();
                settingHits.Clear();
                best = null;
                if (!Searching) return;
                string learned = StartUsage.PickFor(mq.Lower);
                if (catalog != null && filter <= 1) Rank(catalog, results, learned, 0);
                if (filter == 0 || filter == 2)
                {
                    fileCands.Clear();
                    foreach (var e in recent) if (e.File) fileCands.Add(e);
                    int own = fileCands.Count;
                    foreach (var e in indexed)
                    {
                        bool dup = false;
                        for (int i = 0; i < own && !dup; i++) dup = string.Equals(fileCands[i].Target, e.Target, StringComparison.OrdinalIgnoreCase);
                        if (!dup) fileCands.Add(e);
                    }
                    // Apps outrank a file of the same name
                    Rank(fileCands, fileHits, learned, -120);
                }
                if (filter == 0 || filter == 3) Rank(SettingsPages.All(), settingHits, learned, -15);
                if (filter != 0) return;
                List<AppEntry> from = null;
                foreach (var list in new[] { results, settingHits, fileHits })
                    if (list.Count > 0 && list[0].Score >= AppMatch.Strong && (best == null || list[0].Score > best.Score)) { best = list[0]; from = list; }
                if (from != null) from.RemoveAt(0);
            }

            static readonly Comparison<AppEntry> ByScore = (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

            void Rank(List<AppEntry> source, List<AppEntry> into, string learned, int bias)
            {
                foreach (var e in source)
                {
                    if (e.Key == null)
                    {
                        e.Key = AppMatch.Prepare(e.File && !e.Folder ? Path.GetFileNameWithoutExtension(e.Name) : e.Name, null, !e.File && e.Uri.Length == 0);
                        e.Extra = !e.File && e.Uri.Length == 0 && Extra(e.Name);
                    }
                    int s = AppMatch.Score(mq, e.Key);
                    // Uninstallers only when asked by name
                    if (s <= 0 || (e.Extra && s < AppMatch.WordStart - 30)) continue;
                    string key = UsageKey(e);
                    int boost = StartUsage.Boost(key);
                    // Habit lifts weak matches less
                    s += s >= AppMatch.Inside - 60 ? boost : boost / 2;
                    if (learned != null && string.Equals(learned, key, StringComparison.OrdinalIgnoreCase)) s += 220;
                    if (e.File && e.When > DateTime.MinValue) s += (int)(30 / (1 + Math.Max(0, (DateTime.UtcNow - e.When).TotalDays)));
                    // Windows' page wins a tie with Tepi's
                    if (e.Uri.StartsWith(SettingsPages.TepiPrefs, StringComparison.Ordinal)) s -= 5;
                    e.Score = s + bias;
                    into.Add(e);
                }
                into.Sort(ByScore);
            }

            static string UsageKey(AppEntry e) { return e.Id.Length > 0 ? e.Id : e.Uri.Length > 0 ? e.Uri : e.Target; }

            // "Downloads › … › Kuliah › Tugas"
            static string FolderLabel(string path)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path) ?? "", home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (dir.StartsWith(home, StringComparison.OrdinalIgnoreCase)) dir = dir.Substring(home.Length).TrimStart('\\');
                    if (dir.Length == 0) return Path.GetFileName(home);
                    var parts = dir.Split('\\');
                    if (parts.Length > 3) return parts[0] + " › … › " + parts[parts.Length - 2] + " › " + parts[parts.Length - 1];
                    return string.Join(" › ", parts);
                }
                catch (Exception) { return ""; }
            }

            void AskFiles()
            {
                if (index == null) return;
                fileGen++;
                filesPending = true;
                index.Ask(query.Trim(), fileGen);
                fileWait.Stop();
                fileWait.Start();
            }

            void FilesArrived(int gen, List<FoundFile> found)
            {
                if (gen != fileGen || !filesPending || IsDisposed) return;
                filesPending = false;
                fileWait.Stop();
                var now = DateTime.UtcNow;
                var list = new List<AppEntry>();
                if (found != null)
                    foreach (var f in found)
                    {
                        AppEntry e;
                        if (!fileSeen.TryGetValue(f.Path, out e))
                        {
                            e = new AppEntry { Name = f.Name, Target = f.Path, File = true, Folder = f.Folder, When = f.Modified, Where = FolderLabel(f.Path), Born = Anim.Reduced ? DateTime.MinValue : now };
                            fileSeen[f.Path] = e;
                        }
                        list.Add(e);
                    }
                filesFailed = found == null;
                indexed = list;
                fileUtc = now;
                Refilter();
            }

            // Slow or missing index: show none
            void FilesTimedOut()
            {
                if (!filesPending || IsDisposed) return;
                filesPending = false;
                filesFailed = true;
                indexed = new List<AppEntry>();
                Refilter();
            }

            // New results, same row stays picked
            void Refilter()
            {
                if (sel >= 0 && sel < tiles.Count) keepSel = tiles[sel].Entry;
                Filter();
                Render();
                Animate();
            }

            // Cross-fade only when the visible rows change
            void MarkList()
            {
                int sig = best == null ? 17 : RuntimeHelpers.GetHashCode(best);
                foreach (var list in new[] { results, fileHits, settingHits })
                    for (int i = 0; i < list.Count && i < 6; i++) sig = sig * 31 + RuntimeHelpers.GetHashCode(list[i]);
                if (sig == listSig) return;
                listSig = sig;
                if (!Anim.Reduced) listUtc = DateTime.UtcNow;
            }

            float ListK() { return Anim.Reduced ? 1 : Ease.OutCubic((float)(DateTime.UtcNow - listUtc).TotalSeconds / 0.12f); }

            float BornK(AppEntry e) { return Anim.Reduced ? 1 : Ease.OutCubic((float)(DateTime.UtcNow - e.Born).TotalSeconds / 0.16f); }

            float recentTop;
            readonly Spring recentRows = MakeRows();
            bool recentLoaded;
            DateTime iconUtc;

            static Spring MakeRows()
            {
                var s = new Spring(3, 1, 1, 0.01f);
                Springs.Tune(s, 0.28f, 1f);
                return s;
            }

            float IconK(AppEntry e) { return Ease.OutCubic((float)(DateTime.UtcNow - e.IconUtc).TotalSeconds / 0.16f); }

            // Where every tile sits, in content coordinates (before scrolling)
            float Arrange()
            {
                tiles.Clear();
                labels.Clear();
                note = null;
                float x0 = (W - Cols * CellW) / 2, y = Head;
                if (Searching) return ArrangeSearch();
                y = Grid(pins, x0, y, true);
                if (pins.Count == 0) y += 44;
                recentTop = y + 10;
                y = recentTop + Head;
                for (int i = 0; i < recent.Count; i++)
                    tiles.Add(new Tile { Entry = recent[i], Rect = new RectangleF(x0 + (i % 2) * (Cols * CellW / 2), y + (i / 2) * RowH, Cols * CellW / 2, RowH), Row = true });
                y += recentRows.Value * RowH + 10;
                allTop = y;
                y += Head;
                if (catalog != null) y = Grid(catalog, x0, y, false);
                return y + 8;
            }

            float ArrangeSearch()
            {
                float x = Pad - 6, w = W - 2 * x, y = 4;
                if (filter == 0 && best != null)
                {
                    labels.Add(new Label { Y = y, Text = Lang.T("Terbaik") });
                    y += SecHead;
                    tiles.Add(new Tile { Entry = best, Rect = new RectangleF(x, y, w, BestH), Row = true, Group = 0 });
                    y += BestH + 6;
                }
                if (filter <= 1) y = Rows(1, Lang.T("Aplikasi"), results, filter == 0 ? 6 : 60, x, y, w);
                if (filter == 0 || filter == 2)
                {
                    // Late files: group grows, rows glide
                    int n = Math.Min(filter == 0 ? 5 : 30, fileHits.Count);
                    float want = n > 0 ? SecHead + n * SRow + 6 : 0;
                    if (fileH.Target != want)
                    {
                        fileH.Target = want;
                        if (Anim.Reduced || fileSnap) fileH.Snap(want);
                        else Animate();
                    }
                    filesTop = y;
                    if (n > 0)
                    {
                        labels.Add(new Label { Y = y, Text = Lang.T("Berkas"), Files = true });
                        for (int i = 0; i < n; i++)
                            tiles.Add(new Tile { Entry = fileHits[i], Rect = new RectangleF(x, y + SecHead + i * SRow, w, SRow), Row = true, Group = 2 });
                    }
                    y += Math.Max(0, fileH.Value);
                }
                fileSnap = false;
                if (filter == 0 || filter == 3) y = Rows(3, Lang.T("Pengaturan"), settingHits, filter == 0 ? 4 : 99, x, y, w);
                string q = query.Trim();
                bool waiting = filesPending && !filesFailed;
                if (tiles.Count == 0 && fileH.Target == 0)
                {
                    if (filter <= 1 && loading) note = Lang.T("Memuat aplikasi…");
                    else if (filter == 2 && filesFailed) note = Lang.T("Pencarian berkas tidak tersedia");
                    else if ((filter == 0 || filter == 2) && waiting) note = Lang.T("Mencari…");
                    else note = Lang.F("Tidak ada hasil untuk \"{0}\"", q);
                    noteY = y + 26;
                    y += 52;
                }
                return y + 8;
            }

            float Rows(int group, string title, List<AppEntry> list, int cap, float x, float y, float w)
            {
                if (list.Count == 0) return y;
                labels.Add(new Label { Y = y, Text = title });
                y += SecHead;
                int n = Math.Min(cap, list.Count);
                for (int i = 0; i < n; i++)
                    tiles.Add(new Tile { Entry = list[i], Rect = new RectangleF(x, y + i * SRow, w, SRow), Row = true, Group = group });
                return y + n * SRow + 6;
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
                scrollTo = ClampScroll(scrollTo);
                scroll = ClampScroll(scroll);
                FollowSel();
                DrawSearch();
                DrawChips();
                DrawContent();
                DrawFoot();
            }

            // Highlight glides to a new pick
            void FollowSel()
            {
                if (keepSel != null)
                {
                    int at = -1;
                    for (int i = 0; i < tiles.Count && at < 0; i++) if (tiles[i].Entry == keepSel) at = i;
                    sel = Math.Max(0, at);
                    keepSel = null;
                }
                Aim(selVis, hot < 0 ? 1 : 0, false);
                if (tiles.Count == 0) { selShown = null; return; }
                sel = Math.Max(0, Math.Min(tiles.Count - 1, sel));
                var r = Inset(tiles[sel].Rect, 3);
                bool moved = tiles[sel].Entry != selShown;
                bool snap = selSnap || selShown == null || Anim.Reduced;
                selShown = tiles[sel].Entry;
                selSnap = false;
                if (snap) { selX.Snap(r.X); selY.Snap(r.Y); selW.Snap(r.Width); selH.Snap(r.Height); return; }
                if (moved) { Aim(selX, r.X, false); Aim(selY, r.Y, false); Aim(selW, r.Width, false); Aim(selH, r.Height, false); return; }
                Ride(selX, r.X);
                Ride(selY, r.Y);
                Ride(selW, r.Width);
                Ride(selH, r.Height);
            }

            void Aim(Spring s, float to, bool snap)
            {
                if (s.Target == to) return;
                s.Target = to;
                if (snap || Anim.Reduced) s.Snap(to);
                else Animate();
            }

            // Layout shifted under the pick: move with it
            static void Ride(Spring s, float to)
            {
                float d = to - s.Target;
                s.Value += d;
                s.Target = to;
            }

            float ClampScroll(float y) { return Math.Max(0, Math.Min(Math.Max(0, contentH - ViewH), y)); }

            void ScrollTo(float y)
            {
                scrollTo = ClampScroll(y);
                if (Anim.Reduced) scroll = scrollTo;
                else Animate();
            }

            protected override bool Animating(float dt)
            {
                recentRows.Step(dt);
                var now = DateTime.UtcNow;
                bool more = !recentRows.Settled || (now - iconUtc).TotalSeconds < 0.18 || (now - listUtc).TotalSeconds < 0.14 || (now - fileUtc).TotalSeconds < 0.2;
                foreach (var s in motion) { s.Step(dt); more |= !s.Settled; }
                float d = scrollTo - scroll;
                if (Math.Abs(d) < 0.5f) { scroll = scrollTo; return more; }
                scroll += d * (1 - (float)Math.Exp(-dt * 18));
                return true;
            }

            void DrawSearch()
            {
                var box = new RectangleF(Pad, 18, W - Pad * 2, 40);
                p.FillRound(box, 12, p.T.Fill);
                p.StrokeRound(box, 12, p.T.Line, 1f);
                p.Icon((char)0xE721, 15f, p.T.Sub, box.X + 22, box.Y + box.Height / 2);
                float tx = box.X + 42, cy = box.Y + box.Height / 2;
                var f = Ink.Semi(14f);
                if (query.Length == 0) p.Text(Lang.T("Cari aplikasi, berkas, dan pengaturan"), f, p.T.Sub, tx, cy, box.Width - 56, StringAlignment.Near);
                else p.Text(query, f, p.T.Text, tx, cy, box.Width - 56, StringAlignment.Near);
                float caret = tx + (query.Length > 0 ? Math.Min(box.Width - 60, p.Measure(query, f)) + 1 : -2);
                g.DrawLine(p.P(p.T.Text, 1.4f), caret, cy - 9, caret, cy + 9);
            }

            // Filters: a pill slides to the chosen one
            void DrawChips()
            {
                float k = Ease.Clamp01(searchK.Value);
                if (k <= 0.01f) return;
                float keep = p.Alpha, h = 28, y = 66 - (1 - k) * 6;
                p.Alpha = keep * k;
                var f = Ink.Semi(12f);
                float x = Pad + 2;
                for (int i = 0; i < Filters.Length; i++)
                {
                    chipX0[i] = x;
                    chipW[i] = p.Measure(Lang.T(Filters[i]), f) + 28;
                    x += chipW[i];
                }
                var track = new RectangleF(Pad, y, x + 2 - Pad, h);
                p.FillRound(track, h / 2, p.T.Fill);
                float v = Math.Max(0, Math.Min(Filters.Length - 1, pill.Value));
                int a = (int)v, b = Math.Min(Filters.Length - 1, a + 1);
                float t = v - a;
                var thumb = new RectangleF(Ease.Lerp(chipX0[a], chipX0[b], t), y + 2, Ease.Lerp(chipW[a], chipW[b], t), h - 4);
                for (int i = 0; i < Filters.Length; i++)
                {
                    string id = "f" + i;
                    var cell = new RectangleF(chipX0[i], y + 2, chipW[i], h - 4);
                    if (k > 0.5f) Hit(cell, id);
                    float hk = HotK(id);
                    if (i != filter && (hk > 0 || pressId == id)) p.FillRound(pressId == id ? Inset(cell, 1.5f) : cell, (h - 4) / 2, pressId == id ? p.T.FillPressed : Ink.A(p.T.FillHover, hk));
                }
                // Held: the pill sinks a little
                p.FillRound(pressId == "f" + filter ? Inset(thumb, 1.5f) : thumb, (h - 4) / 2, Ink.Mix(p.T.FillHover, p.T.Text, 0.12f));
                for (int i = 0; i < Filters.Length; i++)
                {
                    float near = Ease.Clamp01(1 - Math.Abs(pill.Value - i));
                    p.TextCenter(Lang.T(Filters[i]), f, Ink.Mix(Ink.Mix(p.T.Sub, p.T.Text, HotK("f" + i)), p.T.Text, near), chipX0[i] + chipW[i] / 2, y + h / 2);
                }
                p.Text(Lang.T("Tab untuk ganti filter"), Ink.Small(11f), p.T.Faint, W - Pad, y + h / 2, W - x - Pad - 16, StringAlignment.Far);
                p.Alpha = keep;
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
                float top = BodyTop;
                g.SetClip(new RectangleF(0, top - 2, W, ViewH + 2));
                float keep = p.Alpha, lift = 0;
                // Growing files group: clip at edge
                float filesEdge = top - scroll + filesTop + Math.Max(0, fileH.Value);
                if (Searching)
                {
                    float lk = ListK();
                    lift = (1 - lk) * 3;
                    p.Alpha = keep * (0.55f + 0.45f * lk);
                    foreach (var l in labels)
                    {
                        float cy = top - scroll + l.Y + SecHead / 2 + 2 + lift;
                        if (l.Files && cy > filesEdge) continue;
                        p.Text(l.Text, Ink.SmallSemi(11.5f), p.T.Sub, Pad + 4, cy, 300, StringAlignment.Near);
                    }
                    if (note != null) Note(note, noteY);
                }
                else
                {
                    Header(Lang.T("Disematkan"), Head, Lang.T("Semua aplikasi"), "toall");
                    if (pins.Count == 0) Note(importing ? Lang.T("Memuat…") : Lang.T("Klik kanan aplikasi, lalu Sematkan ke Start"), Head + 22);
                    Header(Lang.T("Terbaru"), recentTop + Head, null, null);
                    if (recentLoaded && recent.Count == 0) Note(Lang.T("Belum ada berkas terbaru"), recentTop + Head + RowH / 2);
                    Header(Lang.T("Semua aplikasi"), allTop + Head, null, null);
                    if (loading) Note(Lang.T("Memuat aplikasi…"), allTop + Head + 40);
                }
                // Keyboard pick, gliding between rows
                float vis = Ease.Clamp01(selVis.Value);
                if (tiles.Count > 0 && vis > 0.01f)
                {
                    var hl = new RectangleF(selX.Value, selY.Value + top - scroll + lift, selW.Value, selH.Value);
                    bool cut = tiles[Math.Min(sel, tiles.Count - 1)].Group == 2 && hl.Bottom > filesEdge;
                    if (!cut) p.FillRound(hl, 12, Ink.A(p.T.Fill, vis));
                }
                float fade = p.Alpha;
                for (int i = 0; i < tiles.Count; i++)
                {
                    var t = tiles[i];
                    var r = new RectangleF(t.Rect.X, t.Rect.Y + top - scroll + lift, t.Rect.Width, t.Rect.Height);
                    if (r.Bottom < top || r.Top > H - Foot) continue;
                    if (t.Group == 2 && r.Top >= filesEdge) continue;
                    hits.Add(new KeyValuePair<RectangleF, string>(r, "t" + i));
                    var inner = g.Save();
                    if (t.Group == 2 && r.Bottom > filesEdge) g.IntersectClip(new RectangleF(0, r.Top, W, filesEdge - r.Top));
                    if (t.Entry.Born > DateTime.MinValue) p.Alpha = fade * BornK(t.Entry);
                    float hk = Math.Max(HotK("t" + i), HotK("o" + i));
                    bool down = pressId == "t" + i;
                    // Held: the row sinks a little
                    if (down)
                    {
                        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = t.Group >= 0 ? 0.985f : 0.95f;
                        g.TranslateTransform(cx, cy);
                        g.ScaleTransform(k, k);
                        g.TranslateTransform(-cx, -cy);
                        p.FillRound(Inset(r, 3), 12, p.T.FillPressed);
                    }
                    else if (hk > 0) p.FillRound(Inset(r, 3), 12, Ink.A(p.T.FillHover, hk));
                    var icon = IconOf(t.Entry);
                    float ik = icon != null ? IconK(t.Entry) : 0, keepA = p.Alpha;
                    if (t.Group >= 0) DrawResult(t, r, i, icon, ik, keepA);
                    else if (t.Row)
                    {
                        var ir = new RectangleF(r.X + 14, r.Y + (RowH - 32) / 2, 32, 32);
                        IconIn(icon, ir, 8, ik, keepA);
                        p.Text(t.Entry.Name, Ink.Semi(12.5f), p.T.Text, r.X + 58, r.Y + RowH / 2 - 8, r.Width - 70, StringAlignment.Near);
                        p.Text(t.Entry.Sub, Ink.Small(11f), p.T.Sub, r.X + 58, r.Y + RowH / 2 + 9, r.Width - 70, StringAlignment.Near);
                    }
                    else
                    {
                        var ir = new RectangleF(r.X + (CellW - 40) / 2, r.Y + 12, 40, 40);
                        IconIn(icon, ir, 10, ik, keepA);
                        p.Text(t.Entry.Name, Ink.Small(11.5f), p.T.Text, r.X + CellW / 2, r.Y + 68, CellW - 10, StringAlignment.Center);
                    }
                    p.Alpha = fade;
                    g.Restore(inner);
                }
                p.Alpha = keep;
                g.Restore(st);
                if (contentH > ViewH)
                {
                    float thumb = Math.Max(28, ViewH * ViewH / contentH), y = top + (ViewH - thumb) * scroll / Math.Max(1, contentH - ViewH);
                    p.FillRound(new RectangleF(W - 7, y, 3, thumb), 1.5f, p.T.Line);
                }
            }

            // One search row: icon, name, where it lives
            void DrawResult(Tile t, RectangleF r, int i, Bitmap icon, float ik, float keepA)
            {
                var e = t.Entry;
                bool big = t.Group == 0;
                float size = big ? 40 : 28, cy = r.Y + r.Height / 2;
                var ir = new RectangleF(r.X + 14, cy - size / 2, size, size);
                IconIn(icon, ir, big ? 10 : 7, ik, keepA);
                if (icon == null && e.File && ik <= 0) p.Icon(e.Folder ? (char)0xE8B7 : (char)0xE8A5, big ? 18f : 14f, p.T.Sub, ir.X + size / 2, cy);
                string sub = e.File ? e.Where : e.Uri.Length > 0 ? e.Sub : big ? Lang.T("Aplikasi") : "";
                // Files: open the folder from the row
                bool folderBtn = e.File && (hot == i || (hot < 0 && sel == i));
                float tx = ir.Right + 12, tw = r.Right - tx - (folderBtn ? 50 : 14);
                var nf = big ? Ink.Semi(14.5f) : Ink.Semi(13f);
                if (sub.Length == 0) p.Text(e.Name, nf, p.T.Text, tx, cy, tw, StringAlignment.Near);
                else
                {
                    p.Text(e.Name, nf, p.T.Text, tx, cy - (big ? 9 : 8), tw, StringAlignment.Near);
                    p.Text(sub, Ink.Small(big ? 11.5f : 11f), p.T.Sub, tx, cy + (big ? 10 : 9), tw, StringAlignment.Near);
                }
                if (!folderBtn) return;
                string id = "o" + i;
                var b = new RectangleF(r.Right - 46, cy - 16, 34, 32);
                Hit(b, id);
                float hk = HotK(id);
                if (pressId == id) p.FillRound(Inset(b, 1.5f), 9, p.T.FillPressed);
                else if (hk > 0) p.FillRound(b, 9, Ink.A(p.T.FillHover, hk));
                p.Icon((char)0xE838, 14f, Ink.Mix(p.T.Sub, p.T.Text, hk), b.X + b.Width / 2, cy);
                if (hotId == id) Tip(b, Lang.T("Buka lokasi berkas"));
            }

            // Placeholder hands over to the real icon
            void IconIn(Bitmap icon, RectangleF ir, float radius, float k, float keep)
            {
                if (k < 1) { p.Alpha = keep * (1 - k); p.FillRound(ir, radius, p.T.Fill); }
                if (icon != null && k > 0) { p.Alpha = keep * k; p.Picture(icon, ir); }
                p.Alpha = keep;
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
                        p.Picture(avatar, face);
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
                    p.Text(Lang.T("Cari di File Explorer"), Ink.Small(12f), p.T.Text, files.X + 32, cy, files.Width - 38, StringAlignment.Near);
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

            // Ids: t tile, o folder, f filter
            static int IndexOf(string id, char kind)
            {
                int i;
                return id != null && id.Length > 1 && id[0] == kind && int.TryParse(id.Substring(1), out i) ? i : -1;
            }

            protected override void HoverMoved(string id)
            {
                int t = IndexOf(id, 't');
                hot = t >= 0 ? t : IndexOf(id, 'o');
            }

            protected override void Clicked(string id, MouseButtons button)
            {
                int t = IndexOf(id, 't'), o = IndexOf(id, 'o'), f = IndexOf(id, 'f');
                if (t >= 0 && t < tiles.Count)
                {
                    if (button == MouseButtons.Right) TileMenu(tiles[t]);
                    else if (button == MouseButtons.Left) Launch(tiles[t].Entry, false);
                    return;
                }
                if (o >= 0 && o < tiles.Count) { ShowFolder(tiles[o].Entry); return; }
                if (f >= 0) { SetFilter(f); return; }
                switch (id)
                {
                    case "toall": ScrollTo(allTop); break;
                    case "files": Open("search-ms:query=" + Uri.EscapeDataString(query.Trim()), null); Done(); break;
                    case "gear": Open("ms-settings:", null); Done(); break;
                    case "power": PowerMenu(); break;
                    case "me": AccountMenu(); break;
                }
            }

            protected override void Wheel(string id, int delta)
            {
                // Touchpads send small deltas: scale, don't step
                ScrollTo(scrollTo - delta / 120f * CellH * 0.8f);
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys key)
            {
                if (Leaving) return true;
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
                    case Keys.Control | Keys.Enter:
                        if (sel < 0 || sel >= tiles.Count) return true;
                        if (tiles[sel].Entry.File) ShowFolder(tiles[sel].Entry);
                        else Launch(tiles[sel].Entry, false);
                        return true;
                    case Keys.Control | Keys.Shift | Keys.Enter:
                        if (sel >= 0 && sel < tiles.Count) Launch(tiles[sel].Entry, true);
                        return true;
                    case Keys.Tab:
                    case Keys.Control | Keys.Right:
                        if (Searching) SetFilter(filter + 1);
                        return true;
                    case Keys.Shift | Keys.Tab:
                    case Keys.Control | Keys.Left:
                        if (Searching) SetFilter(filter - 1);
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
                if (e.KeyChar < ' ' || Leaving) return;
                SetQuery(query + e.KeyChar);
                e.Handled = true;
            }

            void SetQuery(string q)
            {
                bool was = Searching;
                query = q.Length > 80 ? q.Substring(0, 80) : q;
                mq = new MatchQuery(query);
                sel = 0;
                selSnap = true;
                scroll = scrollTo = 0;
                hot = -1;
                Aim(searchK, Searching ? 1 : 0, false);
                if (Searching) AskFiles();
                else
                {
                    // Back to browsing: drop the search state
                    filter = 0;
                    pill.Snap(0);
                    fileGen++;
                    filesPending = filesFailed = false;
                    fileWait.Stop();
                    indexed = new List<AppEntry>();
                    fileSeen.Clear();
                    fileH.Snap(0);
                }
                if (!was) fileSnap = true;
                Filter();
                MarkList();
                Render();
                Animate();
            }

            void SetFilter(int f)
            {
                f = (f + Filters.Length) % Filters.Length;
                if (f == filter || !Searching) return;
                if (sel >= 0 && sel < tiles.Count) keepSel = tiles[sel].Entry;
                filter = f;
                Aim(pill, f, false);
                fileSnap = true;
                scroll = scrollTo = 0;
                selSnap = true;
                Filter();
                MarkList();
                Render();
                isle.Announce(Lang.T(Filters[f]));
            }

            void ShowFolder(AppEntry e)
            {
                StartUsage.Record(UsageKey(e), Searching ? mq.Lower : "");
                Open("explorer.exe", "/select,\"" + e.Target + "\"");
                Done();
            }

            // Search: one row at a time
            // Arrows: sideways in reading order, up and down to the nearest tile in that row
            void Pick(int dx, int dy)
            {
                if (tiles.Count == 0) return;
                sel = Math.Max(0, Math.Min(tiles.Count - 1, sel));
                if (Searching) sel = Math.Max(0, Math.Min(tiles.Count - 1, sel + dx + dy));
                else if (dx != 0) sel = Math.Max(0, Math.Min(tiles.Count - 1, sel + dx));
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
                if (t.Y - Head < scrollTo) ScrollTo(t.Y - Head);
                else if (t.Bottom > scrollTo + ViewH) ScrollTo(t.Bottom - ViewH + 8);
                hot = -1;
                Render();
                Animate();
                isle.Announce(tiles[sel].Entry.Name);
            }

            void Launch(AppEntry e, bool admin)
            {
                StartUsage.Record(UsageKey(e), Searching ? mq.Lower : "");
                if (e.Uri == SettingsPages.TepiPrefs) { Done(); isle.OpenPrefs(); return; }
                if (e.Uri.StartsWith(SettingsPages.TepiPrefs + ":", StringComparison.Ordinal)) { Done(); isle.OpenPrefs(e.Uri.Substring(SettingsPages.TepiPrefs.Length + 1)); return; }
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
