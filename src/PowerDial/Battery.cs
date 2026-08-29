using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace PowerDial
{
    public class BatterySample
    {
        public DateTime At;
        public int RemainingMwh;
    }

    /// <summary>
    /// Live power measurement.
    ///
    /// HP firmware on this machine never reports an instantaneous rate - the ACPI
    /// DischargeRate field returns 0x80000000 (an invalid sentinel). So draw is derived
    /// the same way it was measured by hand: time the battery energy counter over a
    /// window and divide. That means the first reading needs WindowSeconds to appear.
    /// </summary>
    public class BatteryMonitor : IDisposable
    {
        /// <summary>
        /// Original design capacity in mWh, detected per machine. 0 when it cannot be
        /// established, in which case health is reported as unknown rather than guessed.
        ///
        /// Some firmware rewrites the live design-capacity field to equal the learned
        /// capacity, which hides all degradation, so Machine falls back to the highest
        /// capacity ever recorded in the Windows battery report.
        /// </summary>
        public static int OriginalDesignMwh { get { return Config.EffectiveDesignMwh; } }

        public int WindowSeconds = 60;

        readonly List<BatterySample> _samples = new List<BatterySample>();

        // Constructing a ManagementObjectSearcher on every poll is what made this app
        // cost ~4% of a CPU core - absurd for something whose job is saving power.
        // Built once and reused, it is a rounding error.
        ManagementObjectSearcher _statusQ;
        ManagementObjectSearcher _capacityQ;

        public int? RemainingMwh { get; private set; }
        public int? FullChargeMwh { get; private set; }
        public bool OnAc { get; private set; }
        public bool Charging { get; private set; }
        public int PercentOfFull { get; private set; }
        public string LastError { get; private set; }

        /// <summary>Positive = draining, negative = charging. Null until the window fills.</summary>
        public double? Watts { get; private set; }

        /// <summary>Null when there is no battery, or no trustworthy design capacity.</summary>
        public double? HealthPercent
        {
            get
            {
                if (!FullChargeMwh.HasValue || FullChargeMwh.Value <= 0) return null;
                int design = OriginalDesignMwh;
                if (design <= 0) return null;
                // a design figure at or below the current charge tells us nothing useful
                if (design < FullChargeMwh.Value) return null;
                return 100.0 * FullChargeMwh.Value / design;
            }
        }

        /// <summary>False on a desktop, so the interface can drop the battery panels.</summary>
        public static bool Present { get { return Machine.HasBattery; } }

        /// <summary>Hours left at the present rate, or null if not draining.</summary>
        public double? HoursRemaining
        {
            get
            {
                if (!Watts.HasValue || Watts.Value <= 0.1 || !RemainingMwh.HasValue) return null;
                return (RemainingMwh.Value / 1000.0) / Watts.Value;
            }
        }

        /// <summary>Hours from a full charge at the present rate.</summary>
        public double? HoursFromFull
        {
            get
            {
                if (!Watts.HasValue || Watts.Value <= 0.1 || !FullChargeMwh.HasValue) return null;
                return (FullChargeMwh.Value / 1000.0) / Watts.Value;
            }
        }

        /// <summary>
        /// Release the two WMI searchers. They are built once and reused - constructing
        /// them per poll is what once cost this app 3.75% of a core - so they live as long
        /// as the app does and are let go here rather than left to the finaliser.
        /// </summary>
        public void Dispose()
        {
            if (_statusQ != null) { _statusQ.Dispose(); _statusQ = null; }
            if (_capacityQ != null) { _capacityQ.Dispose(); _capacityQ = null; }
            GC.SuppressFinalize(this);
        }

        public void Poll()
        {
            LastError = null;
            try
            {
                ReadWmi();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // fall back to the cheap Win32 call so the UI still shows something
                POWER_STATUS ps;
                if (GetSystemPowerStatus(out ps))
                {
                    OnAc = ps.ACLineStatus == 1;
                    if (ps.BatteryLifePercent <= 100) PercentOfFull = ps.BatteryLifePercent;
                }
                return;
            }

            if (!RemainingMwh.HasValue) return;

            DateTime now = DateTime.UtcNow;
            _samples.Add(new BatterySample { At = now, RemainingMwh = RemainingMwh.Value });

            // discard anything older than twice the window
            while (_samples.Count > 2 &&
                   (now - _samples[0].At).TotalSeconds > WindowSeconds * 2)
                _samples.RemoveAt(0);

            // find the oldest sample at least WindowSeconds back
            BatterySample basis = null;
            for (int i = 0; i < _samples.Count; i++)
            {
                if ((now - _samples[i].At).TotalSeconds >= WindowSeconds) basis = _samples[i];
                else break;
            }
            if (basis == null) { Watts = null; return; }

            double hours = (now - basis.At).TotalHours;
            if (hours <= 0) { Watts = null; return; }
            Watts = (basis.RemainingMwh - RemainingMwh.Value) / hours / 1000.0;
        }

        /// <summary>Throw away accumulated samples - call after changing a setting so the
        /// next reading reflects the new state rather than averaging across the change.</summary>
        public void ResetWindow()
        {
            _samples.Clear();
            Watts = null;
        }

        void ReadWmi()
        {
            if (_statusQ == null)
                _statusQ = new ManagementObjectSearcher("root\\WMI",
                    "SELECT RemainingCapacity, PowerOnline, Charging FROM BatteryStatus");
            if (_capacityQ == null)
                _capacityQ = new ManagementObjectSearcher("root\\WMI",
                    "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");

            foreach (ManagementObject mo in _statusQ.Get())
            {
                using (mo)
                {
                    RemainingMwh = ToInt(mo["RemainingCapacity"]);
                    OnAc = ToBool(mo["PowerOnline"]);
                    Charging = ToBool(mo["Charging"]);
                }
                break;
            }
            foreach (ManagementObject mo in _capacityQ.Get())
            {
                using (mo) { FullChargeMwh = ToInt(mo["FullChargedCapacity"]); }
                break;
            }
            if (RemainingMwh.HasValue && FullChargeMwh.HasValue && FullChargeMwh.Value > 0)
                PercentOfFull = (int)Math.Round(100.0 * RemainingMwh.Value / FullChargeMwh.Value);
        }

        static int? ToInt(object o)
        {
            if (o == null) return null;
            try { return Convert.ToInt32(o); } catch { return null; }
        }

        static bool ToBool(object o)
        {
            if (o == null) return false;
            try { return Convert.ToBoolean(o); } catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetSystemPowerStatus(out POWER_STATUS status);
    }
}
