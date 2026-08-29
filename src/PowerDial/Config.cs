using System;
using System.IO;
using System.Text.Json;

namespace PowerDial
{
    /// <summary>
    /// Per-machine settings that cannot be known in advance.
    ///
    /// The app used to carry one laptop's measurements as constants. Anything that varies
    /// by hardware now lives here instead, starts out null, and is filled in either by
    /// detection or by measuring. A null value means "not known on this PC yet", and the
    /// interface says so rather than showing a number borrowed from someone else's machine.
    /// </summary>
    public sealed class Config
    {
        public int Version { get; set; }

        /// <summary>Original design capacity in mWh. Null = use whatever Machine detected.</summary>
        public int? DesignCapacityMwh { get; set; }

        /// <summary>Extra cost of the discrete GPU being awake, watts. Null = not measured.</summary>
        public double? DiscreteGpuWakeWatts { get; set; }
        public string GpuWakeMeasuredOn { get; set; }

        /// <summary>Seconds per draw measurement window.</summary>
        public int WindowSeconds { get; set; }

        static Config _current;
        static readonly System.Threading.Lock Gate = new System.Threading.Lock();

        // Options now live on PrettyJson, baked in when the serialiser is generated.

        public static string Path
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PowerDial", "config.json");
            }
        }

        public static Config Current
        {
            get
            {
                lock (Gate)
                {
                    if (_current != null) return _current;
                    try
                    {
                        if (File.Exists(Path))
                            _current = JsonSerializer.Deserialize(File.ReadAllText(Path), PrettyJson.Default.Config);
                    }
                    catch { }
                    if (_current == null) _current = new Config();
                    if (_current.WindowSeconds < 15) _current.WindowSeconds = 60;
                    return _current;
                }
            }
        }

        public static void Save()
        {
            lock (Gate)
            {
                try
                {
                    Config c = Current;
                    c.Version = 1;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    File.WriteAllText(Path, JsonSerializer.Serialize(c, PrettyJson.Default.Config));
                }
                catch (Exception ex) { Diag.WriteFailed("what has been measured on this PC (config.json)", ex); }
            }
        }

        /// <summary>Design capacity to use: the override if set, else what was detected.</summary>
        public static int EffectiveDesignMwh
        {
            get
            {
                Config c = Current;
                if (c.DesignCapacityMwh.HasValue && c.DesignCapacityMwh.Value > 0)
                    return c.DesignCapacityMwh.Value;
                return Machine.DesignCapacityMwh;
            }
        }

        public static void SetGpuWake(double watts)
        {
            Current.DiscreteGpuWakeWatts = watts;
            Current.GpuWakeMeasuredOn = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save();
        }
    }
}
