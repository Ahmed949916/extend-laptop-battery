using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PowerDial
{
    public sealed class ProcInfo
    {
        public string Name;
        public int Instances;
        public double WorkingSetMb;
        public double CpuPercent;     // share of one core, summed across instances
        public bool Background;       // no instance owns a visible window
        public bool Self;             // PowerDial itself
    }

    /// <summary>
    /// What is actually running, sampled live.
    ///
    /// CPU has to be derived: Process.TotalProcessorTime is cumulative, so a percentage
    /// only exists relative to a previous snapshot. The first Sample() therefore returns
    /// memory figures with zero CPU, and every call after that is a real delta.
    ///
    /// Enumerating a few hundred processes costs a few milliseconds; TotalProcessorTime
    /// throws Access Denied on protected processes, which is expected and swallowed.
    /// </summary>
    public sealed class ProcessWatch
    {
        sealed class Prev { public TimeSpan Cpu; public DateTime At; }

        readonly Dictionary<string, Prev> _prev = new Dictionary<string, Prev>();
        readonly int _cores = Math.Max(1, Environment.ProcessorCount);
        int _selfId;

        public string LastError { get; private set; }

        public ProcessWatch()
        {
            try { _selfId = Environment.ProcessId; } catch (Exception) { _selfId = -1; }
        }

        /// <summary>
        /// Forget the previous snapshot so CPU starts measuring from now. The next Sample
        /// therefore reports zero CPU, exactly like the first one after launch, and every
        /// call after that is a real delta again.
        /// </summary>
        public void ResetBaseline()
        {
            _prev.Clear();
        }

        /// <summary>Grouped by executable name, because Chrome as 14 processes is one
        /// thing the user recognises, not fourteen.</summary>
        public List<ProcInfo> Sample()
        {
            LastError = null;
            Dictionary<string, ProcInfo> byName = new Dictionary<string, ProcInfo>();
            Dictionary<string, TimeSpan> cpuNow = new Dictionary<string, TimeSpan>();
            DateTime now = DateTime.UtcNow;

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { LastError = ex.Message; return new List<ProcInfo>(); }

            foreach (Process p in all)
            {
                using (p)
                {
                    string name;
                    try { name = p.ProcessName; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;

                    ProcInfo info;
                    if (!byName.TryGetValue(name, out info))
                    {
                        info = new ProcInfo { Name = name, Background = true };
                        byName[name] = info;
                        cpuNow[name] = TimeSpan.Zero;
                    }

                    info.Instances++;
                    try { info.WorkingSetMb += p.WorkingSet64 / 1048576.0; } catch { }
                    try { if (p.MainWindowHandle != IntPtr.Zero) info.Background = false; } catch { }
                    try { if (p.Id == _selfId) info.Self = true; } catch { }
                    try { cpuNow[name] = cpuNow[name] + p.TotalProcessorTime; } catch { }
                }
            }

            foreach (KeyValuePair<string, ProcInfo> kv in byName)
            {
                string name = kv.Key;
                TimeSpan cpu = cpuNow[name];
                Prev prev;
                if (_prev.TryGetValue(name, out prev))
                {
                    double secs = (now - prev.At).TotalSeconds;
                    if (secs > 0.5)
                    {
                        double used = (cpu - prev.Cpu).TotalSeconds;
                        // a process that exited and restarted can read lower; ignore that
                        if (used >= 0) kv.Value.CpuPercent = Math.Max(0, used / secs * 100.0);
                    }
                }
                _prev[name] = new Prev { Cpu = cpu, At = now };
            }

            // forget names that are gone so the dictionary does not grow forever
            if (_prev.Count > byName.Count * 2)
            {
                List<string> dead = new List<string>();
                foreach (string k in _prev.Keys) if (!byName.ContainsKey(k)) dead.Add(k);
                foreach (string k in dead) _prev.Remove(k);
            }

            List<ProcInfo> list = new List<ProcInfo>(byName.Values);
            return list;
        }

        public static List<ProcInfo> TopBy(List<ProcInfo> all, bool byCpu, bool backgroundOnly, int take)
        {
            List<ProcInfo> copy = new List<ProcInfo>();
            foreach (ProcInfo p in all)
            {
                if (backgroundOnly && !p.Background) continue;
                copy.Add(p);
            }
            copy.Sort(delegate (ProcInfo a, ProcInfo b)
            {
                double av = byCpu ? a.CpuPercent : a.WorkingSetMb;
                double bv = byCpu ? b.CpuPercent : b.WorkingSetMb;
                return bv.CompareTo(av);
            });
            if (copy.Count > take) copy.RemoveRange(take, copy.Count - take);
            return copy;
        }

        public static double TotalMb(List<ProcInfo> all, bool backgroundOnly)
        {
            double t = 0;
            foreach (ProcInfo p in all) { if (backgroundOnly && !p.Background) continue; t += p.WorkingSetMb; }
            return t;
        }

        public static double TotalCpu(List<ProcInfo> all, bool backgroundOnly)
        {
            double t = 0;
            foreach (ProcInfo p in all) { if (backgroundOnly && !p.Background) continue; t += p.CpuPercent; }
            return t;
        }
    }
}
