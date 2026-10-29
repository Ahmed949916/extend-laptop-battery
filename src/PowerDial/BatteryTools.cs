using System;
using System.Collections.Generic;

namespace PowerDial
{
    public sealed class BatteryToolsOptions
    {
        public bool LowAlert { get; set; } = true;
        public int LowPercent { get; set; } = 20;
        public bool ChargeAlert { get; set; }
        public int ChargePercent { get; set; } = 80;
        public bool DrainAlert { get; set; } = true;
        public bool AutoEnabled { get; set; }
        public int AutoPercent { get; set; } = 25;
        public int AutoMode { get; set; } = 2;

        internal void Normalise()
        {
            LowPercent = Math.Clamp(LowPercent, 5, 50);
            ChargePercent = Math.Clamp(ChargePercent, 50, 100);
            AutoPercent = Math.Clamp(AutoPercent, 5, 50);
            if (AutoMode != 1 && AutoMode != 2 && AutoMode != 3) AutoMode = 2;
        }
    }

    public sealed class AutomaticChange
    {
        public string Scheme { get; set; }
        public Dictionary<string, int> Before { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> Applied { get; set; } = new Dictionary<string, int>();
    }

    public sealed class BatteryToolsState
    {
        public bool LowSent { get; set; }
        public bool ChargeSent { get; set; }
        public long DrainAfter { get; set; }
        public long SnoozeUntil { get; set; }
        public long PauseUntil { get; set; }
        public bool AutoHandled { get; set; }
        public AutomaticChange Undo { get; set; }
    }

    internal sealed class BatteryObservation
    {
        public long T;
        public bool Valid, Ac;
        public int Percent;
        public double? Watts;
    }

    internal sealed class BatteryDecision
    {
        public bool Changed;
        public string Alert;
        public int Mode;
    }

    /// <summary>Pure policy: no system writes, timers or notifications. All timing is injected.</summary>
    internal sealed class BatteryToolsPolicy
    {
        long _last, _highSince;
        double _highThreshold;
        bool? _ac;

        public void ResetWindow() { _highSince = 0; _last = 0; _ac = null; }

        public BatteryDecision Evaluate(BatteryToolsOptions options, BatteryToolsState state,
                                        BatteryObservation sample, double? baseline)
        {
            BatteryDecision result = new BatteryDecision();
            if (!sample.Valid || sample.Percent < 0 || sample.Percent > 100)
            { ResetWindow(); return result; }
            if (_last == 0 || sample.T <= _last || sample.T - _last > 90 || (_ac.HasValue && _ac.Value != sample.Ac))
                _highSince = 0;
            _last = sample.T;
            _ac = sample.Ac;

            if (sample.Ac || sample.Percent > options.LowPercent + 5)
            { if (state.LowSent) result.Changed = true; state.LowSent = false; }
            if (!sample.Ac || sample.Percent < options.ChargePercent - 5)
            { if (state.ChargeSent) result.Changed = true; state.ChargeSent = false; }
            if (sample.Ac || sample.Percent > options.AutoPercent + 5)
            { if (state.AutoHandled) result.Changed = true; state.AutoHandled = false; }

            // One attempt per discharge/threshold crossing, even after a failed write.
            // Persist this decision before applying, so DoEvents/restarts cannot retry it.
            if (options.AutoEnabled && !sample.Ac && sample.Percent <= options.AutoPercent &&
                !state.AutoHandled && sample.T >= state.PauseUntil)
            {
                state.AutoHandled = true;
                result.Changed = true;
                result.Mode = options.AutoMode;
            }

            if (sample.T < state.SnoozeUntil) { _highSince = 0; return result; }
            if (options.LowAlert && !sample.Ac && sample.Percent <= options.LowPercent && !state.LowSent)
            {
                state.LowSent = true;
                result.Changed = true;
                result.Alert = "Battery is at " + sample.Percent + "%. Open Battery tools to review your power mode.";
            }
            else if (options.ChargeAlert && sample.Ac && sample.Percent >= options.ChargePercent && !state.ChargeSent)
            {
                state.ChargeSent = true;
                result.Changed = true;
                result.Alert = "Charge target reached (" + sample.Percent + "%). This is a reminder; PowerDial cannot stop charging.";
            }

            bool canCheck = options.DrainAlert && !sample.Ac && sample.Watts.HasValue &&
                            double.IsFinite(sample.Watts.Value) && sample.Watts.Value > 0 && baseline.HasValue;
            if (!canCheck) { _highSince = 0; return result; }
            if (_highSince == 0) _highThreshold = Math.Max(baseline.Value * 1.75, baseline.Value + 5);
            if (sample.Watts.Value < _highThreshold) { _highSince = 0; return result; }
            if (_highSince == 0) _highSince = sample.T;
            if (sample.T - _highSince >= 300 && sample.T >= state.DrainAfter && result.Alert == null)
            {
                state.DrainAfter = sample.T + 1800;
                result.Changed = true;
                result.Alert = "Power draw has stayed above " + _highThreshold.ToString("0.0") +
                    " W for five minutes. Check CPU activity and suspected GPU drain; a demanding task may explain it.";
            }
            return result;
        }

        // Compare against prior measured use, excluding the last ten minutes so the
        // current spike does not immediately become its own normal. No model constants.
        internal static double? Baseline(List<HistPoint> history, long now)
        {
            List<double> watts = new List<double>();
            for (int i = history.Count - 1; i >= 0 && watts.Count < 60; i--)
            {
                HistPoint p = history[i];
                if (p.T < now - 7 * 86400) break;
                if (p.T > now - 600 || p.Ac || !double.IsFinite(p.W) || p.W <= 0.05) continue;
                watts.Add(p.W);
            }
            if (watts.Count < 20) return null;
            watts.Sort();
            return watts[watts.Count / 2];
        }
    }
}
