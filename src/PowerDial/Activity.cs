using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PowerDial
{
    /// <summary>
    /// The activity log, on disk.
    ///
    /// It used to live only in the TextBox that displays it, which made it the one thing
    /// this app does not keep. Everything else persists - the per-minute history, the
    /// measured config, the restore point - while the record of what the app actually
    /// changed was thrown away when the window closed. That is backwards for the feature
    /// whose entire job is accountability, and it is exactly what you want the morning
    /// after something changed and you do not know what.
    ///
    /// Writes are best-effort in the same way the rest of the persistence is: a locked
    /// file or a full disk must not take a tray app down while it is logging why a setting
    /// failed. A failure goes to <see cref="Diag"/>, which reports each distinct reason
    /// once - so a log that cannot be written says so in the log, and then stops saying it.
    /// </summary>
    public static class Activity
    {
        /// <summary>Rewrite the file once it passes this, keeping the most recent lines.
        /// Months of ordinary use do not come close; a failing write loop would.</summary>
        const long MaxBytes = 512 * 1024;
        const int KeepLines = 2000;

        static readonly System.Threading.Lock Gate = new System.Threading.Lock();

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PowerDial");
            }
        }

        public static string FilePath { get { return Path.Combine(Folder, "activity.log"); } }

        /// <summary>
        /// One format everywhere, including the date.
        ///
        /// The window used to show bare clock times, which is fine while you are watching
        /// it and useless a week later in a file that spans sessions. Invariant, because
        /// this is persisted: the codebase pins the culture on anything written to disk.
        /// </summary>
        public static string Stamp(DateTime t)
        {
            return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                    TrimIfLarge();
                }
                catch (Exception ex) { Diag.WriteFailed("the activity log (activity.log)", ex); }
            }
        }

        /// <summary>Throw the log away. The next line written starts a new file.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                try { if (File.Exists(FilePath)) File.Delete(FilePath); }
                catch (Exception) { }     // nowhere left to report it to
            }
        }

        /// <summary>The most recent lines from previous runs, oldest first. Empty on a
        /// first run, which is the normal case and not a fault.</summary>
        public static List<string> Tail(int max)
        {
            List<string> outp = new List<string>();
            try
            {
                if (!File.Exists(FilePath)) return outp;
                string[] all = File.ReadAllLines(FilePath);
                int from = Math.Max(0, all.Length - max);
                for (int i = from; i < all.Length; i++)
                    if (all[i].Length > 0) outp.Add(all[i]);
            }
            catch (Exception) { }      // a log we cannot read is not worth failing over
            return outp;
        }

        /// <summary>Caller holds Gate.</summary>
        static void TrimIfLarge()
        {
            FileInfo f = new FileInfo(FilePath);
            if (!f.Exists || f.Length <= MaxBytes) return;

            string[] all = File.ReadAllLines(FilePath);
            if (all.Length <= KeepLines) return;

            List<string> keep = new List<string>();
            keep.Add(Stamp(DateTime.Now) + "  (earlier entries trimmed - the log had grown past " +
                     (MaxBytes / 1024) + " KB)");
            for (int i = all.Length - KeepLines; i < all.Length; i++) keep.Add(all[i]);

            File.WriteAllLines(FilePath, keep.ToArray());
        }
    }
}
