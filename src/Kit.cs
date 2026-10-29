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

        public static void Draw(Graphics g, string s, Font f, Color c, RectangleF r, ContentAlignment align)
        {
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            if (align == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            else if (align == ContentAlignment.MiddleRight) flags |= TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
            else flags |= TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, s, f, Rectangle.Round(r), c, flags);
        }
    }

    // Base: own HWND, focusable, window DPI
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

        RectangleF Track { get { float s = Ui; return new RectangleF(10 * s, Height / 2f - 3 * s, Width - 20 * s, 6 * s); } }

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
                    g.DrawLine(pen, 16 * s, y, Width - 16 * s, y);
                }
        }
    }

    // Setting row: title, detail, control right
    class KitRow : Panel
    {
        public string Title = "", Detail = "";
        public Control Field;

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
            float right = Field != null ? Field.Left - 12 * s : Width - 16 * s;
            using (var f = new Font(Ink.MenuFamily, 13.5f * s, GraphicsUnit.Pixel))
            using (var d = new Font(Ink.MenuFamily, 12f * s, GraphicsUnit.Pixel))
            {
                bool two = Detail.Length > 0;
                KitInk.Draw(g, Title, f, Enabled ? KitInk.Text : KitInk.Faint, new RectangleF(16 * s, two ? 7 * s : 0, right - 16 * s, two ? 22 * s : Height), ContentAlignment.MiddleLeft);
                if (two) KitInk.Draw(g, Detail, d, KitInk.Sub, new RectangleF(16 * s, 28 * s, right - 16 * s, 20 * s), ContentAlignment.MiddleLeft);
            }
        }
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
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Ui = GetDpiForWindow(Handle) / 96f; } catch (EntryPointNotFoundException) { }
            Native.DarkFrame(Handle, !Ink.Light);
            Native.RoundCorners(Handle);
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
