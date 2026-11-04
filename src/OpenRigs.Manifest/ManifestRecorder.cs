using System;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// The subscription SessionManifest builds: reads what is fixed at Start, writes the manifest
    /// when the session folder's path arrives, and again with the outcome when the run ends.
    /// </summary>
    public class ManifestRecorder
    {
        readonly string ingestScript;
        readonly string configFiles;
        readonly ExpectedPlan expected;

        internal ManifestRecorder(string ingestScript, string configFiles, ExpectedPlan expected)
        {
            this.ingestScript = ingestScript;
            this.configFiles = configFiles;
            this.expected = expected;
        }

        /// <summary>No run input: the end is read from OpenRigs.Control's phase, if it is there.</summary>
        /// <param name="sessionPath">The session folder's path, from PathPrefix.</param>
        /// <returns>The manifest's path, once the first write is done.</returns>
        public IObservable<string> Process(IObservable<string> sessionPath)
        {
            return Record<object>(sessionPath, null);
        }

        /// <summary>With the run: its completion is completed, its error the run's error, its elements run.items.</summary>
        /// <typeparam name="TRun">The type of the run's elements.</typeparam>
        /// <param name="sessionPath">The session folder's path, from PathPrefix.</param>
        /// <param name="run">The run.</param>
        /// <returns>The manifest's path, once the first write is done.</returns>
        public IObservable<string> Process<TRun>(IObservable<string> sessionPath, IObservable<TRun> run)
        {
            return Record(sessionPath, run);
        }

        IObservable<string> Record<TRun>(IObservable<string> sessionPath, IObservable<TRun> run)
        {
            return Observable.Create<string>(observer =>
            {
                // At Start: the launch, the workflow's commit and the config files, as they are now.
                var writer = new ManifestWriter(ingestScript, ManifestWriter.Snapshot(configFiles, run != null), expected);
                var gate = new object();
                var items = 0L;
                var begun = false;
                var finished = false;
                // How the run ended, kept until the folder's path arrives: the run may end before
                // LogController has made the folder, and the manifest must still say so.
                RunEnd pending = null;
                ControlSession control = null;
                var subscriptions = new CompositeDisposable();

                Action<RunEnd> finish = end =>
                {
                    lock (gate)
                    {
                        if (finished) return;
                        if (!begun) { pending = end; return; }
                        finished = true;
                        writer.Finish(end, run != null ? (long?)items : null, control);
                    }
                };

                // A write that fails ends the workflow through this node's error, as any Bonsai
                // node's would. Thrown instead, it would go back up the thread that delivered the
                // path or the run's end: from Go, that is ControlServer's own thread, where an
                // exception kills Bonsai with nothing closed.
                subscriptions.Add(sessionPath.Take(1).Subscribe(path =>
                {
                    string manifestPath;
                    RunEnd ended;
                    try
                    {
                        lock (gate)
                        {
                            if (begun) return;
                            begun = true;
                            // PathPrefix comes after Go, so a running SessionControl has its Go by now.
                            control = ControlSession.Current();
                            manifestPath = writer.Begin(path, control);
                            ended = pending;
                            if (ended != null)
                            {
                                finished = true;
                                writer.Finish(ended, run != null ? (long?)items : null, control);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                        return;
                    }
                    observer.OnNext(manifestPath);
                    if (ended != null)
                    {
                        if (ended.Outcome == RunEnd.Failed) observer.OnError(new SessionManifestException(ended.Error));
                        else observer.OnCompleted();
                    }
                }, observer.OnError));

                if (run != null)
                {
                    subscriptions.Add(run.Subscribe(
                        _ => { lock (gate) { items++; } },
                        error =>
                        {
                            try { finish(new RunEnd(RunEnd.Failed, error.Message)); }
                            catch (Exception ex) { NotWritten(ex); }
                            lock (gate) { if (!begun) return; }
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
                subscriptions.Add(Disposable.Create(() =>
                {
                    try { finish(RunEnd.AtDisposal(control ?? ControlSession.Current())); }
                    catch (Exception ex) { NotWritten(ex); }
                }));
                return subscriptions;
            });
        }

        // Where nothing downstream can take the error: Bonsai's console shows stderr, which a trace alone does not reach.
        static void NotWritten(Exception ex)
        {
            var message = "SessionManifest: the final manifest was not written: " + ex.Message;
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
        /// rig-control does after every run), stopped if the controller stopped it before, otherwise
        /// the run ended early from something this node cannot see (an error elsewhere, the
        /// editor's Stop button). Without one, stopped: the editor's Stop is how such a run ends.
        /// </summary>
        internal static RunEnd AtDisposal(ControlSession control)
        {
            if (control == null) return new RunEnd(Stopped, null);
            if (control.FinishedAt.HasValue) return new RunEnd(Completed, null);
            // The controller's stop, which sets the reason (ControlServer's stop), not the phase alone.
            if (control.StopReason != null) return new RunEnd(Stopped, null);
            return new RunEnd(EndedEarly, "The workflow ended before the run finished and without a stop from the controller: an error elsewhere in the workflow, or the editor's Stop button. Bonsai's output says which.");
        }
    }

    /// <summary>The error carried into the manifest when the run failed before the folder existed.</summary>
    public class SessionManifestException : Exception
    {
        /// <summary>Initializes a new instance with the run's error.</summary>
        /// <param name="message">The run's error.</param>
        public SessionManifestException(string message) : base(message) { }
    }
}
