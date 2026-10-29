using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    internal sealed class BatteryToolsCard : Card
    {
        internal readonly CheckBox Low = new CheckBox(), Charge = new CheckBox(), Drain = new CheckBox(), Auto = new CheckBox();
        internal readonly Picker LowAt = new Picker(), ChargeAt = new Picker(), AutoAt = new Picker(), Mode = new Picker();
        internal readonly PillButton Snooze = new PillButton(), Pause = new PillButton(), Resume = new PillButton(), Undo = new PillButton();
        internal readonly Label Status = new Label();
        internal event EventHandler OptionsChanged;

        internal BatteryToolsCard(BatteryToolsOptions options)
        {
            Height = 570;
            AddText("Smart alerts", 20, 16, Theme.Section, 32);
            Check(Low, "Low-battery reminder", 62, options.LowAlert);
            Percent(LowAt, 5, 50, options.LowPercent, 60, "Low-battery percentage");
            Check(Charge, "Charge-target reminder", 106, options.ChargeAlert);
            Percent(ChargeAt, 50, 100, options.ChargePercent, 104, "Charge target percentage");
            Check(Drain, "Warn about sustained high drain", 150, options.DrainAlert);
            AddText("High drain: five minutes above your prior measured usage, with a 30-minute cooldown. " +
                    "Needs 20 battery records. Charge reminders do not stop charging.", 20, 191, Theme.Small, 48);
            Button(Snooze, "Snooze alerts 1h", 20, 245, 170);

            AddText("Battery-percentage automation", 20, 297, Theme.Section, 30);
            Check(Auto, "Automatically switch on battery", 340, options.AutoEnabled);
            Percent(AutoAt, 5, 50, options.AutoPercent, 338, "Automation battery percentage");
            AddText("Apply this mode", 20, 390, Theme.Body, 30);
            Mode.SetBounds(335, 382, 270, 32);
            Mode.Items.Add("Max battery"); Mode.Items.Add("Battery saver"); Mode.Items.Add("Balanced");
            Mode.SelectedIndex = options.AutoMode - 1;
            Mode.AccessibleName = "Automatic power mode";
            Controls.Add(Mode);
            AddText("Runs once at or below the threshold. Manual changes pause it for one hour. " +
                    "Only battery settings change; the chosen mode stays until you change or undo it.", 20, 428, Theme.Small, 48);
            Button(Pause, "Pause 1h", 20, 490, 116);
            Button(Resume, "Resume / retry", 146, 490, 150);
            Button(Undo, "Undo automatic change", 306, 490, 230);
            Status.SetBounds(20, 538, 600, 60);
            Status.Font = Theme.Small; Status.ForeColor = Theme.Dim;
            Status.BackColor = Theme.Panel;
            Status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(Status);
            Height = 620;
            Low.CheckedChanged += Changed; Charge.CheckedChanged += Changed; Drain.CheckedChanged += Changed; Auto.CheckedChanged += Changed;
            LowAt.SelectedIndexChanged += Changed; ChargeAt.SelectedIndexChanged += Changed;
            AutoAt.SelectedIndexChanged += Changed; Mode.SelectedIndexChanged += Changed;
        }

        void Changed(object sender, EventArgs e) { if (OptionsChanged != null) OptionsChanged(this, EventArgs.Empty); }

        internal void ReadInto(BatteryToolsOptions options)
        {
            options.LowAlert = Low.Checked; options.ChargeAlert = Charge.Checked; options.DrainAlert = Drain.Checked;
            options.AutoEnabled = Auto.Checked;
            options.LowPercent = 5 + LowAt.SelectedIndex; options.ChargePercent = 50 + ChargeAt.SelectedIndex;
            options.AutoPercent = 5 + AutoAt.SelectedIndex; options.AutoMode = Mode.SelectedIndex + 1;
            options.Normalise();
        }

        void AddText(string text, int x, int y, Font font, int height)
        {
            Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(608, height),
                Font = font, ForeColor = Theme.Text, BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(label);
        }

        void Check(CheckBox box, string text, int y, bool value)
        {
            box.SetBounds(20, y, 305, 30); box.Text = text; box.Checked = value;
            box.Font = Theme.Body; box.ForeColor = Theme.Text; box.BackColor = Theme.Panel;
            Controls.Add(box);
        }

        void Percent(Picker picker, int min, int max, int value, int y, string name)
        {
            for (int p = min; p <= max; p++) picker.Items.Add(p + "%");
            picker.SetBounds(335, y, 150, 32); picker.SelectedIndex = value - min;
            picker.AccessibleName = name; Controls.Add(picker);
        }

        void Button(PillButton button, string text, int x, int y, int width)
        {
            button.Text = text; button.SetBounds(x, y, width, 34);
            button.BackColor = Theme.Panel; Controls.Add(button);
        }
    }
}
