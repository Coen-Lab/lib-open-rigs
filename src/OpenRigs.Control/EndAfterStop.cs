using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Bonsai;

namespace OpenRigs.Control
{
    /// <summary>
    /// Ends headless Bonsai if it is still running GraceSeconds after the controller's stop (exit code 0) or after the
    /// workflow began to end for any other reason, an error (exit code 1): a device's teardown held up (the live view's
    /// publisher threads keep the process alive until every device's teardown reaches them) would otherwise keep Bonsai
    /// running until the controller kills it, and headless Bonsai exits 0 after an error. It also keeps headless Bonsai
    /// running HoldSeconds once the workflow begins to end, so devices finish closing their serial ports first: a workflow
    /// that logs nothing has no writer thread to do that, and Bonsai exiting mid-close crashed it in SerialStream.Finalize
    /// (ObjectDisposedException) on every preflight stop. Nothing in the editor.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    [Description("Keeps headless Bonsai running HoldSeconds once the workflow begins to end, so devices finish closing their ports, and ends it if it is still running GraceSeconds after the controller's stop (exit code 0) or after the workflow began to end for any other reason, an error (exit code 1); nothing in the editor.")]
    public class EndAfterStop
    {
        double graceSeconds = 2;
        double holdSeconds = 1;

        /// <summary>Gets or sets the seconds the workflow's own teardown is given after the stop; 0 or less leaves it to the controller's kill.</summary>
        [Description("Seconds the workflow's own teardown is given after the stop; 0 or less leaves it to the controller's kill.")]
        public double GraceSeconds { get { return graceSeconds; } set { graceSeconds = value; } }

        /// <summary>Gets or sets the seconds headless Bonsai is kept running once the workflow begins to end; 0 or less keeps it for nothing.</summary>
        [Description("Seconds headless Bonsai is kept running once the workflow begins to end, so devices finish closing their ports; 0 or less keeps it for nothing. Below GraceSeconds.")]
        public double HoldSeconds { get { return holdSeconds; } set { holdSeconds = value; } }

        /// <summary>Never gives anything; in headless Bonsai (--no-editor), watches for the stop from a background thread and holds the process once the workflow ends.</summary>
        public IObservable<string> Process()
        {
            var grace = GraceSeconds;
            var hold = HoldSeconds;
            if (grace > 0 && hold >= grace)
                return Observable.Throw<string>(new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "EndAfterStop: HoldSeconds ({0}) must be below GraceSeconds ({1}), or the grace ends every clean exit", hold, grace)));
            return Observable.Create<string>(observer =>
                Environment.GetCommandLineArgs().Contains("--no-editor") ? Start(grace, hold) : Disposable.Empty);
        }

        static double armedHold;

        /// <summary>
        /// What Process does in headless Bonsai: arms the hold, so the controller's stop starts it (HoldOnStop) before any
        /// device's teardown, watches for the stop when <paramref name="grace"/> is above 0, and holds again once disposed.
        /// </summary>
        public static IDisposable Start(double grace, double hold)
        {
            Volatile.Write(ref armedHold, hold);
            var ending = new ManualResetEventSlim(false);
            if (grace > 0)
            {
                var watch = new Thread(() => Watch(ending, TimeSpan.FromSeconds(grace)));
                watch.IsBackground = true;
                watch.Name = "EndAfterStop";
                watch.Start();
            }
            // Disposed means the workflow is ending (after an error too, with no stop): the watch goes on, since a
            // teardown held up is what it is for, and a hold starts here as well.
            return Disposable.Create(() =>
            {
                Volatile.Write(ref armedHold, 0);
                ending.Set();
                Hold(hold);
            });
        }

        /// <summary>Called by the control server as it takes a stop: starts the armed hold, if an EndAfterStop runs headless.</summary>
        public static Thread HoldOnStop()
        {
            return Hold(Volatile.Read(ref armedHold));
        }

        /// <summary>The last hold started, for the tests.</summary>
        public static Thread LastHold { get; private set; }

        /// <summary>Starts a foreground thread that keeps the process running <paramref name="seconds"/>; null for 0 or less.</summary>
        public static Thread Hold(double seconds)
        {
            if (seconds <= 0) return null;
            var hold = new Thread(() => Thread.Sleep(TimeSpan.FromSeconds(seconds)));
            hold.IsBackground = false;
            hold.Name = "EndAfterStop hold";
            hold.Start();
            LastHold = hold;
            return hold;
        }

        /// <summary>
        /// Why the workflow is ending, or null while it runs: the controller's stop (<paramref name="stopReason"/>, exit
        /// code 0), else, once the workflow began to end (<paramref name="ended"/>), an error (exit code 1).
        /// </summary>
        public static string Why(string stopReason, bool ended, out int exitCode)
        {
            exitCode = stopReason != null ? 0 : 1;
            if (stopReason != null) return "the stop (" + stopReason + ")";
            return ended ? "the workflow began to end with no stop, after an error" : null;
        }

        /// <summary>What EndAfterStop writes to stderr as it ends Bonsai.</summary>
        public static string Message(double graceSeconds, string why, int exitCode)
        {
            return "EndAfterStop: Bonsai was still running " + graceSeconds.ToString(CultureInfo.InvariantCulture) + " s after " + why +
                   ", its teardown held up, so EndAfterStop ends it with exit code " + exitCode.ToString(CultureInfo.InvariantCulture);
        }

        static void Watch(ManualResetEventSlim ending, TimeSpan grace)
        {
            string why = null;
            var code = 1;
            ControlState control = null;
            while (why == null)
            {
                var ended = ending.Wait(100);
                // Kept once seen: the control server forgets its state as it is disposed.
                control = ControlState.Current ?? control;
                why = Why(control == null ? null : control.StopReason, ended, out code);
            }
            Thread.Sleep(grace);
            Console.Error.WriteLine(Message(grace.TotalSeconds, why, code));
            var kill = new Thread(() =>
            {
                Thread.Sleep(3000);
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            });
            kill.IsBackground = true;
            kill.Start();
            Environment.Exit(code);
        }
    }
}
