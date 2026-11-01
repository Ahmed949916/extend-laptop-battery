using System;
using System.Windows.Forms;

namespace PowerDial
{
    public sealed partial class MainForm
    {
        void ManageBackgroundApps()
        {
            try
            {
                // An explicit review takes a fresh snapshot; no new work on the background poll.
                using (AppEndingDialog dialog = new AppEndingDialog(() => new ProcessWatch().Sample()))
                {
                    dialog.Completed += Log;
                    dialog.ShowDialog(this);
                    if (dialog.Result != null)
                    {
                        ResetDrawWindow(); _procWatch.ResetBaseline();
                        _procs = _procWatch.Sample();
                        RefreshWatch(); RefreshFixes(); RefreshOverview();
                    }
                }
                RefreshProcesses();
            }
            catch (Exception ex) { Log("Could not review background apps: " + ex.Message); ShowLog(); }
        }
    }
}
