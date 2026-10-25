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
        bool tasksLoading;
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
            // Works out of the box; settings.ini may point elsewhere
            kulia = new KuliaClient(settings.Has("kulia.url") ? settings.Get("kulia.url", "") : KuliaClient.DefaultUrl,
                                    settings.Has("kulia.key") ? settings.Get("kulia.key", "") : KuliaClient.DefaultKey);
            if (kulia.SignedIn) RefreshTasks(false);
        }

        // Every second; true if redraw needed
        bool LifeTick()
        {
            var now = DateTime.UtcNow;
            bool redraw = false;
            if (mediaInfo.Present)
            {
                SyncLyrics();
                if ((current == View.Media || current == View.Lyric || current == View.Panel) && !VisualizerVisible()) redraw = true;
            }
            var stale = new List<string>();
            foreach (var s in sessions.Values)
            {
                if (s.State == AgentState.Working && (now - s.LastUtc).TotalMinutes > 15) { s.State = AgentState.Ready; redraw = true; }
                if ((s.State == AgentState.Ended && (now - s.LastUtc).TotalMinutes > 3) || (now - s.LastUtc).TotalHours > 6) stale.Add(s.Id);
            }
            foreach (var id in stale) { sessions.Remove(id); redraw = true; }
            if (kulia != null && kulia.SignedIn && now >= nextTasksUtc) RefreshTasks(false);
            CheckDeadlines(now);
            if (SoonTask() != null) redraw = true;
            return redraw;
        }

        // Media + lyrics

        bool MediaVisible()
        {
            return mediaInfo.Present && mediaInfo.Title.Length > 0 && (mediaInfo.Playing || (DateTime.UtcNow - pausedSinceUtc).TotalSeconds < 90);
        }

        bool VisualizerVisible()
        {
            if (!settings.GetBool("visualizer", true) || !mediaInfo.Playing || fullscreen) return false;
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
                if (!b.Any) { t.Title = "Jaringan terputus"; t.Detail = a.Name; }
                else if (!b.Online) { t.Title = "Tanpa internet"; t.Detail = b.Name; }
                else if (b.Wifi) { t.Title = "Wi-Fi tersambung"; t.Detail = b.Name; }
                else { t.Title = "Tersambung"; t.Detail = b.Name.Length > 0 ? b.Name : "LAN"; }
            }
            if (!a.Online && b.Online && tasksError != null) RefreshTasks(false);
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
            var fresh = new List<NotifItem>();
            if (notifPrimed)
                foreach (var n in visible)
                    if (!seenNotifs.Contains(n.Id) && (DateTime.UtcNow - n.Time).TotalMinutes < 3) fresh.Add(n);
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

        void OpenNotifApp(NotifItem item)
        {
            try { Process.Start("explorer.exe", "shell:AppsFolder\\" + item.AppId); }
            catch (Exception ex) { Log.Write("open app: " + ex.Message); }
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
            var ids = items.ConvertAll(n => n.Id);
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
                settings.Save();
            }
            hub = new AgentHub(settings.GetInt("agent.port", 47821), token, settings.GetInt("agent.wait", 110));
            hub.Received += ev => Post(() => OnHook(ev));
            hub.Expired += a => Post(() => DropApproval(a));
            hub.Start();
            HooksInstalled();
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
                    HooksInstalled();
                    ShowInfo(Ink.IcoTerminal, "Claude Code dilepas", "hook Tepi dihapus", true);
                    return;
                }
                string err = HookSetup.Install(hub.Port, settings.Get("agent.token", ""));
                HooksInstalled();
                if (err != null) ShowInfo(Ink.IcoTerminal, "Belum terhubung", err, true);
                else ShowInfo(Ink.IcoTerminal, "Claude Code terhubung", hookProfiles + " profil · berlaku di sesi baru", true);
            }
            catch (Exception ex)
            {
                Log.Write("hook setup: " + ex);
                ShowInfo(Ink.IcoTerminal, "Belum terhubung", Fmt.OneLine(ex.Message, 60), true);
            }
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
                string name = System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
                s.Project = name.Length > 0 ? name : cwd;
            }
            string profile = AgentHub.ProfileOf(Json.Str(ev.Data, "transcript_path"));
            if (profile.Length > 0 || s.Profile.Length == 0) s.Profile = profile;
            s.LastUtc = DateTime.UtcNow;
            return s;
        }

        void OnHook(HookEvent ev)
        {
            var s = SessionFor(ev);
            var input = Json.Obj(ev.Data, "tool_input");
            string tool = Json.Str(ev.Data, "tool_name");
            switch (ev.Name)
            {
                case "SessionStart":
                    s.State = AgentState.Ready;
                    s.Activity = "Siap";
                    break;
                case "UserPromptSubmit":
                    SettleApprovals(s.Id, null, null);
                    s.State = AgentState.Working;
                    s.TurnStartedUtc = DateTime.UtcNow;
                    s.Tool = "";
                    s.Activity = "Berpikir";
                    s.Failed = false;
                    break;
                case "PreToolUse":
                    s.State = AgentState.Working;
                    s.Tool = tool;
                    s.Activity = AgentHub.Describe(tool, input);
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    SettleApprovals(s.Id, tool, AgentHub.Describe(tool, input));
                    if (s.State == AgentState.Waiting) s.State = AgentState.Working;
                    break;
                case "PermissionRequest":
                    var a = ev.Approval;
                    a.Project = s.Project;
                    a.Profile = s.Profile;
                    a.Tool = tool;
                    a.Detail = Detail(tool, input);
                    a.Note = Fmt.OneLine(Json.Str(input, "description"), 160);
                    a.Key = AgentHub.Describe(tool, input);
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
                        s.Activity = "Menunggu izin di terminal";
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

        static string Detail(string tool, Dictionary<string, object> input)
        {
            if (input == null) return "";
            if (tool == "Bash" || tool == "PowerShell") return Json.Str(input, "command");
            string path = Json.Str(input, "file_path");
            if (path.Length > 0) return path;
            string url = Json.Str(input, "url");
            if (url.Length > 0) return url;
            return Fmt.OneLine(Json.Encode(input), 400);
        }

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

        void Decide(Approval a, string decision)
        {
            a.Answer(decision);
            approvals.Remove(a);
            AgentSession s;
            if (sessions.TryGetValue(a.SessionId, out s) && !HasApproval(s.Id))
            {
                s.State = decision == null ? AgentState.Waiting : AgentState.Working;
                if (decision == null) s.Activity = "Menunggu izin di terminal";
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
            if (tasksLoading || kulia == null || !kulia.SignedIn) return;
            tasksLoading = true;
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                var list = kulia.Feed(out err);
                Post(() =>
                {
                    tasksLoading = false;
                    if (list != null) { tasks = list; tasksError = null; tasksAtUtc = DateTime.UtcNow; }
                    else tasksError = err;
                    nextTasksUtc = DateTime.UtcNow.AddMinutes(list != null ? 10 : 2);
                    if (manual && list != null) ShowInfo(Ink.IcoRefresh, "Tugas diperbarui", list.Count + " aktif");
                    Kick();
                });
            });
        }

        void OpenLogin()
        {
            if (kulia == null || !kulia.Configured)
            {
                ShowInfo(Ink.IcoLock, "Kulia belum diatur", "kulia.url / kulia.key kosong", true);
                return;
            }
            expanded = false;
            Kick();
            using (var f = new KuliaLogin(kulia))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
            }
            tasks = null;
            tasksError = null;
            RefreshTasks(false);
            ShowInfo('\0', "Kulia tersambung", kulia.Email, true);
        }

        void SignOutKulia()
        {
            var k = kulia;
            ThreadPool.QueueUserWorkItem(delegate { k.SignOut(); Post(Kick); });
            tasks = null;
            tasksError = null;
            Kick();
        }

        void MarkDone(KuliaTask task)
        {
            string old = task.Status;
            task.Status = "selesai";
            if (tasks != null) tasks.Remove(task);
            Kick();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = kulia.SetStatus(task, "selesai");
                Post(() =>
                {
                    if (ok) ShowInfo('\0', "Ditandai selesai", task.Title, true);
                    else
                    {
                        task.Status = old;
                        ShowInfo(Ink.IcoLock, "Gagal tersimpan di Kulia", task.Title, true);
                        RefreshTasks(false);
                    }
                });
            });
        }

        KuliaTask SoonTask()
        {
            if (tasks == null) return null;
            var now = DateTime.UtcNow;
            foreach (var t in tasks)
                if (t.DeadlineUtc > now && (t.DeadlineUtc - now).TotalHours <= 6) return t;
            return null;
        }

        void CheckDeadlines(DateTime now)
        {
            if (tasks == null) return;
            foreach (var t in tasks)
            {
                double mins = (t.DeadlineUtc - now).TotalMinutes;
                if (mins <= 0 || mins > 60 || warnedTasks.Contains(t.Id)) continue;
                warnedTasks.Add(t.Id);
                var ts = ShowToast(View.Deadline, 8, 3, false);
                if (ts != null) ts.Data = t;
                break;
            }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch (Exception ex) { Log.Write("open url: " + ex.Message); }
        }
    }
}
