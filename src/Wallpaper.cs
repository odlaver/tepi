using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // The desktop picture as a monitor shows it, kept small; glass is cut from it
    static class Wallpaper
    {
        // Quarter size: cut and stretched back up it reads as a light blur
        const int Shrink = 4;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int size, StringBuilder buf, int ini);

        static string key;
        static Bitmap small;
        static Rectangle smallMon;

        static string lastKey;
        static Rectangle lastMon;
        static int lastAt;

        // Windows said the wallpaper changed: look again at once
        public static void Forget() { lastKey = null; }

        // Changes with the file, its fit and the monitor; asked at most every 3 s
        public static string Key(Rectangle mon)
        {
            if (lastKey != null && mon == lastMon && Environment.TickCount - lastAt < 3000) return lastKey;
            lastKey = Fresh(mon);
            lastMon = mon;
            lastAt = Environment.TickCount;
            return lastKey;
        }

        static string Fresh(Rectangle mon)
        {
            var sb = new StringBuilder(520);
            SystemParametersInfo(0x73, sb.Capacity, sb, 0);
            string path = sb.ToString();
            long stamp = 0;
            try { if (path.Length > 0 && File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch (Exception) { }
            int style;
            bool tile;
            Fit(out style, out tile);
            return path + "|" + stamp + "|" + style + tile + "|" + mon;
        }

        // Screen area behind r, stretched to w x h; lo and hi are its darkest and brightest
        public static Bitmap Cut(Rectangle mon, Rectangle r, int w, int h, out float lo, out float hi)
        {
            lo = 0;
            hi = 1;
            var src = Monitor(mon);
            if (src == null || w <= 0 || h <= 0) return null;
            var at = new RectangleF((r.X - mon.X) / (float)Shrink, (r.Y - mon.Y) / (float)Shrink, r.Width / (float)Shrink, r.Height / (float)Shrink);
            int cw = Math.Max(2, (int)Math.Ceiling(at.Width)), ch = Math.Max(2, (int)Math.Ceiling(at.Height));
            using (var crop = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
            {
                using (var cg = Graphics.FromImage(crop))
                {
                    cg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    cg.PixelOffsetMode = PixelOffsetMode.Half;
                    cg.DrawImage(src, new RectangleF(0, 0, cw, ch), at, GraphicsUnit.Pixel);
                }
                lo = 1;
                hi = 0;
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        var c = crop.GetPixel(x, y);
                        float l = (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f;
                        if (l < lo) lo = l;
                        if (l > hi) hi = l;
                    }
                var outBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var og = Graphics.FromImage(outBmp))
                {
                    og.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    og.PixelOffsetMode = PixelOffsetMode.Half;
                    float fx = (float)w / cw, fy = (float)h / ch;
                    og.DrawImage(crop, new RectangleF(-fx / 2, -fy / 2, w + fx, h + fy), new RectangleF(-0.5f, -0.5f, cw + 1, ch + 1), GraphicsUnit.Pixel);
                }
                return outBmp;
            }
        }

        // Rebuilt only when the picture or monitor changed
        static Bitmap Monitor(Rectangle mon)
        {
            string k = Fresh(mon);
            if (k == key && small != null && smallMon == mon) return small;
            key = k;
            smallMon = mon;
            if (small != null) small.Dispose();
            small = null;
            var sb = new StringBuilder(520);
            SystemParametersInfo(0x73, sb.Capacity, sb, 0);
            string path = sb.ToString();
            int style;
            bool tile;
            Fit(out style, out tile);
            Bitmap img = null;
            try
            {
                if (path.Length > 0 && File.Exists(path))
                    using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                    using (var tmp = new Bitmap(ms))
                        img = new Bitmap(tmp);
                int sw = Math.Max(4, mon.Width / Shrink), sh = Math.Max(4, mon.Height / Shrink);
                small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small))
                using (var vivid = new ImageAttributes())
                {
                    g.Clear(DesktopColor());
                    if (img != null)
                    {
                        // A little more colour, like light through tinted glass
                        const float S = 1.2f, R = 0.2126f * (1 - S), G = 0.7152f * (1 - S), B = 0.0722f * (1 - S);
                        vivid.SetColorMatrix(new ColorMatrix(new[]
                        {
                            new[] { R + S, R, R, 0f, 0f }, new[] { G, G + S, G, 0f, 0f }, new[] { B, B, B + S, 0f, 0f },
                            new[] { 0f, 0f, 0f, 1f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f }
                        }));
                        var src = Source(img.Size, mon, style, tile);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, new Rectangle(0, 0, sw, sh), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, vivid);
                    }
                }
            }
            catch (Exception ex) { Log.Write("wallpaper: " + ex.Message); }
            finally { if (img != null) img.Dispose(); }
            return small;
        }

        static void Fit(out int style, out bool tile)
        {
            style = 10;
            tile = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    if (k != null)
                    {
                        int.TryParse(Convert.ToString(k.GetValue("WallpaperStyle", "10")), out style);
                        tile = Convert.ToString(k.GetValue("TileWallpaper", "0")) == "1";
                    }
            }
            catch (Exception) { }
        }

        // Part of the picture that lands on this monitor, by Windows' fit
        static RectangleF Source(Size img, Rectangle mon, int style, bool tile)
        {
            var area = style == 22 ? SystemInformation.VirtualScreen : mon;
            float aw = area.Width, ah = area.Height, iw = img.Width, ih = img.Height, sx, sy, ox, oy;
            if (style == 2) { sx = aw / iw; sy = ah / ih; ox = oy = 0; }
            else if (style == 0 && tile) { sx = sy = 1; ox = oy = 0; }
            else
            {
                float s = style == 6 ? Math.Min(aw / iw, ah / ih) : style == 0 ? 1 : Math.Max(aw / iw, ah / ih);
                sx = sy = s;
                ox = (aw - iw * s) / 2;
                oy = (ah - ih * s) / 2;
            }
            float rx = mon.Left - area.Left, ry = mon.Top - area.Top;
            return new RectangleF((rx - ox) / sx, (ry - oy) / sy, mon.Width / sx, mon.Height / sy);
        }

        static Color DesktopColor()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
                {
                    var parts = Convert.ToString(k == null ? "" : k.GetValue("Background", "")).Split(' ');
                    int r, g, b;
                    if (parts.Length == 3 && int.TryParse(parts[0], out r) && int.TryParse(parts[1], out g) && int.TryParse(parts[2], out b)) return Color.FromArgb(r, g, b);
                }
            }
            catch (Exception) { }
            return Color.Black;
        }
    }
}
