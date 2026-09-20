using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>
    /// One entry in the sidebar.
    ///
    /// Its own control rather than a rectangle painted by the rail, because a painted
    /// rectangle has no accessible identity: a screen reader would be told "list" and
    /// nothing else. One control per entry means one name, one role and one selected
    /// state each, which is what makes the sidebar navigable without a mouse or a screen.
    /// </summary>
    public sealed class NavItem : Control
    {
        /// <summary>Stable id the form switches on - never the label, which is wording and
        /// will change.</summary>
        public string Key = "";

        bool _hot;
        bool _current;

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Current
        {
            get { return _current; }
            set
            {
                if (_current == value) return;
                _current = value;
                // Roving tab stop: only the current entry is in the tab order, and the
                // arrow keys move between them. Six separate tab stops would mean six
                // presses to get past the navigation to the page it just opened.
                TabStop = value;
                Invalidate();
            }
        }

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            Cursor = Cursors.Hand;
            Height = 40;
            TabStop = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            AccessibleName = Text;
            base.OnTextChanged(e);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new NavAccessibleObject(this);
        }

        sealed class NavAccessibleObject : Control.ControlAccessibleObject
        {
            public NavAccessibleObject(NavItem owner) : base(owner) { }

            public override AccessibleRole Role { get { return AccessibleRole.PageTab; } }

            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = base.State;
                    if (((NavItem)Owner).Current) s |= AccessibleStates.Selected;
                    return s;
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Theme.KeyboardNav = false;
            Focus();
            base.OnMouseDown(e);
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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            Rectangle body = new Rectangle(0, 1, Width - 1, Height - 3);
            if (_current) Theme.FillRound(g, body, 6, Theme.Raise);
            else if (_hot) Theme.FillRound(g, body, 6, Theme.Inset);

            // The current entry carries a bar as well as a fill, so "you are here" is not
            // resting on colour alone - and the label goes heavier for the same reason.
            //
            // Lead, not Save: the page title on the page this entry opens is set in the
            // same yellow, so the sidebar and the heading agree about where you are. Green
            // means "energy kept" everywhere else in the app and should not also mean
            // "selected".
            if (_current)
                using (SolidBrush b = new SolidBrush(Theme.Lead))
                    g.FillRectangle(b, 0, body.Y + 9, 3, body.Height - 18);

            Color fg = _current ? Theme.Lead : (_hot ? Theme.Text : Theme.Dim);
            Font f = _current ? Theme.Section : Theme.Body;
            SizeF ts = g.MeasureString(Text, f);
            Theme.Str(g, Text, f, fg, 16, (Height - ts.Height) / 2f);

            if (Focused && Theme.KeyboardNav) Theme.FocusRing(g, ClientRectangle, 6);
        }
    }

    /// <summary>
    /// The sidebar: one coherent navigation model, replacing the Basic/Advanced switch and
    /// the section strip that used to sit three hundred pixels below it.
    ///
    /// Those two looked identical - both drawn as tab strips - and did entirely different
    /// things: one decided what existed, the other only scrolled the column. Nothing on
    /// screen distinguished them, which is the single biggest reason the old window was
    /// hard to find your way around.
    /// </summary>
    public sealed class NavRail : Panel
    {
        readonly List<NavItem> _items = new List<NavItem>();
        int _index = -1;

        /// <summary>Raised when the chosen section changes, however it was chosen.</summary>
        public event EventHandler SelectionChanged;

        public NavRail()
        {
            BackColor = Theme.Sidebar;
            Width = 240;
            Padding = new Padding(12, 16, 12, 12);
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "Sections";
        }

        public string Selected
        {
            get { return _index >= 0 && _index < _items.Count ? _items[_index].Key : ""; }
        }

        /// <summary>Add an entry. Order of calls is order on screen.</summary>
        public NavItem Add(string key, string label)
        {
            NavItem it = new NavItem {
                Key = key, Text = label,
                Location = new Point(Padding.Left, Padding.Top + _items.Count * 42),
                Width = Width - Padding.Left - Padding.Right
            };
            NavItem local = it;
            it.Click += (s, e) => Choose(_items.IndexOf(local), true);
            it.PreviewKeyDown += (s, e) => Arrow(e);
            _items.Add(it);
            Controls.Add(it);
            if (_index < 0) Choose(0, false);
            return it;
        }

        /// <summary>Up and down move between entries, which is how a list behaves
        /// everywhere else in Windows. Tab leaves the sidebar for the page.</summary>
        void Arrow(PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;

            // Claim the key, or WinForms treats it as dialog navigation and moves focus
            // somewhere else as well as here.
            e.IsInputKey = true;
            Theme.KeyboardNav = true;
            int next = _index + (e.KeyCode == Keys.Down ? 1 : -1);
            if (next < 0 || next >= _items.Count) return;
            Choose(next, true);
            _items[next].Focus();
        }

        public void Select(string key)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Key == key) { Choose(i, false); return; }
        }

        void Choose(int i, bool notify)
        {
            if (i < 0 || i >= _items.Count || i == _index) return;
            _index = i;
            for (int n = 0; n < _items.Count; n++) _items[n].Current = (n == i);
            if (notify && SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        /// <summary>Entries stretch with the rail, so the sidebar can narrow without the
        /// labels being clipped by a width fixed at construction.</summary>
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int w = Width - Padding.Left - Padding.Right;
            foreach (NavItem it in _items) it.Width = Math.Max(40, w);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme.Quality(e.Graphics);
            using (Pen p = new Pen(Theme.Edge, 1f))
                e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
        }
    }

    /// <summary>
    /// The battery summary pinned to the foot of the sidebar.
    ///
    /// Charge, what is left and the mode in effect were spread across a header, a card and
    /// a status line that disagreed with each other by a minute. Here they are one block,
    /// stated once, and visible from every section rather than only the one that owns them.
    /// </summary>
    public sealed class StatusBlock : Control
    {
        public bool HasBattery = true;
        public bool OnAc;
        public int ChargePct;

        /// <summary>Null until it has been measured - never a borrowed number.</summary>
        public double? HoursLeft;

        /// <summary>Plain-language name of the profile the machine currently matches, or
        /// empty when it matches none.</summary>
        public string Mode = "";

        public StatusBlock()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
            Height = 96;
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        /// <summary>Everything this block says, in one sentence, for a screen reader - which
        /// cannot see that the four lines belong together.</summary>
        public void Describe()
        {
            if (!HasBattery) { AccessibleName = "No battery fitted"; return; }
            string s = ChargePct + "% charge, " + (OnAc ? "plugged in" : "on battery");
            if (HoursLeft.HasValue) s += ", about " + Hm(HoursLeft.Value) + " left";
            if (Mode.Length > 0) s += ", " + Mode + " mode";
            AccessibleName = s;
        }

        static string Hm(double hours)
        {
            int m = (int)Math.Round(hours * 60);
            return (m / 60) + " h " + (m % 60) + " m";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            using (Pen p = new Pen(Theme.Edge, 1f))
                g.DrawLine(p, 16, 0, Width - 16, 0);

            if (!HasBattery)
            {
                Theme.Str(g, "Mains power", Theme.Body, Theme.Dim, 16, 16);
                Theme.Str(g, "No battery fitted", Theme.Small, Theme.Mute, 16, 38);
                return;
            }

            string pct = ChargePct + "%";
            SizeF ps = g.MeasureString(pct, Theme.Stat);
            Theme.Str(g, pct, Theme.Stat, Theme.Text, 16, 14);
            Theme.StrRight(g, OnAc ? "Plugged in" : "On battery", Theme.Small, Theme.Dim,
                           Width - 16, 14 + ps.Height - 16);

            // Amber below a fifth left: the one place a charge figure earns a warning
            // colour, and it is paired with the number so colour is not saying it alone.
            Rectangle track = new Rectangle(16, 46, Math.Max(10, Width - 32), 6);
            Theme.FillRound(g, track, 3, Theme.Inset);
            int w = (int)Math.Round(track.Width * Math.Max(0, Math.Min(100, ChargePct)) / 100.0);
            if (w > 0)
                Theme.FillRound(g, new Rectangle(track.X, track.Y, w, track.Height), 3,
                                ChargePct <= 20 ? Theme.Spend : Theme.Save);

            string left = OnAc
                ? "Readings pause while charging"
                : (HoursLeft.HasValue ? "About " + Hm(HoursLeft.Value) + " left" : "Measuring how long is left");
            Theme.Str(g, left, Theme.Small, Theme.Dim, 16, 60);
            if (Mode.Length > 0)
                Theme.Str(g, Mode, Theme.Small, Theme.Mute, 16, 76);
        }
    }
}
