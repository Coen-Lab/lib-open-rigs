using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Xml;
using System.Xml.Serialization;
using Bonsai;
using NetMQ;
using OpenCV.Net;
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

    /// <summary>
    /// Passes on a device's data and any error, but not its end. Each stream ends in a
    /// MulticastSubject into the shared DataMessage or VideoMessage, which passes an end on too:
    /// a device whose data ends (Camera-Zapit.Data in a session that opens no Zapit) would close
    /// that subject, and every other device's stream on its port with it.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("Passes on each element and error, never the end, so a device whose data ends leaves the shared streams open.")]
    public class IgnoreCompletion
    {
        /// <summary>Passes on each element and error of a sequence, never its end.</summary>
        /// <typeparam name="TSource">The type of the elements.</typeparam>
        /// <param name="source">The device's data.</param>
        /// <returns>The same elements and error, never ending.</returns>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return source.Concat(Observable.Never<TSource>());
        }
    }

    /// <summary>
    /// The part of a camera's frame its live view shows: the largest rectangle of the tile's shape
    /// centred in the frame, in the frame's pixels.
    /// </summary>
    public class FrameTrim
    {
        /// <summary>The frame's width as it came, in pixels.</summary>
        public int SourceWidth { get; set; }
        /// <summary>The frame's height as it came, in pixels.</summary>
        public int SourceHeight { get; set; }
        /// <summary>The left edge of the part shown, in pixels of the frame.</summary>
        public int X { get; set; }
        /// <summary>The top edge of the part shown, in pixels of the frame.</summary>
        public int Y { get; set; }
        /// <summary>The width of the part shown, in pixels of the frame.</summary>
        public int Width { get; set; }
        /// <summary>The height of the part shown, in pixels of the frame.</summary>
        public int Height { get; set; }

        /// <summary>Whether the part shown is the whole frame.</summary>
        public bool IsWhole { get { return X == 0 && Y == 0 && Width == SourceWidth && Height == SourceHeight; } }

        /// <summary>The smallest tileWidth x tileHeight shaped rectangle holding the whole sourceWidth x sourceHeight
        /// frame at its centre: X or Y is negative by the black added at each side.</summary>
        public static FrameTrim Fit(int sourceWidth, int sourceHeight, int tileWidth, int tileHeight)
        {
            long width = sourceWidth, height = sourceHeight;
            if (tileWidth > 0 && tileHeight > 0)
            {
                if (width * tileHeight > height * tileWidth) height = Math.Max(sourceHeight, (width * tileHeight + tileWidth / 2) / tileWidth);
                else width = Math.Max(sourceWidth, (height * tileWidth + tileHeight / 2) / tileHeight);
            }
            return new FrameTrim
            {
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                X = -(int)((width - sourceWidth) / 2),
                Y = -(int)((height - sourceHeight) / 2),
                Width = (int)width,
                Height = (int)height,
            };
        }

        /// <summary>The largest tileWidth x tileHeight shaped rectangle centred in a sourceWidth x sourceHeight frame,
        /// never under 1 pixel a side (a tile of extreme shape, such as 1000 x 1, would round one to 0).</summary>
        public static FrameTrim For(int sourceWidth, int sourceHeight, int tileWidth, int tileHeight)
        {
            long width = sourceWidth, height = sourceHeight;
            if (tileWidth > 0 && tileHeight > 0)
            {
                if (width * tileHeight > height * tileWidth) width = Math.Min(sourceWidth, Math.Max(1, (height * tileWidth + tileHeight / 2) / tileHeight));
                else height = Math.Min(sourceHeight, Math.Max(1, (width * tileHeight + tileWidth / 2) / tileWidth));
            }
            return new FrameTrim
            {
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                X = (int)(sourceWidth - width) / 2,
                Y = (int)(sourceHeight - height) / 2,
                Width = (int)width,
                Height = (int)height,
            };
        }
    }

    /// <summary>A camera's frame cut to its live-view tile's shape, and where in the frame it was cut.</summary>
    public class TrimmedFrame
    {
        /// <summary>The part of the frame shown, sharing the frame's pixels.</summary>
        public IplImage Image { get; set; }
        /// <summary>Where that part is in the frame.</summary>
        public FrameTrim Trim { get; set; }
    }

    /// <summary>
    /// Cuts each frame to the shape of the live-view tile, keeping its centre, so that resizing it to
    /// the tile shrinks it evenly and never stretches it. A frame already of that shape is passed whole.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Cuts each frame to the tile's shape, keeping its centre, so resizing it to the tile never stretches it.")]
    public class TrimToTile
    {
        /// <summary>Gets or sets the tile's width; only its shape with Height is used.</summary>
        [Description("The tile's width; only its shape with Height is used.")]
        public int Width { get; set; } = 320;

        /// <summary>Gets or sets the tile's height; only its shape with Width is used.</summary>
        [Description("The tile's height; only its shape with Width is used.")]
        public int Height { get; set; } = 240;

        /// <summary>Gets or sets whether the whole frame is kept, black added at its sides to the tile's shape,
        /// instead of cut (the Zapit camera, so a frame with no crop is shown whole).</summary>
        [Description("Keep the whole frame, adding black at its sides to the tile's shape, instead of cutting it.")]
        public bool Fit { get; set; }

        /// <summary>Cuts each frame to the tile's shape.</summary>
        /// <param name="source">The camera's frames.</param>
        /// <returns>Each frame's centre of the tile's shape, and where it was cut.</returns>
        public IObservable<TrimmedFrame> Process(IObservable<IplImage> source)
        {
            return source.Select(image =>
            {
                var trim = Fit ? FrameTrim.Fit(image.Width, image.Height, Width, Height) : FrameTrim.For(image.Width, image.Height, Width, Height);
                if (trim.IsWhole) return new TrimmedFrame { Image = image, Trim = trim };
                if (!Fit) return new TrimmedFrame { Image = image.GetSubRect(new Rect(trim.X, trim.Y, trim.Width, trim.Height)), Trim = trim };
                var canvas = new IplImage(new Size(trim.Width, trim.Height), image.Depth, image.Channels);
                canvas.SetZero();
                CV.Copy(image, canvas.GetSubRect(new Rect(-trim.X, -trim.Y, image.Width, image.Height)));
                return new TrimmedFrame { Image = canvas, Trim = trim };
            });
        }
    }

    /// <summary>
    /// Adds to a video message's header where its frame was cut (TrimToTile): "sourceSize", the
    /// frame's [width, height] as it came, and "trim", the part shown, [x, y, width, height] in its
    /// pixels. A viewer maps a point on the picture back to the frame with them.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Adds the frame's size and the part of it shown (sourceSize, trim) to each video message's header.")]
    public class AddTrimToHeader
    {
        /// <summary>Adds the trim to each message's header, its second frame.</summary>
        /// <param name="source">Each message paired with the trim of the frame it carries.</param>
        /// <returns>The messages, their headers with sourceSize and trim.</returns>
        public IObservable<NetMQMessage> Process(IObservable<Tuple<NetMQMessage, FrameTrim>> source)
        {
            return source.Select(pair =>
            {
                var message = pair.Item1;
                var trim = pair.Item2;
                var frames = message.ToList();
                var header = frames[1].ConvertToString();
                var at = header.LastIndexOf('}');
                if (at < 0) return message;
                header = header.Substring(0, at) + string.Format(CultureInfo.InvariantCulture,
                    ",\"sourceSize\":[{0},{1}],\"trim\":[{2},{3},{4},{5}]",
                    trim.SourceWidth, trim.SourceHeight, trim.X, trim.Y, trim.Width, trim.Height) + header.Substring(at);
                frames[1] = new NetMQFrame(header);
                return new NetMQMessage(frames);
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
