using System;
using System.Collections.Generic;
using System.Globalization;

namespace PowerDial
{
    internal sealed class SessionSummary
    {
        public long Start, End;
        public int StartPercent, EndPercent, Samples;
        public double? EnergyWh, AverageWatts;
        public string Status, Modes, Busiest;
    }

    internal static class SessionSummaries
    {
        internal static List<SessionSummary> From(List<HistPoint> history, long now, string liveSegment)
        {
            List<SessionSummary> result = new List<SessionSummary>();
            List<HistPoint> segment = new List<HistPoint>();
            HistPoint previous = null;
            foreach (HistPoint point in history)
            {
                bool valid = point.Pct >= 0 && point.Pct <= 100;
                bool split = previous != null && (point.Ac || !valid || point.T <= previous.T ||
                    point.T - previous.T > 90 || point.S != previous.S);
                if (split)
                {
                    result.Add(Summarise(segment, point.Ac ? "Plugged in" : "Recording interrupted"));
                    segment.Clear(); previous = null;
                }
                if (point.Ac || !valid) continue;
                segment.Add(point); previous = point;
            }
            if (segment.Count > 0)
            {
                HistPoint last = segment[segment.Count - 1];
                bool active = !string.IsNullOrEmpty(liveSegment) && last.S == liveSegment && now - last.T <= 90;
                result.Add(Summarise(segment, active ? "In progress" : "Recording ended"));
            }
            result.Reverse();
            if (result.Count > 6) result.RemoveRange(6, result.Count - 6);
            return result;
        }

        static SessionSummary Summarise(List<HistPoint> points, string status)
        {
            HistPoint first = points[0], last = points[points.Count - 1];
            SessionSummary result = new SessionSummary { Start = first.T, End = last.T, StartPercent = first.Pct,
                EndPercent = last.Pct, Samples = points.Count, Status = status };
            HashSet<int> modes = new HashSet<int>();
            Dictionary<string, double> apps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            bool energyKnown = first.E.HasValue && first.E.Value > 0;
            double wattSeconds = 0, seconds = 0;
            for (int i = 0; i < points.Count; i++)
            {
                HistPoint p = points[i];
                modes.Add(p.M);
                if (!p.E.HasValue || p.E.Value < 0 || (i > 0 && p.E > points[i - 1].E)) energyKnown = false;
                double dt = i == 0 ? 0 : p.T - points[i - 1].T;
                if (double.IsFinite(p.W) && p.W > 0.05 && dt > 0)
                { wattSeconds += p.W * dt; seconds += dt; }
                foreach (string item in (p.Top ?? "").Split('|'))
                {
                    int space = item.LastIndexOf(' ');
                    if (space <= 0) continue;
                    double cpu;
                    string number = item.Substring(space + 1);
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out cpu) &&
                        !double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out cpu)) continue;
                    if (!double.IsFinite(cpu) || cpu < 0) continue;
                    string name = item.Substring(0, space);
                    double tally; apps.TryGetValue(name, out tally);
                    apps[name] = tally + cpu * dt;
                }
            }
            if (energyKnown && points.Count > 1) result.EnergyWh = (first.E.Value - last.E.Value) / 1000.0;
            if (seconds > 0) result.AverageWatts = wattSeconds / seconds;
            List<string> labels = new List<string>();
            foreach (int code in modes)
            {
                Preset p = Presets.ByCode(code);
                labels.Add(p == null ? "Custom" : (p.Friendly ?? p.Name));
            }
            result.Modes = string.Join(", ", labels);
            double highest = 0;
            foreach (KeyValuePair<string, double> app in apps)
                if (app.Value > highest) { highest = app.Value; result.Busiest = app.Key; }
            return result;
        }
    }
}
