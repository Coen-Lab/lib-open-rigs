using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Harp;

namespace OpenRigs.Core
{
    /// <summary>
    /// Represents an operator that holds a Harp device's messages of the last LookBackSeconds, by
    /// their Harp timestamps, until its second input first emits, then passes them on followed by
    /// every later message.
    /// </summary>
    /// <remarks>
    /// A camera frame reaches its log about 75 ms after the board trigger it is stamped with
    /// (on a rig), so a Harp log that starts only when the experiment folder exists
    /// misses the triggers of the first frames. The window is in Harp time, not arrival time, so a
    /// slow or bursty thread cannot widen or narrow it.
    /// </remarks>
    [Combinator]
    [Description("Holds a Harp device's last LookBackSeconds of messages (by Harp time) until the second input first emits, then passes them on and every later message.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class HarpLookBack
    {
        /// <summary>
        /// Gets or sets how far back, in Harp seconds before the latest message, messages are held.
        /// </summary>
        [Description("How far back, in Harp seconds before the latest message, messages are held.")]
        public double LookBackSeconds { get; set; } = 0.5;

        /// <summary>
        /// Holds the source's recent messages until start first emits, then passes them on and every
        /// later message.
        /// </summary>
        /// <typeparam name="TStart">The type of the elements of the start sequence.</typeparam>
        /// <param name="source">The device's messages, subscribed at once.</param>
        /// <param name="start">The sequence whose first element starts the output.</param>
        /// <returns>The held messages, then every later message.</returns>
        public IObservable<HarpMessage> Process<TStart>(IObservable<HarpMessage> source, IObservable<TStart> start)
        {
            return Observable.Create<HarpMessage>(observer =>
            {
                var gate = new object();
                var held = new Queue<HarpMessage>();
                var started = false;
                var lookBack = LookBackSeconds;
                var startSubscription = new SingleAssignmentDisposable();
                var sourceSubscription = source.Subscribe(
                    message =>
                    {
                        lock (gate)
                        {
                            if (started) { observer.OnNext(message); return; }
                            held.Enqueue(message);
                            if (!message.IsTimestamped) return;
                            var cutoff = message.GetTimestamp() - lookBack;
                            while (held.Count > 0 && (!held.Peek().IsTimestamped || held.Peek().GetTimestamp() < cutoff))
                            {
                                held.Dequeue();
                            }
                        }
                    },
                    error => { lock (gate) observer.OnError(error); },
                    () => { lock (gate) observer.OnCompleted(); });
                startSubscription.Disposable = start.Subscribe(
                    _ =>
                    {
                        lock (gate)
                        {
                            if (started) return;
                            started = true;
                            while (held.Count > 0) observer.OnNext(held.Dequeue());
                        }
                    },
                    error => { lock (gate) observer.OnError(error); });
                return new CompositeDisposable(sourceSubscription, startSubscription);
            });
        }
    }
}
