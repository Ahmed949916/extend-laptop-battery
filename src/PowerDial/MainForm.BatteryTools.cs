using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    public sealed partial class MainForm
    {
        readonly BatteryToolsPolicy _batteryPolicy = new BatteryToolsPolicy();
        readonly List<HistPoint> _drainRecords = new List<HistPoint>();
        BatteryToolsCard _toolsCard;
        SessionSummaryCard _sessionsCard;
        double? _drainBaseline;
        long _monitorAt;
        bool? _monitorAc;
        string _segment = Guid.NewGuid().ToString("N");
        bool _powerBusy;
        string _lastBatteryMessage = "No battery alerts yet.";

        void BuildBatteryTools()
        {
            _toolsCard = new BatteryToolsCard(Config.Current.BatteryTools) {
                Width = W, Margin = new Padding(0, 0, 0, 10), Tag = "Battery tools"
            };
            _toolsCard.OptionsChanged += (s, e) => {
                BatteryToolsOptions options = Config.Current.BatteryTools;
                bool enabled = options.AutoEnabled;
                int percent = options.AutoPercent, mode = options.AutoMode;
                _toolsCard.ReadInto(options);
                if (enabled != options.AutoEnabled || percent != options.AutoPercent || mode != options.AutoMode)
                    Config.Current.BatteryState.AutoHandled = false;
                _batteryPolicy.ResetWindow();
                Config.Save();
                RefreshBatteryTools();
            };
            _toolsCard.Snooze.Click += (s, e) => {
                Config.Current.BatteryState.SnoozeUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
                _batteryPolicy.ResetWindow(); Config.Save(); RefreshBatteryTools();
            };
            _toolsCard.Pause.Click += (s, e) => PauseAutomation("Paused from Battery tools");
            _toolsCard.Resume.Click += (s, e) => {
                Config.Current.BatteryState.PauseUntil = 0;
                Config.Current.BatteryState.AutoHandled = false;
                Config.Save(); Log("Battery automation re-armed; it will check the threshold on the next poll.");
                RefreshBatteryTools();
            };
            _toolsCard.Undo.Click += (s, e) => UndoAutomaticChange();
            _root.Controls.Add(_toolsCard);
            RefreshBatteryTools();
        }

        void RefreshBatteryTools()
        {
            if (_toolsCard == null) return;
            BatteryToolsState state = Config.Current.BatteryState;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string automation = !Config.Current.BatteryTools.AutoEnabled ? "Automation is off." :
                state.PauseUntil > now ? "Automation paused until " + LocalTime(state.PauseUntil) + "." :
                state.AutoHandled ? "Threshold handled. Re-arms after charging or above the threshold + 5%." : "Automation is armed.";
            string alerts = state.SnoozeUntil > now ? " Alerts snoozed until " + LocalTime(state.SnoozeUntil) + "." : "";
            _toolsCard.Status.Text = Machine.HasBattery ? automation + alerts + "\n" + _lastBatteryMessage :
                "This PC has no detected battery. Battery alerts and automation are inactive.";
            _toolsCard.Undo.Enabled = state.Undo != null;
        }

        static string LocalTime(long unix) { return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("t"); }

        void PauseAutomation(string reason)
        {
            if (!Config.Current.BatteryTools.AutoEnabled) return;
            BatteryToolsState state = Config.Current.BatteryState;
            state.PauseUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
            state.AutoHandled = false;
            Config.Save();
            Log(reason + "; battery automation paused for one hour.");
            RefreshBatteryTools();
        }

        // Before each battery poll, discard cross-sleep measurements and CPU deltas.
        void CheckMonitorGap()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_monitorAt != 0 && (now <= _monitorAt || now - _monitorAt > 90))
            {
                _segment = Guid.NewGuid().ToString("N");
                ResetDrawWindow(); _procWatch.ResetBaseline(); _lastPoll = DateTime.MinValue;
                _batteryPolicy.ResetWindow();
            }
            _monitorAt = now;
        }

        void CheckPowerSource()
        {
            if (!_bat.StatusValid) return;
            if (_monitorAc.HasValue && _monitorAc.Value != _bat.OnAc)
            {
                _segment = Guid.NewGuid().ToString("N");
                ResetDrawWindow(); _batteryPolicy.ResetWindow();
            }
            _monitorAc = _bat.OnAc;
        }

        void CheckBatteryTools()
        {
            if (_powerBusy || _pendingRow != null || !Machine.HasBattery) return;
            Config config = Config.Current;
            BatteryDecision decision = _batteryPolicy.Evaluate(config.BatteryTools, config.BatteryState,
                new BatteryObservation { T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Valid = _bat.StatusValid,
                    Ac = _bat.OnAc, Percent = _bat.PercentOfFull, Watts = _bat.Watts }, _drainBaseline);
            bool saved = !decision.Changed || Config.TrySave();
            if (decision.Alert != null) NotifyBattery(decision.Alert);
            if (decision.Mode != 0)
            {
                if (saved) ApplyAutomaticMode(decision.Mode);
                else NotifyBattery("Automation was skipped because its state could not be saved. Check Diagnostics.");
            }
            if (_page == "tools" && Visible) RefreshBatteryTools();
        }

        void NotifyBattery(string message)
        {
            _lastBatteryMessage = message;
            Log(message);
            if (_tray != null)
                try { _tray.ShowBalloonTip(7000, "PowerDial battery", message, ToolTipIcon.Info); }
                catch (Exception) { } // The persistent log and Tools status still contain it.
            RefreshBatteryTools();
        }

        void ApplyAutomaticMode(int code)
        {
            Preset preset = Presets.ByCode(code);
            if (preset == null || _powerBusy) return;
            Preset current = Presets.Match();
            if (current != null && current.Code == code)
            { Log("Battery automation: " + preset.Friendly + " is already active."); return; }
            // Capture all readable DC values before touching anything. Nothing is written
            // if a supported setting cannot be read, or if the undo record cannot be saved.
            AutomaticChange change = new AutomaticChange { Scheme = PowerCfg.ActiveScheme() };
            if (!Guid.TryParse(change.Scheme, out _)) { NotifyBattery("Automation skipped: active power scheme could not be read."); return; }
            foreach (KeyValuePair<string, int> value in preset.Values)
            {
                Knob knob = PowerCfg.Find(value.Key);
                if (knob == null || !PowerCfg.Exists(knob)) continue;
                int? before = PowerCfg.Read(knob, true);
                if (!before.HasValue) { NotifyBattery("Automation skipped: could not read " + knob.Label + "."); return; }
                if (before.Value == value.Value) continue;
                change.Before[value.Key] = before.Value;
                change.Applied[value.Key] = value.Value;
            }
            if (change.Before.Count == 0) return;
            Config.Current.BatteryState.Undo = change;
            if (!Config.TrySave()) { NotifyBattery("Automation skipped: its undo record could not be saved."); return; }
            bool applied = WriteProfile(preset, null, true);
            NotifyBattery(applied ? "Battery at " + _bat.PercentOfFull + "%: applied " + preset.Friendly +
                ". You can undo it in Battery tools." : "Automatic mode was only partly applied. Check Diagnostics or undo it in Battery tools.");
        }

        void UndoAutomaticChange()
        {
            if (_powerBusy) return;
            BatteryToolsState state = Config.Current.BatteryState;
            AutomaticChange change = state.Undo;
            if (change == null || change.Before == null || change.Applied == null) return;
            if (!string.Equals(PowerCfg.ActiveScheme(), change.Scheme, StringComparison.OrdinalIgnoreCase))
            { NotifyBattery("Undo skipped: the active power scheme has changed. Select the earlier scheme first."); return; }
            PauseAutomation("Undoing automatic settings");
            int failed = 0, restored = 0, skipped = 0;
            RunBusy(_toolsCard.Undo, delegate {
                foreach (KeyValuePair<string, int> old in change.Before)
                {
                    Knob knob = PowerCfg.Find(old.Key);
                    int applied;
                    if (knob == null || !change.Applied.TryGetValue(old.Key, out applied)) { skipped++; continue; }
                    int? current = PowerCfg.Read(knob, true);
                    if (!current.HasValue) { failed++; continue; }
                    if (current.Value != applied) { skipped++; continue; } // Preserve later manual edits.
                    string error = PowerCfg.WriteDc(knob, old.Value);
                    if (error != null || PowerCfg.Read(knob, true) != old.Value) { failed++; Log("Undo failed: " + knob.Label + " " + error); }
                    else restored++;
                }
                ResetDrawWindow(); LoadValues(); RefreshWatch(); RefreshFixes();
            });
            if (failed == 0) state.Undo = null;
            Config.Save(); RefreshBasic();
            NotifyBattery("Automatic change undo: " + restored + " restored, " + skipped + " left as subsequently changed, " + failed + " failed.");
        }

        void RecordForAlerts(HistPoint point)
        {
            _drainRecords.Add(point);
            if (_drainRecords.Count > 1500) _drainRecords.RemoveRange(0, _drainRecords.Count - 1500);
            _drainBaseline = BatteryToolsPolicy.Baseline(_drainRecords, point.T);
        }

        void RefreshSessions()
        {
            if (_sessionsCard == null) return;
            _sessionsCard.Sessions = SessionSummaries.From(History.All(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                _bat.StatusValid && !_bat.OnAc ? _segment : null);
            _sessionsCard.Fit();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateCountdownTimer();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateCountdownTimer();
        }

        void UpdateCountdownTimer()
        {
            if (_tick == null || _bat == null || IsDisposed || Disposing) return;
            if (Visible && WindowState != FormWindowState.Minimized && !_bat.Watts.HasValue) _tick.Start();
            else _tick.Stop();
        }
    }
}
