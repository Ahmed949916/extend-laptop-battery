using System;
using System.Collections.Generic;
using System.Drawing;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>
    /// Scrolling container that will not jump to whichever control has focus.
    ///
    /// A stock AutoScroll panel calls ScrollToControl whenever focus moves, so the window
    /// kept opening part-way down with the header card above the fold, and clicking a
    /// slider near the bottom would yank the view around. Returning the current position
    /// keeps the scrollbar fully usable while leaving the view where the user put it.
    /// </summary>
    public sealed class SteadyPanel : FlowLayoutPanel
    {
        /// <summary>
        /// Raised whenever the view has actually moved, whatever moved it.
        ///
        /// Watching the position rather than the cause is the point. The stock Scroll event
        /// covers the scrollbar but not the wheel, and neither fires when something sets
        /// AutoScrollPosition in code - so a section bar wired to Scroll alone goes stale
        /// the moment anyone spins the wheel or the app scrolls itself. Comparing the
        /// position is free, so this can be called from anywhere cheaply.
        /// </summary>
        public event EventHandler Scrolled;

        Point _seen = new Point(int.MinValue, int.MinValue);

        /// <summary>Fire Scrolled if the view has moved since the last check.</summary>
        public void CheckMoved()
        {
            Point p = AutoScrollPosition;
            if (p == _seen) return;
            _seen = p;
            if (Scrolled != null) Scrolled(this, EventArgs.Empty);
        }

        protected override Point ScrollToControl(Control activeControl)
        {
            return DisplayRectangle.Location;
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            CheckMoved();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            CheckMoved();
        }

        // scrolling by any route repaints the panel, which makes this the one hook that
        // catches a programmatic move as well. Safe against re-entry: the handler only
        // invalidates controls outside this panel.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            CheckMoved();
        }
    }

    /// <summary>Rounded surface panel.</summary>
    public class Card : Panel
    {
        public int Radius = 10;
        public Color Fill = Theme.Panel;
        public Color Border = Theme.Edge;

        /// <summary>How heavy that border is. One pixel unless the card is saying
        /// something with its edge - see ModeCard, which thickens it while in use.</summary>
        public float EdgeWidth = 1f;

        /// <summary>Draw the lit top edge. On for cards on the ground, off for cards
        /// nested inside another card, where a second highlight just looks noisy.</summary>
        public bool Lift = true;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ink;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Radius, Fill, Border, EdgeWidth);

            // one lit pixel along the top, the way a real panel catches the light. Cheaper
            // and quieter than a drop shadow, and it survives being drawn on any ground.
            if (Lift && Width > Radius * 2 + 4)
            {
                using (Pen p = new Pen(Theme.Hair, 1f))
                    g.DrawLine(p, Radius, 1, Width - 1 - Radius, 1);
            }
            base.OnPaint(e);
        }
    }

    /// <summary>Flat button with hover and press states.</summary>
    public sealed class PillButton : Control
    {
        public bool Primary;
        public bool Selected;

        /// <summary>
        /// Render as one tab in a strip rather than as a button: no chrome until it is
        /// hovered or current, and the current one carries an underline on the strip's
        /// baseline. Deliberately neutral - a green tab would spend an accent that means
        /// "energy kept" on saying "you are here".
        ///
        /// A property rather than a field because it decides what this control calls itself
        /// to a screen reader: a tab and a button are announced differently, and getting
        /// that wrong is worse than saying nothing.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Tab
        {
            get { return _tab; }
            set { _tab = value; Invalidate(); }
        }
        bool _tab;

        public string Glyph;              // optional Segoe Fluent Icons glyph
        bool _hot, _down;

        // --- working state ----------------------------------------------------------
        // The work behind a button is synchronous and pumps with Application.DoEvents, so
        // a plain Timer still ticks while it runs - which is what makes the arc turn
        // instead of freezing at whatever angle the click left it on.
        bool _busy;
        int _angle;
        string _busyText;
        Timer _spin;

        /// <summary>Spin an arc where the label normally goes. The button keeps its size,
        /// so nothing around it reflows while the work happens.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Busy
        {
            get { return _busy; }
            set
            {
                if (_busy == value) return;
                _busy = value;
                _angle = 0;
                if (_busy)
                {
                    // Windows' "show animations" setting, off, means no turning arc: the
                    // count beside it still climbs, so the button is still visibly working.
                    if (!Theme.ReduceMotion)
                    {
                        if (_spin == null)
                        {
                            _spin = new Timer();
                            _spin.Interval = 45;
                            _spin.Tick += delegate { _angle = (_angle + 24) % 360; Invalidate(); Update(); };
                        }
                        _spin.Start();
                    }
                }
                else
                {
                    if (_spin != null) _spin.Stop();
                    _busyText = null;
                }
                Invalidate();
            }
        }

        /// <summary>Optional caption beside the arc - "4/18" while a list is being written.
        /// Null spins with no text, for work with no countable steps.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string BusyText
        {
            set { _busyText = value; Invalidate(); Update(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _spin != null) { _spin.Stop(); _spin.Dispose(); _spin = null; }
            base.Dispose(disposing);
        }

        /// <summary>The turning arc. A 280-degree sweep so the gap reads as motion; drawn
        /// on the same green as everything else that means the app is doing something.</summary>
        void PaintSpinner(Graphics g, Color fg)
        {
            int d = Math.Min(16, Height - 12);
            if (d < 8) d = 8;
            string cap = _busyText;
            float capw = string.IsNullOrEmpty(cap) ? 0 : g.MeasureString(cap, Theme.Small).Width + 6;
            float x = (Width - d - capw) / 2f;
            float y = (Height - d) / 2f;

            using (Pen p = new Pen(Theme.Edge, 2f))
                g.DrawEllipse(p, x, y, d, d);
            using (Pen p = new Pen(fg, 2f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, x, y, d, d, _angle, 280);
            }
            if (!string.IsNullOrEmpty(cap))
                Theme.Str(g, cap, Theme.Small, Theme.Dim, x + d + 6,
                          (Height - g.MeasureString(cap, Theme.Small).Height) / 2f);
        }

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ink;
            Cursor = Cursors.Hand;
            Height = 36;                 // 34 was sized for 12.5px labels; body type is 14px now
            TabStop = true;
        }

        /// <summary>The label is the name a screen reader reads out, so keep the two the
        /// same automatically rather than hoping every call site remembers.</summary>
        protected override void OnTextChanged(EventArgs e)
        {
            AccessibleName = Text;
            base.OnTextChanged(e);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new PillAccessibleObject(this);
        }

        /// <summary>
        /// What Narrator is told about this button.
        ///
        /// A custom Control subclass reports no role, no name and no state by default, so
        /// before this every control in the app announced as an unnamed pane - the app was
        /// not partially usable with a screen reader, it was silent. Role tells you whether
        /// you are on a tab or a button; Selected is what carries "you are here" for the
        /// section strip and the profile rows, which otherwise existed only as a colour.
        /// </summary>
        sealed class PillAccessibleObject : Control.ControlAccessibleObject
        {
            public PillAccessibleObject(PillButton owner) : base(owner) { }

            public override AccessibleRole Role
            {
                get { return ((PillButton)Owner).Tab ? AccessibleRole.PageTab : AccessibleRole.PushButton; }
            }

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = base.State;
                    PillButton b = (PillButton)Owner;
                    if (b.Selected) s |= AccessibleStates.Selected | AccessibleStates.Checked;
                    if (b.Busy) s |= AccessibleStates.Busy;
                    return s;
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Theme.KeyboardNav = false; _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            if (Tab)
            {
                PaintTab(g);
                return;
            }

            // The recommended action is filled, not outlined. An outlined primary sat at the
            // same visual weight as everything beside it, which is how "Restore original
            // settings" ended up looking like the main thing to do on the profiles section.
            Color fill, border, fg;
            if (Selected)      { fill = Theme.Raise; border = Theme.Save; fg = Theme.Text; }
            else if (Primary)  { fill = Theme.Save;  border = Theme.Save; fg = Theme.Ink;  }
            else               { fill = Theme.Inset; border = Theme.Line; fg = Theme.Text; }

            // Hover and press move the surface, never the accent: green says "this is the
            // one in use", and a hover that also went green would say that about whatever
            // the pointer happened to be passing over.
            if (_down) fill = ControlPaint.Dark(fill, 0.06f);
            else if (_hot) fill = ControlPaint.Light(fill, 0.10f);

            // Theme.ButtonEdge, not 1px: an outlined button on a dark ground needs a
            // border you can actually see to read as a control rather than as a panel in
            // a slightly different shade.
            Theme.FillRound(g, r, 8, fill, border, Theme.ButtonEdge);

            // fg, not the accent: a Primary button is now filled with the accent, so an
            // accent-coloured arc on top of it would be an invisible spinner.
            if (_busy) { PaintSpinner(g, fg); return; }

            float tx = 0;
            SizeF ts = g.MeasureString(Text, Theme.Title);
            float gw = 0;
            if (!string.IsNullOrEmpty(Glyph)) gw = g.MeasureString(Glyph, Theme.Icon).Width + 4;
            tx = (Width - ts.Width - gw) / 2f;
            float ty = (Height - ts.Height) / 2f;
            if (!string.IsNullOrEmpty(Glyph))
            {
                SizeF gs = g.MeasureString(Glyph, Theme.Icon);
                Theme.Str(g, Glyph, Theme.Icon, fg, tx, (Height - gs.Height) / 2f);
                tx += gw;
            }
            Theme.Str(g, Text, Theme.Title, fg, tx, ty);

            if (Focused && Theme.KeyboardNav) Theme.FocusRing(g, ClientRectangle, 8);
        }

        /// <summary>One tab in the section strip. Quiet until it means something.</summary>
        void PaintTab(Graphics g)
        {
            int bar = 2;                                  // the strip's baseline
            Rectangle body = new Rectangle(0, 0, Width - 1, Height - bar - 2);

            if (Selected) Theme.FillRound(g, body, 7, Theme.Raise);
            else if (_hot) Theme.FillRound(g, body, 7, Theme.Inset);

            Color fg = (Selected || _hot) ? Theme.Text : Theme.Dim;
            if (_busy) { PaintSpinner(g, Theme.Save); return; }
            SizeF ts = g.MeasureString(Text, Theme.Tab);
            Theme.Str(g, Text, Theme.Tab, fg,
                      (Width - ts.Width) / 2f, (body.Height - ts.Height) / 2f);

            if (Selected)
                using (SolidBrush b = new SolidBrush(Theme.Save))
                    g.FillRectangle(b, 6, Height - bar, Math.Max(0, Width - 12), bar);

            if (Focused && Theme.KeyboardNav)
                Theme.FocusRing(g, new Rectangle(0, 0, Width, body.Height + 1), 7);
        }

        /// <summary>Raise Click without a mouse. Mirrors SectionToggle.Toggle, so the
        /// keyboard path and the tests drive a button the same way a click does.</summary>
        public void Press()
        {
            OnClick(EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Theme.KeyboardNav = true;
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
        protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }
    }

    /// <summary>
    /// Custom slider. Refuses the mouse wheel on purpose - a focused Win32 TrackBar
    /// swallows wheel events and moves its own thumb, which silently rewrites a system
    /// setting when the user only meant to scroll the window.
    /// </summary>
    public sealed class Slider : Control
    {
        public int Minimum = 0;
        public int Maximum = 100;
        public int Step = 1;
        public Color Accent = Theme.Save;

        int _value;
        bool _drag, _hot;

        public event EventHandler ValueChanged;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Height = 32;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new SliderAccessibleObject(this);
        }

        /// <summary>
        /// A slider that does not report its value is a slider a screen reader cannot use:
        /// you can hear that you are on one, and never what it is set to. Every knob in the
        /// settings section is one of these, so this is most of what the app is for.
        /// </summary>
        sealed class SliderAccessibleObject : Control.ControlAccessibleObject
        {
            public SliderAccessibleObject(Slider owner) : base(owner) { }

            public override AccessibleRole Role { get { return AccessibleRole.Slider; } }

            public override string Value
            {
                get { return ((Slider)Owner).Value.ToString(CultureInfo.CurrentCulture); }
                set
                {
                    int v;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out v))
                        ((Slider)Owner).Value = v;
                }
            }
        }

        Rectangle Track { get { return new Rectangle(8, Height / 2 - 3, Width - 16, 6); } }

        float Ratio { get { return Maximum == Minimum ? 0 : (float)(_value - Minimum) / (Maximum - Minimum); } }

        int ThumbX { get { return Track.X + (int)Math.Round(Ratio * Track.Width); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            Rectangle t = Track;
            Theme.FillRound(g, t, 3, Theme.Inset);

            Rectangle filled = new Rectangle(t.X, t.Y, Math.Max(0, ThumbX - t.X), t.Height);
            if (filled.Width > 0) Theme.FillRound(g, filled, 3, Accent);

            int cx = ThumbX, cy = Height / 2;
            int rad = _drag ? 9 : (_hot || Focused ? 8 : 7);
            if (_hot || _drag || Focused)
            {
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(40, Accent)))
                    g.FillEllipse(halo, cx - rad - 5, cy - rad - 5, (rad + 5) * 2, (rad + 5) * 2);
            }
            using (SolidBrush b = new SolidBrush(Accent))
                g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            using (SolidBrush b = new SolidBrush(Theme.Ink))
                g.FillEllipse(b, cx - rad + 3, cy - rad + 3, (rad - 3) * 2, (rad - 3) * 2);

            if (Focused && Theme.KeyboardNav) Theme.FocusRing(g, ClientRectangle, 8);
        }

        void SetFromX(int x)
        {
            Rectangle t = Track;
            if (t.Width <= 0) return;
            float r = (float)(x - t.X) / t.Width;
            r = Math.Max(0f, Math.Min(1f, r));
            int v = Minimum + (int)Math.Round(r * (Maximum - Minimum));
            if (Step > 1) v = Minimum + (int)Math.Round((double)(v - Minimum) / Step) * Step;
            Value = v;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Theme.KeyboardNav = false;
            Focus(); _drag = true; SetFromX(e.X); base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) SetFromX(e.X);
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Invalidate(); base.OnMouseUp(e); }

        // deliberately swallowed - see the class comment
        protected override void OnMouseWheel(MouseEventArgs e) { }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right ||
                keyData == Keys.Home || keyData == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Theme.KeyboardNav = true;
            int big = Math.Max(Step, (Maximum - Minimum) / 20);
            if (e.KeyCode == Keys.Left)  { Value -= (e.Control ? big : Step); e.Handled = true; }
            if (e.KeyCode == Keys.Right) { Value += (e.Control ? big : Step); e.Handled = true; }
            if (e.KeyCode == Keys.Home)  { Value = Minimum; e.Handled = true; }
            if (e.KeyCode == Keys.End)   { Value = Maximum; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    /// <summary>
    /// Dropdown that actually looks like the rest of the app. A Win32 ComboBox paints its
    /// own field and arrow no matter what you override, so this draws the closed state and
    /// opens a borderless popup list instead.
    /// </summary>
    public sealed class Picker : Control
    {
        readonly List<string> _items = new List<string>();
        int _index = -1;
        bool _hot, _open;
        PickerPopup _popup;

        public event EventHandler SelectedIndexChanged;
        public object Tag2;

        public Picker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Height = 32;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new PickerAccessibleObject(this);
        }

        /// <summary>Announces as a combo box and reads back the option currently chosen,
        /// which is the whole content of the control.</summary>
        sealed class PickerAccessibleObject : Control.ControlAccessibleObject
        {
            public PickerAccessibleObject(Picker owner) : base(owner) { }

            public override AccessibleRole Role { get { return AccessibleRole.ComboBox; } }

            public override string Value
            {
                get { return ((Picker)Owner).SelectedText; }
                set
                {
                    Picker p = (Picker)Owner;
                    int i = p.Items.IndexOf(value);
                    if (i >= 0) p.SelectedIndex = i;
                }
            }
        }

        public List<string> Items { get { return _items; } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int v = (value < 0 || value >= _items.Count) ? -1 : value;
                if (v == _index) return;
                _index = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedText { get { return _index >= 0 ? _items[_index] : ""; } }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Theme.KeyboardNav = false;
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            Open();
            base.OnClick(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter ||
                   keyData == Keys.Down || keyData == Keys.Up || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Theme.KeyboardNav = true;
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Open(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down && _index < _items.Count - 1) { SelectedIndex = _index + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up && _index > 0) { SelectedIndex = _index - 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        void Open()
        {
            if (_open || _items.Count == 0) return;
            _open = true;
            _popup = new PickerPopup(this);
            _popup.FormClosed += (s, e) => { _open = false; _popup = null; Invalidate(); };
            _popup.ShowFor(this);
            Invalidate();
        }

        internal void Choose(int i)
        {
            SelectedIndex = i;
            if (_popup != null) _popup.Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color border = (Focused || _open) ? Theme.Save : Theme.Line;
            Color fill = _hot ? ControlPaint.Light(Theme.Inset, 0.10f) : Theme.Inset;
            Theme.FillRound(g, r, 6, fill, border);

            string t = SelectedText;
            SizeF ts = g.MeasureString(t, Theme.Body);
            Theme.Str(g, t, Theme.Body, Theme.Text, 10, (Height - ts.Height) / 2f);

            string chev = _open ? Theme.GlyphChevDown : Theme.GlyphChevRight;
            SizeF cs = g.MeasureString(chev, Theme.IconSmall);
            Theme.Str(g, chev, Theme.IconSmall, Theme.Dim, Width - cs.Width - 8, (Height - cs.Height) / 2f);

            if (Focused && Theme.KeyboardNav) Theme.FocusRing(g, ClientRectangle, 6);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    /// <summary>The list that drops out of a Picker.</summary>
    public sealed class PickerPopup : Form
    {
        readonly Picker _owner;
        int _hover = -1;
        const int RowH = 26;

        public PickerPopup(Picker owner)
        {
            _owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Inset;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }
        }

        public void ShowFor(Picker p)
        {
            Width = Math.Max(p.Width, 180);
            Height = p.Items.Count * RowH + 8;
            Point at = p.PointToScreen(new Point(0, p.Height + 2));
            Rectangle wa = Screen.FromControl(p).WorkingArea;
            if (at.Y + Height > wa.Bottom) at.Y = p.PointToScreen(Point.Empty).Y - Height - 2;
            Location = at;
            Show();
            Focus();
        }

        protected override void OnDeactivate(EventArgs e) { Close(); base.OnDeactivate(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = (e.Y - 4) / RowH;
            if (i != _hover) { _hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = (e.Y - 4) / RowH;
            if (i >= 0 && i < _owner.Items.Count) _owner.Choose(i);
            base.OnMouseClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(Theme.Ink);
            Theme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), 8, Theme.Inset, Theme.Edge);
            for (int i = 0; i < _owner.Items.Count; i++)
            {
                Rectangle r = new Rectangle(4, 4 + i * RowH, Width - 8, RowH);
                bool sel = i == _owner.SelectedIndex;
                if (i == _hover) Theme.FillRound(g, r, 5, Theme.Edge);
                Theme.Str(g, _owner.Items[i], Theme.Body, sel ? Theme.Save : Theme.Text,
                          r.X + 8, r.Y + (RowH - 17) / 2f);
                if (sel) Theme.StrRight(g, Theme.GlyphCheck, Theme.IconSmall, Theme.Save, r.Right - 8, r.Y + 5);
            }
        }
    }

    /// <summary>
    /// Small circled "i". Hovering pops a panel explaining what the setting does and,
    /// where it is known, what it measured on this machine.
    /// </summary>
    public sealed class InfoDot : Control
    {
        public string Body = "";
        public string Heading = "";
        bool _hot;
        static InfoPopup _popup;

        // Hover alone was too brittle to be the only way in. The target is 18px across, and
        // the popup used to vanish the instant the pointer left it - so a small drift on the
        // way to reading it closed the thing you were reaching for, which reads exactly like
        // "the info icons do nothing". Now: hovering opens it, a short grace period keeps it
        // open while the pointer travels, moving onto the popup itself keeps it open, and a
        // click pins it so it stays until you click again.
        bool _pinned;
        Timer _close;
        static InfoDot _owner;          // whichever dot the shared popup currently belongs to

        public InfoDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(20, 20);
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;

            // This used to be TabStop = false, which meant every explanation in the app -
            // what a watt is, what boost does, what core-seconds are - could only be reached
            // with a mouse. The technical vocabulary this app cannot avoid was locked behind
            // a pointer, so a keyboard user got the jargon and none of the glossary.
            TabStop = true;

            _close = new Timer();
            _close.Interval = 220;
            _close.Tick += delegate { CloseIfAway(); };
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new InfoAccessibleObject(this);
        }

        /// <summary>The dot itself says nothing; what it is about is in the heading it was
        /// given, and what it says is the body. Both belong in the announcement.</summary>
        sealed class InfoAccessibleObject : Control.ControlAccessibleObject
        {
            public InfoAccessibleObject(InfoDot owner) : base(owner) { }

            public override AccessibleRole Role { get { return AccessibleRole.ButtonDropDown; } }

            public override string Name
            {
                get
                {
                    InfoDot d = (InfoDot)Owner;
                    return string.IsNullOrEmpty(d.Heading) ? "More information" : "About " + d.Heading;
                }
                set { Owner.AccessibleName = value; }
            }

            public override string Description { get { return ((InfoDot)Owner).Body; } }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter ||
                   keyData == Keys.Escape || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Theme.KeyboardNav = true;
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                _pinned = !_pinned;
                if (_pinned) Open(); else ClosePopup();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && _pinned)
            {
                ClosePopup();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }

        /// <summary>Tabbing away closes it. Leaving a pinned popup floating over a section
        /// you have already left is how you end up with two explanations on screen.</summary>
        protected override void OnLeave(EventArgs e)
        {
            if (_pinned) ClosePopup();
            Invalidate();
            base.OnLeave(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _close != null) { _close.Stop(); _close.Dispose(); _close = null; }
            base.Dispose(disposing);
        }

        /// <summary>Pinned, or the pointer is still on the dot or over the popup? Then stay.</summary>
        void CloseIfAway()
        {
            if (_pinned) { _close.Stop(); return; }
            Point cur = Cursor.Position;
            bool onDot = ClientRectangle.Contains(PointToClient(cur));
            bool onPopup = _popup != null && _popup.Visible && _popup.Bounds.Contains(cur);
            if (onDot || onPopup) return;          // keep checking until it really leaves
            _close.Stop();
            ClosePopup();
        }

        void Open()
        {
            if (_popup == null) _popup = new InfoPopup();
            _owner = this;
            _popup.Show(this, Heading, Body);
            _close.Stop();
        }

        void ClosePopup()
        {
            _pinned = false;
            if (_owner == this && _popup != null) _popup.Hide();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // A click is the reliable way in: it opens and stays open.
            Theme.KeyboardNav = false;
            Focus();
            _pinned = !_pinned;
            if (_pinned) Open(); else ClosePopup();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);
            Color c = (_hot || _pinned || Focused) ? Theme.Save : Theme.Dim;
            using (Pen p = new Pen(c, 1.2f)) g.DrawEllipse(p, 2, 2, Width - 5, Height - 5);
            SizeF s = g.MeasureString("i", Theme.Small);
            Theme.Str(g, "i", Theme.Small, c, (Width - s.Width) / 2f + 0.5f, (Height - s.Height) / 2f);

            if (Focused && Theme.KeyboardNav) Theme.FocusRing(g, ClientRectangle, 10);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hot = true; Invalidate();
            Open();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = false; Invalidate();
            if (!_pinned) _close.Start();     // grace period, not an immediate close
            base.OnMouseLeave(e);
        }
    }

    /// <summary>Borderless popup that never takes focus from the window behind it.</summary>
    public sealed class InfoPopup : Form
    {
        string _head = "", _body = "";

        public InfoPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Inset;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        public void Show(Control anchor, string heading, string body)
        {
            _head = heading ?? ""; _body = body ?? "";
            int w = 330;
            using (Graphics g = CreateGraphics())
            {
                Theme.Quality(g);
                SizeF hs = g.MeasureString(_head, Theme.Title, w - 24);
                SizeF bs = g.MeasureString(_body, Theme.Small, w - 24);
                Height = (int)(hs.Height + bs.Height) + 26;
            }
            Width = w;
            Point p = anchor.PointToScreen(new Point(anchor.Width + 8, -6));
            Rectangle screen = Screen.FromControl(anchor).WorkingArea;
            if (p.X + Width > screen.Right) p.X = anchor.PointToScreen(Point.Empty).X - Width - 8;
            if (p.Y + Height > screen.Bottom) p.Y = screen.Bottom - Height - 8;
            Location = p;
            Invalidate();
            if (!Visible) base.Show();
            else BringToFront();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(Theme.Ink);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, 8, Theme.Inset, Theme.Edge);
            float y = 10;
            if (_head.Length > 0)
            {
                RectangleF hr = new RectangleF(12, y, Width - 24, Height);
                using (SolidBrush b = new SolidBrush(Theme.Save)) g.DrawString(_head, Theme.Title, b, hr);
                y += g.MeasureString(_head, Theme.Title, Width - 24).Height + 3;
            }
            RectangleF br = new RectangleF(12, y, Width - 24, Height - y);
            using (SolidBrush b = new SolidBrush(Theme.Text)) g.DrawString(_body, Theme.Small, b, br);
        }
    }

    /// <summary>Clickable section header with a rotating chevron.</summary>
    public sealed class SectionToggle : Control
    {
        public bool Expanded;
        public string Caption = "";
        public string Sub = "";
        bool _hot;
        public event EventHandler Toggled;

        public SectionToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ink;
            Height = 38;
            Cursor = Cursors.Hand;
            TabStop = true;          // it could be focused before, but Enter did nothing
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ToggleAccessibleObject(this);
        }

        /// <summary>Announces whether the section it controls is open, which is the one
        /// thing a chevron communicates and a screen reader could not previously hear.</summary>
        sealed class ToggleAccessibleObject : Control.ControlAccessibleObject
        {
            public ToggleAccessibleObject(SectionToggle owner) : base(owner) { }

            public override AccessibleRole Role { get { return AccessibleRole.ButtonDropDown; } }

            public override string Name
            {
                get { return ((SectionToggle)Owner).Caption; }
                set { Owner.AccessibleName = value; }
            }

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = base.State;
                    s |= ((SectionToggle)Owner).Expanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed;
                    return s;
                }
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Theme.KeyboardNav = true;
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Toggle(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Theme.KeyboardNav = false;
            Focus();
            base.OnMouseDown(e);
        }

        protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
        protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        /// <summary>Flip the section. Public so it can be driven without a mouse.</summary>
        public void Toggle()
        {
            Expanded = !Expanded;
            Invalidate();
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            Toggle();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);
            Color c = (_hot || Focused) ? Theme.Text : Theme.Dim;
            string chev = Expanded ? Theme.GlyphChevDown : Theme.GlyphChevRight;

            // Measured rather than nudged by hand: the offsets here were tuned against a
            // 13px caption, and type is set in points now, so a fixed offset would drift
            // the moment the display scaling changed.
            SizeF cs = g.MeasureString(chev, Theme.IconSmall);
            SizeF cap = g.MeasureString(Caption, Theme.Section);
            Theme.Str(g, chev, Theme.IconSmall, c, 3, (Height - cs.Height) / 2f);
            Theme.Str(g, Caption, Theme.Section, Theme.Text, 24, (Height - cap.Height) / 2f);
            if (Sub.Length > 0)
            {
                SizeF sub = g.MeasureString(Sub, Theme.Small);
                Theme.Str(g, Sub, Theme.Small, Theme.Dim, 24 + cap.Width + 10, (Height - sub.Height) / 2f);
            }

            if (Focused && Theme.KeyboardNav) Theme.FocusRing(g, ClientRectangle, 6);
        }
    }

    /// <summary>
    /// The measurement trace. This whole tuning exercise was about watching a number
    /// move over a window rather than trusting a spec, so the header shows the actual
    /// recent history instead of a lone digit.
    /// </summary>
    public sealed class Sparkline : Control
    {
        readonly List<double> _pts = new List<double>();
        public int Capacity = 60;
        public double Floor = 0;
        public double Ceiling = 20;

        public Sparkline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;

            // A picture, not a control: name it so it is not announced as an unnamed pane,
            // and keep it out of the tab order, where there is nothing to do with it.
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Recent power use";
            TabStop = false;
        }

        public void Push(double v)
        {
            _pts.Add(v);
            while (_pts.Count > Capacity) _pts.RemoveAt(0);
            Invalidate();
        }

        public void Clear() { _pts.Clear(); Invalidate(); }
        public int Count { get { return _pts.Count; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            if (_pts.Count < 2)
            {
                Theme.Str(g, "collecting samples", Theme.Small, Theme.Dim, 2, Height / 2f - 8);
                return;
            }

            double lo = Floor, hi = Ceiling;
            foreach (double d in _pts) { if (d > hi) hi = d; if (d < lo) lo = d; }
            if (hi - lo < 1) hi = lo + 1;

            PointF[] line = new PointF[_pts.Count];
            for (int i = 0; i < _pts.Count; i++)
            {
                float x = (float)i / (_pts.Count - 1) * (Width - 2) + 1;
                float y = (float)(1 - (_pts[i] - lo) / (hi - lo)) * (Height - 8) + 4;
                line[i] = new PointF(x, y);
            }

            double last = _pts[_pts.Count - 1];
            Color c = last > 12 ? Theme.Spend : Theme.Save;

            PointF[] area = new PointF[line.Length + 2];
            Array.Copy(line, area, line.Length);
            area[line.Length]     = new PointF(Width - 1, Height);
            area[line.Length + 1] = new PointF(1, Height);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(38, c)))
                g.FillPolygon(b, area);
            using (Pen p = new Pen(c, 1.6f) { LineJoin = LineJoin.Round })
                g.DrawLines(p, line);

            PointF end = line[line.Length - 1];
            using (SolidBrush b = new SolidBrush(c))
                g.FillEllipse(b, end.X - 3, end.Y - 3, 6, 6);
        }
    }
}
