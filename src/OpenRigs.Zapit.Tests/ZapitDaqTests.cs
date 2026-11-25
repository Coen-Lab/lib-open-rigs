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

        // The contract bank (lib_zapit's contract fixture, Data/make_fixture.py) unpacked into a new folder.
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

        // The node on the simulated twin, on a clock the test moves.
        sealed class Harness : IDisposable
        {
            double now;
            readonly Subject<ZapitDaqCommand> commands = new Subject<ZapitDaqCommand>();
            readonly BlockingCollection<ZapitDaqEvent> events = new BlockingCollection<ZapitDaqEvent>();
            readonly IDisposable subscription;
            public readonly SimulatedZapitDevice Device = new SimulatedZapitDevice { AutoTrigger = false };
            public Exception Error;

            // laserCeilingVolts: null is LaserMaxVolts; DefaultCeiling leaves LaserCeilingVolts unset, its default (0) as a launch without it.
            public const double DefaultCeiling = double.NaN;

            public Harness(double sampleRate = 100000, double maxTrialSeconds = 1.0, double earlyStopLeadSeconds = 0.05, double ceilingMw = double.PositiveInfinity, double laserMaxVolts = 5, double? laserCeilingVolts = null, long onboardSamples = 0, double triggerFilterSeconds = 0.001)
            {
                Device.Clock = () => Volatile.Read(ref now);
                Device.OnboardSamples = onboardSamples;
                var node = new ZapitDaq
                {
                    SampleRate = sampleRate, MaxTrialSeconds = maxTrialSeconds, EarlyStopLeadSeconds = earlyStopLeadSeconds, TriggerFilterSeconds = triggerFilterSeconds,
                    TriggerTerminal = Trigger, CeilingMw = ceilingMw, LaserMaxVolts = laserMaxVolts, DeviceFactory = () => Device,
                };
                if (laserCeilingVolts == null) node.LaserCeilingVolts = laserMaxVolts;
                else if (!double.IsNaN(laserCeilingVolts.Value)) node.LaserCeilingVolts = laserCeilingVolts.Value;
                subscription = node.Process(commands).Subscribe(events.Add, e => Error = e);
            }

            public double Now { set => Volatile.Write(ref now, value); }

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
            h.Send(Arm(4, 1, true, 40.0, 0));   // until the outcome: finite at the 1 s cap
            Assert.AreEqual(100000, h.Next(ZapitDaqEventKind.Armed).Samples);
            var before = (double[,])h.Device.LastBuffer.Clone();
            h.Device.Trigger();
            h.Now = 0.1234;
            h.Next(ZapitDaqEventKind.Started);

            h.Send(EarlyStop(3));   // another trial's: ignored
            h.Send(EarlyStop(4));
            h.NothingNext();
            // 12340 generated, 50 ms lead: the ramp starts on the next cycle boundary, 17500, and lasts 25000 samples.
            var buffer = h.Device.LastBuffer;
            var ramp = bank.Assemble(1, variant, 7, 17500);
            Assert.AreEqual(25000, ramp.GetLength(1));
            for (int i = 0; i < 17500; i++) CollectionAssert.AreEqual(Column(before, i), Column(buffer, i));
            for (int i = 0; i < 25000; i++) CollectionAssert.AreEqual(Column(ramp, i), Column(buffer, 17500 + i));
            for (int i = 42499; i < 100000; i++) Assert.IsTrue(buffer[2, i] == 0 && buffer[3, i] == 0, $"Sample {i} is not dark.");
            Assert.IsTrue(Enumerable.Range(17500, 1000).Any(i => buffer[2, i] > 0), "The ramp has no light.");

            h.Now = 0.45;
            var stopped = h.Next(ZapitDaqEventKind.Stopped);
            Assert.IsTrue(stopped.SamplesGenerated >= 42500 && stopped.SamplesGenerated < 100000);
            Assert.IsFalse(h.Device.HasClockedTask);
            AssertZeros(h.Device.OnDemandWrites.Last());
            Assert.IsNull(h.Error);
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
            public void ArmFinite(double[,] samples, double sampleRate, string triggerTerminal, double triggerFilterSeconds) => Twin.ArmFinite(samples, sampleRate, triggerTerminal, triggerFilterSeconds);
            public void WriteAt(double[,] samples, long firstSample) => Twin.WriteAt(samples, firstSample);
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
            var node = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, EarlyStopLeadSeconds = 0.05, CeilingMw = 1e9, LaserMaxVolts = 5, LaserCeilingVolts = 5, DeviceFactory = () => device };
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
            using (new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, EarlyStopLeadSeconds = 0.01, DeviceFactory = () => device }.Process(commands).Subscribe(_ => { }, e => error = e))
                commands.OnNext(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point, LaserVolts = 0.3 });
            StringAssert.Contains(error?.Message, "ceilingMw is 0");
        }
        [DataTestMethod]
        [DataRow(0.001)]
        [DataRow(0.0)]
        [DataRow(0.005)]
        public void TheTriggerFilterWidthReachesTheDevice(double seconds)
        {
            using var h = new Harness(triggerFilterSeconds: seconds);
            h.Send(Load(Unpack()));
            h.Next(ZapitDaqEventKind.BankLoaded);
            h.Send(Arm(1, 1, true, 8.0, 0.05));
            h.Next(ZapitDaqEventKind.Armed);
            Assert.AreEqual(seconds, h.Device.LastTriggerFilterSeconds);
        }

        [TestMethod]
        public void TheTriggerFilterDefaultsToOneMillisecond() => Assert.AreEqual(0.001, new ZapitDaq().TriggerFilterSeconds);

        [DataTestMethod]
        [DataRow(-0.001)]
        [DataRow(0.01)]
        [DataRow(1.0)]
        [DataRow(double.NaN)]
        public void ATriggerFilterOutOfRangeIsRefusedNamingTheField(double seconds)
        {
            Exception error = null;
            var node = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, EarlyStopLeadSeconds = 0.05, TriggerFilterSeconds = seconds, DeviceFactory = () => new SimulatedZapitDevice() };
            using (node.Process(new Subject<ZapitDaqCommand>()).Subscribe(_ => { }, e => error = e)) { }
            StringAssert.Contains(error?.Message, "zapit.triggerFilterSeconds");
            Assert.ThrowsException<InvalidOperationException>(() => new SimulatedZapitDevice().ArmFinite(new double[4, 10], 1000, Trigger, seconds));
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
                var node = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 1, EarlyStopLeadSeconds = 0.01, CeilingMw = 1e9, LaserMaxVolts = 5, LaserCeilingVolts = 5, DeviceFactory = () => device };
                System.Reactive.Linq.Observable.Take(node.Process(commands), 1).Subscribe(_ => { }, _ => { });
                var send = System.Threading.Tasks.Task.Run(() => commands.OnNext(Load(path)));
                Assert.IsTrue(send.Wait(3000), $"Run {i}: LoadBank never returned.");
                AssertZeros(device.OnDemandWrites.Last());
            }
        }

        // Audit C R2: a lead too short for the onboard FIFO (an X-series AO FIFO of 8191 shared by 4 lines) and one scan cycle
        // is refused at the first trial that can stop early, naming the setting and its minimum, with the lines zeroed; a
        // timed trial never stops early, so it is not held to it.
        [TestMethod]
        public void ALeadTooShortForTheOnboardBufferIsRefusedAtTheFirstUntilOutcomeArm()
        {
            using (var h = new Harness(earlyStopLeadSeconds: 0.02, onboardSamples: 2047))
            {
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                h.Send(Arm(1, 1, true, 8.0, 0.05));
                h.Next(ZapitDaqEventKind.Armed);
                var message = h.ErrorMessage(Arm(2, 1, true, 8.0, 0));
                StringAssert.Contains(message, "earlyStopLeadSeconds (0.02 s, 2000 samples)");
                StringAssert.Contains(message, "onboard buffer (2047 samples per channel)");
                StringAssert.Contains(message, "above 0.04547 s");   // 2047 + one 2500-sample cycle at 100 kS/s
                Assert.IsFalse(h.Device.HasClockedTask);
                AssertZeros(h.Device.OnDemandWrites.Last());
            }
        }

        // Rig, 25 Nov 2026: the lab's USB-6453 holds 4095 samples per channel on board, so with 40 Hz banks (2500-sample cycles at
        // 100 kS/s) the lead must pass 0.06595 s. 0.05, the value once given as the default, was refused, its message naming an
        // onboard buffer of 0 (read after the task was deleted); 0.08, the lab's lead, passes.
        [TestMethod]
        public void TheLabsLeadPassesItsOnboardBufferAndTheRefusalNamesThatBuffer()
        {
            using (var h = new Harness(earlyStopLeadSeconds: 0.05, onboardSamples: 4095, maxTrialSeconds: 3))
            {
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                var message = h.ErrorMessage(Arm(1, 1, true, 8.0, 0));
                StringAssert.Contains(message, "onboard buffer (4095 samples per channel) plus one scan cycle (2500 samples): above 0.06595 s");
            }
            using (var h = new Harness(earlyStopLeadSeconds: 0.08, onboardSamples: 4095, maxTrialSeconds: 3))
            {
                h.Send(Load(Unpack()));
                h.Next(ZapitDaqEventKind.BankLoaded);
                h.Send(Arm(1, 1, true, 8.0, 0));
                h.Next(ZapitDaqEventKind.Armed);
                Assert.IsNull(h.Error, h.Error?.Message);
            }
        }

        // Audit C R2 (probe Q2): with a lead that passes the check, no early stop over one whole scan cycle errors.
        [TestMethod]
        public void NoEarlyStopErrorsOnceTheLeadPassesTheCheck()
        {
            var path = Unpack();
            int cycle = ZapitBank.Load(path).Conditions[0].ScanCycle.Length;
            for (long generated = 5000; generated < 5000 + cycle; generated += cycle / 50)
                using (var h = new Harness(earlyStopLeadSeconds: 0.05, onboardSamples: 2047, maxTrialSeconds: 3))
                {
                    h.Send(Load(path));
                    h.Next(ZapitDaqEventKind.BankLoaded);
                    h.Send(Arm(1, 1, true, 2.0, 0));
                    h.Next(ZapitDaqEventKind.Armed);
                    h.Device.Trigger();
                    h.Now = generated / 100000.0;
                    h.Next(ZapitDaqEventKind.Started);
                    h.Send(EarlyStop(1));
                    Assert.IsNull(h.Error, $"{generated} generated: {h.Error?.Message}");
                }
        }

        // Audit C R2: the device-level check stays: a ramp written inside the FIFO is an error, never a cut ramp.
        [TestMethod]
        public void TheTwinRefusesAWriteInsideTheOnboardBuffer()
        {
            double now = 0;
            var device = new SimulatedZapitDevice { AutoTrigger = false, OnboardSamples = 100, Clock = () => now };
            device.ArmFinite(new double[4, 1000], 1000, Trigger, 0);
            device.Trigger();
            now = 0.1;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.WriteAt(new double[4, 10], 150)).Message, "earlyStopLeadSeconds");
            device.WriteAt(new double[4, 10], 200);
        }

        [TestMethod]
        public void TheTwinRefusesWhatNiRefuses()
        {
            var device = new SimulatedZapitDevice { AutoTrigger = false };
            var samples = new double[4, 10];
            device.ArmFinite(samples, 1000, Trigger, 0);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.ArmFinite(samples, 1000, Trigger, 0)).Message, "reserved by a clocked task");
            Assert.ThrowsException<InvalidOperationException>(() => device.Staircase(samples, 1000));
            device.WriteOnDemand(new double[4]);   // the on-demand task takes the lines: the clocked task is deleted
            Assert.AreEqual(1, device.ClockedTasksDeleted);
            Assert.IsFalse(device.HasClockedTask);
            Assert.ThrowsException<InvalidOperationException>(() => device.WriteAt(samples, 0));

            double now = 0;
            device.Clock = () => now;
            device.ArmFinite(samples, 1000, Trigger, 0);
            device.Trigger();
            now = 0.005;
            Assert.AreEqual(5, device.SamplesGenerated);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.WriteAt(new double[4, 1], 4)).Message, "already generated");
            Assert.ThrowsException<InvalidOperationException>(() => device.WriteAt(new double[4, 6], 5));
            Assert.ThrowsException<InvalidOperationException>(() => device.WriteOnDemand(new[] { 0, 0, 10.5, 0 }));
            now = 1;
            Assert.IsTrue(device.IsDone);
        }
    }
}
