using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai.Harp;
using System.Xml.Serialization;
using System.Xml;

namespace OpenRigs.Core
{
    /// <summary>
    /// Groups Harp time-series in chunks of ChunkSize hours from the experiment start, the time in the experiment
    /// folder's name (the last input, LogController's PathPrefix): chunk k starts at start + k * ChunkSize hours and
    /// is keyed by that instant (UTC), so FormatDate names it with the offset in force at its own start. A sample
    /// timed before the start goes into chunk 0; samples that arrive before PathPrefix are dropped, as they would be
    /// by a writer still waiting for its file name. A ChunkSize of 0 is one chunk for the whole run, keyed by the start.
    /// </summary>
    [Combinator]
    [Description("Groups Harp time-series in chunks of ChunkSize hours from the experiment start, read from the experiment folder (the last input, PathPrefix), each keyed by its start (UTC). Samples timed before the start go into the first chunk. ChunkSize 0: one chunk for the whole run.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class GroupByTime
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GroupByTime"/> class.
        /// </summary>
        public GroupByTime()
        {
            ChunkSize = 1;
        }

        // The default real-time reference is unix time in total seconds from 1904
        internal static readonly DateTime ReferenceTime = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Gets or sets the size of each chunk, in whole hours; 0 for one chunk for the whole run.</summary>
        [Description("The size of each chunk, in whole hours; 0 for one chunk for the whole run.")]
        public int ChunkSize { get; set; }

        /// <summary>Gets or sets how long after the end of its chunk a group closes, judged by the heartbeats.</summary>
        [XmlIgnore]
        [Description("The relative time at which each group will close following the end of the chunk.")]
        public TimeSpan? ClosingDuration { get; set; }

        /// <summary>Gets or sets ClosingDuration as XML.</summary>
        [Browsable(false)]
        [XmlElement(nameof(ClosingDuration))]
        public string ClosingDurationXml
        {
            get
            {
                var timeShift = ClosingDuration;
                if (timeShift.HasValue) return XmlConvert.ToString(timeShift.Value);
                else return null;
            }
            set
            {
                if (!string.IsNullOrEmpty(value)) ClosingDuration = XmlConvert.ToTimeSpan(value);
                else ClosingDuration = null;
            }
        }

        /// <summary>
        /// The start of the chunk holding a sample at <paramref name="seconds"/> (Harp seconds), for chunks of
        /// <paramref name="chunkHours"/> from <paramref name="start"/>: chunk 0 for a sample before the start, and
        /// for every sample when <paramref name="chunkHours"/> is 0.
        /// </summary>
        public static DateTime ChunkStart(DateTime start, double seconds, int chunkHours)
        {
            if (chunkHours < 0) throw new InvalidOperationException("GroupByTime's ChunkSize is " + chunkHours + ": give it a whole number of hours, or 0 for one chunk for the whole run.");
            start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
            if (chunkHours == 0) return start;
            var chunkTicks = TimeSpan.FromHours(chunkHours).Ticks;
            var index = Math.Max(0, (ReferenceTime.AddSeconds(seconds) - start).Ticks / chunkTicks);
            return start.AddTicks(index * chunkTicks);
        }

        // Each value with its chunk's start, once PathPrefix has given the experiment start. PathPrefix is subscribed
        // before the source, so the start is known before a logger gated on PathPrefix (HarpLookBack) passes on what it held.
        IObservable<Tuple<T, DateTime>> Keyed<T>(IObservable<T> source, IObservable<string> pathPrefix, Func<T, double> seconds)
        {
            var chunkHours = ChunkSize;
            return Observable.Create<Tuple<T, DateTime>>(observer =>
            {
                var gate = new object();
                DateTime? start = null;
                var startSubscription = pathPrefix.Select(FormatDate.FolderStart).Subscribe(
                    at => { lock (gate) start = at; },
                    error => { lock (gate) observer.OnError(error); });
                var sourceSubscription = source.Subscribe(
                    value => { lock (gate) if (start.HasValue) observer.OnNext(Tuple.Create(value, ChunkStart(start.Value, seconds(value), chunkHours))); },
                    error => { lock (gate) observer.OnError(error); },
                    () => { lock (gate) observer.OnCompleted(); });
                return new CompositeDisposable(startSubscription, sourceSubscription);
            });
        }

        // A chunk of the whole run (ChunkSize 0) has no end for a heartbeat to pass.
        bool ShouldCloseChunk<TElement>(IGroupedObservable<DateTime, TElement> chunk, HarpMessage heartbeat)
        {
            if (ChunkSize == 0) return false;
            var beatTimestamp = ReferenceTime.AddSeconds(heartbeat.GetTimestamp());
            var beatDelta = beatTimestamp - chunk.Key;
            return beatDelta > new TimeSpan(ChunkSize, 0, 0) + ClosingDuration;
        }

        /// <summary>Groups timestamped values into chunks from the experiment start.</summary>
        public IObservable<IGroupedObservable<DateTime, Timestamped<TSource>>> Process<TSource>(
            IObservable<Timestamped<TSource>> source,
            IObservable<string> pathPrefix)
        {
            return Keyed(source, pathPrefix, value => value.Seconds).GroupBy(pair => pair.Item2, pair => pair.Item1);
        }

        /// <summary>Groups Harp messages into chunks from the experiment start.</summary>
        public IObservable<IGroupedObservable<DateTime, HarpMessage>> Process(
            IObservable<HarpMessage> source,
            IObservable<string> pathPrefix)
        {
            return Keyed(source, pathPrefix, value => value.GetTimestamp()).GroupBy(pair => pair.Item2, pair => pair.Item1);
        }

        /// <summary>Groups (value, Harp seconds) pairs into chunks from the experiment start.</summary>
        public IObservable<IGroupedObservable<DateTime, Timestamped<TSource>>> Process<TSource>(
            IObservable<Tuple<TSource, double>> source,
            IObservable<string> pathPrefix)
        {
            return Keyed(source, pathPrefix, value => value.Item2).GroupBy(pair => pair.Item2, pair => Timestamped.Create(pair.Item1.Item1, pair.Item1.Item2));
        }

        /// <summary>Groups timestamped values into chunks from the experiment start, each closed by the heartbeats.</summary>
        public IObservable<IGroupedObservable<DateTime, Timestamped<TSource>>> Process<TSource>(
            IObservable<Timestamped<TSource>> source,
            IObservable<HarpMessage> heartbeats,
            IObservable<string> pathPrefix)
        {
            return Keyed(source, pathPrefix, value => value.Seconds).GroupByUntil(
                pair => pair.Item2, pair => pair.Item1,
                chunk => heartbeats.FirstOrDefaultAsync(message => ShouldCloseChunk(chunk, message)));
        }

        /// <summary>Groups Harp messages into chunks from the experiment start, each closed by the heartbeats.</summary>
        public IObservable<IGroupedObservable<DateTime, HarpMessage>> Process(
            IObservable<HarpMessage> source,
            IObservable<HarpMessage> heartbeats,
            IObservable<string> pathPrefix)
        {
            return Keyed(source, pathPrefix, value => value.GetTimestamp()).GroupByUntil(
                pair => pair.Item2, pair => pair.Item1,
                chunk => heartbeats.FirstOrDefaultAsync(message => ShouldCloseChunk(chunk, message)));
        }

        /// <summary>Groups (value, Harp seconds) pairs into chunks from the experiment start, each closed by the heartbeats.</summary>
        public IObservable<IGroupedObservable<DateTime, Timestamped<TSource>>> Process<TSource>(
            IObservable<Tuple<TSource, double>> source,
            IObservable<HarpMessage> heartbeats,
            IObservable<string> pathPrefix)
        {
            return Keyed(source, pathPrefix, value => value.Item2).GroupByUntil(
                pair => pair.Item2, pair => Timestamped.Create(pair.Item1.Item1, pair.Item1.Item2),
                chunk => heartbeats.FirstOrDefaultAsync(message => ShouldCloseChunk(chunk, message)));
        }
    }
}
