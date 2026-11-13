using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Fires a train of <see cref="Count"/> pulses on <see cref="Line"/> for each input, timed on the
    /// Harp clock: each pulse is a (line, width) request for the PulseController's
    /// <c>TriggerCalibratedPulse</c>, and the next one is due <see cref="PulseGapMs"/> after the
    /// previous one closes. A train that arrives while one is firing starts the same gap after it.
    /// </summary>
    /// <remarks>
    /// The properties are read when each input arrives, so an <c>InputMapping</c> can set them per
    /// train. The first pulse fires as the input arrives, or at the first clock tick if none has come yet. The output
    /// completes once the input has completed and the last pulse has closed, so a step after it
    /// (a weighing) starts with the valve shut. A clock that goes backwards while pulses
    /// are still to fire stops it with an error; one that goes backwards while idle (Bonsai.Harp's
    /// SynchronizeTimestamp sets the board's clock at start) is taken as the new time.
    /// </remarks>
    [Combinator]
    [Description("Fires Count (line, widthMs) requests for TriggerCalibratedPulse per input, timed on the Harp clock (second input, in seconds): each next pulse starts PulseGapMs after the previous one closes. Completes once the input has completed and the last pulse has closed. Stops with an error if the clock goes backwards while pulses are still to fire; while idle it takes the new time.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class FirePulses
    {
        const double Tolerance = 1e-6; // seconds: Harp ticks are 1 ms apart, sums of milliseconds are not exact doubles

        /// <summary>Gets or sets the behavior board DO line to pulse.</summary>
        [Description("The behavior board DO line to pulse, 1 to 3.")]
        public int Line { get; set; } = 1;

        /// <summary>Gets or sets the width of each pulse, in milliseconds.</summary>
        [Description("The width of each pulse, in milliseconds.")]
        public int WidthMs { get; set; } = 1;

        /// <summary>Gets or sets the number of pulses per input.</summary>
        [Description("The number of pulses per input; 0 fires none.")]
        public int Count { get; set; } = 1;

        /// <summary>Gets or sets the off-to-on time between pulses, in milliseconds.</summary>
        [Description("The off-to-on time between consecutive pulses, in milliseconds.")]
        public int PulseGapMs { get; set; }

        class Train
        {
            public int Line, WidthMs, Count, PulseGapMs;
        }

        /// <summary>Fires one train of pulses per input element, timed on <paramref name="clock"/>.</summary>
        public IObservable<Tuple<int, int>> Process<TSource>(IObservable<TSource> source, IObservable<double> clock)
        {
            return Observable.Create<Tuple<int, int>>(observer =>
            {
                var gate = new object();
                var waiting = new Queue<Train>();
                Train active = null;
                int fired = 0;
                double? now = null;
                double nextOnset = double.NegativeInfinity;  // the active train's next pulse
                double closedAt = double.NegativeInfinity;   // when the last pulse fired closes
                bool sourceDone = false, stopped = false;

                Action<Exception> fail = error =>
                {
                    if (stopped) return;
                    stopped = true;
                    observer.OnError(error);
                };

                Action pump = () =>
                {
                    while (!stopped)
                    {
                        if (active == null)
                        {
                            if (waiting.Count == 0)
                            {
                                if (sourceDone && (!now.HasValue || now.Value >= closedAt - Tolerance))
                                {
                                    stopped = true;
                                    observer.OnCompleted();
                                }
                                return;
                            }
                            if (!now.HasValue) return;
                            active = waiting.Dequeue();
                            fired = 0;
                            nextOnset = Math.Max(now.Value, closedAt + active.PulseGapMs / 1000.0);
                        }
                        if (fired == active.Count)
                        {
                            active = null;
                            continue;
                        }
                        if (now.Value < nextOnset - Tolerance) return;
                        var onset = now.Value;
                        closedAt = onset + active.WidthMs / 1000.0;
                        nextOnset = closedAt + active.PulseGapMs / 1000.0;
                        fired++;
                        observer.OnNext(Tuple.Create(active.Line, active.WidthMs));
                    }
                };

                var clockSubscription = clock.Subscribe(
                    seconds =>
                    {
                        lock (gate)
                        {
                            if (stopped) return;
                            if (now.HasValue && seconds < now.Value)
                            {
                                if (active != null || waiting.Count > 0)
                                {
                                    var left = (active != null ? active.Count - fired : 0) + waiting.Sum(train => train.Count);
                                    fail(new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                                        "The Harp board's clock jumped back from {0} s to {1} s with {2} valve pulse(s) still to fire, " +
                                        "so they could not be timed and the workflow stopped. Something reset the board's clock mid-experiment: " +
                                        "start the experiment again, and if it happens again, report it.", now.Value, seconds, left)));
                                    return;
                                }
                                // Idle: the new time is now. The last pulse's close moves with it, so the next train keeps its gap.
                                closedAt -= now.Value - seconds;
                            }
                            now = seconds;
                            pump();
                        }
                    },
                    error => { lock (gate) fail(error); },
                    () =>
                    {
                        lock (gate)
                        {
                            if (!stopped) fail(new InvalidOperationException("FirePulses' Harp clock ended before its pulses were all fired."));
                        }
                    });

                var sourceSubscription = source.Subscribe(
                    _ =>
                    {
                        lock (gate)
                        {
                            if (stopped) return;
                            var train = new Train { Line = Line, WidthMs = WidthMs, Count = Count, PulseGapMs = PulseGapMs };
                            if (train.Count < 0 || train.PulseGapMs < 0 || train.Count > 0 && (train.Line < 1 || train.WidthMs < 1 || train.WidthMs > ushort.MaxValue))
                            {
                                fail(new ArgumentOutOfRangeException(null, string.Format(
                                    "FirePulses cannot fire {0} pulse(s) of {1} ms on line {2} with a {3} ms gap: " +
                                    "the count and the gap must be 0 or more and, for any pulse, the line 1 or more and the width 1 to 65535 ms.",
                                    train.Count, train.WidthMs, train.Line, train.PulseGapMs)));
                                return;
                            }
                            waiting.Enqueue(train);
                            pump();
                        }
                    },
                    error => { lock (gate) fail(error); },
                    () =>
                    {
                        lock (gate)
                        {
                            sourceDone = true;
                            pump();
                        }
                    });

                return new System.Reactive.Disposables.CompositeDisposable(sourceSubscription, clockSubscription);
            });
        }
    }
}
