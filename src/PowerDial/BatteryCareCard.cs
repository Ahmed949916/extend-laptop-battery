using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    internal sealed class BatteryCareCard : Card
    {
        static readonly string[] Purposes = { "Mostly at a desk", "Travel / full runtime", "Long-term storage" };
        readonly Label _historyTitle, _coverage, _legend, _acNote, _trendTitle, _trend, _trendNote, _careTitle, _plan;
        readonly Panel _band = new Panel();
        readonly Picker _purpose = new Picker();
        readonly FlowLayoutPanel _actions = new FlowLayoutPanel();
        ChargeExposure _history = new ChargeExposure();
        internal readonly PillButton Export = new(), ChargingHelp = new(), Evidence = new();

        internal BatteryCareCard()
        {
            Width = 648;
            AccessibleName = "Charge history, capacity trend and care routine";
            _historyTitle = TextLine("Charge history", Theme.Head, Theme.Text);
            _coverage = TextLine("Waiting for recorded readings", Theme.Body, Theme.Dim);
            _band.BackColor = Theme.Inset;
            _band.Paint += DrawBand;
            Controls.Add(_band);
            _legend = TextLine("", Theme.Mono, Theme.Text);
            _acNote = TextLine("", Theme.Small, Theme.Dim);
            _trendTitle = TextLine("Capacity trend · 90 days", Theme.Section, Theme.Text);
            _trend = TextLine("", Theme.Stat, Theme.Text);
            _trendNote = TextLine("", Theme.Small, Theme.Dim);
            _careTitle = TextLine("Care routine", Theme.Section, Theme.Text);
            _purpose.Items.AddRange(Purposes);
            _purpose.AccessibleName = "Choose a care routine";
            Controls.Add(_purpose);
            _plan = TextLine("", Theme.Body, Theme.Dim);
            _purpose.SelectedIndexChanged += (s, e) => {
                _plan.Text = BatteryCare.Plan(_purpose.SelectedIndex);
                Arrange();
            };
            _actions.BackColor = Theme.Panel;
            Controls.Add(_actions);
            AddAction(Export, "Export CSV", ButtonIcon.Export, 125);
            AddAction(ChargingHelp, "Charging support", ButtonIcon.External, 160);
            AddAction(Evidence, "Care guidance", ButtonIcon.External, 148);
            _purpose.SelectedIndex = 0;
            SizeChanged += (s, e) => Arrange();
        }

        Label TextLine(string text, Font font, Color color)
        {
            var label = new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.Panel,
                UseMnemonic = false };
            Controls.Add(label);
            return label;
        }

        void AddAction(PillButton button, string text, ButtonIcon icon, int width)
        {
            button.Text = text;
            button.Icon = icon;
            button.Size = new Size(width, 34);
            button.Margin = new Padding(0, 0, 8, 6);
            _actions.Controls.Add(button);
        }

        void Arrange()
        {
            int pad = LogicalToDeviceUnits(18);
            int width = Math.Max(1, Width - 2 * pad);
            int y = pad;
            void Place(Label label, int gap)
            {
                int h = label.GetPreferredSize(new Size(width, 0)).Height;
                label.SetBounds(pad, y, width, h);
                y += h + LogicalToDeviceUnits(gap);
            }
            Place(_historyTitle, 2);
            Place(_coverage, 10);
            bool hasHistory = _history.ObservedSeconds > 0;
            _band.Visible = _legend.Visible = hasHistory;
            if (hasHistory)
            {
                _band.SetBounds(pad, y, width, LogicalToDeviceUnits(12));
                y += LogicalToDeviceUnits(22);
                Place(_legend, 4);
            }
            Place(_acNote, 22);
            Place(_trendTitle, 4);
            Place(_trend, 4);
            Place(_trendNote, 22);
            Place(_careTitle, 6);
            _purpose.SetBounds(pad, y, Math.Min(width, LogicalToDeviceUnits(260)), LogicalToDeviceUnits(32));
            y += _purpose.Height + LogicalToDeviceUnits(8);
            Place(_plan, 14);
            _actions.Width = width;
            _actions.SetBounds(pad, y, width, _actions.GetPreferredSize(new Size(width, 0)).Height);
            Height = _actions.Bottom + pad;
        }

        void DrawBand(object sender, PaintEventArgs e)
        {
            double total = _history.ObservedSeconds;
            if (total <= 0) return;
            int low = (int)Math.Round(_band.Width * _history.LowSeconds / total);
            int high = (int)Math.Round(_band.Width * _history.HighSeconds / total);
            using var lowBrush = new SolidBrush(Theme.Dim);
            using var otherBrush = new SolidBrush(Theme.Data);
            using var highBrush = new SolidBrush(Theme.Spend);
            e.Graphics.FillRectangle(otherBrush, _band.ClientRectangle);
            e.Graphics.FillRectangle(lowBrush, 0, 0, low, _band.Height);
            e.Graphics.FillRectangle(highBrush, _band.Width - high, 0, high, _band.Height);
        }

        internal void UpdateData(bool present, bool valid, ChargeExposure exposure, CapacityTrend trend)
        {
            Export.Enabled = present;
            _history = present ? exposure : new ChargeExposure();
            bool recorded = _history.ObservedSeconds > 0;
            _coverage.Text = recorded ? $"{Hours(exposure.ObservedSeconds)} recorded in the last 7 days" :
                present ? "No recorded intervals in the last 7 days" : "No battery detected";
            double other = exposure.ObservedSeconds - exposure.LowSeconds - exposure.HighSeconds;
            _legend.Text = $"Below 20%  {Hours(exposure.LowSeconds)}    Other  {Hours(other)}    Above 80%  {Hours(exposure.HighSeconds)}";
            _acNote.Text = recorded ? $"{Hours(exposure.PluggedHighSeconds)} above 80% while plugged in. Sleep and recording gaps are excluded." :
                present ? "Keep PowerDial running to record time at different charge levels." : "Charge history requires a battery.";
            if (present && !valid && recorded) _acNote.Text += " Showing saved readings.";
            if (present && trend.FirstMwh.HasValue && trend.LastMwh.HasValue)
            {
                double first = trend.FirstMwh.Value, last = trend.LastMwh.Value;
                _trend.Text = $"{first / 1000:0.0} → {last / 1000:0.0} Wh  ({(last / first - 1) * 100:+0.0;-0.0;0.0}%)";
            }
            else _trend.Text = present ? $"{trend.Days} recorded {(trend.Days == 1 ? "day" : "days")}" : "Unavailable";
            _trendNote.Text = present ? trend.Note : "Windows needs a battery capacity reading to track this trend.";
            Arrange();
            _band.Invalidate();
        }

        static string Hours(double seconds) => (seconds / 3600).ToString("0.0") + " h";
    }
}
