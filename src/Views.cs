using System;
using System.Drawing;

namespace Tepi
{
    partial class Island
    {
        readonly float[] rawBands = new float[Spectrum.Bands], bands = new float[Spectrum.Bands];
        readonly Spring lyricScroll = new Spring(0, 240, 30, 0.01f);
        readonly Roller taskRoller = new Roller();
        float orbitPhase;
        const float LyricLineH = 30f;

        SizeF ViewSize(View v)
        {
            switch (v)
            {
                case View.Idle: return new SizeF(IdleWidth(), 34);
                case View.Media: return new SizeF(MediaWidth(), 38);
                case View.Lyric: return new SizeF(LyricWidth(), 38);
                case View.Track: return new SizeF(384, 80);
                case View.Agent: return new SizeF(AgentWidth(), 38);
                case View.Approve: return new SizeF(ApproveW, ApproveHeight());
                case View.Done: return new SizeF(414, 70);
                case View.Notif: return new SizeF(404, 66);
                case View.Volume: return new SizeF(300, 44);
                case View.Power:
                case View.Network: return new SizeF(344, 48);
                case View.Privacy: return new SizeF(PrivacyWidth(), 40);
                case View.Info: return new SizeF(InfoWidth(), 42);
                case View.Task: return new SizeF(TaskWidth(), 38);
                case View.Deadline: return new SizeF(414, 74);
                case View.Focus: return new SizeF(FocusWidth(), 38);
                case View.Prompter: return new SizeF(PrompterW, PrompterH);
                case View.Class: return new SizeF(ClassWidth(), 38);
                case View.Download: return new SizeF(DownloadWidth(), 38);
                default: return new SizeF(PanelW, PanelHeight());
            }
        }

        float ViewRadius(View v, SizeF s)
        {
            switch (v)
            {
                case View.Panel:
                case View.Approve: return 30;
                case View.Track:
                case View.Done:
                case View.Notif:
                case View.Deadline:
                case View.Prompter: return 26;
            }
            return s.Height / 2;
        }

        void DrawView(View v, float w, float h, DateTime now)
        {
            switch (v)
            {
                case View.Idle: DrawIdle(w, h, now); break;
                case View.Media: DrawMedia(w, h, now); break;
                case View.Lyric: DrawLyric(w, h, now); break;
                case View.Track: DrawTrack(w, h); break;
                case View.Agent: DrawAgent(w, h); break;
                case View.Approve: DrawApprove(w, h); break;
                case View.Done: DrawDone(w, h, now); break;
                case View.Notif: DrawNotif(w, h); break;
                case View.Volume: DrawVolume(w, h, now); break;
                case View.Power: DrawPower(w, h); break;
                case View.Network: DrawNetwork(w, h); break;
                case View.Privacy: DrawPrivacy(w, h); break;
                case View.Info: DrawInfo(w, h); break;
                case View.Task: DrawTask(w, h, now); break;
                case View.Deadline: DrawDeadline(w, h); break;
                case View.Focus: DrawFocus(w, h, now); break;
                case View.Prompter: DrawPrompter(w, h, now); break;
                case View.Class: DrawClass(w, h, now); break;
                case View.Download: DrawDownload(w, h, now); break;
                case View.Panel: DrawPanel(w, h, now); break;
            }
        }

        // Per-frame state besides springs

        void StepViews(float dt, DateTime now)
        {
            volBar.Step(dt);
            batteryFill.Step(dt);
            lyricScroll.Step(dt);
            orbitPhase = (orbitPhase + dt / 1.15f) % 1f;
            if (spectrum.Active) spectrum.CopyBands(rawBands);
            bool on = mediaInfo.Playing && spectrum.Active;
            for (int i = 0; i < bands.Length; i++)
            {
                float target = on ? rawBands[i] : 0;
                float rate = target > bands[i] ? 22f : 6f;
                bands[i] += (target - bands[i]) * Math.Min(1f, rate * dt);
            }
            if (LyricsReady)
            {
                int idx = lyrics.IndexAt(mediaInfo.Position(now));
                if (idx != lyricIndex)
                {
                    lyricPrev = lyricIndex;
                    lyricIndex = idx;
                    lyricSwitchUtc = now;
                    lyricScroll.Target = Math.Max(0, idx) * LyricLineH;
                    Kick();
                }
            }
            if (DesiredView() != current) Kick();
        }

