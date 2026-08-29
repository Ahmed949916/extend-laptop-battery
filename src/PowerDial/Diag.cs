using System;
using System.Collections.Generic;

namespace PowerDial
{
    /// <summary>
    /// Somewhere for a swallowed write failure to go.
    ///
    /// The persistence code catches everything on purpose - a full disk, a locked file or a
    /// roaming profile that has gone away must not take a tray app down mid-poll. What was
    /// wrong was catching it and saying nothing: the cumulative tally is the only thing that
    /// makes "what has actually been eating the battery" survive a reboot, and if it stops
    /// saving, the failure is invisible and the data is simply gone.
    ///
    /// So the catch still swallows, and the app still runs - but the reason lands here, and
    /// the window drains it into the Activity log on the next poll. Reads are deliberately
    /// not routed here: a missing file on first run is the normal case, not a fault.
    /// </summary>
    public static class Diag
    {
        static readonly System.Threading.Lock Gate = new System.Threading.Lock();
        static readonly List<string> Pending = new List<string>();

        /// <summary>A stuck disk fails every minute. Report each distinct reason once.</summary>
        static readonly HashSet<string> Seen = new HashSet<string>();

        public static void WriteFailed(string what, Exception e)
        {
            if (e == null) return;
            string line = "Could not save " + what + " - " + e.Message;
            lock (Gate)
            {
                if (!Seen.Add(line)) return;
                Pending.Add(line);
            }
        }

        /// <summary>Take everything reported since the last call. Called from the poll.</summary>
        public static List<string> Drain()
        {
            lock (Gate)
            {
                if (Pending.Count == 0) return null;
                List<string> outp = new List<string>(Pending);
                Pending.Clear();
                return outp;
            }
        }
    }
}
