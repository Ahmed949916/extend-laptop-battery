using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Xml;

namespace PowerDial
{
    public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

    public class GpuInfo
    {
        public string Name;
        public GpuVendor Vendor;
        public bool Discrete;
        public long AdapterRam;
    }

    /// <summary>
    /// Everything about the machine that used to be hardcoded for one laptop.
    ///
    /// Detected once at startup and cached, because several of these calls are slow
    /// (the battery report shells out to powercfg and takes a second or two).
    /// Every field degrades to something honest when it cannot be determined - the app
    /// says "unknown" rather than inventing a number.
    /// </summary>
    public static class Machine
    {
        static bool _done;

        public static bool HasBattery { get; private set; }
        public static bool IsPortable { get; private set; }
        public static string Manufacturer { get; private set; }
        public static string Model { get; private set; }
        public static string CpuName { get; private set; }
        public static int CpuCores { get; private set; }
        public static List<GpuInfo> Gpus { get; private set; }
        public static bool BrightnessControllable { get; private set; }

        /// <summary>Best available original design capacity, mWh. 0 when unknown.</summary>
        public static int DesignCapacityMwh { get; private set; }
        /// <summary>Where DesignCapacityMwh came from, for honest display.</summary>
        public static string DesignCapacitySource { get; private set; }

        public static GpuInfo Discrete
        {
            get
            {
                if (Gpus == null) return null;
                foreach (GpuInfo g in Gpus) if (g.Discrete) return g;
                return null;
            }
        }

        public static bool Hybrid
        {
            get
            {
                if (Gpus == null || Gpus.Count < 2) return false;
                bool disc = false, integ = false;
                foreach (GpuInfo g in Gpus) { if (g.Discrete) disc = true; else integ = true; }
                return disc && integ;
            }
        }

        public static string Summary()
        {
            string s = (Manufacturer + " " + Model).Trim();
            if (s.Length == 0) s = "this PC";
            s += IsPortable ? "  ·  laptop" : "  ·  desktop";
            if (!HasBattery) s += ", no battery";
            if (Gpus != null && Gpus.Count > 0)
            {
                GpuInfo d = Discrete;
                s += "  ·  " + (d != null ? d.Name : Gpus[0].Name);
                if (Hybrid) s += " (hybrid)";
            }
            return s;
        }

