using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // Own colours: three picks, palette derived like the pack site
    partial class Island
    {
        Dictionary<string, string> packColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly string[] PaletteKeys =
        {
            "bg", "text", "sub", "faint", "fill", "fillHover", "line", "accent", "onAccent", "progress",
            "card.bg", "card.text", "card.sub", "card.faint", "card.fill", "card.fillHover", "card.line", "card.accent", "card.onAccent",
        };

        bool CustomOn { get { return settings.GetBool("look.custom", false); } }

        // Pick, or the look on screen now
        public Color CustomColor(string key)
        {
            Color c;
            if (ParseHex(settings.Get("look." + key, ""), out c)) return c;
            return key == "bg" ? Ink.Base.Bg : key == "text" ? Ink.Base.Text : Ink.Base.Accent;
        }

        public void SetCustom(bool on)
        {
            if (on)
                foreach (var k in new[] { "bg", "text", "accent" })
                    if (settings.Get("look." + k, "").Length == 0) settings.Set("look." + k, HexOf(CustomColor(k)));
            settings.Set("look.custom", on);
            settings.Save();
            RefreshColors();
        }

        public void SetCustomColor(string key, Color c)
        {
            settings.Set("look." + key, HexOf(c));
            settings.Save();
            RefreshColors();
        }

        // Pack colours, then own colours on top
        Dictionary<string, string> MergedColors()
        {
            var colors = new Dictionary<string, string>(packColors, StringComparer.OrdinalIgnoreCase);
            if (CustomOn)
                foreach (var kv in DerivePalette(CustomColor("bg"), CustomColor("text"), CustomColor("accent"))) colors[kv.Key] = kv.Value;
            return colors;
        }

        // Live preview without reloading pack images
        void RefreshColors()
        {
            Ink.SetPack(MergedColors());
            Ink.Apply(Ink.Light);
            ResetFrost();
            Kick();
        }

        // Same maths as derive() in pack.js
        public static Dictionary<string, string> DerivePalette(Color bg, Color text, Color accent)
        {
            var white = Color.White;
            var d = new Dictionary<string, string>();
            d["bg"] = HexOf(bg);
            d["text"] = HexOf(text);
            d["accent"] = d["progress"] = HexOf(accent);
            d["sub"] = HexOf(MixRound(text, bg, 0.24));
            d["faint"] = HexOf(MixRound(text, bg, 0.44));
            var tint = MixRound(accent, white, 0.2);
            d["fill"] = AlphaHex(tint, 0x2E);
            d["fillHover"] = AlphaHex(tint, 0x4A);
            d["line"] = AlphaHex(tint, 0x3A);
            d["onAccent"] = HexOf(Contrast(bg, accent) >= Contrast(white, accent) ? bg : white);
            Color cbg = text, ca = accent;
            for (int i = 0; i < 14 && Contrast(ca, cbg) < 4.5; i++) ca = MixRound(ca, bg, 0.22);
            d["card.bg"] = HexOf(cbg);
            d["card.text"] = HexOf(bg);
            d["card.sub"] = HexOf(MixRound(bg, cbg, 0.3));
            d["card.faint"] = HexOf(MixRound(bg, cbg, 0.48));
            d["card.accent"] = HexOf(ca);
            d["card.fill"] = AlphaHex(ca, 0x1E);
            d["card.fillHover"] = AlphaHex(ca, 0x35);
            d["card.line"] = AlphaHex(ca, 0x30);
            d["card.onAccent"] = HexOf(Contrast(white, ca) >= 4.5 ? white : bg);
            return d;
        }

        static Color MixRound(Color a, Color b, double k)
        {
            Func<int, int, int> m = (x, y) => (int)Math.Round(x + (y - x) * k, MidpointRounding.AwayFromZero);
            return Color.FromArgb(255, m(a.R, b.R), m(a.G, b.G), m(a.B, b.B));
        }

        // WCAG relative luminance
        static double Luminance(Color c)
        {
            Func<int, double> ch = v => { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }

        public static double Contrast(Color a, Color b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        public static string HexOf(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        static string AlphaHex(Color c, int a) { return "#" + a.ToString("X2") + HexOf(c).Substring(1); }

        static bool ParseHex(string v, out Color c)
        {
            c = Color.Empty;
            int n;
            if (v == null || v.Length != 7 || v[0] != '#' || !int.TryParse(v.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return false;
            c = Color.FromArgb(255, (n >> 16) & 255, (n >> 8) & 255, n & 255);
            return true;
        }

        // Full palette as a pack; returns the folder name
        public string SaveCustomPack()
        {
            string name = Lang.T("Warnaku"), dir = Path.Combine(ThemesDir, name);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(ThemesDir, name + " " + i);
            Directory.CreateDirectory(dir);
            var palette = DerivePalette(CustomColor("bg"), CustomColor("text"), CustomColor("accent"));
            var sb = new StringBuilder("# Tepi " + AppVersion + "\r\n");
            foreach (var k in PaletteKeys) sb.Append(k).Append('=').Append(palette[k]).Append("\r\n");
            File.WriteAllText(Path.Combine(dir, "theme.ini"), sb.ToString(), new UTF8Encoding(false));
            return Path.GetFileName(dir);
        }
    }

    // Colour chip: presets, screen pick, or the full dialog
    class KitSwatch : KitButton
    {
        Color color;
        public event Action<Color> Picked;
        // Name and hex per preset
        public string[][] Presets = new string[0][];

        public Color Value
        {
            get { return color; }
            set { color = value; Text = "      " + Island.HexOf(value); AccessibleDescription = Island.HexOf(value); Invalidate(); }
        }

        void Choose(Color c)
        {
            Value = Color.FromArgb(255, c);
            var h = Picked;
            if (h != null) h(color);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var m = new ContextMenuStrip();
            foreach (var p in Presets)
            {
                var c = ColorTranslator.FromHtml(p[1]);
                var dot = new Bitmap(14, 14);
                using (var g = Graphics.FromImage(dot))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 12, 12);
                    using (var pen = new Pen(Color.FromArgb(110, 128, 128, 128))) g.DrawEllipse(pen, 1, 1, 12, 12);
                }
                m.Items.Add(new ToolStripMenuItem(Lang.T(p[0]), dot, delegate { Choose(c); }) { Checked = Island.HexOf(c) == Island.HexOf(color) });
            }
            if (m.Items.Count > 0) m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Lang.T("Ambil dari layar"), null, delegate
            {
                var picker = new PickerOverlay();
                picker.Picked += Choose;
                picker.Show();
                picker.Activate();
            });
            m.Items.Add(Lang.T("Warna lain…"), null, delegate
            {
                using (var dlg = new ColorDialog { Color = color, FullOpen = true, AnyColor = true })
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Choose(dlg.Color);
            });
            MenuLook.Apply(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(this, new Point(0, Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            KitInk.Smooth(g);
            float s = Height / 30f, r = 7 * s;
            float cx = 15 * s, cy = Height / 2f;
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(Color.FromArgb(90, KitInk.Text), 1f)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        }
    }
}
