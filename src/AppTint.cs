using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Tepi
{
    // Brand colour per notifying app: known list, else the logo's own
    static class AppTint
    {
        // notif.tint: penuh, lembut, mati
        public static string Mode = "penuh";

        public static bool On { get { return Mode != "mati"; } }

        static readonly string[][] Known =
        {
            new[] { "whatsapp", "#25D366" }, new[] { "telegram", "#2AABEE" }, new[] { "discord", "#5865F2" },
            new[] { "teams", "#5B5FC7" }, new[] { "slack", "#E01E5A" }, new[] { "outlook", "#0F78D4" },
            new[] { "gmail", "#EA4335" }, new[] { "instagram", "#E1306C" }, new[] { "messenger", "#0084FF" },
            new[] { "signal", "#3A76F0" }, new[] { "line", "#06C755" }, new[] { "spotify", "#1DB954" },
            new[] { "zoom", "#2D8CFF" }, new[] { "youtube", "#FF0033" }, new[] { "github", "#8957E5" },
            new[] { "kulia", "#3B6FE0" }, new[] { "notion", "#9B9A97" }, new[] { "todoist", "#E44332" },
        };

        static readonly Dictionary<string, Color> cache = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        // Empty when unknown and no colourful logo
        public static Color For(string appId, string appName, Bitmap logo)
        {
            string key = (appId ?? "") + "|" + (appName ?? "");
            Color c;
            if (cache.TryGetValue(key, out c)) return c;
            c = Lookup((appId + " " + appName).ToLowerInvariant());
            if (c.IsEmpty && logo != null) c = FromLogo(logo);
            if (cache.Count > 128) cache.Clear();
            cache[key] = c;
            return c;
        }

        static Color Lookup(string low)
        {
            var words = low.Split(new[] { ' ', '.', '_', '!', '-', '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var k in Known)
            {
                // Short names match whole words: "line" not "online"
                bool hit = k[0].Length <= 4 ? Array.IndexOf(words, k[0]) >= 0 : low.Contains(k[0]);
                if (hit) return ColorTranslator.FromHtml(k[1]);
            }
            return Color.Empty;
        }

        // Most saturated hue in a 16 px copy; greys don't count
        static Color FromLogo(Bitmap logo)
        {
            try
            {
                using (var small = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(logo, 0, 0, 16, 16);
                    }
                    var data = small.LockBits(new Rectangle(0, 0, 16, 16), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var px = new int[256];
                    Marshal.Copy(data.Scan0, px, 0, 256);
                    small.UnlockBits(data);
                    var weight = new float[12];
                    var sum = new float[12, 3];
                    foreach (int v in px)
                    {
                        var c = Color.FromArgb(v);
                        if (c.A < 160) continue;
                        float s = c.GetSaturation(), l = c.GetBrightness();
                        if (s < 0.35f || l < 0.18f || l > 0.85f) continue;
                        int b = (int)(c.GetHue() / 30) % 12;
                        weight[b] += s;
                        sum[b, 0] += c.R * s; sum[b, 1] += c.G * s; sum[b, 2] += c.B * s;
                    }
                    int best = 0;
                    for (int i = 1; i < 12; i++) if (weight[i] > weight[best]) best = i;
                    // Too few coloured pixels: stay neutral
                    if (weight[best] < 6) return Color.Empty;
                    float w = weight[best];
                    return Color.FromArgb(255, (int)(sum[best, 0] / w), (int)(sum[best, 1] / w), (int)(sum[best, 2] / w));
                }
            }
            catch (Exception) { return Color.Empty; }
        }

        // Nudged until it reads against bg at ratio min
        public static Color Readable(Color c, Color bg, double min)
        {
            if (c.IsEmpty) return c;
            bool darkBg = Lum(bg) < 0.4;
            for (int i = 0; i < 12 && Ratio(c, bg) < min; i++)
                c = darkBg ? Mix(c, Color.White, 0.15f) : Mix(c, Color.Black, 0.15f);
            return c;
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static double Lin(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }

        public static double Lum(Color c) { return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B); }

        public static double Ratio(Color a, Color b)
        {
            double x = Lum(a), y = Lum(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
    }
}
