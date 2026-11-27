using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace OpenRigs.Zapit.Tests
{
    [TestClass]
    public class ZapitDaqTests
    {
        const string Trigger = "/Dev1/PFI0";
        static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "Data");

        // The contract bank (the contract fixture, Data/make_fixture.py) unpacked into a new folder.
        static string Unpack()
        {
            var folder = Path.Combine(Path.GetTempPath(), "ZapitDaqTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "Settings_ZapitBank_T.json", "contract_conditions.yml" })
                File.Copy(Path.Combine(DataFolder, name), Path.Combine(folder, name));
            File.WriteAllBytes(Path.Combine(folder, "Settings_ZapitBank_T.bin"), Gunzip("Settings_ZapitBank_T.bin.gz"));
            return Path.Combine(folder, "Settings_ZapitBank_T.json");
        }

        static byte[] Gunzip(string name)
        {
            using var input = new GZipStream(File.OpenRead(Path.Combine(DataFolder, name)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        static ZapitDaqCommand Load(string path) => new ZapitDaqCommand { Kind = ZapitDaqCommandKind.LoadBank, Path = path };

        static ZapitDaqCommand Arm(int trial, int condition, bool laserOn, double powerMw, double durationSeconds) => new ZapitDaqCommand
        {
            Kind = ZapitDaqCommandKind.Arm, Trial = trial, Condition = condition, LaserOn = laserOn, PowerMw = powerMw, DurationSeconds = durationSeconds,
        };

        static ZapitDaqCommand EarlyStop(int trial) => new ZapitDaqCommand { Kind = ZapitDaqCommandKind.EarlyStop, Trial = trial };

        // A trial an EarlyStop may end, with a duration: streamed.
        static ZapitDaqCommand ArmUntilStopped(int trial, int condition, bool laserOn, double powerMw, double durationSeconds)
        {
            var arm = Arm(trial, condition, laserOn, powerMw, durationSeconds);
            arm.UntilStopped = true;
            return arm;
        }

        // The node on the simulated twin, on a clock the test moves. The twin refuses a write NI's would block on (B2's
        // USB-6453: about 8000 samples past the generation), and Now moves the clock in 10 ms steps, each once the feeder has
        // 5000 samples written ahead, as the host's clock lets it; Jump moves it at once (an underflow, if a trial streams).
        sealed class Harness : IDisposable
        {
            double now;
            readonly Subject<ZapitDaqCommand> commands = new Subject<ZapitDaqCommand>();
            readonly BlockingCollection<ZapitDaqEvent> events = new BlockingCollection<ZapitDaqEvent>();
            readonly IDisposable subscription;
            public readonly SimulatedZapitDevice Device = new SimulatedZapitDevice { AutoTrigger = false, WriteAheadLimitSamples = 8000 };
            public readonly ZapitDaq Node;
            public Exception Error;

            // laserCeilingVolts: null is LaserMaxVolts; DefaultCeiling leaves LaserCeilingVolts unset, its default (0) as a launch without it.
            public const double DefaultCeiling = double.NaN;

            // wrap: a device over the twin, to inject a failure.
            public Harness(double sampleRate = 100000, double maxTrialSeconds = 1.0, double ceilingMw = double.PositiveInfinity, double laserMaxVolts = 5, double? laserCeilingVolts = null, long onboardSamples = 0, Func<SimulatedZapitDevice, IZapitDevice> wrap = null)
            {
                Device.Clock = () => Volatile.Read(ref now);
                Device.OnboardSamples = onboardSamples;
                var node = Node = new ZapitDaq
                {
                    SampleRate = sampleRate, MaxTrialSeconds = maxTrialSeconds,
                    TriggerTerminal = Trigger, CeilingMw = ceilingMw, LaserMaxVolts = laserMaxVolts, DeviceFactory = () => wrap == null ? Device : wrap(Device),
                };
                if (laserCeilingVolts == null) node.LaserCeilingVolts = laserMaxVolts;
                else if (!double.IsNaN(laserCeilingVolts.Value)) node.LaserCeilingVolts = laserCeilingVolts.Value;
                subscription = node.Process(commands).Subscribe(events.Add, e => Error = e);
            }

            public double Now
            {
                set
                {
                    var at = Volatile.Read(ref now);
                    do
                    {
                        at = Math.Min(value, at + 0.01);
                        Volatile.Write(ref now, at);
                        Assert.IsTrue(Device.AwaitWritesAhead(5000), $"The feeder fell behind at {at} s.");
                    }
                    while (at < value);
                }
            }

            public double Jump { set => Volatile.Write(ref now, value); }

            public void Send(ZapitDaqCommand command) => commands.OnNext(command);

            public ZapitDaqEvent Next(ZapitDaqEventKind kind)
            {
                Assert.IsTrue(events.TryTake(out var e, 5000), $"No {kind} event came.");
                Assert.AreEqual(kind, e.Kind);
                return e;
            }

            public void NothingNext() => Assert.IsFalse(events.TryTake(out var e, 50), $"Unexpected {e?.Kind}.");

            public string ErrorMessage(ZapitDaqCommand command)
            {
                Send(command);
                Assert.IsNotNull(Error, $"{command} was not refused.");
                return Error.Message;
            }

            // A refusal as an event (Refused): the session runs on.
            public string Refusal(ZapitDaqCommand command)
            {
                Send(command);
                var detail = Next(ZapitDaqEventKind.Refused).Detail;
                Assert.IsNull(Error, $"{command} stopped the session: {Error?.Message}");
                return detail;
            }

            public void Dispose() => subscription.Dispose();
        }

        static void AssertZeros(double[] volts) => CollectionAssert.AreEqual(new double[4], volts);

        static double[] Column(double[,] samples, int i) => Enumerable.Range(0, 4).Select(c => samples[c, i]).ToArray();

        [TestMethod]
        public void TrialsMatchTheAppsWaveforms()
        {
            var bank = ZapitBank.Load(Unpack());
            var expected = JObject.Parse(File.ReadAllText(Path.Combine(DataFolder, "contract_expected.json")));
            var floats = Gunzip("contract_expected.bin.gz");
            int at = 0;
            foreach (var c in expected["cases"])
            {
                int condition = (int)c["condition"], cycles = (int)c["cycles"], rows = (int)c["rows"];
                var got = bank.Assemble(condition, bank.Variant(condition, (bool)c["laser_on"], (double)c["power_mw"]), cycles);
                Assert.AreEqual(rows, got.GetLength(1), c.ToString());
                for (int row = 0; row < rows; row++)
                    for (int channel = 0; channel < 4; channel++)
                        Assert.AreEqual(BitConverter.ToSingle(floats, (at + row * 4 + channel) * 4), got[channel, row], 1e-5, $"{c} row {row} channel {channel}");
                at += rows * 4;
            }
            Assert.AreEqual(floats.Length, at * 4);
        }

        [TestMethod]
        public void ATrialArmsOnTheTriggerLineAndZeroesAtItsEnd()
        {
            using var h = new Harness();
            h.Send(Load(Unpack()));
            var bank = h.Next(ZapitDaqEventKind.BankLoaded).Bank;
            Assert.AreEqual(3, bank.Conditions.Count);
            Assert.AreEqual(2, bank.Conditions[0].PointCount);

            h.Send(Arm(1, 1, true, 8.0, 0.05));
            var armed = h.Next(ZapitDaqEventKind.Armed);
            Assert.AreEqual(30000, armed.Samples);   // 2 cycles of 2500 and the 250 ms ramp
            Assert.AreEqual(8.0, armed.PowerMw);
            Assert.IsTrue(armed.ArmSeconds >= 0);
            Assert.IsTrue(h.Device.HasClockedTask);
            Assert.AreEqual(Trigger, h.Device.LastTriggerTerminal);
            AssertZeros(h.Device.OnDemandWrites.Last());
            var buffer = h.Device.LastBuffer;
            var expected = bank.Assemble(1, bank.Variant(1, true, 8.0), 2);
            CollectionAssert.AreEqual(expected.Cast<double>().ToArray(), buffer.Cast<double>().ToArray());
            h.NothingNext();   // no trigger yet

            h.Device.Trigger();
            h.Now = 0.001;
            Assert.AreEqual(1, h.Next(ZapitDaqEventKind.Started).Trial);
            h.Now = 1;
            var ended = h.Next(ZapitDaqEventKind.Ended);
            Assert.AreEqual(30000, ended.SamplesGenerated);
            Assert.IsFalse(h.Device.HasClockedTask);
            AssertZeros(h.Device.OnDemandWrites.Last());
            AssertZeros(new[] { 0, 0, buffer[2, 29999], buffer[3, 29999] });
            Assert.IsNull(h.Error);
        }

        [TestMethod]
        public void ATrialNeverTriggeredIsStoppedByTheNextArmButAPlayingOneRefusesIt()
        {
            using var h = new Harness();
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 2, false, 0, 0.05));
            h.Next(ZapitDaqEventKind.Armed);
            h.Send(Arm(2, 2, true, 2.0, 0.05));
            Assert.AreEqual(1, h.Next(ZapitDaqEventKind.Stopped).Trial);
            Assert.AreEqual(2, h.Next(ZapitDaqEventKind.Armed).Trial);
            h.Device.Trigger();
            h.Now = 0.001;
            h.Next(ZapitDaqEventKind.Started);
            StringAssert.Contains(h.ErrorMessage(Arm(3, 1, true, 2.0, 0.05)), "Trial 3 cannot arm while trial 2 still plays");
        }

        // Audit F F1: an Arm the moment the last trial's samples are all generated raced the monitor's 1 ms poll and stopped
        // the workflow (30 of 30 probe runs); Arm now looks for itself. The Trigger command is the twin's line.
        [TestMethod]
        public void AnArmAsTheLastTrialEndsFindsItEnded()
        {
            for (int rep = 0; rep < 20; rep++)
            {
                using var h = new Harness();
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                h.Send(Arm(1, 1, true, 2.0, 0.1));
                var armed = h.Next(ZapitDaqEventKind.Armed);
                Assert.AreEqual(0.35, armed.TrialSeconds, 1e-12, "4 whole 25 ms cycles and the 250 ms ramp");
                h.Now = 1.0;
                h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Trigger });
                h.Now = 1.001;
                h.Next(ZapitDaqEventKind.Started);
                h.Now = 1.0 + armed.TrialSeconds;
                h.Send(Arm(2, 2, false, 0, 0.1));
                Assert.IsNull(h.Error, $"rep {rep}: {h.Error?.Message}");
                Assert.AreEqual(1, h.Next(ZapitDaqEventKind.Ended).Trial);
                Assert.AreEqual(2, h.Next(ZapitDaqEventKind.Armed).Trial);
            }
        }

        [TestMethod]
        public void AnEarlyStopRampsThenEndsAtZeroOnLaserAndLed()
        {
            using var h = new Harness();
            h.Send(Load(Unpack()));
            var bank = h.Next(ZapitDaqEventKind.BankLoaded).Bank;
            var variant = bank.Variant(1, true, 40.0);
            h.Send(Arm(4, 1, true, 40.0, 0));   // until the outcome: finite at the 1 s cap, streamed
            var armed = h.Next(ZapitDaqEventKind.Armed);
            Assert.AreEqual(100000, armed.Samples);
            Assert.IsTrue(armed.Streamed);
            // The first window before the start: to the last laser sample at 0 V within the 6253 the feeder keeps ahead (the
            // 10000-sample lead less the longest write, 1247 samples to a laser sample at 0 V, and the 2500-sample cycle).
            Assert.IsTrue(h.Device.SamplesWritten > 6253 - 1247 && h.Device.SamplesWritten <= 6253, $"first window {h.Device.SamplesWritten}");
            Assert.AreEqual(0, h.Device.MaxLaserAtWriteEnd);
            var whole = bank.Assemble(1, variant, 30);
            h.Device.Trigger();
            h.Now = 0.1234;
            h.Next(ZapitDaqEventKind.Started);

            h.Send(EarlyStop(3));   // another trial's: ignored
            h.Send(EarlyStop(4));
            h.NothingNext();
            // 12340 generated, the feeder 5000 to 7500 samples ahead: the ramp starts on the first cycle boundary at or after
            // the samples written, within ZapitDaq.EarlyStopLeadSeconds (10000 samples), and lasts 25000 samples.
            h.Now = 0.5;
            var stopped = h.Next(ZapitDaqEventKind.Stopped);
            Assert.AreEqual(12340, stopped.EarlyStopAt);
            long from = stopped.RampFrom;
            Assert.IsTrue(from % 2500 == 0 && from - 12340 >= 5000 && from - 12340 < 10000, $"ramp from {from}");
            Assert.IsTrue(stopped.SamplesGenerated >= from + 25000 && stopped.SamplesGenerated < 100000);
            var cut = bank.Assemble(1, variant, (int)(from / 2500));
            Assert.AreEqual(from + 25000, cut.GetLength(1));
            var buffer = h.Device.LastBuffer;
            for (int i = 0; i < from; i++) CollectionAssert.AreEqual(Column(whole, i), Column(buffer, i), $"sample {i}");
            for (long i = from; i < cut.GetLength(1); i++) CollectionAssert.AreEqual(Column(cut, (int)i), Column(buffer, (int)i), $"sample {i}");
            for (long i = from + 24999; i < h.Device.LastSamplesGenerated; i++) Assert.IsTrue(buffer[2, i] == 0 && buffer[3, i] == 0, $"Sample {i} is not dark.");
            Assert.IsTrue(Enumerable.Range((int)from, 1000).Any(i => buffer[2, i] > 0), "The ramp has no light.");
            Assert.IsTrue(h.Device.LargestStartedWrite <= 1247, $"a write of {h.Device.LargestStartedWrite} samples");
            Assert.IsTrue(h.Device.MaxLaserAtWriteEnd <= 1e-4, $"a write ended with the laser at {h.Device.MaxLaserAtWriteEnd} V");
            Assert.IsTrue(stopped.MinSamplesAhead >= 4000, $"min ahead {stopped.MinSamplesAhead}");
            Assert.IsFalse(h.Device.HasClockedTask);
            AssertZeros(h.Device.OnDemandWrites.Last());
            AssertZeros(h.Device.Outputs);
            Assert.AreEqual(from + 25000, stopped.SamplesGenerated, "nothing written past the ramp: the DAQ stops itself there");
            Assert.IsNull(h.Error);
        }

        // The early stop lands within the lead and in phase with the scan wherever in a cycle it comes, on B2's FIFO; the
        // trial is Stopped once its ramp has played, not before.
        [TestMethod]
        public void AnEarlyStopRampsWithinTheLeadOnACycleBoundaryWhereverItComes()
        {
            var path = Unpack();
            for (long at = 20000; at < 22500; at += 250)
                using (var h = new Harness(onboardSamples: 4095, maxTrialSeconds: 3))
                {
                    h.Send(Load(path));
                    h.Next(ZapitDaqEventKind.BankLoaded);
                    h.Send(ArmUntilStopped(1, 1, true, 8.0, 2.0));
                    Assert.IsTrue(h.Next(ZapitDaqEventKind.Armed).Streamed);
                    h.Device.Trigger();
                    h.Now = at / 100000.0;
                    h.Next(ZapitDaqEventKind.Started);
                    h.Send(EarlyStop(1));
                    h.NothingNext();
                    // The ramp is 25000 samples from a boundary under 10000 past `at`: no Stopped before 25000 + `at` is generated.
                    h.Now = (at + 25000) / 100000.0;
                    h.NothingNext();
                    h.Now = (at + 10000 + 25000) / 100000.0;
                    var stopped = h.Next(ZapitDaqEventKind.Stopped);
                    Assert.AreEqual(at, stopped.EarlyStopAt);
                    Assert.IsTrue(stopped.RampFrom % 2500 == 0 && stopped.RampFrom >= at && stopped.RampFrom - at < 10000, $"{at}: ramp from {stopped.RampFrom}");
                    Assert.IsTrue(stopped.SamplesGenerated >= stopped.RampFrom + 25000, $"{at}: stopped at {stopped.SamplesGenerated}");
                    Assert.IsNull(h.Error, $"{at}: {h.Error?.Message}");
                    Assert.AreEqual(stopped.RampFrom + 25000, stopped.SamplesGenerated, $"{at}: the DAQ stops itself at the ramp's end");
                    AssertZeros(h.Device.Outputs);
                }
        }

        // A trial whose end is fixed at Arm (a duration, not UntilStopped) is written whole, as it ran on B2; an EarlyStop once
        // it plays writes nothing (NI's write into its running buffer waits until those samples are generated, holding the
        // gate): its own ramp ends it where it was armed to.
        [TestMethod]
        public void ATimedTrialIsWrittenWholeAndAnEarlyStopLeavesItsOwnRamp()
        {
            using var h = new Harness(onboardSamples: 4095);
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 8.0, 0.5));
            var armed = h.Next(ZapitDaqEventKind.Armed);
            Assert.IsFalse(armed.Streamed);
            Assert.AreEqual(75000, armed.Samples);
            Assert.AreEqual(75000, h.Device.SamplesWritten);
            var before = (double[,])h.Device.LastBuffer.Clone();
            h.Device.Trigger();
            h.Now = 0.1;
            h.Next(ZapitDaqEventKind.Started);
            h.Send(EarlyStop(1));
            h.Now = 0.7;
            h.NothingNext();
            h.Now = 0.76;
            var ended = h.Next(ZapitDaqEventKind.Ended);
            Assert.AreEqual(75000, ended.SamplesGenerated);
            Assert.AreEqual(-1, ended.RampFrom);
            CollectionAssert.AreEqual(before.Cast<double>().ToArray(), h.Device.LastBuffer.Cast<double>().ToArray());
            Assert.IsNull(h.Error);
        }

        // An underflow (the DAQ's generation catches up with the feeder: NI stops it, the lines holding the last sample written,
        // which has the laser at 0 V) zeroes every line at once and is logged as the trial's Underflowed event, its last; the
        // session runs on (26 Nov 2026: zero, log, carry on), and the next trials arm and play. The session's third
        // underflow (ZapitDaq.UnderflowLimit) stops the workflow.
        [TestMethod]
        public void AnUnderflowZeroesTheLinesAndTheSessionRunsOnUntilTheThird()
        {
            Assert.AreEqual(3, ZapitDaq.UnderflowLimit);
            using var h = new Harness(onboardSamples: 4095);
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            double at = 0;
            int trial = 0;
            ZapitDaqEvent Underflow()
            {
                h.Send(Arm(++trial, 1, true, 40.0, 0));
                h.Next(ZapitDaqEventKind.Armed);
                h.Device.Trigger();
                h.Now = at + 0.05;
                h.Next(ZapitDaqEventKind.Started);
                h.Jump = at += 0.5;   // a 450 ms stall of the feeder
                var underflowed = h.Next(ZapitDaqEventKind.Underflowed);
                Assert.AreEqual(trial, underflowed.Trial);
                Assert.AreEqual(0, underflowed.MinSamplesAhead);
                Assert.IsTrue(underflowed.SamplesGenerated < underflowed.Samples);
                StringAssert.Contains(underflowed.Detail, $"ZapitDaq's trial {trial} failed at sample");
                StringAssert.Contains(underflowed.Detail, "every line was zeroed at once, the laser ending without its ramp");
                StringAssert.Contains(underflowed.Detail, "prevent the regeneration of old samples");
                Assert.AreEqual(0, h.Device.HeldAtUnderflow[2], "the DAQ held a sample with the laser on");
                Assert.IsFalse(h.Device.HasClockedTask);
                AssertZeros(h.Device.OnDemandWrites.Last());
                AssertZeros(h.Device.Outputs);
                return underflowed;
            }

            for (int n = 1; n <= 2; n++)
            {
                var underflowed = Underflow();
                Assert.AreEqual(n, underflowed.Underflows);
                StringAssert.Contains(underflowed.Detail, $"and the session runs on (underflow {n} of the 3 that stop it)");
                h.NothingNext();
                Assert.IsNull(h.Error, $"underflow {n} stopped the session: {h.Error?.Message}");
                Assert.AreEqual(n, h.Device.Underflows);
            }
            // The session ran on: a timed trial and a streamed one play to their ends, the count carried on their events.
            h.Send(Arm(++trial, 2, false, 0, 0.1));
            Assert.AreEqual(2, h.Next(ZapitDaqEventKind.Armed).Underflows);
            h.Device.Trigger();
            h.Now = at += 0.5;
            h.Next(ZapitDaqEventKind.Started);
            Assert.AreEqual(2, h.Next(ZapitDaqEventKind.Ended).Underflows);
            h.Send(ArmUntilStopped(++trial, 1, true, 8.0, 0.2));
            Assert.IsTrue(h.Next(ZapitDaqEventKind.Armed).Streamed);
            h.Device.Trigger();
            h.Now = at += 0.6;
            h.Next(ZapitDaqEventKind.Started);
            var ended = h.Next(ZapitDaqEventKind.Ended);
            Assert.AreEqual(ended.Samples, ended.SamplesGenerated);
            Assert.IsNull(h.Error, h.Error?.Message);

            var third = Underflow();
            Assert.AreEqual(3, third.Underflows);
            StringAssert.Contains(third.Detail, "and the workflow stopped at the session's underflow 3 (ZapitDaq.UnderflowLimit)");
            for (int i = 0; i < 500 && h.Error == null; i++) Thread.Sleep(10);
            Assert.IsNotNull(h.Error, "the third underflow was not an error");
            Assert.AreEqual(third.Detail, h.Error.Message);
            Assert.AreEqual(3, h.Device.Underflows);
        }

        // A device whose writes fail once Fail is set (a USB card unplugged, say): not an underflow.
        sealed class WriteFails : IZapitDevice
        {
            public readonly SimulatedZapitDevice Twin;
            public volatile bool Fail;
            public WriteFails(SimulatedZapitDevice twin) => Twin = twin;
            public void WriteOnDemand(double[] volts) => Twin.WriteOnDemand(volts);
            public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal) => Twin.ArmFinite(samples, sampleRate, triggerTerminal);
            public void CreateFinite(long samples, double sampleRate, string triggerTerminal) => Twin.CreateFinite(samples, sampleRate, triggerTerminal);
            public void Write(double[,] samples)
            {
                if (Fail) throw new InvalidOperationException("DAQmx: the device was removed.");
                Twin.Write(samples);
            }
            public void StartClocked() => Twin.StartClocked();
            public long SamplesGenerated => Twin.SamplesGenerated;
            public long OnboardSamples => Twin.OnboardSamples;
            public bool HasClockedTask => Twin.HasClockedTask;
            public bool IsDone => Twin.IsDone;
            public void StopClocked() => Twin.StopClocked();
            public double[] Staircase(double[,] samples, double sampleRate) => Twin.Staircase(samples, sampleRate);
            public void Dispose() => Twin.Dispose();
        }

        // Any other failure of a streamed trial's DAQ before its ramp's end is zeroed and logged the same way, but stops the
        // workflow at once and is not counted as an underflow.
        [TestMethod]
        public void AStreamedTrialsOtherDaqFailureIsAnErrorAtOnce()
        {
            WriteFails device = null;
            using var h = new Harness(onboardSamples: 4095, wrap: twin => device = new WriteFails(twin));
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 40.0, 0));
            h.Next(ZapitDaqEventKind.Armed);
            h.Device.Trigger();
            h.Now = 0.05;
            h.Next(ZapitDaqEventKind.Started);
            device.Fail = true;
            h.Jump = 0.06;
            var failed = h.Next(ZapitDaqEventKind.Underflowed);
            Assert.AreEqual(0, failed.Underflows);
            StringAssert.Contains(failed.Detail, "every line was zeroed at once, the laser ending without its ramp, and the workflow stopped: DAQmx: the device was removed.");
            for (int i = 0; i < 500 && h.Error == null; i++) Thread.Sleep(10);
            Assert.IsNotNull(h.Error, "the failure was not an error");
            Assert.AreEqual(failed.Detail, h.Error.Message);
            Assert.AreEqual(0, h.Device.Underflows);
            Assert.IsFalse(h.Device.HasClockedTask);
            AssertZeros(h.Device.Outputs);
        }

        // Zero (the workflow's, and Zapit-Main.LaserOff's) cuts a trial at once, written whole or streamed, started or not:
        // unlike an EarlyStop, which a timed trial ignores once started (decided 26 Nov 2026).
        [TestMethod]
        public void AZeroCutsAStartedTrialAtOnceStreamedOrWrittenWhole()
        {
            foreach (var streamed in new[] { false, true })
            {
                using var h = new Harness(onboardSamples: 4095);
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                h.Send(streamed ? ArmUntilStopped(1, 1, true, 8.0, 0.5) : Arm(1, 1, true, 8.0, 0.5));
                Assert.AreEqual(streamed, h.Next(ZapitDaqEventKind.Armed).Streamed);
                h.Device.Trigger();
                h.Now = 0.1;
                h.Next(ZapitDaqEventKind.Started);
                h.Send(EarlyStop(1));
                if (!streamed) h.NothingNext();
                h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero });
                var zeroed = h.Next(ZapitDaqEventKind.Zeroed);
                Assert.AreEqual(1, zeroed.Trial, $"streamed {streamed}");
                Assert.IsTrue(zeroed.SamplesGenerated < 20000, $"streamed {streamed}: zeroed at {zeroed.SamplesGenerated}");
                Assert.IsFalse(h.Device.HasClockedTask);
                AssertZeros(h.Device.Outputs);
                Assert.IsNull(h.Error, h.Error?.Message);
            }
        }

        // 26 Nov 2026: a DAQ that runs out of samples holds the last one written, so every write ends on a laser sample at
        // 0 V (the galvos' blanking: in B2's bank two 43-sample runs per 2500-sample cycle, whose last sample has the laser on).
        // An underflow wherever it comes, in each laser-on condition of the contract bank (condition 3's runs are 4 to 6
        // samples long), holds the laser at 0 V.
        [TestMethod]
        public void AnUnderflowAnywhereHoldsTheLaserAtZero()
        {
            var path = Unpack();
            foreach (var condition in new[] { 1, 2, 3 })
                for (double at = 0.05; at < 0.08; at += 0.0017)
                    using (var h = new Harness(onboardSamples: 4095))
                    {
                        h.Send(Load(path));
                        h.Next(ZapitDaqEventKind.BankLoaded);
                        h.Send(Arm(1, condition, true, 40.0, 0));
                        h.Next(ZapitDaqEventKind.Armed);
                        h.Device.Trigger();
                        h.Now = at;
                        h.Next(ZapitDaqEventKind.Started);
                        h.Jump = at + 0.2;
                        h.Next(ZapitDaqEventKind.Underflowed);
                        Assert.IsTrue(h.Device.HeldAtUnderflow[2] <= 1e-4, $"condition {condition} at {at} s: held the laser at {h.Device.HeldAtUnderflow[2]} V");
                        Assert.IsTrue(h.Device.MaxLaserAtWriteEnd <= 1e-4, $"condition {condition}: a write ended with the laser at {h.Device.MaxLaserAtWriteEnd} V");
                        AssertZeros(h.Device.Outputs);
                    }
        }

        // A bank whose laser cycle has no sample at 0 V cannot end its writes with the laser off: its streamed trials are
        // refused before any task exists, saying why; its timed trials (written whole) still run.
        [TestMethod]
        public void ABankWithNoLaserZeroInACycleIsRefusedForStreamedTrials()
        {
            var path = Unpack();
            var index = JObject.Parse(File.ReadAllText(path));
            var cycle = index["conditions"][2]["variants"][0]["laser_cycle"];   // condition 3 at 2 mW
            int scanRows = (int)index["scan_rows"], offset = (int)cycle["offset"], length = (int)cycle["length"];
            var bin = Path.ChangeExtension(path, ".bin");
            var raw = File.ReadAllBytes(bin);
            for (int i = 0; i < length; i++)
            {
                int at = (scanRows * 3 + offset + i) * 4;
                if (BitConverter.ToSingle(raw, at) <= 1e-4) BitConverter.GetBytes(0.01f).CopyTo(raw, at);
            }
            File.WriteAllBytes(bin, raw);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                index["hashes"]["bin"] = string.Concat(sha.ComputeHash(raw).Select(b => b.ToString("x2")));
            File.WriteAllText(path, index.ToString());
            using (var h = new Harness(onboardSamples: 4095))
            {
                h.Send(Load(path));
                h.Next(ZapitDaqEventKind.BankLoaded);
                h.Send(Arm(1, 1, true, 8.0, 0.05));
                Assert.IsFalse(h.Next(ZapitDaqEventKind.Armed).Streamed);
                var message = h.ErrorMessage(Arm(2, 1, true, 8.0, 0));
                StringAssert.Contains(message, "The Zapit bank's condition 3 at 2 mW has no laser sample at 0 V in its scan cycle, so a streamed trial's DAQ could hold the laser on");
                Assert.IsFalse(h.Device.HasClockedTask);
                AssertZeros(h.Device.Outputs);
            }
        }

        // An underflow once the ramp has played (only laser and LED at 0 V were left to write) is a clean Stopped.
        [TestMethod]
        public void AnUnderflowAfterTheRampsEndIsAStop()
        {
            using var h = new Harness(onboardSamples: 4095);
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 40.0, 0));
            h.Next(ZapitDaqEventKind.Armed);
            h.Device.Trigger();
            h.Now = 0.2;
            h.Next(ZapitDaqEventKind.Started);
            h.Send(EarlyStop(1));
            h.NothingNext();
            // Written 25000 to 27500 at the EarlyStop: the ramp from 25000 or 27500 ends at 50000 or 52500. At 48000 generated,
            // short of both, the feeder (5000 or more ahead) has written past both.
            h.Now = 0.48;
            h.NothingNext();
            h.Jump = 0.99;
            var stopped = h.Next(ZapitDaqEventKind.Stopped);
            Assert.IsTrue(stopped.SamplesGenerated >= stopped.RampFrom + 25000, $"stopped at {stopped.SamplesGenerated}, ramp from {stopped.RampFrom}");
            Assert.AreEqual(1, h.Device.Underflows);
            Assert.IsNull(h.Error, h.Error?.Message);
            AssertZeros(h.Device.OnDemandWrites.Last());
            AssertZeros(h.Device.Outputs);
        }

        // B2, 26 Nov 2026: a trial cut short ran out of samples at its ramp's end but NI's count stayed short of it, and the
        // trial never ended (no Stopped in 6 s). With all its samples written the feeder asks whether the task is done, which
        // reports the run-out: Stopped.
        [TestMethod]
        public void ACutTrialEndsThoughTheDaqsCountStaysShortOfItsEnd()
        {
            using var h = new Harness(onboardSamples: 4095);
            h.Device.UnderflowCountShortSamples = 100;
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 40.0, 0));
            h.Next(ZapitDaqEventKind.Armed);
            h.Device.Trigger();
            h.Now = 0.2;
            h.Next(ZapitDaqEventKind.Started);
            h.Send(EarlyStop(1));
            h.Now = 0.6;
            var stopped = h.Next(ZapitDaqEventKind.Stopped);
            Assert.AreEqual(stopped.RampFrom + 25000, stopped.SamplesGenerated);
            Assert.IsNull(h.Error, h.Error?.Message);
            AssertZeros(h.Device.Outputs);
        }

        // Every streamed sample comes from the trial checked at Arm: a ceiling lowered after LoadBank refuses the trial
        // before any task exists.
        [TestMethod]
        public void AStreamedTrialsSamplesAreCheckedAgainstTheCeilingAtArm()
        {
            using var h = new Harness();
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Node.LaserCeilingVolts = 0.01;
            StringAssert.Contains(h.ErrorMessage(Arm(1, 1, true, 40.0, 0)), "Trial 1's bank samples drive the laser to");
            Assert.IsFalse(h.Device.HasClockedTask);
            Assert.AreEqual(0, h.Device.SamplesWritten);
            AssertZeros(h.Device.Outputs);
        }

        [TestMethod]
        public void AnEarlyStopBeforeTheTriggerStopsAtOnce()
        {
            using var h = new Harness();
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 3, true, 2.0, 0));
            h.Next(ZapitDaqEventKind.Armed);
            h.Send(EarlyStop(1));
            Assert.AreEqual(0, h.Next(ZapitDaqEventKind.Stopped).SamplesGenerated);
            Assert.IsFalse(h.Device.HasClockedTask);
            AssertZeros(h.Device.OnDemandWrites.Last());
        }

        [TestMethod]
        public void UnsubscribingZeroesEveryLine()
        {
            var h = new Harness();
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 40.0, 0));
            h.Next(ZapitDaqEventKind.Armed);
            h.Device.Trigger();
            h.Now = 0.01;
            h.Next(ZapitDaqEventKind.Started);
            var writes = h.Device.OnDemandWrites.Count;
            h.Dispose();
            Assert.IsFalse(h.Device.HasClockedTask);
            Assert.AreEqual(writes + 1, h.Device.OnDemandWrites.Count);
            AssertZeros(h.Device.OnDemandWrites.Last());
        }

        // NI reports a generation error at Stop: the twin, its clocked task stopping with an error, as NI's does.
        sealed class StopFails : IZapitDevice
        {
            public readonly SimulatedZapitDevice Twin = new SimulatedZapitDevice();
            public void WriteOnDemand(double[] volts) => Twin.WriteOnDemand(volts);
            public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal) => Twin.ArmFinite(samples, sampleRate, triggerTerminal);
            public void CreateFinite(long samples, double sampleRate, string triggerTerminal) => Twin.CreateFinite(samples, sampleRate, triggerTerminal);
            public void Write(double[,] samples) => Twin.Write(samples);
            public void StartClocked() => Twin.StartClocked();
            public long SamplesGenerated => Twin.SamplesGenerated;
            public long OnboardSamples => Twin.OnboardSamples;
            public bool HasClockedTask => Twin.HasClockedTask;
            public bool IsDone => false;
            public void StopClocked()
            {
                var had = Twin.HasClockedTask;
                Twin.StopClocked();
                if (had) throw new InvalidOperationException("DAQmx: the generation failed.");
            }
            public double[] Staircase(double[,] samples, double sampleRate) => Twin.Staircase(samples, sampleRate);
            public void Dispose() => Twin.Dispose();
        }

        [TestMethod]
        public void UnsubscribingZeroesEveryLineEvenWhenStoppingFails()
        {
            var device = new StopFails();
            var node = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, CeilingMw = 1e9, LaserMaxVolts = 5, LaserCeilingVolts = 5, DeviceFactory = () => device };
            var commands = new Subject<ZapitDaqCommand>();
            var events = new BlockingCollection<ZapitDaqEvent>();
            var subscription = node.Process(commands).Subscribe(events.Add, e => { });
            commands.OnNext(Load(Unpack()));
            commands.OnNext(Arm(1, 1, true, 40.0, 0));
            Assert.IsTrue(device.Twin.HasClockedTask);
            var writes = device.Twin.OnDemandWrites.Count;
            try { subscription.Dispose(); } catch (InvalidOperationException) { }
            Assert.AreEqual(writes + 1, device.Twin.OnDemandWrites.Count, "No zero was written after Stop failed.");
            AssertZeros(device.Twin.OnDemandWrites.Last());
        }

        [TestMethod]
        public void ABankIsRefusedUnlessEveryHashMatches()
        {
            var path = Unpack();
            var bank = ZapitBank.Load(path);
            var conditions = Path.Combine(Path.GetDirectoryName(path), "contract_conditions.yml");
            bank.Verify(conditions, bank.Hashes["scanner_fit"], bank.Hashes["laser_curve"], bank.Hashes["sample"]);

            var other = Path.GetTempFileName();
            File.WriteAllText(other, File.ReadAllText(conditions) + "\n");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => bank.Verify(other, null, null, null)).Message, "conditions hash differs");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => bank.Verify(null, new string('0', 64), null, null)).Message, "scanner_fit hash");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => bank.Verify(null, null, null, "x")).Message, "sample hash");

            var bin = Path.ChangeExtension(path, ".bin");
            var bytes = File.ReadAllBytes(bin);
            bytes[100] ^= 1;
            File.WriteAllBytes(bin, bytes);
            using var h = new Harness();
            StringAssert.Contains(h.ErrorMessage(Load(path)), "Settings_ZapitBank_T.bin does not match the hash in Settings_ZapitBank_T.json");
        }

        [TestMethod]
        public void TrialsNeedABankBuiltForTheRigsSampleRate()
        {
            using (var h = new Harness())
                StringAssert.Contains(h.ErrorMessage(Arm(1, 1, true, 2.0, 0.05)), "no Zapit bank is loaded");
            using (var h = new Harness(sampleRate: 50000))
                StringAssert.Contains(h.ErrorMessage(Load(Unpack())), "built for 100000 samples/s; the rig runs 50000");
            using (var h = new Harness())
            {
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                StringAssert.Contains(h.ErrorMessage(Arm(1, 1, true, 5.0, 0.05)), "no condition 1 with the laser on at 5 mW");
            }
        }

        [TestMethod]
        public void EveryTrialIsFiniteAtTheCap()
        {
            var bank = ZapitBank.Load(Unpack());
            foreach (var condition in bank.Conditions)
                foreach (var variant in condition.Variants)
                {
                    var cycles = bank.Cycles(condition.Condition, variant, 0, 1.0);
                    Assert.IsTrue(bank.TrialRows(condition.Condition, variant, cycles) <= 100000);
                    Assert.IsTrue(bank.TrialRows(condition.Condition, variant, cycles + 1) > 100000);
                }
            Assert.AreEqual(30, bank.Cycles(1, bank.Variant(1, true, 2.0), 0, 1.0));   // 30 x 2500 + 25000 = the cap
            using var h = new Harness();
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            StringAssert.Contains(h.ErrorMessage(Arm(1, 1, true, 2.0, 1.0)), "longer than maxTrialSeconds (1 s)");
        }

        [TestMethod]
        public void AZeroCeilingRefusesAnyLaserSampleAboveZeroVolts()
        {
            using var h = new Harness(ceilingMw: 0);
            StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 }), "ceilingMw is 0");
        }

        [TestMethod]
        public void AZeroCeilingRefusesAStaircaseAboveZeroAndAllowsZeros()
        {
            using var h = new Harness(ceilingMw: 0);
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = 1, LaserVolts = 0 });
            h.Next(ZapitDaqEventKind.Pointed);
            StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Staircase, LaserLevelsVolts = new[] { 0.0, 1 }, SamplesPerLevel = 10 }), "ceilingMw is 0");
        }

        [TestMethod]
        public void AZeroCeilingRefusesABankThatDrivesTheLaser()
        {
            using var h = new Harness(ceilingMw: 0);
            var path = Unpack();
            var index = JObject.Parse(File.ReadAllText(path));
            index["ceiling_mw"] = 0.0;   // a bank claiming the disabled ceiling but driving the laser
            File.WriteAllText(path, index.ToString());
            StringAssert.Contains(h.Refusal(Load(path)), "but rig.yml's zapit.ceilingMw is 0 (laser disabled)");
        }

        // rig.yml's ceiling lowered after the bank was built: the bank's powers are not capped at it, so it is refused.
        [TestMethod]
        public void ABankBuiltUnderAHigherCeilingIsRefused()
        {
            using var h = new Harness(ceilingMw: 5);
            StringAssert.Contains(h.Refusal(Load(Unpack())), "rebuild the bank");
            using var zero = new Harness(ceilingMw: 0);
            StringAssert.Contains(zero.Refusal(Load(Unpack())), "rebuild the bank");
            using var same = new Harness(ceilingMw: 1e9);
            same.Send(Load(Unpack()));
            same.Next(ZapitDaqEventKind.BankLoaded);
        }

        // rig.yml's laserMinMaxControlVolts[1] is the hard limit: any laser sample above it or below 0 V is refused.
        [TestMethod]
        public void LaserVoltsOutsideZeroToTheRigsMaximumAreRefused()
        {
            using (var h = new Harness(laserMaxVolts: 1))
                StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 1.5 }), "outside 0 to 1 V");
            using (var h = new Harness(laserMaxVolts: 1))
                StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = -0.04 }), "outside 0 to 1 V");
            using (var h = new Harness(laserMaxVolts: 1))
                StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Staircase, LaserLevelsVolts = new[] { 0.0, 2 }, SamplesPerLevel = 10 }), "outside 0 to 1 V");
            using (var h = new Harness(laserMaxVolts: 0.01))
                StringAssert.Contains(h.ErrorMessage(Load(Unpack())), "The Zapit bank's samples drive the laser from");
            using (var h = new Harness(laserMaxVolts: 1))
            {
                h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 1 });
                h.Next(ZapitDaqEventKind.Pointed);
            }
        }

        // A ZapitDaq whose mappings were lost (the defaults) drives no laser: CeilingMw 0 and LaserMaxVolts 0.
        [TestMethod]
        public void TheDefaultsDisableTheLaser()
        {
            var device = new SimulatedZapitDevice { AutoTrigger = false };
            Exception error = null;
            var commands = new Subject<ZapitDaqCommand>();
            using (new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, DeviceFactory = () => device }.Process(commands).Subscribe(_ => { }, e => error = e))
                commands.OnNext(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 });
            StringAssert.Contains(error?.Message, "ceilingMw is 0");
        }
        // Lab constants, not rig.yml settings (26 Nov 2026): NI's 100 us trigger filter (B2's PFI0 EMI spikes are about 10 ns; the
        // trigger is a level held about 1 s) and the 0.1 s early-stop lead that decision-making's TrialRules reads too. The twin
        // has no filter, so only NI's device applies it: this pins the value it applies.
        [TestMethod]
        public void TheTriggerFilterAndEarlyStopLeadAreLabConstants()
        {
            Assert.AreEqual(1e-4, ZapitDaq.TriggerFilterSeconds);
            Assert.AreEqual(0.1, ZapitDaq.EarlyStopLeadSeconds);
        }

        [TestMethod]
        public void CalibrationWritesPointsAndReadsTheStaircaseOnTheAoClock()
        {
            using var h = new Harness();
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = 1.5, GalvoYVolts = -2, LaserVolts = 0.3 });
            h.Next(ZapitDaqEventKind.Pointed);
            CollectionAssert.AreEqual(new[] { 1.5, -2, 0.3, 0 }, h.Device.OnDemandWrites.Last());

            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Staircase, LaserLevelsVolts = new[] { 0.0, 1, 2 }, SamplesPerLevel = 10 });
            var read = h.Next(ZapitDaqEventKind.StaircaseRead);
            Assert.AreEqual(31, read.AnalogIn.Length);
            Assert.AreEqual(0.5, read.AnalogIn[10]);
            Assert.AreEqual(1.0, read.AnalogIn[29]);
            Assert.AreEqual(0.0, read.AnalogIn[30]);   // the staircase ends with the laser at 0 V
            AssertZeros(h.Device.OnDemandWrites.Last());

            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 2.0, 0.05));
            h.Next(ZapitDaqEventKind.Armed);
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 });
            StringAssert.Contains(h.Next(ZapitDaqEventKind.Refused).Detail, "no Point once trials are armed");
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero });
            h.Next(ZapitDaqEventKind.Zeroed);
            // Between trials: the staircase is held only to LaserMaxVolts, not the ceiling's volts, so it is refused after Go.
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Staircase, LaserLevelsVolts = new[] { 0.0, 1 }, SamplesPerLevel = 10 });
            StringAssert.Contains(h.Next(ZapitDaqEventKind.Refused).Detail, "no Staircase once trials are armed");
            Assert.IsNull(h.Error);
        }

        // Audit 24 Nov 2026 L1: rig.yml's ceilingMw as volts (LaserCeilingVolts) is the DAQ's limit, below laserMaxVolts.
        [TestMethod]
        public void LaserVoltsAboveTheCeilingsVoltsAreRefused()
        {
            using (var h = new Harness(ceilingMw: 1, laserCeilingVolts: 0.5))
                StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = 1, LaserVolts = 5 }), "above the ceiling's 0.5 V");
            using (var h = new Harness(laserCeilingVolts: 0.5))
                StringAssert.Contains(h.Refusal(Load(Unpack())), "above the ceiling's 0.5 V");
            using (var h = new Harness(laserCeilingVolts: 0.5))
            {
                h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.5 });
                h.Next(ZapitDaqEventKind.Pointed);
            }
        }

        // Audit 24 Nov 2026 L1: a bank's ceiling_mw is its own claim; a json edited to claim a lower one (the bin untouched)
        // still drives its peaks, which the rig's ceiling refuses as volts (ceilingMw through the laser curve).
        [TestMethod]
        public void ABankClaimingALowerCeilingThanItDeliversIsRefused()
        {
            var path = Unpack();
            var index = JObject.Parse(File.ReadAllText(path));
            index["ceiling_mw"] = 0.5;
            File.WriteAllText(path, index.ToString());
            using var h = new Harness(ceilingMw: 0.5, laserCeilingVolts: 0.01);
            StringAssert.Contains(h.Refusal(Load(path)), "above the ceiling's 0.01 V");
        }

        // Audit F F2: ceilingMw is a peak power and a variant's powers are time-averaged per point, so the old check of the
        // capped average against the ceiling never fired. LoadBank compares like with like: the bank's ceiling with the rig's
        // (peak), and each variant's delivered average with its asked average.
        [TestMethod]
        public void LoadBankComparesPeakWithPeakAndAverageWithAverage()
        {
            using (var h = new Harness(ceilingMw: 2.0))   // the fixture's 27 mW average under a 1e9 mW peak ceiling
                StringAssert.Contains(h.Refusal(Load(Unpack())), "built under a 1000000000 mW ceiling, above rig.yml's zapit.ceilingMw (2 mW)");
            var path = Unpack();
            var index = JObject.Parse(File.ReadAllText(path));
            index["conditions"][0]["variants"][0]["capped_power_mw"] = 3.0;   // asked 2 mW
            File.WriteAllText(path, index.ToString());
            using (var h = new Harness())
                StringAssert.Contains(h.Refusal(Load(path)), "delivers 3 mW on average where 2 mW was asked");
        }

        // Audit C R1: the staircase measures the laser curve LaserCeilingVolts comes from, so LaserCeilingVolts (even its
        // default 0, as the laser-curve calibration workflow's node has) does not hold it; ceilingMw 0 and LaserMaxVolts still do.
        [TestMethod]
        public void TheStaircaseCoversTheLaserRangeAboveTheCeilingsVolts()
        {
            var full = new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Staircase, LaserLevelsVolts = new[] { 0.0, 1, 2.5, 5 }, SamplesPerLevel = 10 };
            foreach (var ceiling in new[] { Harness.DefaultCeiling, 0.0, 2.0 })
                using (var h = new Harness(laserCeilingVolts: ceiling))
                {
                    h.Send(full);
                    Assert.AreEqual(41, h.Next(ZapitDaqEventKind.StaircaseRead).AnalogIn.Length);
                    Assert.IsNull(h.Error, $"LaserCeilingVolts {ceiling}: {h.Error?.Message}");
                }
            using (var h = new Harness(ceilingMw: 0, laserCeilingVolts: Harness.DefaultCeiling))
                StringAssert.Contains(h.ErrorMessage(full), "ceilingMw is 0");
            using (var h = new Harness(laserMaxVolts: 4, laserCeilingVolts: Harness.DefaultCeiling))
                StringAssert.Contains(h.ErrorMessage(full), "outside 0 to 4 V");
        }

        // Audit C R1: at the default LaserCeilingVolts (0, a launch without it) an aim and a trial's bank stay refused.
        [TestMethod]
        public void TheDefaultCeilingVoltsRefuseAimsAndBanks()
        {
            using (var h = new Harness(laserCeilingVolts: Harness.DefaultCeiling))
                StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 }), "above the ceiling's 0 V");
            using (var h = new Harness(laserCeilingVolts: Harness.DefaultCeiling))
            {
                h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = 1, LaserVolts = 0 });
                h.Next(ZapitDaqEventKind.Pointed);
                // Audit C R6: the bank is Refused (an event), the session runs on, and a trial then has no bank.
                StringAssert.Contains(h.Refusal(Load(Unpack())), "above the ceiling's 0 V");
                StringAssert.Contains(h.ErrorMessage(Arm(1, 1, true, 2.0, 0.05)), "no Zapit bank is loaded");
            }
        }

        // Audit 24 Nov 2026 L3: a subscriber that disposes the node inside an event (Take(1) on BankLoaded) must not
        // deadlock against the monitor thread, and the lines are zeroed.
        [TestMethod]
        public void DisposingInsideAnEventNeitherHangsNorLeavesTheLinesOn()
        {
            var path = Unpack();
            for (int i = 0; i < 5; i++)
            {
                var device = new SimulatedZapitDevice();
                var commands = new Subject<ZapitDaqCommand>();
                var node = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, CeilingMw = 1e9, LaserMaxVolts = 5, LaserCeilingVolts = 5, DeviceFactory = () => device };
                System.Reactive.Linq.Observable.Take(node.Process(commands), 1).Subscribe(_ => { }, _ => { });
                var send = System.Threading.Tasks.Task.Run(() => commands.OnNext(Load(path)));
                Assert.IsTrue(send.Wait(3000), $"Run {i}: LoadBank never returned.");
                AssertZeros(device.OnDemandWrites.Last());
            }
        }

        // Audit C R2, 26 Nov 2026, and streaming: a streamed trial keeps the lead (the constant ZapitDaq.EarlyStopLeadSeconds,
        // 10000 samples at 100 kS/s) less its longest write (1247 samples, to a laser sample at 0 V: contract condition 3) and
        // the bank's longest scan cycle (2500) written ahead, 6253,
        // which must cover the onboard FIFO plus a 20 ms (2000-sample) margin. A DAQ whose FIFO is too big (an X-series card's
        // 8191 samples, or 7500, or 5001, one over) is refused at the bank's first streamed trial, until-outcome or until
        // stopped, before its task starts, the lines zeroed; the message names the FIFO, the cycle and the constant, not rig.yml.
        [DataTestMethod]
        [DataRow(8191L, false, "plus a 2000-sample margin (10191 samples)", "at least 0.13938 s")]
        [DataRow(7500L, true, "plus a 2000-sample margin (9500 samples)", "at least 0.13247 s")]
        [DataRow(4254L, false, "plus a 2000-sample margin (6254 samples)", "at least 0.10001 s")]
        public void AnOnboardBufferTooBigForTheLeadIsRefusedAtTheFirstStreamedArm(long onboard, bool untilStopped, string minimum, string raise)
        {
            using (var h = new Harness(onboardSamples: onboard, maxTrialSeconds: 3))
            {
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                var message = h.ErrorMessage(untilStopped ? ArmUntilStopped(1, 1, true, 8.0, 2.0) : Arm(1, 1, true, 8.0, 0));
                StringAssert.Contains(message, "A streamed Zapit trial keeps 6253 samples written ahead of the DAQ: the early-stop lead (10000 samples, lib-open-rigs' ZapitDaq.EarlyStopLeadSeconds, 0.1 s at 100000 samples/s)");
                StringAssert.Contains(message, "less its longest write (1247 samples, to a laser sample at 0 V) and the bank's longest scan cycle (2500 samples)");
                StringAssert.Contains(message, $"under the DAQ's onboard buffer ({onboard} samples per channel) {minimum}");
                StringAssert.Contains(message, $"ZapitDaq.EarlyStopLeadSeconds must be raised to {raise}");
                Assert.IsFalse(message.Contains("rig.yml"), message);
                Assert.IsFalse(h.Device.HasClockedTask);
                Assert.AreEqual(0, h.Device.SamplesGenerated);
                AssertZeros(h.Device.OnDemandWrites.Last());
                AssertZeros(h.Device.Outputs);
            }
        }

        // A trial written whole never streams, so it needs no lead: 8191 on board arms it.
        [TestMethod]
        public void ATimedTrialNeedsNoLead()
        {
            using var h = new Harness(onboardSamples: 8191, maxTrialSeconds: 3);
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 8.0, 0.05));
            Assert.IsFalse(h.Next(ZapitDaqEventKind.Armed).Streamed);
            Assert.IsNull(h.Error, h.Error?.Message);
        }

        // Rig, 25 Nov 2026: the lab's USB-6453 holds 4095 samples per channel on board; with 40 Hz banks (2500-sample cycles at
        // 100 kS/s) a streamed trial keeps 6253 ahead, over 4095 + 2000 (and 4253 on board just passes); timed trials arm
        // whole, and the check runs at the bank's first streamed trial.
        [DataTestMethod]
        [DataRow(4095L)]
        [DataRow(4253L)]
        public void TheLabsUsb6453PassesTheLead(long onboard)
        {
            using (var h = new Harness(onboardSamples: onboard, maxTrialSeconds: 3))
            {
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                h.Send(Arm(1, 1, true, 8.0, 0.05));
                Assert.IsFalse(h.Next(ZapitDaqEventKind.Armed).Streamed);
                h.Send(Arm(2, 1, true, 8.0, 0));
                h.Next(ZapitDaqEventKind.Stopped);
                Assert.IsTrue(h.Next(ZapitDaqEventKind.Armed).Streamed);
                h.Send(ArmUntilStopped(3, 2, false, 0, 1.0));
                h.Next(ZapitDaqEventKind.Stopped);
                Assert.IsTrue(h.Next(ZapitDaqEventKind.Armed).Streamed);
                Assert.IsNull(h.Error, h.Error?.Message);
            }
        }

        // A streamed trial not yet triggered holds its first window only; the trigger then streams it in 500-sample writes.
        [TestMethod]
        public void AStreamedTrialWaitsOnItsTriggerWithItsFirstWindowWritten()
        {
            using var h = new Harness(onboardSamples: 4095);
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 3, false, 0, 0));
            h.Next(ZapitDaqEventKind.Armed);
            Thread.Sleep(50);
            Assert.AreEqual(6253, h.Device.SamplesWritten, "laser off: every sample at 0 V");
            h.Now = 0.3;
            Assert.AreEqual(6253, h.Device.SamplesWritten, "laser off: every sample at 0 V");
            h.NothingNext();
            h.Device.Trigger();
            h.Now = 0.31;
            h.Next(ZapitDaqEventKind.Started);
            h.Now = 1.4;
            var ended = h.Next(ZapitDaqEventKind.Ended);
            Assert.AreEqual(ended.Samples, ended.SamplesGenerated);
            Assert.IsTrue(h.Device.LargestStartedWrite <= 1247);
            Assert.IsNull(h.Error, h.Error?.Message);
        }

        [TestMethod]
        public void TheTwinStreamsAsNiDoes()
        {
            double now = 0;
            var device = new SimulatedZapitDevice { AutoTrigger = false, Clock = () => now, WriteAheadLimitSamples = 300 };
            device.CreateFinite(1000, 1000, Trigger);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.StartClocked()).Message, "no samples written");
            var ones = new double[4, 400];
            for (int i = 0; i < 400; i++) ones[2, i] = 1;
            device.Write(ones);   // before the start: no limit
            device.StartClocked();
            Assert.AreEqual(0, device.SamplesGenerated);
            device.Trigger();
            now = 0.2;
            Assert.AreEqual(200, device.SamplesGenerated);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.Write(new double[4, 101])).Message, "NI's write would block");
            device.Write(new double[4, 100]);
            CollectionAssert.AreEqual(new double[] { 0, 0, 1, 0 }, device.Outputs);
            now = 0.6;   // generation passes the 500 written: it stops there, holding the last sample
            Assert.AreEqual(500, device.SamplesGenerated);
            Assert.AreEqual(1, device.Underflows);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.IsDone).Message, "prevent the regeneration of old samples");
            Assert.ThrowsException<InvalidOperationException>(() => device.Write(new double[4, 10]));
            device.StopClocked();   // reported already
            CollectionAssert.AreEqual(new double[4], device.Outputs, "the last sample written was 0 V");

            device.CreateFinite(10, 1000, Trigger);
            device.Write(new double[4, 5]);
            device.StartClocked();
            device.Trigger();
            now = 0.65;   // runs out unseen: NI's Stop reports it, and an on-demand write zeroes the lines all the same
            Assert.ThrowsException<InvalidOperationException>(() => device.WriteOnDemand(new double[4]));
            Assert.IsFalse(device.HasClockedTask);
            AssertZeros(device.OnDemandWrites.Last());
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.Write(new double[4, 1])).Message, "No streamed Zapit task");
        }

        [TestMethod]
        public void TheTwinRefusesWhatNiRefuses()
        {
            var device = new SimulatedZapitDevice { AutoTrigger = false };
            var samples = new double[4, 10];
            device.ArmFinite(samples, 1000, Trigger);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.ArmFinite(samples, 1000, Trigger)).Message, "reserved by a clocked task");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.CreateFinite(10, 1000, Trigger)).Message, "reserved by a clocked task");
            Assert.ThrowsException<InvalidOperationException>(() => device.Staircase(samples, 1000));
            Assert.ThrowsException<InvalidOperationException>(() => device.Write(samples));   // not a streamed task
            device.WriteOnDemand(new double[4]);   // the on-demand task takes the lines: the clocked task is deleted
            Assert.AreEqual(1, device.ClockedTasksDeleted);
            Assert.IsFalse(device.HasClockedTask);

            double now = 0;
            device.Clock = () => now;
            device.ArmFinite(samples, 1000, Trigger);
            device.Trigger();
            now = 0.005;
            Assert.AreEqual(5, device.SamplesGenerated);
            Assert.ThrowsException<InvalidOperationException>(() => device.WriteOnDemand(new[] { 0, 0, 10.5, 0 }));
            now = 1;
            Assert.IsTrue(device.IsDone);
            device.StopClocked();
            device.CreateFinite(10, 1000, Trigger);
            Assert.ThrowsException<InvalidOperationException>(() => device.Write(new[,] { { 0.0 }, { 0 }, { 10.5 }, { 0 } }));
            Assert.ThrowsException<InvalidOperationException>(() => device.Write(new double[4, 11]));   // past the task's end
        }

        // Play Condition (ZapitPlay, before Go): trial 0 plays as a task's trial, leaves Point open after it, and a Point,
        // Staircase or LoadBank while it is armed is Refused, never an error.
        [TestMethod]
        public void PlayConditionsTrialLeavesAimingOpenAndRefusesSetupCommandsWhileArmed()
        {
            using var h = new Harness();
            var path = Unpack();
            h.Send(Load(path));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(ZapitPlay.SetupTrial, 2, true, 2.0, 0.05));
            h.Next(ZapitDaqEventKind.Armed);
            StringAssert.Contains(h.Refusal(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 }), "cannot Point while trial 0 is armed");
            StringAssert.Contains(h.Refusal(Load(path)), "cannot LoadBank while trial 0 is armed");
            StringAssert.Contains(h.Refusal(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Staircase, LaserLevelsVolts = new[] { 0.0, 1 }, SamplesPerLevel = 10 }), "cannot Staircase while trial 0 is armed");
            Assert.IsTrue(h.Device.HasClockedTask);   // the armed trial untouched
            h.Device.Trigger();
            h.Now = 0.001;
            Assert.AreEqual(0, h.Next(ZapitDaqEventKind.Started).Trial);
            h.Now = 1;
            h.Next(ZapitDaqEventKind.Ended);
            AssertZeros(h.Device.OnDemandWrites.Last());
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, GalvoXVolts = 1, LaserVolts = 0.3 });
            h.Next(ZapitDaqEventKind.Pointed);   // aiming stays open after a play
            h.Send(Arm(1, 1, true, 2.0, 0.05));
            h.Next(ZapitDaqEventKind.Armed);
            h.Send(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero });
            h.Next(ZapitDaqEventKind.Zeroed);
            StringAssert.Contains(h.Refusal(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 }), "no Point once trials are armed");
            Assert.IsNull(h.Error);
        }

        // ZapitPlay's steps for requests and ZapitDaq's events, the node driven by hand.
        sealed class PlayHarness : IDisposable
        {
            public readonly Subject<string> Requests = new Subject<string>();
            public readonly Subject<ZapitDaqEvent> Events = new Subject<ZapitDaqEvent>();
            public readonly System.Collections.Generic.List<ZapitSetupStep> Steps = new System.Collections.Generic.List<ZapitSetupStep>();
            public Exception Error;
            readonly IDisposable subscription;

            public PlayHarness(double ceilingMw = 30, double maxTrialSeconds = 1)
            {
                subscription = new ZapitPlay { CeilingMw = ceilingMw, MaxTrialSeconds = maxTrialSeconds }.Process(Requests, Events).Subscribe(Steps.Add, e => Error = e);
            }

            public ZapitSetupStep Ask(string request)
            {
                Requests.OnNext(request);
                return Steps.Last();
            }

            public void Loaded() => Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.BankLoaded, Bank = ZapitBank.Load(Unpack()) });

            public void Dispose() => subscription.Dispose();
        }

        [TestMethod]
        public void PlayArmsTheConditionAsATrialAndReportsItsStages()
        {
            using var p = new PlayHarness();
            StringAssert.Contains(p.Ask("1,2,0.05").Detail, "no Zapit bank is loaded");
            p.Loaded();
            var arming = p.Ask("2,2.0,0.05");
            Assert.AreEqual("Arming", arming.Stage);
            var arm = arming.Command;
            Assert.AreEqual(ZapitDaqCommandKind.Arm, arm.Kind);
            Assert.AreEqual(ZapitPlay.SetupTrial, arm.Trial);
            Assert.AreEqual(2, arm.Condition);
            Assert.IsTrue(arm.LaserOn);
            Assert.AreEqual(2.0, arm.PowerMw);
            Assert.AreEqual(0.05, arm.DurationSeconds);
            Assert.IsFalse(arm.UntilStopped);   // written whole: its end is fixed at Arm
            StringAssert.Contains(p.Ask("1,2,0.05").Detail, "condition 2 is still armed or playing");
            p.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Armed, Trial = 0 });
            Assert.AreEqual("Armed 2", p.Steps.Last().ToString());
            p.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Started, Trial = 0 });
            Assert.AreEqual("Playing 2", p.Steps.Last().ToString());
            p.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Started, Trial = 3 });   // not the play's
            Assert.AreEqual("Playing 2", p.Steps.Last().ToString());
            p.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Ended, Trial = 0 });
            Assert.AreEqual("Ended 2 Ended", p.Steps.Last().ToString());
            Assert.AreEqual("Arming", p.Ask("1,8,0.05").Stage);
            p.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Zeroed });   // LaserOff ends it
            Assert.AreEqual("Ended 1 Zeroed", p.Steps.Last().ToString());
            Assert.IsNull(p.Error);
        }

        [TestMethod]
        public void PlayRefusesWhatTheBankOrTheRigCannotPlayAsAStep()
        {
            using var p = new PlayHarness(ceilingMw: 5);
            p.Loaded();
            StringAssert.Contains(p.Ask("1,2").Detail, "a play is condition,powerMw,seconds");
            StringAssert.Contains(p.Ask("1.5,2,0.05").Detail, "a play is condition,powerMw,seconds");
            StringAssert.Contains(p.Ask("4,2,0.05").Detail, "no condition 4");
            StringAssert.Contains(p.Ask("1,3,0.05").Detail, "with the laser on at 3 mW");
            StringAssert.Contains(p.Ask("1,8,0.05").Detail, "at most ceilingMw (5 mW)");
            StringAssert.Contains(p.Ask("1,2,0").Detail, "more than 0 s");
            StringAssert.Contains(p.Ask("1,2,5").Detail, "longer than maxTrialSeconds (1 s)");
            Assert.IsTrue(p.Steps.All(s => s.Stage == "Refused" && s.Command == null));
            Assert.IsNull(p.Error);
            using var off = new PlayHarness(ceilingMw: 0);
            off.Loaded();
            StringAssert.Contains(off.Ask("1,2,0.05").Detail, "laser disabled");
        }

        [TestMethod]
        public void GoWithAPlayArmedZeroesTheLines()
        {
            using var p = new PlayHarness();
            p.Loaded();
            p.Requests.OnCompleted();   // Go with nothing armed: nothing
            Assert.AreEqual(0, p.Steps.Count);
            using var q = new PlayHarness();
            q.Loaded();
            q.Ask("1,2,0.05");
            q.Requests.OnCompleted();
            Assert.AreEqual("Off Go", q.Steps.Last().ToString());
            Assert.AreEqual(ZapitDaqCommandKind.Zero, q.Steps.Last().Command.Kind);
        }
    }
}
