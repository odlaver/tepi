using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Tepi
{
    // Monochrome tokens; Base/Flip swap inverts
    class Tone
    {
        // Labels: primary, secondary, tertiary; fills and separator
        public Color Bg, Text, Sub, Faint, Fill, FillHover, Line;
        // Disabled ink and tracks; held module
        public Color Quaternary, FillPressed;
        // Module light: top glow, bottom lip
        public Color Highlight, Lip;
        // Accent: pill, bars, progress; ink on top of it
        public Color Accent, OnAccent, Progress;
        // Semantic: diff lines only
        public Color Positive, Negative;

        public bool IsDark { get { return Bg.R + Bg.G + Bg.B < 384; } }

        public static Tone Dark()
        {
            return Defaults(new Tone
            {
                Bg = Color.FromArgb(255, 0, 0, 0),
                Text = Color.FromArgb(255, 245, 245, 247),
                Sub = Color.FromArgb(158, 245, 245, 247),
                Faint = Color.FromArgb(92, 245, 245, 247),
                Fill = Color.FromArgb(18, 255, 255, 255),
                FillHover = Color.FromArgb(28, 255, 255, 255),
                Line = Color.FromArgb(23, 255, 255, 255),
            });
        }

        static Tone Defaults(Tone t)
        {
            t.Accent = t.Text;
            t.OnAccent = t.Bg;
            t.Progress = t.Sub;
            bool dark = t.IsDark;
            t.Positive = dark ? Color.FromArgb(255, 110, 200, 140) : Color.FromArgb(255, 28, 128, 70);
            t.Negative = dark ? Color.FromArgb(255, 240, 120, 120) : Color.FromArgb(255, 190, 50, 50);
            Derive(t);
            return t;
        }

        // Inks that follow text and fill
        public static void Derive(Tone t)
        {
            bool dark = t.IsDark;
            t.Quaternary = Ink.A(t.Text, 0.18f);
            t.FillPressed = Ink.A(t.Fill, dark ? 0.57f : 2.2f);
            t.Highlight = dark ? Color.FromArgb(23, 255, 255, 255) : Color.FromArgb(110, 255, 255, 255);
            t.Lip = dark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255);
        }

        public static Tone Paper()
        {
            return Defaults(new Tone
            {
                Bg = Color.FromArgb(255, 245, 245, 245),
                Text = Color.FromArgb(255, 12, 12, 14),
                Sub = Color.FromArgb(158, 12, 12, 14),
                Faint = Color.FromArgb(92, 12, 12, 14),
                Fill = Color.FromArgb(14, 0, 0, 0),
                FillHover = Color.FromArgb(24, 0, 0, 0),
                Line = Color.FromArgb(24, 0, 0, 0),
            });
        }
    }

    // Vector symbols drawn as cached paths
    enum Sym { Play, Pause, Next, Prev, Stop, Check, Plus, Minus, Xmark, ChevronDown, ChevronUp, ChevronRight }

    static class Ink
    {
        public static Tone Base = Tone.Dark(), Flip = Tone.Paper();
        public static bool Light;

        static Color? accent;
        // Theme pack colors; "card." keys style the light cards
        static Dictionary<string, string> pack = new Dictionary<string, string>();

        public static void SetPack(Dictionary<string, string> colors) { pack = colors ?? new Dictionary<string, string>(); }

        static void Override(Tone t, string prefix)
        {
            Color c;
            if (Hex(prefix + "bg", out c)) t.Bg = c;
            if (Hex(prefix + "text", out c)) t.Text = c;
            if (Hex(prefix + "sub", out c)) t.Sub = c;
            if (Hex(prefix + "faint", out c)) t.Faint = c;
            if (Hex(prefix + "fill", out c)) t.Fill = c;
            if (Hex(prefix + "fillHover", out c)) t.FillHover = c;
            if (Hex(prefix + "line", out c)) t.Line = c;
            if (Hex(prefix + "accent", out c)) t.Accent = t.Progress = c;
            if (Hex(prefix + "onAccent", out c)) t.OnAccent = c;
            if (Hex(prefix + "progress", out c)) t.Progress = c;
            if (Hex(prefix + "positive", out c)) t.Positive = c;
            if (Hex(prefix + "negative", out c)) t.Negative = c;
            Tone.Derive(t);
            if (Hex(prefix + "quaternary", out c)) t.Quaternary = c;
            if (Hex(prefix + "fillPressed", out c)) t.FillPressed = c;
            if (Hex(prefix + "highlight", out c)) t.Highlight = c;
            if (Hex(prefix + "lip", out c)) t.Lip = c;
        }

        // "#RRGGBB" or "#AARRGGBB"
        static bool Hex(string key, out Color c)
        {
            c = Color.Empty;
            string v;
            if (!pack.TryGetValue(key, out v) || v.Length < 7 || v[0] != '#') return false;
            uint n;
            if (!uint.TryParse(v.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out n)) return false;
            c = v.Length == 9 ? Color.FromArgb((int)(n >> 24), (int)((n >> 16) & 255), (int)((n >> 8) & 255), (int)(n & 255)) : Color.FromArgb(255, (int)((n >> 16) & 255), (int)((n >> 8) & 255), (int)(n & 255));
            return true;
        }

        // Bumped on look change; caches compare
        public static int Epoch;

        public static void Apply(bool light)
        {
            Epoch++;
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
            Override(Base, "");
            Override(Flip, "card.");
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
        public const char IcoMic = '\uE720', IcoCam = '\uE714', IcoWifi = '\uE701', IcoBolt = '\uE945', IcoBell = '\uEA8F';
        public const char IcoVol0 = '\uE992', IcoVol1 = '\uE993', IcoVol2 = '\uE994', IcoVol3 = '\uE995', IcoMute = '\uE74F';
        public const char IcoCalendar = '\uE787', IcoRefresh = '\uE72C', IcoLock = '\uE72E', IcoGlobe = '\uE774', IcoPlug = '\uE83E';
        public const char IcoNoNet = '\uF384', IcoTerminal = '\uE756', IcoOpen = '\uE8A7';
        public const char IcoBluetooth = '\uE702', IcoQuiet = '\uE708', IcoHide = '\uED1A', IcoSun = '\uE706', IcoTimer = '\uE916';

        // GDI+ samples gradients at build size
        public const float GradientSpan = 1024;
        // Lifts text so caps centre on cy
        public const float CapLift = 0.05f;

        // Spacing scale, 1x px
        public const float Sp4 = 4, Sp8 = 8, Sp12 = 12, Sp16 = 16, Sp20 = 20, Sp24 = 24, Sp32 = 32;
        // Concentric radii: shell, module, control
        public const float RadiusShell = 32, RadiusModule = 20, RadiusControl = 12;
        // Module grid: shell inset, gap, inner padding
        public const float ModuleInset = 12, ModuleGap = 10, ModulePad = 16;

        const int FDisplay = 0, FText = 1, FSemi = 2, FSmall = 3, FMono = 4, FIcons = 5, FSmallSemi = 6;
        static readonly string[] families = new string[7];

        public static string MenuFamily { get { return families[FText]; } }
        public static string IconFamily { get { return families[FIcons]; } }
        // Integer keys: no string per lookup
        static readonly Dictionary<int, Font> fonts = new Dictionary<int, Font>();

        public static void Init()
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var fc = new InstalledFontCollection())
                foreach (var f in fc.Families) have.Add(f.Name);
            families[FDisplay] = Pick(have, "Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");
            families[FText] = Pick(have, "Segoe UI Variable Text", "Segoe UI");
            families[FSemi] = Pick(have, "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
            families[FSmall] = Pick(have, "Segoe UI Variable Small", "Segoe UI");
            families[FMono] = Pick(have, "Cascadia Mono", "Consolas", "Courier New");
            families[FIcons] = Pick(have, "Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");
            families[FSmallSemi] = Pick(have, "Segoe UI Variable Small Semibol", "Segoe UI Semibold", "Segoe UI");
        }

        static string Pick(HashSet<string> have, params string[] names)
        {
            foreach (var n in names) if (have.Contains(n)) return n;
            return System.Drawing.SystemFonts.MessageBoxFont.FontFamily.Name;
        }

        public static Font Display(float px) { return Get(FDisplay, px); }
        public static Font Text(float px) { return Get(FText, px); }
        public static Font Semi(float px) { return Get(FSemi, px); }
        public static Font Small(float px) { return Get(FSmall, px); }
        public static Font SmallSemi(float px) { return Get(FSmallSemi, px); }
        public static Font Mono(float px) { return Get(FMono, px); }
        public static Font Icons(float px) { return Get(FIcons, px); }

        // Type scale roles
        public static Font Hero { get { return Get(FDisplay, 40f); } }
        public static Font Title1 { get { return Get(FDisplay, 22f); } }
        public static Font Title2 { get { return Get(FSemi, 17f); } }
        public static Font Headline { get { return Get(FSemi, 14.5f); } }
        public static Font Body { get { return Get(FText, 13.5f); } }
        public static Font Callout { get { return Get(FText, 12.5f); } }
        public static Font Caption { get { return Get(FSmall, 11f); } }

        static Font Get(int family, float px)
        {
            int key = family * 100000 + (int)Math.Round(px * 10);
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(families[family] ?? "Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        // One-char strings, made once
        static readonly Dictionary<char, string> chars = new Dictionary<char, string>();

        public static string Str(char c)
        {
            string s;
            if (!chars.TryGetValue(c, out s)) { s = c.ToString(); chars[c] = s; }
            return s;
        }

        static readonly string[] percents = new string[101];

        public static string Percent(int v)
        {
            v = Math.Max(0, Math.Min(100, v));
            return percents[v] ?? (percents[v] = v + "%");
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

        // Top over under, straight alpha
        public static Color Over(Color top, Color under)
        {
            float at = top.A / 255f, au = under.A / 255f;
            float a = at + au * (1 - at);
            if (a < 0.002f) return Color.FromArgb(0, under);
            float k = au * (1 - at);
            return Color.FromArgb((int)(a * 255 + 0.5f), (int)((top.R * at + under.R * k) / a + 0.5f), (int)((top.G * at + under.G * k) / a + 0.5f), (int)((top.B * at + under.B * k) / a + 0.5f));
        }

        // Edge notch: flared shoulders, continuous corners
        public static GraphicsPath Notch(RectangleF r, float radius, float shoulder)
        {
            var path = new GraphicsPath();
            AddNotch(path, r, radius, shoulder);
            return path;
        }

        public static void AddNotch(GraphicsPath path, RectangleF r, float radius, float shoulder)
        {
            float top = r.Y - 2, left = r.Left, right = r.Right, bottom = r.Bottom;
            float s = Math.Max(0.5f, Math.Min(shoulder, r.Height / 3));
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(r.Width / 2, bottom - top - s)));
            var k = CornerOf(radius, Smoothing, Math.Min(r.Width / 2, bottom - top - s));
            // Flare wider than tall
            float fw = s * 1.35f;
            path.AddLine(left - fw, top, right + fw, top);
            path.AddBezier(right + fw, top, right + fw * 0.4f, top, right, top + s * 0.45f, right, top + s);
            AddCorner(path, k, right, bottom, 1);
            AddCorner(path, k, left, bottom, 2);
            path.AddBezier(left, top + s, left, top + s * 0.45f, left - fw * 0.4f, top, left - fw, top);
            path.CloseFigure();
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            AddRound(p, r, radius);
            return p;
        }

        // Corner smoothing; 0.6 matches iOS
        public const float Smoothing = 0.6f;

        // Corner curve length along each edge
        public static float CornerExtent(float radius) { return (1 + Smoothing) * radius; }

        // Squircle into a caller's reusable path
        public static void AddRound(GraphicsPath p, RectangleF r, float radius)
        {
            float budget = Math.Min(r.Width, r.Height) / 2f;
            radius = Math.Min(radius, budget);
            if (radius < 0.5f) { p.AddRectangle(r); return; }
            var k = CornerOf(radius, Smoothing, budget);
            AddCorner(p, k, r.Right, r.Top, 0);
            AddCorner(p, k, r.Right, r.Bottom, 1);
            AddCorner(p, k, r.Left, r.Bottom, 2);
            AddCorner(p, k, r.Left, r.Top, 3);
            p.CloseFigure();
        }

        struct Corner { public float R, P, A, B, C, D, Arc, Alpha; }

        static double Rad(double deg) { return deg * Math.PI / 180; }

        // figma-squircle parameters; smoothing yields to space
        static Corner CornerOf(float radius, float smoothing, float budget)
        {
            float s = smoothing, len = (1 + s) * radius;
            if (len > budget) { s = Math.Max(0, budget / radius - 1); len = Math.Max(radius, budget); }
            float arcMeasure = 90 * (1 - s);
            float arc = (float)(Math.Sin(Rad(arcMeasure / 2)) * radius * Math.Sqrt(2));
            float alpha = (90 - arcMeasure) / 2;
            float p34 = radius * (float)Math.Tan(Rad(alpha / 2));
            float beta = 45 * s;
            float c = p34 * (float)Math.Cos(Rad(beta));
            float d = c * (float)Math.Tan(Rad(beta));
            float b = Math.Max(0, (len - arc - c - d) / 3);
            return new Corner { R = radius, P = len, A = 2 * b, B = b, C = c, D = d, Arc = arc, Alpha = alpha };
        }

        // q clockwise from 0 top-right
        static void AddCorner(GraphicsPath p, Corner k, float cx, float cy, int q)
        {
            // u along incoming edge, v outgoing
            float ux = q == 0 ? 1 : q == 2 ? -1 : 0, uy = q == 1 ? 1 : q == 3 ? -1 : 0;
            float vx = -uy, vy = ux;
            float sx = cx - ux * k.P, sy = cy - uy * k.P;
            float ex = cx - ux * (k.D + k.Arc) + vx * k.D, ey = cy - uy * (k.D + k.Arc) + vy * k.D;
            if (k.A + k.B + k.C > 0.01f)
                p.AddBezier(sx, sy, sx + ux * k.A, sy + uy * k.A, sx + ux * (k.A + k.B), sy + uy * (k.A + k.B), ex, ey);
            float ox = cx - ux * k.R + vx * k.R, oy = cy - uy * k.R + vy * k.R;
            p.AddArc(ox - k.R, oy - k.R, 2 * k.R, 2 * k.R, 270 + 90 * q + k.Alpha, 90 - 2 * k.Alpha);
            float ax = cx - ux * k.D + vx * (k.D + k.Arc), ay = cy - uy * k.D + vy * (k.D + k.Arc);
            if (k.A + k.B + k.C > 0.01f)
                p.AddBezier(ax, ay, ax + ux * k.D + vx * k.C, ay + uy * k.D + vy * k.C, ax + ux * k.D + vx * (k.B + k.C), ay + uy * k.D + vy * (k.B + k.C), cx + vx * k.P, cy + vy * k.P);
        }

        sealed class SymbolShape { public GraphicsPath Path; public float Stroke; }
        static readonly Dictionary<int, SymbolShape> symbols = new Dictionary<int, SymbolShape>();

        // Centred on 0,0; stroke 0 means filled
        public static GraphicsPath SymbolPath(Sym kind, float size, out float stroke)
        {
            int key = (int)kind * 100000 + (int)Math.Round(size * 8);
            SymbolShape s;
            if (!symbols.TryGetValue(key, out s))
            {
                if (symbols.Count > 256)
                {
                    foreach (var old in symbols.Values) old.Path.Dispose();
                    symbols.Clear();
                }
                s = BuildSymbol(kind, (float)Math.Round(size * 8) / 8);
                symbols[key] = s;
            }
            stroke = s.Stroke;
            return s.Path;
        }

        // SF Symbols weight: filled, rounded joins
        static SymbolShape BuildSymbol(Sym kind, float s)
        {
            var path = new GraphicsPath(FillMode.Winding);
            float stroke = 0;
            switch (kind)
            {
                case Sym.Play:
                    // Shifted right: optical centre
                    Rounded(path, new[] { new PointF(-0.30f * s, -0.43f * s), new PointF(0.51f * s, 0), new PointF(-0.30f * s, 0.43f * s) }, 0.1f * s);
                    break;
                case Sym.Pause:
                    AddRound(path, new RectangleF(-0.36f * s, -0.42f * s, 0.26f * s, 0.84f * s), 0.085f * s);
                    AddRound(path, new RectangleF(0.10f * s, -0.42f * s, 0.26f * s, 0.84f * s), 0.085f * s);
                    break;
                case Sym.Next:
                case Sym.Prev:
                    int dir = kind == Sym.Next ? 1 : -1;
                    for (int i = 0; i < 2; i++)
                    {
                        float x0 = dir * (-0.5f * s + i * 0.5f * s), x1 = x0 + dir * 0.53f * s;
                        Rounded(path, new[] { new PointF(x0, -0.31f * s), new PointF(x1, 0), new PointF(x0, 0.31f * s) }, 0.07f * s);
                    }
                    break;
                case Sym.Stop:
                    AddRound(path, new RectangleF(-0.36f * s, -0.36f * s, 0.72f * s, 0.72f * s), 0.17f * s);
                    break;
                case Sym.Check:
                    stroke = 0.15f * s;
                    path.AddLines(new[] { new PointF(-0.36f * s, 0.02f * s), new PointF(-0.11f * s, 0.27f * s), new PointF(0.37f * s, -0.29f * s) });
                    break;
                case Sym.Plus:
                    stroke = 0.13f * s;
                    path.AddLine(-0.36f * s, 0, 0.36f * s, 0);
                    path.StartFigure();
                    path.AddLine(0, -0.36f * s, 0, 0.36f * s);
                    break;
                case Sym.Minus:
                    stroke = 0.13f * s;
                    path.AddLine(-0.36f * s, 0, 0.36f * s, 0);
                    break;
                case Sym.Xmark:
                    stroke = 0.13f * s;
                    path.AddLine(-0.3f * s, -0.3f * s, 0.3f * s, 0.3f * s);
                    path.StartFigure();
                    path.AddLine(0.3f * s, -0.3f * s, -0.3f * s, 0.3f * s);
                    break;
                case Sym.ChevronDown:
                case Sym.ChevronUp:
                    stroke = 0.12f * s;
                    float f = kind == Sym.ChevronDown ? 1 : -1;
                    path.AddLines(new[] { new PointF(-0.3f * s, -0.13f * s * f), new PointF(0, 0.15f * s * f), new PointF(0.3f * s, -0.13f * s * f) });
                    break;
                default:
                    stroke = 0.12f * s;
                    path.AddLines(new[] { new PointF(-0.13f * s, -0.3f * s), new PointF(0.15f * s, 0), new PointF(-0.13f * s, 0.3f * s) });
                    break;
            }
            return new SymbolShape { Path = path, Stroke = stroke };
        }

        // Polygon with circular corners of radius r
        static void Rounded(GraphicsPath path, PointF[] pts, float r)
        {
            int n = pts.Length;
            path.StartFigure();
            for (int i = 0; i < n; i++)
            {
                PointF a = pts[(i + n - 1) % n], b = pts[i], c = pts[(i + 1) % n];
                float ux = a.X - b.X, uy = a.Y - b.Y, la = (float)Math.Sqrt(ux * ux + uy * uy);
                float vx = c.X - b.X, vy = c.Y - b.Y, lc = (float)Math.Sqrt(vx * vx + vy * vy);
                ux /= la; uy /= la; vx /= lc; vy /= lc;
                double theta = Math.Acos(Math.Max(-1, Math.Min(1, ux * vx + uy * vy)));
                double phi = Math.PI - theta;
                float d = Math.Min((float)(r / Math.Tan(theta / 2)), Math.Min(la, lc) * 0.45f);
                // Bezier handle as a share of d
                float k = phi < 0.01 ? 0 : (float)(4.0 / 3.0 * Math.Tan(phi / 4) / Math.Tan(phi / 2));
                var t1 = new PointF(b.X + ux * d, b.Y + uy * d);
                var t2 = new PointF(b.X + vx * d, b.Y + vy * d);
                path.AddBezier(t1, new PointF(t1.X + (b.X - t1.X) * k, t1.Y + (b.Y - t1.Y) * k), new PointF(t2.X + (b.X - t2.X) * k, t2.Y + (b.Y - t2.Y) * k), t2);
            }
            path.CloseFigure();
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
            // Content colour rides along with the copy
            var tone = ContentTone(src);
            if (tone.A > 0) bmp.Tag = tone;
            return bmp;
        }

        // Colour kept by Grayscale, else Empty
        public static Color ToneOf(Image img)
        {
            return img != null && img.Tag is Color ? (Color)img.Tag : Color.Empty;
        }

        // Most saturated mid-luminance hue; Empty when grey
        public static Color ContentTone(Image src)
        {
            const int N = 24, Bins = 18;
            var weight = new float[Bins];
            var sum = new float[Bins * 3];
            try
            {
                using (var tiny = new Bitmap(N, N, PixelFormat.Format32bppArgb))
                {
                    using (var tg = Graphics.FromImage(tiny))
                    {
                        tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        tg.DrawImage(src, new Rectangle(0, 0, N, N));
                    }
                    var data = tiny.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var px = new byte[data.Stride * N];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    tiny.UnlockBits(data);
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            int i = y * data.Stride + x * 4;
                            if (px[i + 3] < 128) continue;
                            float b = px[i] / 255f, g = px[i + 1] / 255f, r = px[i + 2] / 255f;
                            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
                            if (chroma < 0.1f) continue;
                            float l = (max + min) / 2, mid = Math.Max(0, 1 - Math.Abs(l - 0.5f) * 1.7f);
                            float w = chroma * mid;
                            float h = max == r ? (g - b) / chroma : max == g ? 2 + (b - r) / chroma : 4 + (r - g) / chroma;
                            int bin = ((int)Math.Floor((h < 0 ? h + 6 : h) / 6 * Bins)) % Bins;
                            weight[bin] += w;
                            sum[bin * 3] += r * w; sum[bin * 3 + 1] += g * w; sum[bin * 3 + 2] += b * w;
                        }
                }
            }
            catch (Exception ex) { Log.Write("tone: " + ex.Message); return Color.Empty; }
            int best = 0;
            for (int i = 1; i < Bins; i++) if (weight[i] + 0.5f * (weight[(i + 1) % Bins] + weight[(i + Bins - 1) % Bins]) > weight[best] + 0.5f * (weight[(best + 1) % Bins] + weight[(best + Bins - 1) % Bins])) best = i;
            // Grey covers: no content colour
            if (weight[best] < N * N * 0.012f) return Color.Empty;
            float wb = weight[best];
            return Color.FromArgb(255, (int)(sum[best * 3] / wb * 255), (int)(sum[best * 3 + 1] / wb * 255), (int)(sum[best * 3 + 2] / wb * 255));
        }

        // Content colour readable on the glass
        public static Color Readable(Color c, bool light)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
            float l = (max + min) / 2, s = chroma < 0.001f ? 0 : chroma / (1 - Math.Abs(2 * l - 1));
            float h = chroma < 0.001f ? 0 : max == r ? (g - b) / chroma : max == g ? 2 + (b - r) / chroma : 4 + (r - g) / chroma;
            if (h < 0) h += 6;
            // Sky blues stay calm
            float cap = h > 2.9f && h < 3.6f ? 0.5f : 0.78f;
            s = Math.Max(0.42f, Math.Min(cap, s));
            l = light ? Math.Max(0.34f, Math.Min(0.46f, l)) : Math.Max(0.66f, Math.Min(0.78f, l));
            float q = (1 - Math.Abs(2 * l - 1)) * s, x = q * (1 - Math.Abs(h % 2 - 1)), m = l - q / 2;
            float rr = 0, gg = 0, bb = 0;
            if (h < 1) { rr = q; gg = x; } else if (h < 2) { rr = x; gg = q; } else if (h < 3) { gg = q; bb = x; }
            else if (h < 4) { gg = x; bb = q; } else if (h < 5) { rr = x; bb = q; } else { rr = q; bb = x; }
            return Color.FromArgb(255, (int)((rr + m) * 255 + 0.5f), (int)((gg + m) * 255 + 0.5f), (int)((bb + m) * 255 + 0.5f));
        }
    }

    // Spring presets: response seconds, damping ratio
    static class Springs
    {
        public const float SnappyResponse = 0.26f, SnappyDamping = 0.86f;
        public const float SmoothResponse = 0.42f, SmoothDamping = 1f;
        public const float BouncyResponse = 0.42f, BouncyDamping = 0.75f;
        public const float InteractiveResponse = 0.16f, InteractiveDamping = 0.86f;

        public static void Tune(Spring s, float response, float damping)
        {
            float w = (float)(2 * Math.PI / Math.Max(0.05f, response));
            s.K = w * w;
            s.D = 2 * (Anim.Reduced ? Math.Max(1f, damping) : damping) * w;
        }

        public static void Snappy(Spring s) { Tune(s, SnappyResponse, SnappyDamping); }
        public static void Smooth(Spring s) { Tune(s, SmoothResponse, SmoothDamping); }
        public static void Bouncy(Spring s) { Tune(s, BouncyResponse, BouncyDamping); }
        public static void Interactive(Spring s) { Tune(s, InteractiveResponse, InteractiveDamping); }

        // Step response after t seconds
        public static float Progress(float t, float response, float damping)
        {
            if (t <= 0) return 0;
            if (Anim.Reduced) damping = Math.Max(1f, damping);
            double w = 2 * Math.PI / Math.Max(0.05f, response);
            if (damping >= 1) return (float)(1 - (1 + w * t) * Math.Exp(-w * t));
            double wd = w * Math.Sqrt(1 - damping * damping), e = Math.Exp(-damping * w * t);
            return (float)(1 - e * (Math.Cos(wd * t) + damping * w / wd * Math.Sin(wd * t)));
        }

        // Settled within a hair
        public static bool Done(float t, float response) { return t > response * 1.6f; }
    }

    // Drawing helpers: global alpha, active tone
    class Painter : IDisposable
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
        // One path reset per shape
        readonly GraphicsPath shape = new GraphicsPath();
        // Gradients remapped per use, never rebuilt
        readonly LinearGradientBrush glow, lip, split;
        readonly Blend glowBlend = new Blend(3), lipBlend = new Blend(4), splitBlend = new Blend(4);
        readonly Color[] pair = new Color[2];
        // Widths per font, cleared when G changes
        readonly Dictionary<Font, Dictionary<string, float>> widths = new Dictionary<Font, Dictionary<string, float>>();
        Graphics widthsFor;
        int widthCount;

        public void Dispose()
        {
            brush.Dispose();
            pen.Dispose();
            line.Dispose();
            wrap.Dispose();
            imageAttr.Dispose();
            shape.Dispose();
            glow.Dispose();
            lip.Dispose();
            split.Dispose();
        }

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
            var unit = new RectangleF(0, 0, Ink.GradientSpan, Ink.GradientSpan);
            glow = new LinearGradientBrush(unit, Color.White, Color.White, LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipXY };
            lip = new LinearGradientBrush(unit, Color.White, Color.White, LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipXY };
            split = new LinearGradientBrush(unit, Color.White, Color.White, LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipXY };
            glowBlend.Positions = new[] { 0f, 0.45f, 1f };
            glowBlend.Factors = new[] { 0f, 1f, 1f };
            glow.Blend = glowBlend;
            lipBlend.Positions = new[] { 0f, 0.3f, 0.7f, 1f };
            lipBlend.Factors = new[] { 1f, 0f, 0f, 1f };
            lip.Blend = lipBlend;
            splitBlend.Positions = new[] { 0f, 0.5f, 0.51f, 1f };
            splitBlend.Factors = new[] { 0f, 0f, 1f, 1f };
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
            shape.Reset();
            Ink.AddRound(shape, r, radius);
            G.FillPath(B(c), shape);
        }

        public void FillRound(RectangleF r, float radius, Brush b)
        {
            shape.Reset();
            Ink.AddRound(shape, r, radius);
            G.FillPath(b, shape);
        }

        public void StrokeRound(RectangleF r, float radius, Color c, float width)
        {
            shape.Reset();
            Ink.AddRound(shape, r, radius);
            G.DrawPath(P(c, width), shape);
        }

        public void Circle(float cx, float cy, float r, Color c) { G.FillEllipse(B(c), cx - r, cy - r, r * 2, r * 2); }

        static void MapV(LinearGradientBrush b, float y, float h)
        {
            b.ResetTransform();
            b.TranslateTransform(0, y);
            b.ScaleTransform(1, Math.Max(0.01f, h) / Ink.GradientSpan);
            b.RotateTransform(90);
        }

        static void MapH(LinearGradientBrush b, float x, float w)
        {
            b.ResetTransform();
            b.TranslateTransform(x, 0);
            b.ScaleTransform(Math.Max(0.01f, w) / Ink.GradientSpan, 1);
        }

        // Module: fill, top inner light, faint lip
        public void Module(RectangleF r, float radius, Color fill)
        {
            shape.Reset();
            Ink.AddRound(shape, r, radius);
            pair[0] = Ink.A(Ink.Over(T.Highlight, fill), Alpha);
            pair[1] = Ink.A(fill, Alpha);
            glow.LinearColors = pair;
            MapV(glow, r.Y, r.Height);
            G.FillPath(glow, shape);
            if (T.Lip.A == 0) return;
            float e = Ink.CornerExtent(Math.Min(radius, Math.Min(r.Width, r.Height) / 2)), w = r.Width - 2 * e;
            if (w < 8) return;
            pair[0] = Ink.A(T.Lip, Alpha);
            pair[1] = Color.FromArgb(0, T.Lip);
            lip.LinearColors = pair;
            MapH(lip, r.X + e, w);
            G.FillRectangle(lip, r.X + e, r.Bottom - 1.4f, w, 0.9f);
        }

        // Hard colour step at x, no clip
        public Brush Split(float x, float w, float at, Color left, Color right)
        {
            if (at <= x + 0.05f) return B(right);
            if (at >= x + w - 0.05f) return B(left);
            float e = Math.Min(0.02f, 0.9f / Math.Max(1, w));
            float f = Math.Max(0.001f, Math.Min(0.998f - e, (at - x) / w));
            splitBlend.Positions[1] = f;
            splitBlend.Positions[2] = f + e;
            split.Blend = splitBlend;
            pair[0] = Ink.A(left, Alpha);
            pair[1] = Ink.A(right, Alpha);
            split.LinearColors = pair;
            MapH(split, x, w);
            return split;
        }

        public float Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (!ReferenceEquals(widthsFor, G) || widthCount > 4096) { widths.Clear(); widthsFor = G; widthCount = 0; }
            Dictionary<string, float> byText;
            if (!widths.TryGetValue(f, out byText)) widths[f] = byText = new Dictionary<string, float>();
            float w;
            if (byText.TryGetValue(s, out w)) return w;
            w = G.MeasureString(s, f, PointF.Empty, line).Width;
            byText[s] = w;
            widthCount++;
            return w;
        }

        static RectangleF LineBox(Font f, float x, float cy, float maxWidth, StringAlignment align)
        {
            float h = f.Size * 2f;
            float left = align == StringAlignment.Near ? x : align == StringAlignment.Far ? x - maxWidth : x - maxWidth / 2;
            return new RectangleF(left, cy - h / 2 - f.Size * Ink.CapLift, maxWidth, h);
        }

        // One line, centred on cy, ellipsized
        public void Text(string s, Font f, Color c, float x, float cy, float maxWidth, StringAlignment align)
        {
            if (string.IsNullOrEmpty(s) || maxWidth < 4) return;
            line.Alignment = align;
            var box = LineBox(f, x, cy, maxWidth, align);
            if (Shadow && c.R > 128 && c.A > 60)
                G.DrawString(s, f, B(Color.FromArgb((int)(90 * c.A / 255f), 0, 0, 0)), new RectangleF(box.X, box.Y + 1f, box.Width, box.Height), line);
            G.DrawString(s, f, B(c), box, line);
        }

        // Same line with any brush, like Split
        public void Text(string s, Font f, Brush b, float x, float cy, float maxWidth, StringAlignment align)
        {
            if (string.IsNullOrEmpty(s) || maxWidth < 4) return;
            line.Alignment = align;
            G.DrawString(s, f, b, LineBox(f, x, cy, maxWidth, align), line);
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

        // Wrapped text centred both ways in r
        public void TextBoxCenter(string s, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(s)) return;
            wrap.Alignment = StringAlignment.Center;
            wrap.LineAlignment = StringAlignment.Center;
            G.DrawString(s, f, B(c), new RectangleF(r.X, r.Y - f.Size * Ink.CapLift, r.Width, r.Height), wrap);
            wrap.LineAlignment = StringAlignment.Near;
        }

        public void Icon(char glyph, float px, Color c, float cx, float cy)
        {
            line.Alignment = StringAlignment.Center;
            G.DrawString(Ink.Str(glyph), Ink.Icons(px), B(c), new RectangleF(cx - px, cy - px, px * 2, px * 2), line);
        }

        public void Icon(char glyph, float px, Brush b, float cx, float cy)
        {
            line.Alignment = StringAlignment.Center;
            G.DrawString(Ink.Str(glyph), Ink.Icons(px), b, new RectangleF(cx - px, cy - px, px * 2, px * 2), line);
        }

        // Cached vector symbol at cx, cy
        public void Symbol(Sym kind, float cx, float cy, float size, Color c)
        {
            float stroke;
            var path = Ink.SymbolPath(kind, size, out stroke);
            G.TranslateTransform(cx, cy);
            if (stroke > 0) G.DrawPath(P(c, stroke), path);
            else G.FillPath(B(c), path);
            G.TranslateTransform(-cx, -cy);
        }

        public void Image(Image img, RectangleF r, float radius)
        {
            matrix.Matrix33 = Alpha;
            imageAttr.SetColorMatrix(matrix);
            shape.Reset();
            Ink.AddRound(shape, r, radius);
            using (var tb = new TextureBrush(img, new Rectangle(0, 0, img.Width, img.Height), imageAttr))
            {
                tb.WrapMode = WrapMode.Clamp;
                tb.TranslateTransform(r.X, r.Y);
                tb.ScaleTransform(r.Width / img.Width, r.Height / img.Height);
                G.FillPath(tb, shape);
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

        // Legacy sizes mapped onto the symbols
        public void Play(float cx, float cy, float s, Color c) { Symbol(Sym.Play, cx, cy, 1.22f * s, c); }

        public void Pause(float cx, float cy, float s, Color c) { Symbol(Sym.Pause, cx, cy, 1.19f * s, c); }

        public void Skip(float cx, float cy, float s, Color c, bool forward) { Symbol(forward ? Sym.Next : Sym.Prev, cx, cy, 1.1f * s, c); }

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
            return char.IsDigit(c) ? p.Measure("0", f) : p.Measure(Ink.Str(c), f);
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
            float raw = (float)((now - changedUtc).TotalSeconds / Duration);
            float t = Anim.Reduced ? Ease.OutCubic(raw) : Ease.OutBack(raw);
            float lineH = f.Size * 1.05f;
            int offset = prev.Length - shown.Length;
            float cx = left;
            bool rolling = raw < 1;
            var state = rolling ? p.G.Save() : null;
            if (rolling) p.G.IntersectClip(new RectangleF(left - 2, cy - lineH * 0.62f, total + 4, lineH * 1.24f));
            float alpha = p.Alpha;
            for (int i = 0; i < shown.Length; i++)
            {
                char now_c = shown[i];
                int j = i + offset;
                char old_c = j >= 0 && j < prev.Length ? prev[j] : ' ';
                float cw = CharW(p, f, now_c);
                string s = Ink.Str(now_c);
                if (old_c != now_c && t < 1f)
                {
                    p.Alpha = alpha * Ease.Clamp01(t * 1.4f);
                    p.Text(s, f, c, cx, cy + (1 - t) * lineH, cw + 6, StringAlignment.Near);
                    p.Alpha = alpha * Ease.Clamp01(1 - t * 1.6f);
                    p.Text(Ink.Str(old_c), f, c, cx, cy - t * lineH, cw + 6, StringAlignment.Near);
                    p.Alpha = alpha;
                }
                else p.Text(s, f, c, cx, cy, cw + 6, StringAlignment.Near);
                cx += cw;
            }
            if (rolling) p.G.Restore(state);
        }
    }
}
