using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    internal sealed class StartupScreen : Form
    {
        readonly Label _stage = new Label();
        readonly ProgressBar _progress = new ProgressBar();

        internal StartupScreen()
        {
            Text = "PowerDial"; BackColor = Theme.Ink; ForeColor = Theme.Text;
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(460, 210);
            FormBorderStyle = FormBorderStyle.FixedSingle; ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(new Label { Text = "PowerDial", Font = Theme.Page, ForeColor = Theme.Lead,
                Location = new Point(28, 26), Size = new Size(404, 46) });
            _stage.Font = Theme.Body; _stage.SetBounds(28, 94, 404, 32); Controls.Add(_stage);
            _progress.SetBounds(28, 148, 404, 6); _progress.Maximum = 4; Controls.Add(_progress);
            SetStage("Reading this PC", 0);
        }

        internal void SetStage(string text, int step)
        { _stage.Text = text; _progress.Value = step; Update(); }

        internal static void Run()
        {
            using (StartupScreen splash = new StartupScreen())
            {
                splash.Show(); splash.Update();
                using (MainForm main = new MainForm(splash.SetStage))
                {
                    splash.Close();
                    Application.Run(main);
                }
            }
        }
    }
}
