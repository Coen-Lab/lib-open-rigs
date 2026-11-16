using Bonsai;
using Bonsai.Harp;
using System;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Sets the Harp clock of the device that keeps the rig's time, and watches it. Writes the PC's UTC
    /// time to the device at start and again at each of its heartbeats until one of them agrees with UTC
    /// within <see cref="ToleranceSeconds"/>: a write can be lost (about one start in four on a
    /// behaviour box). Stops the workflow, naming the device, if the clock is not set within
    /// <see cref="TimeoutSeconds"/> of its first heartbeat, if it sends none within
    /// <see cref="FirstHeartbeatSeconds"/>, or if its heartbeats stop for <see cref="GapSeconds"/> mid-run.
    /// </summary>
    [Combinator]
    [Description("Writes UTC to the clock device (the input is its messages, the output its commands) at start and at each heartbeat until a heartbeat agrees with UTC. Stops the workflow, naming the device, if that takes longer than TimeoutSeconds from its first heartbeat, it sends none within FirstHeartbeatSeconds, or its heartbeats stop for GapSeconds.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class SetHarpClock
    {
        /// <summary>Gets or sets the clock device's name in the data, for the error messages.</summary>
        [Description("The clock device's name in the data, such as TimestampGeneratorGen3-Main.")]
        public string Name { get; set; }

        /// <summary>Gets or sets how far a heartbeat may be from UTC for the clock to count as set, in seconds.</summary>
        [Description("How far a heartbeat may be from the PC's UTC clock for the clock to count as set, in seconds.")]
        public double ToleranceSeconds { get; set; } = 2;

        /// <summary>Gets or sets how long setting the clock may take from the device's first heartbeat, in seconds.</summary>
        [Description("How long setting the clock may take, from the device's first heartbeat, in seconds.")]
        public double TimeoutSeconds { get; set; } = 10;

        /// <summary>Gets or sets how long the device's first heartbeat may take, in seconds.</summary>
        [Description("How long the device's first heartbeat may take, from subscription, in seconds: its port opens only after every device subscribed before it.")]
        public double FirstHeartbeatSeconds { get; set; } = 30;

        /// <summary>Gets or sets the longest gap allowed between heartbeats, in seconds.</summary>
        [Description("The longest gap allowed between the device's heartbeats (one a second), in seconds.")]
        public double GapSeconds { get; set; } = 3;

        /// <summary>The clock writes for the device, from its messages.</summary>
        public IObservable<HarpMessage> Process(IObservable<HarpMessage> source)
        {
            return Observable.Defer(() =>
            {
                var name = Name;
                var tolerance = ToleranceSeconds;
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("SetHarpClock has no Name, the clock device's name in the data.");
                var heartbeats = source.Where(m => m.MessageType == MessageType.Event && m.Address == TimestampSeconds.Address).Publish().RefCount();
                var set = heartbeats.Where(m => HarpClockSet.Agrees(m.GetTimestamp(), DateTime.UtcNow, tolerance)).Take(1);

                var writes = Observable.Return(0L).Concat(heartbeats.Select(_ => 0L))
                    .Select(_ => Write(DateTime.UtcNow))
                    .TakeUntil(set);
                // From the first heartbeat: a workflow that opens many devices can take most of TimeoutSeconds
                // to reach this one's port, which says nothing about its clock.
                var timeout = TimeSpan.FromSeconds(TimeoutSeconds);
                var notSet = heartbeats.Take(1)
                    .SelectMany(_ => Observable.Timer(timeout))
                    .SelectMany(_ => Observable.Throw<HarpMessage>(new TimeoutException(string.Format(
                        "{0}'s Harp clock was not set within {1} s of its first heartbeat: no heartbeat agreed with the PC's UTC clock. Check its USB cable and power.", name, timeout.TotalSeconds))))
                    .TakeUntil(set);
                var firstHeartbeat = TimeSpan.FromSeconds(FirstHeartbeatSeconds);
                var silent = Observable.Timer(firstHeartbeat)
                    .SelectMany(_ => Observable.Throw<HarpMessage>(new TimeoutException(string.Format(
                        "{0} sent no heartbeat within {1} s of the start, so its Harp clock was not set. Check its USB cable and power.", name, firstHeartbeat.TotalSeconds))))
                    .TakeUntil(heartbeats);
                // From the first heartbeat on (the port may take a while to open), on a thread of its own:
                // a busy thread pool can hold a deadline past it.
                var gap = TimeSpan.FromSeconds(GapSeconds);
                var stopped = Observable.Using(
                    () => new EventLoopScheduler(),
                    scheduler => heartbeats.Timeout(
                        Observable.Never<long>(),
                        _ => Observable.Timer(gap, scheduler),
                        Observable.Defer(() => Observable.Throw<HarpMessage>(new TimeoutException(string.Format(
                            "{0}'s Harp clock stopped: no heartbeat for {1} s. Check its USB cable and power.", name, gap.TotalSeconds)))))
                        .IgnoreElements());
                return writes.Merge(notSet).Merge(silent).Merge(stopped);
            });
        }

        /// <summary>The write that sets a Harp clock to <paramref name="utc"/>, to the second, as Bonsai.Harp's SynchronizeTimestamp writes it.</summary>
        public static HarpMessage Write(DateTime utc)
        {
            var seconds = (uint)Math.Round((utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            return TimestampSeconds.FromPayload(MessageType.Write, seconds);
        }
    }
}
