using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Tepi
{
    // Music page: lyrics or album card
    partial class Island
    {
        readonly Spring vinylOut = new Spring(0, 110, 13, 0.002f);
        float vinylAngle;
        bool albumShown, albumKnown;
        DateTime musicModeUtc = DateTime.MinValue;
        Bitmap blurCache, blurSource, labelSource;
        string blurKey;

        // Cover colour, eased on track change
        Bitmap toneSource;
        int toneEpoch = -1;
        Color toneFrom, toneTo;
        DateTime toneUtc = DateTime.MinValue;
        const double ToneSeconds = 0.5;
        Action actMediaPlay;
        string mediaTailKey, mediaTail = "";

        // Raw cover colour; Empty for grey art
        Color CoverTone { get { return mediaInfo.Present ? Ink.ToneOf(mediaInfo.Cover) : Color.Empty; } }

        // Cover colour on glass, else primary
        Color CoverInk(DateTime now)
        {
            var src = mediaInfo.Present ? mediaInfo.Cover : null;
            if (!ReferenceEquals(src, toneSource) || toneEpoch != Ink.Epoch)
            {
                Color raw = Ink.ToneOf(src), next = raw.A > 0 ? Ink.Readable(raw, !Ink.Base.IsDark) : Ink.Base.Text;
                toneFrom = toneEpoch == Ink.Epoch ? ToneAt(now) : next;
                toneTo = next;
                toneSource = src;
                toneEpoch = Ink.Epoch;
                toneUtc = now;
            }
            return ToneAt(now);
        }

        Color ToneAt(DateTime now)
        {
            float k = Ease.OutCubic((float)((now - toneUtc).TotalSeconds / ToneSeconds));
            return k >= 1 ? toneTo : Ink.Mix(toneFrom, toneTo, k);
        }

        bool ToneMoving() { return (DateTime.UtcNow - toneUtc).TotalSeconds < ToneSeconds; }

        // " · artist", built once per track
        string MediaTail()
        {
            if (mediaTailKey != mediaInfo.Key)
            {
                mediaTailKey = mediaInfo.Key;
                mediaTail = mediaInfo.Artist.Length > 0 ? " · " + mediaInfo.Artist : "";
            }
            return mediaTail;
        }

        Action MediaPlayAct()
        {
            if (actMediaPlay == null) actMediaPlay = delegate { media.TogglePlay(); };
            return actMediaPlay;
        }

        bool AlbumMode()
        {
            string v = settings.Get("music.view", "auto");
            if (v == "album") return true;
            if (v == "lyrics") return false;
            bool hasLyrics = lyrics != null && lyrics.Key == mediaInfo.Key &&
                             (lyrics.State == LyricState.Synced || lyrics.State == LyricState.Plain);
            return !hasLyrics;
        }

        void ToggleMusicView()
        {
            settings.Set("music.view", AlbumMode() ? "lyrics" : "album");
            settings.Save();
            Kick();
        }

        void StepMusic(float dt)
        {
            bool onPage = current == View.Panel && page == PageMusic;
            vinylOut.Target = onPage ? (mediaInfo.Playing ? 1f : 0.3f) : 0f;
            vinylOut.Step(dt);
            if (mediaInfo.Playing) vinylAngle = (vinylAngle + dt * 150f) % 360f;
        }

        bool MusicMoving(DateTime now)
        {
            return !vinylOut.Settled || (now - musicModeUtc).TotalSeconds < 0.45;
        }

        // Blurred cover behind the whole panel
        void DrawBackdrop(float alpha)
        {
            if (alpha <= 0.01f || current != View.Panel || backdropInSurface) return;
            var baked = BackdropFor();
            if (baked == null) return;
            int pw = baked.Width, ph = baked.Height;
            var pts = new[] { new PointF(0, 0) };
            g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, pts);
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var dst = new Rectangle((int)Math.Round(pts[0].X), (int)Math.Round(pts[0].Y), pw, ph);
            float a = p.Alpha * alpha;
            if (a >= 0.99f) g.DrawImage(baked, dst, 0, 0, pw, ph, GraphicsUnit.Pixel);
            else
                using (var attr = new ImageAttributes())
                {
                    attr.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                    g.DrawImage(baked, dst, 0, 0, pw, ph, GraphicsUnit.Pixel, attr);
                }
            g.Restore(st);
        }

        bool backdropBaking;
        string backdropWant;

        // Baked once at final size, off-thread
        Bitmap BackdropFor()
        {
            var src = mediaInfo.Cover;
            if (src == null) return null;
            int pw = (int)Math.Ceiling(PanelW * scale), ph = (int)Math.Ceiling(MusicHeight() * scale);
            string key = pw + "x" + ph + Ink.Light;
            if (ReferenceEquals(src, blurSource) && key == blurKey) return blurCache;
            string want = key + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(src);
            if (!backdropBaking && want != backdropWant)
            {
                backdropBaking = true;
                backdropWant = want;
                var copy = (Bitmap)src.Clone(); // GDI+ bitmaps aren't thread-safe
                Color tone = p.T.Bg, wash = Ink.ToneOf(src);
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    Bitmap baked = null, forFrost = null;
                    try
                    {
                        baked = BakeBackdrop(copy, pw, ph, tone, wash);
                        // The frost thread gets its own pixels
                        forFrost = DeepCopy(baked);
                    }
                    catch (Exception ex) { Log.Write("backdrop: " + ex.Message); }
                    finally { copy.Dispose(); }
                    Post(() =>
                    {
                        backdropBaking = false;
                        if (baked == null) { if (forFrost != null) forFrost.Dispose(); return; }
                        if (current != View.Panel || !ReferenceEquals(mediaInfo.Cover, src)) { baked.Dispose(); if (forFrost != null) forFrost.Dispose(); return; }
                        if (blurCache != null) blurCache.Dispose();
                        blurCache = baked;
                        blurSource = src;
                        blurKey = key;
                        PublishBackdrop(forFrost);
                        Kick();
                    });
                });
            }
            return null;
        }

        static Bitmap BakeBackdrop(Bitmap src, int pw, int ph, Color tone, Color wash)
        {
            var baked = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
            using (var bg = Graphics.FromImage(baked))
            using (var soft = Blur(src, Math.Max(8, pw / 8), Math.Max(8, ph / 8)))
            using (var attr = new ImageAttributes())
            {
                attr.SetColorMatrix(new ColorMatrix { Matrix33 = 0.55f });
                bg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                bg.PixelOffsetMode = PixelOffsetMode.Half;
                bg.DrawImage(soft, new Rectangle(0, 0, pw, ph), 0, 0, soft.Width, soft.Height, GraphicsUnit.Pixel, attr);
                // Cover wash; frost plate inherits it
                if (wash.A > 0)
                    using (var wb = new SolidBrush(Color.FromArgb(56, wash))) bg.FillRectangle(wb, 0, 0, pw, ph);
                using (var lg = new LinearGradientBrush(new RectangleF(0, -1, pw, ph + 2), Ink.A(tone, 0.2f), Ink.A(tone, 0.92f), 90f))
                    bg.FillRectangle(lg, 0, 0, pw, ph);
            }
            return baked;
        }

        // Panel closed: its big bitmaps go
        void DropBackdrop()
        {
            if (blurCache == null) return;
            blurCache.Dispose();
            blurCache = null;
            blurKey = null;
            backdropWant = null;
            PublishBackdrop(null);
        }

        // Deep pixel copy, nothing shared
        static Bitmap DeepCopy(Bitmap src)
        {
            var copy = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            var r = new Rectangle(0, 0, src.Width, src.Height);
            var a = src.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var b = copy.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[a.Stride];
                for (int y = 0; y < r.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(a.Scan0.ToInt64() + (long)y * a.Stride), row, 0, a.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, new IntPtr(b.Scan0.ToInt64() + (long)y * b.Stride), Math.Min(a.Stride, b.Stride));
                }
            }
            finally { src.UnlockBits(a); copy.UnlockBits(b); }
            return copy;
        }

        // Cheap blur: shrink, then upscale
        static Bitmap Blur(Bitmap src, int w, int h)
        {
            var out1 = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var tiny = new Bitmap(10, 10, PixelFormat.Format32bppPArgb))
            using (var mid = new Bitmap(40, 40, PixelFormat.Format32bppPArgb))
            {
                using (var gt = Graphics.FromImage(tiny))
                {
                    gt.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    gt.DrawImage(src, new Rectangle(0, 0, 10, 10));
                }
                using (var gm = Graphics.FromImage(mid))
                {
                    gm.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gm.PixelOffsetMode = PixelOffsetMode.Half;
                    gm.DrawImage(tiny, new Rectangle(-4, -4, 48, 48));
                }
                using (var go = Graphics.FromImage(out1))
                {
                    go.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    go.PixelOffsetMode = PixelOffsetMode.Half;
                    float side = Math.Max(w, h) * 1.15f;
                    go.DrawImage(mid, new RectangleF((w - side) / 2, (h - side) / 2, side, side));
                }
            }
            return out1;
        }

        void DrawMusicPage(float w, float h, DateTime now)
        {
            if (!mediaInfo.Present || mediaInfo.Title.Length == 0)
            {
                Empty(w, '', Lang.T("Tidak ada yang diputar"), Lang.T("Spotify, YouTube Music, Apple Music, browser, VLC, dan pemutar lain"));
                return;
            }
            bool album = AlbumMode();
            if (!albumKnown) { albumKnown = true; albumShown = album; }
            if (album != albumShown) { albumShown = album; musicModeUtc = now; }
            float k = Ease.OutCubic((float)((now - musicModeUtc).TotalSeconds / 0.42));
            float alpha = p.Alpha;
            if (k < 1)
            {
                p.Alpha = alpha * (1 - Ease.Clamp01(k * 1.8f));
                bool hold = hitsOn;
                hitsOn = false;
                if (album) DrawLyricsLayout(w, h, now, -k * 8);
                else DrawAlbumLayout(w, h, now, -k * 8);
                hitsOn = hold;
            }
            p.Alpha = alpha * Ease.Clamp01(k * 1.2f);
            if (k < 0.8f) hitsOn = false;
            if (album) DrawAlbumLayout(w, h, now, (1 - k) * 10);
            else DrawLyricsLayout(w, h, now, (1 - k) * 10);
            p.Alpha = alpha;
            double pp = Prof.T();
            DrawProgressRow(w, h, now);
            Prof.Add("progress", pp);
        }

        // Scrub: bar follows pointer, seek on release
        bool scrubbing;
        float scrubFrac;

        void DrawProgressRow(float w, float h, DateTime now)
        {
            float x0 = 24, x1 = w - 24, y = h - 40;
            bool live = mediaInfo.CanSeek && mediaInfo.DurationMs > 0;
            if (live)
                AddHit(new RectangleF(x0, y - 8, x1 - x0, 20), "seek", null, f => { scrubbing = true; scrubFrac = f; },
                       null, delegate { if (scrubbing) media.Seek(scrubFrac * mediaInfo.DurationMs); scrubbing = false; });
            bool active = scrubbing || Hover("seek");
            // Redrawn when a second or pixel turns
            LiveRect(new RectangleF(x0 - 8, y - 26, x1 - x0 + 16, 54), mediaInfo.Playing && !active ? 4 : 0, () => ProgressSig(x1 - x0), t => PaintProgressRow(x0, x1, y, active, t));
        }

        int ProgressSig(float width)
        {
            if (mediaInfo.DurationMs <= 0) return 0;
            double pos = mediaInfo.Position(frameNow);
            return (int)(pos / 1000) * 100003 + (int)(pos / mediaInfo.DurationMs * width * scale * 2);
        }

        void PaintProgressRow(float x0, float x1, float y, bool active, DateTime now)
        {
            // Thicker under the pointer, like Music's scrubber
            float th = active ? 6f : 4f;
            DrawProgress(x0, x1, y - (th - 4) / 2, th, now);
            double pos = scrubbing ? scrubFrac * mediaInfo.DurationMs : mediaInfo.Position(now);
            if (mediaInfo.DurationMs > 0 && (knob == null || active))
                p.Circle(x0 + (x1 - x0) * Ease.Clamp01((float)(pos / mediaInfo.DurationMs)), y + 2, active ? 7f : 4.5f, p.T.Accent);
            p.Text(Fmt.Clock(pos), Ink.Small(11f), scrubbing ? p.T.Text : p.T.Sub, x0, y + 18);
            if (mediaInfo.DurationMs > 0) p.TextRight("-" + Fmt.Clock(mediaInfo.DurationMs - pos), Ink.Small(11f), scrubbing ? p.T.Text : p.T.Sub, x1, y + 18);
        }

        void DrawLyricsLayout(float w, float h, DateTime now, float dy)
        {
            var st = g.Save();
            g.TranslateTransform(0, dy);
            var cover = new RectangleF(24, 64, 120, 120);
            DrawCoverShadow(cover, 16);
            DrawCover(cover, 16);
            DrawTransport(cover.X + cover.Width / 2, 214, false);
            float tx = 166, tw = w - tx - 24;
            FitText(mediaInfo.Title, 18f, 14f, p.T.Text, tx, 78, tw - 40);
            p.Text(mediaInfo.Artist, Ink.Text(13f), p.T.Sub, tx, 101, tw, StringAlignment.Near);
            DrawBars(w - 24, 78, 6, 18, 3f, 2.6f);
            double pt = Prof.T();
            DrawTeleprompter(new RectangleF(tx, 118, tw, h - 118 - 56), now);
            Prof.Add("lyrics", pt);
            g.Restore(st);
        }

        void DrawAlbumLayout(float w, float h, DateTime now, float dy)
        {
            var st = g.Save();
            g.TranslateTransform(0, dy);
            var cover = new RectangleF(28, 66, 136, 136);
            float r = 64, dcx = cover.X + cover.Width / 2 + 10 + vinylOut.Value * 52, dcy = cover.Y + cover.Height / 2;
            double pv = Prof.T();
            DrawVinyl(dcx, dcy, r, vinylAngle, cover);
            Prof.Add("vinyl", pv);
            double pc = Prof.T();
            DrawCoverShadow(cover, 14);
            DrawCover(cover, 14);
            Prof.Add("cover", pc);
            float tx = 252, tw = w - tx - 26;
            double pt = Prof.T();
            FitText(mediaInfo.Title, 22f, 15f, p.T.Text, tx, 92, tw);
            p.Text(mediaInfo.Artist, Ink.Text(13.5f), p.T.Sub, tx, 118, tw, StringAlignment.Near);
            Prof.Add("titles", pt);
            double pb = Prof.T();
            DrawWideBars(tx, 182, tw, 42);
            Prof.Add("bars", pb);
            double pr = Prof.T();
            DrawTransport(tx + tw / 2, 236, true);
            Prof.Add("transport", pr);
            g.Restore(st);
        }

        void FitText(string s, float big, float min, Color c, float x, float cy, float maxW)
        {
            float mw = p.Measure(s, Ink.Display(big));
            float size = mw <= maxW ? big : Math.Max(min, (float)Math.Floor(big * maxW / mw * 2) / 2);
            p.Text(s, Ink.Display(size), c, x, cy, maxW, StringAlignment.Near);
        }

        void DrawCoverShadow(RectangleF r, float radius)
        {
            for (int i = 0; i < 4; i++)
            {
                float grow = 2 + i * 3f;
                p.FillRound(new RectangleF(r.X - grow, r.Y - grow + 5 + i * 1.5f, r.Width + grow * 2, r.Height + grow * 2), radius + grow, Color.FromArgb(28 - i * 6, 0, 0, 0));
            }
        }

        // Transport; album card gets solid play
        void DrawTransport(float cx, float cy, bool bold)
        {
            float gap = bold ? 58 : 40;
            DrawControl(new RectangleF(cx - gap - 17, cy - 17, 34, 34), "prev", delegate { media.Previous(); }, (r, c) => p.Skip(r.X + 17, r.Y + 17, 12, c, false));
            DrawControl(new RectangleF(cx + gap - 17, cy - 17, 34, 34), "next", delegate { media.Next(); }, (r, c) => p.Skip(r.X + 17, r.Y + 17, 12, c, true));
            if (!bold)
            {
                DrawControl(new RectangleF(cx - 19, cy - 19, 38, 38), "play", delegate { media.TogglePlay(); },
                    (r, c) => { if (mediaInfo.Playing) p.Pause(r.X + 19, r.Y + 19, 15, c); else p.Play(r.X + 21, r.Y + 19, 15, c); });
                return;
            }
            float pr = Hover("play") ? 25 : 23;
            p.Circle(cx, cy, pr, p.T.Text);
            if (mediaInfo.Playing) p.Pause(cx, cy, 15, p.T.Bg);
            else p.Play(cx + 2, cy, 16, p.T.Bg);
            AddHit(new RectangleF(cx - 25, cy - 25, 50, 50), "play", delegate { media.TogglePlay(); });
        }

        // Mirrored spectrum bars, one live area
        void DrawWideBars(float x, float baseY, float width, float maxH)
        {
            LiveRect(new RectangleF(x, baseY - maxH, width, maxH * 1.35f + 3), BarsFps(), () => BarsSig(18, maxH), t => PaintWideBars(x, baseY, width, maxH));
        }

        void PaintWideBars(float x, float baseY, float width, float maxH)
        {
            const int count = 18;
            float gap = 5, bw = (width - gap * (count - 1)) / count;
            bool playing = mediaInfo.Playing;
            for (int i = 0; i < count; i++)
            {
                int band = Math.Min(Spectrum.Bands - 1, (int)((i + 0.5f) / count * Spectrum.Bands));
                float v = playing ? bands[band] : 0;
                float bh = 3 + v * (maxH - 3);
                float bx = x + i * (bw + gap);
                p.FillRound(new RectangleF(bx, baseY - bh, bw, bh), bw / 2, Ink.A(p.T.Accent, 0.45f + 0.55f * v));
                p.FillRound(new RectangleF(bx, baseY + 3, bw, bh * 0.35f), bw / 2, Ink.A(p.T.Accent, 0.12f));
            }
        }

        Bitmap discCache;
        string discKey;

        // Static disc; only the label spins
        void DrawVinyl(float cx, float cy, float r, float angle, RectangleF cover)
        {
            EnsureDisc(r);
            var mode = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            DrawDisc(cx, cy, r, angle, mode, cover);
        }

        void EnsureDisc(float r)
        {
            string key = r + "|" + scale;
            if (key != discKey)
            {
                if (discCache != null) discCache.Dispose();
                int px = (int)Math.Ceiling(r * 2 * scale) + 2;
                discCache = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                using (var dg = Graphics.FromImage(discCache))
                {
                    dg.SmoothingMode = SmoothingMode.AntiAlias;
                    dg.ScaleTransform(scale, scale);
                    using (var dp = new Painter { G = dg })
                    {
                    float c = r + 1 / scale;
                    dp.Circle(c, c, r, Color.FromArgb(255, 14, 14, 14));
                    for (float gr = r * 0.44f; gr < r * 0.97f; gr += 2.6f)
                        dg.DrawEllipse(dp.P(Color.FromArgb(gr % 7.8f < 2.6f ? 26 : 13, 255, 255, 255), 0.8f), c - gr, c - gr, gr * 2, gr * 2);
                    using (var sheen = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
                    {
                        dg.FillPie(sheen, c - r, c - r, r * 2, r * 2, -60, 28);
                        dg.FillPie(sheen, c - r, c - r, r * 2, r * 2, 120, 28);
                    }
                    dg.DrawEllipse(dp.P(Color.FromArgb(60, 255, 255, 255), 1f), c - r, c - r, r * 2, r * 2);
                    }
                }
                discKey = key;
            }
        }

        void DrawDisc(float cx, float cy, float r, float angle, InterpolationMode mode, RectangleF cover)
        {
            var dst = new RectangleF(cx - r - 1 / scale, cy - r - 1 / scale, discCache.Width / scale, discCache.Height / scale);
            if (p.Alpha >= 0.99f) g.DrawImage(discCache, dst);
            else
                using (var fade = new ImageAttributes())
                {
                    fade.SetColorMatrix(new ColorMatrix { Matrix33 = p.Alpha });
                    g.DrawImage(discCache, new[] { dst.Location, new PointF(dst.Right, dst.Y), new PointF(dst.X, dst.Bottom) },
                        new RectangleF(0, 0, discCache.Width, discCache.Height), GraphicsUnit.Pixel, fade);
                }
            float lr = r * 0.36f;
            // Label spins alone; cover redrawn on top
            LiveRect(new RectangleF(cx - lr, cy - lr, lr * 2, lr * 2), mediaInfo.Playing ? LiveFps : 0, t =>
            {
                PaintLabel(cx, cy, lr, vinylAngle, mode);
                if (!cover.IsEmpty && inSlot) { DrawCoverShadow(cover, 14); DrawCover(cover, 14); }
            });
            g.InterpolationMode = mode;
        }

        void PaintLabel(float cx, float cy, float lr, float angle, InterpolationMode mode)
        {
            var label = Label(lr);
            if (label != null)
            {
                var st = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(angle);
                g.InterpolationMode = InterpolationMode.Bilinear;
                var attr = new ImageAttributes();
                attr.SetColorMatrix(new ColorMatrix { Matrix33 = p.Alpha });
                g.DrawImage(label, new[] { new PointF(-lr, -lr), new PointF(lr, -lr), new PointF(-lr, lr) },
                    new RectangleF(0, 0, label.Width, label.Height), GraphicsUnit.Pixel, attr);
                attr.Dispose();
                g.Restore(st);
            }
            else p.Circle(cx, cy, lr, p.T.Sub);
            g.InterpolationMode = mode;
            p.Circle(cx, cy, 3f, Color.FromArgb(255, 14, 14, 14));
        }

        readonly System.Collections.Generic.Dictionary<int, Bitmap> labels = new System.Collections.Generic.Dictionary<int, Bitmap>();

        // Circular cover crop for the record label
        Bitmap Label(float lr)
        {
            var src = mediaInfo.Cover;
            if (src == null) return null;
            if (!ReferenceEquals(src, labelSource))
            {
                foreach (var b in labels.Values) b.Dispose();
                labels.Clear();
                labelSource = src;
            }
            int px = (int)Math.Ceiling(lr * 2 * scale);
            Bitmap label;
            if (labels.TryGetValue(px, out label)) return label;
            label = BakeLabel(src, px);
            labels[px] = label;
            return label;
        }

        static Bitmap BakeLabel(Bitmap src, int px)
        {
            var label = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (var lg = Graphics.FromImage(label))
            using (var path = new GraphicsPath())
            using (var tb = new TextureBrush(src, WrapMode.Clamp))
            {
                lg.SmoothingMode = SmoothingMode.AntiAlias;
                path.AddEllipse(0, 0, px, px);
                tb.ScaleTransform((float)px / src.Width, (float)px / src.Height);
                lg.FillPath(tb, path);
            }
            return label;
        }

        // Prebake every cover size off-thread
        static readonly float[][] CoverSizes = { new[] { 24f, 6 }, new[] { 22f, 6 }, new[] { 52f, 13 }, new[] { 40f, 10 }, new[] { 120f, 16 }, new[] { 136f, 14 } };

        void PrewarmArt()
        {
            var src = mediaInfo.Cover;
            if (src == null) return;
            var copy = (Bitmap)src.Clone();
            float s = scale;
            int[] labelPx = { (int)Math.Ceiling(64 * 0.36f * 2 * s) };
            var keys = new int[CoverSizes.Length];
            for (int i = 0; i < keys.Length; i++) keys[i] = CoverKey(CoverSizes[i][0], CoverSizes[i][1]);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var covers = new Bitmap[CoverSizes.Length];
                var labs = new Bitmap[labelPx.Length];
                try
                {
                    for (int i = 0; i < covers.Length; i++) covers[i] = BakeCover(copy, CoverSizes[i][0], CoverSizes[i][1], s);
                    for (int i = 0; i < labs.Length; i++) labs[i] = BakeLabel(copy, labelPx[i]);
                }
                catch (Exception ex) { Log.Write("prewarm: " + ex.Message); }
                finally { copy.Dispose(); }
                Post(() =>
                {
                    bool fresh = ReferenceEquals(mediaInfo.Cover, src) && s == scale;
                    if (fresh && !ReferenceEquals(src, coverSource)) { foreach (var b in coverCache.Values) b.Dispose(); coverCache.Clear(); coverSource = src; }
                    if (fresh && !ReferenceEquals(src, labelSource)) { foreach (var b in labels.Values) b.Dispose(); labels.Clear(); labelSource = src; }
                    for (int i = 0; i < covers.Length; i++)
                    {
                        if (covers[i] == null) continue;
                        if (fresh && !coverCache.ContainsKey(keys[i])) coverCache[keys[i]] = covers[i];
                        else covers[i].Dispose();
                    }
                    for (int i = 0; i < labs.Length; i++)
                    {
                        if (labs[i] == null) continue;
                        if (fresh && !labels.ContainsKey(labelPx[i])) labels[labelPx[i]] = labs[i];
                        else labs[i].Dispose();
                    }
                });
            });
        }
    }
}
