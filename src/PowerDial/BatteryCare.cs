using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PowerDial
{
    internal sealed class ChargeExposure
    {
        internal double ObservedSeconds, HighSeconds, LowSeconds, PluggedHighSeconds;
    }

    internal sealed class CapacityTrend
    {
        internal int Days;
        internal double? FirstMwh, LastMwh;
        internal string Note = "Collecting capacity readings: needs 14 days and at least three recorded days in each comparison week.";
    }

    /// <summary>Read-only summaries. Observation time is not wall-clock coverage or predicted wear.</summary>
    internal static class BatteryCare
    {
        internal const string CareUrl = "https://support.microsoft.com/en-us/windows/experience/power-battery/caring-for-your-battery-in-windows";
        internal const string ChargingUrl = "https://support.microsoft.com/en-us/windows/experience/power-battery/use-smart-charging-in-windows";

        internal static ChargeExposure Exposure(List<HistPoint> points, long now)
        {
            ChargeExposure result = new ChargeExposure();
            long start = now - 7 * 86400;
            for (int i = 1; i < points.Count; i++)
            {
                HistPoint a = points[i - 1], b = points[i];
                long elapsed = b.T - a.T;
                // Both ends must be observed, on the same power source and recording segment.
                // Never fill sleep, restart, missing-telemetry or legacy desktop intervals.
                if (b.T > now || b.T <= start || elapsed <= 0 || elapsed > 90 ||
                    !Valid(a) || !Valid(b) || a.S != b.S || a.Ac != b.Ac) continue;
                double seconds = b.T - Math.Max(start, a.T);
                result.ObservedSeconds += seconds;
                if (a.Pct > 80 && b.Pct > 80)
                {
                    result.HighSeconds += seconds;
                    if (a.Ac) result.PluggedHighSeconds += seconds;
                }
                if (a.Pct < 20 && b.Pct < 20) result.LowSeconds += seconds;
            }
            return result;
        }

        static bool Valid(HistPoint p)
        {
            return p.Pct >= 0 && p.Pct <= 100 && p.E.HasValue && p.E.Value >= 0 && !string.IsNullOrEmpty(p.S);
        }

        internal static CapacityTrend Trend(List<HistPoint> points, long now)
        {
            CapacityTrend result = new CapacityTrend();
            SortedDictionary<long, List<double>> daily = new SortedDictionary<long, List<double>>();
            int? design = null;
            foreach (HistPoint p in points)
            {
                if (p.T > now || p.T < now - 90 * 86400 || !Valid(p) ||
                    !p.F.HasValue || p.F.Value <= 0 || p.F.Value == int.MaxValue ||
                    !p.D.HasValue || p.D.Value <= 0) continue;
                // A changed reference may be a replacement battery or a manual correction.
                // Start again instead of reporting that difference as battery wear.
                if (design.HasValue && design != p.D) daily.Clear();
                design = p.D;
                long day = p.T / 86400;
                if (!daily.TryGetValue(day, out List<double> values))
                { values = new List<double>(); daily.Add(day, values); }
                values.Add(p.F.Value);
            }
            result.Days = daily.Count;
            if (daily.Count == 0) return result;
            List<long> days = new List<long>(daily.Keys);
            long first = days[0], last = days[days.Count - 1];
            if (now / 86400 - last > 7)
            { result.Note = "Capacity readings are stale: no fresh capacity record in the last seven days."; return result; }
            if (last - first < 13) return result;
            List<double> early = new List<double>(), recent = new List<double>();
            foreach (KeyValuePair<long, List<double>> day in daily)
            {
                double median = Median(day.Value);
                if (day.Key < first + 7) early.Add(median);
                if (day.Key > last - 7) recent.Add(median);
            }
            if (early.Count < 3 || recent.Count < 3) return result;
            result.FirstMwh = Median(early);
            result.LastMwh = Median(recent);
            result.Note = "Earlier / recent week medians, with equal weight per recorded UTC day. Firmware estimates can shift with calibration, charge limits or battery replacement; this is not a wear rate or lifespan forecast.";
            return result;
        }

        static double Median(List<double> values)
        {
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        }

        internal static string Plan(int choice)
        {
            switch (choice)
            {
                case 1:
                    return "Travel: charge to 100% when you need the runtime, preferably near departure. Return to your usual charging limit afterwards. Recharge before running very low; repeated full discharges are not a battery-health treatment.";
                case 2:
                    return "Storage: aim for roughly 40-60% before shutting down for an extended period. Store in a cool, dry place and check charge periodically. Do not run a heavy workload just to drain to a target.";
                default:
                    return "Desk use: enable Smart charging or a charge limit in your manufacturer's app or BIOS if supported; around 80% is a practical daily target. Stay plugged in with that limit rather than deliberately cycling the battery. Support varies by model.";
            }
        }

        internal static string Csv(List<HistPoint> points)
        {
            StringBuilder csv = new StringBuilder("utc,charge_percent,on_ac,remaining_mwh,full_charge_mwh,design_mwh\r\n");
            foreach (HistPoint p in points)
            {
                if (!Valid(p) || p.T < 0 || p.T > 253402300799) continue;
                csv.Append(DateTimeOffset.FromUnixTimeSeconds(p.T).ToString("O", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(p.Pct.ToString(CultureInfo.InvariantCulture)).Append(',').Append(p.Ac ? "true" : "false").Append(',');
                csv.Append(p.E.Value.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.Append(p.F?.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.Append(p.D?.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            return csv.ToString();
        }
    }
}
