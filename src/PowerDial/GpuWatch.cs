using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace PowerDial
{
    public sealed class Finding
    {
        public string Name;
        public bool Ok;
        public string Detail;
        public bool Informational;   // not a problem, just context
    }

    /// <summary>
    /// Watches for software that keeps a switchable discrete GPU awake.
    ///
    /// Only meaningful on a hybrid laptop, where the discrete GPU is supposed to power
    /// down when nothing needs it. On a desktop, or a laptop with a single GPU, there is
    /// no wake cost to avoid and this says so instead of inventing work.
    ///
    /// The cost is deliberately not hardcoded. An awake idle discrete GPU can be anywhere
    /// from about 5 W to over 20 W depending on the part, so the figure shown is whatever
    /// has been measured on this PC and stored in the config, or nothing at all.
    ///
    /// It is also the cost *once*, not per offender: any single one of these is enough to
    /// hold the GPU awake, so removing all of them saves the GPU wake cost, not the sum.
    /// </summary>
    public static class GpuWatch
    {
        const string BgRoot =
            "Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications";
        const string ShadowPlay =
            "SOFTWARE\\NVIDIA Corporation\\Global\\ShadowPlay\\NVSPCAPS";

        /// <summary>process name fragment, friendly label, which vendor it belongs to.</summary>
        static readonly string[][] Suspects = {
            // GPU vendor tools and overlays
            new[] { "NVIDIA Share",        "NVIDIA overlay",              "nvidia" },
            new[] { "nvsphelper",          "NVIDIA overlay helper",       "nvidia" },
            new[] { "NVIDIA App",          "NVIDIA App window",           "nvidia" },
            new[] { "NVIDIA Web Helper",   "NVIDIA web helper",           "nvidia" },
            new[] { "RadeonSoftware",      "AMD Radeon Software",         "amd" },
            new[] { "AMDRSServ",           "AMD Radeon service",          "amd" },
            new[] { "IntelArcControl",     "Intel Arc Control",           "intel" },

            // laptop maker gaming suites, the usual culprits
            new[] { "OmenCommandCenter",   "OMEN Gaming Hub",             "hp" },
            new[] { "LightStudio",         "OMEN Light Studio",           "hp" },
            new[] { "ArmouryCrate",        "ASUS Armoury Crate",          "asus" },
            new[] { "ROGLiveService",      "ROG Live Service",            "asus" },
            new[] { "AsusOptimization",    "ASUS Optimization",           "asus" },
            new[] { "LenovoVantage",       "Lenovo Vantage",              "lenovo" },
            new[] { "ImController",        "Lenovo system controller",    "lenovo" },
            new[] { "MSI Center",          "MSI Center",                  "msi" },
            new[] { "Mystic Light",        "MSI Mystic Light",            "msi" },
            new[] { "PredatorSense",       "Acer PredatorSense",          "acer" },
            new[] { "NitroSense",          "Acer NitroSense",             "acer" },
            new[] { "AWCC",                "Alienware Command Center",    "dell" },
            new[] { "AlienFusion",         "Alienware Fusion",            "dell" },

            // peripheral suites that commonly pin a GPU for lighting effects
            new[] { "Razer Synapse",       "Razer Synapse",               "razer" },
            new[] { "iCUE",                "Corsair iCUE",                "corsair" },
            new[] { "lghub",               "Logitech G HUB",              "logitech" },
            new[] { "SteelSeries",         "SteelSeries GG",              "steelseries" },
        };

        /// <summary>
        /// Packaged apps whose background permission is worth checking: product name
        /// fragment, then which vendor it belongs to.
        ///
        /// Every entry here has been narrowed twice, because both mistakes were made.
        ///
        /// Matching on the publisher id came first and flagged every HP package on the
        /// test machine - printer control, privacy settings, hardware diagnostics.
        /// Matching on a bare brand name was barely better: "OMEN" also catches OMEN Audio
        /// Control, which has nothing to do with graphics. So these name the actual gaming
        /// and lighting suites rather than a brand.
        ///
        /// The vendor tag matters for the same reason it does in Suspects. On a laptop
        /// with AMD integrated graphics and an NVIDIA discrete card, AMD Radeon Software
        /// is driving the part that is *supposed* to be running. Flagging it there is a
        /// false alarm, and a watchdog that cries wolf is one people stop reading.
        /// </summary>
        static readonly string[][] BgPackages = {
            new[] { "OMENCommandCenter", "hp" },
            new[] { "OMENGamingHub",     "hp" },
            new[] { "OMENLightStudio",   "hp" },
            new[] { "ArmouryCrate",      "asus" },
            new[] { "ROGLive",           "asus" },
            new[] { "LenovoVantage",     "lenovo" },
            new[] { "MSICenter",         "msi" },
            new[] { "MysticLight",       "msi" },
            new[] { "PredatorSense",     "acer" },
            new[] { "NitroSense",        "acer" },
            new[] { "AlienwareCommandCenter", "dell" },
            // NVIDIA and AMD: the overlay and capture front-ends, not the settings
            // applets. NVIDIA Control Panel is a thin wrapper round the classic control
            // panel and does not poll the GPU; ShadowPlay and the running overlay
            // processes are checked separately and are what actually holds it up.
            new[] { "NVIDIAApp",         "nvidia" },
            new[] { "GeForceExperience", "nvidia" },
            new[] { "RadeonSoftware",    "amd" },
            new[] { "IntelArcControl",   "intel" },
            // peripheral lighting engines apply whatever the graphics vendor is
            new[] { "iCUE",              "peripheral" },
            new[] { "Synapse",           "peripheral" },
            new[] { "GHUB",              "peripheral" },
            new[] { "SteelSeries",       "peripheral" },
        };

        public static List<Finding> Check()
        {
            List<Finding> list = new List<Finding>();
            Machine.Detect(false);

            GpuInfo disc = Machine.Discrete;

            if (disc == null)
            {
                list.Add(new Finding {
                    Name = "No switchable discrete GPU",
                    Ok = true, Informational = true,
                    Detail = Machine.Gpus != null && Machine.Gpus.Count > 0
                             ? Machine.Gpus[0].Name : "integrated graphics only"
                });
                return list;
            }

            if (!Machine.Hybrid || !Machine.IsPortable)
            {
                list.Add(new Finding {
                    Name = disc.Name,
                    Ok = true, Informational = true,
                    Detail = Machine.IsPortable
                        ? "single GPU - it cannot be powered down"
                        : "desktop - the GPU stays powered, nothing to keep asleep"
                });
                return list;
            }

            // --- hybrid laptop: this is where keeping it asleep is worth real watts ---
            int running = 0;
            foreach (string[] sus in Suspects)
            {
                if (!VendorApplies(sus[2])) continue;
                int n = CountByFragment(sus[0]);
                if (n > 0) running++;
                list.Add(new Finding {
                    Name = sus[1],
                    Ok = n == 0,
                    Detail = n == 0 ? "not running" : n + " process(es) running"
                });
            }

            foreach (KeyValuePair<string, bool> kv in BackgroundPackages())
            {
                if (!kv.Value) running++;
                list.Add(new Finding {
                    Name = kv.Key + " background permission",
                    Ok = kv.Value,
                    Detail = kv.Value ? "Never (correct)" : "allowed - set it to Never"
                });
            }

            if (HasVendor(GpuVendor.Nvidia))
            {
                int? sp = ReadDwordFromBinary(ShadowPlay, "IsShadowPlayEnabled");
                if (sp.HasValue)
                {
                    bool ok = sp.Value == 0;
                    if (!ok) running++;
                    list.Add(new Finding {
                        Name = "NVIDIA ShadowPlay / Instant Replay",
                        Ok = ok,
                        Detail = ok ? "off" : "ON - desktop capture holds the GPU awake"
                    });
                }
            }

            if (running == 0)
                list.Add(new Finding {
                    Name = "Nothing is holding " + disc.Name + " awake",
                    Ok = true, Informational = true,
                    Detail = "it can drop to idle"
                });

            return list;
        }

        /// <summary>
        /// A graphics vendor tool only matters when it belongs to the *discrete* GPU.
        /// Radeon Software driving an integrated Radeon is not what keeps an NVIDIA card
        /// awake, and flagging it there is a false alarm.
        /// </summary>
        static bool VendorApplies(string tag)
        {
            GpuInfo disc = Machine.Discrete;
            if (disc == null) return false;
            switch (tag)
            {
                case "nvidia": return disc.Vendor == GpuVendor.Nvidia;
                case "amd":    return disc.Vendor == GpuVendor.Amd;
                case "intel":  return disc.Vendor == GpuVendor.Intel;
                default:       return true;   // OEM and peripheral suites apply anywhere
            }
        }

        static bool HasVendor(GpuVendor v)
        {
            if (Machine.Gpus == null) return false;
            foreach (GpuInfo g in Machine.Gpus) if (g.Vendor == v) return true;
            return false;
        }

        static int CountByFragment(string fragment)
        {
            int n = 0;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        string name;
                        try { name = p.ProcessName; } catch { continue; }
                        if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) n++;
                    }
                }
            }
            catch { }
            return n;
        }

        /// <summary>Gaming-vendor packages and whether their background permission is denied.</summary>
        static Dictionary<string, bool> BackgroundPackages()
        {
            Dictionary<string, bool> found = new Dictionary<string, bool>();
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(BgRoot))
                {
                    if (root == null) return found;
                    foreach (string pkg in root.GetSubKeyNames())
                    {
                        string product = Pretty(pkg);
                        bool interesting = false;
                        foreach (string[] entry in BgPackages)
                            if (product.Contains(entry[0], StringComparison.OrdinalIgnoreCase) &&
                                VendorApplies(entry[1])) interesting = true;
                        if (!interesting) continue;

                        bool disabled = false;
                        using (RegistryKey k = root.OpenSubKey(pkg))
                        {
                            if (k != null)
                            {
                                object v = k.GetValue("Disabled");
                                if (v != null) { try { disabled = Convert.ToInt32(v) == 1; } catch { } }
                            }
                        }
                        found[product] = disabled;
                    }
                }
            }
            catch { }
            return found;
        }

        /// <summary>"AD2F1837.OMENCommandCenter_v10z8vjag6ke6" -> "OMENCommandCenter".</summary>
        static string Pretty(string pkg)
        {
            string s = pkg;
            int dot = s.IndexOf('.');
            if (dot > 0 && dot < 12) s = s.Substring(dot + 1);
            int und = s.IndexOf('_');
            if (und > 0) s = s.Substring(0, und);
            return s;
        }

        /// <summary>
        /// What the flagged items are costing. Any single waker is enough to hold the GPU
        /// awake, so this is the measured wake cost once - not the sum over offenders,
        /// which is what an earlier version did and badly overstated the total.
        /// Returns null when the cost has never been measured on this PC.
        /// </summary>
        public static double? TotalCost(List<Finding> findings)
        {
            bool any = false;
            foreach (Finding f in findings) if (!f.Ok) any = true;
            if (!any) return 0;
            return Config.Current.DiscreteGpuWakeWatts;
        }

        static int? ReadDwordFromBinary(string path, string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    byte[] b = v as byte[];
                    if (b != null)
                    {
                        if (b.Length >= 4) return BitConverter.ToInt32(b, 0);
                        if (b.Length >= 1) return b[0];
                        return null;
                    }
                    if (v != null) return Convert.ToInt32(v);
                }
            }
            catch { }
            return null;
        }

        public static void OpenBackgroundAppsSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
