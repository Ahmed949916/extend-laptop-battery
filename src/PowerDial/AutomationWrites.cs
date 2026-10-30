using System;

namespace PowerDial
{
    internal static class AutomationWrites
    {
        internal static string Guarded(string scheme, int expected, int value, Func<string> activeScheme,
                                       Func<int?> read, Func<int, string> write)
        {
            if (!Guid.TryParse(scheme, out _) || !string.Equals(scheme, activeScheme(), StringComparison.OrdinalIgnoreCase))
                return "Active power scheme changed or is unavailable; no write performed.";
            int? current = read();
            if (!current.HasValue) return "Setting could not be read; no write performed.";
            if (current.Value != expected) return "Setting changed since it was captured; no write performed.";
            string error = write(value);
            if (error != null) return error;
            return read() == value ? null : "The requested value did not stick.";
        }
    }
}
