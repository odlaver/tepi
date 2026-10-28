using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Tepi
{
    enum PillKind { Primary, Secondary, Tertiary }

    // Shared components, no per-frame allocations
    partial class Island
    {
        // Press squish, keyed by hit id
        readonly Spring squish = new Spring(0, 900, 60, 0.002f);
        string squishId;
        // Spectrum bars on springs, per band
        readonly Spring[] wave = MakeWave();
        static readonly int[] WaveBands = { 2, 5, 1, 7, 3, 9, 4, 11 };
        // Scrubber growth and cached clock text
        readonly Spring scrubGrow = new Spring(0, 520, 40, 0.002f);
        int clockLeftS = -1, clockRightS = -1;
        string clockLeft = "", clockRight = "";
        // Faded bitmap draws reuse one attribute set
        readonly ImageAttributes fadeAttr = new ImageAttributes();
        readonly ColorMatrix fadeMatrix = new ColorMatrix();

        static Spring[] MakeWave()
        {
            var w = new Spring[Spectrum.Bands];
            for (int i = 0; i < w.Length; i++) w[i] = new Spring(0, 520, 25, 0.003f);
            return w;
        }

        // Per frame, before views draw
        void TrackPress()
        {
            sliderCount = 0;
            string held = pressedId != null && pressedId == hoverId && dragId == null ? pressedId : null;
            if (held != null && held != squishId)
            {
                squishId = held;
                Springs.Tune(squish, 0.1f, 1f);
                squish.Target = 1;
            }
            else if (held == null && squishId != null && squish.Target != 0)
            {
                // Release springs back past rest
                Springs.Tune(squish, 0.34f, 0.52f);
                squish.Target = 0;
            }
            if (squishId != null && held == null && squish.Settled) squishId = null;
        }

        void StepParts(float dt)
        {
            squish.Step(dt);
            scrubGrow.Step(dt);
            bool silent = true;
            for (int i = 0; i < wave.Length; i++)
            {
                wave[i].Target = bands[i];
                wave[i].Step(dt);
                if (bands[i] > 0.004f) silent = false;
            }
            // Nothing left to hear: rest flat
            if (silent && !mediaInfo.Playing) for (int i = 0; i < wave.Length; i++) wave[i].Snap(0);
        }

        bool PartsMoving() { return !squish.Settled || !scrubGrow.Settled || ToneMoving(); }

        // Held control's scale, 1 at rest
        float PressScale(string id, float depth)
        {
            if (id == null || id != squishId || Anim.Reduced) return 1;
            return 1 - (1 - depth) * squish.Value;
        }

        // Scale around r's centre; pair with Unsquish
        float Squish(string id, RectangleF r, float depth)
        {
            float s = PressScale(id, depth);
            if (Math.Abs(s - 1) < 0.0005f) return 1;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            p.G.TranslateTransform(cx, cy);
            p.G.ScaleTransform(s, s);
            p.G.TranslateTransform(-cx, -cy);
            return s;
        }

        void Unsquish(RectangleF r, float s)
        {
            if (s == 1) return;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            p.G.TranslateTransform(cx, cy);
            p.G.ScaleTransform(1 / s, 1 / s);
            p.G.TranslateTransform(-cx, -cy);
        }

        // Module fill for a control's state
        Color FillFor(string id)
        {
            return Pressed(id) ? p.T.FillPressed : Hover(id) ? p.T.FillHover : p.T.Fill;
        }

        void Module(RectangleF r, float radius) { p.Module(r, radius, p.T.Fill); }

        // Clickable module; caller squishes its content
        void Module(RectangleF r, float radius, string id, Action act)
        {
            if (act != null) AddHit(r, id, act);
            p.Module(r, radius, act != null ? FillFor(id) : p.T.Fill);
        }

        // Sliders drawn this frame, for keyboard nudges
        readonly string[] sliderIds = new string[4];
        readonly float[] sliderValues = new float[4];
        readonly Action<float>[] sliderSets = new Action<float>[4];
        int sliderCount;

        // Arrow keys on a focused slider
        bool NudgeFocused(int dir)
        {
            for (int i = 0; i < sliderCount; i++)
            {
                if (sliderIds[i] != focusId) continue;
                float v = (float)Math.Round(sliderValues[i] * 10 + dir) / 10;
                sliderSets[i](Ease.Clamp01(v));
                Kick();
                return true;
            }
            return false;
        }

        // Fat slider, icon inside, drag anywhere
        void BigSlider(RectangleF r, string id, float value, char glyph, string label, string tail, Action<float> set, Action step, string iconId, Action iconAct)
        {
            value = Ease.Clamp01(value);
            AddHit(r, id, step, set, null);
            if (sliderCount < sliderIds.Length)
            {
                sliderIds[sliderCount] = id;
                sliderValues[sliderCount] = value;
                sliderSets[sliderCount++] = set;
            }
            bool hover = dragId == id || Hover(id) || (iconId != null && Hover(iconId));
            float radius = Math.Min(Ink.RadiusModule, r.Height / 2), cy = r.Y + r.Height / 2;
            float at = r.X + r.Width * value, ix = r.X + 25;
            p.Module(r, radius, hover ? p.T.FillHover : p.T.Fill);
            if (value > 0.001f) p.FillRound(r, radius, p.Split(r.X, r.Width, at, p.T.Accent, Color.Transparent));
            // Icon button over the drag area
            if (iconId != null)
            {
                AddHit(new RectangleF(r.X + 3, r.Y + 3, 44, r.Height - 6), iconId, iconAct);
                if (Hover(iconId)) p.Circle(ix, cy, 16, Pressed(iconId) ? Ink.A(p.T.Text, 0.1f) : Ink.A(p.T.Text, 0.14f));
            }
            // Ink flips over the level
            p.Icon(glyph, 17f, p.Split(r.X, r.Width, at, p.T.OnAccent, p.T.Text), ix, cy);
            if (label != null) p.Text(label, Ink.Semi(13.5f), p.Split(r.X, r.Width, at, p.T.OnAccent, p.T.Text), r.X + 46, cy, r.Width - 110, StringAlignment.Near);
            if (tail != null) p.Text(tail, Ink.Semi(12.5f), p.Split(r.X, r.Width, at, Ink.A(p.T.OnAccent, 0.72f), p.T.Sub), r.Right - 16, cy, 60, StringAlignment.Far);
        }

        // Round toggle with caption below
        void CircleToggle(float cx, float cy, float d, char glyph, bool on, bool available, string label, string id, Action act)
        {
            float r = d / 2;
            if (available) AddHit(new RectangleF(cx - Math.Max(r + 8, 40), cy - r - 6, Math.Max(d + 16, 80), d + 32), id, act);
            bool hover = available && Hover(id), down = available && Pressed(id);
            var circle = new RectangleF(cx - r, cy - r, d, d);
            float s = Squish(id, circle, 0.92f);
            Color fill = on ? (down ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.22f) : hover ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f) : p.T.Accent)
                            : down ? p.T.FillPressed : hover ? p.T.FillHover : p.T.Fill;
            // Accent stays solid; off gets module light
            if (on) p.Circle(cx, cy, r, fill);
            else p.Module(circle, r, fill);
            p.Icon(glyph, 17f, on ? p.T.OnAccent : available ? p.T.Text : p.T.Quaternary, cx, cy);
            Unsquish(circle, s);
            p.TextCenter(label, Ink.Caption, available ? p.T.Sub : p.T.Faint, cx, cy + r + 13);
        }

        void Pill(RectangleF r, string id, string label, PillKind kind, Action act) { Pill(r, id, label, kind, act, true); }

        // Pill: accent, module or text only
        void Pill(RectangleF r, string id, string label, PillKind kind, Action act, bool enabled)
        {
            if (enabled) AddHit(r, id, act);
            bool hover = enabled && Hover(id), down = enabled && Pressed(id);
            float s = Squish(id, r, 0.94f), radius = r.Height / 2;
            Color ink;
            if (kind == PillKind.Primary)
            {
                Color fill = !enabled ? p.T.Quaternary : down ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.2f) : hover ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f) : p.T.Accent;
                p.FillRound(r, radius, fill);
                ink = enabled ? p.T.OnAccent : p.T.Faint;
            }
            else if (kind == PillKind.Secondary)
            {
                p.Module(r, radius, down ? p.T.FillPressed : hover ? p.T.FillHover : p.T.Fill);
                ink = enabled ? p.T.Text : p.T.Faint;
            }
            else
            {
                if (down) p.FillRound(r, radius, p.T.FillPressed);
                ink = !enabled ? p.T.Faint : hover ? p.T.Text : p.T.Sub;
            }
            p.TextCenter(label, Ink.Semi(13f), ink, r.X + r.Width / 2, r.Y + r.Height / 2);
            Unsquish(r, s);
        }

        // Circle button; fill on hover, or tinted
        void IconButton(float cx, float cy, float d, string id, char glyph, Action act, bool tinted)
        {
            float s;
            var r = IconButtonFace(cx, cy, d, id, act, tinted, out s);
            p.Icon(glyph, d * 0.38f, tinted ? p.T.Accent : Hover(id) ? p.T.Text : p.T.Sub, cx, cy);
            Unsquish(r, s);
        }

        void IconButton(float cx, float cy, float d, string id, Sym sym, Action act, bool tinted)
        {
            float s;
            var r = IconButtonFace(cx, cy, d, id, act, tinted, out s);
            p.Symbol(sym, cx, cy, d * 0.42f, tinted ? p.T.Accent : Hover(id) ? p.T.Text : p.T.Sub);
            Unsquish(r, s);
        }

        RectangleF IconButtonFace(float cx, float cy, float d, string id, Action act, bool tinted, out float s)
        {
            var r = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            AddHit(r, id, act);
            s = Squish(id, r, 0.92f);
            bool hover = Hover(id), down = Pressed(id);
            if (tinted) p.Circle(cx, cy, d / 2, Ink.A(p.T.Accent, down ? 0.14f : hover ? 0.3f : 0.22f));
            else if (hover) p.Circle(cx, cy, d / 2, down ? p.T.FillPressed : p.T.FillHover);
            return r;
        }

        // Spectrum bars, right-anchored, centred on cy
        void Waveform(float right, float cy, int count, float maxH, float barW, float gap, Color c)
        {
            bool playing = mediaInfo.Playing;
            float minH = Math.Min(barW, 3f);
            for (int i = 0; i < count; i++)
            {
                int band = WaveBands[i % WaveBands.Length] % Spectrum.Bands;
                float v = playing ? Ease.Clamp01(wave[band].Value) : 0;
                float bh = minH + v * (maxH - minH);
                float x = right - (count - i) * (barW + gap) + gap;
                p.FillRound(new RectangleF(x, cy - bh / 2, barW, bh), barW / 2, playing ? Ink.A(c, 0.6f + 0.4f * v) : p.T.Faint);
            }
        }

        // Logo bitmaps baked per size
        sealed class Logo { public Bitmap Src; public int Px; public Bitmap Baked; }
        readonly List<Logo> logos = new List<Logo>();
        readonly Dictionary<string, string> monograms = new Dictionary<string, string>();

        // App logo in a squircle, else monogram
        void AppIcon(RectangleF r, Bitmap logo, string name)
        {
            float radius = r.Width * 0.225f;
            if (logo != null)
            {
                DrawBaked(LogoFor(logo, r.Width, radius), r.X, r.Y);
                return;
            }
            p.Module(r, radius, Ink.A(p.T.Text, p.T.IsDark ? 0.16f : 0.1f));
            p.TextCenter(Monogram(name), Ink.Semi(r.Height * 0.44f), p.T.Text, r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        string Monogram(string name)
        {
            string key = name ?? "";
            string m;
            if (!monograms.TryGetValue(key, out m))
            {
                m = key.Trim().Length > 0 ? key.Trim().Substring(0, 1).ToUpperInvariant() : "?";
                if (monograms.Count > 64) monograms.Clear();
                monograms[key] = m;
            }
            return m;
        }

        Bitmap LogoFor(Bitmap src, float size, float radius)
        {
            int px = (int)Math.Ceiling(size * scale);
            for (int i = 0; i < logos.Count; i++)
                if (ReferenceEquals(logos[i].Src, src) && logos[i].Px == px) return logos[i].Baked;
            if (logos.Count >= 24) { logos[0].Baked.Dispose(); logos.RemoveAt(0); }
            var baked = BakeCover(src, size, radius, scale);
            logos.Add(new Logo { Src = src, Px = px, Baked = baked });
            return baked;
        }

        // Device-pixel bitmap at x, y with fade
        void DrawBaked(Bitmap img, float x, float y)
        {
            var dst = new RectangleF(x, y, img.Width / scale, img.Height / scale);
            var mode = p.G.InterpolationMode;
            p.G.InterpolationMode = InterpolationMode.NearestNeighbor;
            if (p.Alpha >= 0.99f) p.G.DrawImage(img, dst);
            else
            {
                fadeMatrix.Matrix33 = p.Alpha;
                fadeAttr.SetColorMatrix(fadeMatrix);
                p.G.DrawImage(img, Rectangle.Round(dst), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, fadeAttr);
            }
            p.G.InterpolationMode = mode;
        }

        // Scrubber: track grows while engaged
        void Scrubber(RectangleF r, string id, float fraction, double posMs, double durMs, Color fill, Action<float> scrub, Action release)
        {
            AddHit(new RectangleF(r.X, r.Y - 10, r.Width, 24), id, null, scrub, null, release);
            bool engaged = dragId == id || Hover(id);
            scrubGrow.Target = engaged ? 1 : 0;
            float th = 4 + 4 * Ease.Clamp01(scrubGrow.Value), y = r.Y + 2 - th / 2;
            var track = new RectangleF(r.X, y, r.Width, th);
            p.FillRound(track, th / 2, p.T.Quaternary);
            float f = Ease.Clamp01(fraction);
            if (f > 0.001f) p.FillRound(track, th / 2, p.Split(track.X, track.Width, track.X + track.Width * f, fill, Color.Transparent));
            int ls = (int)(posMs / 1000), rs = (int)Math.Max(0, (durMs - posMs) / 1000);
            if (ls != clockLeftS) { clockLeftS = ls; clockLeft = Fmt.Clock(posMs); }
            if (rs != clockRightS) { clockRightS = rs; clockRight = "-" + Fmt.Clock(durMs - posMs); }
            Color ink = engaged ? p.T.Text : p.T.Sub;
            p.Text(clockLeft, Ink.Caption, ink, r.X, r.Y + 17, 80, StringAlignment.Near);
            if (durMs > 0) p.Text(clockRight, Ink.Caption, ink, r.Right, r.Y + 17, 80, StringAlignment.Far);
        }

        // Row: tile, title, subtitle, trailing
        void ListRow(RectangleF r, char icon, string title, string subtitle, string trailing, bool check, string id, Action act, bool separator)
        {
            if (act != null) AddHit(r, id, act);
            bool hover = act != null && Hover(id), down = act != null && Pressed(id);
            if (hover) p.FillRound(r, Ink.RadiusControl, down ? p.T.FillPressed : p.T.FillHover);
            float cy = r.Y + r.Height / 2, tx = r.X + 12;
            if (icon != '\0')
            {
                var tile = new RectangleF(r.X + 8, cy - 14, 28, 28);
                p.Module(tile, 8, p.T.Fill);
                p.Icon(icon, 14f, p.T.Text, tile.X + 14, cy);
                tx = tile.Right + 12;
            }
            float right = r.Right - 12;
            if (check)
            {
                p.Symbol(Sym.Check, right - 7, cy, 14f, p.T.Accent);
                right -= 26;
            }
            if (!string.IsNullOrEmpty(trailing))
            {
                float tw = Math.Min(r.Width * 0.35f, p.Measure(trailing, Ink.Callout));
                p.Text(trailing, Ink.Callout, p.T.Sub, right, cy, tw + 2, StringAlignment.Far);
                right -= tw + 12;
            }
            if (string.IsNullOrEmpty(subtitle)) p.Text(title, Ink.Body, p.T.Text, tx, cy, right - tx, StringAlignment.Near);
            else
            {
                p.Text(title, Ink.Body, p.T.Text, tx, cy - 9, right - tx, StringAlignment.Near);
                p.Text(subtitle, Ink.Callout, p.T.Sub, tx, cy + 10, right - tx, StringAlignment.Near);
            }
            if (separator) p.G.DrawLine(p.P(p.T.Line, 1f), tx, r.Bottom - 0.5f, r.Right - 12, r.Bottom - 0.5f);
        }

        // Title 2 heading, sentence case
        void SectionHeader(string title, float x, float cy, float w, string trailing)
        {
            float tw = string.IsNullOrEmpty(trailing) ? 0 : Math.Min(w * 0.4f, p.Measure(trailing, Ink.Callout)) + 12;
            p.Text(title, Ink.Title2, p.T.Text, x, cy, w - tw, StringAlignment.Near);
            if (tw > 0) p.Text(trailing, Ink.Callout, p.T.Sub, x + w, cy, tw, StringAlignment.Far);
        }

        // Empty state with one action
        void EmptyState(RectangleF area, char icon, string title, string line, string action, string id, Action act)
        {
            bool button = action != null && act != null;
            float block = 48 + 16 + 20 + 22 + (button ? 18 + 32 : 0);
            float top = area.Y + Math.Max(8, (area.Height - block) / 2), cx = area.X + area.Width / 2;
            var circle = new RectangleF(cx - 24, top, 48, 48);
            p.Module(circle, 24, p.T.Fill);
            p.Icon(icon, 20f, p.T.Sub, cx, top + 24);
            p.TextCenter(title, Ink.Headline, p.T.Text, cx, top + 48 + 16 + 10);
            p.Text(line, Ink.Callout, p.T.Sub, cx, top + 48 + 16 + 20 + 11, area.Width - 48, StringAlignment.Center);
            if (!button) return;
            float bw = Math.Max(120, p.Measure(action, Ink.Semi(13f)) + 40);
            Pill(new RectangleF(cx - bw / 2, top + block - 32, bw, 32), id, action, PillKind.Secondary, act);
        }

        // Progress ring from 12 o'clock
        void Ring(float cx, float cy, float r, float fraction, Color c, float width)
        {
            var box = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            p.G.DrawEllipse(p.P(p.T.Quaternary, width), box);
            float sweep = 360f * Ease.Clamp01(fraction);
            if (sweep > 0.5f) p.G.DrawArc(p.P(c, width), box, -90, sweep);
        }

        // Compact HUD: icon, thick bar, rolling percent
        void LevelHud(float w, float h, char glyph, float level, Roller roller, DateTime now)
        {
            float cy = h / 2;
            p.Icon(glyph, 16f, p.T.Text, 25, cy);
            var bar = new RectangleF(48, cy - 5, w - 48 - 64, 10);
            p.FillRound(bar, 5, p.T.Quaternary);
            float f = Ease.Clamp01(level);
            if (f > 0.001f) p.FillRound(bar, 5, p.Split(bar.X, bar.Width, bar.X + Math.Max(10, bar.Width * f), p.T.Accent, Color.Transparent));
            var font = Ink.Semi(13f);
            float pw = p.Measure("%", font);
            p.Text("%", font, p.T.Sub, w - 18, cy, pw + 2, StringAlignment.Far);
            roller.Draw(p, font, p.T.Text, w - 18 - pw, cy, StringAlignment.Far, now);
        }
    }
}
