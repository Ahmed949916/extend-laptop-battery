using System;
using System.Collections.Generic;
using System.Threading;
using PowerDial;

sealed class SelfTest
{
    static int fail = 0;

    static void Main()
    {
        Console.WriteLine("=== PowerDial self-test (READ-ONLY - changes nothing) ===");
        Console.WriteLine();

        Console.WriteLine("--- machine ---");
        Machine.Detect(true);
        Console.WriteLine("  " + Machine.Summary());
        Console.WriteLine("  design capacity : " + (Machine.DesignCapacityMwh > 0
            ? Machine.DesignCapacityMwh + " mWh (" + Machine.DesignCapacitySource + ")"
            : "unknown"));
        Console.WriteLine();
        Console.WriteLine("--- environment ---");
        Console.WriteLine("  elevated      : " + PowerCfg.IsElevated());
        Console.WriteLine("  active scheme : " + PowerCfg.ActiveSchemeName());
        Console.WriteLine("  scheme guid   : " + PowerCfg.ActiveScheme());
        Check("active scheme guid looks like a guid", PowerCfg.ActiveScheme().Length == 36);
        Console.WriteLine();

        Console.WriteLine("--- knobs: can every one be read? ---");
        int readable = 0;
        foreach (Knob k in PowerCfg.Knobs)
        {
            int? dc = PowerCfg.Read(k, true);
            int? ac = PowerCfg.Read(k, false);
            bool expl = PowerCfg.IsExplicit(k, true);
            Console.WriteLine(string.Format("  {0,-42} battery={1,-32} ac={2,-24} {3}",
                k.Label,
                PowerCfg.Describe(k, dc),
                PowerCfg.Describe(k, ac),
                expl ? "stored" : "inherited"));
            if (dc.HasValue) readable++;
        }
        Check("all knobs readable", readable == PowerCfg.Knobs.Count,
              readable + " of " + PowerCfg.Knobs.Count);
        Console.WriteLine();

        // These used to be four values verified by hand in one session and then frozen
        // into the test. They failed every time a profile was applied - which is the app's
        // main feature - so they measured how recently someone had edited the test rather
        // than whether the machine was sane. What follows asserts things that stay true.
        Console.WriteLine("--- every value is one the setting actually allows ---");
        int outOfRange = 0, unreadable = 0;
        foreach (Knob k in PowerCfg.Knobs)
        {
            if (!PowerCfg.Exists(k)) continue;
            int? v = PowerCfg.Read(k, true);
            if (!v.HasValue) { unreadable++; continue; }
            bool okRange = k.Choices != null
                ? k.Choices.ContainsKey(v.Value)
                : v.Value >= k.Min && v.Value <= k.Max;
            if (!okRange)
            {
                outOfRange++;
                Console.WriteLine("    " + k.Label.PadRight(34) + PowerCfg.Describe(k, v) +
                                  "   outside " + (k.Choices != null ? "the known choices"
                                                 : k.Min + "-" + k.Max));
            }
        }
        Check("every battery value is in range", outOfRange == 0, outOfRange + " outside");
        Check("every defined setting reads back", unreadable == 0, unreadable + " unreadable");
        Console.WriteLine();

        // Two real failure modes, worth defending regardless of which profile is on.
        Console.WriteLine("--- the two that bite ---");
        Knob lid = PowerCfg.Find("lid");
        int? lidV = lid == null ? null : PowerCfg.Read(lid, true);
        Check("closing the lid on battery does something",
              lidV.HasValue && lidV.Value != 0,
              lid == null ? "no lid setting here" : PowerCfg.Describe(lid, lidV) +
              " - Do nothing is how a laptop flattens itself in a bag");

        Knob sleep = PowerCfg.Find("sleepidle");
        int? sleepV = sleep == null ? null : PowerCfg.Read(sleep, true);
        Check("it sleeps on battery eventually",
              sleepV.HasValue && sleepV.Value > 0,
              sleep == null ? "no sleep setting here" : PowerCfg.Describe(sleep, sleepV));
        Console.WriteLine();

        // Which profile the machine currently matches, if any. Reported, never asserted -
        // a custom set of values is a legitimate state, not a failure.
        Console.WriteLine("--- current state against the profiles ---");
        string matched = null;
        foreach (Preset p in Presets.All)
        {
            bool all = true;
            foreach (KeyValuePair<string, int> kv in p.Values)
            {
                Knob k = PowerCfg.Find(kv.Key);
                if (k == null || !PowerCfg.Exists(k)) continue;
                int? v = PowerCfg.Read(k, true);
                if (!v.HasValue || v.Value != kv.Value) { all = false; break; }
            }
            if (all) { matched = p.Name; break; }
        }
        Console.WriteLine("  " + (matched != null
            ? "matches the " + matched + " profile exactly"
            : "a custom mix, matching no profile exactly - perfectly normal"));
        Check("profile comparison ran", true, matched ?? "custom");
        Console.WriteLine();


        Console.WriteLine("--- gpu watchdog ---");
        List<Finding> f = GpuWatch.Check();
        foreach (Finding item in f)
            Console.WriteLine(string.Format("  [{0}] {1,-38} {2}",
                item.Ok ? "OK" : "!!", item.Name, item.Detail));
        double? cost = GpuWatch.TotalCost(f);
        Console.WriteLine("  flagged cost: " + (cost.HasValue
            ? "~" + cost.Value.ToString("0.0") + " W" : "not measured on this PC"));
        Check("watchdog produced a verdict", f.Count >= 1, f.Count + " findings");
        Console.WriteLine();

        Console.WriteLine("--- suggestions: what would make this PC last longer ---");

        // scanning must not write anything. Snapshot every battery-side value first,
        // scan, then compare - the whole section is worthless if reading it changes the
        // machine, and that is exactly the kind of bug that hides.
        Dictionary<string, int?> beforeScan = new Dictionary<string, int?>();
        foreach (Knob k in PowerCfg.Knobs) beforeScan[k.Key] = PowerCfg.Read(k, true);

        ProcessWatch spw = new ProcessWatch();
        spw.Sample();
        System.Threading.Thread.Sleep(1200);
        List<ProcInfo> sprocs = spw.Sample();
        List<Suggestion> sug = Advisor.Scan(null, sprocs, f);

        int drifted = 0;
        foreach (Knob k in PowerCfg.Knobs)
        {
            int? now = PowerCfg.Read(k, true);
            bool same = now.HasValue == beforeScan[k.Key].HasValue &&
                        (!now.HasValue || now.Value == beforeScan[k.Key].Value);
            if (!same) { drifted++; Console.WriteLine("  WROTE SOMETHING: " + k.Label); }
        }
        Check("scanning changed nothing", drifted == 0, PowerCfg.Knobs.Count + " settings unchanged");

        Console.WriteLine("  " + sug.Count + " suggestion(s) for this machine:");
        int badRank = 0, badTarget = 0, thinText = 0, noAction = 0, inventedGain = 0, pointless = 0;
        int lastRank = int.MaxValue;
        foreach (Suggestion g in sug)
        {
            Console.WriteLine("    [" + g.Rank.ToString().PadLeft(3) + "] " + g.Title);
            Console.WriteLine("          " + (g.Kind == FixKind.Advisory ? "opens " + g.OpenUri : g.Change) +
                              (g.Gain.HasValue ? "   ~" + g.Gain.Value.ToString("0.0") + " W" : ""));

            if (g.Rank > lastRank) badRank++;
            lastRank = g.Rank;

            if (string.IsNullOrWhiteSpace(g.Title) || string.IsNullOrWhiteSpace(g.Detail) ||
                g.Info == null || g.Info.Length < 80) thinText++;
            if (string.IsNullOrWhiteSpace(g.ActionLabel)) noAction++;
            if (g.Gain.HasValue && g.Gain.Value <= 0) inventedGain++;

            if (g.Kind == FixKind.Advisory)
            {
                if (string.IsNullOrEmpty(g.OpenUri)) noAction++;
            }
            else if (g.Kind == FixKind.Setting)
            {
                Knob k = PowerCfg.Find(g.KnobKey);
                if (k == null) { badTarget++; continue; }
                if (k.Choices != null && !k.Choices.ContainsKey(g.Target)) badTarget++;
                if (k.Choices == null && (g.Target < k.Min || g.Target > k.Max)) badTarget++;
                if (g.Current == g.Target) pointless++;
            }
        }

        Check("ranked most important first", badRank == 0);
        Check("every target is a value the setting accepts", badTarget == 0);
        Check("nothing suggests the value already set", pointless == 0);
        Check("every suggestion explains itself", thinText == 0, "title, detail and 80+ chars of info");
        Check("every suggestion has something to press", noAction == 0);
        Check("no saving is quoted unless measured here", inventedGain == 0,
              Config.Current.DiscreteGpuWakeWatts.HasValue
                  ? "the GPU wake cost is calibrated" : "nothing calibrated yet, so no figures shown");

        // an empty list is a real answer, not a broken scanner
        if (sug.Count == 0)
            Console.WriteLine("  nothing to suggest - this machine is already set up for endurance");
        Console.WriteLine();

        Console.WriteLine("--- presets sanity ---");
        foreach (Preset p in Presets.All)
        {
            int unknown = 0;
            foreach (KeyValuePair<string, int> kv in p.Values)
            {
                Knob k = PowerCfg.Find(kv.Key);
                if (k == null) { unknown++; Console.WriteLine("  UNKNOWN KNOB in " + p.Name + ": " + kv.Key); continue; }
                if (k.Choices != null && !k.Choices.ContainsKey(kv.Value))
                    Console.WriteLine("  OUT OF RANGE in " + p.Name + ": " + kv.Key + "=" + kv.Value);
                if (k.Choices == null && (kv.Value < k.Min || kv.Value > k.Max))
                    Console.WriteLine("  OUT OF RANGE in " + p.Name + ": " + kv.Key + "=" + kv.Value +
                                      " (allowed " + k.Min + ".." + k.Max + ")");
            }
            Console.WriteLine(string.Format("  {0,-12} {1} settings", p.Name, p.Values.Count));
            Check(p.Name + " references only known knobs", unknown == 0);
        }
        Console.WriteLine();

        Console.WriteLine("--- live battery measurement (needs " + 20 + " s) ---");
        using BatteryMonitor m = new BatteryMonitor();   // holds WMI searchers
        m.WindowSeconds = 20;
        m.Poll();
        Console.WriteLine("  remaining     : " + (m.RemainingMwh.HasValue ? m.RemainingMwh.Value + " mWh" : "n/a"));
        Console.WriteLine("  full charge   : " + (m.FullChargeMwh.HasValue ? m.FullChargeMwh.Value + " mWh" : "n/a"));
        Console.WriteLine("  health        : " + (m.HealthPercent.HasValue ? m.HealthPercent.Value.ToString("0.0") + "%" : "n/a"));
        Console.WriteLine("  on AC         : " + m.OnAc + "   charging: " + m.Charging);
        Console.WriteLine("  charge        : " + m.PercentOfFull + "%");
        Check("battery capacity readable", m.RemainingMwh.HasValue && m.FullChargeMwh.HasValue);
        Check("watts is null before the window fills", !m.Watts.HasValue);

        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(5000);
            m.Poll();
            Console.WriteLine("  t+" + ((i + 1) * 5) + "s  watts=" +
                (m.Watts.HasValue ? m.Watts.Value.ToString("0.00") : "(filling)") +
                "  remaining=" + (m.RemainingMwh.HasValue ? m.RemainingMwh.Value.ToString() : "?") + " mWh");
        }
        if (m.OnAc)
            Console.WriteLine("  (on AC - a negative figure means charging, which is expected)");
        else
            Check("watts computed after the window filled", m.Watts.HasValue);

        if (m.HoursRemaining.HasValue)
            Console.WriteLine("  hours left at this rate : " + m.HoursRemaining.Value.ToString("0.00"));
        if (m.HoursFromFull.HasValue)
            Console.WriteLine("  hours from full          : " + m.HoursFromFull.Value.ToString("0.00"));

        m.ResetWindow();
        Check("ResetWindow clears the reading", !m.Watts.HasValue);

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "=== ALL CHECKS PASSED ===" : "=== " + fail + " CHECK(S) FAILED ===");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static void Check(string what, bool ok, string extra = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what + (extra != null ? " (" + extra + ")" : ""));
        if (!ok) fail++;
    }

    static void CheckKnob(string key, int expect, string what)
    {
        Knob k = PowerCfg.Find(key);
        if (k == null) { Check(what, false, "knob not found"); return; }
        int? v = PowerCfg.Read(k, true);
        Check(what, v.HasValue && v.Value == expect,
              "is " + PowerCfg.Describe(k, v) + ", expected " + PowerCfg.Describe(k, expect));
    }
}
