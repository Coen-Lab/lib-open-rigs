using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class PulseCommandsTests
    {
        // DO1 to DO3 fixed at 40, 45 and 3000 ms and pulse-enabled as given; DO0 the camera trigger.
        static Rigs.LabRig Rig(string enabled = "DO1, DO2, DO3")
        {
            return LabRigTests.Deserialize("behaviorBoard:\n  main:\n    portName: COM1\n    pulseController:\n      outputPulseEnable: \"" + enabled + "\"\n      outputs:\n" +
                "        DO1: { discriminatorType: FixedPulse, pulseWidthMs: 40 }\n        DO2: { discriminatorType: FixedPulse, pulseWidthMs: 45 }\n        DO3: { discriminatorType: FixedPulse, pulseWidthMs: 3000 }\n" +
                "    wiring:\n      DO0: { device: CameraTrigger, targets: [Camera-Overhead] }\n");
        }

        // Each write, "address=value"; with stopMs the subscription is disposed that long after the commands.
        static string Run(string[] pulses, string[] resets = null, string enabled = "DO1, DO2, DO3", int stopMs = 0)
        {
            var source = new PulseCommands { Board = "BehaviorBoard-Main" }.Process(
                Observable.Return(Rig(enabled)), pulses.ToObservable(), (resets ?? new string[0]).ToObservable());
            if (stopMs > 0) source = source.TakeUntil(Observable.Timer(TimeSpan.FromMilliseconds(stopMs)));
            return string.Join(" ", source.ToEnumerable().Select(m => m.Address + "=" + m.GetPayloadUInt16()));
        }

        [TestMethod]
        public void AFlushIsTimedByTheBoardAndReturnsAtOnce()
        {
            var clock = Stopwatch.StartNew();
            Assert.AreEqual("57=10000 45=14336 34=2048", Run(new[] { "BehaviorBoard-Main DO1 10000" }));
            Assert.IsTrue(clock.ElapsedMilliseconds < 2000);
        }

        [TestMethod]
        public void APulseTakesRigYmlsWidthAndAddsItsLineToThePulsedOnes()
        {
            Assert.AreEqual("57=40 45=14336 34=2048 58=45 45=14336 34=4096 59=3000 45=14336 34=8192",
                Run(new[] { "BehaviorBoard-Main DO1", "BehaviorBoard-Main DO2", "BehaviorBoard-Main DO3" }));
            Assert.AreEqual("58=45 45=6144 34=4096", Run(new[] { "BehaviorBoard-Main DO2" }, enabled: "DO1"));
        }

        [TestMethod]
        public void ADOPortPulseIsTimedByTheBoardToo()
        {
            Assert.AreEqual("46=30 45=14337 34=1", Run(new[] { "BehaviorBoard-Main DOPort0 30" }));
        }

        [TestMethod]
        public void ResetClosesEveryLineButTheCameraTriggerAndPutsRigYmlBack()
        {
            Assert.AreEqual("35=14343 57=40 58=45 59=3000 45=14336", Run(new string[0], new[] { "" }));
            Assert.AreEqual("", Run(new string[0], new[] { "Other-Board" }));
        }

        [DataTestMethod]
        [DataRow("BehaviorBoard-Main DO0")]
        [DataRow("BehaviorBoard-Main DO1 99999")]
        [DataRow("BehaviorBoard-Main DO1 0")]
        [DataRow("BehaviorBoard-Main DO9")]
        [DataRow("BehaviorBoard-Main")]
        [DataRow("BehaviorBoard-Main DO1 10 x")]
        [DataRow("Other-Board DO1 10000")]
        public void ACommandItCannotTakeWritesNothing(string pulse)
        {
            Assert.AreEqual("", Run(new[] { pulse }));
        }

        // A stop mid-pulse leaves no line high: the board, modelled here, lowers a pulse-enabled line with a width.
        [DataTestMethod]
        [DataRow("DOPort0")]
        [DataRow("DOPort2")]
        [DataRow("DO1")]
        [DataRow("DO3")]
        public void AStopMidPulseLeavesNoLineHigh(string line)
        {
            Assert.AreEqual(0, HighAfter(Run(new[] { "BehaviorBoard-Main " + line + " 500" }, stopMs: 100)));
        }

        static int HighAfter(string writes)
        {
            int high = 0, enabled = 0;
            var widths = new Dictionary<int, int>();
            foreach (var write in writes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = write.Split('=').Select(int.Parse).ToArray();
                int address = parts[0], value = parts[1];
                if (address == 34) high |= value & ~(enabled & widths.Where(w => w.Value > 0).Aggregate(0, (a, w) => a | w.Key));
                else if (address == 35) high &= ~value;
                else if (address == 45) enabled = value;
                else if (address >= 46 && address <= 48) widths[1 << (address - 46)] = value;
                else if (address >= 56 && address <= 59) widths[0x400 << (address - 56)] = value;
            }
            return high;
        }
    }
}
