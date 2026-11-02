using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace Tepi
{
    // QR code, byte mode, level M, versions 1 to 10
    static class Qr
    {
        // Per version: EC per block, group 1 blocks, data each, group 2 blocks, data each
        static readonly int[][] Blocks =
        {
            null,
            new[] { 10, 1, 16, 0, 0 }, new[] { 16, 1, 28, 0, 0 }, new[] { 26, 1, 44, 0, 0 }, new[] { 18, 2, 32, 0, 0 },
            new[] { 24, 2, 43, 0, 0 }, new[] { 16, 4, 27, 0, 0 }, new[] { 18, 4, 31, 0, 0 }, new[] { 22, 2, 38, 2, 39 },
            new[] { 22, 3, 36, 2, 37 }, new[] { 26, 4, 43, 1, 44 },
        };

        static readonly int[][] Align =
        {
            null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
        };

        // Null when the text is too long
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int ver = 1;
            for (; ver <= 10; ver++) if (DataBytes(ver) * 8 >= 4 + (ver < 10 ? 8 : 16) + data.Length * 8) break;
            if (ver > 10) return null;
            var q = new Grid(ver);
            q.Functions();
            q.Place(Interleave(ver, Pack(ver, data)));
            int best = 0, bestScore = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                q.Mask(m);
                q.Format(m);
                int score = q.Penalty();
                if (score < bestScore) { bestScore = score; best = m; }
                q.Mask(m);
            }
            q.Mask(best);
            q.Format(best);
            return q.Dark;
        }

        static int DataBytes(int ver)
        {
            var b = Blocks[ver];
            return b[1] * b[2] + b[3] * b[4];
        }

        // Mode, count, bytes, terminator, pad
        static byte[] Pack(int ver, byte[] data)
        {
            var bits = new List<bool>();
            Action<int, int> put = (v, n) => { for (int i = n - 1; i >= 0; i--) bits.Add(((v >> i) & 1) != 0); };
            put(4, 4);
            put(data.Length, ver < 10 ? 8 : 16);
            foreach (var b in data) put(b, 8);
            int cap = DataBytes(ver) * 8;
            put(0, Math.Min(4, cap - bits.Count));
            while (bits.Count % 8 != 0) bits.Add(false);
            var bytes = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int v = 0;
                for (int j = 0; j < 8; j++) v = (v << 1) | (bits[i + j] ? 1 : 0);
                bytes.Add((byte)v);
            }
            for (bool odd = false; bytes.Count < DataBytes(ver); odd = !odd) bytes.Add(odd ? (byte)0x11 : (byte)0xEC);
            return bytes.ToArray();
        }

        static byte[] Interleave(int ver, byte[] data)
        {
            var b = Blocks[ver];
            int ec = b[0], count = b[1] + b[3];
            var blocks = new List<byte[]>();
            var ecs = new List<byte[]>();
            var div = Divisor(ec);
            for (int i = 0, at = 0; i < count; i++)
            {
                int len = i < b[1] ? b[2] : b[4];
                var blk = new byte[len];
                Array.Copy(data, at, blk, 0, len);
                at += len;
                blocks.Add(blk);
                ecs.Add(Remainder(blk, div));
            }
            var outp = new List<byte>();
            int longest = Math.Max(b[2], b[4]);
            for (int i = 0; i < longest; i++)
                foreach (var blk in blocks) if (i < blk.Length) outp.Add(blk[i]);
            for (int i = 0; i < ec; i++)
                foreach (var e in ecs) outp.Add(e[i]);
            return outp.ToArray();
        }

        // GF(256) over 0x11D
        static int Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z;
        }

        static byte[] Divisor(int degree)
        {
            var r = new byte[degree];
            r[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    r[j] = (byte)Mul(r[j], root);
                    if (j + 1 < degree) r[j] ^= r[j + 1];
                }
                root = Mul(root, 0x02);
            }
            return r;
        }

        static byte[] Remainder(byte[] data, byte[] div)
        {
            var r = new byte[div.Length];
            foreach (var b in data)
            {
                int factor = b ^ r[0];
                Array.Copy(r, 1, r, 0, r.Length - 1);
                r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= (byte)Mul(div[i], factor);
            }
            return r;
        }

        class Grid
        {
            public readonly bool[,] Dark;
            readonly bool[,] fixedAt;
            readonly int ver, size;

            public Grid(int v)
            {
                ver = v;
                size = 17 + 4 * v;
                Dark = new bool[size, size];
                fixedAt = new bool[size, size];
            }

            void Set(int x, int y, bool dark) { Dark[y, x] = dark; fixedAt[y, x] = true; }

            public void Functions()
            {
                for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
                Finder(3, 3);
                Finder(size - 4, 3);
                Finder(3, size - 4);
                var a = Align[ver];
                for (int i = 0; i < a.Length; i++)
                    for (int j = 0; j < a.Length; j++)
                    {
                        // Corners hold the finders
                        if ((i == 0 && j == 0) || (i == 0 && j == a.Length - 1) || (i == a.Length - 1 && j == 0)) continue;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                                Set(a[i] + dx, a[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                Format(0);
                if (ver >= 7)
                {
                    int rem = ver;
                    for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                    int bits = (ver << 12) | rem;
                    for (int i = 0; i < 18; i++)
                    {
                        bool bit = ((bits >> i) & 1) != 0;
                        int a1 = size - 11 + i % 3, b1 = i / 3;
                        Set(a1, b1, bit);
                        Set(b1, a1, bit);
                    }
                }
            }

            void Finder(int cx, int cy)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = cx + dx, y = cy + dy, d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, d != 2 && d != 4);
                    }
            }

            // Level M carries format bits 00
            public void Format(int mask)
            {
                int data = mask, rem = data;
                for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
                int bits = ((data << 10) | rem) ^ 0x5412;
                Func<int, bool> bit = i => ((bits >> i) & 1) != 0;
                for (int i = 0; i <= 5; i++) Set(8, i, bit(i));
                Set(8, 7, bit(6));
                Set(8, 8, bit(7));
                Set(7, 8, bit(8));
                for (int i = 9; i < 15; i++) Set(14 - i, 8, bit(i));
                for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, bit(i));
                for (int i = 8; i < 15; i++) Set(8, size - 15 + i, bit(i));
                Set(8, size - 8, true);
            }

            // Zigzag up and down in column pairs
            public void Place(byte[] data)
            {
                int i = 0;
                for (int right = size - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right = 5;
                    for (int vert = 0; vert < size; vert++)
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j;
                            bool up = ((right + 1) & 2) == 0;
                            int y = up ? size - 1 - vert : vert;
                            if (fixedAt[y, x] || i >= data.Length * 8) continue;
                            Dark[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                }
            }

            public void Mask(int m)
            {
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        if (fixedAt[y, x]) continue;
                        bool flip;
                        switch (m)
                        {
                            case 0: flip = (x + y) % 2 == 0; break;
                            case 1: flip = y % 2 == 0; break;
                            case 2: flip = x % 3 == 0; break;
                            case 3: flip = (x + y) % 3 == 0; break;
                            case 4: flip = (x / 3 + y / 2) % 2 == 0; break;
                            case 5: flip = x * y % 2 + x * y % 3 == 0; break;
                            case 6: flip = (x * y % 2 + x * y % 3) % 2 == 0; break;
                            default: flip = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                        }
                        if (flip) Dark[y, x] = !Dark[y, x];
                    }
            }

            // Runs, 2x2 blocks, finder look-alikes, balance
            public int Penalty()
            {
                int score = 0, dark = 0;
                for (int pass = 0; pass < 2; pass++)
                    for (int a = 0; a < size; a++)
                    {
                        int run = 0;
                        bool last = false;
                        for (int b = 0; b < size; b++)
                        {
                            bool c = pass == 0 ? Dark[a, b] : Dark[b, a];
                            if (b > 0 && c == last) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                            else run = 1;
                            last = c;
                            if (b + 10 < size)
                            {
                                bool match1 = true, match2 = true;
                                for (int k = 0; k < 11; k++)
                                {
                                    bool v = pass == 0 ? Dark[a, b + k] : Dark[b + k, a];
                                    if (v != Look1[k]) match1 = false;
                                    if (v != Look1[10 - k]) match2 = false;
                                }
                                if (match1) score += 40;
                                if (match2) score += 40;
                            }
                        }
                    }
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        if (Dark[y, x]) dark++;
                        if (x + 1 < size && y + 1 < size && Dark[y, x] == Dark[y, x + 1] && Dark[y, x] == Dark[y + 1, x] && Dark[y, x] == Dark[y + 1, x + 1]) score += 3;
                    }
                int total = size * size;
                score += Math.Abs(dark * 20 - total * 10) / total * 10;
                return score;
            }

            static readonly bool[] Look1 = { true, false, true, true, true, false, true, false, false, false, false };
        }

        // Dark on white with the quiet zone
        public static Bitmap Render(bool[,] dark, int px)
        {
            int n = dark.GetLength(0), side = (n + 8) * px;
            var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (dark[y, x]) g.FillRectangle(Brushes.Black, (x + 4) * px, (y + 4) * px, px, px);
            }
            return bmp;
        }
    }

    // Pairing QR; hides its window from capture while shown
    class KitQr : System.Windows.Forms.Control
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        const uint WdaNone = 0, WdaExclude = 0x11;

        Bitmap image;
        IntPtr hidden;
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 1000 };
        // True while the code is still the one drawn
        public Func<bool> StillValid;
        public event Action Expired;

        public KitQr(string text)
        {
            SetStyle(System.Windows.Forms.ControlStyles.UserPaint | System.Windows.Forms.ControlStyles.AllPaintingInWmPaint | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer, true);
            var m = Qr.Encode(text);
            if (m != null) image = Qr.Render(m, 1);
            AccessibleRole = System.Windows.Forms.AccessibleRole.Graphic;
            watch.Tick += delegate
            {
                if (StillValid == null || StillValid()) return;
                watch.Stop();
                var h = Expired;
                if (h != null) h();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var f = FindForm();
            if (f != null && SetWindowDisplayAffinity(f.Handle, WdaExclude)) hidden = f.Handle;
            watch.Start();
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            if (image == null) return;
            // Whole pixels per module keep edges sharp
            int scale = Math.Max(1, Math.Min(Width, Height) / image.Width);
            int side = image.Width * scale, x = (Width - side) / 2, y = (Height - side) / 2;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(image, new Rectangle(x, y, side, side));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                watch.Dispose();
                if (hidden != IntPtr.Zero) SetWindowDisplayAffinity(hidden, WdaNone);
                if (image != null) image.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
