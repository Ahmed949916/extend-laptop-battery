using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    /// Two things made this the most expensive thing the app did, and both are fixed
    /// here rather than by sampling less often:
    ///
    /// Process.MainWindowHandle is not a field read. Every single call runs an
    /// EnumWindows over every top-level window in the session, so asking each of ~300
    /// processes whether it owns a window was ~60,000 window callbacks per sample. One
    /// EnumWindows pass now builds the set of process ids that own a visible window, and
    /// the loop looks up rather than enumerating.
    ///
    /// The other was exceptions as control flow. WorkingSet64 and TotalProcessorTime
    /// throw Access Denied on protected processes - on a normal machine a hundred or more
    /// of them - and a .NET throw costs microseconds, several hundred times a sample.
    /// Those processes will refuse every time, so the ones that threw are remembered and
    /// skipped; the set is rebuilt when a pid is reused.
    /// </summary>
    public sealed class ProcessWatch
    {
        sealed class Prev { public TimeSpan Cpu; public DateTime At; }

        readonly Dictionary<string, Prev> _prev = new Dictionary<string, Prev>();

        /// <summary>Process ids that refused to be read. Keyed with the start time we saw
        /// so a reused id is retried rather than written off forever.</summary>
        readonly Dictionary<int, int> _denied = new Dictionary<int, int>();

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

            // One pass over the window list for the whole sample, instead of one per
            // process inside the loop.
            HashSet<int> windowed = PidsWithAWindow();
            HashSet<int> seen = new HashSet<int>();

            foreach (Process p in all)
            {
                using (p)
                {
                    string name;
                    int pid;
                    try { name = p.ProcessName; pid = p.Id; } catch { continue; }
                    if (string.IsNullOrEmpty(name)) continue;

                    ProcInfo info;
                    if (!byName.TryGetValue(name, out info))
                    {
                        info = new ProcInfo { Name = name, Background = true };
                        byName[name] = info;
                        cpuNow[name] = TimeSpan.Zero;
                    }

                    seen.Add(pid);
                    info.Instances++;
                    if (windowed.Contains(pid)) info.Background = false;
                    if (pid == _selfId) info.Self = true;

                    // Known refuser? Then it contributes its instance and nothing else,
                    // without two exceptions being thrown to establish what we already
                    // know. System and Secure System are the usual members.
                    if (_denied.ContainsKey(pid)) continue;

                    bool denied = false;
                    try { info.WorkingSetMb += p.WorkingSet64 / 1048576.0; }
                    catch (Exception) { denied = true; }

                    try { cpuNow[name] = cpuNow[name] + p.TotalProcessorTime; }
                    catch (Exception) { denied = true; }

                    if (denied) _denied[pid] = 1;
                }
            }

            // Pids are reused, so the refusal list cannot be kept forever or a new
            // process inheriting a retired id would be silently skipped. Anything not
            // seen in this sample is dropped, which retries it next time round.
            //
            // Against `seen`, gathered inside the loop - not by walking `all` again. Those
            // Process objects have been disposed by then, and Process.Id throws once it
            // has: every id would have been missed, every entry dropped every sample, and
            // the skip list would have done nothing at all while looking like it worked.
            if (_denied.Count > 0)
            {
                List<int> gone = new List<int>();
                foreach (int k in _denied.Keys) if (!seen.Contains(k)) gone.Add(k);
                foreach (int k in gone) _denied.Remove(k);
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

        /// <summary>
        /// The process ids that own a visible top-level window, from a single pass.
        ///
        /// This is what Process.MainWindowHandle does internally, once per call - the
        /// whole point of hoisting it out here is that one enumeration answers for every
        /// process instead of each process paying for its own.
        /// </summary>
        static HashSet<int> PidsWithAWindow()
        {
            HashSet<int> pids = new HashSet<int>();
            try
            {
                EnumWindows(delegate (IntPtr hWnd, IntPtr lp)
                {
                    // Same test MainWindowHandle applies: a visible top-level window with
                    // no owner. Tool windows and hidden message sinks do not count as a
                    // process "having a window open".
                    if (!IsWindowVisible(hWnd)) return true;
                    if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

                    int pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != 0) pids.Add(pid);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return pids;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const uint GW_OWNER = 4;

        // Pinned to System32, like every other import in this codebase: without it the
        // loader would accept a user32.dll dropped beside the exe.
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

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
