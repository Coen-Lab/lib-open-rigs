using Bonsai;
using Bonsai.Harp;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Holds a Harp device's clock to the rig's clock (Timestamp): each heartbeat (its TimestampSeconds event) is compared with
    /// the clock's latest time. A device off its clock-sync cable runs on its own clock from power-on, and its every sample
    /// would be logged on that timebase with nothing said. Passes each agreeing heartbeat once <see cref="Beats"/> in a row
    /// have agreed (the device's ready stage announces at the first), and stops the workflow, before Go or during the run,
    /// once <see cref="Beats"/> in a row disagree by more than <see cref="ToleranceSeconds"/>. On a rig whose clock is this
    /// device, the two always agree.
    /// </summary>
    [Combinator]
    [Description("Each heartbeat of the device (Name.Data, first input) compared with the rig's clock (Timestamp, second input): passes the device's time once Beats heartbeats in a row agree within ToleranceSeconds, and stops the workflow once Beats in a row disagree, naming the device and its clock-sync cable.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class HarpClockAgrees
    {
        /// <summary>Gets or sets the device's name, for the message.</summary>
        [Description("The device's name, such as BehaviorBoard-Main, for the message.")]
        public string Name { get; set; }

        /// <summary>Gets or sets how far the device's heartbeat may be from the clock, in seconds.</summary>
        [Description("How far the device's heartbeat may be from the rig's clock, in seconds.")]
        public double ToleranceSeconds { get; set; } = 0.5;

        /// <summary>Gets or sets how many heartbeats in a row decide agreement or disagreement.</summary>
        [Description("How many heartbeats in a row decide: so many agreeing pass, so many disagreeing stop the workflow. A clock set between two beats costs one.")]
        public int Beats { get; set; } = 3;

        /// <summary>The device's time at each agreeing heartbeat, once <see cref="Beats"/> have agreed in a row.</summary>
        public IObservable<double> Process(IObservable<HarpMessage> device, IObservable<double> clock)
        {
            return Observable.Defer(() =>
            {
                var gate = new object();
                double? latest = null;
                int agreeing = 0, disagreeing = 0;
                var clockTimes = clock.Do(seconds => { lock (gate) latest = seconds; }).IgnoreElements();
                var beats = device
                    .Where(message => message.Address == TimestampSeconds.Address && message.MessageType == MessageType.Event && message.IsTimestamped)
                    .Select(message => Judge(message.GetTimestamp(), gate, ref latest, ref agreeing, ref disagreeing))
                    .Where(seconds => seconds.HasValue)
                    .Select(seconds => seconds.Value);
                return clockTimes.Merge(beats);
            });
        }

        double? Judge(double beat, object gate, ref double? latest, ref int agreeing, ref int disagreeing)
        {
            lock (gate)
            {
                if (!latest.HasValue) return null;
                var apart = beat - latest.Value;
                if (Math.Abs(apart) <= ToleranceSeconds)
                {
                    disagreeing = 0;
                    return ++agreeing >= Beats ? beat : (double?)null;
                }

                agreeing = 0;
                if (++disagreeing < Beats) return null;
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0}'s Harp clock is {1:0.###} s from the rig's clock (Timestamp) on {2} heartbeats in a row, so its data would be logged on its own time: " +
                    "check its clock-sync cable to the clock device, then start again.", Name, apart, Beats));
            }
        }
    }
}
