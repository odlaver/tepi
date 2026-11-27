using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Lyrics in their own floating window
    partial class Island
    {
        LyricWindow lyricWin;

        bool LyricWinWanted { get { return settings.GetBool("lyrics.win", false); } }

        // Pill gives lyrics up while the window shows them
        bool LyricWinShown { get { return lyricWin != null && !lyricWin.IsDisposed && lyricWin.Visible; } }

        // Background of the lyric window: album, kaca (DWM glass) or tembus
        string LyricLook { get { string v = settings.Get("lyrics.win.look", "album"); return v == "kaca" || v == "tembus" ? v : "album"; } }

        // The window style differs per look: rebuilt
        void SetLyricLook(string look)
        {
            settings.Set("lyrics.win.look", look);
            settings.Save();
            if (lyricWin != null && !lyricWin.IsDisposed) lyricWin.Close();
            lyricWin = null;
            SyncLyricWin();
        }

        void ToggleLyricWin()
        {
            settings.Set("lyrics.win", !LyricWinWanted);
            settings.Save();
            SyncLyricWin();
            Kick();
        }

        // Open while a song is loaded and nothing is fullscreen
        void SyncLyricWin()
        {
            bool want = LyricWinWanted && mediaInfo.Present && mediaInfo.Title.Length > 0 && !fullscreen;
            if (want)
            {
                if (lyricWin == null || lyricWin.IsDisposed)
                {
                    lyricWin = new LyricWindow(this);
                    lyricWin.Show();
                }
                lyricWin.Changed();
            }
            else if (lyricWin != null)
            {
                var w = lyricWin;
                lyricWin = null;
                if (!w.IsDisposed) w.Close();
            }
        }

        Action lyricWinAct;

        // Pop-out beside the title; tinted while the window is open
        void DrawLyricWinButton(float cx, float cy)
        {
            IconButton(cx, cy, 32, "lyricWin", Ink.IcoOpen, lyricWinAct ?? (lyricWinAct = delegate { ToggleLyricWin(); }), LyricWinWanted);
        }

        // Lyrics live in their window: say so, offer the way back
        void DrawLyricsAway(RectangleF box)
        {
            float top = box.Y + 14;
            var disc = new RectangleF(box.X, top, 40, 40);
            p.Module(disc, 20, p.T.Fill);
            p.Icon(Ink.IcoOpen, 16f, p.T.Sub, box.X + 20, top + 20);
            p.Text(Lang.T("Lirik tampil di jendela"), Ink.Semi(14.5f), p.T.Text, box.X + 54, top + 20, box.Width - 54, StringAlignment.Near);
            string back = Lang.T("Kembalikan ke sini");
            float bw = p.Measure(back, Ink.Semi(13f)) + 36;
            Pill(new RectangleF(box.X, top + 58, bw, 32), "lyricBack", back, PillKind.Secondary, lyricWinAct ?? (lyricWinAct = delegate { ToggleLyricWin(); }));
        }

        // Line i becomes the one sung now
        void MatchLyricLine(int i)
        {
            if (lyrics == null || lyrics.Key != mediaInfo.Key || lyrics.State != LyricState.Synced || i < 0 || i >= lyrics.Lines.Count) return;
            double orig = lyrics.Lines[i].AtMs - lyrics.Shift;
            double pos = mediaInfo.Position(DateTime.UtcNow);
            int want = (int)(Math.Round((pos - orig) / 100) * 100);
            want = Math.Max(-LyricShiftMax, Math.Min(LyricShiftMax, want));
            int was = LyricOffset(lyrics.Key);
            if (want == was) return;
            NudgeLyrics(want == 0 ? 0 : want - was);
        }


        sealed class LyricWindow : Form
        {
            readonly Island isle;
            readonly string look;
            readonly Painter p = new Painter();
            DibCanvas surface;
            Bitmap canvas;
            Graphics g;
            int cw, ch;
            float scale = 1;

            // Wheel offset in DIPs
            readonly Spring drift = new Spring(0, 240, 30, 0.05f);
            int index = -1, prevIndex = -1;
            string key;
            DateTime switchUtc = DateTime.MinValue;
            readonly System.Windows.Forms.Timer wake = new System.Windows.Forms.Timer();
            readonly System.Windows.Forms.Timer resume = new System.Windows.Forms.Timer { Interval = 3000 };
            readonly PacedTimer frames = new PacedTimer();
            long lastStep;

            bool hover;
            string hotId, pressId;
            int hotLine = -1;
            Point downAt;
            readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
            readonly List<string> lineIds = new List<string>();

            // Frame without lines: drawn on resize, hover, song change
            DibCanvas frameDib;
            string frameFor;
            // Backdrop: the cover, blurred once per song
            Bitmap blur, blurFor;
            float veil = 0.5f;
            Color songTint = Color.Empty;
            Bitmap songTintFor;
            Tone tone, toneFrom;
            // Wrapped lines, drawn once, then only moved
            readonly Dictionary<int, DibCanvas> lineArt = new Dictionary<int, DibCanvas>();
            readonly HashSet<int> lineUsed = new HashSet<int>();
            float[] restH = new float[0], bigH = new float[0], cBuf = new float[0], hBuf = new float[0];
            float bigRow;
            string layoutFor;
            readonly StringFormat wrapFmt;

            const float Radius = 18, Pad = 14, TextX = 30, SwitchSecs = 0.22f, RollSecs = 0.3f, Gap = 9;
            const int Rest = 0, Past = 1, Big = 2;

            [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
            [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
            [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hw, ref Margins m);
            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hw, int attr, ref int value, int size);
            [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int hh, IntPtr src, int sx, int sy, int rop);
            [DllImport("user32.dll")] static extern bool ReleaseCapture();
            [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int blend);
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hw);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hw, IntPtr dc);
            [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hw, int msg, IntPtr wp, IntPtr lp);

            public LyricWindow(Island isle)
            {
                this.isle = isle;
                look = isle.LyricLook;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                Text = Lang.T("Lirik");
                AccessibleName = Lang.T("Lirik");
                wrapFmt = (StringFormat)StringFormat.GenericTypographic.Clone();
                wrapFmt.FormatFlags |= StringFormatFlags.LineLimit;
                Tune(drift, 260, 0.92f);
                wake.Tick += delegate { wake.Stop(); Follow(false); };
                resume.Tick += delegate { resume.Stop(); drift.Target = 0; Animate(); };
                frames.Tick += delegate { Step(); };
                PlaceAtStart();
            }

            // Read by the base constructor too, before isle is set
            bool OnTop { get { return isle != null && isle.settings.GetBool("lyrics.win.top", true); } }
            float TextK { get { return Math.Max(80, Math.Min(160, isle.settings.GetInt("lyrics.win.text", 100))) / 100f; } }
            float W { get { return cw / scale; } }
            float H { get { return ch / scale; } }
            bool Compact { get { return H < 104; } }
            bool Header { get { return H >= 150 && W >= 240; } }
            float Top0 { get { return Header ? 64 : 6; } }
            float TextW { get { return W - TextX - Pad; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = Native.WS_POPUP;
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                    if (OnTop) cp.ExStyle |= Native.WS_EX_TOPMOST;
                    return cp;
                }
            }

            bool Glass { get { return look == "kaca"; } }
            bool Clear { get { return look == "tembus"; } }

            protected override bool ShowWithoutActivation { get { return true; } }
            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(Handle, ref m);
                int none = unchecked((int)0xFFFFFFFE), square = 1;
                DwmSetWindowAttribute(Handle, 34, ref none, 4);
                DwmSetWindowAttribute(Handle, 33, ref square, 4);
            }

            // Saved spot if still on a screen, else bottom right of the island's monitor
            void PlaceAtStart()
            {
                var at = Rectangle.Empty;
                var parts = isle.settings.Get("lyrics.win.at", "").Split(',');
                int x, y, w, h;
                if (parts.Length == 4 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) && int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h) && w > 40 && h > 30)
                {
                    var r = new Rectangle(x, y, w, h);
                    foreach (var s in Screen.AllScreens)
                    {
                        var both = Rectangle.Intersect(s.WorkingArea, r);
                        if (both.Width >= 60 && both.Height >= 30) { at = r; break; }
                    }
                }
                if (at.IsEmpty)
                {
                    float s = DpiScale(isle.monitorHandle);
                    var work = Screen.FromHandle(isle.Handle).WorkingArea;
                    int pw = (int)(340 * s), ph = (int)(170 * s);
                    at = new Rectangle(work.Right - pw - (int)(24 * s), work.Bottom - ph - (int)(24 * s), pw, ph);
                }
                scale = DpiScale(Native.MonitorFromPoint(new Native.POINT { X = at.X + at.Width / 2, Y = at.Y + at.Height / 2 }, 2));
                MinimumSize = new Size((int)(220 * scale), (int)(64 * scale));
                Bounds = at;
            }

            // Media, lyrics, or timing changed
            public void Changed()
            {
                if (IsDisposed) return;
                Follow(true);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                if (Width <= 0 || Height <= 0 || IsDisposed) return;
                MakeCanvas(Width, Height);
                Relayout();
                Render();
            }

            protected override void OnResizeEnd(EventArgs e)
            {
                base.OnResizeEnd(e);
                // Glass shows the spot it now sits on
                if (Glass) Render();
                isle.settings.Set("lyrics.win.at", Left + "," + Top + "," + Width + "," + Height);
                isle.settings.Save();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                wake.Dispose();
                resume.Dispose();
                frames.Stop();
                Free();
                DropArt();
                if (frameDib != null) frameDib.Dispose();
                if (blur != null) blur.Dispose();
                wrapFmt.Dispose();
                p.Dispose();
                base.OnFormClosed(e);
            }

            Lyrics Current
            {
                get
                {
                    var l = isle.lyrics;
                    return l != null && l.Key == isle.mediaInfo.Key ? l : null;
                }
            }

            bool Synced { get { var l = Current; return l != null && l.State == LyricState.Synced; } }
            bool Listed { get { var l = Current; return l != null && (l.State == LyricState.Synced || l.State == LyricState.Plain) && l.Lines.Count > 0; } }

            // Line under the playhead; the next change is booked on a timer
            void Follow(bool reset)
            {
                wake.Stop();
                var l = Current;
                string k = l == null ? null : l.Key + "|" + l.State + "|" + l.Lines.Count;
                if (k != key)
                {
                    key = k;
                    index = prevIndex = -1;
                    drift.Snap(0);
                    DropArt();
                    Relayout();
                }
                int at = Synced ? l.IndexAt(isle.mediaInfo.Position(DateTime.UtcNow)) : -1;
                if (at != index)
                {
                    prevIndex = index;
                    index = at;
                    // Seeks jump; playback rolls
                    bool jump = reset && Math.Abs(at - prevIndex) > 1;
                    switchUtc = jump ? DateTime.MinValue : DateTime.UtcNow;
                    Animate();
                }
                Render();
                Book();
            }

            void Book()
            {
                if (!Synced || !isle.mediaInfo.Playing) return;
                var l = Current;
                double pos = isle.mediaInfo.Position(DateTime.UtcNow);
                double next = index >= 0 ? l.NextAt(index) : l.Lines.Count > 0 ? l.Lines[0].AtMs : double.MaxValue;
                if (next == double.MaxValue) return;
                wake.Interval = (int)Math.Max(15, Math.Min(600000, next - pos + 15));
                wake.Start();
            }

            void Animate()
            {
                if (Anim.Reduced) { drift.Snap(drift.Target); switchUtc = DateTime.MinValue; Render(); return; }
                if (frames.Enabled) return;
                lastStep = System.Diagnostics.Stopwatch.GetTimestamp();
                frames.Start();
            }

            void Step()
            {
                if (IsDisposed) { frames.Stop(); return; }
                float dt = Anim.Dt(ref lastStep);
                drift.Step(dt);
                bool still = drift.Settled && Since() >= RollSecs;
                if (still) drift.Snap(drift.Target);
                Render();
                if (still) frames.Stop();
            }

            // Fixed length, so a line change costs a known number of frames
            float Since() { return switchUtc == DateTime.MinValue ? 99 : (float)(DateTime.UtcNow - switchUtc).TotalSeconds; }
            float SwitchK() { return Ease.Clamp01(Since() / SwitchSecs); }
            float RollK() { return Ease.OutCubic(Ease.Clamp01(Since() / RollSecs)); }

            void MakeCanvas(int w, int h)
            {
                Free();
                cw = w;
                ch = h;
                surface = new DibCanvas(w, h);
                canvas = surface.Wrap();
                g = Graphics.FromImage(canvas);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                p.G = g;
                frameFor = null;
            }

            void Free()
            {
                if (g == null) return;
                g.Dispose();
                canvas.Dispose();
                surface.Dispose();
                surface = null;
                g = null;
            }

            void DropArt()
            {
                foreach (var b in lineArt.Values) b.Dispose();
                lineArt.Clear();
            }

            Font RestFont { get { return Ink.Text(14.5f * TextK * scale); } }
            Font BigFont { get { return Ink.Display(18f * TextK * scale); } }

            // Slot per line: tall enough for its larger, wrapped self
            void Relayout()
            {
                var l = Current;
                string want = l == null || g == null ? null : key + "|" + cw + "|" + TextK + "|" + scale + "|" + Ink.Light;
                if (want == layoutFor) return;
                layoutFor = want;
                DropArt();
                int n = l == null ? 0 : l.Lines.Count;
                restH = new float[n];
                bigH = new float[n];
                cBuf = new float[n];
                hBuf = new float[n];
                if (n == 0) return;
                bool synced = l.State == LyricState.Synced;
                float widthPx = TextW * scale;
                var st = g.Save();
                g.ResetTransform();
                bigRow = g.MeasureString("Ag", BigFont, PointF.Empty, wrapFmt).Height / scale;
                for (int i = 0; i < n; i++)
                {
                    string t = l.Lines[i].Text;
                    if (t.Length == 0) continue;
                    restH[i] = (float)Math.Ceiling(g.MeasureString(t, RestFont, new SizeF(widthPx, 4000), wrapFmt).Height) / scale;
                    bigH[i] = synced ? (float)Math.Ceiling(g.MeasureString(t, BigFont, new SizeF(widthPx, 4000), wrapFmt).Height) / scale : restH[i];
                }
                g.Restore(st);
            }

            // One line in one look, drawn once
            DibCanvas Art(Lyrics l, int i, int look)
            {
                int k = i * 3 + look;
                lineUsed.Add(k);
                DibCanvas d;
                if (lineArt.TryGetValue(k, out d)) return d;
                var font = look == Big ? BigFont : RestFont;
                Color c = look == Big ? p.T.Text : look == Past ? p.T.Hint : p.T.Sub;
                int w = (int)Math.Ceiling(TextW * scale), h = Math.Max(1, (int)Math.Ceiling((look == Big ? bigH[i] : restH[i]) * scale) + 3);
                d = new DibCanvas(w, h);
                using (var bmp = d.Wrap())
                using (var bg = Graphics.FromImage(bmp))
                using (var br = new SolidBrush(c))
                {
                    bg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    // No background behind: a shadow keeps the words readable anywhere
                    if (Clear)
                        using (var sh = new SolidBrush(Color.FromArgb(Ink.Light ? 120 : 170, Ink.Light ? 255 : 0, Ink.Light ? 255 : 0, Ink.Light ? 255 : 0)))
                            bg.DrawString(l.Lines[i].Text, font, sh, new RectangleF(0, 1.2f * scale, w, h), wrapFmt);
                    bg.DrawString(l.Lines[i].Text, font, br, new RectangleF(0, 0, w, h), wrapFmt);
                }
                lineArt[k] = d;
                return d;
            }

            // GDI AlphaBlend: far cheaper than a faded GDI+ DrawImage
            void Blit(DibCanvas d, float xDip, float topDip, float alpha, int clipTop, int clipBottom)
            {
                int a = (int)(alpha * 255 + 0.5f);
                if (a <= 2) return;
                int x = (int)Math.Round(xDip * scale), y = (int)Math.Round(topDip * scale), sy = 0, h = d.Height;
                if (y < clipTop) { sy = clipTop - y; h -= sy; y = clipTop; }
                if (y + h > clipBottom) h = clipBottom - y;
                int w = Math.Min(d.Width, cw - x);
                if (h <= 0 || w <= 0) return;
                AlphaBlend(surface.Dc, x, y, w, h, d.Dc, 0, sy, w, h, (Math.Min(255, a) << 16) | (1 << 24));
            }

            void Render()
            {
                if (g == null || IsDisposed) return;
                hits.Clear();
                if (!ReferenceEquals(toneFrom, Ink.Base)) { toneFrom = Ink.Base; tone = Ink.Base.OnGlass(); }
                p.T = tone;
                p.HalfPx = 0.5f / scale;
                p.Alpha = 1;
                Relayout();
                DrawFrame();
                g.Flush(FlushIntention.Sync);
                BitBlt(surface.Dc, 0, 0, cw, ch, frameDib.Dc, 0, 0, 0x00CC0020);
                g.ResetTransform();
                var area = new RectangleF(0, Top0, W, H - Top0 - 4);
                var l = Current;
                if (Listed) DrawLines(area, l);
                g.ScaleTransform(scale, scale);
                if (!Listed) DrawNote(area, l);
                if (hover) DrawControls();
                g.Flush(FlushIntention.Sync);
                if (!IsHandleCreated) return;
                IntPtr hdc = GetDC(Handle);
                BitBlt(hdc, 0, 0, cw, ch, surface.Dc, 0, 0, 0x00CC0020);
                ReleaseDC(Handle, hdc);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (g == null) return;
                IntPtr hdc = e.Graphics.GetHdc();
                BitBlt(hdc, 0, 0, cw, ch, surface.Dc, 0, 0, 0x00CC0020);
                e.Graphics.ReleaseHdc(hdc);
            }

            // Background, edge, cover and titles; redrawn only when they change
            // Blurred cover under a glass veil, sheen and rim; drawn once per song, size and hover
            void DrawFrame()
            {
                var m = isle.mediaInfo;
                // Glass follows the spot on screen and the wallpaper
                string glassKey = Glass ? Location + Wallpaper.Key(Mon()) : "";
                string want = look + glassKey + "|" + cw + "|" + ch + "|" + Header + "|" + hover + "|" + m.Title + "|" + m.Artist + "|" + (m.Cover == null ? 0 : m.Cover.GetHashCode()) + "|" + Ink.Light;
                if (want == frameFor && frameDib != null) return;
                frameFor = want;
                SongTint(m.Cover);
                Blurred(m.Cover);
                if (frameDib == null || frameDib.Width != cw || frameDib.Height != ch)
                {
                    if (frameDib != null) frameDib.Dispose();
                    frameDib = new DibCanvas(cw, ch);
                }
                using (var fb = frameDib.Wrap())
                using (var fg = Graphics.FromImage(fb))
                {
                    fg.SmoothingMode = SmoothingMode.AntiAlias;
                    fg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    fg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    fg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    fg.Clear(Color.Transparent);
                    fg.ScaleTransform(scale, scale);
                    var keep = p.G;
                    p.G = fg;
                    var r = new RectangleF(0.5f, 0.5f, W - 1, H - 1);
                    // Glass: the wallpaper behind, lightly blurred, with veil, sheen and rim
                    if (Glass || Clear)
                    {
                        if (Glass) GlassEdge(fg, r);
                        else if (hover) p.FillRound(r, 14, Ink.A(Ink.Base.Bg, 0.45f));
                    }
                    else using (var path = Ink.Round(r, Radius))
                    {
                        fg.FillPath(p.B(Ink.Base.Bg), path);
                        if (blur != null)
                        {
                            // Cover fills the window, a little oversized so the soft edges stay outside
                            float side = Math.Max(W, H) * 1.25f;
                            using (var tb = new TextureBrush(blur, WrapMode.TileFlipXY))
                            {
                                tb.TranslateTransform((W - side) / 2, (H - side) / 2);
                                tb.ScaleTransform(side / blur.Width, side / blur.Height);
                                fg.FillPath(tb, path);
                            }
                            // Veil sized to the cover's brightest or darkest spot: text keeps its contrast
                            fg.FillPath(p.B(Ink.A(Ink.Base.Bg, veil)), path);
                        }
                        var st = fg.Save();
                        fg.SetClip(path, CombineMode.Intersect);
                        // Sheen along the top, like the island's glass
                        using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, 62), Color.FromArgb(Ink.Light ? 90 : 34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        {
                            lg.WrapMode = WrapMode.TileFlipXY;
                            fg.FillRectangle(lg, r.X, r.Y, r.Width, 58);
                        }
                        fg.Restore(st);
                        fg.DrawPath(p.P(Color.FromArgb(Ink.Light ? 110 : 56, 255, 255, 255), 1f), path);
                    }
                    p.Shadow = Clear;
                    if (Header)
                    {
                        var box = new RectangleF(Pad, 14, 40, 40);
                        if (m.Cover != null)
                            using (var clip = Ink.Round(box, 10))
                            using (var tb = new TextureBrush(m.Cover, WrapMode.Clamp))
                            {
                                tb.TranslateTransform(box.X, box.Y);
                                tb.ScaleTransform(box.Width / m.Cover.Width, box.Height / m.Cover.Height);
                                fg.FillPath(tb, clip);
                                fg.DrawPath(p.P(Color.FromArgb(40, 255, 255, 255), 1f), clip);
                            }
                        else
                        {
                            p.FillRound(box, 10, p.T.Fill);
                            p.Icon((char)0xE8D6, 15f, p.T.Sub, box.X + 20, box.Y + 20);
                        }
                        float x = box.Right + 12, tw = W - x - (hover ? 44 : Pad);
                        p.Text(m.Title, Ink.Semi(14f), p.T.Text, x, 27, tw, StringAlignment.Near);
                        p.Text(m.Artist, Ink.Text(12.5f), p.T.Sub, x, 44, tw, StringAlignment.Near);
                    }
                    p.Shadow = false;
                    p.G = keep;
                }
            }

            Rectangle Mon() { return Screen.FromRectangle(Bounds).Bounds; }

            // Wallpaper behind the window, then veil, sheen and rim
            void GlassEdge(Graphics fg, RectangleF r)
            {
                float lo, hi;
                using (var path = Ink.Round(r, Radius))
                using (var behind = Wallpaper.Cut(Mon(), Bounds, cw, ch, out lo, out hi))
                {
                    if (behind != null)
                        using (var tb = new TextureBrush(behind, WrapMode.Clamp))
                        {
                            tb.ScaleTransform(1 / scale, 1 / scale);
                            fg.FillPath(tb, path);
                        }
                    else fg.FillPath(p.B(Ink.Base.Bg), path);
                    float v = Ink.Light ? Math.Min(0.72f, 0.24f + 0.45f * (1 - lo)) : Math.Min(0.72f, 0.12f + 0.5f * hi);
                    fg.FillPath(p.B(Ink.A(Ink.Base.Bg, v)), path);
                    var st = fg.Save();
                    fg.SetClip(path, CombineMode.Intersect);
                    using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, 62), Color.FromArgb(Ink.Light ? 90 : 34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    {
                        lg.WrapMode = WrapMode.TileFlipXY;
                        fg.FillRectangle(lg, r.X, r.Y, r.Width, 58);
                    }
                    fg.Restore(st);
                    fg.DrawPath(p.P(Color.FromArgb(Ink.Light ? 110 : 60, 255, 255, 255), 1f), path);
                }
            }

            // Cover shrunk to a few pixels: stretched back up it is a soft blur
            void Blurred(Bitmap cover)
            {
                if (ReferenceEquals(cover, blurFor)) return;
                blurFor = cover;
                if (blur != null) { blur.Dispose(); blur = null; }
                if (cover == null) return;
                try
                {
                    using (var mid = new Bitmap(48, 48, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var mg = Graphics.FromImage(mid))
                        {
                            mg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            mg.DrawImage(cover, new Rectangle(0, 0, 48, 48));
                        }
                        blur = new Bitmap(10, 10, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var bg = Graphics.FromImage(blur))
                        using (var vivid = new System.Drawing.Imaging.ImageAttributes())
                        {
                            // A little more colour than the cover, like light through tinted glass
                            const float S = 1.4f, R = 0.2126f * (1 - S), G = 0.7152f * (1 - S), B = 0.0722f * (1 - S);
                            vivid.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[]
                            {
                                new[] { R + S, R, R, 0f, 0f }, new[] { G, G + S, G, 0f, 0f }, new[] { B, B, B + S, 0f, 0f },
                                new[] { 0f, 0f, 0f, 1f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f }
                            }));
                            bg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            bg.DrawImage(mid, new Rectangle(0, 0, 10, 10), 0, 0, 48, 48, GraphicsUnit.Pixel, vivid);
                        }
                    }
                    // Brightest spot for dark text, darkest for light text
                    float lo = 1, hi = 0;
                    for (int y = 0; y < blur.Height; y++)
                        for (int x = 0; x < blur.Width; x++)
                        {
                            var c = blur.GetPixel(x, y);
                            float l = (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
                            if (l < lo) lo = l;
                            if (l > hi) hi = l;
                        }
                    veil = Ink.Light ? Math.Min(0.8f, 0.32f + 0.42f * (1 - lo)) : Math.Min(0.78f, 0.2f + 0.5f * hi);
                }
                catch (Exception ex) { Log.Write("lyric cover: " + ex.Message); blur = null; }
            }

            // Cover colour kept at 3:1 on the window; once per song
            void SongTint(Bitmap cover)
            {
                if (ReferenceEquals(cover, songTintFor)) return;
                songTintFor = cover;
                songTint = Color.Empty;
                if (cover == null) return;
                try
                {
                    string hex = CoverAccent(cover, Ink.Base.Bg);
                    if (hex.Length > 0) songTint = ColorTranslator.FromHtml(hex);
                }
                catch (Exception ex) { Log.Write("lyric tint: " + ex.Message); }
            }

            void DrawNote(RectangleF area, Lyrics l)
            {
                string text = l == null || l.State == LyricState.Loading ? Lang.T("Mencari lirik…")
                    : l.State == LyricState.Failed ? Lang.T("Server lirik tidak terjangkau · dicoba lagi otomatis")
                    : Lang.T("Lirik tidak ditemukan di LRCLIB maupun NetEase");
                if (!Header) p.Text(isle.mediaInfo.Title, Ink.Semi(13.5f), p.T.Text, Pad, area.Y + area.Height / 2 - 11, W - 2 * Pad, StringAlignment.Near);
                p.Text(text, Ink.Text(12.5f), p.T.Sub, Pad, Header ? area.Y + 18 : area.Y + area.Height / 2 + 10, W - 2 * Pad, StringAlignment.Near);
            }

            // How lit line i is: the sung one grows in, the last one leaves faster
            float On(int i, float k) { return i == index ? k : i == prevIndex ? (1 - k) * (1 - k) : 0; }

            // Gone before it reaches the edge, never cut
            static float Edge(float cy, float h, RectangleF area)
            {
                return Ease.Clamp01((cy - h / 2 - area.Top + 4) / 16f) * Ease.Clamp01((area.Bottom - cy - h / 2 + 4) / 16f);
            }

            // Centres with the lit line taller; the rest close up
            void Stack(Lyrics l, bool synced, float k)
            {
                float y = 0, gap = Gap * TextK, blank = 10 * TextK;
                for (int i = 0; i < l.Lines.Count; i++)
                {
                    float h = l.Lines[i].Text.Length == 0 ? blank : restH[i] + (synced ? (bigH[i] - restH[i]) * On(i, k) : 0) + gap;
                    hBuf[i] = h;
                    cBuf[i] = y + h / 2;
                    y += h;
                }
            }

            // Lines move as finished pictures; the sung one is larger and marked by a small arrow
            void DrawLines(RectangleF area, Lyrics l)
            {
                bool synced = l.State == LyricState.Synced;
                float k = Ease.OutCubic(SwitchK());
                int n = l.Lines.Count;
                Stack(l, synced, k);
                float focal, anchor;
                if (synced)
                {
                    float from = cBuf[Math.Max(0, prevIndex < 0 ? index : prevIndex)], to = cBuf[Math.Max(0, index)];
                    focal = from + (to - from) * RollK() + drift.Value;
                    anchor = Compact ? area.Y + area.Height / 2 - 4 : area.Y + Math.Max(hBuf[Math.Max(0, index)] * 0.8f, area.Height * 0.32f);
                }
                else
                {
                    focal = cBuf[0] + drift.Value;
                    anchor = area.Y + 14 + hBuf[0] / 2;
                }
                int clipTop = (int)(area.Y * scale), clipBottom = (int)(area.Bottom * scale);
                float k2 = k;
                // Hover band first, through GDI+
                if (synced && hotLine >= 0 && hotLine < n && l.Lines[hotLine].Text.Length > 0)
                {
                    float hy = anchor + cBuf[hotLine] - focal;
                    g.SetClip(new Rectangle(0, clipTop, cw, clipBottom - clipTop));
                    g.ScaleTransform(scale, scale);
                    p.FillRound(new RectangleF(6, hy - hBuf[hotLine] / 2 + 1, W - 12, hBuf[hotLine] - 2), 8, Ink.A(p.T.Text, 0.06f));
                    g.ResetTransform();
                    g.ResetClip();
                }
                g.Flush(FlushIntention.Sync);
                lineUsed.Clear();
                for (int i = 0; i < n; i++)
                {
                    float cy = anchor + cBuf[i] - focal;
                    if (cy + hBuf[i] / 2 < area.Top) continue;
                    if (cy - hBuf[i] / 2 > area.Bottom) break;
                    if (l.Lines[i].Text.Length == 0) continue;
                    float edge = Edge(cy, restH[i], area);
                    if (synced) hits.Add(new KeyValuePair<RectangleF, string>(new RectangleF(0, cy - hBuf[i] / 2, W, hBuf[i]), LineId(i)));
                    float on = synced ? On(i, k2) : 0;
                    if (on < 0.99f) Blit(Art(l, i, synced && i < index ? Past : Rest), TextX, cy - restH[i] / 2, edge * (1 - on), clipTop, clipBottom);
                    if (on > 0.01f) Blit(Art(l, i, Big), TextX, cy - bigH[i] / 2, edge * on, clipTop, clipBottom);
                }
                // Arrows on the lit lines, through GDI+
                g.SetClip(new Rectangle(0, clipTop, cw, clipBottom - clipTop));
                g.ScaleTransform(scale, scale);
                foreach (int i in new[] { prevIndex, index })
                {
                    if (!synced || i < 0 || i >= n || l.Lines[i].Text.Length == 0) continue;
                    float on = On(i, k2);
                    if (on <= 0.01f || (i == prevIndex && i == index)) continue;
                    float cy = anchor + cBuf[i] - focal;
                    float edge = Edge(cy, restH[i], area);
                    p.Alpha = edge * on;
                    Arrow(Pad + 1, cy - bigH[i] / 2 + bigRow / 2, 5.5f * TextK, songTint != Color.Empty ? songTint : p.T.Text);
                }
                p.Alpha = 1;
                g.ResetTransform();
                g.ResetClip();
                // Pictures off screen go
                if (lineArt.Count > 24)
                {
                    var gone = new List<int>();
                    foreach (var kv in lineArt) if (!lineUsed.Contains(kv.Key)) gone.Add(kv.Key);
                    foreach (int gk in gone) { lineArt[gk].Dispose(); lineArt.Remove(gk); }
                }
            }

            void Arrow(float x, float cy, float s, Color c)
            {
                var pts = new[] { new PointF(x, cy - s), new PointF(x + s * 1.05f, cy), new PointF(x, cy + s) };
                g.FillPolygon(p.B(c), pts);
            }

            // Shown on hover: close, previous, play, next
            void DrawControls()
            {
                var close = new RectangleF(W - 36, 8, 28, 28);
                Button(close, "close");
                p.Symbol(Sym.Xmark, close.X + 14, close.Y + 14, 10f, Ink.Mix(p.T.Sub, p.T.Text, hotId == "close" ? 1 : 0));
                if (H < 90) return;
                float cy = H - 24, cx = W / 2;
                var strip = new RectangleF(cx - 66, cy - 18, 132, 36);
                p.FillRound(strip, 18, Ink.A(Ink.Base.Bg, 0.9f));
                using (var edge = Ink.Round(strip, 18)) g.DrawPath(p.P(Color.FromArgb(Ink.Light ? 90 : 44, 255, 255, 255), 1f), edge);
                var prev = new RectangleF(cx - 58, cy - 15, 30, 30);
                var play = new RectangleF(cx - 16, cy - 16, 32, 32);
                var next = new RectangleF(cx + 28, cy - 15, 30, 30);
                Button(prev, "prev");
                Button(play, "play");
                Button(next, "next");
                p.Skip(prev.X + 15, prev.Y + 15, 9f, p.T.Text, false);
                if (isle.mediaInfo.Playing) p.Pause(cx, cy, 11f, p.T.Text);
                else p.Play(cx, cy, 11f, p.T.Text);
                p.Skip(next.X + 15, next.Y + 15, 9f, p.T.Text, true);
            }

            void Button(RectangleF r, string id)
            {
                hits.Add(new KeyValuePair<RectangleF, string>(r, id));
                if (hotId == id) p.Circle(r.X + r.Width / 2, r.Y + r.Height / 2, r.Width / 2, pressId == id ? p.T.FillHover : p.T.Fill);
            }

            string HitAt(Point pt)
            {
                var d = new PointF(pt.X / scale, pt.Y / scale);
                for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].Key.Contains(d)) return hits[i].Value;
                return null;
            }

            static bool IsLine(string id) { return id != null && id.Length > 1 && id[0] == 'l' && char.IsDigit(id[1]); }

            // Ids reused across frames: no strings per line per frame
            string LineId(int i)
            {
                while (lineIds.Count <= i) lineIds.Add("l" + lineIds.Count);
                return lineIds[i];
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                // Drag from anywhere but a button moves the window
                if (e.Button == MouseButtons.Left && (pressId == null || IsLine(pressId)))
                {
                    if (Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > 4)
                    {
                        pressId = null;
                        ReleaseCapture();
                        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                    }
                    return;
                }
                string id = HitAt(e.Location);
                bool was = hover;
                hover = true;
                if (id == hotId && was) return;
                hotId = id;
                hotLine = IsLine(id) ? int.Parse(id.Substring(1)) : -1;
                Cursor = id != null ? Cursors.Hand : Cursors.Default;
                Render();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hover = false;
                hotId = null;
                hotLine = -1;
                Render();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                downAt = e.Location;
                pressId = HitAt(e.Location);
                Render();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                string id = HitAt(e.Location), was = pressId;
                pressId = null;
                if (e.Button == MouseButtons.Right) { LineMenu(IsLine(id) ? int.Parse(id.Substring(1)) : -1); return; }
                if (id == null || id != was) { Render(); return; }
                var media = isle.media;
                switch (id)
                {
                    case "close": isle.ToggleLyricWin(); return;
                    case "prev": media.Previous(); break;
                    case "play": media.TogglePlay(); break;
                    case "next": media.Next(); break;
                    default:
                        var l = Current;
                        int i;
                        if (IsLine(id) && Synced && int.TryParse(id.Substring(1), out i) && i < l.Lines.Count && isle.mediaInfo.CanSeek)
                        {
                            drift.Snap(0);
                            media.Seek(l.Lines[i].AtMs + 50);
                        }
                        break;
                }
                Render();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (!Listed || cBuf.Length == 0) return;
                float at = cBuf[Math.Max(0, index)];
                drift.Target = Math.Max(cBuf[0] - at, Math.Min(cBuf[cBuf.Length - 1] - at, drift.Target - e.Delta / 120f * 60 * TextK));
                Animate();
                // Synced lyrics drift back to the sung line
                resume.Stop();
                if (Synced) resume.Start();
            }

            void LineMenu(int line)
            {
                var m = new ContextMenuStrip();
                var l = Current;
                if (l != null && l.State == LyricState.Synced)
                {
                    int now = isle.LyricOffset(l.Key);
                    m.Items.Add(new ToolStripMenuItem(now == 0 ? Lang.T("Waktu lirik asli") : Lang.F("Waktu lirik digeser {0}", ShiftText(now))) { Enabled = false });
                    if (line >= 0) m.Items.Add(Lang.T("Samakan waktu ke baris ini"), null, delegate { isle.MatchLyricLine(line); });
                    m.Items.Add(Lang.T("Lirik lebih awal 0,5 dtk"), null, delegate { isle.NudgeLyrics(-LyricStep); });
                    m.Items.Add(Lang.T("Lirik lebih lambat 0,5 dtk"), null, delegate { isle.NudgeLyrics(LyricStep); });
                    if (now != 0) m.Items.Add(Lang.T("Kembalikan waktu asli"), null, delegate { isle.NudgeLyrics(0); });
                    m.Items.Add(new ToolStripSeparator());
                }
                var bg2 = new ToolStripMenuItem(Lang.T("Latar"));
                foreach (var opt in new[] { new[] { "album", "Sampul album" }, new[] { "kaca", "Kaca" }, new[] { "tembus", "Tembus" } })
                {
                    string v = opt[0];
                    bg2.DropDownItems.Add(new ToolStripMenuItem(Lang.T(opt[1]), null, delegate { isle.SetLyricLook(v); }) { Checked = look == v });
                }
                m.Items.Add(bg2);
                m.Items.Add(new ToolStripMenuItem(Lang.T("Selalu di atas"), null, delegate { SetTop(!OnTop); }) { Checked = OnTop });
                var bigger = m.Items.Add(Lang.T("Huruf lebih besar"), null, delegate { SetText(10); });
                bigger.Enabled = TextK < 1.6f;
                var smaller = m.Items.Add(Lang.T("Huruf lebih kecil"), null, delegate { SetText(-10); });
                smaller.Enabled = TextK > 0.8f;
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Lang.T("Kembalikan lirik ke island"), null, delegate { isle.ToggleLyricWin(); });
                isle.StyleMenu(m);
                m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
                m.Show(Cursor.Position);
            }

            void SetTop(bool on)
            {
                isle.settings.Set("lyrics.win.top", on);
                isle.settings.Save();
                Native.SetWindowPos(Handle, on ? (IntPtr)(-1) : (IntPtr)(-2), 0, 0, 0, 0, 0x13);
            }

            void SetText(int step)
            {
                int v = Math.Max(80, Math.Min(160, isle.settings.GetInt("lyrics.win.text", 100) + step));
                isle.settings.Set("lyrics.win.text", v);
                isle.settings.Save();
                drift.Snap(0);
                Relayout();
                Render();
            }

            protected override void WndProc(ref Message m)
            {
                const int NcHitTest = 0x84, DpiChanged = 0x02E0;
                if (m.Msg == NcHitTest)
                {
                    base.WndProc(ref m);
                    var pt = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                    int edge = (int)(6 * scale);
                    bool l = pt.X < edge, r = pt.X >= Width - edge, t = pt.Y < edge, b = pt.Y >= Height - edge;
                    int code = t ? (l ? 13 : r ? 14 : 12) : b ? (l ? 16 : r ? 17 : 15) : l ? 10 : r ? 11 : 0;
                    if (code != 0) m.Result = (IntPtr)code;
                    return;
                }
                if (m.Msg == DpiChanged)
                {
                    scale = ((int)(long)m.WParam & 0xFFFF) / 96f;
                    MinimumSize = new Size((int)(220 * scale), (int)(64 * scale));
                    var rc = (Rect)Marshal.PtrToStructure(m.LParam, typeof(Rect));
                    Bounds = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                    return;
                }
                base.WndProc(ref m);
            }
        }
    }
}
