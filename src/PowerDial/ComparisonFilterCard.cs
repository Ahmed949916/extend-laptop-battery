using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerDial
{
    public sealed class ComparisonFilterCard : Card
    {
        public readonly PillButton Toggle = new PillButton();
        public string Note = "";

        public ComparisonFilterCard()
        {
            Height = 150;
            Toggle.Text = "Similar CPU sessions";
            Toggle.Size = new Size(190, 34);
            Toggle.BackColor = Theme.Panel;
            Controls.Add(Toggle);
            AccessibleRole = AccessibleRole.Grouping;
        }

        public void Fit()
        {
            using (Bitmap bitmap = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                Theme.Quality(g);
                Height = 68 + (int)Math.Ceiling(Theme.WrapHeight(g, Note, Theme.Small, Math.Max(120, Width - 40))) + 18;
            }
            AccessibleName = "Comparison filter. " + (Toggle.Selected ? "Similar CPU sessions. " : "All usage. ") + Note;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Toggle == null) return;
            Toggle.Location = new Point(Math.Max(20, Width - Toggle.Width - 20), 16);
            Fit();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme.Quality(e.Graphics);
            Theme.Str(e.Graphics, "Comparison filter", Theme.Section, Theme.Text, 20, 22);
            Theme.Wrap(e.Graphics, Note, Theme.Small, Theme.Dim, 20, 68, Math.Max(120, Width - 40));
        }
    }
}
