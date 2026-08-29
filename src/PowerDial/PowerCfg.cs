using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace PowerDial
{
    /// <summary>One tunable Windows power setting.</summary>
    public class Knob
    {
        public string Key;          // short id used by presets
        public string Label;
        public string SubGroup;
        public string Guid;
        public int Min;
        public int Max;
        public string Unit;         // "%", "s", or null when Choices is set
        public Dictionary<int, string> Choices;   // null => numeric slider
        public string Note;         // one line under the title
        public bool Hidden;         // not shown in the Windows Power Options GUI
        public bool Basic;          // timeout / behaviour: does not change draw while in use
        public string Info;         // full explanation shown by the info icon
    }

    /// <summary>
    /// Reads power settings straight from the registry, because powercfg /q refuses to
    /// display hidden settings, and writes them with powercfg.exe.
    /// Only the DC / on-battery side is ever written. AC is left alone by design.
    /// </summary>
    public static class PowerCfg
    {
        public const string SUB_PROCESSOR  = "54533251-82be-4824-96c1-47b60b740d00";
        public const string SUB_VIDEO      = "7516b95f-f776-4464-8c53-06167f40cc99";
        public const string SUB_SLEEP      = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        public const string SUB_SWITCHABLE = "e276e160-7cb0-43c6-b20b-73f5dce39954";
        public const string SUB_BUTTONS    = "4f971e89-eebd-4455-a8de-9e59040e7347";
        public const string SUB_PCIE       = "501a4d13-42af-4429-9fd1-a8218c268e20";

        const string SchemesRoot  = "SYSTEM\\CurrentControlSet\\Control\\Power\\User\\PowerSchemes";
        const string SettingsRoot = "SYSTEM\\CurrentControlSet\\Control\\Power\\PowerSettings";
        const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

        public static readonly List<Knob> Knobs = new List<Knob>
        {
            // ---- impact: these change how much power the machine draws while in use ----
            new Knob {
                Key = "epp", Label = "Energy performance preference",
                SubGroup = SUB_PROCESSOR, Guid = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863",
                Min = 0, Max = 100, Unit = "%", Hidden = true,
                Note = "Usually the best single lever. 0 = all performance, 100 = all efficiency.",
                Info = "Tells the CPU how hard to chase speed. Low numbers make it jump to high clocks eagerly; " +
                       "high numbers make it settle for slower, far more efficient ones. It is a hint rather than " +
                       "a cap, so full speed is still available when something genuinely needs it.\n\n" +
                       "On many laptops this is worth more than everything else here combined, and costs " +
                       "little in responsiveness. Windows often leaves it near the middle on battery.\n\n" +
                       "Raise it, then watch the power draw chart for a minute to see what it actually " +
                       "bought you on this machine. Back off to 70 if 80 feels sluggish."
            },
            new Knob {
                Key = "cpumax", Label = "Maximum processor speed",
                SubGroup = SUB_PROCESSOR, Guid = "bc5038f7-23e0-4960-96da-33abaf5935ec",
                Min = 20, Max = 100, Unit = "%",
                Note = "A hard ceiling. 99% switches boost off completely.",
                Info = "A hard limit on clock speed, expressed against this CPU base clock. Anything above " +
                       "100% is boost territory, so 99% locks boost out entirely.\n\n" +
                       "This is the blunt version of Energy performance preference. It removes boost even for " +
                       "brief bursts, where boost is often the efficient choice: finishing quickly lets the CPU " +
                       "go back to sleep sooner.\n\n" +
                       "Prefer the preference slider. Reach for this only if that is not enough."
            },
            new Knob {
                Key = "cpumin", Label = "Minimum processor speed",
                SubGroup = SUB_PROCESSOR, Guid = "893dee8e-2bef-41e0-89c6-b55d0929964c",
                Min = 0, Max = 100, Unit = "%",
                Note = "Leave at 5%. Raising it burns power doing nothing.",
                Info = "The floor the CPU is never allowed to drop below. At 5% it can idle almost all the way " +
                       "down between keystrokes, which is where most of an idle laptop's efficiency comes from.\n\n" +
                       "Raising it does not make anything feel faster. It just stops the CPU resting. There is " +
                       "no good reason to change this."
            },
            new Knob {
                Key = "boost", Label = "Turbo boost behaviour",
                SubGroup = SUB_PROCESSOR, Guid = "be337238-0d82-4146-a960-4f3749d470c7",
                Hidden = true,
                Choices = new Dictionary<int, string> {
                    { 0, "Off" }, { 1, "Conservative" }, { 2, "Aggressive" },
                    { 3, "Efficient, conservative" }, { 4, "Efficient, aggressive" },
                    { 5, "Aggressive up to base clock" }, { 6, "Efficient, aggressive up to base clock" }
                },
                Note = "Windows often defaults this to Aggressive on battery, the hungriest option.",
                Info = "Controls how eagerly the CPU enters turbo and how long it stays there. The Efficient " +
                       "options hand that decision to the chip's own logic instead of Windows pushing for " +
                       "maximum clocks.\n\n" +
                       "Aggressive is the most power-hungry of the seven and a common default on battery. " +
                       "Efficient, aggressive keeps burst responsiveness while stopping the CPU parking " +
                       "at high clocks.\n\n" +
                       "Change the preference slider first, and measure before stacking this on top."
            },
            new Knob {
                Key = "switchable", Label = "Graphics switching",
                SubGroup = SUB_SWITCHABLE, Guid = "a1662ab2-9d34-4e53-ba8b-2639b9e20857",
                Choices = new Dictionary<int, string> {
                    { 0, "Always use the efficient GPU" }, { 1, "Prefer the efficient GPU" },
                    { 2, "Prefer performance" }, { 3, "Always use the fast GPU" }
                },
                Note = "Effect varies by machine. Measure before trusting it.",
                Info = "Asks Windows to steer work towards the integrated GPU rather than the discrete one. " +
                       "Integrated graphics handle browsing and video for a fraction of the power.\n\n" +
                       "Be sceptical of this one. It is supplied by the graphics driver, and its effect on " +
                       "a discrete card from another vendor is not guaranteed. Change it, watch the power " +
                       "draw chart, and put it back if nothing moves.\n\n" +
                       "The GPU watch section is what actually catches a discrete GPU held awake."
            },

            // ---- basic: timeouts and behaviour. None of these change draw while you work ----
            new Knob {
                Key = "videoidle", Label = "Turn the screen off after",
                SubGroup = SUB_VIDEO, Guid = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e",
                Min = 0, Max = 3600, Unit = "s", Basic = true,
                Note = "Only matters once you have walked away.",
                Info = "How long the screen stays lit with no input. The backlight is worth up to 4 W, so this " +
                       "is real money, but only while you are away from the machine. It changes nothing about " +
                       "what you draw while actually using it.\n\n" +
                       "It changes nothing while you are actually using the machine."
            },
            new Knob {
                Key = "sleepidle", Label = "Sleep after",
                SubGroup = SUB_SLEEP, Guid = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da",
                Min = 0, Max = 7200, Unit = "s", Basic = true,
                Note = "Idle timeout, not a power setting.",
                Info = "How long before an untouched machine goes to sleep. Older S3 sleep is genuinely low " +
                       "power; Modern Standby keeps working in the background and drains more. Run " +
                       "powercfg /a from a terminal to see which this PC uses.\n\n" +
                       "Set to 0 it never sleeps, which is how a laptop ends up flat in a bag."
            },
            new Knob {
                Key = "hibernateidle", Label = "Hibernate after",
                SubGroup = SUB_SLEEP, Guid = "9d7815a6-7ee4-497e-8888-515a05f02364",
                Min = 0, Max = 28800, Unit = "s", Basic = true,
                Note = "Protects a sleeping laptop from draining overnight.",
                Info = "How long it stays asleep before writing memory to disk and switching off completely. " +
                       "Sleep still trickles power; hibernate uses essentially none.\n\n" +
                       "Without this, a laptop left asleep for a couple of days wakes up empty."
            },
            new Knob {
                Key = "lid", Label = "When the lid closes",
                SubGroup = SUB_BUTTONS, Guid = "5ca83367-6e45-459f-a27b-476b1d01c936",
                Hidden = true, Basic = true,
                Choices = new Dictionary<int, string> {
                    { 0, "Do nothing" }, { 1, "Sleep" }, { 2, "Hibernate" }, { 3, "Shut down" }
                },
                Note = "Set to Do nothing, a laptop runs itself flat in a closed bag.",
                Info = "What happens when you shut the lid. Set to Do nothing, the machine keeps running with " +
                       "the screen off - closed in a bag it runs until the battery dies, getting hot the " +
                       "whole time. Worth checking, because it is a common default.\n\n" +
                       "Sleep is the right answer unless you deliberately run it lid-closed on an external monitor."
            },
            new Knob {
                Key = "pcie", Label = "PCI Express power saving",
                SubGroup = SUB_PCIE, Guid = "ee12f906-d277-404b-b6da-e5fa1a576df5",
                Basic = true,
                Choices = new Dictionary<int, string> {
                    { 0, "Off" }, { 1, "Moderate" }, { 2, "Maximum" }
                },
                Note = "Usually already at maximum, in which case there is nothing to gain.",
                Info = "Lets the internal expansion bus idle down between transfers. Maximum is the best " +
                       "setting for battery life and is often already selected, in which case changing " +
                       "it can only make things worse.\n\n" +
                       "Shown for completeness rather than because it needs attention."
            },
        };

        /// <summary>True when Windows actually defines this setting on this PC. The
        /// switchable-graphics group in particular only exists on some hybrid systems,
        /// so rows for settings that are not there get dropped rather than shown dead.</summary>
        public static bool Exists(Knob k)
        {
            try
            {
                using (RegistryKey r = Registry.LocalMachine.OpenSubKey(
                           SettingsRoot + "\\" + k.SubGroup + "\\" + k.Guid))
                    return r != null;
            }
            catch { return false; }
        }

        public static Knob Find(string key)
        {
            foreach (Knob k in Knobs) if (k.Key == key) return k;
            return null;
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static string _schemeCache;
        static DateTime _schemeCacheAt = DateTime.MinValue;

        public static string ActiveScheme()
        {
            // Read() is called once per knob per refresh, so without this the registry
            // gets hit ~20 times for a value that changes almost never.
            if (_schemeCache != null && (DateTime.UtcNow - _schemeCacheAt).TotalSeconds < 10)
                return _schemeCache;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(SchemesRoot))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("ActivePowerScheme");
                        if (v != null)
                        {
                            _schemeCache = v.ToString();
                            _schemeCacheAt = DateTime.UtcNow;
                            return _schemeCache;
                        }
                    }
                }
            }
            catch { }
            return BalancedGuid;
        }

        public static string ActiveSchemeName()
        {
            string outp = Run("powercfg", "/getactivescheme");
            int a = outp.IndexOf('(');
            int b = outp.LastIndexOf(')');
            if (a >= 0 && b > a) return outp.Substring(a + 1, b - a - 1);
            return "(unknown)";
        }

        /// <summary>Current value, or null when there is no stored value and no default.</summary>
        public static int? Read(Knob knob, bool onBattery)
        {
            string valueName = onBattery ? "DCSettingIndex" : "ACSettingIndex";
            string scheme = ActiveScheme();

            int? v = ReadDword(SchemesRoot + "\\" + scheme + "\\" + knob.SubGroup + "\\" + knob.Guid, valueName);
            if (v.HasValue) return v;

            string defRoot = SettingsRoot + "\\" + knob.SubGroup + "\\" + knob.Guid + "\\DefaultPowerSchemeValues\\";
            v = ReadDword(defRoot + scheme, valueName);
            if (v.HasValue) return v;

            return ReadDword(defRoot + BalancedGuid, valueName);
        }

        /// <summary>True when the value is stored in the scheme rather than inherited from a default.</summary>
        public static bool IsExplicit(Knob knob, bool onBattery)
        {
            string valueName = onBattery ? "DCSettingIndex" : "ACSettingIndex";
            return ReadDword(SchemesRoot + "\\" + ActiveScheme() + "\\" + knob.SubGroup + "\\" + knob.Guid,
                             valueName).HasValue;
        }

        static int? ReadDword(string path, string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    if (v == null) return null;
                    return Convert.ToInt32(v);
                }
            }
            catch { return null; }
        }

        /// <summary>Writes the battery-side value. Returns null on success, else the error text.</summary>
        public static string WriteDc(Knob knob, int value)
        {
            return Write(knob, value, true);
        }

        /// <summary>
        /// Write the plugged-in side. Separate from WriteDc and never called by the
        /// advisor, the profiles or the restore point - those are all about battery life
        /// and have no business touching AC. Only the explicit Plugged in switch in the
        /// settings section reaches this, so a stray write cannot happen by accident.
        /// </summary>
        public static string WriteAc(Knob knob, int value)
        {
            return Write(knob, value, false);
        }

        public static string Write(Knob knob, int value, bool onBattery)
        {
            string err = RunChecked("powercfg",
                (onBattery ? "/setdcvalueindex " : "/setacvalueindex ") +
                "SCHEME_CURRENT " + knob.SubGroup + " " + knob.Guid + " " + value);
            if (err != null) return err;
            // powercfg only commits a change when the scheme is re-activated
            return RunChecked("powercfg", "/setactive SCHEME_CURRENT");
        }

        public static string Run(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return (so + se).Trim();
                }
            }
            catch (Exception ex) { return "ERROR: " + ex.Message; }
        }

        static string RunChecked(string exe, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (p.ExitCode != 0)
                    {
                        string msg = (se + " " + so).Trim();
                        if (msg.Length == 0) msg = "powercfg exited with code " + p.ExitCode;
                        if (msg.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("elevated", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("denied", StringComparison.OrdinalIgnoreCase))
                            msg = "needs administrator - use Run as admin";
                        return msg;
                    }
                    return null;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string Describe(Knob knob, int? value)
        {
            if (!value.HasValue) return "unknown";
            if (knob.Choices != null)
            {
                string choice;
                if (knob.Choices.TryGetValue(value.Value, out choice)) return choice;
                return value.Value.ToString(CultureInfo.CurrentCulture);
            }
            if (knob.Unit == "s")
            {
                int s = value.Value;
                if (s == 0) return "never";
                if (s % 3600 == 0) return (s / 3600) + " h";
                if (s % 60 == 0) return (s / 60) + " min";
                return s + " s";
            }
            return value.Value + (knob.Unit == null ? "" : knob.Unit);
        }
    }
}
