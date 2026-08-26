using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace PowerDial
{
    public class Finding
    {
        public string Name;
        public bool Ok;
        public string Detail;
        public double CostWatts;   // measured cost when this is in the bad state
    }

    /// <summary>
    /// Watches the things that wake the discrete GPU.
    ///
    /// This is the most valuable part of the app. On this laptop an awake-but-idle
    /// RTX 3050 Ti draws about 17 W while showing 0% utilisation and 0 MiB allocated -
    /// more than the entire rest of the system at idle. Three separate things were
    /// found doing it, and none of them looked expensive in Task Manager:
    ///
    ///   NVIDIA Instant Replay / overlay   ~11.8 W
    ///   OMEN Command Center background    ~11.1 W
    ///   OMEN Light Studio background      ~12.0 W
    ///
    /// The OMEN ones relaunch at boot as packaged background tasks via sihost.exe, so
    /// disabling their scheduled tasks does nothing. The load-bearing fix is the per-app
    /// background permission, which an OMEN update can silently flip back on - hence
    /// this watchdog.
    /// </summary>
    public static class GpuWatch
    {
        const string BgRoot =
            "Software\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications";
        const string ShadowPlay =
            "SOFTWARE\\NVIDIA Corporation\\Global\\ShadowPlay\\NVSPCAPS";

        static readonly string[] OmenPackages = {
            "AD2F1837.OMENCommandCenter_v10z8vjag6ke6",
            "AD2F1837.OMENLightStudio_v10z8vjag6ke6"
        };

        // process name -> (friendly label, measured cost when running)
        static readonly string[][] Wakers = {
            new[] { "OmenCommandCenterBackground", "OMEN Command Center", "11.1" },
            new[] { "LightStudio-background",      "OMEN Light Studio",   "12.0" },
            new[] { "NVIDIA Share",                "NVIDIA overlay host", "11.8" },
            new[] { "nvsphelper64",                "NVIDIA overlay helper", "0" },
            new[] { "NVIDIA App",                  "NVIDIA App window",   "11.0" },
        };

        public static List<Finding> Check()
        {
            List<Finding> list = new List<Finding>();

            foreach (string[] w in Wakers)
            {
                int n = 0;
                try { n = Process.GetProcessesByName(w[0]).Length; } catch { }
                double cost = 0;
                double.TryParse(w[2], out cost);
                list.Add(new Finding
                {
                    Name = w[1],
                    Ok = n == 0,
                    Detail = n == 0 ? "not running" : n + " process(es) running",
                    CostWatts = n == 0 ? 0 : cost
                });
            }

            foreach (string pkg in OmenPackages)
            {
                int? disabled = null;
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(BgRoot + "\\" + pkg))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue("Disabled");
                            if (v != null) disabled = Convert.ToInt32(v);
                        }
                    }
                }
                catch { }

                string shortName = pkg.StartsWith("AD2F1837.OMENCommandCenter")
                    ? "Gaming Hub background permission"
                    : "Light Studio background permission";

                bool ok = disabled.HasValue && disabled.Value == 1;
                list.Add(new Finding
                {
                    Name = shortName,
                    Ok = ok,
                    Detail = ok ? "Never (correct)"
                                : (disabled.HasValue ? "ALLOWED - flip to Never" : "not set - allowed by default"),
                    CostWatts = ok ? 0 : 11.0
                });
            }

            int? sp = ReadDwordFromBinary(ShadowPlay, "IsShadowPlayEnabled");
            bool spOk = sp.HasValue && sp.Value == 0;
            list.Add(new Finding
            {
                Name = "NVIDIA ShadowPlay / Instant Replay",
                Ok = spOk,
                Detail = !sp.HasValue ? "not present (fine)" : (spOk ? "off" : "ON - desktop capture holds the GPU awake"),
                CostWatts = spOk || !sp.HasValue ? 0 : 11.8
            });

            return list;
        }

        public static double TotalCost(List<Finding> findings)
        {
            double t = 0;
            foreach (Finding f in findings) if (!f.Ok) t += f.CostWatts;
            return t;
        }

        /// <summary>ShadowPlay stores its flags as 4-byte REG_BINARY, not REG_DWORD.</summary>
        static int? ReadDwordFromBinary(string path, string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    if (v is byte[])
                    {
                        byte[] b = (byte[])v;
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

        /// <summary>Opens the Windows page where the background permission is changed.</summary>
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
