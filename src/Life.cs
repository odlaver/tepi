using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Windows.UI.Notifications.Management;

namespace Tepi
{
    // Event sources feeding island state
    partial class Island
    {
        readonly MediaService media = new MediaService();
        readonly LyricsService lyricsSvc = new LyricsService();
        readonly VolumeWatcher volume = new VolumeWatcher();
        readonly Spectrum spectrum = new Spectrum();
        readonly NetWatcher net = new NetWatcher();
        readonly PrivacyWatcher privacy = new PrivacyWatcher();
        readonly NotifWatcher notifWatcher = new NotifWatcher();

        MediaInfo mediaInfo = MediaInfo.None;
        DateTime pausedSinceUtc = DateTime.MinValue;
        bool mediaSeen;
        string pendingTrack;
        Lyrics lyrics;
        int lyricIndex = -1, lyricPrev = -1;
        DateTime lyricSwitchUtc = DateTime.MinValue;

        float vol = 0.5f;
        bool muted, volKnown;
        readonly Spring volBar = new Spring(0.5f, 320, 30, 0.001f);
        readonly Roller volRoller = new Roller();
        bool hasBattery, onAc = true, powerKnown;
        int battery = 100;
        readonly Spring batteryFill = new Spring(0, 90, 12, 0.002f);
        string[] micApps = new string[0], camApps = new string[0];

        List<NotifItem> notifs = new List<NotifItem>();
        readonly HashSet<uint> seenNotifs = new HashSet<uint>();
        bool notifPrimed;
        int unread, notifBurst;
        UserNotificationListenerAccessStatus notifAccess = UserNotificationListenerAccessStatus.Allowed;

        AgentHub hub;
        readonly Dictionary<string, AgentSession> sessions = new Dictionary<string, AgentSession>();
        readonly List<Approval> approvals = new List<Approval>();

        KuliaClient kulia;
        List<KuliaTask> tasks;
        string tasksError;
        DateTime tasksAtUtc = DateTime.MinValue, nextTasksUtc = DateTime.MinValue;
        readonly HashSet<string> warnedTasks = new HashSet<string>();

        bool NotifOn { get { return settings.GetBool("notif", true); } }

        void StartFeeds()
        {
            media.Changed += m => Post(() => OnMedia(m));
            lyricsSvc.Ready += l => Post(() => OnLyrics(l));
            volume.Changed += (level, mute, first) => Post(() => OnVolume(level, mute, first));
            volume.DeviceChanged += () => spectrum.Restart();
            net.Changed += (a, b) => Post(() => OnNet(a, b));
            privacy.Changed += (m, c) => Post(() => OnPrivacy(m, c));
            notifWatcher.Updated += (access, list) => Post(() => OnNotifs(access, list));
            media.Start();
            volume.Start();
            net.Start();
            privacy.Start();
            notifWatcher.Enabled = NotifOn;
            notifWatcher.Start();
            StartAgents();
            // Default works; settings.ini may override
            kulia = new KuliaClient(settings.Has("kulia.url") ? settings.Get("kulia.url", "") : KuliaClient.DefaultUrl,
                                    settings.Has("kulia.key") ? settings.Get("kulia.key", "") : KuliaClient.DefaultKey);
            if (kulia.SignedIn) RefreshTasks(false);
            StartSources();
            StartUpdateCheck();
        }

        // Daily release check; never downloads anything
        public const string AppVersion = "2.0.0.1";
        string newVersion, newUrl;
        bool newRebuild, checking, checkFailed;
        DateTime checkedUtc;
        // Settings › About listens here
        event Action UpdateChanged;

        void StartUpdateCheck()
        {
            if (!settings.GetBool("update.check", true) || Settings.TestMode) return;
            var t = new System.Windows.Forms.Timer { Interval = 60000 };
            t.Tick += delegate { t.Interval = 24 * 3600 * 1000; CheckUpdate(false); };
            t.Start();
        }

