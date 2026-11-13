using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Watches the names of start-up stages as they arrive. Completes once every name in
    /// <see cref="Expected"/> has arrived; if <see cref="TimeoutSeconds"/> of wall-clock time pass
    /// first, stops the workflow with an error naming the stages that arrived and those that never did.
    /// </summary>
    [Combinator]
    [DefaultProperty("Expected")]
    [Description("Watches the names of start-up stages as they arrive. Completes once every name in Expected has arrived; if TimeoutSeconds pass first (wall clock, from subscription), stops the workflow with an error naming the stages that arrived and those that never did.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class StartupWatchdog
    {
        /// <summary>Gets or sets the stages that must all arrive, comma separated.</summary>
        [Description("The stages that must all arrive, comma separated, in the order they normally do.")]
        public string Expected { get; set; } = string.Empty;

        /// <summary>Gets or sets how long the stages may take, in seconds.</summary>
        [Description("How long the stages may take, from subscription, in seconds.")]
        public double TimeoutSeconds { get; set; } = 60;

        /// <summary>Gets or sets what the stages make up, for the error message.</summary>
        [Description("What the stages make up, for the error message, such as 'Zeroing the spout steppers'.")]
        public string What { get; set; } = "Startup";

        /// <summary>Watches the stage names.</summary>
        public IObservable<string> Process(IObservable<string> source)
        {
            return Observable.Defer(() =>
            {
                var expected = (Expected ?? string.Empty).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                var timeoutSeconds = TimeoutSeconds;
                var what = What;
                if (expected.Count == 0) throw new InvalidOperationException("StartupWatchdog for " + what + " names no Expected stages to watch.");
                if (!(timeoutSeconds > 0)) throw new InvalidOperationException("StartupWatchdog for " + what + " has a TimeoutSeconds of " + timeoutSeconds + "; it must be more than 0.");

                var arrived = new HashSet<string>();
                var gate = new object();
                // A stage source that completes early must not end the watch.
                var stages = source
                    .Concat(Observable.Never<string>())
                    .Where(stage =>
                    {
                        lock (gate)
                        {
                            arrived.Add(stage);
                            return expected.All(arrived.Contains);
                        }
                    })
                    .Take(1);

                var timeout = Observable.Timer(TimeSpan.FromSeconds(timeoutSeconds)).SelectMany(_ =>
                {
                    List<string> seen, missing;
                    lock (gate)
                    {
                        seen = expected.Where(arrived.Contains).ToList();
                        missing = expected.Where(s => !arrived.Contains(s)).ToList();
                    }
                    return Observable.Throw<string>(new TimeoutException(string.Format(
                        "{0} did not finish within {1} s. Arrived: {2}. Never arrived: {3}.",
                        what, timeoutSeconds, seen.Count > 0 ? string.Join(", ", seen) : "nothing", string.Join(", ", missing))));
                });

                return stages.Amb(timeout).IgnoreElements();
            });
        }
    }
}
