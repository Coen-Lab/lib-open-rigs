using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        /// generating on the first rising edge of <paramref name="triggerTerminal"/> (at once when empty). Refused while a
        /// clocked task exists.</summary>
        void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal);

        /// <summary>Overwrites the clocked task's buffer from sample <paramref name="firstSample"/>, which must not yet be
        /// generated nor already in the device's onboard FIFO (whose samples cannot be overwritten): one too late is refused
        /// with an error, never written in part.</summary>
        void WriteAt(double[,] samples, long firstSample);

        /// <summary>The clocked task's samples per channel generated so far (0 before its trigger).</summary>
        long SamplesGenerated { get; }

        /// <summary>The clocked task's onboard FIFO, in samples per channel, beyond reach of <see cref="WriteAt"/> (0 with no task).</summary>
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

        public static void InTime(long firstSample, long generated, long onboard)
        {
            if (firstSample < generated + onboard)
                throw new InvalidOperationException($"A Zapit early stop wrote its ramp from sample {firstSample}, but {generated} were generated and the next {onboard} already sit in the device's onboard buffer, " +
                    "so the ramp could not be kept: raise rig.yml's zapit.earlyStopLeadSeconds above the onboard buffer plus one scan cycle (their samples per channel over the sample rate).");
        }

        public static InvalidOperationException Reserved() =>
            new InvalidOperationException("The Zapit AO lines are reserved by a clocked task; it must be stopped and deleted first.");
    }

    /// <summary>The NI DAQ, through NationalInstruments.DAQmx.</summary>
    sealed class NiZapitDevice : IZapitDevice
    {
        readonly ZapitWiring wiring;
        NiTask? clocked;

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
            StopClocked();
            using var task = AnalogOutputTask("ZapitOnDemand");
            new AnalogMultiChannelWriter(task.Stream).WriteSingleSample(true, volts);
        }

        public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal)
        {
            ZapitDeviceChecks.Range(samples);
            if (clocked != null) throw ZapitDeviceChecks.Reserved();
            var task = AnalogOutputTask("ZapitTrial");
            try
            {
                task.Timing.ConfigureSampleClock("", sampleRate, SampleClockActiveEdge.Rising, SampleQuantityMode.FiniteSamples, samples.GetLength(1));
                if (!string.IsNullOrEmpty(triggerTerminal))
                    task.Triggers.StartTrigger.ConfigureDigitalEdgeTrigger(triggerTerminal, DigitalEdgeStartTriggerEdge.Rising);
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

        public void WriteAt(double[,] samples, long firstSample)
        {
            ZapitDeviceChecks.Range(samples);
            if (clocked == null) throw new InvalidOperationException("No Zapit clocked task to write into.");
            ZapitDeviceChecks.InTime(firstSample, clocked.Stream.TotalSamplesGeneratedPerChannel, clocked.Stream.Buffer.OutputOnBoardBufferSize);
            clocked.Stream.WriteRelativeTo = WriteRelativeTo.FirstSample;
            clocked.Stream.WriteOffset = checked((int)firstSample);
            new AnalogMultiChannelWriter(clocked.Stream).WriteMultiSample(false, samples);
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
    /// <see cref="Clock"/> from its trigger, which comes at arming when <see cref="AutoTrigger"/> is set and otherwise
    /// from <see cref="Trigger"/>.
    /// </summary>
    public sealed class SimulatedZapitDevice : IZapitDevice
    {
        readonly Stopwatch stopwatch = Stopwatch.StartNew();
        double rate;
        double? triggeredAt;

        /// <summary>The simulated time, in seconds; the host's stopwatch unless a test sets it.</summary>
        public Func<double> Clock { get; set; }

        /// <summary>Whether an armed task triggers at once (a desk run with no Harp line); tests clear it and call <see cref="Trigger"/>.</summary>
        public bool AutoTrigger { get; set; } = true;

        /// <summary>The photodiode volts for a laser control volts.</summary>
        public Func<double, double> Photodiode { get; set; } = volts => 0.5 * volts;

        /// <summary>Every on-demand write, oldest first.</summary>
        public List<double[]> OnDemandWrites { get; } = new List<double[]>();

        /// <summary>How many clocked tasks an on-demand write deleted.</summary>
        public int ClockedTasksDeleted { get; private set; }

        /// <summary>The last clocked task's buffer, with what <see cref="WriteAt"/> changed.</summary>
        public double[,]? LastBuffer { get; private set; }

        /// <summary>The samples the last clocked task had generated when it was stopped.</summary>
        public long LastSamplesGenerated { get; private set; }

        /// <summary>The onboard FIFO's samples per channel, already beyond reach of <see cref="WriteAt"/>, as NI's; 0 by default.</summary>
        public long OnboardSamples { get; set; }

        /// <summary>The last clocked task's trigger terminal.</summary>
        public string? LastTriggerTerminal { get; private set; }

        /// <summary>A new twin on the host's stopwatch.</summary>
        public SimulatedZapitDevice() => Clock = () => stopwatch.Elapsed.TotalSeconds;

        /// <summary>The trigger's rising edge.</summary>
        public void Trigger()
        {
            if (HasClockedTask && triggeredAt == null) triggeredAt = Clock();
        }

        /// <inheritdoc/>
        public void WriteOnDemand(double[] volts)
        {
            ZapitDeviceChecks.Range(volts);
            if (HasClockedTask)
            {
                ClockedTasksDeleted++;
                StopClocked();
            }
            OnDemandWrites.Add((double[])volts.Clone());
        }

        /// <inheritdoc/>
        public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal)
        {
            ZapitDeviceChecks.Range(samples);
            if (HasClockedTask) throw ZapitDeviceChecks.Reserved();
            if (!(sampleRate > 0)) throw new InvalidOperationException("A Zapit clocked task needs a sample rate above 0.");
            rate = sampleRate;
            LastBuffer = (double[,])samples.Clone();
            LastTriggerTerminal = triggerTerminal;
            HasClockedTask = true;
            triggeredAt = AutoTrigger ? Clock() : (double?)null;
        }

        /// <inheritdoc/>
        public void WriteAt(double[,] samples, long firstSample)
        {
            ZapitDeviceChecks.Range(samples);
            if (!HasClockedTask) throw new InvalidOperationException("No Zapit clocked task to write into.");
            var buffer = LastBuffer!;
            if (firstSample < SamplesGenerated) throw new InvalidOperationException($"Zapit sample {firstSample} was already generated.");
            ZapitDeviceChecks.InTime(firstSample, SamplesGenerated, OnboardSamples);
            if (firstSample + samples.GetLength(1) > buffer.GetLength(1)) throw new InvalidOperationException("A Zapit write runs past the end of the buffer.");
            for (int channel = 0; channel < 4; channel++)
                for (int i = 0; i < samples.GetLength(1); i++) buffer[channel, firstSample + i] = samples[channel, i];
        }

        /// <inheritdoc/>
        public long SamplesGenerated => !HasClockedTask || triggeredAt == null ? 0
            : Math.Min(LastBuffer!.GetLength(1), (long)Math.Floor((Clock() - triggeredAt.Value) * rate + 1e-9));

        /// <inheritdoc/>
        public bool HasClockedTask { get; private set; }

        /// <inheritdoc/>
        public bool IsDone => !HasClockedTask || SamplesGenerated >= LastBuffer!.GetLength(1);

        /// <inheritdoc/>
        public void StopClocked()
        {
            if (!HasClockedTask) return;
            LastSamplesGenerated = SamplesGenerated;
            HasClockedTask = false;
            triggeredAt = null;
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
