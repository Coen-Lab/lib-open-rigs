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
    /// running until the controller kills it, and headless Bonsai exits 0 after an error. Nothing in the editor.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    [Description("Ends headless Bonsai if it is still running GraceSeconds after the controller's stop (exit code 0) or after the workflow began to end for any other reason, an error (exit code 1); nothing in the editor.")]
    public class EndAfterStop
    {
        double graceSeconds = 2;

        /// <summary>Gets or sets the seconds the workflow's own teardown is given after the stop; 0 or less leaves it to the controller's kill.</summary>
        [Description("Seconds the workflow's own teardown is given after the stop; 0 or less leaves it to the controller's kill.")]
        public double GraceSeconds { get { return graceSeconds; } set { graceSeconds = value; } }

        /// <summary>Never gives anything; in headless Bonsai (--no-editor), watches for the stop from a background thread.</summary>
        public IObservable<string> Process()
        {
            return Observable.Create<string>(observer =>
            {
                var grace = GraceSeconds;
                if (grace <= 0 || !Environment.GetCommandLineArgs().Contains("--no-editor")) return Disposable.Empty;
                var ending = new ManualResetEventSlim(false);
                var watch = new Thread(() => Watch(ending, TimeSpan.FromSeconds(grace)));
                watch.IsBackground = true;
                watch.Name = "EndAfterStop";
                watch.Start();
                // Disposed means the workflow is ending: the watch goes on, since a teardown held up is what it is for.
                return Disposable.Create(ending.Set);
            });
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
