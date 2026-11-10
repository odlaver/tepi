using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tepi
{
    partial class Island
    {
        partial class PrefsWindow
        {
            // Screen sketch: five spots for the island
            class PlacePicker : KitControl
            {
                public event Action<int> Changed;
                int selected, hover = -1;
                // Reading order for arrow keys
                static readonly int[] Order = { PlaceTopLeft, PlaceTop, PlaceTopRight, PlaceLeft, PlaceRight };

                public int Selected
                {
                    get { return selected; }
                    set { if (value == selected) return; selected = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.Selection, value); }
                }

                RectangleF Screen { get { float s = Ui; return new RectangleF(8 * s, 6 * s, Width - 16 * s, Height - 12 * s); } }

                // Island sketch at a spot, hugging its edge
                RectangleF Spot(int i)
                {
                    var sc = Screen;
                    float s = Ui, w = 38 * s, h = 10 * s, tall = 30 * s, inset = 14 * s;
                    switch (i)
                    {
                        case PlaceTopLeft: return new RectangleF(sc.X + inset, sc.Y, w, h);
                        case PlaceTopRight: return new RectangleF(sc.Right - inset - w, sc.Y, w, h);
                        // Sides stand upright, like the island there
                        case PlaceLeft: return new RectangleF(sc.X, sc.Y + sc.Height * 0.24f, h, tall);
                        case PlaceRight: return new RectangleF(sc.Right - h, sc.Y + sc.Height * 0.24f, h, tall);
                    }
                    return new RectangleF(sc.X + sc.Width / 2 - w / 2, sc.Y, w, h);
                }

                RectangleF Cell(int i)
                {
                    var r = Spot(i);
                    r.Inflate(9 * Ui, 9 * Ui);
                    return r;
                }

                int At(Point pt)
                {
                    foreach (int i in Order) if (Cell(i).Contains(pt)) return i;
                    return -1;
                }

                void Pick(int i)
                {
                    if (i < 0 || i == selected) return;
                    Selected = i;
                    var h = Changed;
                    if (h != null) h(i);
                }

                protected override void OnMouseMove(MouseEventArgs e)
                {
                    base.OnMouseMove(e);
                    int h = At(e.Location);
                    if (h == hover) return;
                    hover = h;
                    Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }

                protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

                protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Focus(); Pick(At(e.Location)); }

                protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

                protected override void OnKeyDown(KeyEventArgs e)
                {
                    base.OnKeyDown(e);
                    int step = e.KeyCode == Keys.Left || e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Right || e.KeyCode == Keys.Down ? 1 : 0;
                    if (step == 0) return;
                    int at = Array.IndexOf(Order, selected);
                    Pick(Order[(at + step + Order.Length) % Order.Length]);
                    e.Handled = true;
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    var g = e.Graphics;
                    KitInk.Smooth(g);
                    g.Clear(KitInk.Under(this));
                    float s = Ui;
                    var sc = Screen;
                    // The screen, a touch lighter than its group
                    Fill(g, sc, 9 * s, KitInk.Line);
                    foreach (int i in Order)
                    {
                        var r = Spot(i);
                        bool on = i == selected;
                        Color c = on ? KitInk.Text : i == hover ? KitInk.Sub : KitInk.Faint;
                        using (var path = Ink.Round(r, r.Height / 2))
                        using (var b = new SolidBrush(c)) g.FillPath(b, path);
                        if (!on) continue;
                        // Two dots: the island's eyes
                        using (var dot = new SolidBrush(KitInk.Under(this)))
                        {
                            bool upright = r.Height > r.Width;
                            float cy = r.Y + (upright ? r.Width * 0.9f : r.Height / 2), cx = r.X + r.Width / 2, d = (upright ? 2.1f : 2.6f) * s, gap = (upright ? 2.3f : 3.4f) * s;
                            g.FillEllipse(dot, cx - gap - d / 2, cy - d / 2, d, d);
                            g.FillEllipse(dot, cx + gap - d / 2, cy - d / 2, d, d);
                        }
                    }
                    Ring(g, Cell(selected), 10 * s);
                }

                protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

                class Access : ControlAccessibleObject
                {
                    readonly PlacePicker owner;
                    public Access(PlacePicker owner) : base(owner) { this.owner = owner; }
                    public override AccessibleRole Role { get { return AccessibleRole.PageTabList; } }
                    public override int GetChildCount() { return Order.Length; }
                    public override AccessibleObject GetChild(int i) { return new Item(owner, Order[i]); }
                    public override AccessibleObject GetSelected() { return new Item(owner, owner.selected); }
                    public override AccessibleObject GetFocused() { return owner.Focused ? GetSelected() : null; }
                }

                class Item : AccessibleObject
                {
                    readonly PlacePicker owner;
                    readonly int place;
                    public Item(PlacePicker owner, int place) { this.owner = owner; this.place = place; }
                    public override string Name { get { return Lang.T(PlaceNames[place]); } }
                    public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
                    public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
                    public override AccessibleStates State
                    {
                        get { return AccessibleStates.Selectable | AccessibleStates.Focusable | (place == owner.selected ? AccessibleStates.Selected | AccessibleStates.Checked : 0); }
                    }
                    public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Round(owner.Cell(place))); } }
                    public override string DefaultAction { get { return Lang.T("Pilih"); } }
                    public override void DoDefaultAction() { owner.Pick(place); }
                }
            }
        }
    }
}
