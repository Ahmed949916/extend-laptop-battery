using System;
using System.Collections.Generic;
using System.IO;
using PowerDial;

sealed partial class SelfTest
{
    static void RegressionChecks()
    {
        Console.WriteLine("--- history, comparisons and capacity regression checks ---");
        HistoryChecks();
        ComparisonChecks();
        CapacityChecks();
    }

    static void HistoryChecks()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PowerDial-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "2025-12.jsonl"), "{\"T\":100,\"W\":10}\n");
            File.WriteAllText(Path.Combine(folder, "2026-01.jsonl"), "{\"T\":300,\"W\":20}\nbroken json\n{\"T\":200,\"W\":15}\n");
            File.WriteAllText(Path.Combine(folder, "2026-02.jsonl"), "{\"T\":400,\"W\":10}\n");
            File.WriteAllText(Path.Combine(folder, "notes.jsonl"), "{\"T\":500,\"W\":99}\n");
            List<HistPoint> records = History.All(folder);
            Check("all monthly history, including older months and year boundary, is loaded", records.Count == 4);
            Check("history is chronological even when a file is out of order", records[1].T == 200 && records[2].T == 300);
            Check("unchanged history uses the cache", ReferenceEquals(records, History.All(folder)));
            File.AppendAllText(Path.Combine(folder, "2026-02.jsonl"), "{\"T\":450,\"W\":10}\n");
            Check("appending invalidates the history cache", History.All(folder).Count == 5);
            File.WriteAllText(Path.Combine(folder, "2026-03.jsonl"), "{\"T\":600,\"W\":10}\n");
            Check("a newly created month invalidates the history cache", History.All(folder).Count == 6);
            File.Delete(Path.Combine(folder, "2025-12.jsonl"));
            Check("a pruned month disappears from the cache", History.All(folder).Count == 5 && History.All(folder)[0].T == 200);
            Check("a missing history folder is an empty first run", History.All(Path.Combine(folder, "missing")).Count == 0);
        }
        finally { Directory.Delete(folder, true); }
    }

    static void AddSession(List<HistPoint> records, long start, int mode, double cpu, string app, int count = 5)
    {
        for (int i = 0; i < count; i++)
            records.Add(new HistPoint { T = start + i * 60, M = mode, Cpu = cpu, W = 10,
                                       Top = app == null ? null : app + " 12.0|other 2.0" });
    }

    static void ComparisonChecks()
    {
        List<HistPoint> records = new List<HistPoint>();
        AddSession(records, 1000, 1, 20, "Browser Helper");
        AddSession(records, 2000, 2, 200, "Browser Helper");
        AddSession(records, 3000, 3, 20, "Game");
        AddSession(records, 4000, 1, 20, null);
        AddSession(records, 5000, 2, 25, "Browser Helper");
        Comparison.Result matched = Comparison.Select(records, true, 2, 0);
        Check("similar sessions exclude high CPU, different apps and missing telemetry", matched.Points.Count == 10);
        Check("matching uses the latest qualifying session in the current mode", matched.Points[0].M == 1 && matched.Points[5].T == 5000);
        Check("all-usage selection preserves every record", ReferenceEquals(records, Comparison.Select(records, false, 2, 0).Points));
        Check("no reference mode returns no data rather than all data", Comparison.Select(records, true, 4, 0).Points.Count == 0);

        List<HistPoint> shortRun = new List<HistPoint>();
        AddSession(shortRun, 1000, 1, 20, "Browser", 4);
        Check("short sessions are not enough evidence", Comparison.Select(shortRun, true, 1, 0).Points.Count == 0);
        AddSession(shortRun, 1400, 1, 20, "Browser", 4);
        Check("a recording gap cannot turn two short runs into a qualifying session", Comparison.Select(shortRun, true, 1, 0).Points.Count == 0);

        List<HistPoint> boundary = new List<HistPoint>();
        AddSession(boundary, 1000, 1, 20, "Browser", 8);
        Check("the change timestamp splits before and after sessions", Comparison.Select(boundary, true, 1, 1240).Points.Count == 0);
        boundary[3].Ac = true;
        Check("AC separates sessions and is never matched as battery usage", Comparison.Select(boundary, true, 1, 0).Points.Count == 0);
        boundary[3].Ac = false;
        boundary[3].W = double.NaN;
        Check("invalid power readings break sessions", Comparison.Select(boundary, true, 1, 0).Points.Count == 0);
        boundary[3].W = 10;
        boundary[3].Cpu = double.NaN;
        Check("invalid CPU readings break sessions", Comparison.Select(boundary, true, 1, 0).Points.Count == 0);
    }

    static void CapacityChecks()
    {
        using BatteryMonitor monitor = new BatteryMonitor();
        DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        Func<int?> read = delegate { calls++; return calls == 1 ? 50000 : 49000; };
        monitor.RefreshCapacity(start, read);
        Check("capacity is read immediately", monitor.FullChargeMwh == 50000 && calls == 1);
        monitor.RefreshCapacity(start.AddMinutes(1), read);
        Check("ordinary polls do not repeatedly query capacity", calls == 1);
        monitor.RefreshCapacity(start.AddHours(6), read);
        Check("capacity refreshes in a long-running process", monitor.FullChargeMwh == 49000 && calls == 2);
        monitor.RefreshCapacity(start.AddHours(12), delegate { calls++; return -1; });
        Check("invalid capacity preserves the last valid reading", monitor.FullChargeMwh == 49000);
        monitor.RefreshCapacity(start.AddHours(12).AddMinutes(1), read);
        Check("invalid capacity queries have retry backoff", calls == 3);
        monitor.RefreshCapacity(start.AddHours(12).AddMinutes(5), delegate { throw new IOException("test failure"); });
        Check("a failed capacity query preserves the last valid reading", monitor.FullChargeMwh == 49000 && monitor.LastError == "test failure");
        monitor.RefreshCapacity(start.AddDays(2), read);
        Check("capacity refreshes after a long suspend", calls == 4);
    }
}
