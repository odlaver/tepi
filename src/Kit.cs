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
        protected override void OnMouseDown(MouseEventArgs e) { KitInk.ByMouse = true; base.OnMouseDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (KitInk.ByMouse) { KitInk.ByMouse = false; Invalidate(); } base.OnKeyUp(e); }

        float knob;
        readonly Timer anim = new Timer { Interval = 15 };

        public KitSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Text = "";
            anim.Tick += delegate
            {
                float want = Checked ? 1 : 0;
                knob += (want - knob) * (Anim.Reduced ? 1f : 0.35f);
                if (Math.Abs(want - knob) < 0.01f) { knob = want; anim.Stop(); }
                Invalidate();
            };
            CheckedChanged += delegate { anim.Start(); };
        }

        protected override void OnCreateControl() { base.OnCreateControl(); knob = Checked ? 1 : 0; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            float s = Height / 24f;
            var track = new RectangleF(1, (Height - 22 * s) / 2, 40 * s, 22 * s);
            using (var path = Ink.Round(track, 11 * s))
            using (var b = new SolidBrush(Enabled ? Ink.Mix(KitInk.Line, KitInk.Accent, knob) : KitInk.Line)) g.FillPath(b, path);
            float r = 9 * s;
            float cx = track.X + 11 * s + knob * 18 * s, cy = track.Y + 11 * s;
            using (var b = new SolidBrush(Enabled ? Ink.Mix(KitInk.Text, KitInk.OnAccent, knob) : KitInk.Faint)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            if (Focused && ShowFocusCues && !KitInk.ByMouse)
            {
                var ring = KitControl.Inside(RectangleF.Inflate(track, 2, 2), Width, Height);
                using (var path = Ink.Round(ring, ring.Height / 2))
                using (var pen = new Pen(KitInk.Text, 1.6f)) g.DrawPath(pen, path);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
    }

    // Pill choices; arrows move between
    class KitSegmented : KitControl
    {
        public string[] Items = new string[0];
        int selected;
        public event Action<int> Changed;

        public int Selected
        {
            get { return selected; }
            set { if (value == selected) return; selected = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.Selection, selected); }
        }

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
            var fs = new Font(Ink.MenuFamily, 12.5f * Ui, FontStyle.Regular, GraphicsUnit.Pixel);
            for (int i = 0; i < Items.Length; i++)
            {
                var c = Cell(i);
                if (i == selected) Fill(g, c, c.Height / 2, KitInk.Raised);
                KitInk.Draw(g, Items[i], i == selected ? fs : f, i == selected ? KitInk.Text : KitInk.Sub, c, ContentAlignment.MiddleCenter);
            }
            f.Dispose();
            fs.Dispose();
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

        void Set(int v)
        {
            v = Math.Max(Minimum, Math.Min(Maximum, (int)Math.Round((double)(v - Minimum) / Step) * Step + Minimum));
            if (v == Value) return;
            Value = v;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            var h = Changed;
            if (h != null) h(v);
        }

        float LabelW { get { return ShowValue ? 84 * Ui : 0; } }

        RectangleF Track { get { float s = Ui, l = LabelW; return new RectangleF(l + 10 * s, Height / 2f - 3 * s, Width - l - 20 * s, 6 * s); } }

        int At(int x) { var t = Track; return Minimum + (int)Math.Round((Maximum - Minimum) * Ease.Clamp01((x - t.X) / Math.Max(1, t.Width))); }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); dragging = true; Capture = true; Set(At(e.X)); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Set(At(e.X)); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }

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
            float s = Ui, f = (float)(Value - Minimum) / Math.Max(1, Maximum - Minimum);
            var t = Track;
            Fill(g, t, t.Height / 2, KitInk.Line);
            Fill(g, new RectangleF(t.X, t.Y, Math.Max(t.Height, t.Width * f), t.Height), t.Height / 2, Enabled ? KitInk.Text : KitInk.Faint);
            float r = (dragging || Hovered ? 9 : 8) * s, cx = t.X + t.Width * f, cy = t.Y + t.Height / 2;
            using (var b = new SolidBrush(KitInk.Text)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (var pen = new Pen(KitInk.Line, 1f)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            if (ShowValue)
                using (var f2 = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, Format(Value), f2, new Rectangle(0, 0, (int)LabelW, Height), Enabled ? KitInk.Sub : KitInk.Faint,
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
        bool hovered;

        public KitButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { KitInk.ByMouse = true; base.OnMouseDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (KitInk.ByMouse) { KitInk.ByMouse = false; Invalidate(); } base.OnKeyUp(e); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            bool ringOn = Focused && ShowFocusCues && !KitInk.ByMouse;
            // Focused pill shrinks so its ring fits
            var r = ringOn ? new RectangleF(3.5f, 3.5f, Width - 7, Height - 7) : new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color bg = Primary ? (hovered ? Ink.Mix(KitInk.Text, KitInk.Window, 0.15f) : KitInk.Text) : (hovered ? KitInk.Hover : KitInk.Line);
            if (!Enabled) bg = KitInk.Line;
            using (var path = Ink.Round(r, r.Height / 2f))
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            KitInk.Draw(g, Text, Font, !Enabled ? KitInk.Faint : Primary ? KitInk.Window : KitInk.Text, r, ContentAlignment.MiddleCenter);
            if (ringOn)
            {
                var ring = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
                using (var path = Ink.Round(ring, ring.Height / 2f))
                using (var pen = new Pen(KitInk.Text, 1.6f)) g.DrawPath(pen, path);
            }
        }
    }

    // Check row: pill with a drawn tick, so no native CheckBox box leaks in
    class KitCheck : KitButton
    {
        bool on, hovered;

        public event EventHandler CheckedChanged;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        public bool Checked { get { return on; } set { if (on == value) return; on = value; var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); Invalidate(); } }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !on; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            bool ringOn = Focused && ShowFocusCues && !KitInk.ByMouse;
            var r = ringOn ? new RectangleF(3.5f, 3.5f, Width - 7, Height - 7) : new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Ink.Round(r, r.Height / 2f))
            using (var b = new SolidBrush(hovered ? KitInk.Hover : KitInk.Line)) g.FillPath(b, path);
            float box = Math.Min(16f, r.Height - 8f), cy = r.Y + r.Height / 2f, cx = r.X + 7f + box / 2f;
            var tick = new RectangleF(cx - box / 2, cy - box / 2, box, box);
            using (var pen = new Pen(on ? KitInk.Text : KitInk.Faint, 1.4f))
                using (var path = Ink.Round(tick, 4)) g.DrawPath(pen, path);
            if (on)
                using (var pen = new Pen(KitInk.Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(cx - box * 0.3f, cy), new PointF(cx - box * 0.06f, cy + box * 0.22f), new PointF(cx + box * 0.32f, cy - box * 0.22f) });
            KitInk.Draw(g, Text, Font, KitInk.Text, new RectangleF(r.X + 7f + box + 8f, r.Y, r.Width - box - 22f, r.Height), ContentAlignment.MiddleLeft);
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

        public void Pick(int i)
        {
            if (i < 0 || i >= Items.Length || i == selected) return;
            Selected = i;
            var h = Changed;
            if (h != null) h(i);
        }

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

    // Confirmation or notice in the app's own colours, never a native MessageBox
    sealed class KitAsk : KitWindow
    {
        readonly Label text = new Label { AutoSize = false };
        readonly KitButton yes = new KitButton { Primary = true };
        readonly KitButton no = new KitButton { Text = "Batal" };
        readonly KitButton ok = new KitButton { Text = "Tutup", Visible = false };
        bool two;
        Font uiFont;

        // Esc and the close box mean no; only an explicit confirm proceeds
        public static bool Confirm(Form owner, string message, string confirm = "Hapus", string dismiss = "Batal")
        {
            using (var d = new KitAsk(message, true, confirm, dismiss))
                return d.ShowDialog(owner ?? Form.ActiveForm) == DialogResult.Yes;
        }

        public static void Notice(Form owner, string message)
        {
            using (var d = new KitAsk(message, false, "", ""))
                d.ShowDialog(owner ?? Form.ActiveForm);
        }

        KitAsk(string message, bool question, string confirm, string dismiss)
        {
            two = question;
            Text = "Tugas dan catatan";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            text.Text = message;
            text.AccessibleName = message;
            yes.Text = confirm;
            no.Text = dismiss;
            yes.Click += delegate { DialogResult = DialogResult.Yes; Close(); };
            no.Click += delegate { DialogResult = DialogResult.No; Close(); };
            ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.AddRange(new Control[] { text, yes, no, ok });
            // Visibility is a post-parenting property; set it once the buttons are in the tree
            yes.Visible = two;
            no.Visible = two;
            ok.Visible = !two;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }

        public override void Relayout()
        {
            if (!IsHandleCreated) return;
            if (uiFont == null) { uiFont = UiFont(14f); text.Font = UiFont(14f); yes.Font = no.Font = ok.Font = UiFont(13f); }
            Font = uiFont; BackColor = KitInk.Window; ForeColor = KitInk.Text;
            float s = Ui;
            int w = (int)(420 * s);
            int textW = w - (int)(40 * s);
            var size = TextRenderer.MeasureText(text.Text, text.Font, new Size(textW, 0), TextFormatFlags.WordBreak);
            int textH = Math.Max((int)(24 * s), size.Height);
            int rowY = textH + (int)(58 * s);
            text.SetBounds((int)(20 * s), (int)(22 * s), textW, textH);
            no.Visible = two;
            int right = w - (int)(20 * s) - (int)(96 * s);
            if (two)
            {
                no.SetBounds(right, rowY, (int)(96 * s), (int)(34 * s));
                yes.SetBounds(right - (int)(108 * s), rowY, (int)(96 * s), (int)(34 * s));
            }
            ok.SetBounds(right, rowY, (int)(96 * s), (int)(34 * s));
            ClientSize = new Size(w, rowY + (int)(34 * s) + (int)(20 * s));
            ActiveControl = two ? (Control)yes : ok;
        }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && uiFont != null) uiFont.Dispose(); }
    }
}
