using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// The subscription ExperimentManifest builds: reads what is fixed at Start, writes the manifest
    /// when the experiment folder's path arrives, and again with the outcome when the run ends.
    /// </summary>
    public class ManifestRecorder
    {
        readonly string ingestScript;
        readonly string configFiles;
        readonly string configRoot;
        readonly ExpectedPlan expected;
        readonly string trialColumns;
        readonly string streamColumns;

        // How often the controller's stop is looked for while the run goes on: ControlExperiment is read by name, with nothing to subscribe to.
        internal static TimeSpan StopPoll = TimeSpan.FromMilliseconds(250);

        // How long the final write at disposal waits for every file of the experiment to read closed:
        // beyond ffmpeg's 20 s StopTimeout, so a camera's video that closes in time reads closed.
        internal static TimeSpan CloseWait = TimeSpan.FromSeconds(25);

        // How often the files are looked at while the final write waits.
        static readonly TimeSpan ClosePoll = TimeSpan.FromMilliseconds(100);

        // The final writes' threads, for WaitForFinalWrites.
        static readonly List<Thread> Closers = new List<Thread>();

        /// <summary>For tests: waits, at most the timeout, for every final write started at disposal to be done; true when they are.</summary>
        internal static bool WaitForFinalWrites(TimeSpan timeout)
        {
            Thread[] waiting;
            lock (Closers)
            {
                Closers.RemoveAll(t => !t.IsAlive && t.ThreadState != System.Threading.ThreadState.Unstarted);
                waiting = Closers.ToArray();
            }
            var clock = Stopwatch.StartNew();
            foreach (var thread in waiting)
            {
                var left = timeout - clock.Elapsed;
                if (left < TimeSpan.Zero || !thread.Join(left)) return false;
            }
            return true;
        }

        internal ManifestRecorder(string ingestScript, string configFiles, string configRoot, ExpectedPlan expected, string trialColumns = null, string streamColumns = null)
        {
            this.ingestScript = ingestScript;
            this.configFiles = configFiles;
            this.configRoot = configRoot;
            this.expected = expected;
            this.trialColumns = trialColumns;
            this.streamColumns = streamColumns;
        }

        /// <summary>No run input: the end is read from OpenRigs.Control's phase, if it is there.</summary>
        /// <param name="experimentPath">The experiment folder's path, from PathPrefix.</param>
        /// <returns>The manifest's path, once the first write is done.</returns>
        public IObservable<string> Process(IObservable<string> experimentPath)
        {
            return Record<object>(experimentPath, NoConditions, null);
        }

        /// <summary>No run input, with the values of the conditions ExpectedFiles names.</summary>
        /// <param name="experimentPath">The experiment folder's path, from PathPrefix.</param>
        /// <param name="conditions">Each condition's subject's values, by the condition's name.</param>
        /// <returns>The manifest's path, once the first write is done.</returns>
        public IObservable<string> Process(IObservable<string> experimentPath, IObservable<KeyValuePair<string, bool>> conditions)
        {
            return Record<object>(experimentPath, conditions, null);
        }

        /// <summary>The run, with the values of the conditions ExpectedFiles names.</summary>
        /// <typeparam name="TRun">The type of the run's elements.</typeparam>
        /// <param name="experimentPath">The experiment folder's path, from PathPrefix.</param>
        /// <param name="conditions">Each condition's subject's values, by the condition's name.</param>
        /// <param name="run">The run.</param>
        /// <returns>The manifest's path, once the first write is done.</returns>
        public IObservable<string> Process<TRun>(IObservable<string> experimentPath, IObservable<KeyValuePair<string, bool>> conditions, IObservable<TRun> run)
        {
            return Record(experimentPath, conditions, run);
        }

        /// <summary>The conditions' subjects as one sequence of (name, value), for Process.</summary>
        /// <param name="names">Each condition's name, in the order of its subject.</param>
        /// <param name="subjects">Each condition's bool subject.</param>
        /// <returns>Every value any of them sends, with its condition's name.</returns>
        public static IObservable<KeyValuePair<string, bool>> Conditions(string[] names, IObservable<bool>[] subjects)
        {
            return subjects.Select((subject, i) => subject.Select(value => new KeyValuePair<string, bool>(names[i], value))).Merge();
        }

        static readonly IObservable<KeyValuePair<string, bool>> NoConditions = Observable.Empty<KeyValuePair<string, bool>>();

        /// <summary>With the run: its completion is completed, its error the run's error, its elements run.items.</summary>
        /// <typeparam name="TRun">The type of the run's elements.</typeparam>
        /// <param name="experimentPath">The experiment folder's path, from PathPrefix.</param>
        /// <param name="run">The run.</param>
        /// <returns>The manifest's path, once the first write is done.</returns>
        public IObservable<string> Process<TRun>(IObservable<string> experimentPath, IObservable<TRun> run)
        {
            return Record(experimentPath, NoConditions, run);
        }

        IObservable<string> Record<TRun>(IObservable<string> experimentPath, IObservable<KeyValuePair<string, bool>> conditions, IObservable<TRun> run)
        {
            return Observable.Create<string>(observer =>
            {
                // Each run starts with no condition known: a value from an earlier run is not this one's.
                expected.ClearConditions();
                // At Start: the launch, the workflow's commit and the config files, as they are now.
                var writer = new ManifestWriter(ingestScript, ManifestWriter.Snapshot(configFiles, configRoot, run != null, trialColumns, streamColumns), expected);
                var gate = new object();
                var items = 0L;
                var begun = false;
                var finished = false;
                string provisional = null;  // the outcome last written with run.final false
                // How the run ended, kept until the folder's path arrives: the run may end before
                // LogController has made the folder, and the manifest must still say so.
                RunEnd pending = null;
                ControlExperiment control = null;
                var subscriptions = new CompositeDisposable();
                // Before the path, so a BehaviorSubject's value is in by the first write.
                subscriptions.Add(conditions.Subscribe(c => expected.SetCondition(c.Key, c.Value), observer.OnError));

                Action<RunEnd> finish = end =>
                {
                    lock (gate)
                    {
                        if (finished) return;
                        // The first end is the run's. Finished is set only once the write is done, so
                        // a write that failed (the file held by a reader) is tried again at disposal,
                        // with the same end.
                        end = pending ?? end;
                        pending = end;
                        if (!begun) return;
                        writer.Finish(end, run != null ? (long?)items : null, control);
                        finished = true;
                    }
                };

                // A write that fails ends the workflow through this node's error, as any Bonsai
                // node's would. Thrown instead, it would go back up the thread that delivered the
                // path or the run's end: from Go, that is ControlServer's own thread, where an
                // exception kills Bonsai with nothing closed.
                subscriptions.Add(experimentPath.Take(1).Subscribe(path =>
                {
                    string manifestPath;
                    RunEnd ended;
                    try
                    {
                        lock (gate)
                        {
                            if (begun) return;
                            begun = true;
                            // PathPrefix comes after Go, so a running ExperimentControl has its Go by now.
                            control = ControlExperiment.Current();
                            manifestPath = writer.Begin(path, control);
                            ended = pending;
                            if (ended != null)
                            {
                                writer.Finish(ended, run != null ? (long?)items : null, control);
                                finished = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                        return;
                    }
                    observer.OnNext(manifestPath);
                    // Only a completion is held for the folder: a run's error ends the node at once.
                    if (ended != null)
                    {
                        observer.OnCompleted();
                        return;
                    }
                    // The workflow's Finished and the controller's stop are written as soon as they
                    // come, the run still going, as completed or stopped with run.final false:
                    // A controller kills a workflow that has not stopped by its deadline, and the
                    // end-of-run write never comes. Not final, so the controller keeps an
                    // editor's Bonsai open until the end-of-run write, which sets run.final true.
                    if (control != null) subscriptions.Add(Observable.Interval(StopPoll).Subscribe(_ =>
                    {
                        try
                        {
                            lock (gate)
                            {
                                if (finished || (!control.FinishedAt.HasValue && control.StopReason == null)) return;
                                var end = RunEnd.AtDisposal(control);
                                if (end.Outcome == provisional) return;
                                writer.Provisional(end, run != null ? (long?)items : null, control);
                                provisional = end.Outcome;
                            }
                        }
                        catch (Exception ex) { NotWritten("the run's end so far", ex); }
                    }));
                }, observer.OnError));

                if (run != null)
                {
                    subscriptions.Add(run.Subscribe(
                        _ => { lock (gate) { items++; } },
                        error =>
                        {
                            // After the workflow's Finished or the controller's stop, the error is how
                            // the workflow stopped, not why the run ended: AtDisposal says which.
                            var c = control;
                            var end = c != null && (c.FinishedAt.HasValue || c.StopReason != null) ? RunEnd.AtDisposal(c) : new RunEnd(RunEnd.Failed, error.Message);
                            try { finish(end); }
                            catch (Exception ex) { NotWritten("the final manifest", ex); }
                            // Before the folder exists there is no experiment to record, so the error
                            // goes on at once: held until the folder appeared, a run that failed at
                            // Ready left the workflow standing with a dead run, and a Go then made a
                            // folder for it. A completion still waits for the folder.
                            observer.OnError(error);
                        },
                        () =>
                        {
                            try { finish(new RunEnd(RunEnd.Completed, null)); }
                            catch (Exception ex) { observer.OnError(ex); return; }
                            lock (gate) { if (!begun) return; }
                            observer.OnCompleted();
                        }));
                }

                // Disposed before the run ended: the workflow is stopping. Why is read from the
                // controller's phase, the only witness this node has.
                // The other loggers close their files as the same disposal goes on (a camera's CSV and
                // video once its queue drains and ffmpeg exits), so the final write waits for every file
                // to read closed, at most CloseWait, on a thread of its own: disposal does not block,
                // and a foreground thread keeps a headless Bonsai open until the file is written. Written
                // at once, every camera's CSV read closed: false.
                subscriptions.Add(Disposable.Create(() =>
                {
                    RunEnd end;
                    try
                    {
                        end = RunEnd.AtDisposal(control ?? ControlExperiment.Current());
                        lock (gate)
                        {
                            if (finished) return;
                            if (!begun)
                            {
                                finish(end);  // writes nothing: the end is kept for a folder that never comes
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        NotWritten("the final manifest", ex);
                        return;
                    }
                    var closer = new Thread(() =>
                    {
                        try
                        {
                            var waited = Stopwatch.StartNew();
                            while (waited.Elapsed < CloseWait && !writer.FilesClosed()) Thread.Sleep(ClosePoll);
                            finish(end);
                        }
                        catch (Exception ex) { NotWritten("the final manifest", ex); }
                    }) { IsBackground = false, Name = "ExperimentManifest final write" };
                    lock (Closers) Closers.Add(closer);
                    closer.Start();
                }));
                return subscriptions;
            });
        }

        // Where nothing downstream can take the error: Bonsai's console shows stderr, which a trace alone does not reach.
        static void NotWritten(string what, Exception ex)
        {
            var message = "ExperimentManifest: " + what + " was not written: " + ex.Message;
            Trace.TraceError(message);
            Console.Error.WriteLine(message);
        }
    }

    /// <summary>How the run ended, as the manifest's run section says it.</summary>
    public class RunEnd
    {
        /// <summary>The run has not ended.</summary>
        public const string Running = "running";
        /// <summary>The run's sequence completed, or the workflow reported Finished.</summary>
        public const string Completed = "completed";
        /// <summary>The controller (or, without one, the editor) stopped the workflow before that.</summary>
        public const string Stopped = "stopped";
        /// <summary>The run's sequence failed.</summary>
        public const string Failed = "error";
        /// <summary>With a controller: the workflow ended before Finished and without a stop.</summary>
        public const string EndedEarly = "ended-early";

        /// <summary>Initializes a new instance of the <see cref="RunEnd"/> class.</summary>
        /// <param name="outcome">One of the outcomes above.</param>
        /// <param name="error">The error, or null.</param>
        public RunEnd(string outcome, string error)
        {
            Outcome = outcome;
            Error = error;
        }

        /// <summary>Gets the outcome, as the manifest's run.outcome says it.</summary>
        public string Outcome { get; }
        /// <summary>Gets the error, or null.</summary>
        public string Error { get; }

        /// <summary>
        /// The end when the workflow stops with the run unfinished. With a controller: completed
        /// if the workflow reported Finished (even if the controller stopped it after, as
        /// a controller does after every run), stopped if the controller stopped it before, otherwise
        /// the run ended early from something this node cannot see (an error elsewhere, the
        /// editor's Stop button). Without one, stopped: the editor's Stop is how such a run ends.
        /// </summary>
        internal static RunEnd AtDisposal(ControlExperiment control)
        {
            if (control == null) return new RunEnd(Stopped, null);
            if (control.FinishedAt.HasValue) return new RunEnd(Completed, null);
            // The controller's stop, which sets the reason (ControlServer's stop), not the phase alone.
            if (control.StopReason != null) return new RunEnd(Stopped, null);
            return new RunEnd(EndedEarly, EndedEarlyError);
        }

        internal const string EndedEarlyError = "The workflow ended before the run finished and without a stop from the controller: an error elsewhere in the workflow, or the editor's Stop button. Bonsai's output says which.";
    }
}
