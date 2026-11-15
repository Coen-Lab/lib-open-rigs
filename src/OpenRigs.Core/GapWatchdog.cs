using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Passes a stream's values through and stops the workflow with <see cref="Message"/> once none
    /// has arrived for <see cref="GapSeconds"/>, from subscription or from the last value: a stream
    /// that stops arriving mid-run, such as a camera's frames, ends the run saying what stopped.
    /// Rx's Timeout says only "The operation has timed out.", which cannot tell two of them apart.
    /// </summary>
    [Combinator]
    [DefaultProperty("Message")]
    [Description("Passes the values through and stops the workflow with Message once none has arrived for GapSeconds, from subscription or from the last value.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class GapWatchdog
    {
        /// <summary>Gets or sets the longest gap allowed between values, in seconds.</summary>
        [Description("The longest gap allowed between values, from subscription or from the last value, in seconds.")]
        public double GapSeconds { get; set; } = 10;

        /// <summary>Gets or sets the error the workflow stops with: what stopped arriving, and what to check.</summary>
        [Description("The error the workflow stops with: what stopped arriving, and what to check.")]
        public string Message { get; set; } = string.Empty;

        /// <summary>Passes the values through until a gap longer than GapSeconds.</summary>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return Observable.Defer(() =>
            {
                var gapSeconds = GapSeconds;
                var message = Message;
                if (!(gapSeconds > 0)) throw new InvalidOperationException("GapWatchdog has a GapSeconds of " + gapSeconds + "; it must be more than 0.");
                if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("GapWatchdog has no Message: say what stopped arriving and what to check.");
                // TimeoutException, as Timeout throws: its message reaches the operator without a "Parameter name" line.
                return source.Timeout(TimeSpan.FromSeconds(gapSeconds), Observable.Defer(() => Observable.Throw<TSource>(new TimeoutException(message))));
            });
        }
    }
}