        bool ViewsMoving(DateTime now)
        {
            return !volBar.Settled || !batteryFill.Settled || !lyricScroll.Settled || (now - lyricSwitchUtc).TotalSeconds < 0.45 ||
                   volRoller.Moving(now) || taskRoller.Moving(now);
        }

        bool Live(DateTime now)
        {
            if (VisualizerVisible() || current == View.Lyric || current == View.Approve || current == View.Agent || PrompterRunning()) return true;
            if (current == View.Media && mediaInfo.Playing) return true;
            if (BubbleKind() == View.Agent) return true;
            foreach (var b in bands) if (b > 0.004f) return true;
            if (current == View.Panel)
            {
                if (page == PageMusic && mediaInfo.Playing) return true;
                if (page == PageAgents && BusyCount() > 0) return true;
                if (page == PageTasks && tasksLoading) return true;
            }
            return false;
        }

        // Idle

        bool PrivacyOn { get { return micApps.Length + camApps.Length > 0; } }

        float IdleWidth() { return 112 + (PrivacyOn || unread > 0 ? 44 : 0) + (emblem != null ? 30 : 0); }

        void DrawIdle(float w, float h, DateTime now)
        {
            float cy = h / 2, shift = emblem != null ? 15 : 0;
            if (emblem != null) DrawEmblem(24, cy, 10.5f);
            clockRoller.Draw(p, Ink.Display(15.5f), p.T.Text, w / 2 + shift, cy, StringAlignment.Center, now);
            if (PrivacyOn) p.Icon(camApps.Length > 0 ? Ink.IcoCam : Ink.IcoMic, 12f, p.T.Sub, 22 + shift * 2, cy);
            if (unread > 0) DrawCount(w - 22, cy, unread);
        }

        // Inverted dot: something waits for you
        void DrawCount(float cx, float cy, int n)
        {
            string s = n > 9 ? "9+" : n.ToString();
            float r = 8.5f;
            p.Circle(cx, cy, r, p.T.Text);
            p.TextCenter(s, Ink.Semi(10.5f), p.T.Bg, cx, cy);
        }

        // Music

        float MediaWidth()
        {
            float t = p.G == null ? 0 : p.Measure(MediaLine(), Ink.Semi(12.5f));
            return Clamp(t + 112, 236, 400);
        }

        string MediaLine()
        {
            return mediaInfo.Artist.Length > 0 ? mediaInfo.Title + "  ·  " + mediaInfo.Artist : mediaInfo.Title;
        }

        readonly System.Collections.Generic.Dictionary<int, Bitmap> coverCache = new System.Collections.Generic.Dictionary<int, Bitmap>();
        Bitmap coverSource;

        // Integer key: no string per frame
        int CoverKey(float size, float radius) { return ((int)Math.Round(size * 10) * 1000 + (int)Math.Round(radius * 10)) * 1000 + (int)Math.Round(scale * 100); }

        static Bitmap BakeCover(Bitmap src, float size, float radius, float scale)
        {
            int px = (int)Math.Ceiling(size * scale);
            var baked = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var bg = Graphics.FromImage(baked))
            {
                bg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                bg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                bg.ScaleTransform(scale, scale);
                new Painter { G = bg }.Image(src, new RectangleF(0, 0, size, size), radius);
            }
            return baked;
        }

