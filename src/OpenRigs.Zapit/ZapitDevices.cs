using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NationalInstruments.DAQmx;
using NiTask = NationalInstruments.DAQmx.Task;

namespace OpenRigs.Zapit
{
    /// <summary>The AO lines (galvo X, galvo Y, laser, LED) and photodiode AI line of a Zapit DAQ, as NI physical channels.</summary>
    public sealed class ZapitWiring
    {
        /// <summary>The four AO channels in sample-row order: galvo X, galvo Y, laser, LED.</summary>
        public string[] AnalogOutputs { get; set; } = new string[4];

        /// <summary>The photodiode's AI channel.</summary>
        public string Photodiode { get; set; } = "";

        /// <summary>How the photodiode is wired.</summary>
        public ZapitInputTerminal PhotodiodeTerminalConfiguration { get; set; }

        /// <summary>The device's AO sample clock terminal, which the photodiode AI follows.</summary>
        public string AnalogOutputClock => "/" + AnalogOutputs[0].Split('/')[0] + "/ao/SampleClock";
    }

    /// <summary>The photodiode's AI terminal configuration.</summary>
    public enum ZapitInputTerminal
    {
        /// <summary>Referenced single-ended.</summary>
        Rse,
        /// <summary>Non-referenced single-ended.</summary>
        Nrse,
        /// <summary>Differential.</summary>
        Differential,
    }

    /// <summary>The DAQ under <see cref="ZapitDaq"/>: NI's or the simulated twin, with the same refusals.</summary>
    public interface IZapitDevice : IDisposable
    {
        /// <summary>Writes one value per AO line on a short-lived on-demand task. As on NI, where the on-demand task
        /// takes the AO lines, any clocked task is stopped and deleted first.</summary>
        void WriteOnDemand(double[] volts);

        /// <summary>Writes <paramref name="samples"/> (one row per AO line) to a new finite clocked task and starts it,
        /// generating on the first rising edge of <paramref name="triggerTerminal"/> (at once when empty): a trial whose end
        /// is fixed at Arm, written whole, regeneration allowed. Refused while a clocked task exists. NI's puts its digital
        /// filter of minimum pulse width <see cref="ZapitDaq.TriggerFilterSeconds"/> on the start trigger, so a glitch shorter
        /// than it does not start the task; the filter delays the recognised edge by about that width.</summary>
        void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal);

        /// <summary>Creates, not starts, a finite clocked task of <paramref name="samples"/> per channel that does not
        /// regenerate: its samples are streamed with <see cref="Write"/>, the first before <see cref="StartClocked"/>. A
        /// generation that catches up with the samples written stops with an error (NI: "to prevent the regeneration of old
        /// samples"), the lines holding the last sample generated. The trigger and its filter as <see cref="ArmFinite"/>'s.
        /// Refused while a clocked task exists.</summary>
        void CreateFinite(long samples, double sampleRate, string triggerTerminal);

        /// <summary>Appends <paramref name="samples"/> to the streamed task's samples. Once started, a write returns only when
        /// its last sample is within about the onboard FIFO and the USB transfers in flight of the generation (about 8000
        /// samples on B2's USB-6453; a 27000-sample write blocked about 240 ms), so ZapitDaq writes small chunks.</summary>
        void Write(double[,] samples);

        /// <summary>Starts the streamed task: it generates from its trigger, or at once with none.</summary>
        void StartClocked();

        /// <summary>The clocked task's samples per channel generated so far (0 before its trigger).</summary>
        long SamplesGenerated { get; }

        /// <summary>The clocked task's onboard FIFO, in samples per channel (0 with no task): a streamed trial keeps more
        /// than it written ahead of the generation.</summary>
        long OnboardSamples { get; }

        /// <summary>Whether a clocked task exists.</summary>
        bool HasClockedTask { get; }

        /// <summary>Whether the clocked task has generated all its samples.</summary>
        bool IsDone { get; }

