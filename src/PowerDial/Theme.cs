using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>
    /// Instrument-panel palette and type.
    ///
    /// Two accents, and both of them mean something: amber is energy leaving, green is
    /// energy kept. The watts readout switches between them based on the actual
    /// measurement, so colour is carrying data rather than decorating the window.
    ///
    /// **Type is measured in points, never pixels.** A pixel font is a fixed number of
    /// physical pixels whatever the display is doing, and this app declares itself
    /// PerMonitorV2 aware (app.manifest, Program.Main) - which tells Windows not to
    /// bitmap-scale it, on the promise that the app scales itself. It did not: at 150%
    /// scaling every label stayed its literal pixel size and the whole interface rendered
    /// a third too small against everything else on screen. A point is 1/72 inch, so GDI+
    /// resolves it against the device the text is actually being drawn on, and one Font
    /// object is correct on every monitor. Do not put GraphicsUnit.Pixel back.
    /// </summary>
    public static class Theme
    {
        // --- surfaces, darkest first -------------------------------------------------
        public static readonly Color Ink    = Color.FromArgb(0x0F, 0x13, 0x18);  // ground
        public static readonly Color Sidebar= Color.FromArgb(0x14, 0x1A, 0x21);  // navigation rail
        public static readonly Color Panel  = Color.FromArgb(0x18, 0x1E, 0x26);  // card
        public static readonly Color Inset  = Color.FromArgb(0x11, 0x17, 0x21);  // tracks, fields, wells

        /// <summary>Hairline between things. Decorative only - it is ~1.3:1 against a card,
        /// which is far too faint to be what tells you something is a control. That is what
        /// <see cref="Line"/> is for.</summary>
        public static readonly Color Edge   = Color.FromArgb(0x2A, 0x32, 0x3E);

        /// <summary>The border that says "this is a control you can operate". 3.3:1 against
        /// a card, which is what WCAG 1.4.11 asks of a boundary carrying that meaning. The
        /// old button border was Edge at 1.33:1, and buttons duly disappeared into the card
        /// they sat on.</summary>
        public static readonly Color Line   = Color.FromArgb(0x64, 0x6F, 0x7E);

        /// <summary>
        /// How heavy a button's outline is. One pixel of #646F7E clears the 3:1 minimum for
        /// a control boundary on paper, but on a dark ground at laptop density it reads as
        /// a smudge rather than an edge - a button looked like a slightly different shade
        /// of panel. Two pixels is what makes the outlined buttons read as buttons beside
        /// the filled primary one.
        /// </summary>
        public const float ButtonEdge = 2f;

        /// <summary>
        /// How heavy the outline is on the one card that is currently in effect. Same
        /// reasoning as ButtonEdge, and the same weight on purpose: a green hairline said
        /// "this is the mode you are in" so quietly that the tick and the IN USE NOW pill
        /// were carrying the state on their own.
        /// </summary>
        public const float ActiveEdge = 2f;

        // --- text, brightest first ---------------------------------------------------
        public static readonly Color Text   = Color.FromArgb(0xE8, 0xED, 0xF4);

        /// <summary>Secondary text - 8:1 on a card. It was 0x7C8798, which measured 4.75:1
        /// and carried most of the app's explanatory copy at the smallest size in the
        /// window: technically a pass, and genuinely hard to read.</summary>
        public static readonly Color Dim    = Color.FromArgb(0xA9, 0xB4, 0xC2);

        /// <summary>Third-rank metadata only - units, counts, provenance. 4.8:1, so it is
        /// a pass at any size, but it is the quietest thing here: never put an instruction
        /// in it.</summary>
        public static readonly Color Mute   = Color.FromArgb(0x7F, 0x8B, 0x9B);

        // --- the two accents, and the one alarm ---------------------------------------
        /// <summary>
        /// The page title, on every page. Deliberately not Spend: that amber means "energy
        /// leaving" and is spent on figures, bars and warnings, so a title in it would read
        /// as a verdict on the page. This is a yellow, lighter and less orange, and it is
        /// used for exactly one thing - saying where you are.
        /// </summary>
        public static readonly Color Lead   = Color.FromArgb(0xF3, 0xCE, 0x5A);

        public static readonly Color Spend  = Color.FromArgb(0xE8, 0xA3, 0x3D);  // watts going out
        public static readonly Color Save   = Color.FromArgb(0x4F, 0xD6, 0xA9);  // watts kept
        public static readonly Color Alert  = Color.FromArgb(0xF2, 0x70, 0x5E);  // regression

        /// <summary>A bar that is only showing a quantity, not a severity. Amber on every
        /// row made four ordinary processes look like four problems, which is how an accent
        /// that means something stops meaning it.</summary>
        public static readonly Color Data   = Color.FromArgb(0x46, 0x52, 0x5F);

        // Two more steps up the same neutral ramp - Ink, Panel, Raise, Hair - not a third
        // accent. They exist so "this surface is raised" and "this tab is the current one"
        // can be said without spending amber or green, which carry meaning about energy and
        // must not be diluted into decoration.
        public static readonly Color Raise  = Color.FromArgb(0x2C, 0x37, 0x44);  // hover, current tab
        public static readonly Color Hair   = Color.FromArgb(0x3A, 0x45, 0x52);  // lit top edge of a card

        // Bahnschrift is Windows' DIN derivative - an engineering face, not the Segoe
        // default. Used for anything numeric or instrument-like.
        public const string DisplayFamily = "Bahnschrift SemiBold Condensed";
        public const string LabelFamily   = "Bahnschrift SemiBold";
        public const string BodyFamily    = "Segoe UI Variable Text";
        public const string MonoFamily    = "Cascadia Mono";
        public const string IconFamily    = "Segoe Fluent Icons";

        // The scale, in points. At 100% scaling one point is 4/3 of a pixel, so the pixel
        // size each of these lands on at 96 dpi is in the comment - which is what the old
        // pixel constants used to say directly. Nothing is below 9 pt (12 px): that is the
        // floor for a desktop interface, and Small used to sit under it at 10.5 px.
        public static readonly Font Readout   = new Font(DisplayFamily, 33f,   FontStyle.Regular, GraphicsUnit.Point);  // 44 px
        public static readonly Font Page      = new Font(LabelFamily,   21f,   FontStyle.Regular, GraphicsUnit.Point);  // 28 px - page title
        public static readonly Font Stat      = new Font(DisplayFamily, 16f,   FontStyle.Regular, GraphicsUnit.Point);  // 21 px
        public static readonly Font Head      = new Font(LabelFamily,   15f,   FontStyle.Regular, GraphicsUnit.Point);  // 20 px
        public static readonly Font Section   = new Font(LabelFamily,   12f,   FontStyle.Regular, GraphicsUnit.Point);  // 16 px
        public static readonly Font Tab       = new Font(BodyFamily,    10.5f, FontStyle.Regular, GraphicsUnit.Point);  // 14 px
        public static readonly Font Title     = new Font(BodyFamily,    10.5f, FontStyle.Regular, GraphicsUnit.Point);  // 14 px
        public static readonly Font Body      = new Font(BodyFamily,    10.5f, FontStyle.Regular, GraphicsUnit.Point);  // 14 px
        public static readonly Font Small     = new Font(BodyFamily,     9f,   FontStyle.Regular, GraphicsUnit.Point);  // 12 px
        public static readonly Font Value     = new Font(DisplayFamily, 13f,   FontStyle.Regular, GraphicsUnit.Point);  // 17 px
        public static readonly Font Mono      = new Font(MonoFamily,     9f,   FontStyle.Regular, GraphicsUnit.Point);  // 12 px
        public static readonly Font Icon      = new Font(IconFamily,    10f,   FontStyle.Regular, GraphicsUnit.Point);  // 13 px
        public static readonly Font IconSmall = new Font(IconFamily,     9f,   FontStyle.Regular, GraphicsUnit.Point);  // 12 px

        /// <summary>
        /// Show focus rings, because the last thing the user touched was the keyboard.
        ///
        /// CSS has :focus-visible; WinForms has Control.Focused, which is equally true after
        /// a mouse click. Drawing a ring on every click teaches people that the ring means
        /// nothing, so every widget sets this false on mouse-down and true on key-down, and
        /// paints its ring only when it is set. One shared flag rather than one per widget:
        /// "was the last input the keyboard" is a property of the session, not of a button.
        /// </summary>
        public static bool KeyboardNav
        {
            get { return _keyboardNav; }
            set { _keyboardNav = value; }
        }
        static bool _keyboardNav;

        /// <summary>
        /// Windows' UI effects, switched off. Honour it: the spinner is the only motion in
        /// the app, and someone who has asked the whole OS to stop animating should not
        /// have to watch an arc turn for the length of a profile write. The count beside it
        /// still climbs, so the button is visibly working either way.
        /// </summary>
        public static bool ReduceMotion
        {
            get { return !SystemInformation.UIEffectsEnabled; }
        }

        // Segoe Fluent Icons code points
        public const string GlyphInfo     = "";
        public const string GlyphChevDown = "";
        public const string GlyphChevRight= "";
        public const string GlyphRefresh  = "";
        public const string GlyphUndo     = "";
        public const string GlyphShield   = "";
        public const string GlyphWarn     = "";
        public const string GlyphCheck    = "";

        public static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color border)
        {
            FillRound(g, r, radius, fill, border, 1f);
        }

        /// <summary>
        /// The same, with a border you choose the weight of.
        ///
        /// A pen straddles the path it is drawn on, so half of anything thicker than a
        /// pixel falls outside the control and is clipped away by its own bounds - which
        /// is why simply widening the pen makes a 2px border look like a patchy 1px one.
        /// The fill still covers the whole shape; only the stroke is inset, by half the
        /// pen, and the radius comes in with it so the corners stay concentric.
        /// </summary>
        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color border, float width)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);

            int inset = (int)Math.Round((width - 1f) / 2f, MidpointRounding.AwayFromZero);
            Rectangle s = inset <= 0 ? r : new Rectangle(
                r.X + inset, r.Y + inset,
                Math.Max(1, r.Width - inset * 2), Math.Max(1, r.Height - inset * 2));

            using (GraphicsPath p = Round(s, Math.Max(1, radius - inset)))
            using (Pen pen = new Pen(border, width))
                g.DrawPath(pen, p);
        }

        /// <summary>
        /// The keyboard focus ring: 2px, solid, drawn outside the control's own shape.
        ///
        /// It was 1px dotted in the accent green, inset two pixels - which on a dark ground
        /// at a laptop's pixel density was close to invisible, and was drawn on mouse focus
        /// as well, so it taught you to ignore it. Solid and 2px is the minimum that reads
        /// as an indicator rather than an artefact.
        /// </summary>
        /// <summary>
        /// A chevron, centred on (x, cy), pointing down when open and right when shut.
        ///
        /// Drawn rather than set in Segoe Fluent Icons. That font renders nothing for
        /// these code points on some installs - which left every collapsible section
        /// looking like a plain label and every dropdown looking like a text field, since
        /// the one mark saying otherwise was not being painted. Two lines cannot fail.
        /// </summary>
        public static void Chevron(Graphics g, float x, float cy, float size, bool down, Color c)
        {
            float h = size / 2f;
            using (Pen p = new Pen(c, 1.8f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                PointF[] pts = down
                    ? new PointF[] {
                        new PointF(x, cy - h / 2f),
                        new PointF(x + h, cy + h / 2f),
                        new PointF(x + size, cy - h / 2f) }
                    : new PointF[] {
                        new PointF(x + h / 2f, cy - h),
                        new PointF(x + h / 2f + h, cy),
                        new PointF(x + h / 2f, cy + h) };
                g.DrawLines(p, pts);
            }
        }

        public static void FocusRing(Graphics g, Rectangle bounds, int radius)
        {
            Rectangle r = new Rectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(Save, 2f))
                g.DrawPath(pen, p);
        }

        public static void Str(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, x, y);
        }

        public static void StrRight(Graphics g, string s, Font f, Color c, float right, float y)
        {
            SizeF sz = g.MeasureString(s, f);
            Str(g, s, f, c, right - sz.Width, y);
        }

        public static float TextW(Graphics g, string s, Font f)
        {
            return g.MeasureString(s, f).Width;
        }
    }
}
