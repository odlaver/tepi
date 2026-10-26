using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Tepi
{
    // Monochrome tokens; Base/Flip swap inverts
    class Tone
    {
        public Color Bg, Text, Sub, Faint, Fill, FillHover, Line;
        // Accent: pill, bars, progress; ink on top of it
        public Color Accent, OnAccent, Progress;

        public static Tone Dark()
        {
            return Defaults(new Tone
            {
                Bg = Color.FromArgb(255, 0, 0, 0),
                Text = Color.FromArgb(255, 244, 244, 244),
                Sub = Color.FromArgb(160, 244, 244, 244),
                Faint = Color.FromArgb(130, 244, 244, 244),
                Fill = Color.FromArgb(30, 255, 255, 255),
                FillHover = Color.FromArgb(52, 255, 255, 255),
                Line = Color.FromArgb(38, 255, 255, 255),
            });
        }

        static Tone Defaults(Tone t)
        {
            t.Accent = t.Text;
            t.OnAccent = t.Bg;
            t.Progress = t.Sub;
            return t;
        }

        public static Tone Paper()
        {
            return Defaults(new Tone
            {
                Bg = Color.FromArgb(255, 245, 245, 245),
                Text = Color.FromArgb(255, 10, 10, 10),
                Sub = Color.FromArgb(170, 10, 10, 10),
                Faint = Color.FromArgb(150, 10, 10, 10),
                Fill = Color.FromArgb(22, 0, 0, 0),
                FillHover = Color.FromArgb(42, 0, 0, 0),
                Line = Color.FromArgb(34, 0, 0, 0),
            });
        }
    }

    static class Ink
    {
        public static Tone Base = Tone.Dark(), Flip = Tone.Paper();
        public static bool Light;

        static Color? accent;

        public static void Apply(bool light)
        {
            Light = light;
            Base = light ? Tone.Paper() : Tone.Dark();
            Flip = light ? Tone.Dark() : Tone.Paper();
            foreach (var t in new[] { Base, Flip }) { t.Accent = t.Text; t.OnAccent = t.Bg; t.Progress = t.Sub; }
            if (accent.HasValue)
            {
                var c = accent.Value;
                Base.Accent = Base.Progress = c;
                // Dark ink on light accents
                Base.OnAccent = c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 150 ? Color.FromArgb(255, 10, 10, 10) : Color.FromArgb(255, 250, 250, 250);
            }
        }

        // "#RRGGBB", or empty for monochrome
        public static void SetAccent(string hex)
        {
            accent = null;
            if (hex != null && hex.Length == 7 && hex[0] == '#')
            {
                int v;
                if (int.TryParse(hex.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out v))
                    accent = Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
            }
            Apply(Light);
        }

        // Segoe Fluent Icons code points
        public const char IcoMic = '', IcoCam = '', IcoWifi = '', IcoBolt = '', IcoBell = '';
        public const char IcoVol0 = '', IcoVol1 = '', IcoVol2 = '', IcoVol3 = '', IcoMute = '';
        public const char IcoCalendar = '', IcoRefresh = '', IcoLock = '', IcoGlobe = '', IcoPlug = '';
        public const char IcoNoNet = '', IcoTerminal = '', IcoOpen = '';

        static string display, text, textSemi, small, mono, icons;
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public static void Init()
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var fc = new InstalledFontCollection())
                foreach (var f in fc.Families) have.Add(f.Name);
            display = Pick(have, "Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");
            text = Pick(have, "Segoe UI Variable Text", "Segoe UI");
            textSemi = Pick(have, "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
            small = Pick(have, "Segoe UI Variable Small", "Segoe UI");
            mono = Pick(have, "Cascadia Mono", "Consolas", "Courier New");
            icons = Pick(have, "Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");
        }

        static string Pick(HashSet<string> have, params string[] names)
        {
            foreach (var n in names) if (have.Contains(n)) return n;
            return System.Drawing.SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        public static Font Display(float px) { return Get(display, px); }
        public static Font Text(float px) { return Get(text, px); }
        public static Font Semi(float px) { return Get(textSemi, px); }
        public static Font Small(float px) { return Get(small, px); }
        public static Font Mono(float px) { return Get(mono, px); }
        public static Font Icons(float px) { return Get(icons, px); }

        static Font Get(string family, float px)
        {
            string key = family + "|" + px.ToString("0.0");
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public static Color A(Color c, float a)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(c.A * a))), c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Ease.Clamp01(t);
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
            if (radius < 0.5f) { p.AddRectangle(r); return p; }
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Covers stay monochrome: luminance-only copy
        public static Bitmap Grayscale(Image src, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            var m = new ColorMatrix(new[]
            {
                new[] { 0.2126f, 0.2126f, 0.2126f, 0, 0 },
                new[] { 0.7152f, 0.7152f, 0.7152f, 0, 0 },
                new[] { 0.0722f, 0.0722f, 0.0722f, 0, 0 },
                new[] { 0f, 0, 0, 1, 0 },
                new[] { 0f, 0, 0, 0, 1 },
            });
            using (var attr = new ImageAttributes())
            using (var g = Graphics.FromImage(bmp))
            {
                attr.SetColorMatrix(m);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                int side = Math.Min(src.Width, src.Height);
                var from = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                g.DrawImage(src, new Rectangle(0, 0, size, size), from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, attr);
            }
            return bmp;
        }
    }

    // Drawing helpers: global alpha, active tone
    class Painter
    {
        public Graphics G;
        public float Alpha = 1f;
        public Tone T = Ink.Base;
        public bool Shadow; // legibility on see-through glass
        readonly SolidBrush brush = new SolidBrush(Color.White);
        readonly Pen pen = new Pen(Color.White, 1f);
        readonly StringFormat line, wrap;
        readonly ImageAttributes imageAttr = new ImageAttributes();
        readonly ColorMatrix matrix = new ColorMatrix();

        public Painter()
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            line = (StringFormat)StringFormat.GenericTypographic.Clone();
            line.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            line.Trimming = StringTrimming.EllipsisCharacter;
            line.LineAlignment = StringAlignment.Center;
            wrap = (StringFormat)StringFormat.GenericTypographic.Clone();
            wrap.FormatFlags |= StringFormatFlags.LineLimit;
            wrap.Trimming = StringTrimming.EllipsisCharacter;
        }

        public Brush B(Color c) { brush.Color = Ink.A(c, Alpha); return brush; }

        public Pen P(Color c, float width)
        {
            pen.Color = Ink.A(c, Alpha);
            pen.Width = width;
            return pen;
        }

        public void FillRound(RectangleF r, float radius, Color c)
        {
            using (var path = Ink.Round(r, radius)) G.FillPath(B(c), path);
        }

        public void StrokeRound(RectangleF r, float radius, Color c, float width)
        {
            using (var path = Ink.Round(r, radius)) G.DrawPath(P(c, width), path);
        }

        public void Circle(float cx, float cy, float r, Color c) { G.FillEllipse(B(c), cx - r, cy - r, r * 2, r * 2); }

        public float Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return G.MeasureString(s, f, PointF.Empty, line).Width;
        }

        // One line, centred on cy, ellipsized
        public void Text(string s, Font f, Color c, float x, float cy, float maxWidth, StringAlignment align)
        {
            if (string.IsNullOrEmpty(s) || maxWidth < 4) return;
            line.Alignment = align;
            float h = f.Size * 2f;
            float left = align == StringAlignment.Near ? x : align == StringAlignment.Far ? x - maxWidth : x - maxWidth / 2;
            if (Shadow && c.R > 128 && c.A > 60)
                G.DrawString(s, f, B(Color.FromArgb((int)(90 * c.A / 255f), 0, 0, 0)), new RectangleF(left, cy - h / 2 + f.Size * 0.06f + 1f, maxWidth, h), line);
            G.DrawString(s, f, B(c), new RectangleF(left, cy - h / 2 + f.Size * 0.06f, maxWidth, h), line);
        }

        public void Text(string s, Font f, Color c, float x, float cy) { Text(s, f, c, x, cy, 2000, StringAlignment.Near); }
        public void TextRight(string s, Font f, Color c, float right, float cy) { Text(s, f, c, right, cy, 2000, StringAlignment.Far); }
        public void TextCenter(string s, Font f, Color c, float cx, float cy) { Text(s, f, c, cx, cy, 2000, StringAlignment.Center); }

        public void TextBox(string s, Font f, Color c, RectangleF r) { TextBox(s, f, c, r, StringAlignment.Near); }

        public void TextBox(string s, Font f, Color c, RectangleF r, StringAlignment align)
        {
            if (string.IsNullOrEmpty(s)) return;
            wrap.Alignment = align;
            G.DrawString(s, f, B(c), r, wrap);
        }

        // Karaoke wipe with a soft edge
        public void Sweep(string s, Font f, Color hi, Color lo, float x, float cy, float maxWidth, StringAlignment align, float progress)
        {
            if (string.IsNullOrEmpty(s)) return;
            float w = Math.Min(maxWidth, Measure(s, f) + 2);
            float left = align == StringAlignment.Near ? x : align == StringAlignment.Far ? x - w : x - w / 2;
            var box = new RectangleF(left - 1, cy - f.Size, w + 2, f.Size * 2);
            float edge = Math.Min(0.12f, 18f / Math.Max(1, box.Width));
            float a = Math.Max(0.001f, Math.Min(0.997f, progress - edge));
            float b = Math.Max(a + 0.001f, Math.Min(0.999f, progress + edge));
            using (var lg = new LinearGradientBrush(box, Ink.A(hi, Alpha), Ink.A(lo, Alpha), 0f))
            {
                var blend = new ColorBlend(4);
                blend.Colors = new[] { Ink.A(hi, Alpha), Ink.A(hi, Alpha), Ink.A(lo, Alpha), Ink.A(lo, Alpha) };
                blend.Positions = new[] { 0f, a, b, 1f };
                lg.InterpolationColors = blend;
                line.Alignment = StringAlignment.Near;
                if (Shadow && hi.R > 128)
                    G.DrawString(s, f, B(Color.FromArgb(90, 0, 0, 0)), new RectangleF(left, cy - f.Size + f.Size * 0.06f + 1f, w + 4, f.Size * 2), line);
                G.DrawString(s, f, lg, new RectangleF(left, cy - f.Size + f.Size * 0.06f, w + 4, f.Size * 2), line);
            }
        }

        public void Icon(char glyph, float px, Color c, float cx, float cy)
        {
            line.Alignment = StringAlignment.Center;
            G.DrawString(glyph.ToString(), Ink.Icons(px), B(c), new RectangleF(cx - px, cy - px + px * 0.04f, px * 2, px * 2), line);
        }

        public void Image(Image img, RectangleF r, float radius)
        {
            matrix.Matrix33 = Alpha;
            imageAttr.SetColorMatrix(matrix);
            using (var path = Ink.Round(r, radius))
            using (var tb = new TextureBrush(img, new Rectangle(0, 0, img.Width, img.Height), imageAttr))
            {
                tb.WrapMode = WrapMode.Clamp;
                tb.TranslateTransform(r.X, r.Y);
                tb.ScaleTransform(r.Width / img.Width, r.Height / img.Height);
                G.FillPath(tb, path);
            }
        }

        // Orbit: track plus comet-tail arc
        public void Orbit(float cx, float cy, float r, float phase, Color c)
        {
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            G.DrawEllipse(P(Ink.A(c, 0.22f), 1.8f), rect);
            float start = phase * 360f - 90;
            for (int i = 0; i < 6; i++)
            {
                float a = 1f - i / 6f;
                G.DrawArc(P(Ink.A(c, a), 2.2f), rect, start - i * 14, 16);
            }
        }

        public void Check(float cx, float cy, float s, Color c, float progress)
        {
            var a = new PointF(cx - s * 0.42f, cy + s * 0.02f);
            var b = new PointF(cx - s * 0.12f, cy + s * 0.32f);
            var d = new PointF(cx + s * 0.46f, cy - s * 0.32f);
            float p1 = Ease.Clamp01(progress / 0.4f), p2 = Ease.Clamp01((progress - 0.4f) / 0.6f);
            var pen = P(c, s * 0.16f);
            G.DrawLine(pen, a, new PointF(a.X + (b.X - a.X) * p1, a.Y + (b.Y - a.Y) * p1));
            if (p2 > 0) G.DrawLine(pen, b, new PointF(b.X + (d.X - b.X) * p2, b.Y + (d.Y - b.Y) * p2));
        }

        public void Cross(float cx, float cy, float s, Color c)
        {
            var pen = P(c, s * 0.15f);
            G.DrawLine(pen, cx - s * 0.36f, cy - s * 0.36f, cx + s * 0.36f, cy + s * 0.36f);
            G.DrawLine(pen, cx + s * 0.36f, cy - s * 0.36f, cx - s * 0.36f, cy + s * 0.36f);
        }

        public void Play(float cx, float cy, float s, Color c)
        {
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[] { new PointF(cx - s * 0.34f, cy - s * 0.5f), new PointF(cx + s * 0.52f, cy), new PointF(cx - s * 0.34f, cy + s * 0.5f) });
                G.FillPath(B(c), path);
                G.DrawPath(P(c, s * 0.12f), path);
            }
        }

        public void Pause(float cx, float cy, float s, Color c)
        {
            float w = s * 0.28f, gap = s * 0.22f;
            FillRound(new RectangleF(cx - gap / 2 - w, cy - s / 2, w, s), w * 0.35f, c);
            FillRound(new RectangleF(cx + gap / 2, cy - s / 2, w, s), w * 0.35f, c);
        }

        public void Skip(float cx, float cy, float s, Color c, bool forward)
        {
            float dir = forward ? 1 : -1, half = s * 0.5f;
            for (int i = 0; i < 2; i++)
            {
                float x0 = cx + dir * (-half + i * half * 0.95f);
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { new PointF(x0, cy - s * 0.36f), new PointF(x0 + dir * half * 1.02f, cy), new PointF(x0, cy + s * 0.36f) });
                    G.FillPath(B(c), path);
                    G.DrawPath(P(c, s * 0.08f), path);
                }
            }
        }

        public void Battery(float x, float cy, float level, Color c, bool charging)
        {
            var body = new RectangleF(x, cy - 7f, 28, 14);
            StrokeRound(body, 4f, Ink.A(c, 0.55f), 1.4f);
            FillRound(new RectangleF(body.Right + 1.6f, cy - 2.6f, 2.2f, 5.2f), 1f, Ink.A(c, 0.55f));
            float w = Math.Max(2f, (body.Width - 5) * Ease.Clamp01(level));
            FillRound(new RectangleF(body.X + 2.5f, body.Y + 2.5f, w, body.Height - 5), 2f, c);
            if (charging) Icon(Ink.IcoBolt, 10f, T.Bg, body.X + body.Width / 2, cy);
        }
    }

    // Odometer digits that roll on change
    class Roller
    {
        string shown = "", prev = "";
        DateTime changedUtc = DateTime.MinValue;
        const double Duration = 0.42;

        public void Set(string s)
        {
            if (s == shown) return;
            prev = shown;
            shown = s;
            changedUtc = DateTime.UtcNow;
        }

        public string Value { get { return shown; } }

        public bool Moving(DateTime now) { return (now - changedUtc).TotalSeconds < Duration; }

        public float Width(Painter p, Font f) { return CharsWidth(p, f, shown); }

        static float CharW(Painter p, Font f, char c)
        {
            return char.IsDigit(c) ? p.Measure("0", f) : p.Measure(c.ToString(), f);
        }

        static float CharsWidth(Painter p, Font f, string s)
        {
            float w = 0;
            foreach (char c in s) w += CharW(p, f, c);
            return w;
        }

        public void Draw(Painter p, Font f, Color c, float x, float cy, StringAlignment align, DateTime now)
        {
            float total = CharsWidth(p, f, shown);
            float left = align == StringAlignment.Near ? x : align == StringAlignment.Far ? x - total : x - total / 2;
            float t = Ease.OutBack((float)((now - changedUtc).TotalSeconds / Duration));
            float lineH = f.Size * 1.05f;
            int offset = prev.Length - shown.Length;
            float cx = left;
            var state = p.G.Save();
            p.G.IntersectClip(new RectangleF(left - 2, cy - lineH * 0.62f, total + 4, lineH * 1.24f));
            float alpha = p.Alpha;
            for (int i = 0; i < shown.Length; i++)
            {
                char now_c = shown[i];
                int j = i + offset;
                char old_c = j >= 0 && j < prev.Length ? prev[j] : ' ';
                float cw = CharW(p, f, now_c);
                string s = now_c.ToString();
                if (old_c != now_c && t < 1f)
                {
                    p.Alpha = alpha * Ease.Clamp01(t * 1.4f);
                    p.Text(s, f, c, cx, cy + (1 - t) * lineH, cw + 6, StringAlignment.Near);
                    p.Alpha = alpha * Ease.Clamp01(1 - t * 1.6f);
                    p.Text(old_c.ToString(), f, c, cx, cy - t * lineH, cw + 6, StringAlignment.Near);
                    p.Alpha = alpha;
                }
                else p.Text(s, f, c, cx, cy, cw + 6, StringAlignment.Near);
                cx += cw;
            }
            p.G.Restore(state);
        }
    }
}
