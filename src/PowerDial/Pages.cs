using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>
    /// The name of the section you are in, and one line saying what it is for.
    ///
    /// The old window had no page titles because it had no pages - it was one column with
    /// headings scattered down it. Now that the sidebar opens one section at a time, the
    /// heading that used to sit inside the column belongs at the top of the page instead.
    /// </summary>
    public sealed class PageTitle : Control
    {
        public string Title = "";
        public string Subtitle = "";

        public PageTitle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Ink;
            Height = 76;
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);
            Theme.Str(g, Title, Theme.Page, Theme.Lead, 2, 10);
            if (Subtitle.Length > 0)
            {
                RectangleF box = new RectangleF(2, 48, Math.Max(80, Width - 20), 24);
                using (SolidBrush b = new SolidBrush(Theme.Dim))
                    g.DrawString(Subtitle, Theme.Body, b, box);
            }
        }
    }

    /// <summary>
    /// One power mode, as a card rather than a button in a row of four.
    ///
    /// A row of identical buttons said nothing about what any of them would do: you pressed
    /// one and found out. Each card now states the trade in a sentence, lists what it
    /// actually changes, and shows what this laptop measured while it was in use - so the
    /// choice is made before the click rather than after it.
    /// </summary>
    public sealed class ModeCard : Card
    {
        /// <summary>Preset.Code, so the card can be matched to the machine read-back.</summary>
        public int Code;

        public string ModeName = "";
        public string Blurb = "";

        /// <summary>What this mode changes, in user-facing words.</summary>
        public readonly List<string> Chips = new List<string>();

        /// <summary>The mode the machine currently matches.</summary>
        public bool Current;

        /// <summary>Shown on a mode that costs runtime, so the trade is visible before
        /// it is chosen and not only afterwards.</summary>
        public string WarnText = "";

        /// <summary>Measured on this PC while this mode was in use. Null until earned.</summary>
        public double? Watts;
        public int Minutes;

        public readonly PillButton Use = new PillButton();

        const int TextX = 62;
        const int RightW = 216;

        public ModeCard()
        {
            Height = 150;
            Lift = false;
            AccessibleRole = AccessibleRole.Grouping;

            Use.Text = "Use this mode";
            Use.BackColor = Theme.Panel;
            Use.Size = new Size(150, 34);
            Controls.Add(Use);
        }

        public void Describe()
        {
            string s = ModeName + ". " + Blurb;
            if (Current) s = ModeName + ", in use now. " + Blurb;
            if (WarnText.Length > 0) s += " " + WarnText + ".";
            s += Watts.HasValue
                ? " Measured here at " + Watts.Value.ToString("0.0") + " watts over " + Minutes + " minutes."
                : " Not measured on this PC yet.";
            AccessibleName = s;
            Use.AccessibleName = "Use " + ModeName;
        }

        /// <summary>The blurb wraps, so the chips and the height below it can only be
        /// placed once it has been measured against the width the card actually got.</summary>
        public void Reflow(Graphics g)
        {
            Border = Current ? Theme.Save : Theme.Edge;
            EdgeWidth = Current ? Theme.ActiveEdge : 1f;
            Use.Visible = !Current;

            int textW = Math.Max(120, Width - TextX - RightW - 20);
            float blurbH = g.MeasureString(Blurb, Theme.Body, textW).Height;
            int chipsY = (int)(48 + blurbH) + 14;
            Height = Math.Max(146, chipsY + 26 + 20);

            Use.Location = new Point(Width - Use.Width - 24, Height - Use.Height - 20);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            // The indicator carries the state as a shape as well as a colour: filled with a
            // tick when it is the one in use, an empty ring when it is not.
            Rectangle dot = new Rectangle(24, 22, 22, 22);
            if (Current)
            {
                using (SolidBrush b = new SolidBrush(Theme.Save)) g.FillEllipse(b, dot);
                StatusCard.Tick(g, dot.X + 5, dot.Y + 6, 12, Theme.Ink);
            }
            else
            {
                using (Pen pn = new Pen(Theme.Line, 1.6f)) g.DrawEllipse(pn, dot);
            }

            float x = TextX;
            SizeF ns = g.MeasureString(ModeName, Theme.Section);
            Theme.Str(g, ModeName, Theme.Section, Theme.Text, x, 20);
            x += ns.Width + 10;

            if (Current) StatusCard.Pill(g, x, 24, "IN USE NOW", Theme.Save);
            else if (WarnText.Length > 0) StatusCard.Pill(g, x, 24, WarnText, Theme.Spend);

            int textW = Math.Max(120, Width - TextX - RightW - 20);
            RectangleF bb = new RectangleF(TextX, 48, textW, 60);
            using (SolidBrush b = new SolidBrush(Theme.Dim))
                g.DrawString(Blurb, Theme.Body, b, bb);

            float blurbH = g.MeasureString(Blurb, Theme.Body, textW).Height;
            float cx = TextX;
            float cy = 48 + blurbH + 14;
            foreach (string chip in Chips)
            {
                SizeF cs = g.MeasureString(chip, Theme.Small);
                int cw = (int)cs.Width + 18;
                if (cx + cw > TextX + textW) break;          // never run under the right column
                Rectangle r = new Rectangle((int)cx, (int)cy, cw, 24);
                Theme.FillRound(g, r, 4, Theme.Inset);
                Theme.Str(g, chip, Theme.Small, Theme.Dim, cx + 9, cy + 4);
                cx += cw + 8;
            }

            // What it actually cost here, or an honest blank.
            float rx = Width - 24;
            Theme.StrRight(g, "Measured here", Theme.Small, Theme.Mute, rx, 20);
            if (Watts.HasValue)
            {
                Theme.StrRight(g, Watts.Value.ToString("0.0") + " W", Theme.Stat, Theme.Text, rx, 38);
                Theme.StrRight(g, "over " + Minutes + " min", Theme.Small, Theme.Mute, rx, 66);
            }
            else
            {
                Theme.StrRight(g, "Not measured yet", Theme.Body, Theme.Dim, rx, 40);
                Theme.StrRight(g, "use it for five minutes", Theme.Small, Theme.Mute, rx, 64);
            }
        }
    }

    /// <summary>
    /// The battery, stated once, at the top of Overview.
    ///
    /// Charge, what is left, what is being drawn and how healthy the pack is were four
    /// figures in three places, and two of them disagreed about the time left by a minute.
    /// They describe one thing, so they are one card: the charge on the left, and the three
    /// measurements that follow from it in a row beside it, each with the sentence that
    /// says where it came from.
    /// </summary>
    public sealed class StatusCard : Card
    {
        public bool HasBattery = true;
        public bool OnAc, Charging;
        public int ChargePct;
        public int? Mwh, FullMwh;

        /// <summary>Measured, from the recorded average - null until it has been earned.</summary>
        public double? HoursLeft;

        /// <summary>The live 60-second reading, null while the window fills.</summary>
        public double? Watts;
        public int MeasuringLeft;

        public double? Health;

        public StatusCard()
        {
            Height = 150;
            AccessibleRole = AccessibleRole.Grouping;
        }

        /// <summary>One sentence carrying everything the card draws, because a painted card
        /// is silent to a screen reader however clear it looks.</summary>
        public void Describe()
        {
            if (!HasBattery) { AccessibleName = "This PC has no battery"; return; }
            string s = ChargePct + "% charge, " + (OnAc ? "plugged in" : "on battery");
            if (HoursLeft.HasValue) s += ". About " + Hm(HoursLeft.Value) + " left";
            if (Watts.HasValue) s += ". Using " + Watts.Value.ToString("0.0") + " watts";
            if (Health.HasValue) s += ". Battery health " + Health.Value.ToString("0") + "%";
            AccessibleName = s;
        }

        internal static string Hm(double hours)
        {
            if (hours < 0) hours = 0;
            int m = (int)Math.Round(hours * 60);
            return (m / 60) + " h " + (m % 60) + " m";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            if (!HasBattery)
            {
                Theme.Str(g, "Mains power", Theme.Head, Theme.Text, 24, 22);
                Theme.Str(g, "This PC has no battery, so there is no runtime to extend. " +
                             "The power settings and the process list still apply.",
                          Theme.Body, Theme.Dim, 24, 52);
                return;
            }

            // ---- the charge itself, left block ----------------------------------------
            string pct = ChargePct.ToString();
            SizeF ps = g.MeasureString(pct, Theme.Readout);
            SizeF sgn = g.MeasureString("%", Theme.Stat);
            Theme.Str(g, pct, Theme.Readout, Theme.Text, 24, 16);

            // Sat on the number's baseline rather than a fixed offset from its top, which
            // drifts the moment the display scaling or the digit count changes.
            Theme.Str(g, "%", Theme.Stat, Theme.Dim, 24 + ps.Width - 6, 16 + ps.Height - sgn.Height - 6);

            string state = OnAc ? (Charging ? "Charging" : "Plugged in") : "On battery";
            Theme.Str(g, state, Theme.Body, Theme.Text, 24, 74);

            Rectangle track = new Rectangle(24, 100, 186, 8);
            Theme.FillRound(g, track, 4, Theme.Inset);
            int w = (int)Math.Round(track.Width * Math.Max(0, Math.Min(100, ChargePct)) / 100.0);
            if (w > 0)
                Theme.FillRound(g, new Rectangle(track.X, track.Y, w, 8), 4,
                                ChargePct <= 20 ? Theme.Spend : Theme.Save);

            if (Mwh.HasValue && FullMwh.HasValue)
                Theme.Str(g, Mwh.Value.ToString("n0") + " of " + FullMwh.Value.ToString("n0") + " mWh",
                          Theme.Small, Theme.Mute, 24, 116);

            using (Pen p = new Pen(Theme.Edge, 1f))
                g.DrawLine(p, 244, 22, 244, Height - 22);

            // ---- the three measurements that follow from it ---------------------------
            int x = 272;
            int col = Math.Max(150, (Width - 272 - 24) / 3);

            string left = OnAc ? "Paused" : (HoursLeft.HasValue ? Hm(HoursLeft.Value) : "Measuring");
            Figure(g, x, "Time left", left, OnAc
                ? "Readings pause while the laptop is charging."
                : (HoursLeft.HasValue
                    ? "Estimated from your measured usage. It moves as your workload changes."
                    : "Needs a few recorded minutes on battery before it can say."), Theme.Text, col);

            // "16 s" in the place a wattage goes reads as the answer to "how much am I
            // using" - so the figure stays a dash until there is one, and the countdown
            // goes in the sentence underneath where it belongs.
            string draw = Watts.HasValue ? Watts.Value.ToString("0.0") + " W" : "--";
            string drawNote = Watts.HasValue
                ? "Averaged over the last minute, measured on this laptop."
                : (MeasuringLeft > 0
                    ? "Measuring: the first reading lands in " + MeasuringLeft + " seconds."
                    : "Measuring how much power this laptop draws.");
            Figure(g, x + col, "Using now", draw, drawNote,
                   Watts.HasValue ? Theme.Text : Theme.Dim, col);

            if (Health.HasValue)
            {
                bool ageing = Health.Value < 80;
                string hv = Health.Value.ToString("0") + "%";
                float fx = x + col * 2;
                Figure(g, fx, "Battery health", hv,
                       "Holds " + hv + " of the charge it did when new.",
                       ageing ? Theme.Spend : Theme.Save, col);

                // A word as well as the colour: "amber" is not a status anyone can hear.
                if (ageing)
                {
                    // Centred on the figure it qualifies, not hung at a fixed y: the pill
                    // is set in 12px text and the figure in 21px, so a shared top edge put
                    // the badge a third of a line below the number it belongs to - which
                    // is what made it look like it had come loose.
                    SizeF hs = g.MeasureString(hv, Theme.Stat);
                    SizeF ts = g.MeasureString("Ageing", Theme.Small);
                    float pw = ts.Width + 14, ph = ts.Height + 4;
                    float py = 44 + (hs.Height - ph) / 2f;
                    float px = fx + hs.Width + 2;

                    // and never past the end of its own column
                    float limit = fx + col - 18 - pw;
                    if (px > limit) px = Math.Max(fx, limit);

                    Pill(g, px, py, "Ageing", Theme.Spend);
                }
            }
            else
            {
                Figure(g, x + col * 2, "Battery health", "Unknown",
                       "Windows does not report a reliable original capacity for this pack.",
                       Theme.Dim, col);
            }
        }

        /// <summary>One labelled measurement: what it is, the figure, and where it came
        /// from. The provenance line is not decoration - it is what stops a measured
        /// number being read as a promise.</summary>
        static void Figure(Graphics g, float x, string label, string value, string note, Color c, int col)
        {
            Theme.Str(g, label, Theme.Small, Theme.Dim, x, 24);
            Theme.Str(g, value, Theme.Stat, c, x, 44);
            RectangleF box = new RectangleF(x, 82, col - 18, 46);
            using (SolidBrush b = new SolidBrush(Theme.Mute))
                g.DrawString(note, Theme.Small, b, box);
        }

        /// <summary>
        /// A tick, drawn rather than set in Segoe Fluent Icons.
        ///
        /// The icon font renders nothing at all for some code points on some installs -
        /// the advice card shipped with an empty square where its glyph should have been -
        /// and three straight lines cannot fail to draw.
        /// </summary>
        internal static void Tick(Graphics g, float x, float y, float size, Color c)
        {
            using (Pen p = new Pen(c, Math.Max(1.6f, size / 7f)))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new PointF[] {
                    new PointF(x, y + size * 0.55f),
                    new PointF(x + size * 0.38f, y + size * 0.92f),
                    new PointF(x + size, y + size * 0.12f)
                });
            }
        }

        /// <summary>An exclamation, for the same reason.</summary>
        internal static void Bang(Graphics g, float x, float y, float size, Color c)
        {
            float w = Math.Max(2f, size / 8f);
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillRectangle(b, x + size / 2f - w / 2f, y, w, size * 0.62f);
                g.FillRectangle(b, x + size / 2f - w / 2f, y + size * 0.80f, w, w);
            }
        }

        internal static void Pill(Graphics g, float x, float y, string text, Color c)
        {
            SizeF ts = g.MeasureString(text, Theme.Small);
            Rectangle r = new Rectangle((int)x, (int)y, (int)ts.Width + 14, (int)ts.Height + 4);
            Theme.FillRound(g, r, 4, Color.FromArgb(38, c));
            Theme.Str(g, text, Theme.Small, c, r.X + 7, r.Y + 2);
        }
    }

    /// <summary>
    /// The one thing worth doing, and what it will change.
    ///
    /// The old window opened on a button called "Optimise my battery" with no statement of
    /// what it would do, beside an Undo of equal weight. This says what is wrong, what
    /// pressing the button changes, and - when the app cannot make the change itself - says
    /// so plainly instead of offering a button that quietly opens a Windows page.
    /// </summary>
    public sealed class AdviceCard : Card
    {
        public string Title = "";
        public string Body = "";

        /// <summary>The honest caveat: shown when this is something Windows has to do.</summary>
        public string Note = "";

        /// <summary>Everything this app can set is already set - drawn under the rule.</summary>
        public bool AllSet;
        public string FooterText = "";

        /// <summary>Amber for something worth changing, green when there is nothing left.</summary>
        public bool Warn = true;

        public readonly PillButton Act = new PillButton();
        public readonly PillButton Recheck = new PillButton();

        public AdviceCard()
        {
            Height = 190;
            AccessibleRole = AccessibleRole.Grouping;

            Act.Primary = true;
            Act.BackColor = Theme.Panel;
            Act.Size = new Size(250, 38);
            Controls.Add(Act);

            Recheck.Text = "Check again";
            Recheck.Glyph = Theme.GlyphRefresh;
            Recheck.BackColor = Theme.Panel;
            Recheck.Size = new Size(150, 38);
            Controls.Add(Recheck);
        }

        public void Describe()
        {
            AccessibleName = Title;
            AccessibleDescription = Body + (Note.Length > 0 ? " " + Note : "");
        }

        /// <summary>Buttons sit under the copy, and the copy reflows with the card, so the
        /// row has to be placed after the text has been measured rather than at a fixed y.</summary>
        public void Reflow(Graphics g)
        {
            float bodyH = TextHeight(g, Body, Theme.Body, Width - 92);
            float noteH = Note.Length > 0 ? TextHeight(g, Note, Theme.Small, Width - 92) + 6 : 0;
            int y = (int)(58 + bodyH + noteH) + 10;
            Act.Location = new Point(68, y);

            // With nothing to apply, Act is hidden - and Check again was left sitting in
            // the middle of the card, at the offset the missing button had reserved.
            Recheck.Location = new Point(Act.Visible ? 68 + Act.Width + 10 : 68, y);
            Height = y + Act.Height + (AllSet ? 52 : 22);
        }

        static float TextHeight(Graphics g, string s, Font f, int width)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return g.MeasureString(s, f, Math.Max(80, width)).Height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            Color accent = Warn ? Theme.Spend : Theme.Save;

            // the glyph tile, so the state is not carried by the colour of a word alone
            Rectangle tile = new Rectangle(24, 24, 32, 32);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(38, accent)))
                g.FillRectangle(b, tile);
            if (Warn) StatusCard.Bang(g, tile.X + 10, tile.Y + 8, 16, accent);
            else StatusCard.Tick(g, tile.X + 8, tile.Y + 9, 16, accent);

            Theme.Str(g, Title, Theme.Head, Theme.Text, 68, 22);

            float y = 58;
            RectangleF box = new RectangleF(68, y, Math.Max(80, Width - 92), 80);
            using (SolidBrush b = new SolidBrush(Theme.Dim))
                g.DrawString(Body, Theme.Body, b, box);
            y += TextHeight(g, Body, Theme.Body, Width - 92);

            if (Note.Length > 0)
            {
                RectangleF nb = new RectangleF(68, y + 4, Math.Max(80, Width - 92), 44);
                using (SolidBrush b = new SolidBrush(Theme.Mute))
                    g.DrawString(Note, Theme.Small, b, nb);
            }

            if (AllSet && FooterText.Length > 0)
            {
                int ry = Height - 36;
                using (Pen p = new Pen(Theme.Edge, 1f))
                    g.DrawLine(p, 24, ry, Width - 24, ry);
                StatusCard.Tick(g, 24, ry + 14, 12, Theme.Save);
                Theme.Str(g, FooterText, Theme.Small, Theme.Dim, 46, ry + 12);
            }
        }
    }

    /// <summary>
    /// Which mode is in effect, read from the machine rather than from what the app last
    /// wrote, and a way through to change it.
    /// </summary>
    public sealed class ModeSummaryCard : Card
    {
        public string Mode = "";
        public string Blurb = "";
        public bool Known;

        /// <summary>Measured while this mode was in use. Null until it has earned one.</summary>
        public double? Watts;
        public int Minutes;

        public readonly PillButton Change = new PillButton();

        public ModeSummaryCard()
        {
            Height = 206;
            AccessibleRole = AccessibleRole.Grouping;

            Change.Text = "Change";
            Change.BackColor = Theme.Panel;
            Change.Size = new Size(110, 34);
            Controls.Add(Change);
        }

        public void Describe()
        {
            AccessibleName = Known ? "Power mode: " + Mode : "Power mode: custom settings";
            AccessibleDescription = Blurb;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Change.Location = new Point(Math.Max(24, Width - Change.Width - 20), 18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            Theme.Str(g, "Power mode", Theme.Section, Theme.Text, 20, 20);

            Rectangle box = new Rectangle(20, 56, Math.Max(80, Width - 40), 68);
            Theme.FillRound(g, box, 6, Known ? Color.FromArgb(30, Theme.Save) : Theme.Inset,
                            Known ? Theme.Save : Theme.Edge);

            float tx = box.X + 16;
            if (Known)
            {
                StatusCard.Tick(g, tx, box.Y + 14, 14, Theme.Save);
                tx += 24;
            }

            Theme.Str(g, Known ? Mode : "Custom settings", Theme.Body, Theme.Text, tx, box.Y + 9);
            RectangleF bb = new RectangleF(tx, box.Y + 30, box.Width - (tx - box.X) - 14, 32);
            using (SolidBrush b = new SolidBrush(Theme.Dim))
                g.DrawString(Blurb, Theme.Small, b, bb);

            // The lower half used to be empty. What it cost is measured already, so it
            // goes here rather than being left to the Insights page alone.
            if (Watts.HasValue)
            {
                Theme.Str(g, "Measured here", Theme.Small, Theme.Mute, 20, 136);
                Theme.Str(g, Watts.Value.ToString("0.0") + " W", Theme.Stat, Theme.Text, 20, 154);
                Theme.Str(g, "over " + Minutes + " min on battery", Theme.Small, Theme.Mute, 78, 160);
            }
            else
            {
                Theme.Str(g, "Not measured yet", Theme.Body, Theme.Dim, 20, 140);
                Theme.Str(g, "use this mode for five minutes on battery", Theme.Small, Theme.Mute, 20, 162);
            }

            Theme.Str(g, "Read from Windows, not assumed.", Theme.Small, Theme.Mute, 20, Height - 26);
        }
    }

    /// <summary>
    /// What is actually keeping the processor busy.
    ///
    /// It was called "Where your watts go", which is not what it measures: these are
    /// processor seconds, and attributing watts to a process is something this app
    /// deliberately refuses to do. The heading now says what the bars are, and only the
    /// busiest is drawn in amber - four amber bars made four ordinary processes look like
    /// four problems.
    /// </summary>
    public sealed class TopAppsCard : Card
    {
        public sealed class Row
        {
            public string Name = "";
            public double Seconds;
            public bool Background;
        }

        public readonly List<Row> Rows = new List<Row>();
        public string Empty = "";
        public int WindowSeconds;

        public readonly PillButton SeeAll = new PillButton();

        public TopAppsCard()
        {
            // Four rows at 22px from y=72, then the footnote: 168 put the footnote on top
            // of the fourth row.
            Height = 206;
            AccessibleRole = AccessibleRole.Grouping;

            SeeAll.Text = "See all";
            SeeAll.BackColor = Theme.Panel;
            SeeAll.Size = new Size(104, 34);
            Controls.Add(SeeAll);
        }

        public void Describe()
        {
            if (Rows.Count == 0) { AccessibleName = "Apps using the most power. " + Empty; return; }
            string s = "Apps using the most power. ";
            for (int i = 0; i < Rows.Count; i++)
                s += Rows[i].Name + " " + Rows[i].Seconds.ToString("0.0") + " seconds. ";
            AccessibleName = s;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            SeeAll.Location = new Point(Math.Max(24, Width - SeeAll.Width - 20), 18);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            Theme.Str(g, "Apps using the most power", Theme.Section, Theme.Text, 20, 20);
            Theme.Str(g, "Processor time used over the last " + Math.Max(1, WindowSeconds) +
                         " seconds. More time means more drain.",
                      Theme.Small, Theme.Mute, 20, 44);

            if (Rows.Count == 0)
            {
                Theme.Str(g, Empty, Theme.Small, Theme.Dim, 20, 76);
                return;
            }

            double max = 0;
            foreach (Row r in Rows) if (r.Seconds > max) max = r.Seconds;
            if (max <= 0) max = 1;

            const int NameW = 128;
            const int FigW = 74;
            int barMax = Math.Max(30, Width - 40 - NameW - FigW - 16);

            int y = 72;
            for (int i = 0; i < Rows.Count && i < 4; i++)
            {
                Row r = Rows[i];
                Theme.Str(g, r.Name, Theme.Small, Theme.Text, 20, y);
                if (r.Background)
                {
                    float nx = 20 + g.MeasureString(r.Name, Theme.Small).Width + 6;
                    if (nx < 20 + NameW - 24)
                        Theme.Str(g, "BG", Theme.Small, Theme.Mute, nx, y);
                }

                Rectangle track = new Rectangle(20 + NameW, y + 4, barMax, 8);
                Theme.FillRound(g, track, 4, Theme.Inset);
                int bw = (int)Math.Round(r.Seconds / max * barMax);
                if (bw < 2) bw = 2;
                Theme.FillRound(g, new Rectangle(track.X, track.Y, bw, 8), 4,
                                i == 0 ? Theme.Spend : Theme.Data);

                Theme.StrRight(g, r.Seconds.ToString("0.0") + " s", Theme.Small, Theme.Dim,
                               Width - 20, y);
                y += 22;
            }

            Theme.Str(g, "BG marks an app with no window open.", Theme.Small, Theme.Mute, 20, Height - 26);
        }
    }

    /// <summary>
    /// What the last change was actually worth, as three measured figures rather than a
    /// paragraph.
    ///
    /// This used to be two wrapped sentences at the bottom of a card that also held four
    /// mode buttons, a comparison table and a machine summary - the one number anybody
    /// came to this app for, set in 12px grey under everything else. It is the headline of
    /// Insights now, and the arithmetic is unchanged: an average measured before the
    /// change against an average measured since, both on this PC.
    ///
    /// Nothing here is predicted. Until enough minutes have accumulated after a change it
    /// says how many more it needs, rather than showing a figure it has not earned.
    /// </summary>
    public sealed class SavingCard : Card
    {
        /// <summary>No change recorded yet, so there is nothing to compare against.</summary>
        public bool Idle = true;

        /// <summary>A change is recorded but not enough has been measured since.</summary>
        public bool Waiting;

        public double Before, After;
        public int Minutes;

        /// <summary>The plain-language consequence - hours from a full charge, usually.</summary>
        public string Detail = "";

        /// <summary>Shown in place of the figures while Idle or Waiting.</summary>
        public string Message = "";

        public readonly PillButton Undo = new PillButton();

        public SavingCard()
        {
            Height = 178;
            AccessibleRole = AccessibleRole.Grouping;

            // No glyph: the icon font is not reliably present on every install, and an
            // empty square beside "Undo" reads as a broken button.
            Undo.Text = "Undo that change";
            Undo.BackColor = Theme.Panel;
            Undo.Size = new Size(168, 34);
            Undo.Visible = false;
            Controls.Add(Undo);
        }

        /// <summary>
        /// Watts saved - positive is less draw than before. A method rather than a public
        /// property: a public property on a Control is something the designer will try to
        /// serialise, and everything else on these cards is a plain field for the same
        /// reason.
        /// </summary>
        double Delta() { return Before - After; }

        /// <summary>
        /// Amber for more draw than before, green for less, neutral for no measurable
        /// difference. A green figure here is the only place in the app that means
        /// "this worked", so it is not spent on a change that did nothing.
        /// </summary>
        Color DeltaColor()
        {
            if (Math.Abs(Delta()) < 0.15) return Theme.Dim;
            return Delta() > 0 ? Theme.Save : Theme.Spend;
        }

        public void Fit()
        {
            Height = (Idle || Waiting) ? 132 : 178;
            Undo.Visible = !Idle;
            Describe();
        }

        public void Describe()
        {
            if (Idle || Waiting) { AccessibleName = "What your last change was worth. " + Message; return; }
            AccessibleName = "What your last change was worth. Before " +
                             Before.ToString("0.0") + " watts, now " + After.ToString("0.0") +
                             " watts, measured over " + Minutes + " minutes. " + Detail;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Undo.Location = new Point(Math.Max(24, Width - Undo.Width - 20), 16);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            Theme.Str(g, "What your last change was worth", Theme.Section, Theme.Save, 20, 20);
            Theme.Str(g, "Measured on this PC - an average before the change against an average since.",
                      Theme.Small, Theme.Mute, 20, 44);

            if (Idle || Waiting)
            {
                using (SolidBrush b = new SolidBrush(Theme.Dim))
                    g.DrawString(Message, Theme.Body, b,
                                 new RectangleF(20, 76, Math.Max(120, Width - 220), 44));
                return;
            }

            // Three columns, not a sentence: before, after, and the difference between
            // them, each under a label saying which it is.
            int[] x = { 20, 190, 360 };
            Stat(g, x[0], "Before", Before.ToString("0.0") + " W", Theme.Text);
            Stat(g, x[1], "Now", After.ToString("0.0") + " W", Theme.Text);

            string d = Math.Abs(Delta()) < 0.15
                ? "no change"
                : (Delta() > 0 ? "-" : "+") + Math.Abs(Delta()).ToString("0.0") + " W";
            Stat(g, x[2], "Difference", d, DeltaColor());

            Theme.Str(g, "measured over " + Minutes + " minutes since", Theme.Small, Theme.Mute, 20, 146);

            if (Detail.Length > 0)
            {
                float dx = 20 + Theme.TextW(g, "measured over " + Minutes + " minutes since", Theme.Small) + 14;
                if (dx < Width - 120)
                    Theme.Str(g, Detail, Theme.Small, DeltaColor(), dx, 146);
            }
        }

        static void Stat(Graphics g, int x, string label, string value, Color c)
        {
            Theme.Str(g, label, Theme.Small, Theme.Mute, x, 82);
            Theme.Str(g, value, Theme.Stat, c, x, 102);
        }
    }

    /// <summary>
    /// What each mode has actually cost, one row per mode.
    ///
    /// The honest version of "which mode is better": what this laptop really drew in each
    /// one, over a stated number of recorded minutes, rather than a prediction from the
    /// settings. A mode with too little recorded says so instead of drawing a bar.
    ///
    /// Bars are scaled against the thirstiest measured mode, so a longer bar reads as
    /// "costs more" and not as a share of anything. Only two rows are ever coloured - the
    /// cheapest measured mode and the dearest - because colouring all four made a normal
    /// spread of results look like four verdicts.
    /// </summary>
    public sealed class ModeCostCard : Card
    {
        public sealed class Row
        {
            public string Name = "";
            public int Minutes;
            public double Watts;
            public double? Hours;
            public bool Current;
        }

        public readonly List<Row> Rows = new List<Row>();
        public string Empty = "";

        const int Pitch = 34;
        const int FirstRowY = 84;      // not "Top": Control has one of those

        public ModeCostCard()
        {
            Height = 240;
            AccessibleRole = AccessibleRole.Grouping;
        }

        public void Fit()
        {
            Height = Rows.Count == 0 ? 148 : FirstRowY + Rows.Count * Pitch + 22;
            Describe();
        }

        public void Describe()
        {
            string s = "What each mode has cost you. ";
            if (Rows.Count == 0) s += Empty;
            foreach (Row r in Rows)
                s += r.Minutes < 5
                    ? r.Name + ", not measured yet. "
                    : r.Name + ", " + r.Watts.ToString("0.0") + " watts over " + r.Minutes + " minutes. ";
            AccessibleName = s;
        }

        int Best()
        {
            int at = -1;
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].Minutes >= 5 && (at < 0 || Rows[i].Watts < Rows[at].Watts)) at = i;
            return at;
        }

        int Worst()
        {
            int at = -1;
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].Minutes >= 5 && (at < 0 || Rows[i].Watts > Rows[at].Watts)) at = i;
            return at;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Quality(g);

            Theme.Str(g, "What each mode has cost you", Theme.Section, Theme.Save, 20, 20);
            Theme.Str(g, "Recorded while you were on battery. A mode you have not used long enough says so.",
                      Theme.Small, Theme.Mute, 20, 44);

            int best = Best(), worst = Worst();
            if (best < 0)
            {
                using (SolidBrush b = new SolidBrush(Theme.Dim))
                    g.DrawString(Empty, Theme.Body, b, new RectangleF(20, 78, Math.Max(120, Width - 40), 48));
                return;
            }

            double max = 0;
            foreach (Row r in Rows) if (r.Minutes >= 5 && r.Watts > max) max = r.Watts;
            if (max <= 0) max = 1;

            const int NameW = 150;
            const int FigW = 250;
            int barMax = Math.Max(30, Width - 40 - NameW - FigW - 16);

            int y = FirstRowY;
            for (int i = 0; i < Rows.Count; i++)
            {
                Row r = Rows[i];
                Theme.Str(g, r.Name, Theme.Body, Theme.Text, 20, y);

                if (r.Current)
                {
                    float nx = 20 + Theme.TextW(g, r.Name, Theme.Body) + 8;
                    if (nx < 20 + NameW - 34)
                        Theme.Str(g, "in use", Theme.Small, Theme.Mute, nx, y + 2);
                }

                if (r.Minutes < 5)
                {
                    Theme.Str(g, "not measured yet", Theme.Small, Theme.Mute, 20 + NameW, y + 2);
                    y += Pitch;
                    continue;
                }

                Rectangle track = new Rectangle(20 + NameW, y + 5, barMax, 9);
                Theme.FillRound(g, track, 4, Theme.Inset);
                int bw = (int)Math.Round(r.Watts / max * barMax);
                if (bw < 2) bw = 2;
                Color c = i == best ? Theme.Save : (i == worst ? Theme.Spend : Theme.Data);
                Theme.FillRound(g, new Rectangle(track.X, track.Y, bw, 9), 4, c);

                string fig = r.Watts.ToString("0.0") + " W";
                if (r.Hours.HasValue) fig += "   ·   " + Hrs(r.Hours.Value) + " a charge";
                fig += "   ·   " + r.Minutes + " min";
                Theme.StrRight(g, fig, Theme.Small, i == best ? Theme.Save : Theme.Dim, Width - 20, y + 2);

                y += Pitch;
            }

            if (best >= 0)
                Theme.Str(g, "Cheapest so far: " + Rows[best].Name + ".", Theme.Small, Theme.Mute, 20, Height - 26);
        }

        static string Hrs(double h)
        {
            int t = (int)Math.Round(Math.Max(0, h) * 60);
            return (t / 60) + "h " + (t % 60).ToString("00") + "m";
        }
    }
}
