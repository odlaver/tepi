using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Tepi
{
    partial class Island
    {
        sealed partial class DockWindow
        {
            // One spot in the centre capsule: a system button, a divider, or an app
            sealed class Slot
            {
                public string Id = "";
                public DockApp App;
                public bool Sep;
                public RectangleF Icon, Hit;
                public float Grow = 1, Show = 1;
            }

            // Sizes in DIPs
            float IconRest
            {
                get
                {
                    string s = S.Get("dockbar.size", "m");
                    return s == "s" ? 34f : s == "l" ? 50f : 42f;
                }
            }

            float Mag
            {
                get
                {
                    if (!S.GetBool("dockbar.magnify", true) || Anim.Reduced) return 1f;
                    return Math.Max(1f, Math.Min(2f, S.GetInt("dockbar.mag", 150) / 100f));
                }
            }

            const float PadTop = 7, PadBottom = 9, GapX = 8, PadX = 12, Side = 8, SegGap = 21, Hop = 18, HopSecs = 0.42f;
            float BarH { get { return IconRest + PadTop + PadBottom; } }
            // Every tile and hover plate shares this size and centre line
            const float GlyphK = 0.62f;
            float PlateH { get { return IconRest * 0.86f; } }
            float PlateR { get { return PlateH * 0.3f; } }
            float PlateCy(RectangleF pill) { return pill.Y + PadTop + IconRest / 2; }
            float Edge { get { return Full ? 0 : 6; } }
            // Room above the bar: magnified icons, their names, a bounce
            float Lift { get { return IconRest * (Mag - 1) + Hop + 40; } }
            float Radius { get { return Full ? 0 : Math.Min(BarH / 2, 18f); } }

            readonly List<Slot> slots = new List<Slot>();
            readonly Dictionary<string, Spring> sysGrow = new Dictionary<string, Spring>();
            // Widget presence: 0 gone, 1 full width
            readonly Dictionary<string, Spring> widgetShow = new Dictionary<string, Spring>();
            bool widgetsPrimed;
            float leftPresence = 1;
            // The capsule, and the widget and tray spans inside it
            RectangleF leftPill, centerPill, rightPill;
            float sepA = float.NaN, sepB = float.NaN;
            int widgetCap = int.MaxValue;
            float barTop, barBottom, fit = 1, iconSize = 42;
            // Pointer in canvas DIPs while over the dock
            float mouseX = -10000, mouseY = -10000;
            bool inside;

            Point WindowOrigin() { return new Point(mon.Left - (Dry ? 40000 : 0), area.Bottom - ch); }

            void Place()
            {
                if (closing) return;
                int wantW = Math.Max(64, mon.Width) & ~1;
                int wantH = (int)Math.Ceiling((Lift + BarH + Edge) * scale);
                var at = WindowOrigin();
                if (wantW != cw || wantH != ch) MakeCanvas(wantW, wantH);
                at = WindowOrigin();
                // Glass reads the strip under the bar
                if (frost != null && Glass)
                {
                    int top = area.Bottom - (int)Math.Ceiling((BarH + Edge) * scale);
                    frost.Request(new Rectangle(at.X, top, cw, area.Bottom - top), Ink.Base.Bg, Ink.Light, isle.LiquidOn);
                }
                if (tray != null) tray.SetArea(TrayArea());
            }

            void MakeCanvas(int w, int h)
            {
                FreeCanvas();
                cw = w;
                ch = h;
                var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                memDC = Native.CreateCompatibleDC(screen);
                Native.ReleaseDC(IntPtr.Zero, screen);
                IntPtr bits;
                dib = Native.CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                oldBmp = Native.SelectObject(memDC, dib);
                canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                g = Graphics.FromImage(canvas);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                p.G = g;
            }

            void FreeCanvas()
            {
                if (g == null) return;
                g.Dispose();
                canvas.Dispose();
                Native.SelectObject(memDC, oldBmp);
                Native.DeleteObject(dib);
                Native.DeleteDC(memDC);
                g = null;
            }

            // Screen spot of the tray corner, for apps placing flyouts
            Rectangle TrayArea()
            {
                var o = WindowOrigin();
                var r = rightPill.IsEmpty ? new RectangleF(cw / scale - 200, barTop, 200, BarH) : rightPill;
                return new Rectangle(o.X + (int)(r.X * scale), o.Y + (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale));
            }

            Rectangle ToScreen(RectangleF r)
            {
                var o = WindowOrigin();
                return Rectangle.FromLTRB(o.X + (int)Math.Floor(r.Left * scale), o.Y + (int)Math.Floor(r.Top * scale), o.X + (int)Math.Ceiling(r.Right * scale), o.Y + (int)Math.Ceiling(r.Bottom * scale));
            }

            Rectangle IconScreenRect(DockApp app)
            {
                foreach (var s in slots) if (s.App == app) return ToScreen(s.Icon);
                return Rectangle.Empty;
            }

            Spring SysSpring(string id)
            {
                Spring s;
                if (!sysGrow.TryGetValue(id, out s))
                {
                    s = new Spring(1, 1, 1, 0.0005f);
                    Tune(s, 900, 0.82f);
                    sysGrow[id] = s;
                }
                return s;
            }

            // Centre content: Start and search when replacing, then the apps
            void CenterSlots()
            {
                slots.Clear();
                if (Replacing)
                {
                    slots.Add(new Slot { Id = "start" });
                    if (S.GetBool("dockbar.search", true)) slots.Add(new Slot { Id = "search" });
                    if (S.GetBool("dockbar.taskview", false)) slots.Add(new Slot { Id = "taskview" });
                    if (apps.Count > 0) slots.Add(new Slot { Id = "sep", Sep = true });
                }
                foreach (var a in apps) slots.Add(new Slot { Id = "app:" + a.Key, App = a, Show = Ease.Clamp01(a.Show.Value) });
                // The dragged app leaves a gap that follows the pointer
                if (dragApp != null) InsertDragGap();
            }

            float SlotW(Slot s) { return s.Sep ? 13 * fit : iconSize; }

            // Where every piece of the dock sits this frame: one capsule, centred,
            // widgets | Start and apps | tray and clock
            void Compose()
            {
                float dipW = cw / scale, dipH = ch / scale;
                float drop = (1 - reveal.Value) * (BarH + Edge + 8);
                barBottom = dipH - Edge + drop;
                barTop = barBottom - BarH;
                CenterSlots();
                widgetCap = int.MaxValue;
                fit = 1;
                iconSize = IconRest;
                float restW = RestWidth(), wl = LeftWidth(), wr = RightWidth();
                // Too wide for the screen: icons shrink first, then widgets drop from the end
                float room = dipW - Side * 2 - PadX * 2;
                float sides = wl + wr + SegGap * 2;
                if (sides + restW > room && restW > 1)
                {
                    fit = Math.Max(0.6f, (room - sides) / restW);
                    iconSize = IconRest * fit;
                    restW = RestWidth();
                }
                while (sides + restW > room && wl > 0)
                {
                    widgetCap = LeftWidgets().Count - 1;
                    wl = LeftWidth();
                    sides = wl + wr + SegGap * 2;
                }
                // Fit by final widths, layout by springs
                AimWidgets();
                wl = LeftShown();
                bool hasL = wl > 0.5f, hasC = slots.Count > 0, hasR = wr > 0;
                float gapL = hasL && (hasC || hasR) ? SegGap * leftPresence : 0, gapR = hasR && hasC ? SegGap : 0;
                float leftBlock = hasL ? wl + gapL : 0, rightBlock = hasR ? wr + gapR : 0;
                float gap = GapX * fit;
                float originRest = dipW / 2 - (leftBlock + restW + rightBlock) / 2 + leftBlock;
                // Magnification targets from rest positions: no feedback loop
                float sigma = iconSize * 1.35f, mag = Mag;
                bool wave = inside && mouseY >= barTop - iconSize * (mag - 1) - 8 && dragApp == null;
                float x = originRest;
                foreach (var s in slots)
                {
                    float w = SlotW(s) * s.Show;
                    if (!s.Sep)
                    {
                        float d = mouseX - (x + w / 2);
                        float target = wave ? 1 + (mag - 1) * (float)Math.Exp(-(d * d) / (2 * sigma * sigma)) : 1;
                        var spring = s.App != null ? s.App.Grow : s.Id == "gap" ? null : SysSpring(s.Id);
                        if (spring != null) { spring.Target = target; s.Grow = Math.Max(1, spring.Value); }
                    }
                    x += w + gap * s.Show;
                }
                // Grown spans; the slot under the pointer stays under it
                float grownW = 0;
                foreach (var s in slots) grownW += (SlotW(s) * s.Grow + gap) * s.Show;
                grownW -= slots.Count > 0 ? gap * slots[slots.Count - 1].Show : 0;
                float originGrown = originRest;
                float mouseRest = mouseX - originRest;
                if (mouseRest >= restW) originGrown = originRest + restW - grownW;
                else if (mouseRest > 0)
                {
                    float rx = 0, gx = 0;
                    foreach (var s in slots)
                    {
                        float rs = (SlotW(s) + gap) * s.Show, gs = (SlotW(s) * s.Grow + gap) * s.Show;
                        if (mouseRest < rx + rs || s == slots[slots.Count - 1])
                        {
                            float f = rs > 0 ? (mouseRest - rx) / rs : 0;
                            originGrown = mouseX - (gx + f * gs);
                            break;
                        }
                        rx += rs;
                        gx += gs;
                    }
                }
                x = originGrown;
                float floor = barBottom - PadBottom;
                foreach (var s in slots)
                {
                    float w = SlotW(s) * s.Grow * s.Show;
                    float lift = s.App != null ? HopLift(s.App) : 0;
                    s.Icon = new RectangleF(x, floor - w - lift, w, w);
                    if (s.Sep) s.Icon = new RectangleF(x, barTop + 12, w, BarH - 24);
                    s.Hit = new RectangleF(x - gap / 2, Math.Min(s.Icon.Y, barTop), w + gap, barBottom - Math.Min(s.Icon.Y, barTop));
                    x += (SlotW(s) * s.Grow + gap) * s.Show;
                }
                // Widgets and the tray ride on either side of the apps
                float left = originGrown - leftBlock, right = originGrown + grownW + rightBlock;
                leftPill = hasL ? new RectangleF(left, barTop, wl, BarH) : RectangleF.Empty;
                rightPill = hasR ? new RectangleF(right - wr, barTop, wr, BarH) : RectangleF.Empty;
                sepA = hasL && gapL > 0 ? left + wl + gapL / 2 : float.NaN;
                sepB = hasR && gapR > 0 ? right - wr - gapR / 2 : float.NaN;
                centerPill = right > left ? new RectangleF(left - PadX, barTop, right - left + PadX * 2, BarH) : RectangleF.Empty;
            }

            float RestWidth()
            {
                float w = 0, gap = GapX * fit;
                foreach (var s in slots) w += (SlotW(s) + gap) * s.Show;
                if (slots.Count > 0) w -= gap * slots[slots.Count - 1].Show;
                return w;
            }

            // One small hop when launching; a few when an app calls for you
            static float HopLift(DockApp a)
            {
                if (a.Hop < 0) return 0;
                float t = a.Hop / HopSecs;
                float height = a.Attention ? 0.8f : 0.6f;
                return Hop * height * (float)Math.Sin(Math.PI * Ease.Clamp01(t));
            }

            // Springs and clocks; true while anything still moves
            bool Step(float dt)
            {
                bool moving = false;
                reveal.Step(dt);
                hover.Step(dt);
                moving |= !reveal.Settled || !hover.Settled;
                foreach (var s in sysGrow.Values) { s.Step(dt); moving |= !s.Settled; }
                foreach (var s in widgetShow.Values) { s.Step(dt); moving |= !s.Settled; }
                for (int i = apps.Count - 1; i >= 0; i--)
                {
                    var a = apps[i];
                    a.Grow.Step(dt);
                    if (a.Delay > 0) { a.Delay -= dt; moving = true; }
                    else a.Show.Step(dt);
                    a.Dot.Target = DotTarget(a);
                    a.Dot.Step(dt);
                    moving |= !a.Grow.Settled || !a.Show.Settled || !a.Dot.Settled;
                    if (a.Hop >= 0)
                    {
                        a.Hop += dt;
                        moving = true;
                        if (a.Hop >= HopSecs)
                        {
                            a.Hops--;
                            a.Hop = a.Hops > 0 ? 0 : -1;
                            if (a.Hop < 0) a.LaunchUtc = DateTime.MinValue;
                        }
                    }
                    if (a.Leaving && a.Show.Value < 0.02f && a.Show.Target == 0) { apps.RemoveAt(i); moving = true; }
                }
                if (StepDrag(dt)) moving = true;
                if (StepLabel(dt)) moving = true;
                return moving;
            }

            float DotTarget(DockApp a)
            {
                if (!a.Running) return 0;
                IntPtr fg = GetForegroundWindow();
                return a.Wins.Exists(w => w.H == fg) ? 2 : 1;
            }

            // Spans on either side of the apps
            float LeftWidth()
            {
                var ids = LeftWidgets();
                if (ids.Count == 0) return 0;
                float w = -6;
                foreach (var id in ids) w += WidgetW(id) + 6;
                return w;
            }

            Spring WidgetSpring(string id)
            {
                Spring s;
                if (!widgetShow.TryGetValue(id, out s))
                {
                    s = new Spring(0, 1, 1, 0.002f);
                    Tune(s, 240, 1f);
                    widgetShow[id] = s;
                }
                return s;
            }

            float WidgetShown(string id) { return Ease.Clamp01(WidgetSpring(id).Value); }

            // First layout snaps; later changes animate
            void AimWidgets()
            {
                var want = LeftWidgets();
                foreach (var id in DockWidgetIds)
                {
                    var s = WidgetSpring(id);
                    s.Target = want.Contains(id) ? 1 : 0;
                    if (!widgetsPrimed || Anim.Reduced) s.Snap(s.Target);
                }
                widgetsPrimed = true;
                if (WidgetsMoving()) Animate();
            }

            bool WidgetsMoving()
            {
                foreach (var s in widgetShow.Values) if (!s.Settled) return true;
                return false;
            }

            float LeftShown()
            {
                float w = 0, sum = 0;
                foreach (var id in DockWidgetIds)
                {
                    float s = WidgetShown(id);
                    w += (WidgetW(id) + 6) * s;
                    sum += s;
                }
                leftPresence = Math.Min(1, sum);
                return Math.Max(0, w - 6 * leftPresence);
            }

            float RightWidth()
            {
                if (!Replacing) return 0;
                float w = 0;
                foreach (var ic in InlineTray()) w += 26;
                if (OverflowTray().Count > 0) w += 24;
                w += StatusWidth() + 4 + ClockWidth() + 10;
                return w;
            }

            float StatusWidth() { return isle.hasBattery ? 84 : 56; }

            string clockFor = "";
            float clockW;

            float ClockWidth()
            {
                var now = DateTime.Now;
                string key = Fmt.Clock(now) + now.Date.Ticks;
                if (key == clockFor) return clockW;
                clockFor = key;
                float a = p.Measure(Fmt.Clock(now), Ink.Semi(12.5f)), b = p.Measure(now.ToString("d MMM yyyy", Fmt.Id), Ink.Small(11f));
                return clockW = Math.Max(a, b) + 18;
            }

            List<string> LeftWidgets()
            {
                var list = new List<string>();
                if (!Replacing && isle.DockWidget("clock")) list.Add("clock");
                if (isle.DockWidget("media") && isle.HomeHasMedia) list.Add("media");
                if (isle.DockWidget("ai") && (isle.QuotaCount(false) > 0 || isle.BusyCount() > 0)) list.Add("ai");
                if (isle.DockWidget("timer") && isle.TimerOn) list.Add("timer");
                if (isle.DockWidget("weather") && isle.WeatherOn && isle.weather != null) list.Add("weather");
                if (!Replacing && isle.DockWidget("battery") && isle.hasBattery) list.Add("battery");
                if (isle.DockWidget("system") && cpuPct >= 0) list.Add("system");
                if (list.Count > widgetCap) list.RemoveRange(widgetCap, list.Count - widgetCap);
                return list;
            }

            static float WidgetW(string id)
            {
                switch (id)
                {
                    case "clock": return 86;
                    case "media": return 196;
                    case "ai": return 108;
                    case "timer": return 98;
                    case "weather": return 92;
                    case "system": return 106;
                    case "battery": return 112;
                }
                return 80;
            }

            // Windows' own speaker, network and power icons: the status cluster shows those
            static readonly Guid[] SystemIcons = { new Guid("7820ae73-23e3-4229-82c1-e41cb67d5b9c"), new Guid("7820ae74-23e3-4229-82c1-e41cb67d5b9c"), new Guid("7820ae75-23e3-4229-82c1-e41cb67d5b9c") };

            static bool Listed(TrayIcon ic) { return !ic.Hidden && ic.Hicon != IntPtr.Zero && Array.IndexOf(SystemIcons, ic.Guid) < 0; }

            // Tray icons Windows shows on the taskbar; the rest wait behind the arrow
            List<TrayIcon> InlineTray()
            {
                var list = new List<TrayIcon>();
                foreach (var ic in trayIcons) if (Listed(ic) && TrayPromo.Promoted(ic)) list.Add(ic);
                return list;
            }

            List<TrayIcon> OverflowTray()
            {
                var list = new List<TrayIcon>();
                foreach (var ic in trayIcons) if (Listed(ic) && !TrayPromo.Promoted(ic)) list.Add(ic);
                return list;
            }
        }
    }
}
