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

            public Harness(double sampleRate = 100000, double maxTrialSeconds = 1.0, double earlyStopLeadSeconds = 0.01)
            {
                Device.Clock = () => Volatile.Read(ref now);
                var node = new ZapitDaq
                {
                    SampleRate = sampleRate, MaxTrialSeconds = maxTrialSeconds, EarlyStopLeadSeconds = earlyStopLeadSeconds,
                    TriggerTerminal = Trigger, DeviceFactory = () => Device,
                };
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
            // 12340 generated, 10 ms lead: the ramp starts on the next cycle boundary, 15000, and lasts 25000 samples.
            var buffer = h.Device.LastBuffer;
            var ramp = bank.Assemble(1, variant, 6, 15000);
            Assert.AreEqual(25000, ramp.GetLength(1));
            for (int i = 0; i < 15000; i++) CollectionAssert.AreEqual(Column(before, i), Column(buffer, i));
            for (int i = 0; i < 25000; i++) CollectionAssert.AreEqual(Column(ramp, i), Column(buffer, 15000 + i));
            for (int i = 39999; i < 100000; i++) Assert.IsTrue(buffer[2, i] == 0 && buffer[3, i] == 0, $"Sample {i} is not dark.");
            Assert.IsTrue(Enumerable.Range(15000, 1000).Any(i => buffer[2, i] > 0), "The ramp has no light.");

            h.Now = 0.4;
            var stopped = h.Next(ZapitDaqEventKind.Stopped);
            Assert.IsTrue(stopped.SamplesGenerated >= 40000 && stopped.SamplesGenerated < 100000);
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
            StringAssert.Contains(h.ErrorMessage(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Point }), "cannot Point while trial 1 is armed");
        }

        [TestMethod]
        public void TheTwinRefusesWhatNiRefuses()
        {
            var device = new SimulatedZapitDevice { AutoTrigger = false };
            var samples = new double[4, 10];
            device.ArmFinite(samples, 1000, Trigger);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => device.ArmFinite(samples, 1000, Trigger)).Message, "reserved by a clocked task");
            Assert.ThrowsException<InvalidOperationException>(() => device.Staircase(samples, 1000));
            device.WriteOnDemand(new double[4]);   // the on-demand task takes the lines: the clocked task is deleted
            Assert.AreEqual(1, device.ClockedTasksDeleted);
            Assert.IsFalse(device.HasClockedTask);
            Assert.ThrowsException<InvalidOperationException>(() => device.WriteAt(samples, 0));

            double now = 0;
            device.Clock = () => now;
            device.ArmFinite(samples, 1000, Trigger);
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
