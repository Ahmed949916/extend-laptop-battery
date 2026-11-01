using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using PowerDial;

sealed partial class SelfTest
{
    static HistPoint CarePoint(long time, int pct = 90, bool ac = true, string segment = "a", int full = 50000)
    {
        return new HistPoint { T = time, Pct = pct, Ac = ac, S = segment, E = 40000, F = full, D = 60000 };
    }

    static void BatteryCareChecks()
    {
        List<HistPoint> points = new List<HistPoint> { CarePoint(1000), CarePoint(1060), CarePoint(1120) };
        ChargeExposure e = BatteryCare.Exposure(points, 1120);
        Check("charge exposure uses observed intervals, not sample count", e.ObservedSeconds == 120 && e.HighSeconds == 120 && e.PluggedHighSeconds == 120);
        points.Add(CarePoint(1180, ac: false, segment: "b"));
        points.Add(CarePoint(1240, 10, false, "b"));
        points.Add(CarePoint(1300, 10, false, "b"));
        points.Add(CarePoint(5000, 10, false, "b"));
        e = BatteryCare.Exposure(points, 5000);
        Check("source changes, threshold crossings and sleep are not charged as exposure", e.ObservedSeconds == 240 && e.LowSeconds == 60 && e.HighSeconds == 120);
        points.Add(CarePoint(5060, 10, false, "restart"));
        Check("restart boundaries do not add exposure", BatteryCare.Exposure(points, 5060).ObservedSeconds == 240);
        Check("future records are excluded", BatteryCare.Exposure(points, 1059).ObservedSeconds == 0);
        Check("seven-day boundary clips a partial interval", BatteryCare.Exposure(points, 1030 + 7 * 86400).HighSeconds == 90);
        points[1].E = null;
        Check("unknown battery telemetry breaks the interval", BatteryCare.Exposure(points, 1120).ObservedSeconds == 0);
        points = new List<HistPoint> { new HistPoint { T = 1000 }, new HistPoint { T = 1060 } };
        Check("legacy desktop records cannot appear as low-charge exposure", BatteryCare.Exposure(points, 1060).ObservedSeconds == 0);
        points = new List<HistPoint> { CarePoint(1000), CarePoint(1000), CarePoint(900) };
        Check("duplicate and reversed timestamps add no observation time", BatteryCare.Exposure(points, 1120).ObservedSeconds == 0);

        points = new List<HistPoint>();
        const long day = 86400;
        for (int i = 0; i < 14; i++) points.Add(CarePoint((100 + i) * day, full: i < 7 ? 50000 : 48000));
        // Many readings on one noisy day must not dominate the weekly comparison.
        for (int i = 1; i < 100; i++) points.Add(CarePoint(113 * day + i, full: 70000));
        points.Sort((a, b) => a.T.CompareTo(b.T));
        CapacityTrend trend = BatteryCare.Trend(points, 114 * day);
        Check("capacity trend uses equally weighted daily medians", trend.Days == 14 && trend.FirstMwh == 50000 && trend.LastMwh == 48000);
        Check("short history cannot become a wear trend", BatteryCare.Trend(points, 105 * day).FirstMwh == null);
        Check("stale capacity does not appear current", BatteryCare.Trend(points, 122 * day).FirstMwh == null);
        points.Add(CarePoint(114 * day)); points[points.Count - 1].D = 70000;
        Check("design-reference changes restart capacity comparison", BatteryCare.Trend(points, 114 * day).Days == 1);
        points = new List<HistPoint> { CarePoint(100 * day), CarePoint(113 * day) };
        Check("two isolated days cannot imply a capacity trend", BatteryCare.Trend(points, 114 * day).FirstMwh == null);

        HistPoint old = JsonSerializer.Deserialize("{\"T\":1000,\"Pct\":80}", CompactJson.Default.HistPoint);
        Check("legacy history leaves health fields unknown", old.F == null && old.D == null);
        points = new List<HistPoint> { CarePoint(1000) };
        points[0].F = null;
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string csv = BatteryCare.Csv(points);
            Check("CSV is locale independent and preserves missing capacity", csv.Contains(",90,true,40000,,60000\r\n", StringComparison.Ordinal));
            Check("CSV has no process names or segment identifiers", !csv.Contains("segment", StringComparison.Ordinal) && !csv.Contains("Top", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
