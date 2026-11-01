using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    internal sealed class BatteryCareCard : Card
    {
        static readonly string[] Purposes = { "Mostly at a desk", "Travel / full runtime", "Long-term storage" };
        readonly Label _exposure, _trend, _plan;
        readonly Picker _purpose = new Picker();
        internal readonly PillButton Export = new PillButton(), ChargingHelp = new PillButton(), Evidence = new PillButton();

        internal BatteryCareCard()
        {
            Width = 648;
            Height = 710;
            AccessibleName = "Battery care: charge habits, capacity trend and care plans";
            TextAt("Charge habits · last seven days", 18, 30, Theme.Section, Theme.Save);
            _exposure = TextAt("Collecting observed intervals…", 56, 108, Theme.Body, Theme.Text);
            TextAt("Counts only adjacent readings while PowerDial runs. Sleep, gaps and source changes are excluded. These are exposure indicators, not damage estimates. 20% and 80% are guidance, not safety boundaries.",
                170, 70, Theme.Small, Theme.Dim);
            TextAt("Capacity trend · up to 90 days", 254, 30, Theme.Section, Theme.Save);
            _trend = TextAt("Collecting capacity readings…", 292, 120, Theme.Body, Theme.Text);
            Label planHeading = TextAt("Care plan", 428, 30, Theme.Section, Theme.Save);
            planHeading.Width = 130;
            planHeading.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _purpose.SetBounds(170, 424, 260, 32);
            _purpose.Items.AddRange(Purposes);
            _purpose.AccessibleName = "Battery care use case";
            Controls.Add(_purpose);
            _plan = TextAt("", 472, 100, Theme.Body, Theme.Text);
            _purpose.SelectedIndexChanged += (s, e) => _plan.Text = BatteryCare.Plan(_purpose.SelectedIndex);
            _purpose.SelectedIndex = 0;
            TextAt("Keep vents clear and avoid charging in heat. Battery temperature is not monitored here. This plan is advice; it does not change charging or power settings.",
                584, 62, Theme.Small, Theme.Dim);
            AddButton(ChargingHelp, "Charging support", 18, 180);
            AddButton(Evidence, "Battery care source", 210, 190);
            AddButton(Export, "Export health CSV", 412, 210);
        }

        Label TextAt(string text, int y, int height, Font font, Color color)
        {
            Label label = new Label { Text = text, Location = new Point(18, y), Size = new Size(612, height),
                Font = font, ForeColor = color, BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(label);
            return label;
        }

        void AddButton(PillButton button, string text, int x, int width)
        {
            button.Text = text; button.SetBounds(x, 660, width, 34);
            button.BackColor = Theme.Panel; Controls.Add(button);
        }

        internal void UpdateData(bool present, bool valid, ChargeExposure exposure, CapacityTrend trend)
        {
            Export.Enabled = present;
            if (!present)
            {
                _exposure.Text = "No battery detected. Charge-habit monitoring is inactive.";
                _trend.Text = "No battery capacity to track on this PC.";
                return;
            }
            _exposure.Text = exposure.ObservedSeconds <= 0 ? "Not enough adjacent battery readings yet. Leave PowerDial running to start recording." :
                "Observed: " + Hours(exposure.ObservedSeconds) + "\n" +
                "Above 80%: " + Hours(exposure.HighSeconds) + "  ·  plugged in above 80%: " + Hours(exposure.PluggedHighSeconds) + "\n" +
                "Below 20%: " + Hours(exposure.LowSeconds);
            if (!valid) _exposure.Text += "\nLive telemetry unavailable; showing recorded history only.";
            _trend.Text = trend.FirstMwh.HasValue ?
                (trend.FirstMwh.Value / 1000).ToString("0.0") + " → " + (trend.LastMwh.Value / 1000).ToString("0.0") +
                " Wh (" + ((trend.LastMwh.Value / trend.FirstMwh.Value - 1) * 100).ToString("+0.0;-0.0;0.0") +
                "% change)\n" + trend.Note : trend.Note + "\n" + trend.Days + (trend.Days == 1 ? " recorded day so far." : " recorded days so far.");
        }

        static string Hours(double seconds) { return (seconds / 3600).ToString("0.0") + " h"; }
    }
}
