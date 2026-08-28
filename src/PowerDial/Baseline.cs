using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PowerDial
{
    public class BaselineEntry
    {
        public string Key { get; set; }
        public int? Value { get; set; }
        public bool WasExplicit { get; set; }   // stored in the scheme vs inherited from a default

        /// <summary>
        /// The plugged-in value as it stood on first run. Recorded because the settings
        /// section can now write that side too, and a restore point that only covers
        /// battery would quietly fail to undo half of what the app can do. Null on
        /// snapshots taken before this existed - those restore battery only, and say so.
        /// </summary>
        public int? AcValue { get; set; }
        public bool AcWasExplicit { get; set; }
    }

    public class BaselineFile
    {
        public string CapturedUtc { get; set; }
        public string Scheme { get; set; }
        public string Source { get; set; }
        public List<BaselineEntry> Entries { get; set; }

        /// <summary>
        /// Screen brightness as it stood on first run, 0-100. Null when the display does
        /// not expose it, or when the snapshot predates this field - in which case restore
        /// leaves brightness alone and says so, rather than inventing a level to go back to.
        ///
        /// It belongs here because the app changes it: every profile sets a brightness, and
        /// so does the suggestion that turns the screen down. Without it, the one change you
        /// can actually see was the one Restore could not undo.
        /// </summary>
        public int? Brightness { get; set; }
    }

    /// <summary>
    /// Remembers the on-battery settings as they stood before this app touched anything,
    /// so "Restore my settings" always has somewhere to go back to.
    ///
    /// The snapshot is taken the first time the app runs. Because the app writes nothing
    /// until a control is used, that first-run state IS the pre-app state. If no snapshot
    /// exists there is nothing to restore to, and the app says so rather than applying
    /// settings taken from some other machine.
    /// </summary>
    public static class Baseline
    {
        // There is deliberately no built-in fallback table. An earlier version carried
        // one laptop's settings as a seed, which would have restored a stranger's
        // configuration onto any other machine. If no snapshot exists there is nothing
        // to restore to, and the app says so.

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PowerDial");
            }
        }

        public static string FilePath { get { return Path.Combine(Folder, "baseline.json"); } }

        public static bool Exists { get { return File.Exists(FilePath); } }

        /// <summary>Captures the present state if nothing has been captured yet.
        /// Returns a line describing what happened.</summary>
        public static string CaptureIfMissing()
        {
            if (Exists) return null;
            try
            {
                BaselineFile bf = new BaselineFile
                {
                    CapturedUtc = DateTime.UtcNow.ToString("u"),
                    Scheme = PowerCfg.ActiveScheme(),
                    Source = "captured on first run, before this app wrote anything",
                    Entries = new List<BaselineEntry>(),
                    Brightness = Brightness.Get()
                };
                foreach (Knob k in PowerCfg.Knobs)
                {
                    bf.Entries.Add(new BaselineEntry
                    {
                        Key = k.Key,
                        Value = PowerCfg.Read(k, true),
                        WasExplicit = PowerCfg.IsExplicit(k, true),
                        AcValue = PowerCfg.Read(k, false),
                        AcWasExplicit = PowerCfg.IsExplicit(k, false)
                    });
                }
                Save(bf);
                return "Saved your current settings as the restore point (" + bf.Entries.Count +
                       " values" + (bf.Brightness.HasValue ? " plus brightness at " + bf.Brightness.Value + "%" : "") + ").";
            }
            catch (Exception ex)
            {
                return "Could not save a restore point: " + ex.Message;
            }
        }

        public static void Save(BaselineFile bf)
        {
            Directory.CreateDirectory(Folder);
            JsonSerializerOptions o = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(bf, o));
        }

        public static BaselineFile Load()
        {
            try
            {
                if (Exists)
                {
                    BaselineFile bf = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(FilePath));
                    if (bf != null && bf.Entries != null && bf.Entries.Count > 0) return bf;
                }
            }
            catch { }

            return new BaselineFile
            {
                CapturedUtc = "(none)",
                Scheme = PowerCfg.ActiveScheme(),
                Source = "no restore point has been captured on this PC yet",
                Entries = new List<BaselineEntry>()
            };
        }

        /// <summary>Re-applies the snapshot. Returns log lines describing each write.</summary>
        public static List<string> Restore()
        {
            List<string> log = new List<string>();
            BaselineFile bf = Load();
            log.Add("Restore point: " + bf.Source);
            if (bf.CapturedUtc != "(none)") log.Add("Captured " + bf.CapturedUtc + " UTC");

            int ok = 0, failed = 0, inherited = 0, acOk = 0, acSkipped = 0;
            foreach (BaselineEntry e in bf.Entries)
            {
                Knob k = PowerCfg.Find(e.Key);
                if (k == null) { log.Add("  skipped " + e.Key + " - no longer a setting"); continue; }
                if (!e.Value.HasValue) { log.Add("  skipped " + k.Label + " - nothing was recorded"); continue; }

                // the plugged-in side first, and only when this snapshot actually recorded it
                if (e.AcValue.HasValue)
                {
                    string acErr = PowerCfg.Write(k, e.AcValue.Value, false);
                    int? acBack = acErr == null ? PowerCfg.Read(k, false) : null;
                    if (acErr == null && acBack.HasValue && acBack.Value == e.AcValue.Value) acOk++;
                    else log.Add("  unverified " + k.Label + " plugged in - " +
                                 (acErr != null ? acErr : "reads back as " + PowerCfg.Describe(k, acBack)));
                }
                else acSkipped++;

                string err = PowerCfg.WriteDc(k, e.Value.Value);
                if (err != null) { log.Add("  failed  " + k.Label + " - " + err); failed++; continue; }

                int? back = PowerCfg.Read(k, true);
                if (back.HasValue && back.Value == e.Value.Value)
                {
                    string tail = e.WasExplicit ? "" : "  (was inheriting this value; it is now set explicitly)";
                    if (!e.WasExplicit) inherited++;
                    log.Add("  restored " + k.Label + " to " + PowerCfg.Describe(k, e.Value) + tail);
                    ok++;
                }
                else
                {
                    log.Add("  unverified " + k.Label + " - reads back as " + PowerCfg.Describe(k, back));
                    failed++;
                }
            }

            // Brightness last, because it is the one the user can see move, and because a
            // snapshot taken before this field existed has nothing to go back to.
            if (bf.Brightness.HasValue)
            {
                int want = bf.Brightness.Value;
                string berr = Brightness.Set(want);
                if (berr != null) { log.Add("  failed  screen brightness - " + berr); failed++; }
                else
                {
                    int? back = Brightness.Get();
                    // panels often expose only a few levels and snap to the nearest
                    if (back.HasValue && Math.Abs(back.Value - want) <= 5)
                    { log.Add("  restored screen brightness to " + back.Value + "%"); ok++; }
                    else
                    { log.Add("  unverified screen brightness - asked for " + want + "%, reads " +
                              (back.HasValue ? back.Value + "%" : "unavailable")); failed++; }
                }
            }
            else log.Add("  skipped screen brightness - none was recorded on this PC");

            log.Add(ok + " restored on battery, " + acOk + " plugged in, " + failed + " failed.");
            if (acSkipped > 0)
                log.Add(acSkipped + " setting(s) had no plugged-in value recorded - this restore point " +
                        "predates the app being able to write that side, so it was left alone.");
            if (inherited > 0)
                log.Add(inherited + " of them had no stored value before and now do. The behaviour is " +
                        "identical; only the bookkeeping differs.");
            return log;
        }
    }
}
