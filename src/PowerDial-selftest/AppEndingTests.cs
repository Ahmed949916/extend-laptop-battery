using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PowerDial;

sealed partial class SelfTest
{
    static void AppEndingChecks()
    {
        const string path = "C:\\Apps\\chrome.exe", user = "S-1-5-21-test", windows = "C:\\Windows";
        Check("supported user apps may pass eligibility checks", AppEnding.Eligible(path, user, user, 1, 1, false, false, windows));
        Check("unknown applications are excluded", !AppEnding.Eligible("C:\\Apps\\unknown.exe", user, user, 1, 1, false, false, windows));
        Check("Windows executables are excluded even with a supported name", !AppEnding.Eligible("C:\\WINDOWS\\chrome.exe", user, user, 1, 1, false, false, windows));
        Check("other users are excluded", !AppEnding.Eligible(path, user, "someone-else", 1, 1, false, false, windows));
        Check("session zero and other sessions are excluded", !AppEnding.Eligible(path, user, user, 0, 0, false, false, windows) &&
            !AppEnding.Eligible(path, user, user, 2, 1, false, false, windows));
        Check("critical and elevated processes are excluded", !AppEnding.Eligible(path, user, user, 1, 1, false, true, windows) &&
            !AppEnding.Eligible(path, user, user, 1, 1, true, false, windows));
        Check("unknown owners and relative paths fail closed", !AppEnding.Eligible(path, null, user, 1, 1, false, false, windows) &&
            !AppEnding.Eligible("chrome.exe", user, user, 1, 1, false, false, windows));

        // Only this disposable test-created child is ended. No live application is selected.
        string folder = Path.Combine(Path.GetTempPath(), "PowerDial-app-ending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Process fixture = null;
        try
        {
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            string exe = Path.Combine(folder, "chrome.exe");
            File.Copy(Path.Combine(folder, "SelfTest.exe"), exe);
            fixture = Process.Start(new ProcessStartInfo(exe, "--app-end-fixture") { UseShellExecute = false, CreateNoWindow = true });
            using (AppEnding review = new AppEnding(new List<ProcInfo>()))
            {
                Check("PowerDial cannot capture itself for ending", review.Capture(Environment.ProcessId, new HashSet<int>()) == null);
                Check("a service PID cannot be captured", review.Capture(fixture.Id, new HashSet<int> { fixture.Id }) == null);
                AppEndTarget target = review.Capture(fixture.Id, new HashSet<int>());
                Check("native ownership, session, critical and elevation checks accept the disposable fixture", target != null);
                if (target != null)
                {
                    AppEndGroup group = new AppEndGroup { Name = "disposable test fixture" };
                    group.Targets.Add(target); review.Groups.Add(group);
                    string result = review.End(group);
                    Check("only the reviewed fixture is ended and its exit is verified", result.Contains("1 ended", StringComparison.Ordinal) && fixture.WaitForExit(5000));
                    Check("retained exited handles do not retarget a reused PID", review.End(group).Contains("1 already exited", StringComparison.Ordinal));
                    Check("groups outside the review cannot be ended", review.End(new AppEndGroup()).Contains("No app", StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            if (fixture != null)
            {
                if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(5000); }
                fixture.Dispose();
            }
            Directory.Delete(folder, true);
        }
    }
}