        // Newer tag, or same tag rebuilt
        void CheckUpdate(bool manual)
        {
            if (checking || updating) return;
            checking = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null, url = null, asset = null, digest = null;
                bool ok = false, rebuild = false;
                try
                {
                    var r = Web.Send("GET", "https://api.github.com/repos/odlaver/tepi/releases/latest", null, null, 10000);
                    if (r.Ok)
                    {
                        var o = Json.Parse(r.Body);
                        ok = true;
                        tag = Json.Str(o, "tag_name").TrimStart('v');
                        url = Json.Str(o, "html_url");
                        DateTime uploaded;
                        ReadAsset(o, out asset, out digest, out uploaded);
                        Version latest, mine;
                        if (!Version.TryParse(tag, out latest) || !Version.TryParse(AppVersion, out mine) || latest < mine || !url.StartsWith("https://github.com/odlaver/tepi/")) tag = null;
                        else if (latest == mine)
                        {
                            rebuild = true;
                            // Local build newer than asset: keep
                            if (digest == null || digest == OwnDigest() || uploaded <= System.IO.File.GetLastWriteTimeUtc(Application.ExecutablePath)) tag = null;
                        }
                    }
                }
                catch (Exception ex) { Log.Write("update: " + ex.Message); }
                Post(() =>
                {
                    checking = false;
                    checkFailed = !ok;
                    if (ok) checkedUtc = DateTime.UtcNow;
                    if (tag != null)
                    {
                        bool first = newVersion != tag || newDigest != digest;
                        newVersion = tag;
                        newUrl = url;
                        newAsset = asset;
                        newDigest = digest;
                        newRebuild = rebuild;
                        if (first && !manual) ShowInfo(Ink.IcoRefresh, rebuild ? Lang.T("Pembaruan Tepi tersedia") : Lang.F("Tepi {0} tersedia", tag), Lang.T("tray › Perbarui"), true);
                    }
                    else if (ok) newVersion = newUrl = newAsset = newDigest = null;
                    if (UpdateChanged != null) UpdateChanged();
                });
            });
            if (UpdateChanged != null) UpdateChanged();
        }

        // Status line for Settings › About
        string UpdateText()
        {
            if (updating) return Lang.T("Mengunduh pembaruan");
            if (checking) return Lang.T("Memeriksa GitHub");
            if (newVersion != null) return newRebuild ? Lang.F("Build baru Tepi {0} tersedia", newVersion) : Lang.F("Tepi {0} tersedia", newVersion);
            if (checkFailed) return Lang.T("GitHub tidak terjangkau");
            if (checkedUtc != DateTime.MinValue) return Lang.F("Sudah terbaru · dicek {0}", Fmt.Hm(checkedUtc.ToLocalTime()));
            return Lang.T("Dicocokkan lewat SHA-256 dengan rilis di GitHub");
        }

        // Every second; true if redraw needed
        bool LifeTick(bool minute)
        {
            var now = DateTime.UtcNow;
            bool redraw = false;
            if (mediaInfo.Present) SyncLyrics();
            List<string> stale = null;
            foreach (var s in sessions.Values)
            {
                if (s.State == AgentState.Working && (now - s.LastUtc).TotalMinutes > 15) { s.State = AgentState.Ready; redraw = true; }
                if ((s.State == AgentState.Ended && (now - s.LastUtc).TotalMinutes > 3) || (now - s.LastUtc).TotalHours > 6)
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(s.Id);
                }
            }
            if (stale != null) foreach (var id in stale) { sessions.Remove(id); redraw = true; }
            if (kulia != null && kulia.SignedIn && now >= nextTasksUtc) RefreshTasks(false);
            SyncSourcesTick(now);
            CheckDeadlines(now);
            CheckClasses(now);
            // Countdowns read in minutes
            var soon = SoonTask();
            if (soon != null && (minute || (current == View.Task && Fmt.Left(soon.DeadlineUtc - now) != taskRoller.Value))) redraw = true;
            return redraw;
        }

        // Media + lyrics

        bool MediaVisible()
        {
            return mediaInfo.Present && mediaInfo.Title.Length > 0 && (mediaInfo.Playing || (DateTime.UtcNow - pausedSinceUtc).TotalSeconds < 90);
        }

        bool VisualizerVisible()
        {
            if (!settings.GetBool("visualizer", true) || !mediaInfo.Playing || fullscreen || dormant || hiddenByUser) return false;
            return current == View.Media || current == View.Lyric || current == View.Track || (current == View.Panel && page == PageMusic);
        }

        void OnMedia(MediaInfo m)
        {
            var prev = mediaInfo;
            if (prev.Playing && !m.Playing) pausedSinceUtc = DateTime.UtcNow;
            bool shortClip = m.DurationMs > 0 && m.DurationMs < 31000;
            if (m.Present && m.Key != prev.Key && mediaSeen && m.Title.Length > 0 && !shortClip) pendingTrack = m.Key;
            mediaInfo = m;
            // Free old artwork now, not at finalization
            if (prev.Cover != null && !ReferenceEquals(prev.Cover, m.Cover)) prev.Cover.Dispose();
            if (m.Cover != null && !ReferenceEquals(prev.Cover, m.Cover)) PrewarmArt();
            if (m.Present) mediaSeen = true;
            SyncLyrics();
            if (pendingTrack != null && pendingTrack == m.Key && m.Playing)
            {
                pendingTrack = null;
                if (!expanded) ShowToast(View.Track, 3.2, 1, false);
            }
            Kick();
        }

        void SyncLyrics()
        {
            if (!mediaInfo.Present) return;
            // Privacy switch: nothing goes to LRCLIB
            if (!settings.GetBool("lyrics.online", true))
            {
                if (lyrics == null || lyrics.Key != mediaInfo.Key) lyrics = new Lyrics { Key = mediaInfo.Key, State = LyricState.Missing };
                return;
            }
            if (lyrics != null && lyrics.Key == mediaInfo.Key && lyrics.State != LyricState.Failed) return;
            var l = lyricsSvc.Get(mediaInfo);
            if (l == null) return;
            if (lyrics == null || lyrics.Key != l.Key) { lyricIndex = lyricPrev = -1; lyricScroll.Snap(0); }
            lyrics = l;
        }

        void OnLyrics(Lyrics l)
        {
            if (l.Key != mediaInfo.Key) return;
            lyrics = l;
            lyricIndex = lyricPrev = -1;
            Kick();
        }

        bool LyricsReady { get { return lyrics != null && lyrics.Key == mediaInfo.Key && lyrics.State == LyricState.Synced; } }

        bool LyricOnIsland()
        {
            if (!settings.GetBool("lyrics", true) || !LyricsReady || !mediaInfo.Playing) return false;
            double pos = mediaInfo.Position(DateTime.UtcNow);
            int i = lyrics.IndexAt(pos);
            if (i < 0 || lyrics.Lines[i].Text.Length == 0) return false;
            // Instrumental break: back to the cover
            return !(pos > lyrics.SungUntil(i) + 2500 && lyrics.NextAt(i) - pos > 3000);
        }

        // System

        void OnVolume(float level, bool mute, bool first)
        {
            vol = level;
            muted = mute;
            volBar.Target = level;
            volRoller.Set(mute ? "0" : ((int)Math.Round(level * 100)).ToString());
            if (first || !volKnown) { volKnown = true; volBar.Snap(level); return; }
            if (!expanded) ShowToast(View.Volume, 1.5, 1, false, true);
            Kick();
        }

        void OnNet(NetState a, NetState b)
        {
            var t = ShowToast(View.Network, 3.2, 2, false);
            if (t != null)
            {
                t.Flag = b.Online;
                if (!b.Any) { t.Title = Lang.T("Jaringan terputus"); t.Detail = a.Name; }
                else if (!b.Online) { t.Title = Lang.T("Tanpa internet"); t.Detail = b.Name; }
                else if (b.Wifi) { t.Title = Lang.T("Wi-Fi tersambung"); t.Detail = b.Name; }
                else { t.Title = Lang.T("Tersambung"); t.Detail = b.Name.Length > 0 ? b.Name : "LAN"; }
            }
            if (!a.Online && b.Online && tasksError != null) RefreshTasks(false);
            if (!a.Online && b.Online && sources != null) sources.Retry();
            Kick();
        }

        void OnPrivacy(string[] mic, string[] cam)
        {
            string newMic = null, newCam = null;
            foreach (var x in mic) if (Array.IndexOf(micApps, x) < 0) { newMic = x; break; }
            foreach (var x in cam) if (Array.IndexOf(camApps, x) < 0) { newCam = x; break; }
            micApps = mic;
            camApps = cam;
            if (newCam != null || newMic != null)
            {
                var t = ShowToast(View.Privacy, 3, 2, false);
                if (t != null) { t.Flag = newCam != null; t.Title = newCam ?? newMic; }
            }
            Kick();
        }

        // Messages

        void OnNotifs(UserNotificationListenerAccessStatus access, List<NotifItem> list)
        {
            notifAccess = access;
            if (list == null || !NotifOn) { Kick(); return; }
            var visible = list.FindAll(n => n.Chat);
            // Toasts Windows doesn't keep stay here
            var live = new HashSet<uint>();
            foreach (var n in visible) live.Add(n.Id);
            foreach (var n in notifs)
                if (!live.Contains(n.Id) && (n.Held || !NotifSource.Kept(n.AppId)))
                {
                    n.Held = true;
                    visible.Add(n);
                }
            visible.Sort((a, b) => b.Time.CompareTo(a.Time));
            // Apps re-post the same message: keep the newest copy, toast once
            var known = new HashSet<string>();
            foreach (var n in notifs) known.Add(NotifKey(n));
            var keys = new HashSet<string>();
            visible.RemoveAll(n => !keys.Add(NotifKey(n)));
            if (visible.Count > 40) visible.RemoveRange(40, visible.Count - 40);
            var fresh = new List<NotifItem>();
            if (notifPrimed)
                foreach (var n in visible)
                    if (!n.Held && !seenNotifs.Contains(n.Id) && !known.Contains(NotifKey(n)) && (DateTime.UtcNow - n.Time).TotalMinutes < 3) fresh.Add(n);
            seenNotifs.Clear();
            foreach (var n in list) seenNotifs.Add(n.Id);
            notifPrimed = true;
            notifs = visible;
            unread = Math.Min(unread, visible.Count);
            if (fresh.Count > 0)
            {
                if (expanded && page == PageMessages) unread = 0;
                else unread += fresh.Count;
                if (!Native.DoNotDisturb())
                {
                    bool burst = toast != null && toast.View == View.Notif;
                    var t = ShowToast(View.Notif, 5.5, 2, true);
                    if (t != null)
                    {
                        notifBurst = burst ? notifBurst + fresh.Count : fresh.Count;
                        t.Data = fresh[0];
                    }
                }
            }
            Kick();
        }

        static string NotifKey(NotifItem n) { return n.AppId + "\n" + n.Title + "\n" + n.Body; }

        void OpenNotifApp(NotifItem item)
        {
            // Quoted; a stray quote can't add arguments
            if (item.AppId.Length > 0 && item.AppId.IndexOfAny(new[] { '"', '\r', '\n' }) < 0)
            {
                try { Process.Start("explorer.exe", "\"shell:AppsFolder\\" + item.AppId + "\""); }
                catch (Exception ex) { Log.Write("open app: " + ex.Message); }
            }
            notifs.RemoveAll(n => n.Held && n.AppId == item.AppId);
            unread = 0;
            expanded = false;
            if (toast != null && toast.View == View.Notif) DismissToast();
            Kick();
        }

        void RemoveNotifs(List<NotifItem> items)
        {
            foreach (var n in items) notifs.Remove(n);
            unread = Math.Min(unread, notifs.Count);
            Kick();
            var ids = items.FindAll(n => !n.Held).ConvertAll(n => n.Id);
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var id in ids)
                {
                    try { NotifSource.Remove(id); } catch (Exception ex) { Log.Write("notif remove: " + ex.Message); }
                }
                notifWatcher.Refresh();
            });
        }

        // Agents

        void StartAgents()
        {
            string token = settings.Get("agent.token", "");
            if (token.Length < 24)
            {
                var bytes = new byte[24];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                settings.Set("agent.token", token);
                // New installs: a port nobody can guess
                if (!settings.Has("agent.port")) settings.Set("agent.port", AgentHub.FreePort());
                settings.Save();
            }
            StartHub();
            CheckLinks();
        }

        void StartHub()
        {
            hub = new AgentHub(settings.GetInt("agent.port", 47821), settings.Get("agent.token", ""), settings.GetInt("agent.wait", 110));
            hub.Received += ev => Post(() => OnHook(ev));
            hub.Expired += a => Post(() => DropApproval(a));
            hub.Start();
        }

        bool hooksOn;
        int hookProfiles;

        bool HooksInstalled()
        {
            try
            {
                hooksOn = HookSetup.Installed(hub.Port);
                hookProfiles = HookSetup.Profiles().Count;
            }
            catch (Exception ex) { Log.Write("hook check: " + ex.Message); hooksOn = false; }
            return hooksOn;
        }

        // Applies to new Claude Code sessions
        void ToggleHooks()
        {
            try
            {
                if (HooksInstalled())
                {
                    HookSetup.Remove(hub.Port);
                    QuotaFollowHooks(false);
                    HooksInstalled();
                    ShowInfo(Ink.IcoTerminal, Lang.T("Claude Code dilepas"), Lang.T("hook Tepi dihapus"), true);
                    CheckLinks();
                    return;
                }
                string err = HookSetup.Install(hub.Port, settings.Get("agent.token", ""));
                if (err == null) QuotaFollowHooks(true);
                HooksInstalled();
                if (err != null) ShowInfo(Ink.IcoTerminal, Lang.T("Belum terhubung"), err, true);
                else ShowInfo(Ink.IcoTerminal, Lang.T("Claude Code terhubung"), hookProfiles + Lang.T(" profil · berlaku di sesi baru"), true);
            }
            catch (Exception ex)
            {
                Log.Write("hook setup: " + ex);
                ShowInfo(Ink.IcoTerminal, Lang.T("Belum terhubung"), Fmt.OneLine(ex.Message, 60), true);
            }
            CheckLinks();
        }

        string linked = "";

        // Agent names with hooks in place
        void CheckLinks()
        {
            var names = new List<string>();
            if (HooksInstalled()) names.Add("Claude Code");
            foreach (var id in AgentSetup.Ids)
            {
                try { if (AgentSetup.Installed(id)) names.Add(AgentSetup.Label(id)); }
                catch (Exception ex) { Log.Write("agent check: " + ex.Message); }
            }
            linked = string.Join(", ", names);
        }

        // Applies to that agent's new sessions
        void ToggleAgent(string id)
        {
            string name = AgentSetup.Label(id);
            try
            {
                if (AgentSetup.Installed(id))
                {
                    AgentSetup.Remove(id);
                    ShowInfo(Ink.IcoTerminal, name + Lang.T(" dilepas"), Lang.T("hook Tepi dihapus"), true);
                }
                else
                {
                    string err = AgentSetup.Install(id);
                    if (err != null) ShowInfo(Ink.IcoTerminal, Lang.T("Belum terhubung"), err, true);
                    else ShowInfo(Ink.IcoTerminal, Lang.F("{0} terhubung", name), id == "codex" ? Lang.T("percayai lewat /hooks di Codex") : Lang.T("pantau saja · berlaku di sesi baru"), true);
                }
            }
            catch (Exception ex)
            {
                Log.Write("agent setup: " + ex);
                ShowInfo(Ink.IcoTerminal, Lang.T("Belum terhubung"), Fmt.OneLine(ex.Message, 60), true);
            }
            CheckLinks();
        }

        AgentSession SessionFor(HookEvent ev)
        {
            AgentSession s;
            if (!sessions.TryGetValue(ev.SessionId, out s))
            {
                s = new AgentSession { Id = ev.SessionId, StartedUtc = DateTime.UtcNow, State = AgentState.Ready };
                sessions[ev.SessionId] = s;
            }
            string cwd = Json.Str(ev.Data, "cwd");
            if (cwd.Length > 0)
            {
                s.Cwd = cwd;
                string name = AgentHub.LastPart(cwd);
                s.Project = name.Length > 0 ? name : cwd;
            }
            string profile = AgentHub.ProfileOf(Json.Str(ev.Data, "transcript_path"));
            if (profile.Length > 0 || s.Profile.Length == 0) s.Profile = profile;
            s.LastUtc = DateTime.UtcNow;
            return s;
        }

        // Failed event must not strand agent
        void OnHook(HookEvent ev)
        {
            try { OnHookCore(ev); }
            catch (Exception ex)
            {
                Log.Write("hook event " + ev.Name + ": " + ex);
                if (ev.Approval != null)
                {
                    ev.Approval.Answer(null);
                    approvals.Remove(ev.Approval);
                }
                Kick();
            }
        }

        void OnHookCore(HookEvent ev)
        {
            if (ev.Agent == "plugin") { OnPlugin(ev); return; }
            if (ev.Agent == "usage") { OnUsage(ev); return; }
            if ((ev.Agent != "claude" && !AgentAdapters.Normalize(ev)) || ev.SessionId.Length == 0)
            {
                if (ev.Approval != null) ev.Approval.Answer(null);
                return;
            }
            var s = SessionFor(ev);
            if (ev.Agent != "claude") s.Profile = AgentAdapters.Label(ev.Agent);
            if (ev.Window != IntPtr.Zero) s.Window = ev.Window;
            if (ev.Pid > 0 && (s.Pid == 0 || ev.Window != IntPtr.Zero)) s.Pid = ev.Pid;
            var input = Json.Obj(ev.Data, "tool_input");
            string tool = Json.Str(ev.Data, "tool_name");
            switch (ev.Name)
            {
                case "SessionStart":
                    s.State = AgentState.Ready;
                    s.Activity = Lang.T("Siap");
                    break;
                case "UserPromptSubmit":
                    SettleApprovals(s.Id, null, null);
                    s.State = AgentState.Working;
                    s.TurnStartedUtc = DateTime.UtcNow;
                    s.Tool = "";
                    s.Activity = Lang.T("Berpikir");
                    s.Failed = false;
                    break;
                case "PreToolUse":
                    s.State = AgentState.Working;
                    s.Tool = tool;
                    s.Activity = AgentHub.Describe(tool, input);
                    // Codex has no PostToolUse hook here
                    if (ev.Agent == "codex") { Tally(s, tool, input); TakePlan(s, tool, input); }
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    if (ev.Name == "PostToolUse") { Tally(s, tool, input); TakePlan(s, tool, input); }
                    SettleApprovals(s.Id, tool, AgentHub.Describe(tool, input));
                    if (s.State == AgentState.Waiting)
                    {
                        s.State = AgentState.Working;
                        s.Activity = AgentHub.Describe(tool, input);
                    }
                    break;
                case "PermissionRequest":
                    var a = ev.Approval;
                    a.Agent = AgentAdapters.Label(ev.Agent);
                    a.Project = s.Project;
                    a.Profile = s.Profile;
                    a.Tool = tool;
                    FillApproval(a, tool, input);
                    a.Key = AgentHub.Describe(tool, input);
                    if (!a.Truncated && s.Trusted.Contains(a.Trust))
                    {
                        a.Answer("allow");
                        s.State = AgentState.Working;
                        s.Tool = tool;
                        s.Activity = a.Key;
                        break;
                    }
                    approvals.Add(a);
                    s.State = AgentState.Waiting;
                    s.Tool = tool;
                    s.Activity = AgentHub.Describe(tool, input);
                    expanded = false;
                    topGap.Velocity += 90;
                    break;
                case "Notification":
                    if (Json.Str(ev.Data, "notification_type") == "permission_prompt" && !HasApproval(s.Id))
                    {
                        s.State = AgentState.Waiting;
                        s.Activity = Lang.T("Menunggu izin di terminal");
                    }
                    break;
                case "Stop":
                case "StopFailure":
                    SettleApprovals(s.Id, null, null);
                    s.State = AgentState.Done;
                    s.Failed = ev.Name == "StopFailure";
                    s.LastMessage = Fmt.OneLine(Json.Str(ev.Data, "last_assistant_message"), 600);
                    var t = ShowToast(View.Done, 6, 2, false);
                    if (t != null) t.Data = s;
                    break;
                case "SessionEnd":
                    SettleApprovals(s.Id, null, null);
                    s.State = AgentState.Ended;
                    break;
            }
            Kick();
        }

        // Everything that will run or be written
        // Counts behind the session summary
        static void Tally(AgentSession s, string tool, Dictionary<string, object> input)
        {
            if (tool == "Bash" || tool == "PowerShell") s.Commands++;
            else if (tool == "Edit" || tool == "Write" || tool == "MultiEdit" || tool == "NotebookEdit")
            {
                string f = Json.Str(input, "file_path");
                if (f.Length == 0) f = Json.Str(input, "notebook_path");
                if (f.Length > 0) s.Files.Add(f.ToLowerInvariant());
            }
        }

        static string Summary(AgentSession s, DateTime now)
        {
            var parts = new List<string>();
            int min = Math.Max(0, (int)(now - s.StartedUtc).TotalMinutes);
            parts.Add(min < 1 ? Lang.T("baru") : min < 60 ? min + Lang.T(" mnt") : (min / 60) + Lang.T(" j ") + (min % 60) + Lang.T(" mnt"));
            if (s.Files.Count > 0) parts.Add(s.Files.Count + Lang.T(" berkas"));
            if (s.Commands > 0) parts.Add(s.Commands + Lang.T(" perintah"));
            return string.Join(" · ", parts);
        }

        // Bring the session's terminal forward
        bool FocusTerminal(AgentSession s)
        {
            if (s == null) return false;
            if (!TerminalFinder.Focus(s.Window))
            {
                s.Window = TerminalFinder.ForPid(s.Pid);
                if (!TerminalFinder.Focus(s.Window)) return false;
            }
            expanded = false;
            if (toast != null && toast.View == View.Done) DismissToast();
            Kick();
            return true;
        }

        // Shown text, flags, and the "Always" key
        static void FillApproval(Approval a, string tool, Dictionary<string, object> input)
        {
            a.Note = Fmt.Visible(Fmt.OneLine(Json.Str(input, "description"), 160));
            if (a.Note.Length == 0) a.Note = ChangeSummary(tool, input);
            a.Trust = Json.Sha256(tool + "\n" + Json.Canonical(input));
            a.Flags.Clear();
            if (input != null && (tool == "Bash" || tool == "PowerShell"))
                foreach (var kv in input)
                {
                    if (kv.Key == "command" || kv.Key == "description") continue;
                    string v = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (kv.Key == "run_in_background") { if (v == "True") a.Flags.Add(Lang.T("jalan di latar")); }
                    else if (kv.Key == "dangerouslyDisableSandbox") { if (v == "True") a.Flags.Add(Lang.T("tanpa sandbox")); }
                    else if (kv.Key == "timeout") a.Flags.Add(Lang.F("batas {0}", Duration(TimeSpan.FromMilliseconds(Json.Num(input, "timeout", 0)))));
                    else a.Flags.Add(kv.Key + ": " + Fmt.Visible(Fmt.OneLine(v, 40)));
                }
            a.Diff = DiffBuilder.For(tool, input);
            string d = Detail(tool, input);
            // Long plain text: terminal shows it whole
            a.Truncated = d.Length > 8000 && a.Diff == null;
            a.Detail = Fmt.Visible(a.Truncated ? d.Substring(0, 8000) + "…" : d);
        }

        static string Detail(string tool, Dictionary<string, object> input)
        {
            if (input == null) return "";
            string d = Plain(input, "command");
            if (tool != "Bash" && tool != "PowerShell") d = null;
            string path = Plain(input, "file_path");
            if (d == null && path != null)
            {
                if (tool == "Edit")
                    d = path + (Json.Str(input, "replace_all") == "True" ? Lang.T(" · semua kemunculan") : "") + "\n" +
                        Prefix("- ", Plain(input, "old_string")) + "\n" + Prefix("+ ", Plain(input, "new_string"));
                else if (tool == "MultiEdit")
                {
                    var sb = new System.Text.StringBuilder(path);
                    var edits = Json.Arr(input, "edits");
                    for (int i = 0; i < edits.Length; i++)
                    {
                        var e = edits[i] as Dictionary<string, object>;
                        if (e == null) continue;
                        sb.Append("\n@@ ").Append(i + 1).Append('/').Append(edits.Length).Append(Json.Str(e, "replace_all") == "True" ? Lang.T(" · semua kemunculan") : "");
                        sb.Append('\n').Append(Prefix("- ", Plain(e, "old_string"))).Append('\n').Append(Prefix("+ ", Plain(e, "new_string")));
                    }
                    d = sb.ToString();
                }
                else if (tool == "Write") d = path + "\n" + (Plain(input, "content") ?? "");
                else if (tool == "Read") d = path;
            }
            if (d == null && tool == "NotebookEdit")
                d = Json.Str(input, "notebook_path") + " · " + Json.Str(input, "edit_mode") + " " + Json.Str(input, "cell_id") + "\n" + Json.Str(input, "new_source");
            if (d == null && tool == "WebFetch") d = Plain(input, "url");
            if (d == null) d = Json.Encode(input);
            return d.Replace("\r", "");
        }

        static string Plain(Dictionary<string, object> input, string key)
        {
            object v;
            return input.TryGetValue(key, out v) ? v as string : null;
        }

        static string Prefix(string mark, string text)
        {
            return mark + (text ?? "").Replace("\r", "").Replace("\n", "\n" + mark);
        }

        // Size note beside the tool name
        static string ChangeSummary(string tool, Dictionary<string, object> input)
        {
            if (input == null) return "";
            if (tool == "Edit")
                return Lang.T("ganti ") + LineCount(Json.Str(input, "old_string")) + Lang.T(" baris jadi ") + LineCount(Json.Str(input, "new_string")) + Lang.T(" baris");
            if (tool == "Write") return Lang.T("tulis ") + LineCount(Json.Str(input, "content")) + Lang.T(" baris");
            if (tool == "MultiEdit") return Json.Arr(input, "edits").Length + Lang.T(" perubahan");
            if (tool == "NotebookEdit") return Lang.T("ubah sel notebook");
            return "";
        }

        static int LineCount(string s) { return s.Length == 0 ? 0 : s.Split('\n').Length; }

        bool HasApproval(string sessionId) { return approvals.Exists(a => a.SessionId == sessionId); }

        // Later event: answered in terminal
        void SettleApprovals(string sessionId, string tool, string described)
        {
            for (int i = approvals.Count - 1; i >= 0; i--)
            {
                var a = approvals[i];
                if (a.SessionId != sessionId) continue;
                if (tool != null && (a.Tool != tool || a.Key != described)) continue;
                a.Answer(null);
                approvals.RemoveAt(i);
            }
        }

        // Allow now and every identical request in this session
        void AllowAlways(Approval a)
        {
            if (a.Truncated) return;
            AgentSession s;
            if (sessions.TryGetValue(a.SessionId, out s)) s.Trusted.Add(a.Trust);
            Decide(a, "allow");
        }

        void Decide(Approval a, string decision)
        {
            a.Answer(decision);
            approvals.Remove(a);
            AgentSession s;
            if (sessions.TryGetValue(a.SessionId, out s) && !HasApproval(s.Id))
            {
                s.State = decision == null ? AgentState.Waiting : AgentState.Working;
                if (decision == null) s.Activity = Lang.T("Menunggu izin di terminal");
            }
            Kick();
        }

        void DropApproval(Approval a)
        {
            approvals.Remove(a);
            Kick();
        }

        void ReleaseApprovals()
        {
            foreach (var a in approvals) a.Answer(null);
            approvals.Clear();
        }

        AgentSession BusySession()
        {
            AgentSession best = null;
            foreach (var s in sessions.Values)
                if ((s.State == AgentState.Working || s.State == AgentState.Waiting) && (best == null || s.LastUtc > best.LastUtc)) best = s;
            return best;
        }

        int BusyCount()
        {
            int n = 0;
            foreach (var s in sessions.Values) if (s.State == AgentState.Working || s.State == AgentState.Waiting) n++;
            return n;
        }

        List<AgentSession> SortedSessions()
        {
            var list = new List<AgentSession>(sessions.Values);
            list.Sort((a, b) =>
            {
                int ra = Rank(a.State), rb = Rank(b.State);
                return ra != rb ? ra.CompareTo(rb) : b.LastUtc.CompareTo(a.LastUtc);
            });
            return list;
        }

        static int Rank(AgentState s)
        {
            return s == AgentState.Waiting ? 0 : s == AgentState.Working ? 1 : s == AgentState.Done ? 2 : s == AgentState.Ready ? 3 : 4;
        }

        // Kulia

        void RefreshTasks(bool manual)
        {
            if (manual) refreshAsked = 1;
            if (kuliaLoading || kulia == null || !kulia.SignedIn) return;
            kuliaLoading = true;
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err, schedErr = null;
                List<KuliaTask> list;
                List<ClassSlot> sched = null;
                try { list = kulia.Feed(out err); }
                catch (Exception ex) { Log.Write("kulia feed: " + ex.Message); list = null; err = "Kulia sedang bermasalah · dicoba lagi otomatis"; }
                try { if (list != null) sched = kulia.Schedule(out schedErr); }
                catch (Exception ex) { Log.Write("kulia schedule: " + ex.Message); schedErr = "Jadwal gagal dimuat"; }
                Post(() =>
                {
                    kuliaLoading = false;
                    // Signed out meanwhile: drop the result
                    if (!kulia.SignedIn) { MergeSources(); Kick(); return; }
                    if (list != null) { kuliaTasks = list; tasksError = null; tasksAtUtc = DateTime.UtcNow; }
                    if (sched != null) { kuliaClasses = sched; classesError = null; }
                    else if (list != null) classesError = schedErr;
                    else tasksError = err;
                    nextTasksUtc = DateTime.UtcNow.AddMinutes(list != null ? 10 : 2);
                    MergeSources();
                    Kick();
                });
            });
        }

        void OpenLogin()
        {
            if (kulia == null || !kulia.Configured)
            {
                ShowInfo(Ink.IcoLock, Lang.T("Kulia belum diatur"), Lang.T("kulia.url / kulia.key kosong"), true);
                return;
            }
            expanded = false;
            Kick();
            using (var f = new KuliaLogin(kulia))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
            }
            kuliaTasks = null;
            kuliaClasses = null;
            tasksError = null;
            MergeSources();
            RefreshTasks(false);
            ShowInfo('\0', Lang.T("Kulia tersambung"), kulia.Email, true);
        }

        void SignOutKulia()
        {
            var k = kulia;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { k.SignOut(); } catch (Exception ex) { Log.Write("kulia signout: " + ex.Message); }
                Post(Kick);
            });
            kuliaTasks = null;
            kuliaClasses = null;
            tasksError = null;
            classesError = null;
            MergeSources();
            Kick();
        }

        void MarkDone(KuliaTask task)
        {
            if (task.ReadOnly) return;
            if (task.Source != "kulia") { CloseExternal(task); return; }
            string old = task.Status;
            task.Status = "selesai";
            if (tasks != null) tasks.Remove(task);
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok;
                try { ok = kulia.SetStatus(task, "selesai"); }
                catch (Exception ex) { Log.Write("kulia status: " + ex.Message); ok = false; }
                Post(() =>
                {
                    if (ok) ShowInfo('\0', Lang.T("Ditandai selesai"), task.Title, true);
                    else
                    {
                        task.Status = old;
                        ShowInfo(Ink.IcoLock, Lang.T("Gagal tersimpan di Kulia"), task.Title, true);
                        RefreshTasks(false);
                    }
                });
            });
        }

        KuliaTask SoonTask()
        {
            if (tasks == null) return null;
            var now = DateTime.UtcNow;
            // Date-only tasks have no countdown
            foreach (var t in tasks)
                if (!t.DateOnly && t.DeadlineUtc > now && (t.DeadlineUtc - now).TotalHours <= 6) return t;
            return null;
        }

        void CheckDeadlines(DateTime now)
        {
            if (tasks == null) return;
            foreach (var t in tasks)
            {
                double mins = (t.DeadlineUtc - now).TotalMinutes;
                if (t.DateOnly || mins <= 0 || mins > 60 || warnedTasks.Contains(t.Id)) continue;
                warnedTasks.Add(t.Id);
                var ts = ShowToast(View.Deadline, 8, 3, false);
                if (ts != null) ts.Data = t;
                break;
            }
        }

        // Web links only; a path would run
        static void OpenUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
            try { Process.Start(u.AbsoluteUri); } catch (Exception ex) { Log.Write("open url: " + ex.Message); }
        }
    }
}