        // Cover baked once per size
        void DrawCover(RectangleF r, float radius)
        {
            var src = mediaInfo.Cover;
            if (src != null)
            {
                if (!ReferenceEquals(src, coverSource))
                {
                    foreach (var b in coverCache.Values) b.Dispose();
                    coverCache.Clear();
                    coverSource = src;
                }
                int key = CoverKey(r.Width, radius);
                Bitmap baked;
                if (!coverCache.TryGetValue(key, out baked))
                {
                    baked = BakeCover(src, r.Width, radius, scale);
                    coverCache[key] = baked;
                }
                var mode = g.InterpolationMode;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                var dst = new RectangleF(r.X, r.Y, baked.Width / scale, baked.Height / scale);
                if (p.Alpha >= 0.99f) g.DrawImage(baked, dst);
                else
                    using (var attr = new System.Drawing.Imaging.ImageAttributes())
                    {
                        attr.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = p.Alpha });
                        g.DrawImage(baked, new[] { dst.Location, new PointF(dst.Right, dst.Y), new PointF(dst.X, dst.Bottom) },
                            new RectangleF(0, 0, baked.Width, baked.Height), GraphicsUnit.Pixel, attr);
                    }
                g.InterpolationMode = mode;
                return;
            }
            else
            {
                p.FillRound(r, radius, p.T.Fill);
                p.Icon('', r.Height * 0.42f, p.T.Sub, r.X + r.Width / 2, r.Y + r.Height / 2);
            }
        }

        void DrawBars(float right, float cy, int count, float maxH, float barW, float gap)
        {
            bool playing = mediaInfo.Playing;
            for (int i = 0; i < count; i++)
            {
                int band = (int)((i + 0.5f) / count * Spectrum.Bands);
                float v = playing ? bands[Math.Min(Spectrum.Bands - 1, band)] : 0;
                float bh = 3f + v * (maxH - 3f);
                float x = right - (count - i) * (barW + gap) + gap;
                p.FillRound(new RectangleF(x, cy - bh / 2, barW, bh), barW / 2, Ink.A(p.T.Accent, playing ? 0.5f + 0.5f * v : 0.35f));
            }
        }

        void DrawProgress(float x0, float x1, float y, float thickness, DateTime now)
        {
            if (mediaInfo.DurationMs <= 0) return;
            float f = (float)(mediaInfo.Position(now) / mediaInfo.DurationMs);
            p.FillRound(new RectangleF(x0, y, x1 - x0, thickness), thickness / 2, p.T.Line);
            p.FillRound(new RectangleF(x0, y, Math.Max(thickness, (x1 - x0) * Ease.Clamp01(f)), thickness), thickness / 2, p.T.Progress);
        }

        void DrawMedia(float w, float h, DateTime now)
        {
            float cy = h / 2;
            if (mediaInfo.Playing && mediaInfo.Cover != null) DrawMiniVinyl(19, cy, 12.5f);
            else DrawCover(new RectangleF(7, cy - 12, 24, 24), 7);
            p.Text(MediaLine(), Ink.Semi(12.5f), p.T.Text, 40, cy - 0.5f, w - 40 - 52, StringAlignment.Near);
            if (mediaInfo.Playing) DrawBars(w - 15, cy, 5, 17, 3f, 2.6f);
            else p.Pause(w - 26, cy, 11, p.T.Sub);
            DrawProgress(h / 2, w - h / 2, h - 3.2f, 1.6f, now);
        }

        string LyricText(int i)
        {
            return lyrics != null && i >= 0 && i < lyrics.Lines.Count ? lyrics.Lines[i].Text : "";
        }

        float lyricW;
        string lyricWKey;

        // Grow fast, shrink late: fewer morphs
        float LyricWidth()
        {
            if (p.G == null || !LyricsReady) return 260;
            int i = Math.Max(0, lyricIndex);
            float t = 0;
            for (int k = i; k < Math.Min(lyrics.Lines.Count, i + 3); k++) t = Math.Max(t, p.Measure(LyricText(k), Ink.Display(14.5f)));
            float want = Clamp(t + 92, 220, 500);
            if (lyricWKey != lyrics.Key || want > lyricW || want < lyricW - 70) { lyricW = want; lyricWKey = lyrics.Key; }
            return lyricW;
        }

        void DrawLyric(float w, float h, DateTime now)
        {
            float cy = h / 2;
            DrawCover(new RectangleF(8, cy - 11, 22, 22), 6);
            DrawBars(w - 13, cy, 3, 15, 2.8f, 2.4f);
            if (!LyricsReady) return;
            double pos = mediaInfo.Position(now);
            float k = Ease.OutCubic((float)((now - lyricSwitchUtc).TotalSeconds / 0.38));
            float left = 38, avail = w - 38 - 30;
            var font = Ink.Display(14.5f);
            float alpha = p.Alpha;
            if (k < 1 && lyricPrev >= 0)
            {
                p.Alpha = alpha * (1 - k);
                p.Text(LyricText(lyricPrev), font, p.T.Text, left + avail / 2, cy - k * 15, avail, StringAlignment.Center);
            }
            if (lyricIndex >= 0)
            {
                var line = lyrics.Lines[lyricIndex];
                double start = line.AtMs, end = lyrics.SungUntil(lyricIndex);
                float prog = (float)((pos - start) / Math.Max(1, end - start));
                p.Alpha = alpha * k;
                p.Sweep(line.Text, font, p.T.Text, p.T.Faint, left + avail / 2, cy + (1 - k) * 15, avail, StringAlignment.Center, Ease.Clamp01(prog));
            }
            p.Alpha = alpha;
        }

        void DrawTrack(float w, float h)
        {
            float cy = h / 2;
            DrawCover(new RectangleF(14, cy - 26, 52, 52), 13);
            p.Text(mediaInfo.Title, Ink.Semi(14.5f), p.T.Text, 80, cy - 10, w - 80 - 60, StringAlignment.Near);
            p.Text(mediaInfo.Artist, Ink.Text(12.5f), p.T.Sub, 80, cy + 11, w - 80 - 60, StringAlignment.Near);
            DrawBars(w - 20, cy, 4, 22, 3.4f, 3f);
            AddHit(new RectangleF(0, 0, w, h), "track", delegate { Expand(PageMusic); });
        }

        // Agents

        float AgentWidth()
        {
            var s = BusySession();
            if (s == null || p.G == null) return 200;
            float a = p.Measure(s.Project, Ink.Semi(13f)), b = Math.Min(230, p.Measure(AgentLine(s), Ink.Mono(12f)));
            return Clamp(40 + a + 10 + b + 18 + (BusyCount() > 1 ? 26 : 0), 180, 460);
        }

        static string AgentLine(AgentSession s)
        {
            if (s.State == AgentState.Waiting) return s.Activity.Length > 0 ? s.Activity : Lang.T("Menunggu izin");
            if (s.Tool.Length == 0) return s.Activity;
            string label = AgentHub.ToolLabel(s.Tool);
            return s.Activity.Length > 0 ? label + " " + s.Activity : label;
        }

        void DrawAgent(float w, float h)
        {
            var s = BusySession();
            if (s == null) return;
            float cy = h / 2;
            if (s.State == AgentState.Waiting) p.Icon(Ink.IcoTerminal, 13f, p.T.Text, 20, cy);
            else p.Orbit(20, cy, 7.5f, orbitPhase, p.T.Text);
            var semi = Ink.Semi(13f);
            float x = 40;
            p.Text(s.Project, semi, p.T.Text, x, cy, 160, StringAlignment.Near);
            x += Math.Min(160, p.Measure(s.Project, semi)) + 10;
            int n = BusyCount();
            p.Text(AgentLine(s), Ink.Mono(12f), p.T.Sub, x, cy, w - x - (n > 1 ? 40 : 18), StringAlignment.Near);
            if (n > 1) p.TextRight("+" + (n - 1), Ink.Small(11.5f), p.T.Sub, w - 16, cy);
        }

        const int ApproveMaxLines = 10, ApproveScrollMax = 300;
        const float ApproveW = 452;
        readonly StringFormat approveMeasure = new StringFormat(StringFormat.GenericTypographic);

        string ApproveText(Approval a) { return a.Detail.Length > 0 ? a.Detail : a.Tool; }

        // Wrapped line count, measured once
        int ApproveLines(Approval a)
        {
            if (a.Lines > 0 || p.G == null) return Math.Max(1, a.Lines);
            int chars, lines;
            p.G.MeasureString(ApproveText(a), Ink.Mono(12f), new SizeF(ApproveW - 56, 100000), approveMeasure, out chars, out lines);
            a.Lines = Math.Max(1, lines);
            return a.Lines;
        }

        float ApproveLineH() { return p.G == null ? 15.5f : Ink.Mono(12f).GetHeight(p.G); }

        // New card: a stray click can't allow
        const double ArmSeconds = 0.8;

        bool ApproveArming(DateTime now)
        {
            return approvals.Count > 0 && approvals[0].ShownUtc != default(DateTime) && (now - approvals[0].ShownUtc).TotalSeconds < ArmSeconds + 0.05;
        }

        bool ScrollApproval(int delta)
        {
            if (approvals.Count == 0) return false;
            var a = approvals[0];
            int total = ApproveLines(a), shown = Math.Min(total, ApproveMaxLines);
            if (total <= shown || total > ApproveScrollMax) return false;
            int step = delta / 40;
            if (step == 0) step = Math.Sign(delta);
            a.Scroll = Math.Max(0, Math.Min(total - shown, a.Scroll - step));
            // Reaching the end counts as having read it
            if (a.Scroll >= total - shown) a.ReadAll = true;
            Kick();
            return true;
        }

        float ApproveHeight()
        {
            if (approvals.Count == 0) return 172;
            int total = ApproveLines(approvals[0]), shown = Math.Min(total, ApproveMaxLines);
            return 64 + shown * ApproveLineH() + 20 + (total > shown ? 22 : 0) + 10 + 32 + 14;
        }

        void DrawApprove(float w, float h)
        {
            if (approvals.Count == 0) return;
            var a = approvals[0];
            if (a.ShownUtc == default(DateTime)) a.ShownUtc = DateTime.UtcNow;
            bool armed = (DateTime.UtcNow - a.ShownUtc).TotalSeconds >= ArmSeconds;
            string who = a.Project + (a.Profile.Length > 0 ? " · " + a.Profile : "");
            if (approvals.Count > 1) who = "1/" + approvals.Count + " · " + who;
            p.Text(a.Agent + Lang.T(" minta izin"), Ink.Semi(14.5f), p.T.Text, 24, 28, w - 200, StringAlignment.Near);
            p.TextRight(who, Ink.Small(11.5f), p.T.Sub, w - 24, 28);
            string label = AgentHub.ToolLabel(a.Tool);
            p.Text(label, Ink.Semi(12f), p.T.Text, 24, 51, 140, StringAlignment.Near);
            float lx = 24 + p.Measure(label, Ink.Semi(12f)) + 8;
            p.Text(a.Note, Ink.Text(12f), p.T.Sub, lx, 51, w - lx - 24, StringAlignment.Near);

            // Whole command readable before allowing; long ones scroll
            int total = ApproveLines(a), shown = Math.Min(total, ApproveMaxLines);
            bool clipped = total > shown, scrollable = clipped && total <= ApproveScrollMax;
            float lineH = ApproveLineH();
            var box = new RectangleF(16, 64, w - 32, shown * lineH + 20);
            p.FillRound(box, 12, p.T.Fill);
            var inner = new RectangleF(box.X + 12, box.Y + 10, box.Width - 24, shown * lineH + 1);
            var st = g.Save();
            g.IntersectClip(inner);
            float top = scrollable ? a.Scroll * lineH : 0;
            p.TextBox(ApproveText(a), Ink.Mono(12f), p.T.Text, new RectangleF(inner.X, inner.Y - top, inner.Width, (scrollable ? total : shown) * lineH + 1));
            g.Restore(st);
            if (scrollable)
            {
                // Scroll thumb
                float track = box.Height - 16, thumb = Math.Max(14, track * shown / total);
                float ty = box.Y + 8 + (track - thumb) * a.Scroll / Math.Max(1, total - shown);
                p.FillRound(new RectangleF(box.Right - 6, ty, 3, thumb), 1.5f, p.T.Sub);
            }
            float y = box.Bottom + 10;
            if (clipped && !a.ReadAll)
            {
                string more = scrollable ? Lang.F("Gulir untuk membaca {0} baris lagi.", total - shown - a.Scroll)
                                         : Lang.T("Masih ada ") + (total - shown) + Lang.T(" baris lagi. Baca utuh di terminal.");
                p.Text(more, Ink.Small(11.5f), p.T.Text, 24, y + 4, w - 48, StringAlignment.Near);
                y += 22;
            }
            else if (clipped) y += 22;

            var allow = new RectangleF(w - 16 - 112, y, 112, 32);
            var deny = new RectangleF(allow.X - 8 - 92, y, 92, 32);
            var always = new RectangleF(deny.X - 8 - 84, y, 84, 32);
            bool locked = (clipped && !a.ReadAll) || !armed;
            if (locked) p.StrokeRound(allow, 10, p.T.Faint, 1.2f);
            else p.FillRound(allow, 10, Hover("allow") ? Ink.A(p.T.Text, 0.82f) : p.T.Text);
            p.TextCenter(Lang.T("Izinkan"), Ink.Semi(13f), locked ? p.T.Faint : p.T.Bg, allow.X + allow.Width / 2, allow.Y + 16);
            if (Hover("deny")) p.FillRound(deny, 10, p.T.Fill);
            p.StrokeRound(deny, 10, p.T.Sub, 1.2f);
            p.TextCenter(Lang.T("Tolak"), Ink.Semi(13f), p.T.Text, deny.X + deny.Width / 2, deny.Y + 16);
            if (!locked && Hover("always")) p.FillRound(always, 10, p.T.Fill);
            p.StrokeRound(always, 10, locked ? p.T.Faint : p.T.Sub, 1.2f);
            p.TextCenter(Lang.T("Selalu"), Ink.Semi(13f), locked ? p.T.Faint : p.T.Text, always.X + always.Width / 2, always.Y + 16);
            var term = new RectangleF(14, y, always.X - 8 - 14, 32);
            if (Hover("term")) p.FillRound(term, 10, p.T.Fill);
            p.Icon(Ink.IcoTerminal, 12f, p.T.Sub, term.X + 18, term.Y + 16);
            p.Text(Lang.T("Terminal"), Ink.Small(12f), Hover("term") || (clipped && !scrollable) ? p.T.Text : p.T.Sub, term.X + 32, term.Y + 16, term.Width - 36, StringAlignment.Near);
            if (!locked) AddHit(allow, "allow", delegate { Decide(a, "allow"); });
            if (!locked) AddHit(always, "always", delegate { AllowAlways(a); });
            AddHit(deny, "deny", delegate { Decide(a, "deny"); });
            AddHit(term, "term", delegate { Decide(a, null); });
        }

        void DrawDone(float w, float h, DateTime now)
        {
            var s = toast != null ? toast.Data as AgentSession : null;
            if (s == null) return;
            float cy = h / 2;
            float k = (float)((now - toast.ShownUtc).TotalSeconds / 0.6);
            float pop = Ease.OutBack(Ease.Clamp01(k * 1.6f));
            p.Circle(34, cy, 15 * pop, p.T.Text);
            if (s.Failed) p.Cross(34, cy, 13, p.T.Bg);
            else p.Check(34, cy, 14, p.T.Bg, Ease.OutCubic(k - 0.15f));
            string title = (s.Failed ? Lang.T("Berhenti, ada galat · ") : Lang.T("Selesai · ")) + s.Project;
            p.Text(title, Ink.Semi(13.5f), p.T.Text, 60, cy - 10, w - 60 - 70, StringAlignment.Near);
            if (s.TurnStartedUtc > DateTime.MinValue) p.TextRight(Duration(s.LastUtc - s.TurnStartedUtc), Ink.Small(11.5f), p.T.Sub, w - 22, cy - 10);
            p.Text(s.LastMessage.Length > 0 ? s.LastMessage : Lang.T("Menunggu balasanmu"), Ink.Text(12f), p.T.Sub, 60, cy + 10, w - 60 - 22, StringAlignment.Near);
            AddHit(new RectangleF(0, 0, w, h), "done", delegate { if (!FocusTerminal(s)) Expand(PageAgents); });
        }

        static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return Math.Max(1, (int)t.TotalSeconds) + Lang.T(" dtk");
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + Lang.T(" mnt");
            return (int)t.TotalHours + Lang.T(" jam ") + t.Minutes + Lang.T(" mnt");
        }

        // Messages

        void DrawAppBadge(NotifItem item, RectangleF r)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            p.Circle(cx, cy, r.Width / 2, p.T.Fill);
            if (item.Logo != null)
            {
                var inner = RectangleF.Inflate(r, -r.Width * 0.18f, -r.Height * 0.18f);
                p.Image(item.Logo, inner, inner.Width * 0.24f);
                return;
            }
            string n = item.AppName.Length > 0 ? item.AppName : "?";
            p.TextCenter(n.Substring(0, 1).ToUpperInvariant(), Ink.Semi(r.Height * 0.42f), p.T.Text, cx, cy);
        }

        string NotifBody(NotifItem item)
        {
            if (!settings.GetBool("notif.content", true)) return Lang.T("Pesan baru");
            return item.Body.Length > 0 ? item.Body : item.AppName;
        }

        void DrawNotif(float w, float h)
        {
            var item = toast != null ? toast.Data as NotifItem : null;
            if (item == null) return;
            float cy = h / 2;
            DrawAppBadge(item, new RectangleF(14, cy - 20, 40, 40));
            string meta = notifBurst > 1 ? notifBurst + Lang.T(" pesan · ") + item.AppName : item.AppName;
            float mw = Math.Min(130, p.Measure(meta, Ink.Small(11f)));
            p.TextRight(meta, Ink.Small(11f), p.T.Sub, w - 22, cy - 10);
            p.Text(item.Title.Length > 0 ? item.Title : item.AppName, Ink.Semi(13.5f), p.T.Text, 66, cy - 10, w - 66 - 32 - mw, StringAlignment.Near);
            p.Text(NotifBody(item), Ink.Text(12.5f), p.T.Sub, 66, cy + 10, w - 66 - 22, StringAlignment.Near);
            AddHit(new RectangleF(0, 0, w, h), "notifOpen", delegate { OpenNotifApp(item); });
        }

        // System

        void DrawVolume(float w, float h, DateTime now)
        {
            float cy = h / 2;
            char glyph = muted ? Ink.IcoMute : vol < 0.01f ? Ink.IcoVol0 : vol < 0.34f ? Ink.IcoVol1 : vol < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3;
            p.Icon(glyph, 15f, p.T.Text, 26, cy);
            float x0 = 50, x1 = w - 62;
            p.FillRound(new RectangleF(x0, cy - 3, x1 - x0, 6), 3, p.T.Line);
            float f = muted ? 0 : Ease.Clamp01(volBar.Value);
            p.FillRound(new RectangleF(x0, cy - 3, Math.Max(6, (x1 - x0) * f), 6), 3, p.T.Text);
            volRoller.Draw(p, Ink.Semi(13f), p.T.Text, w - 22, cy, StringAlignment.Far, now);
        }

        void DrawPower(float w, float h)
        {
            float cy = h / 2;
            string kind = toast != null ? toast.Data as string : "";
            p.Battery(20, cy, batteryFill.Value, p.T.Text, onAc);
            string title = kind == "plug" ? Lang.T("Mengisi daya") : kind == "unplug" ? Lang.T("Charger dicabut") : kind == "full" ? Lang.T("Baterai penuh") : Lang.T("Baterai lemah");
            p.Text(title, Ink.Semi(13.5f), p.T.Text, 64, cy, w - 64 - 70, StringAlignment.Near);
            p.TextRight(battery + "%", Ink.Semi(13.5f), p.T.Text, w - 22, cy);
        }

        void DrawNetwork(float w, float h)
        {
            if (toast == null) return;
            float cy = h / 2;
            p.Icon(toast.Flag ? Ink.IcoWifi : Ink.IcoGlobe, 16f, toast.Flag ? p.T.Text : p.T.Sub, 28, cy);
            p.Text(toast.Title, Ink.Semi(13.5f), p.T.Text, 54, cy, 160, StringAlignment.Near);
            p.TextRight(toast.Detail, Ink.Text(12.5f), p.T.Sub, w - 22, cy);
        }

        float PrivacyWidth()
        {
            if (toast == null || p.G == null) return 280;
            return Clamp(p.Measure(toast.Title, Ink.Semi(13f)) + p.Measure(toast.Flag ? Lang.T(" memakai kamera") : Lang.T(" memakai mikrofon"), Ink.Text(12.5f)) + 70, 220, 420);
        }

        void DrawPrivacy(float w, float h)
        {
            if (toast == null) return;
            float cy = h / 2;
            p.Icon(toast.Flag ? Ink.IcoCam : Ink.IcoMic, 14f, p.T.Text, 24, cy);
            var semi = Ink.Semi(13f);
            p.Text(toast.Title, semi, p.T.Text, 44, cy, 200, StringAlignment.Near);
            float x = 44 + Math.Min(200, p.Measure(toast.Title, semi));
            p.Text(toast.Flag ? Lang.T(" memakai kamera") : Lang.T(" memakai mikrofon"), Ink.Text(12.5f), p.T.Sub, x, cy, w - x - 16, StringAlignment.Near);
        }

        float InfoWidth()
        {
            if (toast == null || p.G == null) return 220;
            float t = p.Measure(toast.Title, Ink.Semi(13.5f)) + (toast.Detail.Length > 0 ? 14 + Math.Min(200, p.Measure(toast.Detail, Ink.Text(12.5f))) : 0);
            return Clamp(t + (toast.Glyph != '\0' || (toast.Emblem && emblem != null) ? 74 : 46), 160, 460);
        }

        void DrawInfo(float w, float h)
        {
            if (toast == null) return;
            float cy = h / 2, x = 22;
            if (toast.Emblem && emblem != null) { DrawEmblem(31, cy, 11f); x = 50; }
            else if (toast.Glyph != '\0') { p.Icon(toast.Glyph, 14f, p.T.Text, 30, cy); x = 48; }
            var semi = Ink.Semi(13.5f);
            p.Text(toast.Title, semi, p.T.Text, x, cy, w - x - 20, StringAlignment.Near);
            if (toast.Detail.Length > 0)
            {
                float dx = x + p.Measure(toast.Title, semi) + 14;
                p.Text(toast.Detail, Ink.Text(12.5f), p.T.Sub, dx, cy, w - dx - 20, StringAlignment.Near);
            }
            var act = toast.Data as Action;
            if (act != null) AddHit(new RectangleF(0, 0, w, h), "infoAct", delegate { act(); DismissToast(); });
        }

        // Kulia

        float TaskWidth()
        {
            var t = SoonTask();
            if (t == null || p.G == null) return 240;
            return Clamp(p.Measure(t.Title, Ink.Semi(12.5f)) + 110, 220, 420);
        }

        void DrawCountdownRing(float cx, float cy, float r, KuliaTask t)
        {
            float f = (float)Ease.Clamp01((float)((t.DeadlineUtc - DateTime.UtcNow).TotalHours / 6));
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            g.DrawEllipse(p.P(p.T.Line, 2f), rect);
            if (f > 0.003f) g.DrawArc(p.P(p.T.Text, 2.2f), rect, -90, -360 * f);
        }

        void DrawTask(float w, float h, DateTime now)
        {
            var t = SoonTask();
            if (t == null) return;
            float cy = h / 2;
            DrawCountdownRing(20, cy, 7.5f, t);
            taskRoller.Set(Fmt.Left(t.DeadlineUtc - DateTime.UtcNow));
            var f = Ink.Semi(12f);
            float rw = taskRoller.Width(p, f);
            p.Text(t.Title, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - rw - 30, StringAlignment.Near);
            taskRoller.Draw(p, f, p.T.Sub, w - 18, cy, StringAlignment.Far, now);
            AddHit(new RectangleF(0, 0, w, h), "taskSoon", delegate { Expand(PageTasks); });
        }

        void DrawDeadline(float w, float h)
        {
            var t = toast != null ? toast.Data as KuliaTask : null;
            if (t == null) return;
            float cy = h / 2;
            p.Circle(34, cy, 16, p.T.Text);
            p.Icon(Ink.IcoCalendar, 14f, p.T.Bg, 34, cy);
            var left = t.DeadlineUtc - DateTime.UtcNow;
            p.Text(Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + Lang.T(" menit lagi · ") + (t.Kind == "ujian" ? Lang.T("ujian") : Lang.T("tenggat")), Ink.Semi(13.5f), p.T.Text, 62, cy - 10, w - 84, StringAlignment.Near);
            p.Text(t.Title + (t.Course.Length > 0 ? " · " + t.Course : ""), Ink.Text(12.5f), p.T.Sub, 62, cy + 11, w - 84, StringAlignment.Near);
            AddHit(new RectangleF(0, 0, w, h), "deadline", delegate { OpenUrl(t.Url); DismissToast(); });
        }

        // Bubble

        void DrawBubbleContent(DateTime now, float cy, float bx, float br, float alpha)
        {
            if (alpha <= 0.01f) return;
            p.Alpha = alpha;
            View kind = BubbleKind();
            if (kind == View.Agent)
            {
                var s = BusySession();
                if (s != null && s.State == AgentState.Waiting) p.Icon(Ink.IcoTerminal, 12f, p.T.Text, bx, cy);
                else p.Orbit(bx, cy, 7f, orbitPhase, p.T.Text);
            }
            else if (kind == View.Download) p.Icon(IcoDownload, 12f, p.T.Text, bx, cy);
            else if (kind == View.Focus) DrawFocusRing(bx, cy, 7f);
            else if (kind == View.Task)
            {
                var t = SoonTask();
                if (t != null) DrawCountdownRing(bx, cy, 7f, t);
            }
            p.Alpha = 1f;
        }
    }
}