        public static void Detect(bool force)
        {
            if (_done && !force) return;
            _done = true;

            Gpus = new List<GpuInfo>();
            Manufacturer = ""; Model = ""; CpuName = "";

            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                           "SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem"))
                    foreach (ManagementObject mo in q.Get())
                    {
                        Manufacturer = Str(mo["Manufacturer"]);
                        Model = Str(mo["Model"]);
                        // 2 = Mobile. Chassis type is checked below as a second opinion.
                        IsPortable = Num(mo["PCSystemType"]) == 2;
                        break;
                    }
            }
            catch { }

            if (!IsPortable)
            {
                try
                {
                    using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                               "SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                        foreach (ManagementObject mo in q.Get())
                        {
                            ushort[] types = mo["ChassisTypes"] as ushort[];
                            if (types != null)
                                foreach (ushort t in types)
                                    // 8 portable, 9 laptop, 10 notebook, 11 hand held,
                                    // 14 sub notebook, 30-32 tablet/convertible/detachable
                                    if (t == 8 || t == 9 || t == 10 || t == 11 || t == 14 ||
                                        t == 30 || t == 31 || t == 32) IsPortable = true;
                            break;
                        }
                }
                catch { }
            }

            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                           "SELECT Name, NumberOfCores FROM Win32_Processor"))
                    foreach (ManagementObject mo in q.Get())
                    {
                        CpuName = Str(mo["Name"]).Trim();
                        CpuCores = Num(mo["NumberOfCores"]);
                        break;
                    }
            }
            catch { }

            DetectGpus();
            DetectBattery();

            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                           "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    BrightnessControllable = false;
                    foreach (ManagementObject mo in q.Get()) { BrightnessControllable = true; break; }
                }
            }
            catch { BrightnessControllable = false; }
        }

        static void DetectGpus()
        {
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                           "SELECT Name, PNPDeviceID, AdapterRAM FROM Win32_VideoController"))
                    foreach (ManagementObject mo in q.Get())
                    {
                        string name = Str(mo["Name"]);
                        string pnp = Str(mo["PNPDeviceID"]).ToUpperInvariant();
                        if (name.Length == 0) continue;
                        // ignore Windows' software fallback adapter
                        if (name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("Basic Display", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        GpuVendor v = GpuVendor.Unknown;
                        if (pnp.Contains("VEN_10DE")) v = GpuVendor.Nvidia;
                        else if (pnp.Contains("VEN_1002")) v = GpuVendor.Amd;
                        else if (pnp.Contains("VEN_8086")) v = GpuVendor.Intel;
                        else
                        {
                            string n = name.ToUpperInvariant();
                            if (n.Contains("NVIDIA") || n.Contains("GEFORCE")) v = GpuVendor.Nvidia;
                            else if (n.Contains("RADEON") || n.Contains("AMD")) v = GpuVendor.Amd;
                            else if (n.Contains("INTEL")) v = GpuVendor.Intel;
                        }

                        Gpus.Add(new GpuInfo {
                            Name = name, Vendor = v, AdapterRam = Num64(mo["AdapterRAM"]),
                            Discrete = LooksDiscrete(name, v)
                        });
                    }
            }
            catch { }

            // If nothing looked discrete but there are two real adapters, the one that is
            // not the integrated vendor is almost certainly the discrete part.
            if (Gpus.Count >= 2)
            {
                bool any = false;
                foreach (GpuInfo g in Gpus) if (g.Discrete) any = true;
                if (!any)
                    foreach (GpuInfo g in Gpus)
                        if (g.Vendor == GpuVendor.Nvidia) { g.Discrete = true; break; }
            }
        }

        /// <summary>Heuristic. Integrated parts are named for their family, discrete ones
        /// for their model line, so the name is the most reliable signal available.</summary>
        static bool LooksDiscrete(string name, GpuVendor v)
        {
            string n = name.ToUpperInvariant();
            if (v == GpuVendor.Nvidia)
                return n.Contains("GEFORCE") || n.Contains("RTX") || n.Contains("GTX") ||
                       n.Contains("QUADRO") || n.Contains("NVIDIA");
            if (v == GpuVendor.Amd)
                return n.Contains(" RX ") || n.StartsWith("RX") || n.Contains("RADEON PRO") ||
                       n.Contains("FIREPRO") || n.Contains("W6") || n.Contains("W7");
            if (v == GpuVendor.Intel)
                return n.Contains("ARC");
            return false;
        }

        static void DetectBattery()
        {
            HasBattery = false;
            DesignCapacityMwh = 0;
            DesignCapacitySource = "unknown";

            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                           "SELECT DesignCapacity FROM Win32_Battery"))
                    foreach (ManagementObject mo in q.Get())
                    {
                        HasBattery = true;
                        int d = Num(mo["DesignCapacity"]);
                        if (d > 0) { DesignCapacityMwh = d; DesignCapacitySource = "reported by the battery"; }
                        break;
                    }
            }
            catch { }

            if (!HasBattery) return;    // desktop: nothing further to find

            if (DesignCapacityMwh <= 0)
            {
                try
                {
                    using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                               "root\\WMI", "SELECT DesignedCapacity FROM BatteryStaticData"))
                        foreach (ManagementObject mo in q.Get())
                        {
                            int d = Num(mo["DesignedCapacity"]);
                            if (d > 0) { DesignCapacityMwh = d; DesignCapacitySource = "battery firmware"; }
                            break;
                        }
                }
                catch { }
            }

            // The best source, and the one that copes with firmware that rewrites design
            // capacity to match the learned value (which hides all degradation): the
            // highest full-charge capacity ever recorded in the Windows battery report.
            int fromHistory = DesignFromBatteryReport();
            if (fromHistory > DesignCapacityMwh)
            {
                DesignCapacityMwh = fromHistory;
                DesignCapacitySource = "highest ever recorded in the Windows battery report";
            }
        }

        /// <summary>
        /// Runs `powercfg /batteryreport /xml` and takes the largest capacity it mentions.
        /// Slow (a second or two) so it is only called during detection.
        /// </summary>
        public static int DesignFromBatteryReport()
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                "powerdial-batteryreport-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powercfg",
                    "/batteryreport /xml /output \"" + tmp + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000)) return 0;
                }
                if (!File.Exists(tmp)) return 0;

                XmlDocument doc = new XmlDocument();
                doc.XmlResolver = null;
                doc.Load(tmp);

                int best = 0;
                foreach (XmlNode n in doc.SelectNodes("//*"))
                {
                    if (n.Attributes == null) continue;
                    best = Math.Max(best, AttrInt(n, "DesignCapacity"));
                    best = Math.Max(best, AttrInt(n, "FullChargeCapacity"));
                }
                return best;
            }
            catch { return 0; }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        static int AttrInt(XmlNode n, string attr)
        {
            XmlAttribute a = n.Attributes[attr];
            int v;
            if (a != null && int.TryParse(a.Value, out v) && v > 0) return v;
            return 0;
        }

        static string Str(object o) { return o == null ? "" : o.ToString(); }
        static int Num(object o) { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
        static long Num64(object o) { try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; } }
    }
}
