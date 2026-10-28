using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinTimer = System.Windows.Forms.Timer;

namespace Tepi
{
    // Emblem or mascot frames
    sealed class Sprite : IDisposable
    {
        public readonly Bitmap[] Frames;
        public readonly float Fps;
        public Sprite(Bitmap[] frames, float fps) { Frames = frames; Fps = fps; }
        public int Count { get { return Frames.Length; } }
        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    // Theme packs: themes\<name>, name@N.png strips
    partial class Island
    {
        string[] emblemFiles = new string[0], cornerFiles = new string[0];
        string emblemPath, cornerPath, packDir;
        Sprite emblem, corner;
        Bitmap knob;
        readonly Dictionary<string, List<string>> greetings = new Dictionary<string, List<string>>();
        readonly Random greetRandom = new Random();
        const float CornerSize = 78, KnobHeight = 15;
        const int SpriteBudget = 600 * 1024;

        int animFps = 12, animSeconds = 3, animIdle;
        float knobHeight = KnobHeight, knobAnchor = 0.5f, knobX = 1f;
        bool waterOn;
        DateTime emblemPlayUtc = DateTime.MinValue, rippleUtc = DateTime.MinValue, bubblesUtc = DateTime.MinValue;
        float emblemPlaySeconds;
        readonly WinTimer spriteTimer = new WinTimer();

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

        // Load pack; empty means plain
        void LoadPack()
        {
            string name = settings.Get("theme.pack", "");
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string knobPath = null;
            DropSprite(ref emblem, ref emblemPath);
            DropSprite(ref corner, ref cornerPath);
            DropCornerNext();
            if (knob != null) knob.Dispose();
            knob = null;
            emblemFiles = cornerFiles = new string[0];
            greetings.Clear();
            string dir = name.Length > 0 ? Path.Combine(ThemesDir, Path.GetFileName(name)) : null;
            packDir = dir != null && Directory.Exists(dir) ? dir : null;
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
                    emblemFiles = Files(dir, "emblem*.png");
                    cornerFiles = Files(dir, "corner*.png");
                    string k = Path.Combine(dir, "knob.png");
                    if (File.Exists(k)) knobPath = k;
                    string lines = Path.Combine(dir, "greetings.txt");
                    if (File.Exists(lines)) ReadGreetings(File.ReadAllLines(lines));
                }
                catch (Exception ex) { Log.Write("theme pack: " + ex.Message); }
            }
            animFps = Option(colors, "anim.fps", 12, 1, 30);
            animSeconds = Option(colors, "anim.seconds", 3, 1, 20);
            animIdle = Option(colors, "anim.idle", 0, 0, 3600);
            packEyes = Option(colors, "eyes", 1, 0, 1) == 1;
            knobHeight = Option(colors, "knob.height", (int)KnobHeight, 8, 48);
            knobAnchor = Fraction(colors, "knob.anchor", 0.5f);
            knobX = Fraction(colors, "knob.x", 1f);
            if (knobPath != null)
                try { using (var src = LoadImage(knobPath)) knob = Fit(src, new Rectangle(0, 0, src.Width, src.Height), (int)Math.Ceiling(knobHeight * 4 * scale)); }
                catch (Exception ex) { Log.Write("theme knob: " + ex.Message); }
            string fx;
            waterOn = colors.TryGetValue("effects", out fx) && fx.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0;
            PickDecor();
            PrepareCorner();
            Ink.SetPack(colors);
            Ink.Apply(Ink.Light);
        }

        static int Option(Dictionary<string, string> ini, string key, int fallback, int lo, int hi)
        {
            string v;
            int n;
            return ini.TryGetValue(key, out v) && int.TryParse(v, out n) ? Math.Max(lo, Math.Min(hi, n)) : fallback;
        }

        static float Fraction(Dictionary<string, string> ini, string key, float fallback)
        {
            string v;
            float f;
            return ini.TryGetValue(key, out v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) ? Math.Max(0, Math.Min(1, f)) : fallback;
        }

        static string[] Files(string dir, string pattern)
        {
            var f = Directory.GetFiles(dir, pattern);
            Array.Sort(f, StringComparer.OrdinalIgnoreCase);
            return f;
        }

