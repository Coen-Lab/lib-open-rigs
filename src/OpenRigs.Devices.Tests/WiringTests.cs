using System;
using System.IO;
using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class WiringTests
    {
        static string Rig(string wiring)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "rootPath: x\nbehaviorBoard:\n  main:\n    portName: COM1\n    wiring:\n" + wiring);
            return path;
        }

        [TestMethod]
        public void FindsTheOneLineWiredToTheDevice()
        {
            var rig = Rig("      DO1: { device: RewardValve, side: left }\n      DO3: { device: ZapitTrigger }\n");
            Assert.AreEqual(DigitalOutputs.DO3, WiredOutput.Find(rig, "BehaviorBoard-Main", "ZapitTrigger"));
        }

        [DataTestMethod]
        [DataRow("      DO1: { device: RewardValve }\n", "has 0 lines with device ZapitTrigger (none)")]
        [DataRow("      DO2: { device: ZapitTrigger }\n      DO3: { device: ZapitTrigger }\n", "has 2 lines with device ZapitTrigger (DO2, DO3)")]
        [DataRow("      DO0: { device: ZapitTrigger }\n", "is on DO0")]
        public void RefusesAnythingButOnePulseLine(string wiring, string message)
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => WiredOutput.Find(Rig(wiring), "BehaviorBoard-Main", "ZapitTrigger"));
            StringAssert.Contains(ex.Message, message);
        }

        [TestMethod]
        public void PulseLinesLoseTheHeldLine()
        {
            var lines = new WithoutPulseLine().Process(System.Reactive.Linq.Observable.Return(Tuple.Create(DigitalOutputs.DO1 | DigitalOutputs.DO2 | DigitalOutputs.DO3, DigitalOutputs.DO3)));
            Assert.AreEqual(DigitalOutputs.DO1 | DigitalOutputs.DO2, System.Reactive.Linq.Observable.Wait(lines));
        }

        [TestMethod]
        public void HarpClockAgreesOnlyNearUtc()
        {
            var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            var harp = (now - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            Assert.IsTrue(HarpClockSet.Agrees(harp + 1.5, now, 2));
            Assert.IsFalse(HarpClockSet.Agrees(harp - 3, now, 2));
            Assert.IsFalse(HarpClockSet.Agrees(3600, now, 2), "a clock counting from power-on");
        }
    }
}
