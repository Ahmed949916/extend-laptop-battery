using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerDial
{
    /// <summary>One recorded moment.</summary>
    public sealed class HistPoint
    {
        public long T { get; set; }          // unix seconds, UTC
        public double W { get; set; }        // watts drawn; 0 when unknown or on AC
        public int Pct { get; set; }         // charge, percent of full
        public bool Ac { get; set; }
        public double Cpu { get; set; }      // total CPU across all processes, share of one core
        public double BgMb { get; set; }     // background working set, MB
        public string Top { get; set; }      // "chrome 12.4|claude 8.1|dwm 2.0"

        // derived, not stored - System.Text.Json serialises get-only properties by
        // default, which was writing a redundant ISO timestamp on every line
        [JsonIgnore]
        public DateTime When { get { return DateTimeOffset.FromUnixTimeSeconds(T).LocalDateTime; } }
    }

    /// <summary>Cumulative tally for one process name, across every recorded session.</summary>
    public sealed class Offender
    {
        public string Name { get; set; }
        public double CpuSeconds { get; set; }   // core-seconds burned while recording
        public double PeakMb { get; set; }
        public double LastMb { get; set; }
        public int Samples { get; set; }
        public bool Background { get; set; }
        public long LastSeen { get; set; }
    }

    /// <summary>
    /// Local, persistent record of what this machine was actually doing.
    ///
    /// Without this the app forgets everything the moment it closes, so it can only ever
    /// show the last few minutes. One line of JSON per minute, in a file per month, plus a
    /// running tally per process so "what has been eating the battery lately" survives
    /// reboots rather than being re-guessed each launch.
    ///
    /// A minute of data is roughly 150 bytes, so a month of continuous use is about 6 MB.
    /// Files older than KeepMonths are deleted on startup.
    /// </summary>
    public static class History
    {
        public const int KeepMonths = 3;

        static readonly System.Threading.Lock Gate = new System.Threading.Lock();
        static Dictionary<string, Offender> _offenders;
        static int _sinceFlush;

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PowerDial", "history");
            }
        }

        static string MonthFile(DateTime utc)
        {
            return Path.Combine(Folder, utc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
        }

        static string OffendersFile { get { return Path.Combine(Folder, "offenders.json"); } }

        public static void Append(HistPoint p, List<ProcInfo> procs, double intervalSeconds)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(MonthFile(DateTime.UtcNow),
                        JsonSerializer.Serialize(p, CompactJson.Default.HistPoint) + Environment.NewLine);
                }
                // A full or locked disk must not take the app down - but it must not be
                // silent either, or the charts quietly stop growing and nothing says why.
                catch (Exception ex) { Diag.WriteFailed("this minute's history line", ex); }

                if (procs != null) Tally(procs, intervalSeconds, p.T);

                _sinceFlush++;
                if (_sinceFlush >= 5) { SaveOffenders(); _sinceFlush = 0; }
            }
        }

        static void Tally(List<ProcInfo> procs, double intervalSeconds, long t)
        {
            Dictionary<string, Offender> map = LoadOffenders();
            foreach (ProcInfo pi in procs)
            {
                if (pi.CpuPercent <= 0 && pi.WorkingSetMb <= 0) continue;
                Offender o;
                if (!map.TryGetValue(pi.Name, out o))
                {
                    o = new Offender { Name = pi.Name };
                    map[pi.Name] = o;
                }
                o.CpuSeconds += pi.CpuPercent / 100.0 * intervalSeconds;
                if (pi.WorkingSetMb > o.PeakMb) o.PeakMb = pi.WorkingSetMb;
                o.LastMb = pi.WorkingSetMb;
                o.Background = pi.Background;
                o.Samples++;
                o.LastSeen = t;
            }
        }

        public static Dictionary<string, Offender> LoadOffenders()
        {
            if (_offenders != null) return _offenders;
            _offenders = new Dictionary<string, Offender>();
            try
            {
                if (File.Exists(OffendersFile))
                {
                    List<Offender> list = JsonSerializer.Deserialize(
                        File.ReadAllText(OffendersFile), CompactJson.Default.ListOffender);
                    if (list != null)
                        foreach (Offender o in list)
                            if (!string.IsNullOrEmpty(o.Name)) _offenders[o.Name] = o;
                }
            }
            catch { }
            return _offenders;
        }

        public static void SaveOffenders()
        {
            try
            {
                Dictionary<string, Offender> map = LoadOffenders();
                Directory.CreateDirectory(Folder);
                List<Offender> list = new List<Offender>(map.Values);
                File.WriteAllText(OffendersFile, JsonSerializer.Serialize(list, CompactJson.Default.ListOffender));
            }
            catch (Exception ex) { Diag.WriteFailed("the per-process tally (offenders.json)", ex); }
        }

        /// <summary>
        /// Throw away the cumulative tally and start counting again. Destructive on
        /// purpose - the whole value of this file is that it survives reboots, so the
        /// caller confirms before calling. The per-minute history is left alone.
        /// </summary>
        public static void ClearOffenders()
        {
            lock (Gate)
            {
                _offenders = new Dictionary<string, Offender>();
                _sinceFlush = 0;
                try { if (File.Exists(OffendersFile)) File.Delete(OffendersFile); }
                catch (Exception ex) { Diag.WriteFailed("the cleared tally (offenders.json could not be deleted)", ex); }
            }
        }

        /// <summary>Cumulative worst offenders, most core-seconds first.</summary>
        public static List<Offender> TopOffenders(int take, bool backgroundOnly)
        {
            List<Offender> list = new List<Offender>();
            foreach (Offender o in LoadOffenders().Values)
            {
                if (backgroundOnly && !o.Background) continue;
                list.Add(o);
            }
            list.Sort(delegate (Offender a, Offender b) { return b.CpuSeconds.CompareTo(a.CpuSeconds); });
            if (list.Count > take) list.RemoveRange(take, list.Count - take);
            return list;
        }

        /// <summary>Most recent points, oldest first. Reads at most the last two months.</summary>
        public static List<HistPoint> Recent(int max)
        {
            List<HistPoint> outp = new List<HistPoint>();
            try
            {
                DateTime utc = DateTime.UtcNow;
                string[] files = { MonthFile(utc.AddMonths(-1)), MonthFile(utc) };
                foreach (string f in files)
                {
                    if (!File.Exists(f)) continue;
                    foreach (string line in File.ReadAllLines(f))
                    {
                        if (line.Length < 5) continue;
                        try
                        {
                            HistPoint p = JsonSerializer.Deserialize(line, CompactJson.Default.HistPoint);
                            if (p != null && p.T > 0) outp.Add(p);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (outp.Count > max) outp.RemoveRange(0, outp.Count - max);
            return outp;
        }

        public sealed class Stats
        {
            public int Points;
            public DateTime First, Last;
            public double AvgWatts;      // on battery only
            public double MinWatts, MaxWatts;
            public int BatteryPoints;
            public double BatteryMinutes;
        }

        public static Stats Summarise(List<HistPoint> pts)
        {
            Stats s = new Stats { MinWatts = double.MaxValue };
            if (pts == null || pts.Count == 0) return s;
            double sum = 0;
            foreach (HistPoint p in pts)
            {
                s.Points++;
                if (!p.Ac && p.W > 0.05)
                {
                    s.BatteryPoints++;
                    sum += p.W;
                    if (p.W < s.MinWatts) s.MinWatts = p.W;
                    if (p.W > s.MaxWatts) s.MaxWatts = p.W;
                }
            }
            s.First = pts[0].When;
            s.Last = pts[pts.Count - 1].When;
            if (s.BatteryPoints > 0) s.AvgWatts = sum / s.BatteryPoints;
            else s.MinWatts = 0;
            s.BatteryMinutes = s.BatteryPoints;   // one point per minute
            return s;
        }

        /// <summary>Delete month files older than KeepMonths.</summary>
        public static int Prune()
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                DateTime cutoff = DateTime.UtcNow.AddMonths(-KeepMonths);
                foreach (string f in Directory.GetFiles(Folder, "*.jsonl"))
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    DateTime when;
                    if (DateTime.TryParseExact(stem, "yyyy-MM", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out when))
                    {
                        if (when < new DateTime(cutoff.Year, cutoff.Month, 1))
                        {
                            File.Delete(f);
                            removed++;
                        }
                    }
                }
            }
            catch { }
            return removed;
        }

        public static long DiskBytes()
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                foreach (string f in Directory.GetFiles(Folder))
                    total += new FileInfo(f).Length;
            }
            catch { }
            return total;
        }
    }
}
