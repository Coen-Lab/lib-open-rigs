using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Core
{
    /// <summary>
    /// Takes one weight from the balance once the first input completes: waits for
    /// <see cref="SettleWindow"/> consecutive stable readings and emits the last one's value,
    /// in grams, or fails after <see cref="TimeoutSeconds"/>.
    /// </summary>
    /// <remarks>
    /// The first input is the step to wait for, typically a <c>PulseBurst</c>, whose elements
    /// are ignored. Only its completion matters, and an empty burst still weighs. The second
    /// input is the balance readings, such as the <c>BalanceReading</c> subject. Readings that
    /// arrived before the burst completed are not counted.
    /// </remarks>
    [Combinator]
    [DefaultProperty("SettleWindow")]
    [Description("Weighs once the first input completes: waits for SettleWindow consecutive stable readings on the second input (the balance) and emits the last one's weight, in grams, or fails after TimeoutSeconds. Put it after a PulseBurst to weigh what the burst delivered. An empty burst still weighs, which gives the baseline.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class WeighStable
    {
        /// <summary>
        /// Gets or sets the number of consecutive stable readings required.
        /// </summary>
        [Description("Consecutive stable readings required before the weight is taken, mapped from the procedure's settleWindow.")]
        public int SettleWindow { get; set; } = 10;

        /// <summary>
        /// Gets or sets how long to wait for the balance to settle, in seconds.
        /// </summary>
        [Description("Give up with an error if the balance has not settled within this many seconds, mapped from the procedure's weighTimeoutS.")]
        public double TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Weighs once <paramref name="step"/> completes.
        /// </summary>
        public IObservable<double> Process<TStep>(IObservable<TStep> step, IObservable<WeightMeasurement> readings)
        {
            // Publish so the readings are subscribed once, up front: a BehaviorSubject replays its
            // last reading to a late subscriber, and that reading predates the step. The properties
            // are read when the weighing starts, which lets values mapped from the settings after
            // the workflow is built still apply.
            return readings.Publish(shared => step
                .IgnoreElements()
                .Select(_ => 0.0)
                .Concat(Observable.Defer(() => Settled(shared, SettleWindow, TimeoutSeconds))));
        }

        /// <summary>
        /// The next weight after <paramref name="window"/> consecutive stable readings.
        /// </summary>
        public static IObservable<double> Settled(IObservable<WeightMeasurement> readings, int window, double timeoutSeconds)
        {
            int count = Math.Max(1, window);
            var timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 60);
            return readings
                .Buffer(count, 1)
                .Where(buffer => buffer.Count == count && buffer.All(reading => reading.IsStable))
                .Select(buffer => (double)buffer[buffer.Count - 1].Value)
                .Take(1)
                .Timeout(timeout, Observable.Throw<double>(new TimeoutException(
                    "The balance did not give " + count + " consecutive stable readings within " + timeout.TotalSeconds +
                    " s. Check the balance, its serial settings and that readings are arriving.")));
        }
    }
}
