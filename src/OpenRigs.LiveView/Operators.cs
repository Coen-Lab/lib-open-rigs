using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Xml;
using System.Xml.Serialization;
using Bonsai;
using OpenRigs.Shared;

namespace OpenRigs.LiveView
{
    /// <summary>
    /// The live view's session key, sub-&lt;SubjectId&gt;/ses-&lt;SessionId&gt;_&lt;start&gt;, taken from the
    /// experiment folder OpenRigs.Logging made (PathPrefix), so the key names that folder.
    /// OpenRigs.Streaming's StreamController stamps its own date at Start, about a second before
    /// LogController stamps the folder's, so a key made from its own date can name no folder.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("The live view's session key, sub-<SubjectId>/ses-<SessionId>_<start>, from the experiment folder's path.")]
    public class SessionKey
    {
        /// <summary>Turns each experiment folder's path into its session key.</summary>
        /// <param name="source">The experiment folder's path, from PathPrefix.</param>
        /// <returns>The key, sub-&lt;SubjectId&gt;/ses-&lt;SessionId&gt;_&lt;start&gt;.</returns>
        public IObservable<string> Process(IObservable<string> source)
        {
            return source.Select(FromPathPrefix);
        }

        /// <summary>The last two folders of the experiment path, joined by a forward slash.</summary>
        public static string FromPathPrefix(string pathPrefix)
        {
            var path = (pathPrefix ?? string.Empty).TrimEnd('\\', '/');
            var experiment = Path.GetFileName(path);
            var subject = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
            if (!subject.StartsWith("sub-", StringComparison.Ordinal) || !experiment.StartsWith("ses-", StringComparison.Ordinal))
            {
                throw new ArgumentException(string.Format(
                    "The experiment folder '{0}' does not end in sub-<SubjectId>\\ses-<SessionId>_<start>, so the live view has no session key. A / or \\ in SubjectId or SessionId does this: remove it and Start again.",
                    pathPrefix));
            }
            return subject + "/" + experiment;
        }
    }

    /// <summary>Seconds since the first element, rounded to 0.1 s, at once and then every Interval.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Seconds since the first element, at once and then every Interval.")]
    public class ElapsedSeconds
    {
        /// <summary>Gets or sets how often to send the elapsed time.</summary>
        [XmlIgnore]
        [Description("How often to send the elapsed time.")]
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Gets or sets <see cref="Interval"/> in XML's duration form, for the workflow file.</summary>
        [Browsable(false)]
        [XmlElement(nameof(Interval))]
        public string IntervalXml
        {
            get { return XmlConvert.ToString(Interval); }
            set { Interval = XmlConvert.ToTimeSpan(value); }
        }

        /// <summary>Counts seconds from the first element of a sequence.</summary>
        /// <typeparam name="TSource">The type of the elements; only the first one's arrival is used.</typeparam>
        /// <param name="source">The sequence whose first element starts the count.</param>
        /// <returns>The seconds since that element, at once and then every Interval.</returns>
        public IObservable<double> Process<TSource>(IObservable<TSource> source)
        {
            return source.Take(1).SelectMany(_ =>
            {
                var watch = Stopwatch.StartNew();
                return Observable.Timer(TimeSpan.Zero, Interval).Select(__ => Math.Round(watch.Elapsed.TotalSeconds, 1));
            });
        }
    }

    /// <summary>One message of a board's channels stream: the register it came from and its value with the board's timestamp.</summary>
    public class BoardChannelMessage
    {
        /// <summary>The register's name, as the board's package names it (DigitalInputState, OutputSet).</summary>
        public string Register { get; set; }
        /// <summary>The register's value, with the board's timestamp in seconds (Harp's Timestamped: Seconds, Value).</summary>
        public object Value { get; set; }
    }

    /// <summary>
    /// Names each value with the register it came from, for a board's channels stream. A register
    /// that holds a state rather than an event (OutputState, a pulse length) is sent again every
    /// Repeat, unchanged and with its own timestamp, so a window that connects mid-experiment
    /// still learns it; the board reports such a register only when it is read or written.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Names each value with its register, and sends a state register's latest value again every Repeat.")]
    public class TagRegister
    {
        /// <summary>Gets or sets the register's name.</summary>
        [Description("The register's name.")]
        public string Register { get; set; }

        /// <summary>Gets or sets how often the latest value is sent again; zero never.</summary>
        [XmlIgnore]
        [Description("How often the latest value is sent again; zero never.")]
        public TimeSpan Repeat { get; set; }

        /// <summary>Gets or sets <see cref="Repeat"/> in XML's duration form, for the workflow file.</summary>
        [Browsable(false)]
        [XmlElement(nameof(Repeat))]
        public string RepeatXml
        {
            get { return XmlConvert.ToString(Repeat); }
            set { Repeat = XmlConvert.ToTimeSpan(value); }
        }

