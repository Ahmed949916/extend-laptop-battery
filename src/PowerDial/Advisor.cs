using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PowerDial
{
    /// <summary>What pressing a suggestion's button actually does.</summary>
    public enum FixKind
    {
        Setting,      // writes one power setting on the battery side
        Advisory      // nothing this app can safely write - opens the place you do it
    }

    public class Suggestion
    {
        public string Id;
        public string Title;        // one line, says what to do
        public string Detail;       // why it is worth doing, two lines at most
        public string Info;         // the long version, behind the info icon
        public string ActionLabel;  // button text
        public FixKind Kind;
        public int Rank;            // higher sorts first

        public string KnobKey;      // Setting only
        public int Target;
        public int Current;
        public string NowText;      // human form of the current value
        public string ThenText;     // human form of the target

        /// <summary>
        /// Watts this is worth. Null far more often than not, and deliberately so: it is
        /// only ever filled from something measured on this PC. A number copied from
        /// another machine would be wrong by several times, and wrong numbers here are
        /// worse than none because they are what people act on.
        /// </summary>
        public double? Gain;

        public string OpenUri;      // Advisory only

        public bool CanApply { get { return Kind != FixKind.Advisory; } }

        public string Change
        {
            get { return Kind == FixKind.Advisory ? "" : NowText + "  →  " + ThenText; }
        }
    }

    /// <summary>
    /// Looks at how this PC is set up right now and says what would make the battery last
    /// longer. Read-only: nothing here writes anything until Apply is called.
    ///
    /// Two rules shape the whole thing.
    ///
    /// First, only suggest what is actually wrong. Every check reads the live value and
    /// stays quiet when it is already sensible, so an empty list means "nothing left to
    /// do" rather than "not implemented". A list that always has twelve items in it is one
    /// nobody reads.
    ///
    /// Second, never invent a saving. Most of these have no honest watt figure attached
    /// because the figure depends on hardware nobody has measured here. Ranking carries
    /// the priority instead, and the text says what the setting does rather than promising
    /// a number.
    /// </summary>
    public static class Advisor
    {
        /// <summary>
        /// Background CPU above this share of one core is worth mentioning. A policy
        /// threshold, not a hardware measurement - it is about when the list is worth
        /// interrupting someone over, not about what any particular chip costs.
        /// </summary>
        const double BackgroundCpuThreshold = 8.0;

        public static List<Suggestion> Scan(BatteryMonitor bat, List<ProcInfo> procs, List<Finding> findings)
        {
            List<Suggestion> list = new List<Suggestion>();
            Machine.Detect(false);

            AddGpuWakers(list, findings);
            AddBackgroundLoad(list, procs);

            // The battery side of a power scheme does nothing on a machine that has no
            // battery. Suggesting those writes on a desktop would be theatre.
            if (Machine.HasBattery)
            {
                AddPowerSettings(list);
            }

            list.Sort(delegate (Suggestion a, Suggestion b) { return b.Rank.CompareTo(a.Rank); });
            return list;
        }

        // ------------------------------------------------------------------ checks

        static void AddGpuWakers(List<Suggestion> list, List<Finding> findings)
        {
            GpuInfo disc = Machine.Discrete;
            if (disc == null || !Machine.Hybrid || !Machine.IsPortable) return;

            if (findings == null) findings = GpuWatch.Check();
            int flagged = 0;
            string first = null;
            foreach (Finding f in findings)
            {
                if (f.Ok) continue;
                flagged++;
                if (first != null) continue;
                // findings are named for the check ("X background permission"); the
                // sentence below wants the thing, not the check
                first = f.Name;
                int cut = first.IndexOf(" background permission", StringComparison.Ordinal);
                if (cut > 0) first = first.Substring(0, cut);
            }
            if (flagged == 0) return;

            list.Add(new Suggestion {
                Id = "gpu",
                Kind = FixKind.Advisory,
                Rank = 100,
                Title = flagged + " thing" + (flagged == 1 ? "" : "s") + " can hold " + disc.Name + " awake",
                Detail = "Usually worth more than every other item on this list put together. " +
                         "Set each one's Background apps permission to Never" +
                         (first == null ? "." : ", starting with " + first + "."),
                Info = "A switchable discrete GPU is supposed to power down when nothing needs it. Held " +
                       "awake it can draw anywhere from about 5 W to over 20 W while reporting 0% " +
                       "utilisation, which is why it is invisible in Task Manager.\n\n" +
                       "Any single one of the flagged items is enough to keep it up, so this is worth the " +
                       "wake cost once rather than the sum of the entries.\n\n" +
                       "There is no button that fixes this properly. Several of these relaunch at every " +
                       "boot as packaged background tasks, so ending the process or disabling a scheduled " +
                       "task achieves nothing - the per-app Background apps permission is the one that " +
                       "holds. This opens the Windows page where you set it. The GPU watch section lower " +
                       "down lists exactly what was found.",
                ActionLabel = "Open Windows settings",
                OpenUri = "ms-settings:appsfeatures",
                Gain = Config.Current.DiscreteGpuWakeWatts
            });
        }

        static void AddBackgroundLoad(List<Suggestion> list, List<ProcInfo> procs)
        {
            if (procs == null || procs.Count == 0) return;
            double cpu = ProcessWatch.TotalCpu(procs, true);
            if (cpu < BackgroundCpuThreshold) return;

            List<ProcInfo> top = ProcessWatch.TopBy(procs, true, true, 3);
            string names = "";
            foreach (ProcInfo p in top)
            {
                if (p.Self) continue;
                if (names.Length > 0) names += ", ";
                names += p.Name + " " + p.CpuPercent.ToString("0.0") + "%";
            }

            list.Add(new Suggestion {
                Id = "background",
                Kind = FixKind.Advisory,
                Rank = 40,
                Title = "Background processes are using " + cpu.ToString("0.0") + "% of one core",
                Detail = (names.Length > 0 ? "Worst right now: " + names + ". " : "") +
                         "Nothing with a window is doing this - it is all running behind your back.",
                Info = "This counts every process that has no visible window, which is the work you are " +
                       "not choosing to do. A CPU that never gets to idle is a CPU drawing power all the " +
                       "time, and it is the one thing on this list that no power setting can fix.\n\n" +
                       "Updaters, sync clients, telemetry agents and vendor tray software are the usual " +
                       "cause. The Since recording began table further down names whichever has burned the " +
                       "most CPU across every session, which is a fairer accusation than a single sample.\n\n" +
                       "This opens the Windows startup apps page. Nothing is ended for you: killing a " +
                       "process you did not choose to run is a good way to break something quietly, and a " +
                       "process stopped by hand comes straight back at the next boot anyway.",
                ActionLabel = "Open startup apps",
                OpenUri = "ms-settings:startupapps"
            });
        }


        static void AddPowerSettings(List<Suggestion> list)
        {
            int? epp = Dc("epp");
            if (epp.HasValue && epp.Value < 70)
                Add(list, "epp", 80, 90,
                    "Raise the energy performance preference to 80%",
                    "It is at " + epp.Value + "%, which tells the CPU to chase clock speed. This is usually " +
                    "the single biggest setting on the page and costs little in responsiveness.",
                    "This is a hint to the CPU about how eagerly to chase speed. Low numbers make it jump " +
                    "to high clocks at the smallest provocation; high numbers let it settle for slower, far " +
                    "more efficient ones.\n\n" +
                    "It is a preference rather than a cap, so full speed is still there when something " +
                    "genuinely needs it - which is why it usually costs less in feel than it saves in " +
                    "power. Windows often leaves it near the middle on battery.\n\n" +
                    "80 is the value the Balanced profile uses. If it feels sluggish, drop the slider to " +
                    "70 rather than putting it back where it was.",
                    "Set it to 80%");

            int? boost = Dc("boost");
            if (boost.HasValue && (boost.Value == 2 || boost.Value == 5))
                Add(list, "boost", 4, 70,
                    "Stop turbo boost being aggressive on battery",
                    "It is set to " + Describe("boost", boost) + ", the hungriest of the seven options and a " +
                    "common Windows default. Efficient, aggressive keeps the burst without the parking.",
                    "This controls how eagerly the CPU enters turbo and how long it stays there. The " +
                    "Efficient options hand that judgement to the chip's own logic instead of Windows " +
                    "pushing for maximum clocks.\n\n" +
                    "Aggressive is not the same as fast. It keeps clocks high after the work is finished, " +
                    "which burns power without making anything feel quicker. Efficient, aggressive still " +
                    "boosts hard for a burst, then lets go.\n\n" +
                    "Change the energy performance preference first if it is also low - measure between " +
                    "the two rather than stacking them blind.",
                    "Make it efficient");

            if (Machine.Hybrid)
            {
                int? sw = Dc("switchable");
                if (sw.HasValue && sw.Value >= 2)
                    Add(list, "switchable", 0, 65,
                        "Steer graphics work to the efficient GPU",
                        "It is set to " + Describe("switchable", sw) + " on battery. Integrated graphics " +
                        "handle browsing and video for a fraction of the power.",
                        "This asks Windows to prefer the integrated GPU over the discrete one while on " +
                        "battery. For browsing, documents and video the integrated part is more than " +
                        "enough and costs a fraction as much.\n\n" +
                        "Be sceptical of what it is worth. The setting comes from the graphics driver, and " +
                        "how much notice a given driver takes of it varies. Apply it, watch the power draw " +
                        "chart for a minute, and put it back if nothing moves.\n\n" +
                        "It does not disable anything. Games and anything that asks for the fast GPU by " +
                        "name still get it.",
                        "Prefer the efficient GPU");
            }

            int? cpumin = Dc("cpumin");
            if (cpumin.HasValue && cpumin.Value > 10)
                Add(list, "cpumin", 5, 50,
                    "Let the CPU idle properly again",
                    "The minimum processor speed is pinned at " + cpumin.Value + "%, so it never fully " +
                    "rests. Nothing feels faster for it.",
                    "This is the floor the CPU is never allowed to drop below. At 5% it can wind almost " +
                    "all the way down between keystrokes, which is where most of an idle laptop's " +
                    "efficiency comes from.\n\n" +
                    "Raising it does not make anything faster - work still ramps the clocks up on " +
                    "demand. It only stops the chip resting in the gaps, which on a machine that is " +
                    "mostly idle is nearly all of the time.\n\n" +
                    "There is no good reason to run this above 5%.",
                    "Drop it to 5%");

            int? sleep = Dc("sleepidle");
            if (sleep.HasValue && sleep.Value == 0)
                Add(list, "sleepidle", 900, 48,
                    "Let it sleep when you walk away",
                    "Sleep after is set to never on battery, so an untouched laptop stays awake until the " +
                    "battery is gone. This is how one ends up flat in a bag.",
                    "How long an untouched machine waits before sleeping. Set to never, it does not - it " +
                    "runs at full idle draw through lunch, through a meeting, and overnight.\n\n" +
                    "This costs you nothing while you are working. It is purely about the time you are not " +
                    "at the keyboard, which on most days is more hours than the time you are.\n\n" +
                    "15 minutes is what the Balanced profile uses. Worth knowing which kind of sleep this " +
                    "PC does: older S3 sleep is genuinely low power, while Modern Standby keeps working in " +
                    "the background and drains noticeably more. Run powercfg /a in a terminal to find out.",
                    "Sleep after 15 min");

            int? lid = Dc("lid");
            if (lid.HasValue && lid.Value == 0)
                Add(list, "lid", 1, 46,
                    "Make closing the lid actually do something",
                    "It is set to do nothing, so the machine keeps running with the screen off. Closed in a " +
                    "bag that means full draw and nowhere for the heat to go.",
                    "What happens when you shut the lid. Do nothing is a surprisingly common default, and " +
                    "the failure it produces is nasty: the laptop runs at full idle power inside a closed " +
                    "bag, gets hot with no airflow, and comes out empty.\n\n" +
                    "Sleep is the right answer unless you deliberately run the machine lid-closed on an " +
                    "external monitor - in which case leave this one alone, it is doing what you want.\n\n" +
                    "This changes the battery side only. Plugged in, the lid keeps doing whatever it does " +
                    "now.",
                    "Sleep on close");

            int? video = Dc("videoidle");
            if (video.HasValue && (video.Value == 0 || video.Value > 900))
                Add(list, "videoidle", 300, 30,
                    "Turn the screen off sooner when idle",
                    "It is set to " + Describe("videoidle", video) + ". The backlight is one of the largest " +
                    "single draws in the machine and there is no reason to light an empty room.",
                    "How long the screen stays lit with no input. The backlight is worth real power, so " +
                    "this is not a trivial setting - but it only ever helps once you have walked away. It " +
                    "changes nothing about what you draw while actually using the machine.\n\n" +
                    "To cut backlight power while you are working, use the brightness slider instead.\n\n" +
                    "5 minutes is what the Balanced profile uses. Shorter than about 2 minutes gets " +
                    "irritating during anything you read rather than type.",
                    "Screen off after 5 min");

            int? hib = Dc("hibernateidle");
            if (hib.HasValue && hib.Value == 0)
                Add(list, "hibernateidle", 7200, 25,
                    "Hibernate after a couple of hours asleep",
                    "Hibernate is set to never, so a sleeping machine keeps trickling power indefinitely. " +
                    "Two days asleep is enough to find it empty.",
                    "How long the machine stays asleep before writing memory to disk and switching off " +
                    "completely. Sleep still draws a trickle; hibernate draws essentially nothing.\n\n" +
                    "Without it, a laptop left asleep over a weekend wakes up flat - and on Modern Standby " +
                    "machines, which keep doing background work while apparently asleep, considerably " +
                    "sooner than that.\n\n" +
                    "The cost is a few seconds longer to resume, and it needs hibernation to be enabled on " +
                    "the PC at all. If powercfg refuses this one, run powercfg /hibernate on from an " +
                    "administrator terminal first.",
                    "Hibernate after 2 h");

            int? pcie = Dc("pcie");
            if (pcie.HasValue && pcie.Value < 2)
                Add(list, "pcie", 2, 20,
                    "Let the PCI Express bus idle down",
                    "It is set to " + Describe("pcie", pcie) + " rather than Maximum. Small, safe, and " +
                    "already correct on most machines - yours is one of the exceptions.",
                    "Lets the internal expansion bus drop into low-power states between transfers. " +
                    "Maximum is the best setting for battery life and is normally already selected, which " +
                    "is why this suggestion rarely appears.\n\n" +
                    "The saving is modest - this is not where the watts are - but there is no real " +
                    "downside either. Link power management is well supported hardware; the worst case is " +
                    "a fractionally longer wake from idle on a device you will not notice.\n\n" +
                    "Listed for completeness rather than because it will change your afternoon.",
                    "Set it to Maximum");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Current battery-side value, or null when Windows has no such setting here.</summary>
        static int? Dc(string key)
        {
            Knob k = PowerCfg.Find(key);
            if (k == null || !PowerCfg.Exists(k)) return null;
            return PowerCfg.Read(k, true);
        }

        static string Describe(string key, int? v)
        {
            Knob k = PowerCfg.Find(key);
            return k == null ? "unknown" : PowerCfg.Describe(k, v);
        }

        static void Add(List<Suggestion> list, string key, int target, int rank,
                        string title, string detail, string info, string action)
        {
            Knob k = PowerCfg.Find(key);
            if (k == null) return;
            int? cur = PowerCfg.Read(k, true);
            if (!cur.HasValue || cur.Value == target) return;

            list.Add(new Suggestion {
                Id = key,
                Kind = FixKind.Setting,
                KnobKey = key,
                Rank = rank,
                Target = target,
                Current = cur.Value,
                NowText = PowerCfg.Describe(k, cur),
                ThenText = PowerCfg.Describe(k, target),
                Title = title,
                Detail = detail,
                Info = info,
                ActionLabel = action
            });
        }

        // ------------------------------------------------------------------ apply

        /// <summary>
        /// Carries out one suggestion. Returns null on success, otherwise what went wrong.
        /// Every write is read straight back and compared before this claims anything.
        /// </summary>
        public static string Apply(Suggestion s)
        {
            if (s == null) return "nothing to apply";

            if (s.Kind == FixKind.Advisory)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(s.OpenUri) { UseShellExecute = true });
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }


            Knob k = PowerCfg.Find(s.KnobKey);
            if (k == null) return "no such setting";
            string werr = PowerCfg.WriteDc(k, s.Target);
            if (werr != null) return werr;

            int? v = PowerCfg.Read(k, true);
            if (!v.HasValue || v.Value != s.Target)
                return "did not stick - it reads back as " + PowerCfg.Describe(k, v);
            return null;
        }
    }
}
