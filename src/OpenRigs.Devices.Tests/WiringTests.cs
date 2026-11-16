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

        static Rigs.LabRig LabRig(string wiring)
        {
            return LabRigTests.Deserialize("behaviorBoard:\n  main:\n    portName: COM1\n    wiring:\n" + wiring);
        }

        [TestMethod]
        public void FindsEveryLineWiredToTheDevice()
        {
            var rig = LabRig("      DO1: { device: RewardValve, side: left }\n      DO2: { device: RewardValve, side: right }\n      DO3: { device: ZapitTrigger }\n");
            Assert.AreEqual(DigitalOutputs.DO1 | DigitalOutputs.DO2, WiredOutputs.Find(rig, "BehaviorBoard-Main", "RewardValve"));
        }

        [DataTestMethod]
        [DataRow("      DO3: { device: ZapitTrigger }\n", "has no line with device RewardValve")]
        [DataRow("      DO0: { device: RewardValve, side: left }\n", "puts a RewardValve on DO0")]
        public void RefusesNoLineOrALineOffThePulseLines(string wiring, string message)
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => WiredOutputs.Find(LabRig(wiring), "BehaviorBoard-Main", "RewardValve"));
            StringAssert.Contains(ex.Message, message);
        }

        // No board by default: a workflow names the board, so an unset one stops at start rather than reading main's.
        [TestMethod]
        public void AnUnsetBoardIsRefused()
        {
            var rig = LabRig("      DO1: { device: RewardValve, side: left }\n");
            var ex = Assert.ThrowsException<ArgumentException>(() => System.Reactive.Linq.Observable.Wait(
                new WiredOutputs { Device = "RewardValve" }.Process(System.Reactive.Linq.Observable.Return(rig))));
            StringAssert.Contains(ex.Message, "RigDevice's Name is ''");
        }

        [TestMethod]
        public void RefusesABoardRigYmlLacks()
        {
            Assert.ThrowsException<System.Collections.Generic.KeyNotFoundException>(() => WiredOutputs.Find(LabRigTests.Deserialize("rootPath: x\n"), "BehaviorBoard-Main", "RewardValve"));
        }

        static Rigs.LabRig PulsedRig(string enabled, string wiring)
        {
            return LabRigTests.Deserialize("behaviorBoard:\n  main:\n    portName: COM1\n    pulseController:\n      outputPulseEnable: " + enabled + "\n    wiring:\n" + wiring);
        }

        [TestMethod]
        public void PulseLinesLoseTheZapitTriggerLine()
        {
            var rig = PulsedRig("DO1, DO2, DO3", "      DO1: { device: RewardValve, side: left }\n      DO2: { device: RewardValve, side: right }\n      DO3: { device: ZapitTrigger }\n");
            Assert.AreEqual(DigitalOutputs.DO1 | DigitalOutputs.DO2, PulseEnabledLines.Find(rig, "BehaviorBoard-Main"));
        }

        [TestMethod]
        public void PulseLinesNeedNoZapitTrigger()
        {
            var rig = PulsedRig("DO1", "      DO1: { device: RewardValve, side: left }\n");
            Assert.AreEqual(DigitalOutputs.DO1, PulseEnabledLines.Find(rig, "BehaviorBoard-Main"));
        }

        // A valve on a line the board does not pulse would be opened by TriggerPulse and left open.
        [TestMethod]
        public void RefusesAValveLineThatIsNotPulsed()
        {
            var rig = PulsedRig("DO1", "      DO1: { device: RewardValve, side: left }\n      DO2: { device: RewardValve, side: right }\n");
            var ex = Assert.ThrowsException<InvalidOperationException>(() => PulseEnabledLines.Find(rig, "BehaviorBoard-Main"));
            StringAssert.Contains(ex.Message, "puts a RewardValve on DO2");
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
