using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace PowerDial
{
    public sealed partial class MainForm
    {
        BatteryCareCard _careCard;
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
            _root.Controls.Add(_careCard);
            _root.Controls.SetChildIndex(_careCard, _root.Controls.GetChildIndex(_cardHealthAdvice) + 1);
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
