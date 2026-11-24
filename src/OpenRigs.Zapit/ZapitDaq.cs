using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Serialization;
using Bonsai;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// Runs Zapit on the rig's NI DAQ (or its simulated twin). Trials: a loaded bank's cycles and ramp tail concatenated
    /// into one finite AO buffer, armed on the trigger line, at most <see cref="MaxTrialSeconds"/> long and ending with laser
    /// and LED at 0 V; an early stop writes the ramp <see cref="EarlyStopLeadSeconds"/> ahead of the generated samples, then
    /// stops. Between trials the clocked task is stopped and deleted and every line zeroed on a short-lived on-demand task.
    /// Calibration: on-demand points, and a laser staircase read back through the photodiode on the AO clock. Every line is
    /// zeroed on subscribe and on unsubscribe, however the workflow stops. A Point once trials are armed is Refused (an event;
    /// the session runs on); any other refusal or DAQ error is an error. No laser sample above <see cref="LaserCeilingVolts"/>
    /// is written, and a bank holding one, or a power above <see cref="CeilingMw"/>, is refused at LoadBank.
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

        /// <summary>The rig's laser ceiling, in mW (rig.yml's zapit.ceilingMw). At 0, the default, the laser is disabled and ZapitDaq refuses any laser sample above 0 V, so a lost mapping fails safe.</summary>
        [Description("The rig's laser ceiling, in mW. At 0 (the default) the laser is disabled and ZapitDaq refuses any laser sample above 0 V.")]
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

        /// <summary>How far ahead of the generated samples an early stop writes its ramp, in seconds.</summary>
        [Description("How far ahead of the generated samples an early stop writes its ramp, in seconds.")]
        public double EarlyStopLeadSeconds { get; set; }

        /// <summary>Tests replace the device; null takes <see cref="Simulated"/>.</summary>
        [XmlIgnore, Browsable(false)]
        internal Func<IZapitDevice>? DeviceFactory { get; set; }

        static readonly double[] Zeros = new double[4];

        // The bank stores float32 volts from lib_zapit's float maths: some rows come out a few microvolts below 0 (-2e-6 V
        // in the contract bank), and a maximum such as 4.9 V reads back 1e-7 V above it. The laser limits allow that rounding and no more.
        const double FloatSlackVolts = 1e-4;
        const double FloatSlackMw = 1e-3;   // the bank's powers come from the same float maths

        /// <summary>Runs each command in order and reports what the DAQ did.</summary>
        public override IObservable<ZapitDaqEvent> Process(IObservable<ZapitDaqCommand> source) => Observable.Create<ZapitDaqEvent>(observer =>
        {
            if (!(SampleRate > 0) || !(MaxTrialSeconds > 0) || !(EarlyStopLeadSeconds > 0))
                throw new InvalidOperationException("ZapitDaq needs SampleRate, MaxTrialSeconds and EarlyStopLeadSeconds above 0 (rig.yml's zapit block).");
            var wiring = new ZapitWiring
            {
                AnalogOutputs = new[] { GalvoXChannel, GalvoYChannel, LaserChannel, LedChannel },
                Photodiode = PhotodiodeChannel,
                PhotodiodeTerminalConfiguration = PhotodiodeTerminalConfiguration,
            };
            if (DeviceFactory == null && !Simulated && (wiring.AnalogOutputs.Any(string.IsNullOrEmpty) || string.IsNullOrEmpty(PhotodiodeChannel)))
                throw new InvalidOperationException("ZapitDaq needs its four AO channels and the photodiode channel (rig.yml's zapit block).");
            var device = DeviceFactory?.Invoke() ?? (Simulated ? new SimulatedZapitDevice() : CreateNi(wiring));
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

        sealed class Trial
        {
            public ZapitDaqCommand Command = null!;
            public ZapitBankVariant Variant = null!;
            public int Cycles;
            public long Samples;
            public long StopAt;
            public bool Started;
            public bool Stopping;
        }

        sealed class Session : IDisposable
        {
            readonly ZapitDaq node;
            readonly IZapitDevice device;
            readonly IObserver<ZapitDaqEvent> observer;   // called only under the gate, so the commands' and the monitor's calls never race
            readonly object gate = new object();
            readonly Thread monitor;
            volatile bool disposed;
            ZapitBank? bank;
            Trial? trial;
            bool trialsArmed;   // a trial was armed: Point is refused for the rest of the session

            public Session(ZapitDaq node, IZapitDevice device, IObserver<ZapitDaqEvent> observer)
            {
                this.node = node;
                this.device = device;
                this.observer = observer;
                device.WriteOnDemand(Zeros);
                monitor = new Thread(Monitor) { IsBackground = true, Name = "ZapitDaq" };
                monitor.Start();
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
                if (!(loaded.CeilingMw <= node.CeilingMw))
                    throw new InvalidOperationException($"The Zapit bank was built under a {loaded.CeilingMw} mW ceiling, above rig.yml's zapit.ceilingMw ({node.CeilingMw} mW): rebuild the bank.");
                // The bank's ceiling_mw is its own claim: its powers and volts are held to the rig's ceiling too.
                var over = loaded.Conditions.SelectMany(c => c.Variants).Where(v => v.LaserOn && !(v.CappedPowerMw <= node.CeilingMw + FloatSlackMw)).Select(v => v.CappedPowerMw).DefaultIfEmpty().Max();
                if (over > 0)
                    throw new InvalidOperationException($"The Zapit bank delivers {over} mW, above rig.yml's zapit.ceilingMw ({node.CeilingMw} mW): rebuild the bank.");
                RefuseLaser(new[] { loaded.MinLaserVolts, loaded.MaxLaserVolts }, "The Zapit bank's samples");
                loaded.Verify(command.ConditionsPath, command.ScannerFitHash, command.LaserCurveHash, command.SampleHash);
                bank = loaded;
                Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.BankLoaded, Bank = loaded });
            }

            void Arm(ZapitDaqCommand command)
            {
                var loaded = bank ?? throw new InvalidOperationException($"Trial {command.Trial} cannot arm: no Zapit bank is loaded.");
                if (trial != null && trial.Started)
                    throw new InvalidOperationException($"Trial {command.Trial} cannot arm while trial {trial.Command.Trial} still plays.");
                if (trial != null) Finish(ZapitDaqEventKind.Stopped);   // armed but never triggered
                if (disposed) return;   // a subscriber disposed the node inside that event: the lines are zeroed
                trialsArmed = true;
                var watch = Stopwatch.StartNew();
                var variant = loaded.Variant(command.Condition, command.LaserOn, command.PowerMw);
                var cycles = loaded.Cycles(command.Condition, variant, command.DurationSeconds, node.MaxTrialSeconds);
                var samples = loaded.Assemble(command.Condition, variant, cycles);
                RefuseLaser(Laser(samples), $"Trial {command.Trial}'s bank samples");
                device.WriteOnDemand(Zeros);
                device.ArmFinite(samples, loaded.SampleRate, node.TriggerTerminal);
                trial = new Trial { Command = command, Variant = variant, Cycles = cycles, Samples = samples.GetLength(1), StopAt = samples.GetLength(1) };
                Emit(TrialEvent(ZapitDaqEventKind.Armed, 0, watch.Elapsed.TotalSeconds));
            }

            void EarlyStop(ZapitDaqCommand command)
            {
                if (trial == null || trial.Command.Trial != command.Trial || trial.Stopping) return;   // already over
                if (!trial.Started)
                {
                    Finish(ZapitDaqEventKind.Stopped);
                    return;
                }
                var loaded = bank!;
                var condition = trial.Command.Condition;
                int cycleRows = loaded.Conditions[condition - 1].ScanCycle.Length;
                var from = device.SamplesGenerated + (long)Math.Ceiling(node.EarlyStopLeadSeconds * loaded.SampleRate);
                // The ramp starts on a cycle boundary, so the laser stays in phase with the scan: the trial cut to fewer cycles.
                var cycles = (int)((from + trial.Variant.TailReplaces + cycleRows - 1) / cycleRows);
                trial.Stopping = true;
                if (cycles >= trial.Cycles) return;   // its own ramp comes first
                var body = loaded.BodyRows(condition, trial.Variant, cycles);
                var ramp = loaded.Assemble(condition, trial.Variant, cycles, body);
                var rest = new double[4, trial.Samples - body];
                for (int i = 0; i < rest.GetLength(1); i++)
                    for (int channel = 0; channel < 4; channel++)
                        rest[channel, i] = ramp[channel, Math.Min(i, ramp.GetLength(1) - 1)];   // the last row held
                device.WriteAt(rest, body);
                trial.StopAt = body + ramp.GetLength(1);
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
                device.WriteOnDemand(new[] { command.GalvoXVolts, command.GalvoYVolts, command.LaserVolts, 0 });
                Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Pointed });
            }

            void Staircase(ZapitDaqCommand command)
            {
                RefuseWhilePlaying(command);
                var levels = command.LaserLevelsVolts;
                if (levels == null || levels.Length == 0 || command.SamplesPerLevel < 1)
                    throw new InvalidOperationException("A Zapit staircase needs laser levels and at least one sample per level.");
                RefuseLaser(levels, "The staircase");
                var samples = new double[4, levels.Length * command.SamplesPerLevel + 1];   // the last sample: laser at 0 V
                for (int i = 0; i < samples.GetLength(1); i++)
                {
                    samples[0, i] = command.GalvoXVolts;
                    samples[1, i] = command.GalvoYVolts;
                    if (i < samples.GetLength(1) - 1) samples[2, i] = levels[i / command.SamplesPerLevel];
                }
                device.WriteOnDemand(Zeros);
                var read = device.Staircase(samples, node.SampleRate);
                device.WriteOnDemand(Zeros);
                Emit(new ZapitDaqEvent { Kind = ZapitDaqEventKind.StaircaseRead, Samples = samples.GetLength(1), AnalogIn = read });
            }

            void RefuseLaser(double[] volts, string what)
            {
                double max = volts.Max(), min = volts.Min();
                if (node.CeilingMw <= 0 && max > 0)
                    throw new InvalidOperationException($"{what} drive the laser to {max} V, but rig.yml's zapit.ceilingMw is 0 (laser disabled).");
                if (!(max <= node.LaserMaxVolts + FloatSlackVolts) || !(min >= -FloatSlackVolts))
                    throw new InvalidOperationException($"{what} drive the laser from {min} to {max} V, outside 0 to {node.LaserMaxVolts} V (rig.yml's zapit.laserMinMaxControlVolts[1]).");
                if (!(max <= node.LaserCeilingVolts + FloatSlackVolts))
                    throw new InvalidOperationException($"{what} drive the laser to {max} V, above the ceiling's {node.LaserCeilingVolts} V (LaserCeilingVolts: rig.yml's zapit.ceilingMw through the laser curve).");
            }

            static double[] Laser(double[,] samples) => Enumerable.Range(0, samples.GetLength(1)).Select(i => samples[2, i]).ToArray();

            void RefuseWhilePlaying(ZapitDaqCommand command)
            {
                if (trial != null) throw new InvalidOperationException($"ZapitDaq cannot {command.Kind} while trial {trial.Command.Trial} is armed.");
            }

            void Finish(ZapitDaqEventKind kind)
            {
                var generated = device.SamplesGenerated;
                device.StopClocked();
                device.WriteOnDemand(Zeros);
                var ended = trial;
                trial = null;
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
                SamplesGenerated = generated,
                ArmSeconds = armSeconds,
            };

            void Emit(ZapitDaqEvent e)
            {
                e.Time = DateTimeOffset.Now;
                observer.OnNext(e);
            }

            void Poll(Trial current)
            {
                var generated = device.SamplesGenerated;
                if (!current.Started && generated > 0)
                {
                    current.Started = true;
                    Emit(TrialEvent(ZapitDaqEventKind.Started, generated, 0));
                    if (disposed) return;
                }
                if (trial == current && (device.IsDone || generated >= current.StopAt))
                    Finish(current.Stopping ? ZapitDaqEventKind.Stopped : ZapitDaqEventKind.Ended);
            }

            void Monitor()
            {
                while (!disposed)
                {
                    try
                    {
                        lock (gate)
                        {
                            if (!disposed && trial != null) Poll(trial);
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

            // Zeroes first, under the gate: the monitor touches the device only under it and stops once it sees disposed. A
            // dispose from inside an event (the gate already held, or on the monitor's own thread) never joins the monitor,
            // which would wait on that gate for ever.
            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    try { device.StopClocked(); }   // NI reports a failed generation here: the lines are zeroed all the same
                    finally
                    {
                        try { device.WriteOnDemand(Zeros); }
                        finally { device.Dispose(); }
                    }
                }
                if (Thread.CurrentThread != monitor && !System.Threading.Monitor.IsEntered(gate)) monitor.Join();
            }
        }
    }
}
