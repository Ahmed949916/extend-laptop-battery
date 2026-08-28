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
    ///
    /// Longest goes further than Endurance and is the only one that turns turbo off
    /// outright. Worth knowing why that is not obviously the winner: boost finishing a
    /// burst quickly lets the CPU return to idle sooner, and idle is where the savings
    /// really are, so disabling it can cost as much as it saves on some machines. It is
    /// offered because on others it plainly helps - which is exactly why the app tells you
    /// to A/B the draw rather than trusting the label. For the same reason cpumax stays at
    /// 99 (boost off) rather than throttling to a fraction of base clock, which reliably
    /// makes everything slower without reliably drawing less over the whole task.
    /// </summary>
    public static class Presets
    {
        public static readonly List<Preset> All = new List<Preset>
        {
            new Preset {
                Name = "Longest",
                Blurb = "Everything traded for runtime. Boost off, dimmest screen, sleeps quickly.",
                Brightness = 20,
                Values = new Dictionary<string, int> {
                    { "epp", 100 },        // all efficiency, no chasing clocks at all
                    { "cpumax", 99 },      // 99 rather than lower: see the note below
                    { "boost", 0 },        // Off - the only preset that disables turbo outright
                    { "cpumin", 5 },
                    { "videoidle", 60 },
                    { "sleepidle", 180 },
                    { "hibernateidle", 1800 },
                    { "lid", 1 },          // Sleep
                    { "switchable", 0 },   // always the efficient GPU
                    { "pcie", 2 },         // maximum link power saving
                }
            },
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
