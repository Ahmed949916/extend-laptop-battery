using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PowerDial
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // A tray app that dies silently is a tray app you cannot debug. Anything
            // unhandled gets written next to the settings snapshot and shown once.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception, "domain");
            Application.ThreadException += (s, e) => Report(e.Exception, "ui");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // single instance, so the tray does not fill up with duplicates
            bool created;
            using (Mutex mtx = new Mutex(true, "PowerDial.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("PowerDial is already running. Look for it in the notification area.",
                                    "PowerDial", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                try
                {
                    Application.Run(new MainForm());
                }
                catch (Exception ex)
                {
                    Report(ex, "startup");
                }
            }
        }

        static void Report(Exception ex, string where)
        {
            if (ex == null) return;
            string path = "";
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerDial");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "crash.log");
                File.AppendAllText(path,
                    "---- " + DateTime.Now.ToString("u") + "  (" + where + ")" + Environment.NewLine +
                    ex.ToString() + Environment.NewLine + Environment.NewLine);
            }
            catch { }

            try
            {
                MessageBox.Show(
                    ex.GetType().Name + ": " + ex.Message +
                    (path.Length > 0 ? Environment.NewLine + Environment.NewLine + "Written to " + path : ""),
                    "PowerDial stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
