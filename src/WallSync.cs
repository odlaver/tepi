using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Tepi
{
    // wall.sync: glass veil and accent follow the desktop wallpaper; blur stays
    partial class Island
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
        static extern bool SpiGetString(uint action, uint size, StringBuilder buf, uint ini);

        readonly System.Windows.Forms.Timer wallDebounce = new System.Windows.Forms.Timer { Interval = 800 };
        bool wallHooked;
        int wallBusy;
        // Dominant wallpaper hue, as read; null for greys
        Color? wallPick;
        // Accent we set ourselves, so it can be given back
        string wallAccent;

        bool WallSyncOn { get { return settings.GetBool("wall.sync", false) && !CustomOn; } }

        void StartWallSync()
        {
            if (wallHooked) return;
            wallHooked = true;
            wallDebounce.Tick += delegate { wallDebounce.Stop(); WallRead(); };
            SystemEvents.UserPreferenceChanged += OnWallPrefs;
            if (settings.GetBool("wall.sync", false)) WallRead();
        }

        void StopWallSync()
        {
            if (!wallHooked) return;
            wallHooked = false;
            SystemEvents.UserPreferenceChanged -= OnWallPrefs;
            wallDebounce.Stop();
        }

        // Wallpaper or slideshow moved on: look again once it settles
        void OnWallPrefs(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.Desktop && e.Category != UserPreferenceCategory.Color) return;
            Post(delegate { if (settings.GetBool("wall.sync", false)) { wallDebounce.Stop(); wallDebounce.Start(); } });
        }

        // Switch flipped in settings
        void WallToggle(bool on)
        {
            if (on) WallRead(); else WallApply();
        }

        // Decode off the UI thread; a big wallpaper takes a moment
        void WallRead()
        {
            if (!settings.GetBool("wall.sync", false)) return;
            if (Interlocked.Exchange(ref wallBusy, 1) != 0) { wallDebounce.Stop(); wallDebounce.Start(); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Color? pick = null;
                try { pick = WallPickNow(); }
                catch (Exception ex) { Log.Write("wall sync: " + ex.Message); }
                Post(delegate
                {
                    Interlocked.Exchange(ref wallBusy, 0);
                    wallPick = pick;
                    WallApply();
                });
            });
        }

        static Color? WallPickNow()
        {
            var sb = new StringBuilder(520);
            string path = SpiGetString(0x73, 520, sb, 0) ? sb.ToString() : "";
            if (path.Length == 0 || !File.Exists(path))
            {
                // Slideshow and some themes keep the current picture here
                string cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                path = File.Exists(cached) ? cached : "";
            }
            if (path.Length > 0)
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var img = Image.FromStream(fs, false, false))
                using (var mid = new System.Drawing.Bitmap(96, 96))
                {
                    // Two steps: a 4K picture sampled straight to 24 px would alias
                    using (var g = Graphics.FromImage(mid))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, new Rectangle(0, 0, 96, 96));
                    }
                    Color c;
                    return HuePick(mid, out c) ? (Color?)c : null;
                }
            }
            // Plain colour desktop: "R G B"
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
            {
                var v = k == null ? null : k.GetValue("Background") as string;
                var part = v == null ? new string[0] : v.Split(' ');
                int r, g2, b;
                if (part.Length == 3 && int.TryParse(part[0], out r) && int.TryParse(part[1], out g2) && int.TryParse(part[2], out b))
                {
                    var solid = Color.FromArgb(255, Math.Max(0, Math.Min(255, r)), Math.Max(0, Math.Min(255, g2)), Math.Max(0, Math.Min(255, b)));
                    if (solid.GetSaturation() > 0.12f) return solid;
                }
            }
            return null;
        }

        // Applies what was read; safe to call on any look change
        void WallApply()
        {
            bool live = WallSyncOn && wallPick.HasValue;
            Color? veil = live ? (Color?)WallVeil(wallPick.Value, Ink.Light) : null;
            bool veilChanged = Ink.VeilTint != veil;
            Ink.VeilTint = veil;

            // Accent follows the wallpaper only while none is chosen
            string own = settings.Get("accent", "");
            string want = live && own.Length == 0 ? WallAccentHex(wallPick.Value) : null;
            bool accentChanged = false;
            if (want != null)
            {
                if (want != wallAccent) { wallAccent = want; Ink.SetAccent(want); accentChanged = true; }
            }
            else if (wallAccent != null)
            {
                accentChanged = true;
                wallAccent = null;
                Ink.SetAccent(own);
                albumAccentFor = null;
                albumTinted = false;
                AlbumAccent();
            }

            if (!veilChanged && !accentChanged) return;
            Ink.Epoch++;
            ResetFrost();
            if (dockWin != null) dockWin.Changed();
            if (prefs != null && !prefs.IsDisposed) prefs.Relayout();
            Kick();
        }

        // Deep, low-lightness tint in the wallpaper's hue; contrast with the ink stays as for plain black or paper
        static Color WallVeil(Color pick, bool light)
        {
            double s = Math.Min(pick.GetSaturation(), light ? 0.55 : 0.62);
            return FromHsl(pick.GetHue(), s, light ? 0.92 : 0.15);
        }

        // Vivid version of the hue, lifted to 3:1 on the island
        static string WallAccentHex(Color pick)
        {
            bool light = Ink.Light;
            var c = FromHsl(pick.GetHue(), Math.Max(pick.GetSaturation(), 0.55), light ? 0.38 : 0.68);
            var bg = Ink.Base.Bg;
            var toward = Luminance(bg) < 0.5 ? Color.White : Color.Black;
            for (int i = 0; i < 12 && Contrast(c, bg) < 3; i++) c = MixRound(c, toward, 0.15);
            return HexOf(c);
        }

        static Color FromHsl(double hue, double s, double l)
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q, h = hue / 360.0;
            Func<double, int> ch = t =>
            {
                t = ((t % 1) + 1) % 1;
                double v = t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
                return (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
            };
            return Color.FromArgb(255, ch(h + 1 / 3.0), ch(h), ch(h - 1 / 3.0));
        }
    }
}
