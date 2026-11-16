using System;
using System.Collections.Generic;

namespace OpenRigs.Core
{
    /// <summary>
    /// A trial's quiet waits on the Harp clock, counted from the interval's start: the trial start needs every spout released for
    /// lickQuietSeconds (a held contact blocks it), and the wheel's distance (forward and back added) within wheelQuietCm over
    /// the last wheelQuietSeconds; the start is the first moment at or after earliest at which both hold. With both off, the start is
    /// earliest. One class for every task, so all of them wait alike. Not thread-safe, as a List is not: a task calls it
    /// under the one lock its lick, wheel and clock inputs share.
    /// </summary>
    public class QuietWait
    {
        /// <summary>The longest quiet wait, in seconds, lick or wheel: a longer one is a typo, not a training stage.</summary>
        public const double MaxQuietSeconds = 10;
        /// <summary>The most wheel travel, in cm, a wheel quiet wait may allow.</summary>
        public const double MaxWheelQuietCm = 20;

        readonly double earliest, lickQuietSeconds, wheelQuietSeconds, wheelQuietCm;

        /// <summary>
        /// What is wrong with a training stage's quiet waits, empty when nothing is: each a finite number from 0, the
        /// seconds at most MaxQuietSeconds and the cm at most MaxWheelQuietCm, and the wheel's seconds and cm both above 0
        /// (its wait) or both 0 (off). A task checks its stages with it when it loads them; the constructor refuses them.
        /// </summary>
        public static List<string> SettingsProblems(double lickQuietSeconds, double wheelQuietSeconds, double wheelQuietCm)
        {
            var problems = new List<string>();
            Check(problems, "lickQuietSeconds", lickQuietSeconds, MaxQuietSeconds, "s");
            Check(problems, "wheelQuietSeconds", wheelQuietSeconds, MaxQuietSeconds, "s");
            Check(problems, "wheelQuietCm", wheelQuietCm, MaxWheelQuietCm, "cm");
            if ((wheelQuietSeconds > 0) != (wheelQuietCm > 0))
                problems.Add(string.Format("wheelQuietSeconds {0} and wheelQuietCm {1} are both above 0 (the wheel's quiet wait) or both 0 (off), not one of each", wheelQuietSeconds, wheelQuietCm));
            return problems;
        }

        static void Check(List<string> problems, string name, double value, double max, string unit)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > max)
                problems.Add(string.Format("{0} is {1}, not from 0 to {2} {3}", name, value, max, unit));
        }
        readonly Queue<KeyValuePair<double, double>> moves = new Queue<KeyValuePair<double, double>>();  // (Harp time, cm) in the window
        double lickSince, wheelSince, lastWheel, recentCm;
        bool held;

        /// <summary>
        /// Starts the waits for one interval.
        /// </summary>
        /// <param name="intervalStart">Harp time the interval began, where both waits start.</param>
        /// <param name="earliest">The soonest the trial may start: the interval's end less the lead.</param>
        /// <param name="lickQuietSeconds">How long every spout must be released before the trial starts; 0 for no lick wait.</param>
        /// <param name="wheelQuietSeconds">The window the wheel's travel is counted over; 0 for no wheel wait.</param>
        /// <param name="wheelQuietCm">The most travel, in cm, the window may hold.</param>
        /// <param name="wheelAtStart">The wheel's distance in cm at the interval's start, NaN if none has been read.</param>
        /// <param name="lickHeld">Whether a spout is touched at the interval's start.</param>
        public QuietWait(double intervalStart, double earliest, double lickQuietSeconds, double wheelQuietSeconds, double wheelQuietCm, double wheelAtStart, bool lickHeld)
        {
            var problems = SettingsProblems(lickQuietSeconds, wheelQuietSeconds, wheelQuietCm);
            if (double.IsNaN(intervalStart) || double.IsInfinity(intervalStart)) problems.Add("the interval's start is " + intervalStart);
            if (double.IsNaN(earliest) || double.IsInfinity(earliest)) problems.Add("the earliest start is " + earliest);
            if (problems.Count > 0) throw new ArgumentException("QuietWait: " + string.Join("; ", problems) + ".");
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
            if (double.IsNaN(cm)) throw new ArgumentException(string.Format("QuietWait: the wheel's distance at Harp time {0} is NaN.", now));
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

        /// <summary>When the trial starts if nothing more restarts a wait (infinity while a spout is held): exact, not rounded to a clock tick.</summary>
        public double TrialStartTime
        {
            get
            {
                if (lickQuietSeconds > 0 && held) return double.PositiveInfinity;
                return Math.Max(earliest, Math.Max(lickQuietSeconds > 0 ? lickSince + lickQuietSeconds : 0, wheelSince + wheelQuietSeconds));
            }
        }

        /// <summary>The soonest the trial may start, as given.</summary>
        public double Earliest { get { return earliest; } }
    }
}
