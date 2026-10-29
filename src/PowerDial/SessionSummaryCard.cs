using System;
using System.Collections.Generic;
using System.Drawing;

namespace PowerDial
{
    internal sealed class SessionSummaryCard : Card
    {
        internal List<SessionSummary> Sessions = new List<SessionSummary>();
        const string Intro = "Recorded unplugged segments. Sleep gaps and restarts split sessions; unobserved time is excluded. CPU activity is not app energy.";

        internal void Fit()
        {
            using (Bitmap bitmap = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                float y = 54 + Theme.WrapHeight(g, Intro, Theme.Small, Math.Max(120, Width - 40)) + 20;
                foreach (SessionSummary session in Sessions)
                    y += 30 + Theme.WrapHeight(g, Detail(session), Theme.Small, Math.Max(120, Width - 40)) + 20;
                Height = (int)Math.Ceiling(y + (Sessions.Count == 0 ? 50 : 12));
            }
            AccessibleName = "Recent battery sessions. " + Intro;
            foreach (SessionSummary session in Sessions) AccessibleName += " " + Title(session) + ". " + Detail(session);
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Sessions != null) Fit();
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; Theme.Quality(g);
            Theme.Str(g, "Recent battery sessions", Theme.Section, Theme.Save, 20, 20);
            int width = Math.Max(120, Width - 40);
            Theme.Wrap(g, Intro, Theme.Small, Theme.Dim, 20, 54, width);
            float y = 54 + Theme.WrapHeight(g, Intro, Theme.Small, width) + 20;
            if (Sessions.Count == 0) Theme.Str(g, "No battery sessions recorded yet.", Theme.Body, Theme.Dim, 20, y);
            foreach (SessionSummary session in Sessions)
            {
                Theme.Str(g, Title(session), Theme.Body, Theme.Text, 20, y);
                y += 30;
                string detail = Detail(session);
                Theme.Wrap(g, detail, Theme.Small, Theme.Dim, 20, y, width);
                y += Theme.WrapHeight(g, detail, Theme.Small, width) + 20;
            }
        }

        static string Title(SessionSummary session)
        {
            return DateTimeOffset.FromUnixTimeSeconds(session.Start).LocalDateTime.ToString("d MMM, HH:mm") +
                "  -  " + DateTimeOffset.FromUnixTimeSeconds(session.End).LocalDateTime.ToString("HH:mm") +
                "   ·   " + session.Status;
        }

        static string Detail(SessionSummary session)
        {
            double minutes = (session.End - session.Start) / 60.0;
            return minutes.ToString("0") + " min observed  ·  " + session.StartPercent + "% to " + session.EndPercent +
                "%  ·  " + (session.EnergyWh.HasValue ? session.EnergyWh.Value.ToString("0.00") + " Wh used" : "Energy unavailable") +
                "  ·  " + (session.AverageWatts.HasValue ? session.AverageWatts.Value.ToString("0.0") + " W average" : "Average pending") +
                "\nModes: " + session.Modes + ". Busiest sampled app: " + (session.Busiest ?? "not recorded") + ".";
        }
    }
}
