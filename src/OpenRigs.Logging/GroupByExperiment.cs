using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Bonsai.Harp;

namespace OpenRigs.Logging
{
    /// <summary>
    /// Represents an operator that puts a whole experiment's rows in one group, keyed by the
    /// experiment's start time, so LogWorkflowData writes one file per experiment.
    /// </summary>
    /// <remarks>
    /// The lab's WorkflowData logs are small (a row per trial or event), so they are one file per
    /// experiment, not one per hour as GroupByTime makes for device logs (specs/logging.md, section 3).
    /// The key is the start time in the experiment folder's name (ses-&lt;SessionId&gt;_&lt;start&gt;), so
    /// every WorkflowData file of an experiment carries the same time, the folder's.
    /// </remarks>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Puts every row of the experiment in one group keyed by the experiment's start time, read from the experiment folder's name (the second input, PathPrefix), so the log is one file per experiment.")]
    public class GroupByExperiment
    {
        const string TimeFormat = "yyyy-MM-ddTHH-mm-ss";

        /// <summary>
        /// Groups every row into one group once the experiment folder is known.
        /// </summary>
        /// <typeparam name="TSource">The type of the rows.</typeparam>
        /// <param name="source">The timestamped rows.</param>
        /// <param name="pathPrefix">The experiment folder (PathPrefix); its first value is used.</param>
        /// <returns>One group, keyed by the experiment's start time.</returns>
        public IObservable<IGroupedObservable<DateTime, Timestamped<TSource>>> Process<TSource>(
            IObservable<Timestamped<TSource>> source,
            IObservable<string> pathPrefix)
        {
            return pathPrefix.Take(1).SelectMany(prefix =>
            {
                var start = Start(prefix);
                return source.GroupBy(row => start);
            });
        }

        /// <summary>
        /// The start time in an experiment folder's name, ses-&lt;SessionId&gt;_&lt;start&gt; (LogController).
        /// </summary>
        /// <param name="pathPrefix">The experiment folder.</param>
        /// <returns>The start time, as written in the folder's name (UTC).</returns>
        public static DateTime Start(string pathPrefix)
        {
            var folder = Path.GetFileName((pathPrefix ?? string.Empty).TrimEnd('\\', '/'));
            var at = folder.LastIndexOf('_');
            DateTime start;
            if (at < 0 || !DateTime.TryParseExact(folder.Substring(at + 1), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
            {
                throw new InvalidOperationException(string.Format(
                    "The experiment folder '{0}' does not end in _<start> ({1}), so its WorkflowData files cannot be named.",
                    folder, TimeFormat));
            }

            return start;
        }
    }
}
