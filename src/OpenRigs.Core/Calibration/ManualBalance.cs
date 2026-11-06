using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenRigs.Core.Calibration
{
    /// <summary>
    /// The balance a dispense calibration uses, given at run time: a serial port for a balance the
    /// computer reads, or <see cref="Manual"/> for one read by eye, each weight typed when asked.
    /// </summary>
    public static class BalancePort
    {
        /// <summary>The port value that means a balance read by eye.</summary>
        public const string Manual = "manual";

        /// <summary>Whether a port value means a balance read by eye, ignoring case and spaces.</summary>
        public static bool IsManual(string port)
        {
            return string.Equals((port ?? "").Trim(), Manual, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Sets the dispense calibration's balance port from the one given at run time. A manual balance
    /// (port <c>manual</c>) gets a settle window of one reading, the one weight typed, and
    /// <see cref="ManualTimeoutSeconds"/> to type it.
    /// </summary>
    [Combinator]
    [Description("Sets the settings' balance port to the one given at run time (second input), as written: a COM port, or 'manual' (written in lower case) for a balance read by eye, which also makes each weighing take the one weight typed (settleWindow 1) and wait ManualTimeoutSeconds for it. An empty port fails, naming both choices. Input is a Tuple of the DispenseCalibration settings and the port.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ApplyBalancePort
    {
        /// <summary>Gets or sets how long a manual weighing waits for the weight to be typed, in seconds.</summary>
        [Description("How long a manual weighing waits for the weight to be typed, in seconds.")]
        public double ManualTimeoutSeconds { get; set; } = 3600;

        /// <summary>Sets each settings' balance port.</summary>
        public IObservable<DispenseCalibration> Process(IObservable<Tuple<DispenseCalibration, string>> source)
        {
            return source.Select(input => Apply(input.Item1, input.Item2, ManualTimeoutSeconds));
        }

        internal static DispenseCalibration Apply(DispenseCalibration settings, string port, double manualTimeoutSeconds)
        {
            port = (port ?? "").Trim();
            if (port.Length == 0)
            {
                throw new InvalidOperationException(
                    "The balance port is empty: give the balance's COM port, such as COM5, or manual to type each weight.");
            }

            if (settings.Balance == null) settings.Balance = new SerialBalance();
            if (BalancePort.IsManual(port))
            {
                settings.Balance.PortName = BalancePort.Manual;
                settings.Procedure.SettleWindow = 1;
                settings.Procedure.WeighTimeoutS = manualTimeoutSeconds;
            }
            else
            {
                settings.Balance.PortName = port;
            }

            return settings;
        }
    }

    /// <summary>
    /// What a manual balance says and takes: the line telling the person what to do, and the weight
    /// they typed when one was due.
    /// </summary>
    public class ManualWeighingEvent
    {
        /// <summary>What to do now, and what became of the last weight typed.</summary>
        public string Prompt { get; set; }

        /// <summary>The weight taken, or null.</summary>
        public WeightMeasurement? Reading { get; set; }
    }

    /// <summary>
    /// A balance read by eye. Follows the dispense procedure's bursts by the pulses it fires and asks
    /// for a weight after each: the baseline after priming, then one per sample. A weight typed while
    /// one is due is that burst's reading; one typed at any other time is refused, saying why.
    /// </summary>
    [Combinator]
    [Description("A balance read by eye. Follows the procedure's bursts by the pulses fired (first input: the settings when the run starts; second: the (channel, size) pulse requests) and, once each burst has landed, asks for a weight in its Prompt: the baseline after priming, then each sample's. The next weight typed (third input: text in grams, such as 20.415 or 20.415 g) is that burst's reading. A weight typed when none is due, or text that is not a weight, is refused in the Prompt.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class ManualWeighing
    {
        struct Burst
        {
            public string Name;
            public int Count;
            public int Size;
            public int Interval;
        }

        /// <summary>Asks for and takes each weight of the runs the settings start.</summary>
        public IObservable<ManualWeighingEvent> Process(
            IObservable<DispenseCalibration> start,
            IObservable<Tuple<int, int>> pulses,
            IObservable<string> typed)
        {
            return Observable.Create<ManualWeighingEvent>(observer =>
            {
                var gate = new object();
                List<Burst> bursts = null;
                string unit = "";
                int index = 0, fired = 0;
                bool due = false;
                string note = "";
                var timer = new SerialDisposable();
                Action<string> say = prompt =>
                {
                    observer.OnNext(new ManualWeighingEvent { Prompt = prompt + (note.Length > 0 ? " " + note : "") });
                };
                Func<string> dueText = () => index == 0
                    ? string.Format("Weigh Now: the baseline, the container's weight {0}.",
                        bursts[0].Count > 0 ? "after " + bursts[0].Count + " priming pulses" : "before any pulse")
                    : string.Format("Weigh Now: {0}, after {1} pulses of {2} {3}.", bursts[index].Name, bursts[index].Count, bursts[index].Size, unit);
                Action land = () =>
                {
                    // PulseBurst completes one period after its last pulse, so the drop has landed
                    // before the weighing starts; a little after that, the person is asked.
                    var burst = bursts[index];
                    var wait = TimeSpan.FromMilliseconds(burst.Count > 0 ? burst.Size + burst.Interval + 250 : 250);
                    timer.Disposable = Observable.Timer(wait).Subscribe(_ =>
                    {
                        lock (gate)
                        {
                            due = true;
                            say(dueText());
                        }
                    });
                };

                say("Manual balance: once the run starts, each weight is asked for here; type it in grams.");
                var subscriptions = new CompositeDisposable(timer);
                subscriptions.Add(start.Subscribe(settings =>
                {
                    lock (gate)
                    {
                        var p = settings.Procedure;
                        unit = p.XUnit ?? "";
                        int interval = p.Samples.Max(s => s.InterPulseIntervalMs);
                        bursts = new List<Burst>();
                        bursts.Add(new Burst
                        {
                            Name = "priming",
                            Count = p.PrimingPulses,
                            Size = p.PrimingSize ?? p.Samples.Max(s => s.Size),
                            Interval = interval
                        });
                        for (int i = 0; i < p.Samples.Count; i++)
                        {
                            bursts.Add(new Burst
                            {
                                Name = string.Format("sample {0} of {1}", i + 1, p.Samples.Count),
                                Count = p.Samples[i].Repeats,
                                Size = p.Samples[i].Size,
                                Interval = p.Samples[i].InterPulseIntervalMs
                            });
                        }

                        index = fired = 0;
                        due = false;
                        note = "";
                        if (bursts[0].Count > 0) say("Priming the line: wait.");
                        else land();
                    }
                }, observer.OnError));
                subscriptions.Add(pulses.Subscribe(pulse =>
                {
                    lock (gate)
                    {
                        if (pulse == null || bursts == null || due || index >= bursts.Count) return;
                        fired++;
                        var burst = bursts[index];
                        say(string.Format("Firing {0}: pulse {1} of {2}, {3} {4}. Wait.", burst.Name, fired, burst.Count, burst.Size, unit));
                        if (fired >= burst.Count) land();
                    }
                }, observer.OnError));
                subscriptions.Add(typed.Subscribe(text =>
                {
                    lock (gate)
                    {
                        var entry = (text ?? "").Trim();
                        var number = entry.EndsWith("g", StringComparison.OrdinalIgnoreCase) ? entry.Substring(0, entry.Length - 1).Trim() : entry;
                        double grams;
                        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out grams) || grams < 0)
                        {
                            note = string.Format("('{0}' is not a weight in grams: type it again.)", entry);
                            say(due ? dueText() : "No weight is due yet: wait.");
                            return;
                        }

                        if (!due)
                        {
                            note = string.Format("({0} g refused: no weight was due. Wait to be asked.)", number);
                            say(bursts == null ? "No weight is due before the run starts." : "No weight is due yet: wait.");
                            return;
                        }

                        var name = index == 0 ? "the baseline" : bursts[index].Name;
                        var reading = new WeightMeasurement(0, (float)grams, 1);
                        due = false;
                        index++;
                        fired = 0;
                        note = string.Format("({0} g taken for {1}.)", number, name);
                        observer.OnNext(new ManualWeighingEvent
                        {
                            Prompt = (index < bursts.Count ? string.Format("Firing {0}: wait.", bursts[index].Name) : "Every weight taken: writing the calibration.") + " " + note,
                            Reading = reading
                        });
                    }
                }, observer.OnError));
                return subscriptions;
            });
        }
    }

    /// <summary>
    /// The weights a manual balance took, as the readings a serial balance would publish.
    /// </summary>
    [Combinator]
    [Description("The weights a manual balance took, as stable WeightMeasurement readings.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ManualReadings
    {
        /// <summary>Passes each event's reading, when it has one.</summary>
        public IObservable<WeightMeasurement> Process(IObservable<ManualWeighingEvent> source)
        {
            return source.Where(e => e.Reading.HasValue).Select(e => e.Reading.Value);
        }
    }

    /// <summary>
    /// One line summing up a calibration curve, such as "10 ms 0.62 ul, 20 ms 1.19 ul".
    /// </summary>
    [Combinator]
    [Description("One line summing up a calibration curve's points, such as '10 ms 0.62 ul, 20 ms 1.19 ul'.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class DescribeCalibrationCurve
    {
        /// <summary>Sums up each curve.</summary>
        public IObservable<string> Process(IObservable<CalibrationCurve> source)
        {
            return source.Select(Describe);
        }

        internal static string Describe(CalibrationCurve curve)
        {
            return string.Join(", ", curve.Points.Select(p => string.Format(
                CultureInfo.InvariantCulture, "{0:0.##} {1} {2:0.000} {3}", p.X, curve.XUnit, p.Y, curve.YUnit)).ToArray());
        }
    }
}
