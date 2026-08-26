using System;
using System.Collections.Generic;
using System.Threading;
using PowerDial;

class SelfTest
{
    static int fail = 0;

    static void Main()
    {
        Console.WriteLine("=== PowerDial self-test (READ-ONLY - changes nothing) ===");
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

        Console.WriteLine("--- spot-check against values verified by hand this session ---");
        CheckKnob("epp", 80, "EPP on battery should be 80");
        CheckKnob("lid", 1, "lid close should be Sleep");
        CheckKnob("switchable", 0, "switchable graphics should be Force power-saving");
        CheckKnob("cpumin", 5, "min processor state should be 5");
        Console.WriteLine();

        Console.WriteLine("--- brightness ---");
        int? b = Brightness.Get();
        Console.WriteLine("  current : " + (b.HasValue ? b.Value + "%" : "unavailable"));
        Check("brightness readable", b.HasValue);
        if (b.HasValue)
            Console.WriteLine("  backlight cost at this level: ~" +
                (b.Value * Brightness.WattsPerPoint).ToString("0.00") + " W");
        Console.WriteLine();

        Console.WriteLine("--- gpu watchdog ---");
        List<Finding> f = GpuWatch.Check();
        foreach (Finding item in f)
            Console.WriteLine(string.Format("  [{0}] {1,-38} {2}",
                item.Ok ? "OK" : "!!", item.Name, item.Detail));
        Console.WriteLine("  total flagged waste: ~" + GpuWatch.TotalCost(f).ToString("0.0") + " W");
        Check("watchdog produced findings", f.Count >= 7, f.Count + " findings");
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
            Console.WriteLine(string.Format("  {0,-12} {1} settings, brightness {2}",
                p.Name, p.Values.Count,
                p.Brightness.HasValue ? p.Brightness.Value + "%" : "unchanged"));
            Check(p.Name + " references only known knobs", unknown == 0);
        }
        Console.WriteLine();

        Console.WriteLine("--- live battery measurement (needs " + 20 + " s) ---");
        BatteryMonitor m = new BatteryMonitor();
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