        /// <summary>Stops and deletes the clocked task, if any.</summary>
        void StopClocked();

        /// <summary>Plays <paramref name="samples"/> as finite AO and reads the photodiode on the AO sample clock, one value
        /// per sample. Refused while a clocked task exists.</summary>
        double[] Staircase(double[,] samples, double sampleRate);
    }

    static class ZapitDeviceChecks
    {
        public static void Range(double[] volts)
        {
            if (volts.Length != 4) throw new InvalidOperationException("A Zapit on-demand write needs 4 values: galvo X, galvo Y, laser, LED.");
            if (volts.Any(v => !(Math.Abs(v) <= ZapitBank.RangeVolts))) throw new InvalidOperationException($"A Zapit write exceeds +/-{ZapitBank.RangeVolts} V.");
        }

        public static void Range(double[,] samples)
        {
            if (samples.GetLength(0) != 4 || samples.GetLength(1) < 1) throw new InvalidOperationException("Zapit clocked samples need 4 rows (galvo X, galvo Y, laser, LED) and at least one column.");
            foreach (var v in samples) if (!(Math.Abs(v) <= ZapitBank.RangeVolts)) throw new InvalidOperationException($"Zapit clocked samples exceed +/-{ZapitBank.RangeVolts} V.");
        }

        public static InvalidOperationException Reserved() =>
            new InvalidOperationException("The Zapit AO lines are reserved by a clocked task; it must be stopped and deleted first.");
    }

    /// <summary>The NI DAQ, through NationalInstruments.DAQmx.</summary>
    sealed class NiZapitDevice : IZapitDevice
    {
        readonly ZapitWiring wiring;
        NiTask? clocked;
        AnalogMultiChannelWriter? writer;   // the streamed task's

        public NiZapitDevice(ZapitWiring wiring) => this.wiring = wiring;

        NiTask AnalogOutputTask(string name)
        {
            var task = new NiTask(name);
            foreach (var channel in wiring.AnalogOutputs)
                task.AOChannels.CreateVoltageChannel(channel, "", -ZapitBank.RangeVolts, ZapitBank.RangeVolts, AOVoltageUnits.Volts);
            return task;
        }

        public void WriteOnDemand(double[] volts)
        {
            ZapitDeviceChecks.Range(volts);
            try { StopClocked(); }   // a failed generation is reported here: the lines are written all the same
            finally
            {
                using var task = AnalogOutputTask("ZapitOnDemand");
                new AnalogMultiChannelWriter(task.Stream).WriteSingleSample(true, volts);
            }
        }

        public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal)
        {
            ZapitDeviceChecks.Range(samples);
            if (clocked != null) throw ZapitDeviceChecks.Reserved();
            var task = TrialTask(samples.GetLength(1), sampleRate, triggerTerminal);
            try
            {
                task.Stream.WriteRegenerationMode = WriteRegenerationMode.AllowRegeneration;
                new AnalogMultiChannelWriter(task.Stream).WriteMultiSample(false, samples);
                task.Start();
                clocked = task;
            }
            catch
            {
                task.Dispose();
                throw;
            }
        }

        public void CreateFinite(long samples, double sampleRate, string triggerTerminal)
        {
            if (clocked != null) throw ZapitDeviceChecks.Reserved();
            var task = TrialTask(samples, sampleRate, triggerTerminal);
            try
            {
                task.Stream.WriteRegenerationMode = WriteRegenerationMode.DoNotAllowRegeneration;
                task.Stream.Timeout = 2000;   // a write never waits on a streamed trial: one that does is an error, not a hang
                task.Control(TaskAction.Verify);
                writer = new AnalogMultiChannelWriter(task.Stream);
                clocked = task;
            }
            catch
            {
                task.Dispose();
                throw;
            }
        }

        public void Write(double[,] samples)
        {
            ZapitDeviceChecks.Range(samples);
            if (clocked == null || writer == null) throw new InvalidOperationException("No streamed Zapit task to write to.");
            writer.WriteMultiSample(false, samples);
        }

        public void StartClocked()
        {
            if (clocked == null) throw new InvalidOperationException("No Zapit clocked task to start.");
            clocked.Start();
        }

        NiTask TrialTask(long samples, double sampleRate, string triggerTerminal)
        {
            var task = AnalogOutputTask("ZapitTrial");
            try
            {
                task.Timing.ConfigureSampleClock("", sampleRate, SampleClockActiveEdge.Rising, SampleQuantityMode.FiniteSamples, checked((int)samples));
                if (!string.IsNullOrEmpty(triggerTerminal))
                {
                    task.Triggers.StartTrigger.ConfigureDigitalEdgeTrigger(triggerTerminal, DigitalEdgeStartTriggerEdge.Rising);
                    // The trigger is a level held about 1 s (DO3 high for the trial), so a 100 us filter rejects EMI spikes
                    // (about 10 ns on B2's PFI0) without missing a trigger. It delays the recognised edge by about its width.
                    // The minimum pulse width must be set before the filter is enabled, or DAQmx refuses the enable.
                    try
                    {
                        task.Triggers.StartTrigger.DigitalEdge.DigitalFilterMinimumPulseWidth = ZapitDaq.TriggerFilterSeconds;
                        task.Triggers.StartTrigger.DigitalEdge.DigitalFilterEnable = true;
                    }
                    catch (DaqException e)
                    {
                        throw new InvalidOperationException($"The NI device refused the digital filter on the Zapit trigger {triggerTerminal} " +
                            $"(lib-open-rigs' fixed ZapitDaq.TriggerFilterSeconds, {ZapitDaq.TriggerFilterSeconds} s): this card or line cannot filter its start trigger. {e.Message}", e);
                    }
                }
                return task;
            }
            catch
            {
                task.Dispose();
                throw;
            }
        }

        public long SamplesGenerated => clocked?.Stream.TotalSamplesGeneratedPerChannel ?? 0;

        public long OnboardSamples => clocked?.Stream.Buffer.OutputOnBoardBufferSize ?? 0;

        public bool HasClockedTask => clocked != null;

        public bool IsDone => clocked?.IsDone ?? true;

        public void StopClocked()
        {
            if (clocked == null) return;
            try { clocked.Stop(); }
            finally
            {
                clocked.Dispose();
                clocked = null;
                writer = null;
            }
        }

        public double[] Staircase(double[,] samples, double sampleRate)
        {
            ZapitDeviceChecks.Range(samples);
            if (clocked != null) throw ZapitDeviceChecks.Reserved();
            int count = samples.GetLength(1);
            using var input = new NiTask("ZapitPhotodiode");
            using var output = AnalogOutputTask("ZapitStaircase");
            var terminal = wiring.PhotodiodeTerminalConfiguration switch
            {
                ZapitInputTerminal.Nrse => AITerminalConfiguration.Nrse,
                ZapitInputTerminal.Differential => AITerminalConfiguration.Differential,
                _ => AITerminalConfiguration.Rse,
            };
            input.AIChannels.CreateVoltageChannel(wiring.Photodiode, "", terminal, -ZapitBank.RangeVolts, ZapitBank.RangeVolts, AIVoltageUnits.Volts);
            input.Timing.ConfigureSampleClock(wiring.AnalogOutputClock, sampleRate, SampleClockActiveEdge.Rising, SampleQuantityMode.FiniteSamples, count);
            output.Timing.ConfigureSampleClock("", sampleRate, SampleClockActiveEdge.Rising, SampleQuantityMode.FiniteSamples, count);
            new AnalogMultiChannelWriter(output.Stream).WriteMultiSample(false, samples);
            input.Stream.Timeout = (int)(count / sampleRate * 1000) + 10000;
            input.Start();
            output.Start();
            var read = new AnalogSingleChannelReader(input.Stream).ReadMultiSample(count);
            output.WaitUntilDone(10000);
            return read;
        }

        public void Dispose() => StopClocked();
    }


    /// <summary>
    /// The simulated twin of the NI DAQ: keeps state only, with NI's refusals. A finite task generates at its rate on
    /// <see cref="Clock"/> from its trigger, which comes at its start when <see cref="AutoTrigger"/> is set and otherwise
    /// from <see cref="Trigger"/>. A streamed task (<see cref="CreateFinite"/>) does not regenerate, as NI's: a generation
    /// that catches up with the samples written stops there with an error (reported by <see cref="IsDone"/>, a write, or
    /// <see cref="StopClocked"/>), the lines holding the last sample generated (<see cref="Outputs"/>); a write after the
    /// start ending more than <see cref="WriteAheadLimitSamples"/> past the generation is refused, where NI's would block.
    /// </summary>
    public sealed class SimulatedZapitDevice : IZapitDevice
    {
        readonly object sync = new object();   // ZapitDaq calls from its threads, a test triggers and waits from its own
        readonly Stopwatch stopwatch = Stopwatch.StartNew();
        double rate;
        double? triggeredAt;
        bool started, streamed, underflowReported;
        long total, written;
        double lastWrite;   // host ms
        const double QuietMs = 30;
        long? underflowAt;
        double[] held = new double[4];   // the lines when no clocked task generates

        /// <summary>The simulated time, in seconds; the host's stopwatch unless a test sets it.</summary>
        public Func<double> Clock { get; set; }

        /// <summary>Whether a started task triggers at once (a desk run with no Harp line); tests clear it and call <see cref="Trigger"/>.</summary>
        public bool AutoTrigger { get; set; } = true;

        /// <summary>The photodiode volts for a laser control volts.</summary>
        public Func<double, double> Photodiode { get; set; } = volts => 0.5 * volts;

        /// <summary>Every on-demand write, oldest first.</summary>
        public List<double[]> OnDemandWrites { get; } = new List<double[]>();

        /// <summary>How many clocked tasks an on-demand write deleted.</summary>
        public int ClockedTasksDeleted { get; private set; }

        /// <summary>The last clocked task's samples: all of an armed one, a streamed one's as written (unwritten ones 0).</summary>
        public double[,]? LastBuffer { get; private set; }

        /// <summary>The samples the last clocked task had generated when it was stopped.</summary>
        public long LastSamplesGenerated { get; private set; }

        /// <summary>The samples per channel the clocked task holds (written, for a streamed one).</summary>
        public long SamplesWritten { get { lock (sync) return HasClockedTask ? written : 0; } }

        /// <summary>The onboard FIFO's samples per channel, as NI's: 0 with no clocked task (whatever is set); 0 by default.</summary>
        public long OnboardSamples
        {
            get { return HasClockedTask ? onboardSamples : 0; }
            set { onboardSamples = value; }
        }

        long onboardSamples;

        /// <summary>The most samples a write after the start may end past the generation (0, the default: no limit); NI's
        /// write blocks beyond about the onboard FIFO and the USB transfers in flight (about 8000 on B2's USB-6453).</summary>
        public long WriteAheadLimitSamples { get; set; }

        /// <summary>The most samples one write held once its streamed task had started.</summary>
        public int LargestStartedWrite { get; private set; }

        /// <summary>The highest laser volts any streamed write ended on: the sample a DAQ that ran out of samples there would hold.</summary>
        public double MaxLaserAtWriteEnd { get; private set; }

        /// <summary>The sample the lines held when a streamed task last ran out of samples (null before any did).</summary>
        public double[]? HeldAtUnderflow { get; private set; }

        /// <summary>How many streamed tasks ran out of samples.</summary>
        public int Underflows { get; private set; }

        /// <summary>How far short of the samples written <see cref="SamplesGenerated"/> stays once a streamed task has run out
        /// of samples (0 by default): on B2's USB-6453 NI's count once stayed short of a cut trial's end (26 Nov 2026; forced
        /// underflows read about 52 past the samples written), so only <see cref="IsDone"/>, a write
        /// or the stop reported the end.</summary>
        public long UnderflowCountShortSamples { get; set; }

        /// <summary>What the four lines hold now: the clocked task's last sample generated, held once it stops, or the last on-demand write.</summary>
        public double[] Outputs
        {
            get
            {
                lock (sync)
                {
                    var generated = Generated();
                    return HasClockedTask && generated > 0 ? Column(generated - 1) : (double[])held.Clone();
                }
            }
        }

        /// <summary>The last clocked task's trigger terminal.</summary>
        public string? LastTriggerTerminal { get; private set; }

        /// <summary>A new twin on the host's stopwatch.</summary>
        public SimulatedZapitDevice() => Clock = () => stopwatch.Elapsed.TotalSeconds;

        /// <summary>The trigger's rising edge.</summary>
        public void Trigger()
        {
            lock (sync)
                if (HasClockedTask && started && triggeredAt == null) triggeredAt = Clock();
        }

        /// <summary>Waits until a streamed task holds <paramref name="aheadSamples"/> past its generation (or all its samples),
        /// or is not generating, or its writer has written nothing for 30 ms with samples still to generate (its stream ends
        /// there): a test moving <see cref="Clock"/> by steps lets ZapitDaq's feeder keep up, as the host's clock does. False
        /// when <paramref name="timeoutMs"/> passes first.</summary>
        public bool AwaitWritesAhead(long aheadSamples, int timeoutMs = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                lock (sync)
                {
                    if (!HasClockedTask || !streamed || triggeredAt == null) return true;
                    var generated = Generated();
                    if (underflowAt != null || written >= Math.Min(total, generated + aheadSamples)) return true;
                    // The writer stopped short of that with samples still to generate: its stream ends there (an early stop).
                    if (written > generated && watch.ElapsedMilliseconds > QuietMs && stopwatch.Elapsed.TotalMilliseconds - lastWrite > QuietMs) return true;
                }
                if (watch.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(0);
            }
        }

        /// <inheritdoc/>
        public void WriteOnDemand(double[] volts)
        {
            ZapitDeviceChecks.Range(volts);
            lock (sync)
            {
                try
                {
                    if (HasClockedTask)
                    {
                        ClockedTasksDeleted++;
                        StopClocked();
                    }
                }
                finally
                {
                    held = (double[])volts.Clone();
                    OnDemandWrites.Add((double[])volts.Clone());
                }
            }
        }

        /// <inheritdoc/>
        public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal)
        {
            ZapitDeviceChecks.Range(samples);
            lock (sync)
            {
                Create(samples.GetLength(1), sampleRate, triggerTerminal);
                LastBuffer = (double[,])samples.Clone();
                written = total;
                StartClocked();
            }
        }

        /// <inheritdoc/>
        public void CreateFinite(long samples, double sampleRate, string triggerTerminal)
        {
            lock (sync)
            {
                if (samples < 1) throw new InvalidOperationException("A Zapit clocked task needs at least one sample.");
                Create(samples, sampleRate, triggerTerminal);
                LastBuffer = new double[4, samples];
                streamed = true;
            }
        }

        void Create(long samples, double sampleRate, string triggerTerminal)
        {
            if (HasClockedTask) throw ZapitDeviceChecks.Reserved();
            if (!(sampleRate > 0)) throw new InvalidOperationException("A Zapit clocked task needs a sample rate above 0.");
            rate = sampleRate;
            total = samples;
            written = 0;
            streamed = started = underflowReported = false;
            underflowAt = null;
            triggeredAt = null;
            LastTriggerTerminal = triggerTerminal;
            HasClockedTask = true;
        }

        /// <inheritdoc/>
        public void Write(double[,] samples)
        {
            ZapitDeviceChecks.Range(samples);
            lock (sync)
            {
                if (!HasClockedTask || !streamed) throw new InvalidOperationException("No streamed Zapit task to write to.");
                int count = samples.GetLength(1);
                if (triggeredAt != null)
                {
                    var generated = Generated();
                    if (underflowAt != null) throw ReportUnderflow();
                    if (WriteAheadLimitSamples > 0 && written + count - generated > WriteAheadLimitSamples)
                        throw new InvalidOperationException($"A Zapit write of {count} samples would end {written + count - generated} samples past the generation, beyond the twin's WriteAheadLimitSamples ({WriteAheadLimitSamples}): NI's write would block.");
                }
                if (written + count > total) throw new InvalidOperationException("A Zapit write runs past the end of the task's samples.");
                if (started) LargestStartedWrite = Math.Max(LargestStartedWrite, count);
                MaxLaserAtWriteEnd = Math.Max(MaxLaserAtWriteEnd, samples[2, count - 1]);
                var buffer = LastBuffer!;
                for (int channel = 0; channel < 4; channel++)
                    for (int i = 0; i < count; i++) buffer[channel, written + i] = samples[channel, i];
                written += count;
                lastWrite = stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        /// <inheritdoc/>
        public void StartClocked()
        {
            lock (sync)
            {
                if (!HasClockedTask) throw new InvalidOperationException("No Zapit clocked task to start.");
                if (written == 0) throw new InvalidOperationException("A streamed Zapit task cannot start with no samples written.");
                started = true;
                triggeredAt = AutoTrigger ? Clock() : (double?)null;
            }
        }

        // The samples generated, noting an underflow: a streamed task stops where its samples run out.
        long Generated()
        {
            if (!HasClockedTask || triggeredAt == null) return 0;
            if (underflowAt != null) return Math.Max(0, underflowAt.Value - UnderflowCountShortSamples);
            var generated = Math.Min(total, (long)Math.Floor((Clock() - triggeredAt.Value) * rate + 1e-9));
            if (generated <= written) return generated;
            underflowAt = written;
            HeldAtUnderflow = written > 0 ? Column(written - 1) : new double[4];
            Underflows++;
            return written;
        }

        InvalidOperationException ReportUnderflow()
        {
            underflowReported = true;
            return new InvalidOperationException($"The twin's generation caught up with the {underflowAt} samples written of {total} and stopped, the lines holding the last sample (NI: the generation has stopped to prevent the regeneration of old samples).");
        }

        double[] Column(long i) => Enumerable.Range(0, 4).Select(c => LastBuffer![c, i]).ToArray();

        /// <inheritdoc/>
        public long SamplesGenerated { get { lock (sync) return Generated(); } }

        /// <inheritdoc/>
        public bool HasClockedTask { get; private set; }

        /// <inheritdoc/>
        public bool IsDone
        {
            get
            {
                lock (sync)
                {
                    if (!HasClockedTask) return true;
                    var generated = Generated();
                    if (underflowAt != null) throw ReportUnderflow();
                    return generated >= total;
                }
            }
        }

        /// <inheritdoc/>
        public void StopClocked()
        {
            lock (sync)
            {
                if (!HasClockedTask) return;
                var generated = Generated();
                LastSamplesGenerated = generated;
                if (generated > 0) held = Column(generated - 1);   // NI's lines hold the last sample
                HasClockedTask = false;
                triggeredAt = null;
                if (underflowAt != null && !underflowReported) throw ReportUnderflow();   // NI's Stop reports the error
            }
        }

        /// <inheritdoc/>
        public double[] Staircase(double[,] samples, double sampleRate)
        {
            ZapitDeviceChecks.Range(samples);
            if (HasClockedTask) throw ZapitDeviceChecks.Reserved();
            return Enumerable.Range(0, samples.GetLength(1)).Select(i => Photodiode(samples[2, i])).ToArray();
        }

        /// <inheritdoc/>
        public void Dispose() => StopClocked();
    }
}
