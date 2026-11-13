using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Opaque window colours from island tokens
    static class KitInk
    {
        // Last input was the mouse: no rings
        public static bool ByMouse;

        static Color O(Color c) { return Color.FromArgb(255, c.R, c.G, c.B); }
        public static Color Window { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, Ink.Light ? 0.0f : 0.04f)); } }
        public static Color Side { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, Ink.Light ? 0.05f : 0.07f)); } }
        public static Color Group { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, Ink.Light ? 0.035f : 0.09f)); } }
        public static Color Line { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, Ink.Light ? 0.12f : 0.15f)); } }
        public static Color Hover { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, Ink.Light ? 0.09f : 0.16f)); } }
        // Selected segment, lifted above its track
        public static Color Raised { get { return Ink.Light ? Color.White : O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.3f)); } }
        public static Color Text { get { return O(Ink.Base.Text); } }
        public static Color Sub { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.62f)); } }
        public static Color Faint { get { return O(Ink.Mix(Ink.Base.Bg, Ink.Base.Text, 0.38f)); } }
        // Monochrome accent: switches use text colour
        public static Color Accent { get { return O(Ink.Base.Accent); } }
        public static Color OnAccent { get { return O(Ink.Base.OnAccent); } }

        // First opaque colour behind a control
        public static Color Under(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                if (p is KitGroup) return Group;
                if (p.BackColor.A == 255) return p.BackColor;
            }
            return Window;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        static StringFormat glyphFormat;

        public static void Draw(Graphics g, string s, Font f, Color c, RectangleF r, ContentAlignment align)
        {
            // Lone icon glyph: centre its ink, not its line box
            if (s != null && s.Length == 1 && align == ContentAlignment.MiddleCenter && f.Name == Ink.IconFamily)
            {
                if (glyphFormat == null)
                {
                    glyphFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
                    glyphFormat.FormatFlags |= StringFormatFlags.NoWrap;
                    glyphFormat.Alignment = StringAlignment.Center;
                    glyphFormat.LineAlignment = StringAlignment.Center;
                }
                var n = Ink.IconNudge(s[0], f.Size);
                float half = g.PixelOffsetMode == PixelOffsetMode.HighQuality || g.PixelOffsetMode == PixelOffsetMode.Half ? 0.5f : 0;
                float cx = r.X + r.Width / 2 + n.X + half, cy = r.Y + r.Height / 2 + n.Y + half;
                using (var b = new SolidBrush(c)) g.DrawString(s, f, b, new RectangleF(cx - f.Size, cy - f.Size, f.Size * 2, f.Size * 2), glyphFormat);
                return;
            }
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            if (align == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            else if (align == ContentAlignment.MiddleRight) flags |= TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
            else flags |= TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, s, f, Rectangle.Round(r), c, flags);
        }
    }

    // Shared tick for Kit controls; stops when idle
    static class KitMotion
    {
        static readonly Timer timer = new Timer { Interval = 15 };
        static readonly List<Func<float, bool>> live = new List<Func<float, bool>>();
        static DateTime last;

        static KitMotion() { timer.Tick += Tick; }

        public static void Run(Func<float, bool> step)
        {
            if (!live.Contains(step)) live.Add(step);
            if (timer.Enabled) return;
            last = DateTime.UtcNow;
            timer.Start();
        }

        static void Tick(object sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            float dt = Math.Min(0.05f, (float)(now - last).TotalSeconds);
            last = now;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                bool more;
                try { more = live[i](dt); } catch (ObjectDisposedException) { more = false; }
                if (!more) live.RemoveAt(i);
            }
            if (live.Count == 0) timer.Stop();
        }
    }

    // Time-based approach; snaps under reduced motion
    class Glide
    {
        public float Value, Target;
        readonly float tau;

        public Glide(float tau) { this.tau = tau; }

        public bool Step(float dt)
        {
            if (Anim.Reduced) { Value = Target; return false; }
            Value += (Target - Value) * (1 - (float)Math.Exp(-dt / (tau * Anim.Pace)));
            if (Math.Abs(Target - Value) < 0.004f) { Value = Target; return false; }
            return true;
        }

        public void Snap(float v) { Value = Target = v; }
    }

    // Base: own HWND, focusable, window DPI
    // TextBox with Ctrl+Backspace and Ctrl+Delete
    class KitText : TextBox
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && !e.Alt && (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete) && !ReadOnly)
            {
                DeleteWord(e.KeyCode == Keys.Back);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        static int Kind(char c) { return char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2; }

        void DeleteWord(bool back)
        {
            if (SelectionLength == 0)
            {
                string x = Text;
                int at = SelectionStart, i = at;
                if (back)
                {
                    while (i > 0 && Kind(x[i - 1]) == 0) i--;
                    if (i > 0) { int k = Kind(x[i - 1]); while (i > 0 && Kind(x[i - 1]) == k) i--; }
                    Select(i, at - i);
                }
                else
                {
                    while (i < x.Length && Kind(x[i]) == 0) i++;
                    if (i < x.Length) { int k = Kind(x[i]); while (i < x.Length && Kind(x[i]) == k) i++; }
                    Select(at, i - at);
                }
            }
            // Keeps undo working
            SelectedText = "";
        }
    }

    abstract class KitControl : Control
    {
        protected KitControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = true;
            BackColor = Color.Transparent;
        }

        public float Ui { get { var w = FindForm() as KitWindow; return w != null ? w.Ui : 1f; } }
        protected bool Hovered;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { KitInk.ByMouse = true; base.OnMouseDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (KitInk.ByMouse) { KitInk.ByMouse = false; Invalidate(); } base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // Focus ring only for keyboard use
        protected void Ring(Graphics g, RectangleF r, float radius)
        {
            if (!Focused || !ShowFocusCues || KitInk.ByMouse) return;
            var ring = Inside(RectangleF.Inflate(r, 2, 2), Width, Height);
            using (var path = Ink.Round(ring, Math.Min(radius + 2, ring.Height / 2)))
            using (var pen = new Pen(KitInk.Text, 2f)) g.DrawPath(pen, path);
        }

        // Rings stay whole inside the control
        public static RectangleF Inside(RectangleF r, int w, int h)
        {
            float x0 = Math.Max(r.Left, 1f), y0 = Math.Max(r.Top, 1f), x1 = Math.Min(r.Right, w - 1.5f), y1 = Math.Min(r.Bottom, h - 1.5f);
            return RectangleF.FromLTRB(x0, y0, Math.Max(x0 + 1, x1), Math.Max(y0 + 1, y1));
        }

        protected void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            using (var path = Ink.Round(r, radius))
            using (var b = new SolidBrush(c)) g.FillPath(b, path);
        }
    }

    // iOS switch over CheckBox for readers
    class KitSwitch : CheckBox
    {
        protected override void OnMouseDown(MouseEventArgs e)
        {
            KitInk.ByMouse = true;
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { press.Target = 1; Go(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.Target = 0; Go(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.Target = 1; Go(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.Target = 0; press.Target = 0; Go(); }
        protected override void OnKeyUp(KeyEventArgs e) { if (KitInk.ByMouse) { KitInk.ByMouse = false; Invalidate(); } base.OnKeyUp(e); }

        // Knob springs; press stretches it
        readonly Spring knob = new Spring(0, 420, 31, 0.002f);
        readonly Glide press = new Glide(0.05f), hover = new Glide(0.08f), ena = new Glide(0.09f);
        readonly Func<float, bool> step;

        public KitSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Text = "";
            step = Step;
            ena.Snap(1);
            EnabledChanged += delegate { ena.Target = Enabled ? 1 : 0; if (IsHandleCreated) Go(); else ena.Snap(ena.Target); };
            CheckedChanged += delegate
            {
                knob.K = 420 / (Anim.Pace * Anim.Pace);
                knob.D = Anim.Damping(0.75f) * 2 * (float)Math.Sqrt(knob.K);
                knob.Target = Checked ? 1 : 0;
                if (!IsHandleCreated) knob.Snap(knob.Target);
                Go();
            };
        }

        void Go()
        {
            if (Anim.Reduced) { knob.Snap(knob.Target); press.Snap(press.Target); hover.Snap(hover.Target); ena.Snap(ena.Target); Invalidate(); return; }
            KitMotion.Run(step);
        }

        bool Step(float dt)
        {
            if (IsDisposed) return false;
            knob.Step(dt);
            bool a = press.Step(dt), b = hover.Step(dt), c = ena.Step(dt);
            Invalidate();
            return a || b || c || !knob.Settled;
        }

        protected override void OnCreateControl() { base.OnCreateControl(); knob.Snap(Checked ? 1 : 0); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            float s = Height / 24f, k = Math.Max(-0.05f, Math.Min(1.05f, knob.Value)), kc = Ease.Clamp01(k);
            var track = new RectangleF(1, (Height - 22 * s) / 2, 40 * s, 22 * s);
            Color off = Ink.Mix(KitInk.Line, KitInk.Faint, 0.3f * hover.Value), on = Ink.Mix(KitInk.Accent, KitInk.Window, 0.12f * hover.Value);
            using (var path = Ink.Round(track, 11 * s))
            using (var b = new SolidBrush(Ink.Mix(KitInk.Line, Ink.Mix(off, on, kc), ena.Value))) g.FillPath(b, path);
            float r = 9 * s, stretch = 5 * s * press.Value;
            float cx = track.X + 11 * s + k * 18 * s, cy = track.Y + 11 * s;
            var pill = RectangleF.FromLTRB(cx - r - stretch * kc, cy - r, cx + r + stretch * (1 - kc), cy + r);
            using (var path = Ink.Round(pill, r))
            using (var b = new SolidBrush(Ink.Mix(KitInk.Faint, Ink.Mix(KitInk.Text, KitInk.OnAccent, kc), ena.Value))) g.FillPath(b, path);
            if (Focused && ShowFocusCues && !KitInk.ByMouse)
            {
                var ring = KitControl.Inside(RectangleF.Inflate(track, 2, 2), Width, Height);
                using (var path = Ink.Round(ring, ring.Height / 2))
                using (var pen = new Pen(KitInk.Text, 1.6f)) g.DrawPath(pen, path);
            }
        }

    }

    // Pill choices; arrows move between
    class KitSegmented : KitControl
    {
        public string[] Items = new string[0];
        int selected, hot = -1;
        public event Action<int> Changed;

        // Pill slides between segments
        readonly Spring pill = new Spring(0, 520, 40, 0.002f);
        readonly Glide hotFade = new Glide(0.07f);
        readonly Func<float, bool> step;

        public KitSegmented() { step = Step; }

        public int Selected
        {
            get { return selected; }
            set
            {
                if (value == selected) return;
                selected = value;
                pill.K = 520 / (Anim.Pace * Anim.Pace);
                pill.D = Anim.Damping(0.82f) * 2 * (float)Math.Sqrt(pill.K);
                pill.Target = value;
                if (!IsHandleCreated || Anim.Reduced) pill.Snap(value);
                else KitMotion.Run(step);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, selected);
            }
        }

        bool Step(float dt)
        {
            if (IsDisposed) return false;
            pill.Step(dt);
            bool more = hotFade.Step(dt);
            Invalidate();
            return more || !pill.Settled;
        }

        void Hot(int i)
        {
            if (i == hot && hotFade.Target == (i >= 0 ? 1 : 0)) return;
            if (i >= 0) { if (i != hot) hotFade.Value = 0; hot = i; }
            hotFade.Target = i >= 0 ? 1 : 0;
            if (Anim.Reduced) { hotFade.Snap(hotFade.Target); Invalidate(); }
            else KitMotion.Run(step);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int at = -1;
            for (int i = 0; i < Items.Length; i++) if (Cell(i).Contains(e.Location)) at = i;
            Hot(at);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hot(-1); }

        RectangleF Cell(int i)
        {
            // Active pill fills its segment fully
            float w = (Width - 1f) / Math.Max(1, Items.Length);
            return new RectangleF(i * w, 0, w, Height - 1);
        }

        void Pick(int i)
        {
            if (i < 0 || i >= Items.Length || i == selected) return;
            Selected = i;
            var h = Changed;
            if (h != null) h(i);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            for (int i = 0; i < Items.Length; i++) if (Cell(i).Contains(e.Location)) { Pick(i); return; }
        }

        protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Pick(selected - 1);
            else if (e.KeyCode == Keys.Right) Pick(selected + 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            var box = new RectangleF(0, 0, Width - 1, Height - 1);
            Fill(g, box, Height / 2f, KitInk.Line);
            var f = new Font(Ink.MenuFamily, 12.5f * Ui, FontStyle.Regular, GraphicsUnit.Pixel);
            float w = (Width - 1f) / Math.Max(1, Items.Length);
            if (hot >= 0 && hot < Items.Length && hot != selected && hotFade.Value > 0)
            {
                var hc = RectangleF.Inflate(Cell(hot), -2, -2);
                Fill(g, hc, hc.Height / 2, Ink.Mix(KitInk.Line, KitInk.Hover, hotFade.Value));
            }
            if (Items.Length > 0)
            {
                float at = Math.Max(-0.1f, Math.Min(Items.Length - 0.9f, pill.Value));
                var pr = new RectangleF(at * w, 0, w, Height - 1);
                Fill(g, pr, pr.Height / 2, KitInk.Raised);
            }
            for (int i = 0; i < Items.Length; i++)
            {
                var c = Cell(i);
                float near = Ease.Clamp01(1 - Math.Abs(pill.Value - i));
                KitInk.Draw(g, Items[i], f, Ink.Mix(KitInk.Sub, KitInk.Text, near), c, ContentAlignment.MiddleCenter);
            }
            f.Dispose();
            Ring(g, box, Height / 2f);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

        class Access : ControlAccessibleObject
        {
            readonly KitSegmented owner;
            public Access(KitSegmented owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.PageTabList; } }
            public override int GetChildCount() { return owner.Items.Length; }
            public override AccessibleObject GetChild(int i) { return new Item(owner, i); }
            public override AccessibleObject GetSelected() { return GetChild(owner.selected); }
            public override AccessibleObject GetFocused() { return owner.Focused ? GetChild(owner.selected) : null; }
        }

        class Item : AccessibleObject
        {
            readonly KitSegmented owner;
            readonly int index;
            public Item(KitSegmented owner, int index) { this.owner = owner; this.index = index; }
            public override string Name { get { return owner.Items[index]; } }
            public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
            public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
            public override AccessibleStates State
            {
                get { return AccessibleStates.Selectable | AccessibleStates.Focusable | (index == owner.selected ? AccessibleStates.Selected | AccessibleStates.Checked : 0); }
            }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Round(owner.Cell(index))); } }
            public override string DefaultAction { get { return Lang.T("Pilih"); } }
            public override void DoDefaultAction() { owner.Pick(index); }
        }
    }

    // Slider: arrows, Home/End, drag
    class KitSlider : KitControl
    {
        public int Minimum, Maximum = 100, Step = 1, Value;
        public Func<int, string> Format = v => v.ToString();
        // Value text left of the track
        public bool ShowValue;
        public event Action<int> Changed;
        bool dragging;

        // Shown fill and knob size glide
        readonly Glide shown = new Glide(0.045f), grow = new Glide(0.07f), ena = new Glide(0.09f);
        Func<float, bool> step;
        bool seeded;

        bool Tick(float dt)
        {
            if (IsDisposed) return false;
            bool a = shown.Step(dt), b = grow.Step(dt), c = ena.Step(dt);
            Invalidate();
            return a || b || c;
        }

        void Go()
        {
            if (step == null) step = Tick;
            grow.Target = dragging ? 1 : Hovered ? 0.5f : 0;
            shown.Target = Frac;
            ena.Target = Enabled ? 1 : 0;
            if (!seeded || Anim.Reduced) { seeded = true; shown.Snap(shown.Target); grow.Snap(grow.Target); ena.Snap(ena.Target); Invalidate(); return; }
            KitMotion.Run(step);
        }

        float Frac { get { return (float)(Value - Minimum) / Math.Max(1, Maximum - Minimum); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Go(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Go(); }

        void Set(int v)
        {
            v = Math.Max(Minimum, Math.Min(Maximum, (int)Math.Round((double)(v - Minimum) / Step) * Step + Minimum));
            if (v == Value) return;
            Value = v;
            Go();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            var h = Changed;
            if (h != null) h(v);
        }

        float LabelW { get { return ShowValue ? 84 * Ui : 0; } }

        RectangleF Track { get { float s = Ui, l = LabelW; return new RectangleF(l + 10 * s, Height / 2f - 3 * s, Width - l - 20 * s, 6 * s); } }

        int At(int x) { var t = Track; return Minimum + (int)Math.Round((Maximum - Minimum) * Ease.Clamp01((x - t.X) / Math.Max(1, t.Width))); }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); dragging = true; Capture = true; Go(); Set(At(e.X)); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Set(At(e.X)); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Go(); }

        protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || base.IsInputKey(key); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Set(Value - Step);
            else if (e.KeyCode == Keys.Right) Set(Value + Step);
            else if (e.KeyCode == Keys.Home) Set(Minimum);
            else if (e.KeyCode == Keys.End) Set(Maximum);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            if (!seeded) { seeded = true; shown.Snap(Frac); ena.Snap(Enabled ? 1 : 0); }
            else if (shown.Target != Frac || ena.Target != (Enabled ? 1 : 0)) Go();
            float s = Ui, f = shown.Value;
            var t = Track;
            Fill(g, t, t.Height / 2, KitInk.Line);
            Fill(g, new RectangleF(t.X, t.Y, Math.Max(t.Height, t.Width * f), t.Height), t.Height / 2, Ink.Mix(KitInk.Faint, KitInk.Text, ena.Value));
            float r = (8 + 2 * grow.Value) * s, cx = t.X + t.Width * f, cy = t.Y + t.Height / 2;
            using (var b = new SolidBrush(KitInk.Text)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(KitInk.Line, 1f)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            if (ShowValue)
                using (var f2 = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, Format(Value), f2, new Rectangle(0, 0, (int)LabelW, Height), Ink.Mix(KitInk.Faint, KitInk.Sub, ena.Value),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            Ring(g, new RectangleF(1, 1, Width - 3, Height - 3), Height / 2f);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

        class Access : ControlAccessibleObject
        {
            readonly KitSlider owner;
            public Access(KitSlider owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.Slider; } }
            public override string Value { get { return owner.Format(owner.Value); } set { int v; if (int.TryParse(value, out v)) owner.Set(v); } }
        }
    }

    // Pill-drawn real Button for focus, readers
    class KitButton : Button
    {
        public bool Primary;
        // Hover fades in, press sinks
        readonly Glide hover = new Glide(0.07f), press = new Glide(0.04f), ena = new Glide(0.09f);
        readonly Func<float, bool> step;

        public KitButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
            step = Step;
            ena.Snap(1);
            EnabledChanged += delegate { ena.Target = Enabled ? 1 : 0; if (IsHandleCreated) Go(); else ena.Snap(ena.Target); };
        }

        bool Step(float dt)
        {
            if (IsDisposed) return false;
            bool a = hover.Step(dt), b = press.Step(dt), c = ena.Step(dt);
            Invalidate();
            return a || b || c;
        }

        void Go()
        {
            if (Anim.Reduced) { hover.Snap(hover.Target); press.Snap(press.Target); ena.Snap(ena.Target); Invalidate(); return; }
            KitMotion.Run(step);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.Target = 1; Go(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            KitInk.ByMouse = true;
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { press.Target = 1; Go(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.Target = 0; Go(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { press.Target = 1; Go(); } }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (KitInk.ByMouse) { KitInk.ByMouse = false; Invalidate(); }
            if (e.KeyCode == Keys.Space) { press.Target = 0; Go(); }
            base.OnKeyUp(e);
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.Target = 0; press.Target = 0; Go(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            bool ringOn = Focused && ShowFocusCues && !KitInk.ByMouse;
            // Focused pill shrinks so its ring fits
            var r = ringOn ? new RectangleF(3.5f, 3.5f, Width - 7, Height - 7) : new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float sink = 1.5f * press.Value;
            r = RectangleF.Inflate(r, -sink * 1.6f, -sink);
            float h = hover.Value, p = press.Value;
            Color bg = Primary ? Ink.Mix(KitInk.Text, KitInk.Window, 0.15f * h + 0.12f * p) : Ink.Mix(Ink.Mix(KitInk.Line, KitInk.Hover, h), KitInk.Faint, 0.25f * p);
            bg = Ink.Mix(KitInk.Line, bg, ena.Value);
            using (var path = Ink.Round(r, r.Height / 2f))
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            KitInk.Draw(g, Text, Font, Ink.Mix(KitInk.Faint, Primary ? KitInk.Window : KitInk.Text, ena.Value), r, ContentAlignment.MiddleCenter);
            if (ringOn)
            {
                var ring = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
                using (var path = Ink.Round(ring, ring.Height / 2f))
                using (var pen = new Pen(KitInk.Text, 1.6f)) g.DrawPath(pen, path);
            }
        }
    }

    // List choice: button opening styled menu
    class KitPopup : KitButton
    {
        public string[] Items = new string[0];
        int selected;
        public event Action<int> Changed;

        public int Selected { get { return selected; } set { selected = value; Text = (value >= 0 && value < Items.Length ? Items[value] : "") + "  ▾"; } }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var m = new ContextMenuStrip();
            for (int i = 0; i < Items.Length; i++)
            {
                int at = i;
                m.Items.Add(new ToolStripMenuItem(Items[i], null, delegate
                {
                    Selected = at;
                    var h = Changed;
                    if (h != null) h(at);
                }) { Checked = i == selected });
            }
            MenuLook.Apply(m);
            m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
            m.Show(this, new Point(0, Height));
        }
    }

    // Rounded group of rows with hairline separators
    class KitGroup : Panel
    {
        public KitGroup()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = KitInk.Group;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(Parent != null ? Parent.BackColor : KitInk.Window);
            var w = FindForm() as KitWindow;
            float s = w != null ? w.Ui : 1;
            using (var path = Ink.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12 * s))
            using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
            using (var pen = new Pen(KitInk.Line, 1f))
                for (int i = 1; i < Controls.Count; i++)
                {
                    int y = Controls[i].Top;
                    var row = Controls[i] as KitRow;
                    float from = row != null && row.Glyph != '\0' ? (56 + row.Lead) * s : 16 * s;
                    g.DrawLine(pen, from, y, Width - 16 * s, y);
                }
        }
    }

    // Setting row: title, detail, control right
    class KitRow : Panel
    {
        public string Title = "", Detail = "";
        // Tiny tail after the title, e.g. ".0.2"
        public string Suffix = "";
        public Control Field;
        // Icon tile before the title
        public char Glyph;
        public const char MarkGlyph = '\u0001';
        // Room left of the icon, e.g. a grip
        public float Lead;
        // Room kept right of the text, e.g. a chevron
        public float Trail;
        public bool Divider;

        public KitRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var w = FindForm() as KitWindow;
            float s = w != null ? w.Ui : 1;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float right = Field != null ? Field.Left - 12 * s : Width - (16 + Trail) * s;
            float x = (Glyph != '\0' ? 56 : 16) * s + Lead * s;
            if (Glyph != '\0') DrawGlyph(g, s);
            // Hairline between grip and icon
            if (Divider)
                using (var pen = new Pen(Ink.Mix(KitInk.Line, KitInk.Sub, 0.45f), Math.Max(1f, s)))
                    g.DrawLine(pen, (Lead - 2) * s, Height * 0.3f, (Lead - 2) * s, Height * 0.7f);
            using (var f = new Font(Ink.MenuFamily, 13.5f * s, GraphicsUnit.Pixel))
            using (var d = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
            {
                bool two = Detail.Length > 0;
                var tr = new RectangleF(x, two ? 7 * s : 0, right - x, two ? 22 * s : Height);
                KitInk.Draw(g, Title, f, Enabled ? KitInk.Text : KitInk.Faint, tr, ContentAlignment.MiddleLeft);
                if (Suffix.Length > 0) DrawSuffix(g, f, tr, s);
                if (two) KitInk.Draw(g, Detail, d, KitInk.Sub, new RectangleF(x, 28 * s, right - x, 20 * s), ContentAlignment.MiddleLeft);
            }
        }

        void DrawGlyph(Graphics g, float s)
        {
            KitInk.Smooth(g);
            // Tall rows keep the tile beside the title
            var tile = new RectangleF((14 + Lead) * s, Height > 80 * s ? 16 * s : (Height - 30 * s) / 2, 30 * s, 30 * s);
            using (var path = Ink.Round(tile, 8 * s))
            using (var b = new SolidBrush(KitInk.Hover)) g.FillPath(b, path);
            // Mark glyph: Tepi's own pill
            if (Glyph == MarkGlyph)
            {
                var pill = new RectangleF(tile.X + 5 * s, tile.Y + 10 * s, 20 * s, 10 * s);
                using (var p2 = Ink.Round(pill, 5 * s)) g.FillPath(Brushes.Black, p2);
                using (var dot = new SolidBrush(KitInk.Text)) g.FillEllipse(dot, pill.Right - 8 * s, pill.Y + 2.5f * s, 5 * s, 5 * s);
                return;
            }
            using (var icons = new Font(Ink.IconFamily, 15f * s, GraphicsUnit.Pixel))
                KitInk.Draw(g, Glyph.ToString(), icons, Enabled ? KitInk.Text : KitInk.Faint, tile, ContentAlignment.MiddleCenter);
        }

        // Small text on the title's baseline
        void DrawSuffix(Graphics g, Font f, RectangleF tr, float s)
        {
            const TextFormatFlags tight = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            using (var small = new Font(Ink.MenuFamily, 9.5f * s, GraphicsUnit.Pixel))
            {
                int padded = TextRenderer.MeasureText(g, Title, f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                int bare = TextRenderer.MeasureText(g, Title, f, Size.Empty, tight).Width;
                float x = tr.X + (padded - bare) / 2f + bare + 1 * s;
                float baseline = tr.Y + (tr.Height - f.Height) / 2f + Ascent(f);
                TextRenderer.DrawText(g, Suffix, small, new Point((int)Math.Round(x), (int)Math.Round(baseline - Ascent(small))), KitInk.Sub, tight);
            }
        }

        static float Ascent(Font f) { return f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style); }
    }

    // Window: theme colours, dark caption, manual DPI
    class KitWindow : Form
    {
        public float Ui = 1f;

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        public KitWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = KitInk.Window;
            ForeColor = KitInk.Text;
            KeyPreview = true;
            ShowInTaskbar = true;
            if (AppIcon != null) Icon = AppIcon;
        }

        // Tepi's mark on every window
        public static Icon AppIcon;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Ui = GetDpiForWindow(Handle) / 96f; } catch (EntryPointNotFoundException) { }
            Native.DarkFrame(Handle, !Ink.Light);
            Native.RoundCorners(Handle);
        }

        // Soft arrival unless motion is reduced
        protected override void OnLoad(EventArgs e)
        {
            if (!Anim.Reduced) Opacity = 0;
            base.OnLoad(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Opacity >= 1) return;
            var start = DateTime.UtcNow;
            var fade = new Timer { Interval = 15 };
            fade.Tick += delegate
            {
                double k = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 160);
                Opacity = 1 - (1 - k) * (1 - k);
                if (k >= 1) { fade.Stop(); fade.Dispose(); }
            };
            fade.Start();
        }

        // Soft exit for modeless windows
        bool leaving;
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || leaving || Modal || Anim.Reduced || !Visible || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            leaving = true;
            double from = Opacity;
            var start = DateTime.UtcNow;
            var fade = new Timer { Interval = 15 };
            fade.Tick += delegate
            {
                if (IsDisposed) { fade.Stop(); fade.Dispose(); return; }
                double k = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 120);
                Opacity = from * (1 - k * k);
                if (k < 1) return;
                fade.Stop();
                fade.Dispose();
                Close();
            };
            fade.Start();
        }

        protected override bool ProcessDialogKey(Keys key)
        {
            if (key == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED)
            {
                Ui = ((int)m.WParam & 0xFFFF) / 96f;
                var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                Relayout();
                return;
            }
            base.WndProc(ref m);
        }

        // Rebuild on scale, language, theme change
        public virtual void Relayout() { }

        public Font UiFont(float px) { return new Font(Ink.MenuFamily, px * Ui, GraphicsUnit.Pixel); }

        [DllImport("user32.dll")] static extern uint GetDpiForSystem();

        // Scale pixel-laid dialogs to system DPI
        public static void ScaleToDpi(Form f)
        {
            float s = 1f;
            try { s = GetDpiForSystem() / 96f; } catch (EntryPointNotFoundException) { }
            if (s < 1.01f) return;
            f.AutoScaleMode = AutoScaleMode.None;
            f.ClientSize = new Size((int)(f.ClientSize.Width * s), (int)(f.ClientSize.Height * s));
            ScaleTree(f, s);
            if (f.MinimumSize.Width > 0) f.MinimumSize = new Size((int)(f.MinimumSize.Width * s), (int)(f.MinimumSize.Height * s));
        }

        static void ScaleTree(Control parent, float s)
        {
            parent.Font = new Font(parent.Font.FontFamily, parent.Font.Size * s, parent.Font.Style, parent.Font.Unit);
            foreach (Control c in parent.Controls)
            {
                if (!(c.Parent is Form) || c.Dock == DockStyle.None)
                    c.Bounds = new Rectangle((int)(c.Left * s), (int)(c.Top * s), (int)(c.Width * s), (int)(c.Height * s));
                ScaleTree(c, s);
            }
        }
    }
}
