using System;
using System.Management;

namespace PowerDial
{
    /// <summary>
    /// Panel backlight, via WMI. Not available everywhere - plenty of desktops and
    /// external monitors do not expose it, so Machine.BrightnessControllable says
    /// whether this PC does. The watts-per-point cost lives in Config, measured per
    /// display rather than assumed. Does not require elevation.
    /// </summary>
    public static class Brightness
    {
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
