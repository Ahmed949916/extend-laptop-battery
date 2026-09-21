using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

namespace PowerDial
{
    public sealed class BatterySample
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
    public sealed class BatteryMonitor : IDisposable
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

        // Only the full-charge capacity still comes from WMI, and only until it has been
        // read once. Everything read on the poll now comes from CallNtPowerInformation -
        // see ReadPower. Constructing a searcher per poll once cost this app ~4% of a
        // core; reusing one was the first fix, not needing one at all is the real one.
        ManagementObjectSearcher _capacityQ;

        /// <summary>
        /// False once the syscall has answered. WMI is then never touched again on the
        /// poll path - it is kept only as the fallback for firmware the syscall cannot
        /// read, and for the full-charge capacity, which is read once and rarely moves.
        /// </summary>
        bool _useWmiForStatus;

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
            if (_capacityQ != null) { _capacityQ.Dispose(); _capacityQ = null; }
            GC.SuppressFinalize(this);
        }

        public void Poll()
        {
            LastError = null;
            try
            {
                // The syscall first, every time, and WMI only where it cannot answer.
                // A WMI query marshals through COM into WmiPrvSE.exe, so it wakes a
                // second process and both of them pay - four times a minute, forever,
                // including while this window is hidden in the tray. The same three
                // values come out of one kernel call that allocates nothing.
                if (!_useWmiForStatus && ReadPower()) ReadCapacityOnce();
                else { _useWmiForStatus = true; ReadWmi(); }
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

        /// <summary>
        /// Charge, AC state and charging, from one kernel call.
        ///
        /// SYSTEM_BATTERY_STATE carries the same RemainingCapacity that BatteryStatus
        /// does, in mWh, plus the two flags - with no COM, no second process and no
        /// allocation. Returns false when there is nothing usable in it, which is the
        /// signal to fall back to WMI permanently rather than retrying a syscall that
        /// this firmware evidently does not fill in.
        ///
        /// Its Rate field is deliberately ignored. That is the same instantaneous figure
        /// the ACPI DischargeRate exposes, and on this hardware it returns the invalid
        /// sentinel - which is the whole reason draw is timed from the counter instead.
        /// </summary>
        bool ReadPower()
        {
            SYSTEM_BATTERY_STATE st;
            if (CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, out st,
                                       Marshal.SizeOf(typeof(SYSTEM_BATTERY_STATE))) != 0)
                return false;

            // No battery is a real answer, not a failure - so it counts as handled. A
            // desktop that fell through to WMI here would have gone on asking WmiPrvSE
            // for a battery it does not have, four times a minute, forever.
            if (st.BatteryPresent == 0)
            {
                RemainingMwh = null;
                OnAc = st.AcOnLine != 0;
                Charging = false;
                return true;
            }

            // Unknown capacity is the all-ones sentinel, and that IS a firmware the
            // syscall cannot read - worth falling back for.
            if (st.RemainingCapacity == 0xFFFFFFFF) return false;

            RemainingMwh = unchecked((int)st.RemainingCapacity);
            OnAc = st.AcOnLine != 0;
            Charging = st.Charging != 0;

            if (FullChargeMwh.HasValue && FullChargeMwh.Value > 0)
                PercentOfFull = (int)Math.Round(100.0 * RemainingMwh.Value / FullChargeMwh.Value);
            else if (st.MaxCapacity > 0 && st.MaxCapacity != 0xFFFFFFFF)
                PercentOfFull = (int)Math.Round(100.0 * RemainingMwh.Value / st.MaxCapacity);

            return true;
        }

        /// <summary>
        /// The full-charge capacity, once. It is what health is measured against and what
        /// the percentage divides by, and it moves by a few mWh over months - so there is
        /// nothing to gain from asking WMI for it every fifteen seconds.
        /// </summary>
        void ReadCapacityOnce()
        {
            if (FullChargeMwh.HasValue && FullChargeMwh.Value > 0) return;
            try
            {
                if (_capacityQ == null)
                    _capacityQ = new ManagementObjectSearcher("root\\WMI",
                        "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");

                foreach (ManagementObject mo in _capacityQ.Get())
                {
                    using (mo) { FullChargeMwh = ToInt(mo["FullChargedCapacity"]); }
                    break;
                }
                if (RemainingMwh.HasValue && FullChargeMwh.HasValue && FullChargeMwh.Value > 0)
                    PercentOfFull = (int)Math.Round(100.0 * RemainingMwh.Value / FullChargeMwh.Value);
            }
            catch (Exception ex) { LastError = ex.Message; }
        }

        /// <summary>The original path, kept for firmware the syscall cannot read.</summary>
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

        ManagementObjectSearcher _statusQ;

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

        const int SystemBatteryState = 5;

        /// <summary>
        /// SYSTEM_BATTERY_STATE, as powerbase.h declares it. Capacities and Rate are in
        /// mW / mWh; 0xFFFFFFFF means the firmware does not know.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_BATTERY_STATE
        {
            public byte AcOnLine;
            public byte BatteryPresent;
            public byte Charging;
            public byte Discharging;
            public byte Spare1a, Spare1b, Spare1c, Spare1d;
            public uint MaxCapacity;
            public uint RemainingCapacity;
            public uint Rate;                 // ignored - see ReadPower
            public uint EstimatedTime;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
        }

        // Same reasoning as the kernel32 import below: pinned to System32 so a DLL
        // dropped beside the exe cannot be loaded in its place.
        [DllImport("powrprof.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int CallNtPowerInformation(int level, IntPtr input, int inputSize,
                                                 out SYSTEM_BATTERY_STATE output, int outputSize);

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

        // Pin the search to the system directory. Without it the loader walks a search
        // order that includes the application directory, so a kernel32.dll dropped beside
        // the exe would be loaded in preference - the classic DLL-planting route into a
        // process. This one is a system DLL and has no business being found anywhere else.
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetSystemPowerStatus(out POWER_STATUS status);
    }
}
