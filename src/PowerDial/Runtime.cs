using System;
using System.Collections.Generic;

namespace PowerDial
{
    /// <summary>
    /// How long this battery actually lasts, worked out from watts this PC recorded.
    ///
    /// This is deliberately not a model. Every figure is the arithmetic of two measured
    /// quantities: draw timed from the battery energy counter, and the capacity the pack
    /// currently holds. Nothing is assumed about the hardware, so on a PC with no recorded
    /// time on battery the answer is "not measured yet" rather than a borrowed number.
    ///
    /// The live 60-second reading answers "what am I drawing this minute", which swings
    /// with whatever the CPU happens to be doing. The average answers the question people
    /// actually have - "how long does this thing last" - and it only becomes meaningful
    /// once there are enough recorded minutes behind it, which is why Minutes is carried
    /// alongside and shown.
    /// </summary>
    public sealed class RuntimeAverage
    {
        /// <summary>Recorded minutes on battery behind these figures. 0 = nothing measured.</summary>
        public int Minutes;

        /// <summary>Mean measured draw across those minutes, watts.</summary>
        public double Watts;

        /// <summary>
        /// Robust spread of measured draw: the 10th and 90th percentile rather than the
        /// outright min and max. A single spiky minute should not become "your best case".
        /// </summary>
        public double Low, High;

        /// <summary>A figure needs both some recorded time and a draw worth dividing by.</summary>
        public bool Known { get { return Minutes > 0 && Watts > 0.05; } }

        static double? Hours(int? mwh, double watts)
        {
            if (!mwh.HasValue || mwh.Value <= 0 || watts <= 0.05) return null;
            double h = (mwh.Value / 1000.0) / watts;
            if (h > 99) return null;
            return h;
        }

        /// <summary>Hours a full charge lasts at the measured average draw.</summary>
        public double? FromFull(int? fullMwh)
        {
            return Known ? Hours(fullMwh, Watts) : null;
        }

        /// <summary>Hours left from the present charge at the measured average draw.</summary>
        public double? Left(int? remainingMwh)
        {
            return Known ? Hours(remainingMwh, Watts) : null;
        }

        /// <summary>Longest run seen from full - at the 10th percentile of measured draw.</summary>
        public double? Best(int? fullMwh)
        {
            return Known && Low > 0.05 ? Hours(fullMwh, Low) : null;
        }

        /// <summary>Shortest run seen from full - at the 90th percentile of measured draw.</summary>
        public double? Worst(int? fullMwh)
        {
            return Known && High > 0.05 ? Hours(fullMwh, High) : null;
        }

        static double Percentile(List<double> sorted, double q)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            int i = (int)Math.Round(q * (sorted.Count - 1));
            if (i < 0) i = 0;
            if (i > sorted.Count - 1) i = sorted.Count - 1;
            return sorted[i];
        }

        /// <summary>
        /// Average the recorded points that were actually on battery and actually drawing.
        /// Points on AC carry W = 0 by design and would drag the mean to nothing, so they
        /// are skipped rather than counted as a very efficient minute.
        /// </summary>
        public static RuntimeAverage From(List<HistPoint> pts)
        {
            RuntimeAverage r = new RuntimeAverage();
            if (pts == null || pts.Count == 0) return r;

            List<double> w = new List<double>();
            double sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                HistPoint p = pts[i];
                if (p.Ac) continue;
                if (p.W <= 0.05) continue;
                w.Add(p.W);
                sum += p.W;
            }
            if (w.Count == 0) return r;

            w.Sort();
            r.Minutes = w.Count;            // history is one point a minute
            r.Watts = sum / w.Count;
            r.Low = Percentile(w, 0.10);
            r.High = Percentile(w, 0.90);
            return r;
        }
    }
}
