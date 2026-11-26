using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Serialization;
using Bonsai;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// Runs Zapit on the rig's NI DAQ (or its simulated twin). Trials: a loaded bank's cycles and ramp tail concatenated
    /// into one finite AO task, armed on the trigger line, at most <see cref="MaxTrialSeconds"/> long and ending with laser
    /// and LED at 0 V. A trial whose end is fixed at Arm (a duration, not UntilStopped) is written whole before its trigger.
    /// A trial an EarlyStop may end (an Arm with UntilStopped, or no duration) is streamed: its task does not regenerate,
    /// the first samples are written before the start and a feeder thread (priority Highest, the Windows timer at 1 ms, the
    /// only thread that calls the DAQ while the trial streams) keeps the lead less its longest write and the bank's longest
    /// scan cycle written ahead (6292 samples for B2's bank). Every write ends on a laser sample at 0 V (the galvos'
    /// blanking), so a DAQ that runs out of samples holds the laser off; a bank with a laser cycle without one cannot stream.
    /// An EarlyStop writes nothing: the feeder streams the ramp from the first scan-cycle boundary at or after the samples
    /// written, so it starts within <see cref="EarlyStopLeadSeconds"/> of the EarlyStop, in phase with the scan, and writes
    /// nothing after the ramp's last sample (laser and LED at 0 V), where the DAQ runs out and stops itself (about 10 ms; NI's
    /// Stop of a running task takes about 110 ms on B2's USB-6453). A streamed trial whose DAQ runs out of samples (or fails)
    /// before its ramp's end has every line zeroed at once and is logged as Underflowed. An underflow leaves the session
    /// running, up to the <see cref="UnderflowLimit"/>th, which is an error; any other DAQ failure is an error at once. An
    /// EarlyStop to a trial written whole changes nothing once it has started (its own ramp ends it). Between trials the clocked task is stopped
    /// and deleted and every line zeroed on a short-lived on-demand task.
    /// Calibration: on-demand points, and a laser staircase read back through the photodiode on the AO clock. Every line is
    /// zeroed on subscribe and on unsubscribe, however the workflow stops. A Point once trials are armed, and a bank over the
    /// ceiling, are Refused (an event; the session runs on); any other refusal or DAQ error is an error. No laser sample above
    /// <see cref="LaserCeilingVolts"/> is written except by the staircase (the calibration that measures the laser curve the
    /// ceiling comes from), and a bank holding one, built under a ceiling above <see cref="CeilingMw"/> (peak), or delivering
    /// more average power than it asked (average), is refused at LoadBank. The simulated twin starts a trial on a Trigger
    /// command, as the NI DAQ on its line. A trial that has played is noticed at the next Arm as well as by polling. A bank's
    /// first streamed trial is refused, the lines zeroed, before its task starts, unless the samples it keeps written ahead
    /// cover the device's onboard buffer and 20 ms more (B2: 6292 over 4095 + 2000), so a lead too short fails at once, not
    /// at a random underflow.
    /// </summary>
    [Description("Runs Zapit trials and calibration steps on the rig's NI DAQ, or its simulated twin.")]
    public class ZapitDaq : Combinator<ZapitDaqCommand, ZapitDaqEvent>
    {
        /// <summary>Whether to run the simulated twin instead of the NI DAQ.</summary>
        [Description("Whether to run the simulated twin instead of the NI DAQ.")]
        public bool Simulated { get; set; }

        /// <summary>Galvo X's AO channel, such as Dev1/ao0.</summary>
        [Description("Galvo X's AO channel, such as Dev1/ao0.")]
        public string GalvoXChannel { get; set; } = "";

        /// <summary>Galvo Y's AO channel.</summary>
        [Description("Galvo Y's AO channel.")]
        public string GalvoYChannel { get; set; } = "";

        /// <summary>The laser's AO channel.</summary>
        [Description("The laser's AO channel.")]
        public string LaserChannel { get; set; } = "";

        /// <summary>The masking LED's AO channel.</summary>
        [Description("The masking LED's AO channel.")]
        public string LedChannel { get; set; } = "";

        /// <summary>The photodiode's AI channel, read by the laser staircase.</summary>
        [Description("The photodiode's AI channel, read by the laser staircase.")]
        public string PhotodiodeChannel { get; set; } = "";

        /// <summary>How the photodiode is wired.</summary>
        [Description("How the photodiode is wired.")]
        public ZapitInputTerminal PhotodiodeTerminalConfiguration { get; set; }

        /// <summary>The trigger line trials start on, such as /Dev1/PFI0.</summary>
        [Description("The trigger line trials start on, such as /Dev1/PFI0.")]
        public string TriggerTerminal { get; set; } = "";

        /// <summary>The AO sample rate, in samples per second; a bank built for another is refused.</summary>
        [Description("The AO sample rate, in samples per second; a bank built for another is refused.")]
        public double SampleRate { get; set; }

        /// <summary>The rig's laser ceiling, a peak power in mW (rig.yml's zapit.ceilingMw); a bank built under a higher one is refused. At 0, the default, the laser is disabled and ZapitDaq refuses any laser sample above 0 V, so a lost mapping fails safe.</summary>
        [Description("The rig's laser ceiling, a peak power in mW; a bank built under a higher one is refused. At 0 (the default) the laser is disabled and ZapitDaq refuses any laser sample above 0 V.")]
        public double CeilingMw { get; set; }

        /// <summary>The highest laser control volts (rig.yml's zapit.laserMinMaxControlVolts[1]). ZapitDaq refuses any laser sample above it or below 0 V. The default, 0, refuses any laser sample above 0 V.</summary>
        [Description("The highest laser control volts (rig.yml's laserMinMaxControlVolts[1]). Any laser sample above it or below 0 V is refused.")]
        public double LaserMaxVolts { get; set; }

        /// <summary>The laser ceiling as control volts: rig.yml's zapit.ceilingMw through the rig's laser curve (lib_zapit's
        /// max_control_volts, passed at launch). ZapitDaq refuses any laser sample above it, and any bank holding one. The
        /// default, 0, refuses any laser sample above 0 V, so a lost mapping fails safe.</summary>
        [Description("The laser ceiling as control volts (ceilingMw through the laser curve, from the launch). Any laser sample above it is refused; 0, the default, refuses any above 0 V.")]
        public double LaserCeilingVolts { get; set; }

        /// <summary>The longest trial with its ramp, in seconds; until-outcome trials are finite at it.</summary>
        [Description("The longest trial with its ramp, in seconds; until-outcome trials are finite at it.")]
        public double MaxTrialSeconds { get; set; }

        /// <summary>The most an early stop's ramp starts after the EarlyStop, in seconds (B2: 63 to 90 ms measured). A streamed
        /// trial keeps this less its longest write (to a laser sample at 0 V: 1208 samples in B2's bank) and the bank's longest
        /// scan cycle written ahead of the generation, which must cover the device's onboard buffer and 20 ms more: at 100000
        /// samples/s with 2500-sample cycles, 6292 samples ahead of a USB-6453's 4095 (B2's), checked at a bank's first
        /// streamed trial. A lab constant, not a rig.yml setting; TrialRules' hold and laser-cap limits read it too.</summary>
        public const double EarlyStopLeadSeconds = 0.1;

        /// <summary>The minimum pulse width of the NI digital filter on the trigger line, in seconds, always applied: an EMI
        /// spike (about 10 ns on B2's PFI0) does not start a trial. The trigger is a level held about 1 s; the filter delays the
        /// recognised edge by about its width. A lab constant, not a rig.yml setting.</summary>
        public const double TriggerFilterSeconds = 1e-4;

        /// <summary>The underflows that stop a session: a streamed trial whose DAQ runs out of samples before its ramp's end
        /// has every line zeroed (the laser ending without its ramp, from a laser sample at 0 V) and is logged as Underflowed,
        /// and the session runs on, until the session's underflows reach this, which is an error. A lab constant, not a
        /// rig.yml setting.</summary>
        public const int UnderflowLimit = 3;

        /// <summary>Tests replace the device; null takes <see cref="Simulated"/>.</summary>
        [XmlIgnore, Browsable(false)]
        internal Func<IZapitDevice>? DeviceFactory { get; set; }

        static readonly double[] Zeros = new double[4];

        // The bank stores float32 volts from lib_zapit's float maths: some rows come out a few microvolts below 0 (-2e-6 V
        // in the contract bank), and a maximum such as 4.9 V reads back 1e-7 V above it. The laser limits allow that rounding and no more.
        internal const double FloatSlackVolts = 1e-4;   // ZapitSetupCapture's too
        const double FloatSlackMw = 1e-3;   // the bank's powers come from the same float maths

        /// <summary>Runs each command in order and reports what the DAQ did.</summary>
        public override IObservable<ZapitDaqEvent> Process(IObservable<ZapitDaqCommand> source) => Observable.Create<ZapitDaqEvent>(observer =>
        {
            if (!(SampleRate > 0) || !(MaxTrialSeconds > 0))
                throw new InvalidOperationException("ZapitDaq needs SampleRate and MaxTrialSeconds above 0 (rig.yml's zapit block).");
            var wiring = new ZapitWiring
            {
                AnalogOutputs = new[] { GalvoXChannel, GalvoYChannel, LaserChannel, LedChannel },
                Photodiode = PhotodiodeChannel,
                PhotodiodeTerminalConfiguration = PhotodiodeTerminalConfiguration,
            };
            if (DeviceFactory == null && !Simulated && (wiring.AnalogOutputs.Any(string.IsNullOrEmpty) || string.IsNullOrEmpty(PhotodiodeChannel)))
                throw new InvalidOperationException("ZapitDaq needs its four AO channels and the photodiode channel (rig.yml's zapit block).");
            // The twin starts on the Trigger command the workflow sends with the line, so a desk run triggers as late as a rig.
            var device = DeviceFactory?.Invoke() ?? (Simulated ? new SimulatedZapitDevice { AutoTrigger = false } : CreateNi(wiring));
            Session session;
            try { session = new Session(this, device, observer); }
            catch
            {
                device.Dispose();
                throw;
            }
            var commands = source.Subscribe(session.Handle, session.Fail, session.Complete);
            return new CompositeDisposable(commands, session);
        });

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IZapitDevice CreateNi(ZapitWiring wiring) => new NiZapitDevice(wiring);

        /// <summary>A refusal by the laser ceiling (ceilingMw or LaserCeilingVolts): at LoadBank a Refused event, elsewhere an error.</summary>
        sealed class CeilingRefusal : InvalidOperationException
        {
            public CeilingRefusal(string message) : base(message) { }
        }

        /// <summary>The shortest write of a streamed trial, in seconds of samples: NI's writes are slow calls (up to 11 ms on
        /// B2's USB-6453). Internal and settable only so a hardware check can stream a laser-off bank in a real bank's writes.</summary>
        internal static double StreamMinWriteSeconds = 0.005;

        /// <summary>How far past the onboard FIFO a streamed trial must keep written ahead, in seconds: the feeder's slack.</summary>
        internal const double StreamMarginSeconds = 0.02;

        static long SamplesIn(double seconds, double sampleRate) => (long)Math.Floor(seconds * sampleRate + 1e-6);

        /// <summary>How a bank's trials stream. Every write ends on a laser sample at 0 V (at most <see cref="FloatSlackVolts"/>,
        /// the galvos' blanking), so a DAQ that runs out of samples holds the laser off: the longest write is the longest
        /// stretch of any trial to such a sample (cycles, a cycle joining the ramp tail, at least
        /// <see cref="StreamMinWriteSeconds"/>). A streamed trial keeps the lead less the longest write and the longest scan
        /// cycle written ahead, so an early stop's ramp, which starts on the first cycle boundary at or after the samples
        /// written, starts within the lead. Problem says why the bank's trials cannot stream.</summary>
        internal sealed class StreamPlan
        {
            public long Lead, MaxWrite, LongestCycle, Target;
            public string? Problem;
        }

        internal static StreamPlan PlanStream(ZapitBank bank)
        {
            var plan = new StreamPlan
            {
                Lead = SamplesIn(EarlyStopLeadSeconds, bank.SampleRate),
                MaxWrite = Math.Max(1, SamplesIn(StreamMinWriteSeconds, bank.SampleRate)),
                LongestCycle = bank.Conditions.Max(c => c.ScanCycle.Length),
            };
            foreach (var c in bank.Conditions)
                foreach (var v in c.Variants)
                {
                    var s = bank.Assemble(c.Condition, v, 2);   // two cycles then the ramp tail: every join a trial, cut short or not, makes
                    bool dark = false;
                    long last = -1;   // a write starts after a laser sample at 0 V
                    for (int i = 0; i < s.GetLength(1); i++)
                    {
                        if (!(s[2, i] <= FloatSlackVolts)) continue;
                        dark |= i < c.ScanCycle.Length;
                        plan.MaxWrite = Math.Max(plan.MaxWrite, i - last);
                        last = i;
                    }
                    if (!dark && plan.Problem == null)
                        plan.Problem = $"The Zapit bank's condition {c.Condition} {(v.LaserOn ? $"at {v.PowerMw} mW" : "with the laser off")} has no laser sample at 0 V in its scan cycle";
                }
            plan.Target = plan.Lead - plan.MaxWrite - plan.LongestCycle;
            return plan;
        }

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint milliseconds);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint milliseconds);

        sealed class Trial
        {
            public ZapitDaqCommand Command = null!;
            public ZapitBankVariant Variant = null!;
            public int Cycles;
            public long Samples;
            public long StopAt;
            public double Seconds;
            public bool Started;
            public bool Stopping;
            public Feed? Feed;   // a streamed trial's; null for one written whole
        }

        // A streamed trial's samples, under the device gate: the trial as armed (its cycles, then its ramp tail), or, cut by
        // an early stop at RampFrom, its cycles to there and the same ramp tail, which ends with laser and LED at 0 V. The
        // feeder writes nothing past End: a trial cut short runs out of samples there and its DAQ stops itself, holding that
        // last sample, in about 10 ms; NI's Stop of a task still running takes about 110 ms on B2's USB-6453. Every write
        // ends on a laser sample at 0 V, which a DAQ that runs out of samples holds.
        sealed class Feed
        {
            public double[,] Samples = null!;
            public long Body;   // the armed trial's first ramp-tail row
            public int TailRows;
            public long Target;
            public long MaxWrite;
            public long Written;
            public long Generated;   // the last count read
            public long ReadAt = Stopwatch.GetTimestamp();   // when, as Stopwatch ticks
            public long MinAhead = long.MaxValue;
            public long RampFrom = -1;
            public long EarlyStopAt = -1;
            public Exception? Failure;

            public long Total => Samples.GetLength(1);

            /// <summary>The samples to write: the task's, or an early stop's ramp end.</summary>
            public long End => RampFrom < 0 ? Total : RampFrom + TailRows;

            long Row(long sample) => RampFrom < 0 || sample < RampFrom ? sample : Body + sample - RampFrom;

            /// <summary>The next write: from the samples written to the last laser sample at 0 V before <paramref name="limit"/>
            /// (exclusive), or nothing when no such sample is there.</summary>
            public double[,]? Next(long limit)
            {
                limit = Math.Min(limit, End);
                long last = limit - 1;
                while (last >= Written && !(Samples[2, Row(last)] <= FloatSlackVolts)) last--;
                if (last < Written) return null;
                var next = new double[4, last - Written + 1];
                for (int i = 0; i < next.GetLength(1); i++)
                {
                    var row = Row(Written + i);
                    for (int channel = 0; channel < 4; channel++) next[channel, i] = Samples[channel, row];
                }
                return next;
            }
        }

        sealed class Session : IDisposable
        {
            readonly ZapitDaq node;
            readonly IZapitDevice device;
            readonly IObserver<ZapitDaqEvent> observer;   // called only under the gate, so the commands' and the monitor's calls never race
            readonly object gate = new object();
            // Every device call is made under the device gate, taken after the gate and never before it, and no event is
            // emitted under it: the feeder takes only the device gate, so a slow subscriber holding the gate never starves it.
            readonly object deviceGate = new object();
            readonly Thread monitor;
            readonly Thread feeder;
            readonly ManualResetEventSlim feeding = new ManualResetEventSlim(false);
            volatile bool disposed;
            ZapitBank? bank;
            Trial? trial;
            Feed? feed;   // under the device gate: the streamed trial the feeder tops up
            bool trialsArmed;   // a trial was armed: Point is refused for the rest of the session
            ZapitBank? planned;   // the bank plan is for
            StreamPlan? plan;
            bool leadChecked;   // plan passed CheckLead on this DAQ
            bool fineTimer;   // timeBeginPeriod(1) held while a streamed trial is armed
            int underflows;   // the session's so far: UnderflowLimit stops it

            public Session(ZapitDaq node, IZapitDevice device, IObserver<ZapitDaqEvent> observer)
            {
                this.node = node;
                this.device = device;
                this.observer = observer;
                device.WriteOnDemand(Zeros);
                monitor = new Thread(Monitor) { IsBackground = true, Name = "ZapitDaq" };
                feeder = new Thread(Feeder) { IsBackground = true, Name = "ZapitDaq feeder", Priority = ThreadPriority.Highest };
                monitor.Start();
                feeder.Start();
            }

            public void Handle(ZapitDaqCommand command)
            {
                lock (gate)
                {
                    if (disposed) return;
                    try
                    {
                        switch (command.Kind)
                        {
                            case ZapitDaqCommandKind.LoadBank: LoadBank(command); break;
                            case ZapitDaqCommandKind.Arm: Arm(command); break;
                            case ZapitDaqCommandKind.EarlyStop: EarlyStop(command); break;
                            case ZapitDaqCommandKind.Zero: Finish(ZapitDaqEventKind.Zeroed); break;
                            case ZapitDaqCommandKind.Point: Point(command); break;
                            case ZapitDaqCommandKind.Staircase: Staircase(command); break;
                            case ZapitDaqCommandKind.Trigger: (device as SimulatedZapitDevice)?.Trigger(); break;
                            default: throw new InvalidOperationException($"ZapitDaq has no command {command.Kind}.");
                        }
                    }
                    catch (Exception error)
                    {
                        if (!disposed) observer.OnError(error);
                    }
                }
            }

            public void Fail(Exception error)
            {
                lock (gate) observer.OnError(error);
            }

            public void Complete()
            {
                lock (gate) observer.OnCompleted();
            }

            void LoadBank(ZapitDaqCommand command)
            {
                RefuseWhilePlaying(command);
                var loaded = ZapitBank.Load(command.Path ?? throw new InvalidOperationException("LoadBank needs the bank's Path."));
                if (loaded.SampleRate != node.SampleRate)
                    throw new InvalidOperationException($"The Zapit bank was built for {loaded.SampleRate} samples/s; the rig runs {node.SampleRate}.");
                loaded.Verify(command.ConditionsPath, command.ScannerFitHash, command.LaserCurveHash, command.SampleHash);
                // Over the ceiling (a stale or missing laser curve passes LaserCeilingVolts 0): Refused, the session runs on and
                // shows why, and any bank loaded before stays.
                try
                {
                    if (!(loaded.CeilingMw <= node.CeilingMw))
                        throw new CeilingRefusal($"The Zapit bank was built under a {loaded.CeilingMw} mW ceiling, above rig.yml's zapit.ceilingMw ({node.CeilingMw} mW): rebuild the bank.");
                    // Like with like: ceiling_mw and ceilingMw are peak powers (above); a variant's powers are time-averaged per
                    // point, so its delivered average is held to its asked average. The bank's ceiling_mw is its own claim: its
                    // samples' peak is held to the rig's ceiling as volts (LaserCeilingVolts, ceilingMw through the laser curve).
                    var raised = loaded.Conditions.SelectMany(c => c.Variants).FirstOrDefault(v => v.LaserOn && !(v.CappedPowerMw <= v.PowerMw + FloatSlackMw));
                    if (raised != null)
                        throw new CeilingRefusal($"The Zapit bank delivers {raised.CappedPowerMw} mW on average where {raised.PowerMw} mW was asked: rebuild the bank.");
                    RefuseLaser(new[] { loaded.MinLaserVolts, loaded.MaxLaserVolts }, "The Zapit bank's samples");
                }
                catch (CeilingRefusal refusal)
                {
                    Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Refused, Detail = refusal.Message + " " + command.Path });
                    return;
                }
                bank = loaded;
                Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.BankLoaded, Bank = loaded, Detail = command.Path });
            }

            // A streamed trial keeps the plan's target written ahead (the lead less the longest write and the longest scan cycle,
            // so an early stop's ramp starts within the lead). It must cover the onboard FIFO, which NI fills from it, with
            // StreamMarginSeconds to spare, or the generation would catch up with the feeder; and every write must be able to
            // end on a laser sample at 0 V. Fixed per DAQ and bank, so checked at a bank's first streamed trial, the task
            // created (NI knows its onboard buffer only then), before its start.
            void CheckLead(ZapitBank loaded, StreamPlan plan)
            {
                if (plan.Problem != null)
                    throw new InvalidOperationException(plan.Problem + ", so a streamed trial's DAQ could hold the laser on should it run out of samples: ZapitDaq writes a streamed trial " +
                        "(one an early stop may end) only in pieces ending on a laser sample at 0 V, the galvos' blanking. Trials with a fixed duration still run; rebuild the bank with blanking in every laser cycle to stream.");
                var rate = loaded.SampleRate;
                long onboard = device.OnboardSamples, margin = SamplesIn(StreamMarginSeconds, rate), minimum = onboard + margin;
                if (plan.Target >= minimum) return;
                var c = System.Globalization.CultureInfo.InvariantCulture;
                throw new InvalidOperationException(string.Format(c,
                    "A streamed Zapit trial keeps {0} samples written ahead of the DAQ: the early-stop lead ({1} samples, lib-open-rigs' ZapitDaq.EarlyStopLeadSeconds, {2} s at {3} samples/s) " +
                    "less its longest write ({4} samples, to a laser sample at 0 V) and the bank's longest scan cycle ({5} samples). That is under the DAQ's onboard buffer ({6} samples per channel) plus a {7}-sample margin ({8} samples), " +
                    "so the DAQ could run out of samples mid-trial: ZapitDaq.EarlyStopLeadSeconds must be raised to at least {9} s before trials run on this DAQ.",
                    plan.Target, plan.Lead, EarlyStopLeadSeconds, rate, plan.MaxWrite, plan.LongestCycle, onboard, margin, minimum, (minimum + plan.MaxWrite + plan.LongestCycle) / rate));
            }

            void Arm(ZapitDaqCommand command)
            {
                var loaded = bank ?? throw new InvalidOperationException($"Trial {command.Trial} cannot arm: no Zapit bank is loaded.");
                if (trial != null) Poll(trial);   // it may have started or ended since the monitor's last look
                if (disposed) return;
                if (trial != null && trial.Started)
                    throw new InvalidOperationException($"Trial {command.Trial} cannot arm while trial {trial.Command.Trial} still plays.");
                if (trial != null) Finish(ZapitDaqEventKind.Stopped);   // armed but never triggered
                if (disposed) return;   // a subscriber disposed the node inside that event: the lines are zeroed
                trialsArmed = true;
                var watch = Stopwatch.StartNew();
                var variant = loaded.Variant(command.Condition, command.LaserOn, command.PowerMw);
                var cycles = loaded.Cycles(command.Condition, variant, command.DurationSeconds, node.MaxTrialSeconds);
                var samples = loaded.Assemble(command.Condition, variant, cycles);
                RefuseLaser(Laser(samples), $"Trial {command.Trial}'s bank samples");   // every sample written, streamed or not, comes from these
                // A trial an EarlyStop may end (until stopped, or no duration) is streamed; one whose end is fixed at Arm is
                // written whole, regeneration allowed, as it ran on B2.
                Feed? streamed = null;
                if (command.UntilStopped || command.DurationSeconds == 0)
                {
                    if (planned != loaded)
                    {
                        plan = PlanStream(loaded);
                        planned = loaded;
                        leadChecked = false;
                    }
                    if (plan!.Problem != null) CheckLead(loaded, plan);   // refused before any task exists
                    streamed = new Feed { Samples = samples, Body = loaded.BodyRows(command.Condition, variant, cycles), TailRows = variant.TailRows, Target = plan.Target, MaxWrite = plan.MaxWrite };
                }
                lock (deviceGate)
                {
                    device.WriteOnDemand(Zeros);
                    if (streamed == null) device.ArmFinite(samples, loaded.SampleRate, node.TriggerTerminal);
                    else
                    {
                        try
                        {
                            device.CreateFinite(samples.GetLength(1), loaded.SampleRate, node.TriggerTerminal);
                            // The first window before the start, where a write never waits; the feeder writes the rest.
                            var first = streamed.Next(streamed.Target) ?? throw new InvalidOperationException($"Trial {command.Trial} has no laser sample at 0 V in its first {streamed.Target} samples to end its first write on.");
                            device.Write(first);
                            streamed.Written = first.GetLength(1);
                            if (!leadChecked) CheckLead(loaded, plan!);
                            leadChecked = true;
                            device.StartClocked();
                        }
                        catch
                        {
                            device.WriteOnDemand(Zeros);   // deletes the task: the lines at 0 V
                            throw;
                        }
                        feed = streamed;
                    }
                }
                if (streamed != null)
                {
                    FineTimer(true);
                    feeding.Set();
                }
                trial = new Trial { Command = command, Variant = variant, Cycles = cycles, Samples = samples.GetLength(1), StopAt = samples.GetLength(1), Seconds = samples.GetLength(1) / loaded.SampleRate, Feed = streamed };
                Emit(TrialEvent(ZapitDaqEventKind.Armed, 0, watch.Elapsed.TotalSeconds));
            }

            // A streamed trial's early stop writes nothing: the feeder streams its ramp from the first scan-cycle boundary at
            // or after the samples written (so the laser stays in phase with the scan), less than the lead past the samples
            // generated. A trial written whole is not changed: its own ramp ends it where it was armed to (NI's write into a
            // running buffer waits until those samples are generated, too late, holding the gate).
            void EarlyStop(ZapitDaqCommand command)
            {
                if (trial == null || trial.Command.Trial != command.Trial || trial.Stopping) return;   // already over
                Poll(trial);   // started (so it ramps) or ended since the monitor's last look
                if (disposed || trial == null) return;
                if (!trial.Started)
                {
                    Finish(ZapitDaqEventKind.Stopped);
                    return;
                }
                var f = trial.Feed;
                if (f == null) return;
                var loaded = bank!;
                var condition = trial.Command.Condition;
                int cycleRows = loaded.Conditions[condition - 1].ScanCycle.Length;
                lock (deviceGate)
                {
                    if (f.Failure != null) return;   // the next poll reports it
                    f.EarlyStopAt = f.Generated;   // read by the Poll above
                    trial.Stopping = true;
                    // The ramp starts on a cycle boundary, so the laser stays in phase with the scan: the trial cut to fewer cycles.
                    var cycles = (int)((f.Written + trial.Variant.TailReplaces + cycleRows - 1) / cycleRows);
                    if (cycles >= trial.Cycles) return;   // its own ramp comes first
                    f.RampFrom = loaded.BodyRows(condition, trial.Variant, cycles);
                    trial.StopAt = f.RampFrom + trial.Variant.TailRows;
                }
            }

            void Point(ZapitDaqCommand command)
            {
                if (trialsArmed)
                {
                    Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Refused, Detail = "ZapitDaq takes no Point once trials are armed (after Go)." });
                    return;
                }
                RefuseWhilePlaying(command);
                RefuseLaser(new[] { command.LaserVolts }, "The aiming point");
                lock (deviceGate) device.WriteOnDemand(new[] { command.GalvoXVolts, command.GalvoYVolts, command.LaserVolts, 0 });
                Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Pointed });
            }

            void Staircase(ZapitDaqCommand command)
            {
                // Between trials too: the staircase is not held to the ceiling's volts (below), so it is calibration only.
                if (trialsArmed)
                {
                    Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Refused, Detail = "ZapitDaq takes no Staircase once trials are armed (after Go)." });
                    return;
                }
                RefuseWhilePlaying(command);
                var levels = command.LaserLevelsVolts;
                if (levels == null || levels.Length == 0 || command.SamplesPerLevel < 1)
                    throw new InvalidOperationException("A Zapit staircase needs laser levels and at least one sample per level.");
                // The staircase measures the laser curve LaserCeilingVolts comes from, so it covers the laser's range: every
                // other limit holds (ceilingMw 0 disables it, and 0 to LaserMaxVolts).
                RefuseLaser(levels, "The staircase", ceilingVolts: false);
                var samples = new double[4, levels.Length * command.SamplesPerLevel + 1];   // the last sample: laser at 0 V
                for (int i = 0; i < samples.GetLength(1); i++)
                {
                    samples[0, i] = command.GalvoXVolts;
                    samples[1, i] = command.GalvoYVolts;
                    if (i < samples.GetLength(1) - 1) samples[2, i] = levels[i / command.SamplesPerLevel];
                }
                double[] read;
                lock (deviceGate)
                {
                    device.WriteOnDemand(Zeros);
                    read = device.Staircase(samples, node.SampleRate);
                    device.WriteOnDemand(Zeros);
                }
                Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.StaircaseRead, Samples = samples.GetLength(1), AnalogIn = read });
            }

            void RefuseLaser(double[] volts, string what, bool ceilingVolts = true)
            {
                double max = volts.Max(), min = volts.Min();
                if (node.CeilingMw <= 0 && max > 0)
                    throw new CeilingRefusal($"{what} drive the laser to {max} V, but rig.yml's zapit.ceilingMw is 0 (laser disabled).");
                if (!(max <= node.LaserMaxVolts + FloatSlackVolts) || !(min >= -FloatSlackVolts))
                    throw new InvalidOperationException($"{what} drive the laser from {min} to {max} V, outside 0 to {node.LaserMaxVolts} V (rig.yml's zapit.laserMinMaxControlVolts[1]).");
                if (ceilingVolts && !(max <= node.LaserCeilingVolts + FloatSlackVolts))
                    throw new CeilingRefusal($"{what} drive the laser to {max} V, above the ceiling's {node.LaserCeilingVolts} V (LaserCeilingVolts: rig.yml's zapit.ceilingMw through the laser curve).");
            }

            static double[] Laser(double[,] samples) => Enumerable.Range(0, samples.GetLength(1)).Select(i => samples[2, i]).ToArray();

            void RefuseWhilePlaying(ZapitDaqCommand command)
            {
                if (trial != null) throw new InvalidOperationException($"ZapitDaq cannot {command.Kind} while trial {trial.Command.Trial} is armed.");
            }

            // Stops and deletes any clocked task and zeroes every line, the zero written even when the stop reports an error;
            // the trial is over. Returns the samples generated (a failed stream's last count) and the stop's error, if any.
            long Stop(out Exception? stopError)
            {
                long generated = 0;
                stopError = null;
                var f = trial?.Feed;
                lock (deviceGate)
                {
                    feed = null;
                    feeding.Reset();
                    if (f != null)
                    {
                        generated = f.Generated;
                        if (f.Failure == null)
                        {
                            try { generated = Math.Max(generated, device.SamplesGenerated); }
                            catch (Exception) { }   // a stream run out of samples: its count stands, and the stop reports the error
                        }
                    }
                    else
                    {
                        try { generated = device.SamplesGenerated; }
                        catch (Exception error) { stopError = error; }
                    }
                    try { device.StopClocked(); }
                    catch (Exception error) { stopError ??= error; }
                    finally { device.WriteOnDemand(Zeros); }
                }
                trial = null;
                FineTimer(false);
                return generated;
            }

            void Finish(ZapitDaqEventKind kind)
            {
                var ended = trial;
                var generated = Stop(out var stopError);
                // A streamed trial past its ramp's end is over whatever the DAQ reports (one cut short has run out of samples
                // there, as meant): it held its last sample, laser and LED at 0 V.
                if (stopError != null && !(ended?.Feed != null && generated >= ended.StopAt)) throw stopError;
                Emit(ended == null ? new ZapitDaqEvent { Kind = kind } : TrialEvent(ended, kind, generated, 0));
            }

            ZapitDaqEvent TrialEvent(ZapitDaqEventKind kind, long generated, double armSeconds) => TrialEvent(trial!, kind, generated, armSeconds);

            static ZapitDaqEvent TrialEvent(Trial t, ZapitDaqEventKind kind, long generated, double armSeconds) => new ZapitDaqEvent
            {
                Kind = kind,
                Trial = t.Command.Trial,
                Condition = t.Command.Condition,
                LaserOn = t.Command.LaserOn,
                PowerMw = t.Variant.PowerMw,
                CappedPowerMw = t.Variant.CappedPowerMw,
                PowerCapped = t.Variant.PowerCapped,
                Samples = t.Samples,
                TrialSeconds = t.Seconds,
                SamplesGenerated = generated,
                ArmSeconds = armSeconds,
                Streamed = t.Feed != null,
                MinSamplesAhead = t.Feed == null || t.Feed.MinAhead == long.MaxValue ? -1 : t.Feed.MinAhead,
                EarlyStopAt = t.Feed?.RampFrom >= 0 ? t.Feed.EarlyStopAt : -1,
                RampFrom = t.Feed?.RampFrom ?? -1,
            };

            void Emit(ZapitDaqEvent e)
            {
                e.Time = DateTimeOffset.Now;
                e.Underflows = underflows;
                observer.OnNext(e);
            }

            // fresh: read the DAQ's count of a streamed trial (Arm, EarlyStop) rather than the feeder's last; the monitor takes the
            // feeder's, so the feeder is not kept waiting on the DAQ's slow calls (IsDone up to 16 ms, the count up to 4 ms on
            // B2's USB-6453). A streamed trial is over once its samples to write are all generated.
            void Poll(Trial current, bool fresh = true)
            {
                long generated;
                bool done;
                var f = current.Feed;
                lock (deviceGate)
                {
                    if (f == null)
                    {
                        generated = device.SamplesGenerated;
                        done = device.IsDone;
                    }
                    else
                    {
                        // The feeder reads the count every loop: one older than 10 ms means it is held up, and the monitor reads it.
                        if ((fresh || Stopwatch.GetTimestamp() - f.ReadAt > Stopwatch.Frequency / 100) && f.Failure == null && feed == f)
                        {
                            try
                            {
                                var count = device.SamplesGenerated;
                                f.ReadAt = Stopwatch.GetTimestamp();
                                f.Generated = Math.Max(f.Generated, count);
                                // Once they run out NI's count reads on past the samples written (about 52 more on B2's USB-6453), the read not failing.
                                if (f.Written < f.End && count >= f.Written)
                                    throw new InvalidOperationException($"The DAQ had generated {count} of the {f.Written} samples written: it ran out of samples (NI's -200290, the generation stopped to prevent the regeneration of old samples).");
                            }
                            catch (Exception error) { StreamFailed(f, error); }
                        }
                        generated = f.Generated;
                        done = f.Failure != null || generated >= f.End;
                    }
                }
                if (!current.Started && generated > 0)
                {
                    current.Started = true;
                    Emit(TrialEvent(ZapitDaqEventKind.Started, generated, 0));
                    if (disposed) return;
                }
                if (trial != current) return;
                if (f?.Failure != null && generated < current.StopAt)
                {
                    Stop(out _);   // zeroed already, by StreamFailed
                    // Run out of samples: the session runs on, up to UnderflowLimit. Any other failure (a write or read that
                    // failed, or lines that could not be zeroed) stops it at once.
                    var underflow = Underflow(f.Failure);
                    if (underflow) underflows++;
                    var stops = !underflow || underflows >= UnderflowLimit;
                    var c = System.Globalization.CultureInfo.InvariantCulture;
                    var message = string.Format(c,
                        "ZapitDaq's trial {0} failed at sample {1} of the {2} before its ramp's end (samples written: {3}, the last with the laser at 0 V), so every line was zeroed at once, the laser ending without its ramp, ",
                        current.Command.Trial, generated, current.StopAt, f.Written) +
                        (!underflow ? "and the workflow stopped: "
                        : stops ? string.Format(c, "and the workflow stopped at the session's underflow {0} (ZapitDaq.UnderflowLimit): ", underflows)
                        : string.Format(c, "and the session runs on (underflow {0} of the {1} that stop it): ", underflows, UnderflowLimit)) + f.Failure.Message;
                    var underflowed = TrialEvent(current, ZapitDaqEventKind.Underflowed, generated, 0);
                    underflowed.Detail = message;
                    Emit(underflowed);   // the trial's own event, logged before any error stops the workflow
                    if (stops) throw new InvalidOperationException(message, f.Failure);
                    return;
                }
                if (done || generated >= current.StopAt)
                    Finish(current.Stopping ? ZapitDaqEventKind.Stopped : ZapitDaqEventKind.Ended);
            }

            // Under the device gate: a streamed trial's DAQ ran out of samples (NI: "to prevent the regeneration of old
            // samples"), or a write or read failed. The card holds its last sample, the laser maybe on: zero every line at once,
            // here, not at the monitor's next look; the monitor then reports it (Poll).
            // NI's -200290 ("the generation has stopped to prevent the regeneration of old samples"), and the twin's.
            static bool Underflow(Exception error) => error.Message.IndexOf("regeneration of old samples", StringComparison.OrdinalIgnoreCase) >= 0;

            void StreamFailed(Feed f, Exception error)
            {
                f.Failure = error;
                feeding.Reset();
                try { f.Generated = Math.Max(f.Generated, device.SamplesGenerated); }
                catch (Exception) { }   // the last count read stands
                // Run out of samples: the generation stopped at the samples written, so with all of them written (a trial cut
                // short, at its ramp's end) the trial played to its end.
                if (Underflow(error)) f.Generated = Math.Max(f.Generated, f.Written);
                if (f.Written < f.End && f.Generated >= f.Written) f.MinAhead = 0;   // ran dry before its end
                try { device.StopClocked(); }
                catch (Exception) { }   // NI reports the failed generation here too
                finally
                {
                    try { device.WriteOnDemand(Zeros); }
                    catch (Exception zero) { f.Failure = new AggregateException("ZapitDaq could not zero the lines after a failed trial.", error, zero); }
                }
            }

            // Tops up the streamed trial to its target written ahead, in writes of a chunk, so none waits on NI and the device
            // gate is never held long. With all its samples written there is nothing to write, so the feeder asks NI whether
            // the task is done (a slow call): at its end it is, and a trial cut short has run out of samples there, which NI
            // reports as an error, not always with its count of samples generated reaching the samples written (B2, 26 Nov 2026).
            void TopUp(Feed f)
            {
                if (f.Written >= f.End)
                {
                    if (device.IsDone) f.Generated = Math.Max(f.Generated, f.End);   // throws once run out of samples: StreamFailed
                    else f.Generated = Math.Max(f.Generated, device.SamplesGenerated);
                    f.ReadAt = Stopwatch.GetTimestamp();
                    return;
                }
                var generated = device.SamplesGenerated;
                f.ReadAt = Stopwatch.GetTimestamp();
                f.Generated = Math.Max(f.Generated, generated);
                if (generated > 0 && f.Written < f.End) f.MinAhead = Math.Min(f.MinAhead, f.Written - generated);
                while (f.Written < f.End && f.Written - generated < f.Target)
                {
                    var next = f.Next(f.Written + f.MaxWrite) ?? throw new InvalidOperationException($"ZapitDaq found no laser sample at 0 V within {f.MaxWrite} samples of sample {f.Written} to end a write on.");
                    device.Write(next);
                    f.Written += next.GetLength(1);
                }
            }

            void Feeder()
            {
                while (!disposed)
                {
                    bool active = false;
                    lock (deviceGate)
                    {
                        var f = feed;
                        if (!disposed && f != null && f.Failure == null)
                        {
                            active = true;
                            try { TopUp(f); }
                            catch (Exception error) { StreamFailed(f, error); }
                        }
                    }
                    if (active) Thread.Sleep(1);
                    else feeding.Wait(100);
                }
            }

            // Thread.Sleep(1) sleeps a whole 15.6 ms timer tick unless the timer runs at 1 ms: held while a streamed trial is armed.
            void FineTimer(bool on)
            {
                if (on == fineTimer) return;
                fineTimer = on;
                try
                {
                    if (on) timeBeginPeriod(1);
                    else timeEndPeriod(1);
                }
                catch (Exception) { }   // no winmm (not Windows): the feeder's sleeps are coarser
            }

            void Monitor()
            {
                while (!disposed)
                {
                    try
                    {
                        lock (gate)
                        {
                            if (!disposed && trial != null) Poll(trial, fresh: false);
                        }
                    }
                    catch (Exception error)
                    {
                        lock (gate)
                        {
                            if (!disposed) observer.OnError(error);
                        }
                        return;
                    }
                    Thread.Sleep(1);
                }
            }

            // Zeroes first, under the gate: the monitor and the feeder touch the device only under the device gate and stop
            // once they see disposed. A dispose from inside an event (the gate already held, or on the monitor's own thread)
            // never joins the monitor, which would wait on that gate for ever; the feeder never takes the gate.
            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    try
                    {
                        lock (deviceGate)
                        {
                            feed = null;
                            try { device.StopClocked(); }   // NI reports a failed generation here: the lines are zeroed all the same
                            finally
                            {
                                try { device.WriteOnDemand(Zeros); }
                                finally { device.Dispose(); }
                            }
                        }
                    }
                    finally
                    {
                        FineTimer(false);
                        feeding.Set();
                    }
                }
                if (Thread.CurrentThread != feeder) feeder.Join();
                if (Thread.CurrentThread != monitor && !System.Threading.Monitor.IsEntered(gate)) monitor.Join();
            }
        }
    }
}
