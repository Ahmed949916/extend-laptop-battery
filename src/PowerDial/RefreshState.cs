using System.Collections.Generic;

namespace PowerDial
{
    internal static class RefreshState
    {
        internal static bool SameSuggestions(List<Suggestion> a, List<Suggestion> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                Suggestion x = a[i], y = b[i];
                if (x.Id != y.Id || x.Title != y.Title || x.Detail != y.Detail || x.Info != y.Info ||
                    x.ActionLabel != y.ActionLabel || x.Kind != y.Kind || x.Rank != y.Rank ||
                    x.KnobKey != y.KnobKey || x.Target != y.Target || x.Current != y.Current ||
                    x.NowText != y.NowText || x.ThenText != y.ThenText || x.Gain != y.Gain || x.OpenUri != y.OpenUri) return false;
            }
            return true;
        }

        internal static bool SameFindings(List<Finding> a, List<Finding> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name || a[i].Ok != b[i].Ok || a[i].Detail != b[i].Detail ||
                    a[i].Informational != b[i].Informational) return false;
            return true;
        }
    }
}
