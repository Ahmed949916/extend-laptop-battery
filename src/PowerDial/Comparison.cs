using System;
using System.Collections.Generic;
using System.Globalization;

namespace PowerDial
{
    /// <summary>
    /// An observational filter, not a savings model. A session is a contiguous run of
    /// valid battery minutes under one mode. Gaps, AC and the last change split sessions.
    /// Match their mean CPU load and most frequently busiest app; GPU/display activity
    /// is not recorded, so even matched sessions cannot establish causation.
    /// </summary>
    internal static class Comparison
    {
        internal sealed class Result
        {
            public List<HistPoint> Points = new List<HistPoint>();
            public string Note;
        }

        sealed class Session
        {
            public readonly List<HistPoint> Points = new List<HistPoint>();
            public double Cpu;
            public string App;

            public void Summarise()
            {
                Dictionary<string, int> apps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int best = 0;
                foreach (HistPoint p in Points)
                {
                    Cpu += p.Cpu;
                    string first = (p.Top ?? "").Split('|')[0];
                    int space = first.LastIndexOf(' ');
                    if (space <= 0) continue;
                    string name = first.Substring(0, space);
                    int count;
                    apps.TryGetValue(name, out count);
                    apps[name] = ++count;
                    if (count > best) { best = count; App = name; }
                }
                Cpu /= Points.Count;
                // Missing process records must not masquerade as the same workload.
                if (best * 2 < Points.Count) App = null;
            }
        }

        internal static Result Select(List<HistPoint> history, bool similar, int mode, long change)
        {
            if (!similar) return new Result {
                Points = history,
                Note = "All recorded usage. Workloads may differ; observed differences do not prove savings."
            };

            List<Session> sessions = new List<Session>();
            Session current = null;
            HistPoint previous = null;
            foreach (HistPoint p in history)
            {
                if (p.Ac || !double.IsFinite(p.W) || p.W <= 0.05 ||
                    !double.IsFinite(p.Cpu) || p.Cpu < 0)
                { current = null; previous = null; continue; }
                if (previous == null || p.T <= previous.T || p.T - previous.T > 90 ||
                    p.M != previous.M || (previous.T < change && p.T >= change))
                {
                    current = new Session();
                    sessions.Add(current);
                }
                current.Points.Add(p);
                previous = p;
            }

            Session reference = null;
            foreach (Session session in sessions)
            {
                session.Summarise();
                if (session.Points.Count >= 5 && session.App != null && session.Points[0].M == mode)
                    reference = session;
            }
            Result result = new Result();
            if (reference == null)
            {
                result.Note = "No reference session yet. Record at least five consecutive battery minutes " +
                              "in the current mode with a consistent busiest app. No unfiltered fallback is used.";
                return result;
            }

            double tolerance = Math.Max(10, reference.Cpu * 0.25);
            int count = 0;
            foreach (Session session in sessions)
            {
                if (session.Points.Count < 5 || session.App == null ||
                    !string.Equals(session.App, reference.App, StringComparison.OrdinalIgnoreCase) ||
                    Math.Abs(session.Cpu - reference.Cpu) > tolerance) continue;
                result.Points.AddRange(session.Points);
                count++;
            }
            result.Note = count + " sessions matching " + reference.App + ", CPU " +
                reference.Cpu.ToString("0.0", CultureInfo.CurrentCulture) + "% +/- " +
                tolerance.ToString("0.0", CultureInfo.CurrentCulture) + " points of one core. Reference: " +
                reference.Points[0].When.ToString("g", CultureInfo.CurrentCulture) +
                ". GPU and display activity may still differ; this does not prove savings.";
            return result;
        }
    }
}
