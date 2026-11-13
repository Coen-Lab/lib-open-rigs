using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Passes the first Harp time that agrees with the PC's UTC clock, within
    /// <see cref="ToleranceSeconds"/>, and completes. Until a Harp clock is set it counts from
    /// power-on, so files named by its time would be dated 1904; loggers wait for this.
    /// </summary>
    [Combinator]
    [Description("Passes the first Harp time (seconds since 1904-01-01 UTC) within ToleranceSeconds of the PC's UTC clock, then completes: the Harp clock is set.")]
    [WorkflowElementCategory(ElementCategory.Condition)]
    public class HarpClockSet
    {
        static readonly DateTime HarpEpoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Gets or sets how far the Harp clock may be from UTC, in seconds.</summary>
        [Description("How far the Harp clock may be from the PC's UTC clock, in seconds.")]
        public double ToleranceSeconds { get; set; } = 2;

        /// <summary>Passes the first Harp time that agrees with UTC.</summary>
        public IObservable<double> Process(IObservable<double> source)
        {
            return source.Where(seconds => Agrees(seconds, DateTime.UtcNow, ToleranceSeconds)).Take(1);
        }

        /// <summary>Whether <paramref name="harpSeconds"/> is within <paramref name="tolerance"/> s of <paramref name="utcNow"/>.</summary>
        public static bool Agrees(double harpSeconds, DateTime utcNow, double tolerance)
        {
            return Math.Abs(harpSeconds - (utcNow - HarpEpoch).TotalSeconds) <= tolerance;
        }
    }
}
