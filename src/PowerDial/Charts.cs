using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>
    /// The measured model of this laptop. Every constant here came from timing the
    /// battery counter on this machine, not from a spec sheet.
    /// </summary>
    public static class Model
    {
        /// <summary>Backlight cost per brightness point. From 12.00 W at 99% against
        /// 9.24 W at 30%, same settings and workload: 2.76 W over 69 points.</summary>
        public const double WattsPerPoint = 0.04;

        /// <summary>Draw with the backlight subtracted out, by workload.</summary>
        public const double IdleBase = 5.72;    // reading, PDFs, CPU under 5%
        public const double BrowseBase = 7.44;  // browsing, chat, docs
        public const double VideoBase = 10.13;  // video playback, active dev

        public static double Draw(double baseW, int brightness) { return baseW + brightness * WattsPerPoint; }
        public static double Hours(double wh, double watts) { return watts <= 0.05 ? 0 : wh / watts; }
    }

    /// <summary>Watts over time, with min / average / max called out.</summary>
    public class LineChart : Control
    {
        readonly List<double> _pts = new List<double>();
        public int Capacity = 180;
        public string Empty = "no samples yet";

        public LineChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
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

            int left = 40, top = 8, right = Width - 8, bottom = Height - 18;
            int cw = right - left, ch = bottom - top;
            if (cw < 20 || ch < 20) return;

            if (_pts.Count < 2)
            {
                Theme.Str(g, Empty, Theme.Small, Theme.Dim, left, top + ch / 2f - 8);
                return;
            }

            double lo = double.MaxValue, hi = double.MinValue, sum = 0;
            foreach (double d in _pts) { if (d < lo) lo = d; if (d > hi) hi = d; sum += d; }
            double avg = sum / _pts.Count;
            double pad = Math.Max(0.5, (hi - lo) * 0.2);
            double ylo = Math.Max(0, lo - pad), yhi = hi + pad;
            if (yhi - ylo < 1) yhi = ylo + 1;

            // grid and y labels
            using (Pen grid = new Pen(Theme.Edge, 1f))
            {
                for (int i = 0; i <= 3; i++)
                {
                    float y = top + ch * i / 3f;
                    g.DrawLine(grid, left, y, right, y);
                    double val = yhi - (yhi - ylo) * i / 3.0;
                    Theme.StrRight(g, val.ToString("0.0"), Theme.Small, Theme.Dim, left - 6, y - 8);
                }
            }

            PointF[] line = new PointF[_pts.Count];
            for (int i = 0; i < _pts.Count; i++)
            {
                float x = left + (float)i / (_pts.Count - 1) * cw;
                float y = top + (float)((yhi - _pts[i]) / (yhi - ylo)) * ch;
                line[i] = new PointF(x, y);
            }

            double last = _pts[_pts.Count - 1];
            Color c = last > 12 ? Theme.Spend : Theme.Save;

            PointF[] area = new PointF[line.Length + 2];
            Array.Copy(line, area, line.Length);
            area[line.Length] = new PointF(right, bottom);
            area[line.Length + 1] = new PointF(left, bottom);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(34, c))) g.FillPolygon(b, area);
            using (Pen p = new Pen(c, 1.8f) { LineJoin = LineJoin.Round }) g.DrawLines(p, line);

            // average line
            float ay = top + (float)((yhi - avg) / (yhi - ylo)) * ch;
            using (Pen p = new Pen(Theme.Dim, 1f) { DashStyle = DashStyle.Dash })
                g.DrawLine(p, left, ay, right, ay);

            PointF end = line[line.Length - 1];
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, end.X - 3.5f, end.Y - 3.5f, 7, 7);

            Theme.Str(g, "min " + lo.ToString("0.00") + "   avg " + avg.ToString("0.00") +
                         "   max " + hi.ToString("0.00") + " W   ·   " + _pts.Count + " samples",
                      Theme.Small, Theme.Dim, left, bottom + 2);
        }
    }

    /// <summary>
    /// Runtime against brightness for three workloads, with a marker at where the
    /// slider currently sits. Makes the cost of a bright screen legible at a glance.
    /// </summary>
    public class CurveChart : Control
    {
        public double CapacityWh = 43.905;
        public int Brightness = 30;

        public CurveChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            int left = 34, top = 10, right = Width - 96, bottom = Height - 20;
            int cw = right - left, ch = bottom - top;
            if (cw < 40 || ch < 30) return;

            double yhi = Model.Hours(CapacityWh, Model.Draw(Model.IdleBase, 0)) + 0.4;

            using (Pen grid = new Pen(Theme.Edge, 1f))
            {
                for (int hh = 0; hh <= (int)yhi; hh += 2)
                {
                    float y = bottom - (float)(hh / yhi) * ch;
                    g.DrawLine(grid, left, y, right, y);
                    Theme.StrRight(g, hh + "h", Theme.Small, Theme.Dim, left - 5, y - 8);
                }
                for (int b = 0; b <= 100; b += 25)
                {
                    float x = left + (float)b / 100 * cw;
                    g.DrawLine(grid, x, top, x, bottom);
                    Theme.Str(g, b + "%", Theme.Small, Theme.Dim, x - 8, bottom + 3);
                }
            }

            DrawCurve(g, left, top, cw, ch, bottom, yhi, Model.IdleBase, Theme.Save, "reading");
            DrawCurve(g, left, top, cw, ch, bottom, yhi, Model.BrowseBase, Theme.Text, "browsing");
            DrawCurve(g, left, top, cw, ch, bottom, yhi, Model.VideoBase, Theme.Spend, "video");

            // where the brightness slider is right now
            float mx = left + (float)Brightness / 100 * cw;
            using (Pen p = new Pen(Color.FromArgb(150, Theme.Text), 1f) { DashStyle = DashStyle.Dot })
                g.DrawLine(p, mx, top, mx, bottom);
            Theme.Str(g, Brightness + "%", Theme.Small, Theme.Text, mx + 3, top);

            // legend with the value at the current brightness
            float ly = top + 2;
            Legend(g, right + 8, ref ly, Theme.Save, "reading", Model.IdleBase);
            Legend(g, right + 8, ref ly, Theme.Text, "browsing", Model.BrowseBase);
            Legend(g, right + 8, ref ly, Theme.Spend, "video", Model.VideoBase);
        }

        void Legend(Graphics g, float x, ref float y, Color c, string name, double baseW)
        {
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, x, y + 6, 10, 3);
            Theme.Str(g, name, Theme.Small, Theme.Dim, x + 14, y);
            double h = Model.Hours(CapacityWh, Model.Draw(baseW, Brightness));
            int t = (int)Math.Round(h * 60);
            Theme.Str(g, (t / 60) + ":" + (t % 60).ToString("00"), Theme.Small, c, x + 14, y + 14);
            y += 34;
        }

        void DrawCurve(Graphics g, int left, int top, int cw, int ch, int bottom,
                       double yhi, double baseW, Color c, string name)
        {
            PointF[] pts = new PointF[21];
            for (int i = 0; i <= 20; i++)
            {
                int b = i * 5;
                double h = Model.Hours(CapacityWh, Model.Draw(baseW, b));
                pts[i] = new PointF(left + (float)b / 100 * cw, bottom - (float)(h / yhi) * ch);
            }
            using (Pen p = new Pen(c, 1.8f) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
        }
    }

    public class Segment
    {
        public string Name;
        public double Value;
        public Color Color;
    }

    /// <summary>Horizontal stacked bar with a labelled legend underneath.</summary>
    public class StackBar : Control
    {
        public List<Segment> Segments = new List<Segment>();
        public string Unit = "W";
        public string Empty = "";

        public StackBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            double total = 0;
            foreach (Segment s in Segments) total += Math.Max(0, s.Value);
            if (total <= 0.001)
            {
                Theme.Str(g, Empty, Theme.Small, Theme.Dim, 0, 6);
                return;
            }

            int barH = 16, w = Width;
            float x = 0;
            foreach (Segment s in Segments)
            {
                float sw = (float)(Math.Max(0, s.Value) / total) * w;
                if (sw < 0.5f) continue;
                Rectangle r = new Rectangle((int)x, 0, (int)Math.Ceiling(sw), barH);
                using (SolidBrush b = new SolidBrush(s.Color)) g.FillRectangle(b, r);
                x += sw;
            }

            float ly = barH + 8;
            float lx = 0;
            foreach (Segment s in Segments)
            {
                using (SolidBrush b = new SolidBrush(s.Color)) g.FillRectangle(b, lx, ly + 4, 9, 9);
                string txt = s.Name + "  " + s.Value.ToString("0.0") + " " + Unit +
                             "  (" + (100.0 * s.Value / total).ToString("0") + "%)";
                Theme.Str(g, txt, Theme.Small, Theme.Dim, lx + 14, ly);
                lx += 14 + Theme.TextW(g, txt, Theme.Small) + 18;
            }
        }
    }
}
