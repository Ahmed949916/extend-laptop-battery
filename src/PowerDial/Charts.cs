using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PowerDial
{
    /// <summary>Watts over time, with min / average / max called out.</summary>
    public sealed class LineChart : Control
    {
        readonly List<double> _pts = new List<double>();
        public int Capacity = 240;
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

        public void Seed(IEnumerable<double> vals)
        {
            _pts.Clear();
            foreach (double v in vals) _pts.Add(v);
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
    /// Charge over time, straight from the saved history, so it spans previous runs
    /// rather than only the current one. Amber where the machine was on battery,
    /// dim where it was plugged in.
    /// </summary>
    public sealed class BatteryChart : Control
    {
        List<HistPoint> _pts = new List<HistPoint>();
        public string Empty = "no history recorded yet";

        public BatteryChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public void SetData(List<HistPoint> pts) { _pts = pts ?? new List<HistPoint>(); Invalidate(); }
        public int Count { get { return _pts.Count; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Quality(g);
            g.Clear(BackColor);

            int left = 34, top = 8, right = Width - 8, bottom = Height - 18;
            int cw = right - left, ch = bottom - top;
            if (cw < 20 || ch < 20) return;

            if (_pts.Count < 2)
            {
                Theme.Str(g, Empty, Theme.Small, Theme.Dim, left, top + ch / 2f - 8);
                return;
            }

            using (Pen grid = new Pen(Theme.Edge, 1f))
                for (int pct = 0; pct <= 100; pct += 25)
                {
                    float y = bottom - pct / 100f * ch;
                    g.DrawLine(grid, left, y, right, y);
                    Theme.StrRight(g, pct + "%", Theme.Small, Theme.Dim, left - 5, y - 8);
                }

            long t0 = _pts[0].T, t1 = _pts[_pts.Count - 1].T;
            double span = Math.Max(1, t1 - t0);

            for (int i = 1; i < _pts.Count; i++)
            {
                HistPoint a = _pts[i - 1], b = _pts[i];
                float x1 = left + (float)((a.T - t0) / span) * cw;
                float x2 = left + (float)((b.T - t0) / span) * cw;
                float y1 = bottom - a.Pct / 100f * ch;
                float y2 = bottom - b.Pct / 100f * ch;
                // a gap of more than 15 minutes means the app was not running; do not join it
                if (b.T - a.T > 900) continue;
                Color c = b.Ac ? Theme.Dim : Theme.Spend;
                using (Pen p = new Pen(c, b.Ac ? 1.2f : 2f)) g.DrawLine(p, x1, y1, x2, y2);
            }

            string range = _pts[0].When.ToString("d MMM HH:mm") + "  to  " +
                           _pts[_pts.Count - 1].When.ToString("d MMM HH:mm");
            Theme.Str(g, range, Theme.Small, Theme.Dim, left, bottom + 2);
            Theme.StrRight(g, "amber = on battery", Theme.Small, Theme.Dim, right, bottom + 2);
        }
    }

    /// <summary>
    /// What is actually running, ranked. The bar behind each row is proportional to
    /// whichever column is being sorted on, so the shape of the list is readable
    /// before any of the numbers are.
    /// </summary>
    public sealed class ProcessTable : Control
    {
        public List<ProcInfo> Rows = new List<ProcInfo>();
        public bool SortByCpu = false;
        public string Empty = "sampling...";
        // the same table renders live processes and the saved cumulative tally, so the
        // column labels and units are set by whoever fills it in
        public string CpuHeader = "cpu";
        public string CpuSuffix = "%";
        public string MemHeader = "memory";
        public string MemSuffix = " MB";
        public string CpuFormat = "0.0";
        public string MemFormat = "0";
        public const int RowH = 21;

        public ProcessTable()
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

            int nameX = 2, cpuR = Width - 92, memR = Width - 6;

            Theme.Str(g, "process", Theme.Small, Theme.Dim, nameX, 1);
            Theme.StrRight(g, CpuHeader, Theme.Small, Theme.Dim, cpuR, 1);
            Theme.StrRight(g, MemHeader, Theme.Small, Theme.Dim, memR, 1);
            using (Pen p = new Pen(Theme.Edge, 1f)) g.DrawLine(p, 0, 17, Width, 17);

            if (Rows.Count == 0)
            {
                Theme.Str(g, Empty, Theme.Small, Theme.Dim, nameX, 24);
                return;
            }

            double max = 0.0001;
            foreach (ProcInfo r in Rows)
            {
                double v = SortByCpu ? r.CpuPercent : r.WorkingSetMb;
                if (v > max) max = v;
            }

            int y = 21;
            foreach (ProcInfo r in Rows)
            {
                double v = SortByCpu ? r.CpuPercent : r.WorkingSetMb;
                float frac = (float)Math.Max(0, Math.Min(1, v / max));
                Color accent = r.Background ? Theme.Spend : Theme.Save;

                if (frac > 0.01f)
                {
                    Rectangle bar = new Rectangle(0, y, (int)(Width * frac), RowH - 3);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(26, accent)))
                        g.FillRectangle(b, bar);
                }

                string label = r.Name;
                if (r.Instances > 1) label += "  x" + r.Instances;
                if (r.Self) label += "  (this app)";
                Theme.Str(g, label, Theme.Small, r.Background ? Theme.Text : Theme.Dim, nameX + 4, y + 1);

                Theme.StrRight(g, r.CpuPercent.ToString(CpuFormat) + CpuSuffix, Theme.Small,
                               SortByCpu ? accent : Theme.Dim, cpuR, y + 1);
                Theme.StrRight(g, r.WorkingSetMb.ToString(MemFormat) + MemSuffix, Theme.Small,
                               SortByCpu ? Theme.Dim : accent, memR, y + 1);
                y += RowH;
            }
        }
    }

    public sealed class Segment
    {
        public string Name;
        public double Value;
        public Color Color;
    }

    /// <summary>Horizontal stacked bar with a labelled legend underneath.</summary>
    public sealed class StackBar : Control
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
