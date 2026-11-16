using Bonsai;
using Bonsai.Harp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Each input stamped with the next tick of the Harp clock and given from the clock's own thread with no lock
    /// held. WithLatestTimestamp gives its value under a lock the clock's thread needs for every tick, so a
    /// trial loop started from it can stop the clock.
    /// </summary>
    [Combinator]
    [Description("Each input stamped with the next tick of the Harp clock, given from the clock's own thread with no lock held. Inputs: the values, the Harp clock in seconds.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class OnNextTick
    {
        /// <summary>Holds each value until the clock's next tick, then gives it stamped with that tick.</summary>
        public IObservable<Timestamped<TSource>> Process<TSource>(IObservable<TSource> source, IObservable<double> clock)
        {
            return Observable.Create<Timestamped<TSource>>(observer =>
            {
                var gate = new object();
                var waiting = new List<TSource>();
                return new CompositeDisposable(
                    source.Subscribe(value => { lock (gate) waiting.Add(value); }, observer.OnError),
                    clock.Subscribe(seconds =>
                    {
                        TSource[] due;
                        lock (gate)
                        {
                            if (waiting.Count == 0) return;
                            due = waiting.ToArray();
                            waiting.Clear();
                        }
                        foreach (var value in due) observer.OnNext(Timestamped.Create(value, seconds));
                    }, observer.OnError));
            });
        }
    }
}
