using Microsoft.Win32;
using System;
using System.Management;

namespace PowerDial
{
    // Board 8901 / F.24, verified with Test-OmenChargePause.ps1 on AC.
    internal static class OmenChargeControl
    {
        internal static bool IsSupported()
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return Equals(bios?.GetValue("SystemManufacturer"), "HP")
                && Equals(bios.GetValue("BaseBoardProduct"), "8901")
                && Equals(bios.GetValue("BIOSVersion"), "F.24");
        }

        internal static bool ShouldHold(bool held, int percent, int resume, int pause)
        {
            return percent >= pause || (held && percent > resume);
        }

        internal static byte ReadMode() => Exchange(false, false);

        internal static void SetPaused(bool pause)
        {
            if (Exchange(true, pause) != 0)
                throw new InvalidOperationException("HP firmware declined the charging request.");
        }

        static byte Exchange(bool write, bool pause)
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            using var search = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM hpqBIntM WHERE Active = TRUE"));
            using var devices = search.Get();
            if (devices.Count != 1) throw new InvalidOperationException("Expected one active HP firmware interface.");
            foreach (ManagementObject device in devices)
            using (device)
            using (var dataClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null))
            using (var data = dataClass.CreateInstance())
            using (var input = device.GetMethodParameters("hpqBIOSInt4"))
            {
                data["Sign"] = new byte[] { 83, 69, 67, 85 }; // SECU
                data["Command"] = write ? 2u : 1u;
                data["CommandType"] = 0x2Bu;
                data["Size"] = 4u;
                data["hpqBData"] = new byte[] { 0, (byte)(pause ? 5 : 0), 0, 0 };
                input["InData"] = data;
                using var output = device.InvokeMethod("hpqBIOSInt4", input, null);
                using var reply = output?["OutData"] as ManagementBaseObject;
                if (reply?["rwReturnCode"] is uint code && code == 0 &&
                    reply["Data"] is byte[] bytes && bytes.Length == 4) return bytes[0];
                throw new InvalidOperationException("Invalid HP battery-control reply.");
            }
            throw new InvalidOperationException("HP firmware interface unavailable.");
        }
    }
}