        /// <summary>Names each value with the register, sending the latest again every Repeat.</summary>
        /// <typeparam name="TSource">The register's timestamped value.</typeparam>
        /// <param name="source">The register's values.</param>
        /// <returns>Each value named with its register, and the latest again every Repeat.</returns>
        public IObservable<BoardChannelMessage> Process<TSource>(IObservable<TSource> source)
        {
            var register = Register;
            var repeat = Repeat;
            var tagged = source.Select(value => new BoardChannelMessage { Register = register, Value = value });
            if (repeat <= TimeSpan.Zero) return tagged;
            return tagged.Select(message => Observable.Return(message).Concat(Observable.Interval(repeat).Select(_ => message))).Switch();
        }
    }

    /// <summary>What the live view is showing: the workflow running and the devices streamed and skipped.</summary>
    public class LiveViewReport
    {
        /// <summary>The workflow's path within its repository, from Bonsai's command line; null when opened in the editor with File, Open.</summary>
        public string Workflow { get; set; }
        /// <summary>The repository's folder name.</summary>
        public string Project { get; set; }
        /// <summary>The repository's origin URL.</summary>
        public string Repo { get; set; }
        /// <summary>The branch checked out; null when detached.</summary>
        public string Branch { get; set; }
        /// <summary>The commit checked out.</summary>
        public string Commit { get; set; }
        /// <summary>Whether tracked files differ from the commit; null when git did not answer.</summary>
        public bool? Dirty { get; set; }
        /// <summary>The streams sent: one per device, two for a Behavior board (.encoder and .channels).</summary>
        public string[] Streamed { get; set; }
        /// <summary>Each device in rig.yml not streamed, and why.</summary>
        public string[] Skipped { get; set; }
        /// <summary>Problems with rig.yml or its liveView settings.</summary>
        public string[] Notes { get; set; }
    }

    /// <summary>
    /// The live view's report, at once and then every Interval: a viewer that connects late still
    /// gets it, which a single message at Start would miss. The workflow's identity is read once,
    /// on a pool thread, since the dirty flag asks git.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    [Description("The workflow running and the devices streamed and skipped, at once and then every Interval.")]
    public class ReportLiveView
    {
        /// <summary>Gets or sets the streams sent, separated by |.</summary>
        [Description("The streams sent, separated by |.")]
        public string Streamed { get; set; }

        /// <summary>Gets or sets each device not streamed and why, separated by |.</summary>
        [Description("Each device not streamed and why, separated by |.")]
        public string Skipped { get; set; }

        /// <summary>Gets or sets problems with rig.yml or its liveView settings, separated by |.</summary>
        [Description("Problems with rig.yml or its liveView settings, separated by |.")]
        public string Notes { get; set; }

        /// <summary>Gets or sets how often to send the report.</summary>
        [XmlIgnore]
        [Description("How often to send the report.")]
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets <see cref="Interval"/> in XML's duration form, for the workflow file.</summary>
        [Browsable(false)]
        [XmlElement(nameof(Interval))]
        public string IntervalXml
        {
            get { return XmlConvert.ToString(Interval); }
            set { Interval = XmlConvert.ToTimeSpan(value); }
        }

        /// <summary>Sends the report at once and then every Interval.</summary>
        /// <returns>The report, with the workflow's identity read once.</returns>
        public IObservable<LiveViewReport> Process()
        {
            return Observable.Start(() => WorkflowSource.Read(Environment.CurrentDirectory))
                .SelectMany(source => Observable.Timer(TimeSpan.Zero, Interval).Select(_ => new LiveViewReport
                {
                    Workflow = source.Workflow,
                    Project = source.Project,
                    Repo = source.Repo,
                    Branch = source.Branch,
                    Commit = source.Commit,
                    Dirty = source.Dirty,
                    Streamed = Split(Streamed),
                    Skipped = Split(Skipped),
                    Notes = Split(Notes),
                }));
        }

        static string[] Split(string list)
        {
            return (list ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    /// <summary>
    /// The workflow running and the commit it came from, found as OpenRigs.Manifest finds them
    /// (both compile in src\Shared\WorkflowFile.cs), so the live view and the experiment's
    /// manifest.json name the same workflow.
    /// </summary>
    class WorkflowSource
    {
        public string Workflow;
        public string Project;
        public string Repo;
        public string Branch;
        public string Commit;
        public bool? Dirty;

        public static WorkflowSource Read(string workingDirectory)
        {
            var source = new WorkflowSource();
            var full = WorkflowFile.Find(Environment.GetCommandLineArgs(), workingDirectory);
            var root = Git.FindRoot(WorkflowFile.Folder(full, workingDirectory));
            source.Workflow = WorkflowFile.Relative(full, root);
            if (root == null) return source;
            source.Project = Path.GetFileName(root);
            source.Repo = Git.OriginUrl(root);
            Git.Head(root, out source.Branch, out source.Commit);
            source.Dirty = Git.IsDirty(root);
            return source;
        }
    }
}
