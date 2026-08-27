using System.Collections.Generic;

namespace PowerDial
{
    public class Preset
    {
        public string Name;
        public string Blurb;
        public int? Brightness;                        // null = leave alone
        public Dictionary<string, int> Values;         // knob key -> battery-side value
    }

    /// <summary>
    /// Every preset writes the DC / on-battery side only. Plugged-in behaviour is never
    /// touched, which is what kept the tuning safe while it was being measured.
    ///
    /// Balanced is the sensible starting point on any machine. Endurance trades
    /// responsiveness for the last watt or so; Full speed gives the CPU its head while
    /// still on battery. Apply one, then watch the power draw chart to see what it did
    /// here - the numbers differ by hardware.
    /// </summary>
    public static class Presets
    {
        public static readonly List<Preset> All = new List<Preset>
        {
            new Preset {
                Name = "Endurance",
                Blurb = "Every last minute. Boost locked out, dim screen, quick sleep.",
                Brightness = 25,
                Values = new Dictionary<string, int> {
                    { "epp", 90 },
                    { "cpumax", 99 },
                    { "boost", 1 },        // Enabled (conservative)
                    { "cpumin", 5 },
                    { "videoidle", 120 },
                    { "sleepidle", 300 },
                    { "hibernateidle", 3600 },
                    { "lid", 1 },          // Sleep
                    { "switchable", 0 },
                }
            },
            new Preset {
                Name = "Balanced",
                Blurb = "The sensible default. Efficient without feeling slow.",
                Brightness = 30,
                Values = new Dictionary<string, int> {
                    { "epp", 80 },
                    { "cpumax", 100 },
                    { "boost", 4 },        // Efficient Aggressive
                    { "cpumin", 5 },
                    { "videoidle", 300 },
                    { "sleepidle", 900 },
                    { "hibernateidle", 7200 },
                    { "lid", 1 },
                    { "switchable", 0 },
                }
            },
            new Preset {
                Name = "Full speed",
                Blurb = "Unrestricted CPU while on battery. Expect much shorter runtime.",
                Brightness = 60,
                Values = new Dictionary<string, int> {
                    { "epp", 50 },
                    { "cpumax", 100 },
                    { "boost", 2 },        // Aggressive
                    { "cpumin", 5 },
                    { "videoidle", 600 },
                    { "sleepidle", 1800 },
                    { "hibernateidle", 10800 },
                    { "lid", 1 },
                    { "switchable", 1 },
                }
            },
        };

        public static Preset Find(string name)
        {
            foreach (Preset p in All) if (p.Name == name) return p;
            return null;
        }
    }
}
