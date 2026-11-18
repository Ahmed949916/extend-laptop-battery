using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    internal sealed class ChargeLimitCard : Card
    {
        internal readonly PillButton Toggle = new(), Apply = new();
        internal readonly Label Status;
        internal readonly Slider Resume, Pause;
        readonly Panel _range = new Panel { BackColor = Theme.Panel };
        readonly Label _title, _charge, _resumeLabel, _pauseLabel;
        int? _percent;

        internal ChargeLimitCard(Config config)
        {
            Width = 648;
            AccessibleName = "Battery charge limit";
            _title = AddText("Battery charge limit", Theme.Head, Theme.Text);
            Status = AddText("", Theme.Body, Theme.Dim);
            _charge = AddText("Current charge unavailable", Theme.Value, Theme.Text);
            _range.Paint += DrawChargeRange;
            _range.SizeChanged += (s, e) => _range.Invalidate();
            Controls.Add(_range);
            _resumeLabel = AddText("", Theme.Body, Theme.Text);
            Resume = AddSlider("Resume at", config.ResumeChargePercent, _resumeLabel);
            _pauseLabel = AddText("", Theme.Body, Theme.Text);
            Pause = AddSlider("Pause at", config.PauseChargePercent, _pauseLabel);
            Toggle.Text = "Enable charge limit";
            Apply.Text = "Apply limits";
            Controls.Add(Toggle);
            Controls.Add(Apply);
            Apply.Enabled = false;
            Resume.ValueChanged += (s, e) =>
            {
                Resume.Value = Math.Min(Resume.Value, Pause.Value - 1);
                UpdatePending();
            };
            Pause.ValueChanged += (s, e) =>
            {
                Pause.Value = Math.Max(Pause.Value, Resume.Value + 1);
                UpdatePending();
            };
            Status.TextChanged += (s, e) => Arrange();
            SizeChanged += (s, e) => Arrange();
            Arrange();
        }

        internal void UpdatePending()
        {
            Apply.Enabled = Resume.Value != Config.Current.ResumeChargePercent || Pause.Value != Config.Current.PauseChargePercent;
            Apply.Primary = Apply.Enabled;
            Apply.Invalidate();
            _range.Invalidate();
        }

        internal void ShowCharge(int? percent)
        {
            if (percent == _percent) return;
            _percent = percent;
            _charge.Text = percent.HasValue ? $"Current charge   {percent}%" : "Current charge unavailable";
            _range.Invalidate();
        }

        void DrawChargeRange(object sender, PaintEventArgs e)
        {
            if (Resume == null || Pause == null) return;
            int inset = Math.Max(3, _range.Height / 3);
            int span = Math.Max(0, _range.Width - 2 * inset);
            int At(int pct) => inset + (int)Math.Round(span * pct / 100.0);
            using var marker = new SolidBrush(Theme.Text);
            int y = _range.Height / 3;
            Theme.Quality(e.Graphics);
            Theme.FillRound(e.Graphics, new Rectangle(inset, y, span, y), y / 2, Theme.Data);
            int bandWidth = Math.Max(1, At(Pause.Value) - At(Resume.Value));
            Theme.FillRound(e.Graphics, new Rectangle(At(Resume.Value), y, bandWidth, y), Math.Min(y, bandWidth) / 2, Theme.Save);
            if (_percent.HasValue)
                e.Graphics.FillEllipse(marker, At(_percent.Value) - inset, (_range.Height - 2 * inset) / 2, 2 * inset, 2 * inset);
        }

        void Arrange()
        {
            int Scale(int value) => LogicalToDeviceUnits(value);
            int pad = Scale(18), gap = Scale(20);
            int width = Math.Max(1, Width - 2 * pad);
            int y = pad;
            int PlaceLabel(Label label, int left, int top, int available)
            {
                int height = Math.Max(label.Font.Height, label.GetPreferredSize(new Size(available, 0)).Height);
                label.SetBounds(left, top, available, height);
                return label.Bottom;
            }
            int PlaceSlider(Label label, Slider slider, int left, int top, int available)
            {
                int bottom = PlaceLabel(label, left, top, available);
                slider.SetBounds(left, bottom, available, Scale(32));
                return slider.Bottom;
            }

            y = PlaceLabel(_title, pad, y, width) + Scale(4);
            y = PlaceLabel(Status, pad, y, width) + Scale(12);
            y = PlaceLabel(_charge, pad, y, width) + Scale(4);
            _range.SetBounds(pad, y, width, Scale(14));
            y = _range.Bottom + Scale(14);
            if (width >= Scale(500))
            {
                int half = (width - gap) / 2;
                y = Math.Max(PlaceSlider(_resumeLabel, Resume, pad, y, half),
                    PlaceSlider(_pauseLabel, Pause, pad + half + gap, y, width - half - gap));
            }
            else
            {
                y = PlaceSlider(_resumeLabel, Resume, pad, y, width) + Scale(4);
                y = PlaceSlider(_pauseLabel, Pause, pad, y, width);
            }
            y += Scale(10);
            int toggleWidth = Math.Min(width, Scale(184)), applyWidth = Math.Min(width, Scale(130));
            Toggle.SetBounds(pad, y, toggleWidth, Scale(36));
            if (toggleWidth + Scale(10) + applyWidth > width)
                Apply.SetBounds(pad, Toggle.Bottom + Scale(8), applyWidth, Toggle.Height);
            else
                Apply.SetBounds(Toggle.Right + Scale(10), y, applyWidth, Toggle.Height);
            Height = Apply.Bottom + pad;
        }

        Label AddText(string text, Font font, Color color)
        {
            var label = new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.Panel, UseMnemonic = false };
            Controls.Add(label);
            return label;
        }

        Slider AddSlider(string name, int value, Label label)
        {
            var slider = new Slider { AccessibleName = name + " percentage", Minimum = 1, Maximum = 100, Value = value };
            Controls.Add(slider);
            label.Text = $"{name} {value}%";
            slider.ValueChanged += (s, e) => label.Text = $"{name} {slider.Value}%";
            return slider;
        }

    }
}
