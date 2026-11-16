using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Passes a stream's values through and stops the workflow with <see cref="Message"/> once none
    /// has arrived for <see cref="GapSeconds"/>, from subscription (or from a second input's first value)
    /// or from the last value: a stream that stops arriving mid-run, such as a camera's frames, ends the
    /// run saying what stopped.
    /// Rx's Timeout says only "The operation has timed out.", which cannot tell two of them apart.
    /// </summary>
    [Combinator]
    [DefaultProperty("Message")]
    [Description("Passes the values through and stops the workflow with Message once none has arrived for GapSeconds, from subscription (with a second input, from its first value) or from the last value.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class GapWatchdog
    {
        /// <summary>Gets or sets the longest gap allowed between values, in seconds.</summary>
        [Description("The longest gap allowed between values, from subscription or from the last value, in seconds.")]
        public double GapSeconds { get; set; } = 10;

        /// <summary>Gets or sets the error the workflow stops with: what stopped arriving, and what to check.</summary>
        [Description("The error the workflow stops with: what stopped arriving, and what to check.")]
        public string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets what is watched, such as a camera's name, put before Message; blank for none.</summary>
        [Description("What is watched, such as a camera's name (Camera-Face), put before Message so one message serves every instance of a device. Blank for none.")]
        public string Name { get; set; }

        /// <summary>Passes the values through until a gap longer than GapSeconds, the first counted from subscription.</summary>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return Watch(source, scheduler => Observable.Timer(TimeSpan.FromSeconds(GapSeconds), scheduler));
        }

        /// <summary>
        /// Passes the values through until a gap longer than GapSeconds, the first counted from the first value of
        /// <paramref name="start"/> (or the first value, if sooner): a stream that cannot begin before something
        /// else, such as frames before the camera trigger starts, is not timed while it waits.
        /// </summary>
        public IObservable<TSource> Process<TSource, TStart>(IObservable<TSource> source, IObservable<TStart> start)
        {
            return Watch(source, scheduler => start.Take(1).SelectMany(_ => Observable.Timer(TimeSpan.FromSeconds(GapSeconds), scheduler)));
        }

        IObservable<TSource> Watch<TSource>(IObservable<TSource> source, Func<IScheduler, IObservable<long>> firstTimeout)
        {
            return Observable.Defer(() =>
            {
                var gapSeconds = GapSeconds;
                var message = string.IsNullOrWhiteSpace(Name) ? Message : Name.Trim() + ": " + Message;
                if (!(gapSeconds > 0)) throw new InvalidOperationException("GapWatchdog has a GapSeconds of " + gapSeconds + "; it must be more than 0.");
                if (string.IsNullOrWhiteSpace(Message)) throw new InvalidOperationException("GapWatchdog has no Message: say what stopped arriving and what to check.");
                // The deadline runs on a thread of its own, not the thread pool's, which a busy rig can hold
                // past it. TimeoutException, as Timeout throws: its message reaches the operator without a
                // "Parameter name" line.
                return Observable.Using(
                    () => new EventLoopScheduler(),
                    scheduler => source.Timeout(
                        firstTimeout(scheduler),
                        _ => Observable.Timer(TimeSpan.FromSeconds(gapSeconds), scheduler),
                        Observable.Defer(() => Observable.Throw<TSource>(new TimeoutException(message)))));
            });
        }
    }
}
