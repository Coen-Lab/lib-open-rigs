#nullable disable
using System;
using System.Globalization;

namespace OpenRigs.Trials
{
    /// <summary>A sound's levelDbfs read (open_rigs.core.task SoundLevelDbfs): dBFS RMS, or Mute for no sound. Like
    /// SoundAzimuths, a value a task file gives as a number or a word.</summary>
    public static class SoundLevels
    {
        /// <summary>The level that plays nothing (not "off", which YAML 1.1 reads as false).</summary>
        public const string Mute = "mute";

        /// <summary>A level as read (a number, or Mute): its dBFS RMS, NaN for Mute. Throws, naming the value, on anything else.</summary>
        public static double Parse(object level)
        {
            if (level as string == Mute) return double.NaN;
            double dbfs;
            if ((level is string || level is IConvertible) && !(level is bool) && double.TryParse(Convert.ToString(level, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out dbfs) && !double.IsNaN(dbfs) && !double.IsInfinity(dbfs)) return dbfs;
            throw new InvalidOperationException(string.Format("A sound level is a number of dBFS RMS or {0}, not {1}.", Mute, level ?? "nothing"));
        }
    }
}