        // Next mascot, baked off the UI thread
        DateTime cornerUtc;
        Sprite cornerNext;
        string cornerNextPath;
        float cornerNextScale;
        int cornerLoading;
        int packGen;

        // Pick emblem and mascot
        void PickDecor()
        {
            string e = emblemFiles.Length > 0 ? emblemFiles[greetRandom.Next(emblemFiles.Length)] : null;
            if (e != emblemPath)
            {
                DropSprite(ref emblem, ref emblemPath);
                if (e != null) { emblem = LoadSprite(e, true, scale, animFps); emblemPath = e; }
            }
            DropSprite(ref corner, ref cornerPath);
            if (cornerNext != null && cornerNextScale != scale) DropCornerNext();
            if (!expanded || cornerFiles.Length == 0) return;
            if (cornerNext != null)
            {
                corner = cornerNext;
                cornerPath = cornerNextPath;
                cornerNext = null;
                cornerUtc = DateTime.UtcNow;
            }
            // Next one bakes after the panel closes
            else PrepareCorner();
        }

        // Pre-bake next random mascot on worker
        void PrepareCorner()
        {
            if (cornerFiles.Length == 0 || cornerNext != null) return;
            if (System.Threading.Interlocked.CompareExchange(ref cornerLoading, 1, 0) != 0) return;
            string c = cornerFiles[greetRandom.Next(cornerFiles.Length)];
            float s = scale;
            int fps = animFps, gen = packGen;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var sprite = LoadSprite(c, false, s, fps);
                Post(delegate
                {
                    cornerLoading = 0;
                    if (sprite == null) return;
                    if (gen != packGen || s != scale) { sprite.Dispose(); return; }
                    // Panel open without mascot: show now
                    if (expanded && corner == null && current == View.Panel)
                    {
                        corner = sprite;
                        cornerPath = c;
                        cornerUtc = DateTime.UtcNow;
                        Kick();
                        return;
                    }
                    if (cornerNext != null) cornerNext.Dispose();
                    cornerNext = sprite;
                    cornerNextPath = c;
                    cornerNextScale = s;
                });
            });
        }

        // Free shown mascot, then ready the next
        void DropCorner()
        {
            if (corner == null) return;
            DropSprite(ref corner, ref cornerPath);
            PrepareCorner();
        }

        void DropCornerNext()
        {
            packGen++;
            if (cornerNext != null) cornerNext.Dispose();
            cornerNext = null;
            cornerNextPath = null;
        }

        // Decode caps: bounded file and pixel count
        const long MaxPackFile = 8 * 1024 * 1024;
        const int MaxPackSide = 16384, MaxPackPixels = 8 * 1024 * 1024;

        static Image LoadImage(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxPackFile) throw new InvalidDataException("file over 8 MB");
            var head = new byte[24];
            using (var fs = File.OpenRead(path))
                if (fs.Read(head, 0, 24) == 24 && head[0] == 0x89 && head[1] == (byte)'P' && head[12] == (byte)'I' && head[13] == (byte)'H')
                {
                    long w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                    long h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                    if (w <= 0 || h <= 0 || w > MaxPackSide || h > MaxPackSide || w * h > MaxPackPixels) throw new InvalidDataException("image " + w + "x" + h + " too large");
                }
                else throw new InvalidDataException("not a PNG");
            return Image.FromFile(path);
        }

        static void DropSprite(ref Sprite s, ref string path)
        {
            if (s != null) s.Dispose();
            s = null;
            path = null;
        }

        static int FrameCount(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int at = stem.LastIndexOf('@'), n;
            return at > 0 && int.TryParse(stem.Substring(at + 1), out n) && n > 1 ? Math.Min(n, 120) : 1;
        }

        // Frames baked at screen size, worker-safe
        static Sprite LoadSprite(string path, bool round, float scale, int fps)
        {
            Bitmap[] frames = null;
            try
            {
                using (var src = LoadImage(path))
                {
                    int n = Math.Max(1, Math.Min(FrameCount(path), src.Width)), fw = src.Width / n;
                    int px = round ? (int)Math.Ceiling(24 * scale) + 2 : (int)Math.Ceiling(CornerSize * scale);
                    // Thin long strips to budget
                    int step = Math.Max(1, (int)Math.Ceiling((double)n * px * px * 4 / SpriteBudget));
                    frames = new Bitmap[(n + step - 1) / step];
                    for (int i = 0; i < frames.Length; i++)
                    {
                        var cell = new Rectangle(i * step * fw, 0, fw, src.Height);
                        frames[i] = round ? RoundEmblem(src, cell, px) : Fit(src, cell, px);
                    }
                    return new Sprite(frames, (float)fps / step);
                }
            }
            catch (Exception ex)
            {
                // Frames built before the failure
                if (frames != null) foreach (var f in frames) if (f != null) f.Dispose();
                Log.Write("theme sprite " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        // Circle crop of top square
        static Bitmap RoundEmblem(Image src, Rectangle cell, int px)
        {
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var clip = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                clip.AddEllipse(0, 0, px, px);
                g.SetClip(clip);
                float side = Math.Min(cell.Width, cell.Height);
                g.DrawImage(src, new RectangleF(0, 0, px, px), new RectangleF(cell.X + (cell.Width - side) / 2, cell.Y, side, side), GraphicsUnit.Pixel);
            }
            return bmp;
        }

        // Fit longest side, premultiplied
        static Bitmap Fit(Image src, Rectangle cell, int px)
        {
            float s = (float)px / Math.Max(cell.Width, cell.Height);
            int w = Math.Max(1, (int)Math.Round(cell.Width * s)), h = Math.Max(1, (int)Math.Round(cell.Height * s));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), cell, GraphicsUnit.Pixel);
            }
            return bmp;
        }

        void ApplyPack()
        {
            LoadPack();
            ResetFrost();
            Kick();
        }

        // Greetings

        // Sections: [pagi] [siang] [sore] [malam]
        void ReadGreetings(string[] lines)
        {
            string part = "";
            foreach (var raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                if (l.StartsWith("[") && l.EndsWith("]")) { part = DayPartName(l.Substring(1, l.Length - 2)); continue; }
                List<string> list;
                if (!greetings.TryGetValue(part, out list)) greetings[part] = list = new List<string>();
                list.Add(l);
            }
        }

        static string DayPartName(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "pagi": case "morning": return "pagi";
                case "siang": case "noon": case "afternoon": return "siang";
                case "sore": case "evening": return "sore";
                case "malam": case "night": return "malam";
                default: return "";
            }
        }

        static string DayPart()
        {
            int h = DateTime.Now.Hour;
            return h < 4 ? "malam" : h < 11 ? "pagi" : h < 15 ? "siang" : h < 18 ? "sore" : "malam";
        }

        string Greeting(string fallback)
        {
            var pool = new List<string>();
            List<string> l;
            if (greetings.TryGetValue("", out l)) pool.AddRange(l);
            if (greetings.TryGetValue(DayPart(), out l)) pool.AddRange(l);
            return pool.Count > 0 ? pool[greetRandom.Next(pool.Count)] : fallback;
        }

        // Voice: voice\<pagi|siang|sore|malam>\*.wav, else voice\*.wav

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern bool PlaySound(string sound, IntPtr module, uint flags);

        // Async play, Windows owns the buffer
        void PlayGreetingVoice()
        {
            if (packDir == null || !settings.GetBool("theme.voice", true)) return;
            // Silent in fullscreen, presentations, DND
            if (fullscreen || Native.DoNotDisturb()) return;
            try
            {
                string root = Path.Combine(packDir, "voice");
                if (!Directory.Exists(root)) return;
                string part = Path.Combine(root, DayPart());
                var files = Directory.Exists(part) ? Directory.GetFiles(part, "*.wav") : new string[0];
                if (files.Length == 0) files = Directory.GetFiles(root, "*.wav");
                if (files.Length > 0) PlaySound(files[greetRandom.Next(files.Length)], IntPtr.Zero, 0x0001 | 0x0002 | 0x20000);
            }
            catch (Exception ex) { Log.Write("theme voice: " + ex.Message); }
        }

        // Animation

        // Emblem plays on cue only
        void PlayEmblem(float seconds)
        {
            if (emblem == null) return;
            emblemPlayUtc = DateTime.UtcNow;
            emblemPlaySeconds = seconds;
            if (emblem.Count > 1) StartSprites();
            else Kick();
        }

        // Round up to whole loops
        static double PlayLength(Sprite s, float seconds)
        {
            double loop = s.Count / s.Fps;
            return Math.Max(1, Math.Round(seconds / loop)) * loop;
        }

        static Bitmap FrameAt(Sprite s, double t, float seconds)
        {
            if (s.Count == 1 || t < 0 || t >= PlayLength(s, seconds)) return s.Frames[0];
            return s.Frames[(int)(t * s.Fps) % s.Count];
        }

        bool EmblemPlaying(DateTime now)
        {
            return emblem != null && emblem.Count > 1 && (now - emblemPlayUtc).TotalSeconds < PlayLength(emblem, emblemPlaySeconds);
        }

        bool EmblemShown() { return current == View.Idle || (current == View.Info && toast != null && toast.Emblem); }

        bool CornerPlaying(DateTime now)
        {
            return corner != null && corner.Count > 1 && current == View.Panel && (now - panelOpenedUtc).TotalSeconds < PlayLength(corner, animSeconds);
        }

        // Pack-rate frames, never vsync
        void StartSprites()
        {
            spriteTimer.Interval = BubblesMoving(DateTime.UtcNow) ? 50 : Math.Max(16, 1000 / animFps);
            if (!spriteTimer.Enabled) spriteTimer.Start();
        }

        void SpriteTick()
        {
            var now = DateTime.UtcNow;
            bool e = EmblemPlaying(now), c = CornerPlaying(now), b = BubblesMoving(now);
            if (!e && !c && !b) { spriteTimer.Stop(); Kick(); return; }
            if (!b && spriteTimer.Interval == 50) spriteTimer.Interval = Math.Max(16, 1000 / animFps);
            if ((e && EmblemShown()) || c || b) Kick();
        }

        // Idle flourish every anim.idle seconds
        void ThemeTick(DateTime now)
        {
            if (animIdle <= 0 || emblem == null || current != View.Idle || fullscreen || hiddenByUser || dormant) return;
            if ((now - emblemPlayUtc).TotalSeconds < animIdle) return;
            PlayEmblem(1.5f);
            StartBubbles(4);
        }

        bool ThemeMoving(DateTime now)
        {
            return CornerMoving(now) || (now - rippleUtc).TotalSeconds < RippleLife || (emblem != null && emblem.Count == 1 && (now - emblemPlayUtc).TotalSeconds < PulseLife);
        }

        const double RippleLife = 0.8, PulseLife = 0.55;

        void Ripple() { if (waterOn && !Anim.Reduced) rippleUtc = DateTime.UtcNow; }

        void DrawEmblem(float cx, float cy, float r)
        {
            if (emblem == null) return;
            var now = DateTime.UtcNow;
            Bitmap frame = FrameAt(emblem, (now - emblemPlayUtc).TotalSeconds, emblemPlaySeconds);
            float grow = 0;
            if (emblem.Count == 1)
            {
                // Still emblem: small hop
                double t = (now - emblemPlayUtc).TotalSeconds;
                if (t < PulseLife) grow = (float)Math.Sin(t / PulseLife * Math.PI) * r * 0.16f;
            }
            float rr = r + grow;
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(frame, new RectangleF(cx - rr, cy - rr, rr * 2, rr * 2));
            g.Restore(st);
            g.DrawEllipse(p.P(p.T.Accent, 1.2f), cx - rr, cy - rr, rr * 2, rr * 2);
            double rt = (now - rippleUtc).TotalSeconds;
            if (rt < RippleLife)
            {
                float k = (float)(rt / RippleLife);
                float ring = rr + 2 + Ease.OutCubic(k) * 10;
                g.DrawEllipse(p.P(Color.FromArgb((int)(150 * (1 - k)), p.T.Accent), 1.4f * (1 - k) + 0.4f), cx - ring, cy - ring, ring * 2, ring * 2);
            }
        }

        // Mascot on panel's lower right
        void DrawCorner(DateTime now)
        {
            cornerPx = Rectangle.Empty;
            if (corner == null || current != View.Panel) return;
            // Late arrival still pops in
            var shown = cornerUtc > panelOpenedUtc ? cornerUtc : panelOpenedUtc;
            float k = Ease.Clamp01((float)((now - shown).TotalSeconds - 0.12) / 0.35f);
            if (k <= 0) return;
            Bitmap frame = FrameAt(corner, (now - panelOpenedUtc).TotalSeconds, animSeconds);
            float fit = CornerSize / Math.Max(frame.Width, frame.Height);
            float w = frame.Width * fit, h = frame.Height * fit;
            float x = shell.Right - w * 0.4f, y = shell.Bottom - h * 0.42f + (1 - Ease.OutBack(k)) * 14;
            var st = g.Save();
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            var dst = new Rectangle((int)Math.Round(x * scale), (int)Math.Round(y * scale), (int)Math.Round(w * scale), (int)Math.Round(h * scale));
            cornerPx = Rectangle.Inflate(dst, 1, 1);
            if (k < 1)
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = k });
                    g.DrawImage(frame, dst, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, ia);
                }
            else g.DrawImage(frame, dst, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
            g.Restore(st);
        }

        bool CornerMoving(DateTime now)
        {
            var shown = cornerUtc > panelOpenedUtc ? cornerUtc : panelOpenedUtc;
            return corner != null && current == View.Panel && (now - shown).TotalSeconds < 0.6;
        }

        // Water bubbles

        struct Bubble { public float X, R, Rise, Phase, Delay; }
        Bubble[] bubbles = new Bubble[0];
        const float BubbleLife = 1.4f;

        void StartBubbles(int n)
        {
            if (!waterOn || Anim.Reduced) return;
            bubbles = new Bubble[n];
            for (int i = 0; i < n; i++)
                bubbles[i] = new Bubble
                {
                    X = 0.08f + 0.84f * (float)greetRandom.NextDouble(),
                    R = n > 6 ? 2.5f + 3.5f * (float)greetRandom.NextDouble() : 1.3f + 1.6f * (float)greetRandom.NextDouble(),
                    Rise = n > 6 ? 0.22f + 0.3f * (float)greetRandom.NextDouble() : 0.55f + 0.4f * (float)greetRandom.NextDouble(),
                    Phase = (float)(greetRandom.NextDouble() * Math.PI * 2),
                    Delay = 0.45f * (float)greetRandom.NextDouble(),
                };
            bubblesUtc = DateTime.UtcNow;
            StartSprites();
        }

        bool BubblesMoving(DateTime now)
        {
            return bubbles.Length > 0 && (now - bubblesUtc).TotalSeconds < BubbleLife + 0.5;
        }

        // Inside island clip, over content
        void DrawWater(DateTime now)
        {
            if (!BubblesMoving(now)) return;
            float t = (float)(now - bubblesUtc).TotalSeconds;
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var b in bubbles)
            {
                float k = (t - b.Delay) / BubbleLife;
                if (k <= 0 || k >= 1) continue;
                float a = k < 0.2f ? k / 0.2f : k > 0.65f ? (1 - k) / 0.35f : 1;
                float y = shell.Bottom - 3 - Ease.OutCubic(k) * shell.Height * b.Rise;
                float x = shell.X + b.X * shell.Width + (float)Math.Sin(b.Phase + k * 7) * 2.5f;
                g.DrawEllipse(p.P(Color.FromArgb((int)(170 * a), p.T.Text), 1.1f), x - b.R, y - b.R, b.R * 2, b.R * 2);
                g.FillEllipse(p.B(Color.FromArgb((int)(110 * a), p.T.Sub)), x - b.R * 0.45f, y - b.R * 0.55f, b.R * 0.5f, b.R * 0.5f);
            }
            g.Restore(st);
        }

        // Progress knob at the playhead
        bool DrawKnob(float x, float cy, float x0, float x1)
        {
            if (knob == null) return false;
            float h = knobHeight, w = knob.Width * h / knob.Height;
            // Stays within the bar ends
            float left = Math.Max(x0, Math.Min(x - w * knobX, x1 - w));
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(knob, new RectangleF(left, cy - h * knobAnchor, w, h));
            g.Restore(st);
            return true;
        }
    }
}
