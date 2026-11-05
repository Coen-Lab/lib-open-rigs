using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Xml.Serialization;
using Bonsai;

namespace OpenRigs.Logging
{
    /// <summary>
    /// One row a log wrote, as it was written: the session and log it belongs to, its number in
    /// that log, the log's header and the line itself.
    /// </summary>
    public class LoggedRow
    {
        /// <summary>
        /// Gets or sets the session the row was written into, as its folder's last two names
        /// (sub-&lt;SubjectId&gt;/ses-&lt;SessionId&gt;_&lt;DateTime&gt;), the key OpenRigs.Streaming's messages carry.
        /// </summary>
        public string Session { get; set; }

        /// <summary>Gets or sets the log's name, such as BonsaiData_Trial: its file name without the hour or extension.</summary>
        public string Log { get; set; }

        /// <summary>
        /// Gets or sets the row's number in its log, from 0, counted across the log's hourly files
        /// in this session, so it is the row's index in the log read whole.
        /// </summary>
        public long Row { get; set; }

        /// <summary>Gets or sets the log's header line, its column names; empty for a log written without one.</summary>
        public string Header { get; set; }

        /// <summary>Gets or sets the line written, exactly as it is in the file, without its line break.</summary>
        public string Line { get; set; }
    }

    /// <summary>
    /// Represents an operator that emits every row a log with PublishRows on writes, as it is
    /// written, from anywhere in the workflow.
    /// </summary>
    /// <remarks>
    /// A live view needs a session's trials as they happen, and the rows are written deep in a
    /// workflow (av-dome's LogTrial sits inside a SelectMany, whose subjects nothing at the top
    /// level can see). So the rows go through one place in the process instead of a subject: every
    /// CsvWriter with PublishRows on hands its rows here, and OpenRigs.LiveView's StreamDevices,
    /// last in the workflow, streams them without the workflow wiring anything. One Bonsai runs one
    /// workflow, so the process is the session.
    /// </remarks>
    [XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Emits every row a log with PublishRows on writes (LogTrial and LogAsync turn it on), as it is written, from anywhere in the workflow: its session, log name, row number, header and line.")]
    public class LoggedRows : Source<LoggedRow>
    {
        static readonly object Gate = new object();
        static readonly Subject<LoggedRow> Rows = new Subject<LoggedRow>();
        // Rows written so far in each session's log, by session folder and log name: the next row's number.
        static readonly Dictionary<string, long> Counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Emits every row a log with PublishRows on writes from now on.</summary>
        /// <returns>The rows, as they are written.</returns>
        public override IObservable<LoggedRow> Generate()
        {
            return Rows.AsObservable();
        }

        /// <summary>
        /// Writes one row of a CsvWriter, then flushes it and publishes it as asked. Called by the
        /// write CsvWriter builds when AutoFlush or PublishRows is on; not meant to be called otherwise.
        /// </summary>
        /// <param name="writer">The log file's writer.</param>
        /// <param name="line">The row's line, without its line break.</param>
        /// <param name="header">The log's header line.</param>
        /// <param name="flush">Whether to flush the row to disk before publishing it.</param>
        /// <param name="publish">Whether to publish the row.</param>
        public static void WriteRow(TextWriter writer, string line, string header, bool flush, bool publish)
        {
            writer.WriteLine(line);
            // Flushed before it is published, so a reader told of a row finds it in the file.
            if (flush) writer.Flush();
            if (!publish) return;

            var path = FilePath(writer);
            if (path == null) return;
            var logFolder = Path.GetDirectoryName(path);
            var session = Path.GetDirectoryName(logFolder);
            var subject = Path.GetDirectoryName(session);
            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(subject)) return;
            var log = LogName(path);
            var key = session + "|" + log;
            // Under one lock, numbering and emitting together, so rows from two logs written on
            // two threads reach subscribers one at a time and each log's in its own order.
            lock (Gate)
            {
                long row;
                Counts.TryGetValue(key, out row);
                Counts[key] = row + 1;
                Rows.OnNext(new LoggedRow
                {
                    Session = Path.GetFileName(subject) + "/" + Path.GetFileName(session),
                    Log = log,
                    Row = row,
                    Header = header ?? string.Empty,
                    Line = line,
                });
            }
        }

        // The file a writer writes into; null if it is not a file (nothing to name the row by).
        static string FilePath(TextWriter writer)
        {
            var file = (writer as StreamWriter)?.BaseStream as FileStream;
            return file?.Name;
        }

        /// <summary>
        /// A log's name from one of its files: LogData names them &lt;Log&gt;_&lt;hour&gt;.&lt;extension&gt;
        /// (FormatFileName), and the hour holds no underscore, so the name is everything before the last one.
        /// </summary>
        /// <param name="path">The log file's path.</param>
        /// <returns>The log's name.</returns>
        public static string LogName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var hour = name.LastIndexOf('_');
            return hour > 0 ? name.Substring(0, hour) : name;
        }
    }
}
