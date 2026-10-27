using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Tepi
{
    // Theme packs: %APPDATA%\Tepi\themes\<name>\ with theme.ini, emblem.png, greetings.txt
    partial class Island
    {
        Bitmap emblem;
        string[] greetings = new string[0];
        readonly Random greetRandom = new Random();

        static string ThemesDir { get { return Path.Combine(Settings.Dir, "themes"); } }

        static string[] ThemePacks()
        {
            try
            {
                if (!Directory.Exists(ThemesDir)) return new string[0];
                var names = new List<string>();
                foreach (var d in Directory.GetDirectories(ThemesDir))
                    if (File.Exists(Path.Combine(d, "theme.ini"))) names.Add(Path.GetFileName(d));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names.ToArray();
            }
            catch (Exception ex) { Log.Write("themes: " + ex.Message); return new string[0]; }
        }

        // Read the chosen pack; empty name means plain Tepi
        void LoadPack()
        {
            string name = settings.Get("theme.pack", "");
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (emblem != null) { emblem.Dispose(); emblem = null; }
            greetings = new string[0];
            string dir = name.Length > 0 ? Path.Combine(ThemesDir, Path.GetFileName(name)) : null;
            if (dir != null && Directory.Exists(dir))
            {
                try
                {
                    string ini = Path.Combine(dir, "theme.ini");
                    if (File.Exists(ini))
                        foreach (var raw in File.ReadAllLines(ini))
                        {
                            string line = raw.Trim();
                            int eq = line.IndexOf('=');
                            if (eq > 0 && !line.StartsWith("#") && !line.StartsWith(";")) colors[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                    string pic = Path.Combine(dir, "emblem.png");
                    if (File.Exists(pic)) emblem = RoundEmblem(pic, 64);
                    string lines = Path.Combine(dir, "greetings.txt");
                    if (File.Exists(lines)) greetings = Array.FindAll(File.ReadAllLines(lines), l => l.Trim().Length > 0);
                }
                catch (Exception ex) { Log.Write("theme pack: " + ex.Message); }
            }
            Ink.SetPack(colors);
            Ink.Apply(Ink.Light);
        }

        // Circle crop, once, so frames only blit it
        static Bitmap RoundEmblem(string path, int px)
        {
            using (var src = Image.FromFile(path))
            {
                var bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                using (var clip = new GraphicsPath())
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    clip.AddEllipse(0, 0, px, px);
                    g.SetClip(clip);
                    float side = Math.Min(src.Width, src.Height);
                    g.DrawImage(src, new RectangleF(0, 0, px, px), new RectangleF((src.Width - side) / 2, 0, side, side), GraphicsUnit.Pixel);
                }
                return bmp;
            }
        }

        void ApplyPack()
        {
            LoadPack();
            ResetFrost();
            DropSurfaces();
            Kick();
        }

        string Greeting(string fallback)
        {
            return greetings.Length > 0 ? greetings[greetRandom.Next(greetings.Length)] : fallback;
        }

        void DrawEmblem(float cx, float cy, float r)
        {
            if (emblem == null) return;
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(emblem, new RectangleF(cx - r, cy - r, r * 2, r * 2));
            g.Restore(st);
            g.DrawEllipse(p.P(p.T.Accent, 1.2f), cx - r, cy - r, r * 2, r * 2);
        }
    }

}
