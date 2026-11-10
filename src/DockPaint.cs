using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Tepi
{
    partial class Island
    {
        sealed partial class DockWindow
        {
            readonly List<KeyValuePair<RectangleF, string>> hits = new List<KeyValuePair<RectangleF, string>>();
            readonly Dictionary<string, Action> acts = new Dictionary<string, Action>(), menus = new Dictionary<string, Action>();
            readonly Dictionary<string, Bitmap> restIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            int restPx;
            readonly Spring labelShow = new Spring(0, 1, 1, 0.002f);
            string labelText = "";

            static RectangleF Inset(RectangleF r, float d) { return new RectangleF(r.X + d, r.Y + d, r.Width - d * 2, r.Height - d * 2); }
            static RectangleF Inset(RectangleF r, float dx, float dy) { return new RectangleF(r.X + dx, r.Y + dy, r.Width - dx * 2, r.Height - dy * 2); }

            void Render()
            {
                if (!shown || closing) return;
                Place();
                if (g == null) return;
                drawnSig = Signature();
                hits.Clear();
                acts.Clear();
                menus.Clear();
                p.T = Glass ? Ink.Base.OnGlass() : Ink.Base;
                p.Alpha = 1;
                p.HalfPx = 0.5f / scale;
                g.ResetTransform();
                g.SetClip(new Rectangle(0, 0, cw, ch));
                g.Clear(Color.Transparent);
                g.ResetClip();
                g.ScaleTransform(scale, scale);
                Compose();
                if (reveal.Value < 0.02f && reveal.Target == 0) DrawSensor();
                else
                {
                    Hit(centerPill, "pill", null, null);
                    // Text on the bar needs the guarded glass; icons alone get the vivid one
                    bool text = !leftPill.IsEmpty || !rightPill.IsEmpty;
                    if (Full) DrawBar(new RectangleF(-1, barTop, cw / scale + 2, BarH + Edge + 2), 0, true);
                    else
                    {
                        Shadow(centerPill);
                        DrawBar(centerPill, Radius, text);
                    }
                    Dividers();
                    DrawSpan(leftPill, true);
                    DrawSpan(rightPill, false);
                    DrawCenter();
                    DrawTop();
                }
                Present();
            }

            void Present()
            {
                g.Flush(FlushIntention.Sync);
                var at = WindowOrigin();
                var dst = new Native.POINT(at.X, at.Y);
                var size = new Native.SIZE(cw, ch);
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
                if (dumpDir != null) DumpDock();
            }

            // TEPI_DUMP: the dock skips screenshots too
            void DumpDock()
            {
                try
                {
                    Directory.CreateDirectory(dumpDir);
                    using (var copy = canvas.Clone(new Rectangle(0, 0, cw, ch), PixelFormat.Format32bppArgb)) copy.Save(Path.Combine(dumpDir, "dock.png"), ImageFormat.Png);
                }
                catch (Exception ex) { Log.Write("dock dump: " + ex.Message); }
            }

            // Hidden: a 2 px edge the pointer can still find
            void DrawSensor()
            {
                float h = 2 / scale;
                g.FillRectangle(p.B(Color.FromArgb(1, 0, 0, 0)), 0, ch / scale - h, cw / scale, h);
                Hit(new RectangleF(0, ch / scale - h, cw / scale, h), "sensor", null, null);
            }

            // Elevation from a cached nine-slice tile; the middle hides under glass
            Bitmap shadowTile;
            int shadowKey, shadowPad, shadowCore;
            const float ShadowRoom = 20;

            void Shadow(RectangleF r)
            {
                if (r.IsEmpty) return;
                int key = (int)(Radius * 100) * 1000 + (int)(scale * 100);
                if (shadowTile == null || key != shadowKey) ShadowTile(key);
                int c = shadowPad + shadowCore;
                var b = Rectangle.FromLTRB((int)Math.Floor(r.Left * scale) - shadowPad, (int)Math.Floor(r.Top * scale) - shadowPad, (int)Math.Ceiling(r.Right * scale) + shadowPad, (int)Math.Ceiling(r.Bottom * scale) + shadowPad);
                b.Offset(-offX, -offY);
                if (b.Width < c * 2 || b.Height < c * 2) return;
                var st = g.Save();
                g.ResetTransform();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                int t = shadowTile.Width;
                // Corners as is, edges stretched from the tile's middle line
                Piece(new Rectangle(b.Left, b.Top, c, c), new Rectangle(0, 0, c, c));
                Piece(new Rectangle(b.Right - c, b.Top, c, c), new Rectangle(t - c, 0, c, c));
                Piece(new Rectangle(b.Left, b.Bottom - c, c, c), new Rectangle(0, t - c, c, c));
                Piece(new Rectangle(b.Right - c, b.Bottom - c, c, c), new Rectangle(t - c, t - c, c, c));
                Piece(new Rectangle(b.Left + c, b.Top, b.Width - c * 2, c), new Rectangle(c, 0, 1, c));
                Piece(new Rectangle(b.Left + c, b.Bottom - c, b.Width - c * 2, c), new Rectangle(c, t - c, 1, c));
                Piece(new Rectangle(b.Left, b.Top + c, c, b.Height - c * 2), new Rectangle(0, c, c, 1));
                Piece(new Rectangle(b.Right - c, b.Top + c, c, b.Height - c * 2), new Rectangle(t - c, c, c, 1));
                g.Restore(st);
            }

            void Piece(Rectangle dst, Rectangle src)
            {
                if (dst.Width > 0 && dst.Height > 0) g.DrawImage(shadowTile, dst, src, GraphicsUnit.Pixel);
            }

            void ShadowTile(int key)
            {
                if (shadowTile != null) shadowTile.Dispose();
                shadowKey = key;
                shadowPad = (int)Math.Ceiling(ShadowRoom * scale);
                shadowCore = (int)Math.Ceiling(Radius * scale) + 2;
                int size = (shadowPad + shadowCore) * 2 + 1;
                shadowTile = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using (var tg = Graphics.FromImage(shadowTile))
                using (var sp = new Painter { G = tg })
                {
                    tg.SmoothingMode = SmoothingMode.AntiAlias;
                    tg.ScaleTransform(scale, scale);
                    float pad = shadowPad / scale, side = (size - shadowPad * 2) / scale;
                    ShadowLayers(sp, new RectangleF(pad, pad, side, side), Radius, 0.85f);
                }
            }

            void DrawBar(RectangleF r, float radius, bool text)
            {
                if (r.IsEmpty) return;
                using (var path = radius > 0 ? Ink.Round(r, radius) : Box(r))
                {
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    bool glass = plate != null && Glass;
                    var img = glass ? (text ? plate.Text : plate.Image) : null;
                    if (glass) Backdrop(img, r, 1f);
                    if (glass && isle.LiquidOn && radius > 0) Lens(img, r, radius);
                    // Solid until glass arrives; over glass only a light veil
                    g.FillPath(p.B(Ink.A(Ink.Base.Bg, glass ? (isle.LiquidOn ? 0.1f : 0.06f) : 0.9f)), path);
                    Sheen(r);
                    g.Restore(st);
                    RimLight(r, path);
                    Rim(r, radius);
                }
            }

            // The plate under r, scaled about r's centre (1 = plain)
            void Backdrop(Bitmap img, RectangleF r, float s)
            {
                var o = WindowOrigin();
                var inner = g.Save();
                g.ResetTransform();
                g.InterpolationMode = s == 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var dst = Rectangle.FromLTRB((int)Math.Floor(r.Left * scale), (int)Math.Floor(r.Top * scale), (int)Math.Ceiling(r.Right * scale), (int)Math.Ceiling(r.Bottom * scale));
                float cx = (r.X + r.Width / 2) * scale + o.X, cy = (r.Y + r.Height / 2) * scale + o.Y;
                var src = new RectangleF((dst.X + o.X - cx) / s + cx - plate.Rect.X, (dst.Y + o.Y - cy) / s + cy - plate.Rect.Y, dst.Width / s, dst.Height / s);
                dst.Offset(-offX, -offY);
                g.DrawImage(img, dst, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel);
                g.Restore(inner);
            }

            // Thick glass bends light at its rim: along the top and bottom edges
            // the backdrop shows a little magnified, as through a lens
            void Lens(Bitmap img, RectangleF r, float radius)
            {
                float rim = Math.Min(7f, r.Height * 0.13f), x0 = r.X + radius * 0.55f, x1 = r.Right - radius * 0.55f;
                if (x1 - x0 < 4) return;
                LensStrip(img, r, new RectangleF(x0, r.Y, x1 - x0, rim));
                LensStrip(img, r, new RectangleF(x0, r.Bottom - rim, x1 - x0, rim));
            }

            void LensStrip(Bitmap img, RectangleF bar, RectangleF strip)
            {
                const float s = 1.14f;
                var o = WindowOrigin();
                var dst = Rectangle.FromLTRB((int)Math.Floor(strip.Left * scale), (int)Math.Floor(strip.Top * scale), (int)Math.Ceiling(strip.Right * scale), (int)Math.Ceiling(strip.Bottom * scale));
                float cx = (bar.X + bar.Width / 2) * scale + o.X, cy = (bar.Y + bar.Height / 2) * scale + o.Y;
                var src = new RectangleF((dst.X + o.X - cx) / s + cx - plate.Rect.X, (dst.Y + o.Y - cy) / s + cy - plate.Rect.Y, dst.Width / s, dst.Height / s);
                var st = g.Save();
                g.ResetTransform();
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                dst.Offset(-offX, -offY);
                g.DrawImage(img, dst, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel);
                g.FillRectangle(p.B(Ink.A(Ink.Base.Bg, 0.08f)), dst);
                g.Restore(st);
            }

            // Glass catches light on its top edge, a little on the bottom
            void RimLight(RectangleF r, GraphicsPath path)
            {
                using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.White, Color.White, 90f))
                {
                    var cb = new ColorBlend(3);
                    cb.Colors = new[] { Color.FromArgb(Ink.Light ? 170 : 120, 255, 255, 255), Color.FromArgb(Ink.Light ? 50 : 16, 255, 255, 255), Color.FromArgb(Ink.Light ? 110 : 52, 255, 255, 255) };
                    cb.Positions = new[] { 0f, 0.5f, 1f };
                    lg.InterpolationColors = cb;
                    using (var pen = new Pen(lg, 1f)) g.DrawPath(pen, path);
                }
            }

            static GraphicsPath Box(RectangleF r)
            {
                var gp = new GraphicsPath();
                gp.AddRectangle(r);
                return gp;
            }

            // Light from above; the pointer warms the glass under it
            void Sheen(RectangleF r)
            {
                float h = Math.Min(r.Height * 0.55f, 28);
                using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, h + 2), Color.FromArgb(Ink.Light ? 80 : 24, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(lg, r.X, r.Y, r.Width, h);
                if (hover.Value < 0.01f || mouseX < r.Left - 60 || mouseX > r.Right + 60) return;
                float rx = Math.Max(70, r.Height * 1.7f), ry = r.Height * 1.05f;
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(mouseX - rx, r.Top - ry * 0.55f, rx * 2, ry * 2);
                    using (var pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb((int)(hover.Value * (Ink.Light ? 80 : 40)), 255, 255, 255);
                        pb.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(pb, gp);
                    }
                }
            }

            // Top edge catches the light near the pointer
            void Rim(RectangleF r, float radius)
            {
                if (hover.Value < 0.01f || mouseX < r.Left || mouseX > r.Right) return;
                float span = Math.Max(60, r.Height * 1.5f);
                float a = Math.Max(r.Left + radius * 0.7f, mouseX - span), b = Math.Min(r.Right - radius * 0.7f, mouseX + span);
                if (b - a < 2) return;
                using (var lg = new LinearGradientBrush(new PointF(mouseX - span, 0), new PointF(mouseX + span, 0), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                {
                    var blend = new ColorBlend(3);
                    int peak = (int)(hover.Value * (Ink.Light ? 230 : 150));
                    blend.Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(peak, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                    blend.Positions = new[] { 0f, 0.5f, 1f };
                    lg.InterpolationColors = blend;
                    using (var pen = new Pen(lg, 1.3f)) g.DrawLine(pen, a, r.Top + 0.65f, b, r.Top + 0.65f);
                }
            }

            void Hit(RectangleF r, string id, Action act, Action menu)
            {
                hits.Add(new KeyValuePair<RectangleF, string>(r, id));
                if (act != null) acts[id] = act;
                if (menu != null) menus[id] = menu;
            }

            void DrawCenter()
            {
                foreach (var s in slots)
                {
                    if (s.Show < 0.01f || s.Id == "gap") continue;
                    if (s.Sep)
                    {
                        float cx = s.Icon.X + s.Icon.Width / 2;
                        g.DrawLine(p.P(Ink.A(p.T.Line, s.Show * 1.6f), 1f), cx, s.Icon.Top, cx, s.Icon.Bottom);
                        continue;
                    }
                    if (s.App == null) DrawSys(s);
                    else DrawApp(s);
                }
            }

            void DrawSys(Slot s)
            {
                string id = "sys:" + s.Id;
                bool hot = hoverId == id;
                var r = s.Icon;
                if (pressId == id) r = Inset(r, r.Width * 0.05f);
                Action act = null, menu = null;
                switch (s.Id)
                {
                    case "start": act = delegate { ToggleSheet("start"); }; menu = QuickMenu; break;
                    case "search": act = delegate { ToggleSheet("start"); }; break;
                    case "taskview": act = delegate { ShellVerb("WindowSwitcher"); }; break;
                }
                Hit(s.Hit, id, act, menu);
                hot |= keyMode && keySel == id;
                Plate(OnBar(s.Icon, 0, pressId == id), hot, pressId == id, 1);
                if (keyMode && keySel == id) FocusRing(s.Icon);
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, u = r.Width / 42f;
                Color ink = hot ? p.T.Text : Ink.Mix(p.T.Sub, p.T.Text, 0.55f);
                switch (s.Id)
                {
                    case "start":
                        float q = 6.6f * u, gap = 1.7f * u;
                        for (int i = 0; i < 4; i++)
                            p.FillRound(new RectangleF(cx - q - gap / 2 + (i % 2) * (q + gap), cy - q - gap / 2 + (i / 2) * (q + gap), q, q), 1.4f * u, ink);
                        break;
                    case "search": p.Icon('\uE721', 16 * u, ink, cx, cy); break;
                    case "taskview": p.Icon('\uE7C4', 16 * u, ink, cx, cy); break;
                }
            }

            void DrawApp(Slot s)
            {
                var a = s.App;
                if (a == dragApp) return;
                var app = a;
                Hit(s.Hit, s.Id, delegate { OpenApp(app, false); }, delegate { AppMenu(app); });
                bool focus = keyMode && keySel == s.Id;
                bool hot = (hoverId == s.Id && dragApp == null) || focus, down = pressId == s.Id && dragApp == null;
                Plate(OnBar(s.Icon, HopLift(a), down), hot, down, s.Show);
                if (focus) FocusRing(s.Icon);
                var r = Glyph(down ? Inset(s.Icon, s.Icon.Width * 0.05f) : s.Icon);
                DrawIconImage(a, r, s.Show);
                Indicator(a, s.Icon);
                if (a.Badge > 0 && s.Show > 0.5f) Badge(s.Icon, a.Badge);
            }

            // Apps and system buttons sit on one tile shape
            void Tile(RectangleF slot, bool hot, bool down, float alpha)
            {
                Plate(Inset(slot, slot.Width * (down ? 0.1f : 0.07f)), hot, down, alpha);
            }

            void Plate(RectangleF plate, bool hot, bool down, float alpha)
            {
                p.Alpha = Ease.Clamp01(alpha);
                p.FillRound(plate, Math.Min(plate.Width, plate.Height) * 0.3f, down ? p.T.FillPressed : hot ? p.T.FillHover : p.T.Fill);
                p.Alpha = 1;
            }

            // Icon may rise; plate stays put
            RectangleF OnBar(RectangleF slot, float lift, bool down)
            {
                var plate = Inset(new RectangleF(slot.X, slot.Y + lift, slot.Width, slot.Height), slot.Width * (down ? 0.1f : 0.07f));
                float top = Math.Max(plate.Y, barTop + 4);
                return RectangleF.FromLTRB(plate.Left, Math.Min(top, plate.Bottom - 8), plate.Right, plate.Bottom);
            }

            // Keyboard focus: a ring the eye finds at once
            void FocusRing(RectangleF slot)
            {
                var ring = Inset(slot, slot.Width * 0.02f);
                p.StrokeRound(ring, ring.Width * 0.32f, p.T.Text, 2f);
            }

            static RectangleF Glyph(RectangleF slot)
            {
                float s = slot.Width * GlyphK;
                return new RectangleF(slot.X + (slot.Width - s) / 2, slot.Y + (slot.Height - s) / 2, s, s);
            }

            void DrawIconImage(DockApp a, RectangleF r, float alpha)
            {
                Bitmap ic;
                icons.TryGetValue(a.Key, out ic);
                if (ic == null)
                {
                    // No icon: the name's first letter on a tile
                    p.Alpha = alpha;
                    p.TextCenter(a.Name.Length > 0 ? a.Name.Substring(0, 1).ToUpperInvariant() : "?", Ink.Semi(r.Width * 0.62f), p.T.Text, r.X + r.Width / 2, r.Y + r.Height / 2);
                    p.Alpha = 1;
                    return;
                }
                // At rest: an exact pixel copy, crisp at any scale
                if (alpha > 0.995f && Math.Abs(r.Width - iconSize * GlyphK) < 0.02f)
                {
                    var rest = RestIcon(a.Key, ic);
                    if (rest != null) { DrawExact(rest, r); return; }
                }
                if (alpha > 0.995f)
                {
                    // Magnified: from a copy near the drawn size, cheap to scale
                    var big = BigIcon(a.Key, ic);
                    var keep = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(big ?? ic, r);
                    g.InterpolationMode = keep;
                    return;
                }
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = Ease.Clamp01(alpha) });
                    var dst = new Rectangle((int)Math.Round(r.X * scale) - offX, (int)Math.Round(r.Y * scale) - offY, Math.Max(1, (int)Math.Round(r.Width * scale)), Math.Max(1, (int)Math.Round(r.Height * scale)));
                    var st = g.Save();
                    g.ResetTransform();
                    g.DrawImage(ic, dst, 0, 0, ic.Width, ic.Height, GraphicsUnit.Pixel, ia);
                    g.Restore(st);
                }
            }

            Bitmap RestIcon(string key, Bitmap src)
            {
                int px = Math.Max(8, (int)Math.Round(iconSize * GlyphK * scale));
                if (px != restPx)
                {
                    foreach (var b in restIcons.Values) if (b != null) b.Dispose();
                    restIcons.Clear();
                    restPx = px;
                }
                Bitmap bmp;
                if (restIcons.TryGetValue(key, out bmp)) return bmp;
                bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.DrawImage(src, new Rectangle(0, 0, px, px));
                }
                restIcons[key] = bmp;
                return bmp;
            }

            readonly Dictionary<string, Bitmap> bigIcons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            int bigPx;

            Bitmap BigIcon(string key, Bitmap src)
            {
                int px = Math.Max(8, (int)Math.Ceiling(iconSize * GlyphK * Math.Max(1, Mag) * scale));
                if (px >= src.Width) return null;
                if (px != bigPx)
                {
                    foreach (var b in bigIcons.Values) if (b != null) b.Dispose();
                    bigIcons.Clear();
                    bigPx = px;
                }
                Bitmap bmp;
                if (bigIcons.TryGetValue(key, out bmp)) return bmp;
                bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.DrawImage(src, new Rectangle(0, 0, px, px));
                }
                bigIcons[key] = bmp;
                return bmp;
            }

            // A capsule drawn once into its own bitmap, with its hit spots
            sealed class SideCache
            {
                public Bitmap Bmp;
                public string Sig = "";
                public readonly List<KeyValuePair<RectangleF, string>> Hits = new List<KeyValuePair<RectangleF, string>>();
                public readonly Dictionary<string, Action> Acts = new Dictionary<string, Action>(), Menus = new Dictionary<string, Action>();

                public void Drop()
                {
                    if (Bmp != null) Bmp.Dispose();
                    Bmp = null;
                    Sig = "";
                }
            }

            readonly SideCache leftSide = new SideCache(), rightSide = new SideCache();
            const int SpanPad = 4;
            // Pixel origin of the surface being drawn; zero for the canvas
            int offX, offY;

            // Widgets or tray, drawn once onto a clear bitmap and reused while
            // only their position changes (the bar grows under the pointer)
            void DrawSpan(RectangleF span, bool left)
            {
                if (span.IsEmpty && left) DropGhosts();
                if (span.IsEmpty) return;
                var c = left ? leftSide : rightSide;
                const int pad = SpanPad;
                int w = (int)Math.Ceiling(span.Width * scale) + pad * 2, h = (int)Math.Ceiling(span.Height * scale) + pad * 2;
                int px = (int)Math.Round(span.X * scale) - pad, py = (int)Math.Round(span.Y * scale) - pad;
                string sig = SpanSig(left) + "@" + w + "x" + h;
                if (sig != c.Sig || c.Bmp == null)
                {
                    if (left && c.Bmp != null) KeepGhosts(c.Bmp);
                    if (c.Bmp == null || c.Bmp.Width != w || c.Bmp.Height != h)
                    {
                        if (c.Bmp != null) c.Bmp.Dispose();
                        c.Bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    }
                    var main = g;
                    int from = hits.Count;
                    using (var sg = Graphics.FromImage(c.Bmp))
                    {
                        sg.Clear(Color.Transparent);
                        sg.SmoothingMode = SmoothingMode.AntiAlias;
                        sg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // The span lands exactly on (pad, pad)
                        sg.TranslateTransform(pad - span.X * scale, pad - span.Y * scale);
                        sg.ScaleTransform(scale, scale);
                        g = sg;
                        p.G = sg;
                        offX = px;
                        offY = py;
                        try { if (left) DrawLeft(); else DrawRight(); }
                        finally
                        {
                            g = main;
                            p.G = main;
                            offX = offY = 0;
                        }
                    }
                    c.Sig = sig;
                    c.Hits.Clear();
                    c.Acts.Clear();
                    c.Menus.Clear();
                    for (int i = from; i < hits.Count; i++)
                    {
                        string id = hits[i].Value;
                        var r = hits[i].Key;
                        r.Offset(-span.X, -span.Y);
                        c.Hits.Add(new KeyValuePair<RectangleF, string>(r, id));
                        Action a;
                        if (acts.TryGetValue(id, out a)) c.Acts[id] = a;
                        if (menus.TryGetValue(id, out a)) c.Menus[id] = a;
                    }
                    hits.RemoveRange(from, hits.Count - from);
                }
                // Hit spots and tray anchors follow the span
                foreach (var kv in c.Hits)
                {
                    var r = kv.Key;
                    r.Offset(span.X, span.Y);
                    hits.Add(new KeyValuePair<RectangleF, string>(r, kv.Value));
                    if (!left && kv.Value.StartsWith("tray:", StringComparison.Ordinal))
                    {
                        var ic = TrayById(kv.Value);
                        if (ic != null) ic.Spot = ToScreen(r);
                    }
                }
                foreach (var kv in c.Acts) acts[kv.Key] = kv.Value;
                foreach (var kv in c.Menus) menus[kv.Key] = kv.Value;
                var st = g.Save();
                g.ResetTransform();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(c.Bmp, px, py, w, h);
                g.Restore(st);
            }

            // Thin lines between widgets, apps and the tray
            void Dividers()
            {
                if (!float.IsNaN(sepA)) g.DrawLine(p.P(Ink.A(p.T.Line, 1.6f * leftPresence), 1f), sepA, barTop + 12, sepA, barBottom - 12);
                if (!float.IsNaN(sepB)) g.DrawLine(p.P(Ink.A(p.T.Line, 1.6f), 1f), sepB, barTop + 12, sepB, barBottom - 12);
            }

            // Everything a span shows, apart from where it sits
            string SpanSig(bool left)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(Ink.Epoch).Append(scale).Append(Lang.English).Append('|');
                if (hoverId != null && Owns(hoverId, left)) sb.Append('h').Append(hoverId);
                if (pressId != null && Owns(pressId, left)) sb.Append('p').Append(pressId);
                var now = DateTime.Now;
                if (left)
                {
                    foreach (var id in LeftWidgets()) sb.Append(id).Append(',');
                    foreach (var id in DockWidgetIds) sb.Append((int)(WidgetShown(id) * 1000)).Append(',');
                    sb.Append(Fmt.Clock(now)).Append(now.Day);
                    var mi = isle.mediaInfo;
                    if (mi.Present) sb.Append(mi.Title).Append(mi.Artist).Append(mi.App).Append(mi.Playing).Append(mi.Cover != null ? mi.Cover.GetHashCode() : 0);
                    sb.Append(isle.quotaVersion).Append(isle.BusyCount());
                    if (isle.TimerOn) sb.Append(isle.TimerClock()).Append(isle.timerPaused).Append(isle.TimerLabel()).Append((int)(isle.TimerFraction() * 300));
                    if (isle.weather != null) sb.Append(isle.weather.Temp).Append(isle.weather.Code);
                    sb.Append(isle.battery).Append(isle.onAc).Append(cpuPct).Append(ramPct);
                }
                else
                {
                    foreach (var ic in InlineTray()) sb.Append(ic.Key).Append(ic.Stamp).Append(',');
                    sb.Append(OverflowTray().Count > 0).Append(card != null && card.Kind == DockCard.TrayKind);
                    sb.Append(isle.net.Current.Sig).Append(Math.Round(isle.vol, 2)).Append(isle.muted);
                    sb.Append(isle.hasBattery).Append(isle.battery).Append(isle.onAc);
                    sb.Append(Fmt.Clock(now)).Append(now.Day).Append(isle.unread);
                }
                return sb.ToString();
            }

            static bool Owns(string id, bool left)
            {
                if (left) return id.StartsWith("w_", StringComparison.Ordinal);
                return id.StartsWith("tray:", StringComparison.Ordinal) || id == "chev" || id == "status" || id == "vol" || id == "clock" || id == "desk";
            }

            public void FreeCaches()
            {
                foreach (var b in restIcons.Values) if (b != null) b.Dispose();
                foreach (var b in bigIcons.Values) if (b != null) b.Dispose();
                restIcons.Clear();
                bigIcons.Clear();
                DropGhosts();
                leftSide.Drop();
                rightSide.Drop();
            }

            void DrawExact(Bitmap bmp, RectangleF r)
            {
                var st = g.Save();
                g.ResetTransform();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, (int)Math.Round(r.X * scale) - offX, (int)Math.Round(r.Y * scale) - offY, bmp.Width, bmp.Height);
                g.Restore(st);
            }

            // Dot when running, a wider bar for the front app; accent when it calls for you
            void Indicator(DockApp a, RectangleF r)
            {
                float v = a.Dot.Value;
                if (v < 0.02f) return;
                float focus = Ease.Clamp01(v - 1), cx = r.X + r.Width / 2, cy = barBottom - 4.6f;
                float w = 4 + 12 * focus, h = 3.6f;
                Color c = a.Attention ? p.T.Accent : Ink.Mix(Ink.A(p.T.Text, 0.62f), p.T.Text, focus);
                p.FillRound(new RectangleF(cx - w / 2, cy - h / 2, w, h), h / 2, Ink.A(c, Ease.Clamp01(v)));
            }

            // White numbers on it reach 4.77:1
            static readonly Color BadgeRed = Color.FromArgb(255, 217, 48, 37);

            // Unread count from Windows notifications
            void Badge(RectangleF r, int n)
            {
                string s = n > 99 ? "99+" : n.ToString();
                var f = Ink.SmallSemi(11f);
                float h = 17, w = Math.Max(h, p.Measure(s, f) + 9);
                var b = new RectangleF(r.Right - w * 0.7f, r.Top - h * 0.28f, w, h);
                p.FillRound(Inset(b, -1.2f), (h + 2.4f) / 2, Ink.A(Ink.Base.Bg, 0.55f));
                p.FillRound(b, h / 2, BadgeRed);
                p.TextCenter(s, f, Color.White, b.X + b.Width / 2, b.Y + h / 2);
            }

            void DrawTop()
            {
                RectangleF anchor;
                string text = LabelFor(keyMode ? keySel : hoverId, out anchor);
                if (text.Length > 0) labelText = text;
                float t = labelShow.Value;
                if (t > 0.01f && labelText.Length > 0 && !anchor.IsEmpty) DrawLabel(labelText, anchor, t);
                if (dragApp != null) DrawDragGhost();
            }

            string LabelFor(string id, out RectangleF anchor)
            {
                anchor = RectangleF.Empty;
                if (string.IsNullOrEmpty(id) || dragApp != null) return "";
                foreach (var s in slots)
                    if (s.Id == id && s.App != null) { anchor = s.Icon; return s.App.Name; }
                    else if (s.Id == id.Replace("sys:", "") && id.StartsWith("sys:", StringComparison.Ordinal))
                    {
                        anchor = s.Icon;
                        return s.Id == "start" ? "Start" : s.Id == "search" ? Lang.T("Cari") : Lang.T("Semua jendela");
                    }
                foreach (var kv in hits)
                    if (kv.Value == id && id.StartsWith("tray:", StringComparison.Ordinal))
                    {
                        var ic = trayIcons.Find(x => "tray:" + x.Key == id);
                        if (ic != null && (ic.Version < 4 || ic.ShowTip)) { anchor = kv.Key; return FirstLine(ic.Tip); }
                    }
                return "";
            }

            static string FirstLine(string s)
            {
                int n = s.IndexOfAny(new[] { '\r', '\n' });
                return (n >= 0 ? s.Substring(0, n) : s).Trim();
            }

            void DrawLabel(string text, RectangleF anchor, float t)
            {
                var f = Ink.Semi(12f);
                float w = Math.Min(300, p.Measure(text, f) + 22), h = 26;
                float cx = Math.Max(w / 2 + 4, Math.Min(cw / scale - w / 2 - 4, anchor.X + anchor.Width / 2));
                float bottom = anchor.Y - 9 + (1 - t) * 5;
                var r = new RectangleF(cx - w / 2, bottom - h, w, h);
                p.Alpha = t;
                p.FillRound(r, 9, Ink.A(Ink.Base.Bg, 0.9f));
                p.StrokeRound(r, 9, p.T.Line, 1f);
                p.Text(text, f, p.T.Text, cx, r.Y + h / 2, w - 22, StringAlignment.Center);
                p.Alpha = 1;
            }

            bool StepLabel(float dt)
            {
                labelShow.Step(dt);
                return !labelShow.Settled;
            }

            // Tray, network and sound, battery, clock, the desktop sliver
            void DrawRight()
            {
                if (rightPill.IsEmpty) return;
                var r = rightPill;
                float x = r.X, top = PlateCy(r) - PlateH / 2;
                foreach (var ic in InlineTray())
                {
                    DrawTrayIcon(ic, new RectangleF(x, top, 26, PlateH));
                    x += 26;
                }
                if (OverflowTray().Count > 0)
                {
                    var cr = new RectangleF(x, top, 24, PlateH);
                    bool hot = hoverId == "chev" || (card != null && card.Kind == DockCard.TrayKind);
                    Hit(cr, "chev", ToggleTrayCard, null);
                    if (hot) p.FillRound(cr, PlateR, pressId == "chev" ? p.T.FillPressed : p.T.FillHover);
                    // Points up while closed, down while the grid is open
                    p.Icon(card != null && card.Kind == DockCard.TrayKind ? '\uE70D' : '\uE70E', 10f, hot ? p.T.Text : p.T.Sub, cr.X + cr.Width / 2, cr.Y + cr.Height / 2);
                    x += 24;
                }
                DrawStatus(new RectangleF(x, top, StatusWidth(), PlateH));
                x += StatusWidth() + 4;
                DrawClock(new RectangleF(x, top, ClockWidth(), PlateH));
                // Show desktop: the bar's far end, padding included
                var desk = new RectangleF(r.Right - 10, r.Y, 10 + PadX, r.Height);
                Hit(desk, "desk", ShowDesktop, null);
                if (hoverId == "desk") g.DrawLine(p.P(p.T.Sub, 1.2f), desk.X + 4.5f, r.Y + 13, desk.X + 4.5f, r.Bottom - 13);
            }

            void DrawTrayIcon(TrayIcon ic, RectangleF cell)
            {
                string id = "tray:" + ic.Key;
                Hit(cell, id, null, null);
                ic.Spot = ToScreen(cell);
                if (hoverId == id) p.FillRound(cell, PlateR, pressId == id ? p.T.FillPressed : p.T.FillHover);
                var bmp = tray != null ? tray.Picture(ic) : null;
                if (bmp == null) return;
                float s = 16;
                var dst = new RectangleF(cell.X + (cell.Width - s) / 2, cell.Y + (cell.Height - s) / 2, s, s);
                g.DrawImage(bmp, dst);
            }

            void DrawStatus(RectangleF r)
            {
                bool hot = hoverId == "status";
                Hit(r, "status", delegate { ToggleSheet("quick"); }, null);
                if (hot) p.FillRound(r, PlateR, pressId == "status" ? p.T.FillPressed : p.T.FillHover);
                // The icon group, centred: Wi-Fi to the battery's cap
                float span = isle.hasBattery ? 66 : 36.5f;
                float cx = r.X + (r.Width - span) / 2 + 7.25f, cy = r.Y + r.Height / 2;
                var net = isle.net.Current;
                char netGlyph = !net.Any ? Ink.IcoNoNet : net.Wifi ? Ink.IcoWifi : '\uE839';
                p.Icon(netGlyph, 14.5f, net.Online || !net.Any ? p.T.Text : p.T.Sub, cx, cy);
                cx += 22;
                char volGlyph = isle.muted ? Ink.IcoMute : isle.vol < 0.01f ? Ink.IcoVol0 : isle.vol < 0.34f ? Ink.IcoVol1 : isle.vol < 0.67f ? Ink.IcoVol2 : Ink.IcoVol3;
                p.Icon(volGlyph, 14.5f, p.T.Text, cx, cy);
                if (isle.hasBattery)
                {
                    cx += 24;
                    p.Battery(cx - 9, cy, isle.battery / 100f, isle.battery <= 20 && !isle.onAc ? p.T.Negative : p.T.Text, isle.onAc);
                }
            }

            void DrawClock(RectangleF r)
            {
                bool hot = hoverId == "clock";
                Hit(r, "clock", delegate { ToggleSheet("cal"); }, ClockMenu);
                if (hot) p.FillRound(r, PlateR, pressId == "clock" ? p.T.FillPressed : p.T.FillHover);
                var now = DateTime.Now;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                p.TextCenter(Fmt.Clock(now), Ink.Semi(12.5f), p.T.Text, cx, cy - 7);
                p.TextCenter(now.ToString("d MMM yyyy", Fmt.Id), Ink.Small(11f), p.T.Sub, cx, cy + 8);
                // Messages waiting: a count, like the app badges
                if (isle.unread > 0) Badge(r, isle.unread);
            }

            static void ShowDesktop() { ShellVerb("ToggleDesktop"); }

            // Explorer's own commands, no simulated keys
            static void ShellVerb(string verb)
            {
                try
                {
                    var t = Type.GetTypeFromProgID("Shell.Application");
                    object sh = Activator.CreateInstance(t);
                    t.InvokeMember(verb, BindingFlags.InvokeMethod, null, sh, null);
                    Marshal.ReleaseComObject(sh);
                }
                catch (Exception ex) { Log.Write("dock " + verb + ": " + ex.Message); }
            }

            // Live widgets in the left capsule
            void DrawLeft()
            {
                if (leftPill.IsEmpty) return;
                float x = leftPill.X;
                var want = LeftWidgets();
                foreach (var id in DockWidgetIds)
                {
                    float s = WidgetShown(id);
                    if (s < 0.01f) { DropGhost(id); continue; }
                    bool here = want.Contains(id);
                    var r = new RectangleF(x, leftPill.Y + 6, WidgetW(id), leftPill.Height - 12);
                    x += (WidgetW(id) + 6) * s;
                    if (here && s > 0.995f)
                    {
                        DrawWidget(id, r);
                        widgetPx[id] = new Point((int)Math.Floor((r.X - 1 - leftPill.X) * scale) + SpanPad, (int)Math.Ceiling((r.Right + 1 - leftPill.X) * scale) + SpanPad);
                        continue;
                    }
                    // Arriving or leaving: fades while resizing
                    widgetPx.Remove(id);
                    var st = g.Save();
                    g.SetClip(new RectangleF(r.X - 1, leftPill.Y, (r.Width + 2) * s, leftPill.Height), CombineMode.Intersect);
                    if (here) { p.Alpha = s * s; DrawWidget(id, r); p.Alpha = 1; }
                    else DrawGhost(id, r, s * s);
                    g.Restore(st);
                }
            }

            // Leavers' last picture and bitmap columns
            readonly Dictionary<string, Bitmap> ghosts = new Dictionary<string, Bitmap>();
            readonly Dictionary<string, Point> widgetPx = new Dictionary<string, Point>();

            // Before a redraw, leavers keep their look
            void KeepGhosts(Bitmap old)
            {
                var want = LeftWidgets();
                foreach (var id in DockWidgetIds)
                {
                    Point cols;
                    if (want.Contains(id)) { DropGhost(id); continue; }
                    if (ghosts.ContainsKey(id) || !widgetPx.TryGetValue(id, out cols)) continue;
                    widgetPx.Remove(id);
                    var at = Rectangle.Intersect(Rectangle.FromLTRB(cols.X, 0, cols.Y, old.Height), new Rectangle(0, 0, old.Width, old.Height));
                    if (at.Width > 0 && at.Height > 0) ghosts[id] = old.Clone(at, PixelFormat.Format32bppPArgb);
                }
            }

            void DropGhost(string id)
            {
                Bitmap b;
                if (!ghosts.TryGetValue(id, out b)) return;
                b.Dispose();
                ghosts.Remove(id);
            }

            void DropGhosts()
            {
                foreach (var b in ghosts.Values) b.Dispose();
                ghosts.Clear();
                widgetPx.Clear();
            }

            void DrawGhost(string id, RectangleF r, float alpha)
            {
                Bitmap b;
                if (!ghosts.TryGetValue(id, out b)) return;
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = Ease.Clamp01(alpha) });
                    var st = g.Save();
                    g.ResetTransform();
                    int dx = (int)Math.Floor((r.X - 1 - leftPill.X) * scale) + SpanPad;
                    g.DrawImage(b, new Rectangle(dx, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
                    g.Restore(st);
                }
            }

            void DrawWidget(string id, RectangleF r)
            {
                string hid = "w_" + id;
                bool hot = hoverId == hid;
                Action open = null;
                switch (id)
                {
                    case "clock": open = delegate { isle.OpenFromDock(PageHome); }; break;
                    case "media": open = delegate { isle.OpenFromDock(PageMusic); }; break;
                    case "ai": open = delegate { isle.OpenFromDock(PageAgents); }; break;
                    case "timer": open = delegate { isle.OpenFromDock(PageControl); }; break;
                    case "weather": open = delegate { ToggleSheet("weather"); }; break;
                    case "battery": open = delegate { isle.OpenFromDock(PageControl); }; break;
                    case "system": open = delegate { Open("taskmgr.exe"); }; break;
                }
                Hit(r, hid, open, null);
                if (hot && open != null) p.FillRound(r, 12, pressId == hid ? p.T.FillPressed : p.T.Fill);
                float cy = r.Y + r.Height / 2;
                switch (id)
                {
                    case "clock":
                        p.Text(Fmt.Clock(DateTime.Now), Ink.Display(18f), p.T.Text, r.X + 10, cy - 7, r.Width - 14, StringAlignment.Near);
                        p.Text(DateTime.Now.ToString("ddd, d MMM", Fmt.Id), Ink.Small(11f), p.T.Sub, r.X + 10, cy + 11, r.Width - 14, StringAlignment.Near);
                        break;
                    case "media": DrawMedia(r, cy); break;
                    case "ai": DrawAi(r, cy); break;
                    case "timer":
                        Ring(r.X + 20, cy, 11, isle.TimerFraction(), isle.timerPaused);
                        p.Text(isle.TimerClock(), Ink.Semi(13.5f), isle.timerPaused ? p.T.Sub : p.T.Text, r.X + 38, cy - 6, r.Width - 42, StringAlignment.Near);
                        p.Text(isle.TimerLabel(), Ink.Small(11f), p.T.Sub, r.X + 38, cy + 10, r.Width - 42, StringAlignment.Near);
                        break;
                    case "weather":
                        WeatherIcon(p, isle.weather.Code, r.X + 19, cy, 17f, p.T.Text);
                        p.Text(isle.weather.Temp + "°", Ink.Semi(14f), p.T.Text, r.X + 35, cy - 6, r.Width - 37, StringAlignment.Near);
                        p.Text(WeatherWord(isle.weather.Code), Ink.Small(11f), p.T.Sub, r.X + 35, cy + 10, r.Width - 37, StringAlignment.Near);
                        break;
                    case "battery":
                        p.Battery(r.X + 9, cy, isle.battery / 100f, isle.battery <= 20 && !isle.onAc ? p.T.Negative : p.T.Text, isle.onAc);
                        p.Text(Ink.Percent(isle.battery), Ink.Semi(14f), isle.battery <= 20 && !isle.onAc ? p.T.Negative : p.T.Text, r.X + 44, cy - 6, r.Width - 46, StringAlignment.Near);
                        p.Text(isle.onAc ? Lang.T("mengisi daya") : Lang.T("Baterai"), Ink.Small(11f), p.T.Sub, r.X + 44, cy + 10, r.Width - 46, StringAlignment.Near);
                        break;
                    case "system":
                        Meter(r.X + 10, cy - 8, r.Width - 18, "CPU", cpuPct);
                        Meter(r.X + 10, cy + 10, r.Width - 18, "RAM", ramPct);
                        break;
                }
            }

            // Font has sun and cloud only
            public static void WeatherIcon(Painter p, int code, float cx, float cy, float size, Color c)
            {
                if (code == 0) { p.Icon('\uE706', size, c, cx, cy); return; }
                if (code == 45 || code == 48)
                {
                    var fog = p.P(c, Math.Max(1.2f, size / 13));
                    for (int i = 0; i < 3; i++)
                    {
                        float half = size * (i == 1 ? 0.42f : 0.32f), y = cy - size * 0.24f + i * size * 0.24f;
                        p.G.DrawLine(fog, cx - half, y, cx + half, y);
                    }
                    return;
                }
                bool storm = code >= 95, drizzle = code >= 51 && code <= 57, wet = storm || IsRain(code) || drizzle;
                if (!wet) { p.Icon('\uE753', size, c, cx, cy); return; }
                p.Icon('\uE753', size, c, cx, cy - size * 0.16f);
                var pen = p.P(c, Math.Max(1.2f, size / 13));
                float top = cy + size * 0.16f, bottom = cy + size * 0.42f;
                if (storm)
                {
                    float s = size;
                    p.G.DrawLines(pen, new[] { new PointF(cx + s * 0.06f, top), new PointF(cx - s * 0.08f, cy + s * 0.31f), new PointF(cx + s * 0.06f, cy + s * 0.31f), new PointF(cx - s * 0.08f, bottom + s * 0.04f) });
                    return;
                }
                for (int i = -1; i <= 1; i++)
                {
                    float x = cx + i * size * 0.22f;
                    if (drizzle) p.Circle(x, top + size * 0.12f, size / 18, c);
                    else p.G.DrawLine(pen, x + size * 0.05f, top, x - size * 0.05f, bottom);
                }
            }

            // Cover, title, and a play button that stays put
            void DrawMedia(RectangleF r, float cy)
            {
                var mi = isle.mediaInfo;
                var cover = new RectangleF(r.X + 6, cy - 18, 36, 36);
                if (mi.Cover != null) p.Image(mi.Cover, cover, 9);
                else p.FillRound(cover, 9, p.T.FillHover);
                float tx = cover.Right + 9, play = r.Right - 18;
                p.Text(mi.Title, Ink.Semi(12.5f), p.T.Text, tx, cy - 7, play - 18 - tx, StringAlignment.Near);
                p.Text(mi.Artist.Length > 0 ? mi.Artist : mi.App, Ink.Small(11f), p.T.Sub, tx, cy + 9, play - 18 - tx, StringAlignment.Near);
                var btn = new RectangleF(play - 14, cy - 14, 28, 28);
                Hit(btn, "w_play", delegate { isle.media.TogglePlay(); }, null);
                p.Circle(play, cy, hoverId == "w_play" ? 14 : 13, p.T.Text);
                if (mi.Playing) p.Pause(play, cy, 9.5f, p.T.Bg);
                else p.Play(play, cy, 10f, p.T.Bg);
            }

            // Highest quota as a ring; a busy agent fills its centre
            void DrawAi(RectangleF r, float cy)
            {
                float top = 0;
                string label = "";
                foreach (var q in isle.quotas)
                {
                    if (!QuotaShown(q)) continue;
                    for (int i = 0; i < q.W.Length; i++)
                        if (q.W[i].Has && q.W[i].Pct >= top) { top = q.W[i].Pct; label = WindowLabel(q.W[i], i); }
                }
                int busy = isle.BusyCount();
                Ring(r.X + 20, cy, 12, top / 100f, false);
                if (busy > 0) p.Circle(r.X + 20, cy, 4f, p.T.Accent);
                string big = label.Length > 0 ? Ink.Percent((int)Math.Floor(top)) : Lang.F("{0} agen", busy);
                p.Text(big, Ink.Semi(13.5f), p.T.Text, r.X + 40, cy - 6, r.Width - 44, StringAlignment.Near);
                string small = busy > 0 ? Lang.F("{0} bekerja", busy) : label;
                p.Text(small, Ink.Small(11f), p.T.Sub, r.X + 40, cy + 10, r.Width - 44, StringAlignment.Near);
            }

            void Ring(float cx, float cy, float rad, float f, bool quiet)
            {
                var rect = new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2);
                g.DrawEllipse(p.P(p.T.Line, 2.4f), rect);
                float sweep = 360f * Ease.Clamp01(f);
                if (sweep > 0.5f) g.DrawArc(p.P(quiet ? p.T.Faint : p.T.Progress, 2.4f), rect, -90, sweep);
            }

            void Meter(float x, float cy, float w, string name, int pct)
            {
                p.Text(name, Ink.SmallSemi(11f), p.T.Sub, x, cy, 30, StringAlignment.Near);
                p.TextRight(pct < 0 ? "–" : Ink.Percent(pct), Ink.SmallSemi(11f), p.T.Text, x + w, cy);
                var track = new RectangleF(x + 30, cy - 1.5f, w - 30 - 34, 3);
                p.FillRound(track, 1.5f, p.T.Line);
                if (pct > 0) p.FillRound(new RectangleF(track.X, track.Y, Math.Max(3, track.Width * pct / 100f), 3), 1.5f, pct >= 90 ? p.T.Negative : p.T.Progress);
            }
        }
    }
}
