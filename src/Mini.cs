using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace Tepi
{
    // Mini: click-through sliver on the edge
    partial class Island
    {
        const int MiniHotkey = 11;
        // Sliver sizes at 1x: rest, busy
        const float MiniW = 56, MiniH = 6, MiniBusyW = 64, MiniBusyH = 8;
        // Eyes sliver: height, widths, eye scale, x
        const float MiniEyesH = 14, MiniEyesW = 40, MiniEyesDotW = 52, MiniEyesBusyW = 76, MiniEyeK = 0.6f, MiniEyeX = 18;
        // Dwell, away and poll timings
        const int MiniEdgeMs = 150, MiniDwellMs = 350, MiniAwayMs = 600, MiniPollMs = 100, MiniFastMs = 30;
        // Reach beside, band below, still radius
        const float MiniMargin = 32, MiniBand = 12, MiniStill = 10;
        // Arrow into a bar: tuck away
        const char IcoMini = '\uE898';
        const int ExStyle = -20, ExTransparent = 0x20;

        readonly WinTimer miniTimer = new WinTimer { Interval = MiniPollMs };
        readonly StringBuilder fgClass = new StringBuilder(64);
        static readonly uint ownPid = GetCurrentProcessId();
        bool miniCovered, miniPeek, miniBlocked, clickThrough, fgDesktop, fgOwn, fgPopup;
        // Otomatis override: 1 in, -1 out
        int miniForce;
        IntPtr fgSeen;
        Native.POINT miniAnchor;
        DateTime miniDwellUtc = DateTime.MinValue, miniNearUtc, dropHoldUtc = DateTime.MinValue;
        RectangleF miniLine;
        Action actMini;
        Action<DateTime> miniHair, miniBreath;

        [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT pt);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetExStyle(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] static extern short KeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

        // Test builds steer pointer and input here
        partial void PilotPointer(ref Native.POINT at, ref bool down);
        partial void PilotThrough(ref bool through);

        // normal, mini, or auto (Otomatis)
        string MiniMode
        {
            get
            {
                string m = settings.Get("mini", "auto");
                return m == "mini" || m == "normal" ? m : "auto";
            }
        }

        void MiniInit()
        {
            miniTimer.Tick += delegate { MiniTick(); };
        }

        // Policy says mini, before holds
        bool MiniWanted()
        {
            string m = MiniMode;
            if (m == "mini") return true;
            if (m == "normal") return false;
            return miniForce != 0 ? miniForce > 0 : miniCovered || miniIdle;
        }

        // In use: the island stays out
        bool MiniHeld()
        {
            return expanded || pointerInside || keyboardMode || dragId != null || draggingOut || (menu != null && menu.Visible);
        }

        bool MiniOn() { return MiniWanted() && !miniPeek && !MiniHeld(); }

        bool MiniBusy() { return ActivityView() != View.Idle || PrivacyOn || LockShown; }

        SizeF MiniSize()
        {
            // Side edge: the sliver stands upright; eyes peek out
            if (OnSide) return EyesOn ? new SizeF(22, 30) : MiniBusy() ? new SizeF(MiniBusyH, MiniBusyW) : new SizeF(MiniH, MiniW);
            if (!EyesOn) return MiniBusy() ? new SizeF(MiniBusyW, MiniBusyH) : new SizeF(MiniW, MiniH);
            // Eyes only, whatever is running
            return new SizeF(MiniEyesW, MiniEyesH);
        }

        // Urgent pops out; quiet stays tucked
        bool MiniLets(View v, int priority)
        {
            if (priority >= 3) return true;
            switch (v)
            {
                case View.Notif: return settings.GetBool("mini.notif", true);
                case View.Volume:
                case View.Brightness:
                case View.Locks:
                case View.Shot:
                case View.Mic:
                case View.Drive:
                case View.AudioDevice:
                case View.Power:
                case View.Remind:
                case View.Done:
                case View.Deadline:
                case View.Pray:
                case View.Dictate:
                case View.ShelfDrop: return true;
            }
            return false;
        }

        Action MiniAct()
        {
            if (actMini == null) actMini = ToggleMini;
            return actMini;
        }

        // Hotkey, tray, Kontrol: flip what shows now
        void ToggleMini()
        {
            string m = MiniMode;
            if (m != "auto") { SetMiniMode(m == "mini" ? "normal" : "mini"); return; }
            // Otomatis: override until coverage changes
            miniForce = MiniWanted() ? -1 : 1;
            miniPeek = false;
            Kick();
        }

        void SetMiniMode(string mode)
        {
            settings.Set("mini", mode);
            settings.Save();
            miniForce = 0;
            miniPeek = false;
            CheckMiniAuto();
            Kick();
        }

        // Dwell at the edge: out until away
        void RevealMini()
        {
            miniPeek = true;
            miniNearUtc = DateTime.UtcNow;
            miniDwellUtc = DateTime.MinValue;
            miniTimer.Interval = MiniPollMs;
            Kick();
        }

        // From Kick: click-through and polling
        void MiniSync()
        {
            if (!MiniWanted()) miniPeek = false;
            // Held out: retract only once away
            else if (current != View.Mini && !miniPeek && MiniHeld()) { miniPeek = true; miniNearUtc = DateTime.UtcNow; }
            ApplyClickThrough();
            bool poll = (current == View.Mini || miniPeek) && !hiddenByUser && !fullscreen && !dormant;
            if (poll == miniTimer.Enabled) return;
            miniDwellUtc = DateTime.MinValue;
            miniTimer.Interval = MiniPollMs;
            if (poll) miniTimer.Start();
            else miniTimer.Stop();
        }

        // Panel closed by leaving: straight back in
        void MiniLeft() { miniPeek = false; QuietRestart(); }

        // Otomatis: a quiet minute retracts too
        bool miniIdle;
        DateTime quietUtc = DateTime.UtcNow;

        void QuietRestart()
        {
            quietUtc = DateTime.UtcNow;
            if (miniIdle) { miniIdle = false; Kick(); }
        }

        void CheckMiniIdle()
        {
            // Activity back: leave idle mini
            if (current == View.Mini) { if (miniIdle && MiniBusy()) QuietRestart(); return; }
            bool quiet = current == View.Idle && !MiniBusy() && !MiniHeld() && !miniPeek && toast == null && !PrompterOn;
            if (!quiet) { QuietRestart(); return; }
            if (!miniIdle && (DateTime.UtcNow - quietUtc).TotalSeconds >= 60) { miniIdle = true; Kick(); }
        }

        // Running script or sliver: every click passes
        void ApplyClickThrough()
        {
            if (!IsHandleCreated) return;
            bool want = PrompterRunning() || current == View.Mini;
            PilotThrough(ref want);
            if (want == clickThrough) return;
            int ex = Native.GetWindowLong(Handle, ExStyle);
            SetExStyle(Handle, ExStyle, want ? ex | ExTransparent : ex & ~ExTransparent);
            clickThrough = want;
        }

        void ReadPointer(out Native.POINT at, out bool down)
        {
            if (!GetCursorPos(out at)) at = new Native.POINT(-32000, -32000);
            down = (KeyState(1) & 0x8000) != 0 || (KeyState(2) & 0x8000) != 0 || (KeyState(4) & 0x8000) != 0;
            PilotPointer(ref at, ref down);
        }

        // Last drawn shell, screen px
        Rectangle ShellOnScreen(int margin)
        {
            var r = lastShellPx;
            r.Offset(lastLeft, lastTop);
            r.Inflate(margin, margin);
            return r;
        }

        // Pointer against the island's edge: depth in, offset along
        void EdgeAxes(Native.POINT c, out int depth, out int along)
        {
            var mon = monitorPx;
            int at = PlaceNow;
            if (at == PlaceLeft) { depth = c.X - mon.Left; along = c.Y; }
            else if (at == PlaceRight) { depth = mon.Right - 1 - c.X; along = c.Y; }
            else { depth = c.Y - mon.Top; along = c.X; }
        }

        // Shell's span along the edge and its reach off it
        void ShellAxes(Rectangle s, out int lo, out int hi, out int reachOut)
        {
            var mon = monitorPx;
            int at = PlaceNow;
            if (at == PlaceLeft) { lo = s.Top; hi = s.Bottom; reachOut = s.Right - mon.Left; }
            else if (at == PlaceRight) { lo = s.Top; hi = s.Bottom; reachOut = mon.Right - s.Left; }
            else { lo = s.Left; hi = s.Right; reachOut = s.Bottom - mon.Top; }
        }

        void MiniTick()
        {
            var now = DateTime.UtcNow;
            Native.POINT c;
            bool down;
            ReadPointer(out c, out down);
            if (current == View.Mini) { MiniDwell(c, down, now); MiniEyesSense(c); return; }
            if (!miniPeek) return;
            bool over = ShellOnScreen(0).Contains(c.X, c.Y);
            // Still pointer under reveal: start hover
            if (over && !pointerInside && !clickThrough) OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, c.X - lastLeft, c.Y - lastTop, 0));
            var near = ShellOnScreen((int)(8 * scale));
            bool close = near.Contains(c.X, c.Y);
            // Left before any real mouse message
            if (pointerInside && !close) PointerLeave();
            if (MiniHeld() || close) { miniNearUtc = now; return; }
            if ((now - miniNearUtc).TotalMilliseconds < MiniAwayMs) return;
            miniPeek = false;
            Kick();
        }

        // Still pointer at the edge, no button
        void MiniDwell(Native.POINT c, bool down, DateTime now)
        {
            var s = ShellOnScreen(0);
            int reach = (int)Math.Round(MiniMargin * scale);
            int depth, along, lo, hi, outward;
            EdgeAxes(c, out depth, out along);
            ShellAxes(s, out lo, out hi, out outward);
            int band = Math.Max(outward, (int)Math.Round(MiniBand * scale));
            bool beside = along >= lo - reach && along < hi + reach;
            bool edge = depth >= 0 && depth <= 1;
            bool zone = depth >= 0 && depth <= band && beside;
            // Dragging onto sliver opens for drop
            if (down && ShelfOn && depth >= 0 && depth <= (int)Math.Ceiling(24 * scale) && beside)
            {
                if (dropHoldUtc == DateTime.MinValue) dropHoldUtc = now;
                else if ((now - dropHoldUtc).TotalMilliseconds >= 300) { dropHoldUtc = DateTime.MinValue; RevealMini(); return; }
            }
            else dropHoldUtc = DateTime.MinValue;
            if (!zone || down)
            {
                // A click there means a tab
                miniBlocked = zone;
                MiniRest();
                return;
            }
            if (miniBlocked) return;
            float still = MiniStill * scale;
            // Along the edge must hold still; into it may press
            int alongMoved = OnSide ? Math.Abs(c.Y - miniAnchor.Y) : Math.Abs(c.X - miniAnchor.X);
            int depthMoved = OnSide ? Math.Abs(c.X - miniAnchor.X) : Math.Abs(c.Y - miniAnchor.Y);
            if (miniDwellUtc == DateTime.MinValue || alongMoved > still || (!edge && depthMoved > still))
            {
                miniDwellUtc = now;
                miniAnchor = c;
                miniTimer.Interval = MiniFastMs;
                return;
            }
            // Pushed to edge: quick; band: deliberate
            if ((now - miniDwellUtc).TotalMilliseconds >= (edge ? MiniEdgeMs : MiniDwellMs)) RevealMini();
        }

        void MiniRest()
        {
            miniDwellUtc = DateTime.MinValue;
            if (miniTimer.Interval != MiniPollMs) miniTimer.Interval = MiniPollMs;
        }

        // Otomatis: foreground covers the island's edge
        void CheckMiniAuto()
        {
            if (MiniMode != "auto" || !IsHandleCreated || hiddenByUser || dormant) return;
            bool covered = TopCovered();
            if (covered == miniCovered) return;
            miniCovered = covered;
            miniForce = 0;
            Kick();
        }

        bool TopCovered()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return miniCovered;
            if (fg != fgSeen)
            {
                // Class and owner read once per window
                fgSeen = fg;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                fgClass.Length = 0;
                Native.GetClassName(fg, fgClass, fgClass.Capacity);
                string cls = fgClass.ToString();
                fgOwn = pid == ownPid;
                fgDesktop = fg == GetShellWindow() || cls == "Progman" || cls == "WorkerW";
                const int Caption = 0x00C00000;
                fgPopup = (Native.GetWindowLong(fg, -16) & Caption) != Caption || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
            }
            if (fgDesktop) return false;
            // Own windows, other screens: keep answer
            if (fgOwn || Native.MonitorFromWindow(fg, 2) != monitorHandle) return miniCovered;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return miniCovered;
            var s = ShellOnScreen(0);
            var mon = monitorPx;
            int half = (int)(MiniW / 2 * scale), deep = (int)(48 * scale);
            int at = PlaceNow;
            if (at == PlaceLeft || at == PlaceRight)
            {
                // Side: the window spans the island's height at that edge
                int cy = s.Top + s.Height / 2;
                bool flush = at == PlaceLeft ? r.Left <= mon.Left + 2 && r.Right > mon.Left + deep : r.Right >= mon.Right - 2 && r.Left < mon.Right - deep;
                if (flush && r.Top <= cy - half && r.Bottom >= cy + half) return true;
            }
            else
            {
                int cx = s.Left + s.Width / 2;
                if (r.Top <= mon.Top + 2 && r.Bottom > mon.Top + deep && r.Left <= cx - half && r.Right >= cx + half) return true;
            }
            // Start, Alt+Tab, flyouts: no change
            return fgPopup ? miniCovered : false;
        }

        // Clock tick: catches maximize and restore
        void MiniClock()
        {
            if (!fullscreen) CheckMiniAuto();
            if (MiniMode == "auto") CheckMiniIdle();
        }

        // Sliver: progress of what runs underneath
        void DrawMini(float w, float h, DateTime now)
        {
            View v = ActivityView();
            bool dot = PrivacyOn || RapatOn;
            if (EyesOn) { MiniFace(w, h, now, v, dot); return; }
            bool caps = LockShown;
            if (v == View.Idle && !dot && !caps) return;
            float cy = h / 2, right = dot ? w - 15 : w - 12, left = caps ? 15 : 12;
            // Recording: accent dot at the end
            if (dot) p.Circle(w - 9, cy, 1.7f, MicSilenced || RapatOn ? p.T.Negative : p.T.Accent);
            // Lock keys: a caps arrow at the start
            if (caps) p.Symbol(Sym.ChevronUp, 10, cy, 6.5f, p.T.Text);
            if (v == View.Idle) return;
            if (OnSide)
            {
                // Upright sliver: progress fills downward
                float f = MiniFraction(now), cx = w / 2;
                var track = new RectangleF(cx - 1, 8, 2, h - 16);
                p.FillRound(track, 1, p.T.Quaternary);
                if (f > 0.001f) p.FillRound(new RectangleF(track.X, track.Y, 2, Math.Max(2, track.Height * f)), 1, p.T.Accent);
                return;
            }
            MiniLine(new RectangleF(left, cy - 1, right - left, 2), v, now);
        }

        void MiniLine(RectangleF r, View v, DateTime now)
        {
            if (miniHair == null)
            {
                miniHair = t => Hair(miniLine, MiniFraction(t), p.T.Accent);
                miniBreath = t => Breath(miniLine, t);
            }
            miniLine = r;
            if (MiniFraction(now) < 0) { LiveRect(miniLine, 12, miniBreath); return; }
            LiveRect(miniLine, MiniFps(v, miniLine.Width), miniHair);
        }

        // Eyes lead; hairline and dot follow
        void MiniFace(float w, float h, DateTime now, View v, bool dot)
        {
            float cy = h / 2;
            DrawEyes(w / 2, cy, MiniEyeK);
            // Same dots as the plain sliver
            if (dot) p.Circle(w - 9, cy, 1.7f, MicSilenced || RapatOn ? p.T.Negative : p.T.Accent);
            if (LockShown) p.Symbol(Sym.ChevronUp, 10, cy, 6.5f, p.T.Text);
        }

        // 0..1, or -1 while unknown
        float MiniFraction(DateTime now)
        {
            switch (ActivityView())
            {
                case View.Timer: return TimerFraction();
                case View.Media:
                case View.Lyric: return mediaInfo.DurationMs > 0 ? Ease.Clamp01((float)(mediaInfo.Position(now) / mediaInfo.DurationMs)) : -1;
                case View.Download:
                    var d = ActiveDownload();
                    return d != null && d.Percent >= 0 ? d.Percent / 100f : -1;
                case View.Agent:
                    var s = BusySession();
                    return s != null && s.Plan != null && s.Plan.Count > 0 ? (float)PlanDone(s) / s.Plan.Count : -1;
                case View.Run:
                case View.Meet: return -1;
                case View.Task:
                    var task = SoonTask();
                    return task == null ? 0 : Ease.Clamp01((float)((task.DeadlineUtc - now).TotalHours / 6));
                case View.Class:
                    var c = ClassNow();
                    if (c == null) return 0;
                    double total = (c.End - c.Start).TotalSeconds;
                    return total <= 0 ? 0 : Ease.Clamp01((float)((DateTime.Now - c.Start).TotalSeconds / total));
            }
            return 0;
        }

        // Half a device pixel per step
        float MiniFps(View v, float width)
        {
            if (v == View.Media || v == View.Lyric) return ProgressFps(width);
            if (v != View.Timer || timerPaused) return 0;
            double span = timerKind == TimerKind.Stopwatch ? 60 : (timerEndUtc - timerStartUtc).TotalSeconds;
            return span <= 0 ? 0 : Clamp((float)(width * scale * 2 / span), 0.2f, 10f);
        }

        void Hair(RectangleF r, float f, Color c)
        {
            p.FillRound(r, r.Height / 2, p.T.Quaternary);
            if (f > 0.001f) p.FillRound(r, r.Height / 2, p.Split(r.X, r.Width, r.X + Math.Max(r.Height, r.Width * f), c, Color.Transparent));
        }

        // Unknown progress: the line breathes
        void Breath(RectangleF r, DateTime now)
        {
            double t = (now - DateTime.MinValue).TotalSeconds % 2.4 / 2.4;
            float k = 0.5f - 0.5f * (float)Math.Cos(t * Math.PI * 2);
            p.FillRound(r, r.Height / 2, Ink.A(p.T.Text, 0.22f + 0.4f * k));
        }
    }
}
