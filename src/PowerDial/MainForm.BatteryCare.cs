using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace PowerDial
{
    public sealed partial class MainForm
    {
        BatteryCareCard _careCard;
        ChargeLimitCard _chargeLimit;
        int _careVersion = -1;
        long _careMinute = -1;
        ChargeExposure _careExposure = new ChargeExposure();
        CapacityTrend _careTrend = new CapacityTrend();

        void BuildBatteryCare()
        {
            _careCard = new BatteryCareCard { Width = W, Margin = new Padding(0, 0, 0, 10), Tag = "Battery health" };
            _careCard.ChargingHelp.Click += (s, e) => OpenCareSource(BatteryCare.ChargingUrl);
            _careCard.Evidence.Click += (s, e) => OpenCareSource(BatteryCare.CareUrl);
            _careCard.Export.Click += (s, e) => ExportBatteryCare();
            if (OmenChargeControl.IsSupported())
            {
                _chargeLimit = new ChargeLimitCard(Config.Current) { Width = W, Margin = new Padding(0, 0, 0, 12) };
                _root.Controls.Add(_chargeLimit);
                _chargeLimit.Apply.Click += (s, e) => { if (SaveChargeRange()) UpdateChargeLimit(); };
                _chargeLimit.Toggle.Click += (s, e) => ToggleChargeLimit();
            }
            _root.Controls.Add(_careCard);
            _root.Controls.SetChildIndex(_careCard, _root.Controls.GetChildIndex(_cardHealth) + 1);
        }

        bool SaveChargeRange()
        {
            var config = Config.Current;
            int resume = config.ResumeChargePercent, pause = config.PauseChargePercent;
            if (resume == _chargeLimit.Resume.Value && pause == _chargeLimit.Pause.Value) return true;
            config.ResumeChargePercent = _chargeLimit.Resume.Value;
            config.PauseChargePercent = _chargeLimit.Pause.Value;
            if (!Config.TrySave())
            {
                config.ResumeChargePercent = resume;
                config.PauseChargePercent = pause;
                _chargeLimit.Status.Text = "Could not save the selected limits.";
                return false;
            }
            _chargeLimit.UpdatePending();
            return true;
        }

        void ToggleChargeLimit()
        {
            if ((!_elevated || !Config.Current.OmenChargeLimitEnabled) && !SaveChargeRange()) return;
            if (!_elevated) { RelaunchElevated(); return; }
            var config = Config.Current;
            config.OmenChargeLimitEnabled = !config.OmenChargeLimitEnabled;
            if (!Config.TrySave())
            {
                config.OmenChargeLimitEnabled = !config.OmenChargeLimitEnabled;
                _chargeLimit.Status.Text = "Could not save the charge limit setting.";
                return;
            }
            _bat.Poll();
            UpdateChargeLimit();
        }

        bool ReleaseChargeLimit()
        {
            if (!Config.Current.OmenPauseOwned) return true;
            try
            {
                if (_chargeLimit?.Status == null || !_elevated || !_bat.StatusValid || !_bat.OnAc)
                    throw new InvalidOperationException("Reconnect AC and run PowerDial as administrator to restore charging.");
                byte mode = OmenChargeControl.ReadMode();
                if (mode == 4)
                {
                    OmenChargeControl.SetPaused(false);
                    mode = OmenChargeControl.ReadMode();
                }
                if (mode > 1) throw new InvalidOperationException("Automatic charging is awaiting confirmation.");
                Config.Current.OmenPauseOwned = false;
                if (!Config.TrySave())
                {
                    Config.Current.OmenPauseOwned = true;
                    throw new IOException("Could not save charging recovery.");
                }
                return true;
            }
            catch (Exception ex)
            {
                string message = "Charging recovery pending. " + ex.Message;
                if (_chargeLimit?.Status != null && _chargeLimit.Status.Text != message)
                {
                    _chargeLimit.Status.Text = message;
                    Log(message);
                }
                return false;
            }
        }

        void UpdateChargeLimit()
        {
            if (_chargeLimit?.Status == null) return;
            _chargeLimit.ShowCharge(_bat.StatusValid ? _bat.PercentOfFull : null);
            var config = Config.Current;
            var status = _chargeLimit.Status;
            _chargeLimit.Toggle.Text = !_elevated ? "Run as administrator" :
                config.OmenChargeLimitEnabled ? "Disable charge limit" : "Enable charge limit";
            if (!config.OmenChargeLimitEnabled)
            {
                if (ReleaseChargeLimit()) status.Text = "Charge limit off.";
                return;
            }
            if (!_elevated || !_bat.StatusValid || !_bat.OnAc)
            {
                status.Text = !_elevated ? "Charge limit needs administrator access." :
                    "Charge limit waiting for AC power and a valid battery reading.";
                return;
            }
            if (!OmenChargeControl.ShouldHold(config.OmenPauseOwned, _bat.PercentOfFull, config.ResumeChargePercent, config.PauseChargePercent))
            {
                if (ReleaseChargeLimit()) status.Text = $"Charge limit on. Automatic charging allowed up to {config.PauseChargePercent}%.";
                return;
            }
            try
            {
                byte mode = OmenChargeControl.ReadMode();
                if (mode <= 1)
                {
                    if (!config.OmenPauseOwned)
                    {
                        // Record ownership before the write so an interrupted call can be recovered.
                        config.OmenPauseOwned = true;
                        if (!Config.TrySave())
                        {
                            config.OmenPauseOwned = false;
                            throw new IOException("Could not save charging recovery information.");
                        }
                    }
                    OmenChargeControl.SetPaused(true);
                    mode = OmenChargeControl.ReadMode();
                }
                status.Text = mode == 4 && config.OmenPauseOwned ?
                    $"Charging paused. Automatic charging resumes at {config.ResumeChargePercent}%." :
                    "Waiting for HP firmware to confirm the charging state.";
            }
            catch (Exception ex) { status.Text = "Charge limit pending. " + ex.Message; }
        }

        void RefreshBatteryCare()
        {
            if (_careCard == null || _page != "health") return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Machine.HasBattery)
            {
                var points = History.All();
                if (_careVersion != History.Version || _careMinute != now / 60)
                {
                    _careExposure = BatteryCare.Exposure(points, now);
                    _careTrend = BatteryCare.Trend(points, now);
                    _careVersion = History.Version;
                    _careMinute = now / 60;
                }
            }
            _careCard.UpdateData(Machine.HasBattery, _bat.StatusValid, _careExposure, _careTrend);
        }

        void OpenCareSource(string url)
        {
            try { using (Process process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { } }
            catch (Exception ex) { Log("Could not open battery-care source: " + ex.Message); }
        }

        void ExportBatteryCare()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", DefaultExt = "csv",
                FileName = "PowerDial-battery-health.csv", Title = "Export recorded battery health" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, BatteryCare.Csv(History.All()));
                    Log("Battery health CSV saved to " + dialog.FileName + ". Includes retained readings; blank capacity fields were not measured.");
                }
                catch (Exception ex) { Log("Battery health export failed: " + ex.Message); ShowLog(); }
            }
        }
    }
}
