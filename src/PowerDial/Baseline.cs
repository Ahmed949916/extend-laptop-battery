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
    }

    public class BaselineFile
    {
        public string CapturedUtc { get; set; }
        public string Scheme { get; set; }
        public string Source { get; set; }
        public List<BaselineEntry> Entries { get; set; }
    }

    /// <summary>
    /// Remembers the on-battery settings as they stood before this app touched anything,
    /// so "Restore my settings" always has somewhere to go back to.
    ///
    /// The snapshot is taken the first time the app runs. Because the app writes nothing
    /// until a control is used, that first-run state IS the pre-app state. If the capture
    /// ever fails, the seed below is the configuration verified by hand during the tuning
    /// session that produced 6.92 W.
    /// </summary>
    public static class Baseline
    {
        /// <summary>Hand-verified end-of-tuning state. Null value = the setting had no
        /// stored battery value and was inheriting a Windows default.</summary>
        static readonly BaselineEntry[] Seed = {
            new BaselineEntry { Key = "epp",           Value = 80,    WasExplicit = true  },
            new BaselineEntry { Key = "cpumax",        Value = 100,   WasExplicit = false },
            new BaselineEntry { Key = "cpumin",        Value = 5,     WasExplicit = false },
            new BaselineEntry { Key = "boost",         Value = 2,     WasExplicit = false },
            new BaselineEntry { Key = "videoidle",     Value = 180,   WasExplicit = true  },
            new BaselineEntry { Key = "sleepidle",     Value = 600,   WasExplicit = true  },
            new BaselineEntry { Key = "hibernateidle", Value = 10800, WasExplicit = true  },
            new BaselineEntry { Key = "lid",           Value = 1,     WasExplicit = true  },
            new BaselineEntry { Key = "switchable",    Value = 0,     WasExplicit = true  },
            new BaselineEntry { Key = "pcie",          Value = 2,     WasExplicit = false },
        };

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
                    Entries = new List<BaselineEntry>()
                };
                foreach (Knob k in PowerCfg.Knobs)
                {
                    bf.Entries.Add(new BaselineEntry
                    {
                        Key = k.Key,
                        Value = PowerCfg.Read(k, true),
                        WasExplicit = PowerCfg.IsExplicit(k, true)
                    });
                }
                Save(bf);
                return "Saved your current settings as the restore point (" + bf.Entries.Count + " values).";
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
                Source = "built-in: the settings verified by hand during the tuning session",
                Entries = new List<BaselineEntry>(Seed)
            };
        }

        /// <summary>Re-applies the snapshot. Returns log lines describing each write.</summary>
        public static List<string> Restore()
        {
            List<string> log = new List<string>();
            BaselineFile bf = Load();
            log.Add("Restore point: " + bf.Source);
            if (bf.CapturedUtc != "(none)") log.Add("Captured " + bf.CapturedUtc + " UTC");

            int ok = 0, failed = 0, inherited = 0;
            foreach (BaselineEntry e in bf.Entries)
            {
                Knob k = PowerCfg.Find(e.Key);
                if (k == null) { log.Add("  skipped " + e.Key + " - no longer a setting"); continue; }
                if (!e.Value.HasValue) { log.Add("  skipped " + k.Label + " - nothing was recorded"); continue; }

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

            log.Add(ok + " restored, " + failed + " failed.");
            if (inherited > 0)
                log.Add(inherited + " of them had no stored value before and now do. The behaviour is " +
                        "identical; only the bookkeeping differs.");
            return log;
        }
    }
}
