using System;
using System.Management;

namespace PowerDial
{
    /// <summary>
    /// Panel backlight, via WMI. Measured cost on this machine: 0.04 W per percentage
    /// point, i.e. about 4 W across the full range - which at idle is over half the
    /// total draw, making this the single biggest thing the user controls directly.
    /// Does not require elevation.
    /// </summary>
    public static class Brightness
    {
        public const double WattsPerPoint = 0.04;

        public static int? Get()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                           "root\\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementObject mo in s.Get())
                        return Convert.ToInt32(mo["CurrentBrightness"]);
                }
            }
            catch { }
            return null;
        }

        /// <summary>Returns null on success, else the error text.</summary>
        public static string Set(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                           "root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    bool any = false;
                    foreach (ManagementObject mo in s.Get())
                    {
                        mo.InvokeMethod("WmiSetBrightness",
                                        new object[] { (uint)1, (byte)percent });
                        any = true;
                    }
                    if (!any) return "no WmiMonitorBrightnessMethods instance - external monitor?";
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
