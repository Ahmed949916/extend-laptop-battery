using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PowerDial
{
    /// <summary>
    /// Instrument-panel palette and type.
    ///
    /// Two accents, and both of them mean something: amber is energy leaving, green is
    /// energy kept. The watts readout switches between them based on the actual
    /// measurement, so colour is carrying data rather than decorating the window.
    /// </summary>
    public static class Theme
    {
        public static readonly Color Ink    = Color.FromArgb(0x0E, 0x11, 0x16);  // ground
        public static readonly Color Panel  = Color.FromArgb(0x16, 0x1B, 0x23);  // card
        public static readonly Color Inset  = Color.FromArgb(0x1E, 0x24, 0x2E);  // tracks, log
        public static readonly Color Edge   = Color.FromArgb(0x2A, 0x32, 0x3E);  // hairline
        public static readonly Color Text   = Color.FromArgb(0xDD, 0xE3, 0xEC);
        public static readonly Color Dim    = Color.FromArgb(0x7C, 0x87, 0x98);
        public static readonly Color Spend  = Color.FromArgb(0xE8, 0xA3, 0x3D);  // watts going out
        public static readonly Color Save   = Color.FromArgb(0x4F, 0xD6, 0xA9);  // watts kept
        public static readonly Color Alert  = Color.FromArgb(0xF2, 0x70, 0x5E);  // regression

        // Two more steps up the same neutral ramp - Ink, Panel, Inset, Raise - not a third
        // accent. They exist so "this surface is raised" and "this tab is the current one"
        // can be said without spending amber or green, which carry meaning about energy and
        // must not be diluted into decoration.
        public static readonly Color Raise  = Color.FromArgb(0x28, 0x30, 0x3C);  // hover, current tab
        public static readonly Color Hair   = Color.FromArgb(0x36, 0x3F, 0x4C);  // lit top edge of a card

        // Bahnschrift is Windows' DIN derivative - an engineering face, not the Segoe
        // default. Used for anything numeric or instrument-like.
        public const string DisplayFamily = "Bahnschrift SemiBold Condensed";
        public const string LabelFamily   = "Bahnschrift SemiBold";
        public const string BodyFamily    = "Segoe UI Variable Text";
        public const string MonoFamily    = "Cascadia Mono";
        public const string IconFamily    = "Segoe Fluent Icons";

        public static readonly Font Readout   = new Font(DisplayFamily, 44f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Stat      = new Font(DisplayFamily, 21f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Section   = new Font(LabelFamily, 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Head      = new Font(LabelFamily, 16f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Tab       = new Font(BodyFamily, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Title     = new Font(BodyFamily, 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Body      = new Font(BodyFamily, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Small     = new Font(BodyFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Value     = new Font(DisplayFamily, 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Mono      = new Font(MonoFamily, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font Icon      = new Font(IconFamily, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font IconSmall = new Font(IconFamily, 9f, FontStyle.Regular, GraphicsUnit.Pixel);

        // Segoe Fluent Icons code points
        public const string GlyphInfo     = "";
        public const string GlyphChevDown = "";
        public const string GlyphChevRight= "";
        public const string GlyphRefresh  = "";
        public const string GlyphUndo     = "";
        public const string GlyphShield   = "";
        public const string GlyphWarn     = "";
        public const string GlyphCheck    = "";

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
            using (GraphicsPath p = Round(r, radius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
            }
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
