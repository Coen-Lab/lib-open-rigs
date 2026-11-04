using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core
{
    /// <summary>
    /// Fires a burst of pulse requests for one dispense sample: <c>repeats</c> requests of the
    /// sample's size on <see cref="Channel"/>, one every size plus inter-pulse interval, then
    /// completes once the last pulse has had time to land.
    /// </summary>
    /// <remarks>
    /// Each request is the (channel, size) pair the behavior board PulseController's
    /// <c>TriggerCalibratedPulse</c> subject takes. Fed a <see cref="DispenseProcedure"/>
    /// instead of a sample, it fires the priming burst: <c>primingPulses</c> pulses at
    /// <c>primingSize</c>, or the largest sample size. Completion is what the next step waits
    /// for: a <c>WeighStable</c> after it weighs once the burst is over.
    /// </remarks>
    [Combinator]
    [DefaultProperty("Channel")]
    [Description("Fires a burst of (channel, size) pulse requests for each DispenseSample: repeats requests, one every size plus interPulseIntervalMs, then completes once the last pulse has landed. Send the output to the subject that fires one pulse, such as the PulseController's TriggerCalibratedPulse. Fed the DispenseProcedure instead, it fires the priming burst.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PulseBurst
    {
        /// <summary>
        /// Gets or sets the channel the requests are for, such as the behavior board DO line.
        /// </summary>
        [Description("The channel the requests are for: the behavior board DO line, 1 to 3, for TriggerCalibratedPulse.")]
        public int Channel { get; set; } = 1;

        /// <summary>
        /// Fires one burst per sample.
        /// </summary>
        public IObservable<Tuple<int, int>> Process(IObservable<DispenseSample> source)
        {
            return source.SelectMany(sample => Burst(Channel, sample.Size, sample.Repeats, sample.InterPulseIntervalMs));
        }

        /// <summary>
        /// Fires the priming burst of each procedure.
        /// </summary>
        public IObservable<Tuple<int, int>> Process(IObservable<DispenseProcedure> source)
        {
            return source.SelectMany(procedure =>
            {
                if (procedure.Samples == null || procedure.Samples.Count == 0)
                {
                    throw new InvalidOperationException("The dispense procedure has no samples to take the priming size from.");
                }
                int size = procedure.PrimingSize ?? procedure.Samples.Max(sample => sample.Size);
                int interval = procedure.Samples.Max(sample => sample.InterPulseIntervalMs);
                return Burst(Channel, size, procedure.PrimingPulses, interval);
            });
        }

        /// <summary>
        /// The requests of one burst: <paramref name="count"/> of them, one every size plus
        /// interval, completing one more period after the last so the last drop can land.
        /// </summary>
        public static IObservable<Tuple<int, int>> Burst(int channel, int size, int count, int intervalMs)
        {
            if (size <= 0) throw new InvalidOperationException("A dispense sample needs a positive size.");
            if (count <= 0) return Observable.Empty<Tuple<int, int>>();
            var request = Tuple.Create(channel, size);
            var period = TimeSpan.FromMilliseconds(size + Math.Max(0, intervalMs));
            return Observable.Timer(TimeSpan.Zero, period)
                .Take(count)
                .Select(_ => request)
                .Concat(Observable.Timer(period).IgnoreElements().Select(_ => request));
        }
    }
}
