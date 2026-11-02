using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Tepi
{
    // accent=album: accent taken from the cover
    partial class Island
    {
        public const string AccentAlbum = "album";
        Bitmap albumAccentFor;
        bool albumTinted;

        // On media change; a cover is read once
        void AlbumAccent()
        {
            if (settings.Get("accent", "") != AccentAlbum) return;
            var cover = mediaInfo.Present ? mediaInfo.Cover : null;
            if (ReferenceEquals(cover, albumAccentFor) && (cover != null) == albumTinted) return;
            albumAccentFor = cover;
            albumTinted = cover != null;
            Ink.SetAccent(cover != null ? CoverAccent(cover, Ink.Base.Bg) : "");
            Kick();
        }

        // Most colourful hue bucket, lifted to 3:1 on the island
        static string CoverAccent(Bitmap cover, Color bg)
        {
            const int N = 24;
            var weight = new double[12];
            var sumR = new double[12];
            var sumG = new double[12];
            var sumB = new double[12];
            using (var small = new Bitmap(N, N, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(cover, new Rectangle(0, 0, N, N));
                }
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        var c = small.GetPixel(x, y);
                        float sat = c.GetSaturation(), bri = c.GetBrightness();
                        // Greys and near-black carry no hue
                        double w = sat * (1 - Math.Abs(bri - 0.5) * 1.6);
                        if (w <= 0.05) continue;
                        int b = (int)(c.GetHue() / 30) % 12;
                        weight[b] += w;
                        sumR[b] += c.R * w;
                        sumG[b] += c.G * w;
                        sumB[b] += c.B * w;
                    }
            }
            int best = 0;
            for (int i = 1; i < 12; i++) if (weight[i] > weight[best]) best = i;
            if (weight[best] < 1.5) return "";
            var pick = Color.FromArgb(255, (int)(sumR[best] / weight[best]), (int)(sumG[best] / weight[best]), (int)(sumB[best] / weight[best]));
            var toward = Luminance(bg) < 0.5 ? Color.White : Color.Black;
            for (int i = 0; i < 12 && Contrast(pick, bg) < 3; i++) pick = MixRound(pick, toward, 0.15);
            return HexOf(pick);
        }
    }
}
