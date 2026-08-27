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
    public class Config
    {
        public int Version { get; set; }

        /// <summary>Original design capacity in mWh. Null = use whatever Machine detected.</summary>
        public int? DesignCapacityMwh { get; set; }

        /// <summary>Watts per brightness point, measured on this panel. Null = not measured.</summary>
        public double? WattsPerBrightnessPoint { get; set; }
        public string BrightnessMeasuredOn { get; set; }

        /// <summary>Extra cost of the discrete GPU being awake, watts. Null = not measured.</summary>
        public double? DiscreteGpuWakeWatts { get; set; }
        public string GpuWakeMeasuredOn { get; set; }

        /// <summary>Seconds per draw measurement window.</summary>
        public int WindowSeconds { get; set; }

        static Config _current;
        static readonly object Gate = new object();

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
                            _current = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path));
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
                    File.WriteAllText(Path,
                        JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
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

        /// <summary>
        /// Backlight cost per brightness point. Null until measured on this display.
        /// There is no sensible default: a 13 inch 1080p panel and a 17 inch 4K panel
        /// differ by several times, so guessing would be worse than saying nothing.
        /// </summary>
        public static double? BacklightWattsPerPoint
        {
            get { return Current.WattsPerBrightnessPoint; }
        }

        public static void SetBacklight(double wattsPerPoint)
        {
            Current.WattsPerBrightnessPoint = wattsPerPoint;
            Current.BrightnessMeasuredOn = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save();
        }

        public static void SetGpuWake(double watts)
        {
            Current.DiscreteGpuWakeWatts = watts;
            Current.GpuWakeMeasuredOn = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save();
        }
    }

    /// <summary>
    /// The backlight cost, in watts per brightness point. Measured on this display by
    /// holding everything else still, and stored per machine - never assumed, because a
    /// small dim panel and a large bright one differ by several times.
    ///
    /// Modelled runtime curves used to live here and were removed. Everything on screen
    /// comes from this machine, now.
    /// </summary>
    public static class Model
    {
        /// <summary>Null until measured on this display. Panels differ by several times,
        /// so a default would be worse than admitting we do not know.</summary>
        public static double? WattsPerPoint { get { return Config.BacklightWattsPerPoint; } }

        /// <summary>Backlight watts at the given brightness, or null if uncalibrated.</summary>
        public static double? Backlight(int brightness)
        {
            double? w = WattsPerPoint;
            return w.HasValue ? (double?)(brightness * w.Value) : null;
        }
    }

}
