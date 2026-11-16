using System;
using System.Reactive;
using System.Reactive.Linq;

namespace OpenRigs.Video
{
    internal static class ObservableExtensions
    {
        public static IObservable<TSource> FillGaps<TSource>(this IObservable<TSource> source, Func<TSource, TSource, int> gapSelector)
        {
            return FillGaps(source, value => value, gapSelector);
        }

        /// <summary>
        /// Passes the values through with a default value in place of each one missing between two, by their
        /// counters (a dropped frame keeps its place, so later frames still pair with their own trigger), and passes
        /// the source's error and completion on. Nothing is filled before the first value: with no earlier counter, a
        /// first value that came late (the first frames dropped) cannot be told from one on time, so it is taken as
        /// the first. A counter that goes back stops the sequence with an error.
        /// </summary>
        public static IObservable<TSource> FillGaps<TSource, TCounter>(
            this IObservable<TSource> source,
            Func<TSource, TCounter> counterSelector,
            Func<TCounter, TCounter, int> gapSelector)
        {
            return Observable.Create<TSource>(observer =>
            {
                bool hasPrevious = false;
                TCounter previousCounter = default;
                var gapObserver = Observer.Create<TSource>(value =>
                {
                    var counter = counterSelector(value);
                    if (hasPrevious)
                    {
                        var missing = gapSelector(previousCounter, counter);
                        if (missing < 0)
                        {
                            observer.OnError(new InvalidOperationException(
                                $"Negative gap sizes are not allowed.\n  Previous counter: {previousCounter}\n  Current counter: {counter}"));
                            return;
                        }

                        while (missing > 0)
                        {
                            observer.OnNext(default);
                            missing--;
                        }
                    }
                    observer.OnNext(value);
                    previousCounter = counter;
                    hasPrevious = true;
                },
                // A camera's errors (not found, unplugged) reach the workflow: Observer.Create's default threw them on
                // the camera's own thread, where they were lost.
                observer.OnError,
                observer.OnCompleted);
                return source.SubscribeSafe(gapObserver);
            });
        }
    }
}
