using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;

namespace OpenRigs.Logging
{
    /// <summary>
    /// Represents an operator that puts a whole experiment's rows in one group, keyed by the
    /// experiment's start time, so LogWorkflowData writes one file per experiment.
    /// </summary>
    /// <remarks>
    /// The lab's WorkflowData logs are small (a row per trial or event), so they are one file per
    /// experiment, not one per chunk as GroupByTime makes for device logs (specs/logging.md, section 3).
    /// The key is the start time in the experiment folder's name (ses-&lt;SessionId&gt;_&lt;start&gt;), so
    /// every WorkflowData file of an experiment carries the same time, the folder's.
    /// </remarks>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Puts every row of the experiment in one group keyed by the experiment's start time, read from the experiment folder's name (the second input, PathPrefix), so the log is one file per experiment.")]
    public class GroupByExperiment
    {
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
        /// <returns>The start, the instant the folder's name gives (UTC), which FormatDate writes back as the folder's time.</returns>
        public static DateTime Start(string pathPrefix)
        {
            return FormatDate.FolderStart(pathPrefix);
        }
    }
}
