using System;
using System.Collections.Generic;

namespace OpenRigs.Core
{
    /// <summary>
    /// A trial's quiet waits on the Harp clock, counted from the interval's start: Go needs every spout released for
    /// lickQuietSeconds (a held contact blocks Go), and the wheel's distance (forward and back added) within wheelQuietCm over
    /// the last wheelQuietSeconds; Go is the first moment at or after earliest at which both hold. With both off, Go is
    /// earliest. One class for every task, so all of them wait alike.
    /// </summary>
    public class QuietWait
    {
        readonly double earliest, lickQuietSeconds, wheelQuietSeconds, wheelQuietCm;
        readonly Queue<KeyValuePair<double, double>> moves = new Queue<KeyValuePair<double, double>>();  // (Harp time, cm) in the window
        double lickSince, wheelSince, lastWheel, recentCm;
        bool held;

        /// <summary>
        /// Starts the waits for one interval.
        /// </summary>
        /// <param name="intervalStart">Harp time the interval began, where both waits start.</param>
        /// <param name="earliest">The soonest Go may be: the interval's end less the lead.</param>
        /// <param name="lickQuietSeconds">How long every spout must be released before Go; 0 for no lick wait.</param>
        /// <param name="wheelQuietSeconds">The window the wheel's travel is counted over; 0 for no wheel wait.</param>
        /// <param name="wheelQuietCm">The most travel, in cm, the window may hold.</param>
        /// <param name="wheelAtStart">The wheel's distance in cm at the interval's start, NaN if none has been read.</param>
        /// <param name="lickHeld">Whether a spout is touched at the interval's start.</param>
        public QuietWait(double intervalStart, double earliest, double lickQuietSeconds, double wheelQuietSeconds, double wheelQuietCm, double wheelAtStart, bool lickHeld)
        {
            this.earliest = earliest;
            this.lickQuietSeconds = lickQuietSeconds;
            this.wheelQuietSeconds = wheelQuietSeconds;
            this.wheelQuietCm = wheelQuietCm;
            lickSince = wheelSince = intervalStart;
            lastWheel = wheelAtStart;
            held = lickHeld;
        }

        /// <summary>A spout touched or released at Harp time now (anyHeld: any spout still touched) restarts the lick wait.</summary>
        public void Lick(bool anyHeld, double now)
        {
            held = anyHeld;
            lickSince = Math.Max(lickSince, now);
        }

        /// <summary>The wheel's distance in cm at Harp time now: once the travel within the last wheelQuietSeconds passes wheelQuietCm,
        /// the wait restarts at the latest move that still takes it past.</summary>
        public void Wheel(double cm, double now)
        {
            var moved = double.IsNaN(lastWheel) ? 0 : Math.Abs(cm - lastWheel);
            lastWheel = cm;
            if (!(wheelQuietSeconds > 0)) return;
            while (moves.Count > 0 && moves.Peek().Key <= now - wheelQuietSeconds) recentCm -= moves.Dequeue().Value;
            if (moved == 0) return;
            moves.Enqueue(new KeyValuePair<double, double>(now, moved));
            recentCm += moved;
            while (recentCm > wheelQuietCm)
            {
                var oldest = moves.Dequeue();
                recentCm -= oldest.Value;
                wheelSince = Math.Max(wheelSince, oldest.Key);
            }
        }

        /// <summary>When Go falls if nothing more restarts a wait (infinity while a spout is held): exact, not rounded to a clock tick.</summary>
        public double GoTime
        {
            get
            {
                if (lickQuietSeconds > 0 && held) return double.PositiveInfinity;
                return Math.Max(earliest, Math.Max(lickQuietSeconds > 0 ? lickSince + lickQuietSeconds : 0, wheelSince + wheelQuietSeconds));
            }
        }

        /// <summary>The soonest Go may be, as given.</summary>
        public double Earliest { get { return earliest; } }
    }
}
