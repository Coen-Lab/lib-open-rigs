using Bonsai;
using Bonsai.Harp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Delays each input by <see cref="DueSeconds"/> of Harp time: the input is emitted, with the
    /// clock time it fell due, at the first tick of the clock (second input, in seconds) at or past
    /// the tick it arrived on plus <see cref="DueSeconds"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="DueSeconds"/> is read as each input arrives, so an <c>InputMapping</c> can delay
    /// each input by its own value. An input that arrives before the first tick is timed from that
    /// tick. The output completes once the input has completed and nothing is waiting. A clock that
    /// goes backwards while an input waits, or ends before the input, stops it with an error; one
    /// that goes backwards with nothing waiting (Bonsai.Harp's SynchronizeTimestamp sets the board's
    /// clock at start) is taken as the new time.
    /// </remarks>
    [Combinator]
    [DefaultProperty("DueSeconds")]
    [Description("Delays each input by DueSeconds of Harp time (second input: the clock, in seconds) and emits it timestamped with the clock time it fell due. DueSeconds is read per input, so an InputMapping can delay each by its own value. Completes once the input has completed and nothing waits. Stops with an error if the clock goes backwards while an input waits, or ends before the input; with nothing waiting it takes the new time.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class HarpDelay
    {
        const double Tolerance = 1e-6; // seconds: sums of seconds are not exact doubles

        /// <summary>Gets or sets the delay, in seconds of Harp time.</summary>
        [Description("The delay, in seconds of Harp time.")]
        public double DueSeconds { get; set; }

        /// <summary>Delays each input by <see cref="DueSeconds"/> on <paramref name="clock"/>.</summary>
        public IObservable<Timestamped<TSource>> Process<TSource>(IObservable<TSource> source, IObservable<double> clock)
        {
            return Observable.Create<Timestamped<TSource>>(observer =>
            {
                var gate = new object();
                var timed = new List<Tuple<double, TSource>>();     // (due time, input), in order of due time then arrival
                var untimed = new List<Tuple<double, TSource>>();   // (delay, input) that came before the first tick
                double? now = null;
                bool sourceDone = false, stopped = false;
                // Notifications are queued under the gate and sent outside it, one thread at a time:
                // whichever thread finds no one sending sends the queue; the others queue and return.
                // Sending under the gate deadlocked a rig (BLUE-OPTO-2, 14 Nov 2026): the clock's thread,
                // holding the clock subject's lock (Bonsai's BehaviorSubject ticks under it), waited for the
                // gate, while a lick's thread held the gate and, ending the trial downstream, waited for the
                // clock subject's lock to unsubscribe.
                var outbox = new Queue<Action>();
                bool sending = false;

                Action<Exception> fail = error =>
                {
                    if (stopped) return;
                    stopped = true;
                    outbox.Enqueue(() => observer.OnError(error));
                };
                Action<double, TSource> schedule = (due, value) =>
                {
                    var index = timed.FindLastIndex(item => item.Item1 <= due) + 1;
                    timed.Insert(index, Tuple.Create(due, value));
                };
                Action pump = () =>
                {
                    while (!stopped && timed.Count > 0 && now.Value >= timed[0].Item1 - Tolerance)
                    {
                        var item = timed[0];
                        timed.RemoveAt(0);
                        var next = Timestamped.Create(item.Item2, now.Value);
                        outbox.Enqueue(() => observer.OnNext(next));
                    }
                    if (!stopped && sourceDone && timed.Count == 0 && untimed.Count == 0)
                    {
                        stopped = true;
                        outbox.Enqueue(observer.OnCompleted);
                    }
                };
                // Called under the gate: whether this thread is to send the queue.
                Func<bool> claim = () =>
                {
                    if (sending || outbox.Count == 0) return false;
                    sending = true;
                    return true;
                };
                Action send = () =>
                {
                    while (true)
                    {
                        Action next;
                        lock (gate)
                        {
                            if (outbox.Count == 0)
                            {
                                sending = false;
                                return;
                            }
                            next = outbox.Dequeue();
                        }
                        try
                        {
                            next();
                        }
                        catch
                        {
                            lock (gate) sending = false;
                            throw;
                        }
                    }
                };
                // Runs a handler under the gate, then sends what it queued unless another thread is sending.
                Action<Action> handle = body =>
                {
                    bool mine;
                    lock (gate)
                    {
                        body();
                        mine = claim();
                    }
                    if (mine) send();
                };

                var clockSubscription = clock.Subscribe(
                    seconds => handle(() =>
                    {
                        if (stopped) return;
                        // Idle, a clock that goes backwards (SynchronizeTimestamp at start) gives the new time.
                        if (now.HasValue && seconds < now.Value && timed.Count > 0)
                        {
                            fail(new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                                "The Harp board's clock jumped back from {0} s to {1} s while {2} timed event(s) were waiting, " +
                                "so they could not be timed and the workflow stopped. Something reset the board's clock mid-experiment: " +
                                "start the experiment again, and if it happens again, report it.", now.Value, seconds, timed.Count)));
                            return;
                        }
                        now = seconds;
                        foreach (var item in untimed) schedule(seconds + item.Item1, item.Item2);
                        untimed.Clear();
                        pump();
                    }),
                    error => handle(() => fail(error)),
                    () => handle(() =>
                    {
                        if (!stopped) fail(new InvalidOperationException("HarpDelay's clock ended before its input did, so what waits would never be emitted."));
                    }));

                var sourceSubscription = source.Subscribe(
                    value => handle(() =>
                    {
                        if (stopped) return;
                        var due = DueSeconds;
                        if (!(due >= 0) || double.IsInfinity(due))
                        {
                            fail(new ArgumentOutOfRangeException(null, "HarpDelay's DueSeconds is " + due + "; it must be 0 or more seconds."));
                            return;
                        }
                        if (now.HasValue) schedule(now.Value + due, value);
                        else untimed.Add(Tuple.Create(due, value));
                        if (now.HasValue) pump();
                    }),
                    error => handle(() => fail(error)),
                    () => handle(() =>
                    {
                        sourceDone = true;
                        if (now.HasValue || untimed.Count == 0) pump();
                    }));

                // Nothing queued is sent once unsubscribed.
                var forget = Disposable.Create(() => { lock (gate) { stopped = true; outbox.Clear(); } });
                return new CompositeDisposable(forget, sourceSubscription, clockSubscription);
            });
        }
    }
}
