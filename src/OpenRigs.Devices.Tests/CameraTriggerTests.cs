using System;
using System.Linq;
using System.Reactive.Linq;
using Bonsai.Harp;
using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class CameraTriggerTests
    {
        const string Trigger = "    cameraTriggerController: { trigger0Enabled: true, trigger0Frequency: 50, trigger1Enabled: false, trigger1Frequency: 60 }\n";

        static CameraOnTrigger Find(string board, double exposure = 19000)
        {
            var rig = LabRigTests.Deserialize("behaviorBoard:\n  main:\n    portName: COM1\n" + board +
                "camera:\n  face: { serialNumber: '1', exposureTime: " + exposure + ", gain: 0, binning: 1 }\n");
            var camera = RigDevice.Find(rig.Camera, "Camera-Face");
            var entry = RigDevice.Find(rig.BehaviorBoard, "BehaviorBoard-Main");
            return CameraTrigger.Find(new NamedDevice<Rigs.SpinnakerCamera>("Camera-Face", camera), new NamedDevice<Rigs.BehaviorBoard>("BehaviorBoard-Main", entry), 1000);
        }

        [TestMethod]
        public void FindsTheTriggerLineNamingTheCamera()
        {
            var found = Find("    wiring:\n      DO0: { device: CameraTrigger, targets: [Camera-Pupil, Camera-Face] }\n" + Trigger);
            Assert.AreEqual(0, found.Output);
            Assert.AreEqual(50, found.Frequency);
            Assert.AreEqual("Camera-Face", found.Camera.Name);
        }

        [DataTestMethod]
        [DataRow("    wiring:\n      DO0: { device: CameraTrigger, targets: [Camera-Body] }\n", "has 0 CameraTrigger lines naming Camera-Face")]
        [DataRow("", "has 0 CameraTrigger lines naming Camera-Face")]
        [DataRow("    wiring:\n      DO0: { device: CameraTrigger, targets: [Camera-Face] }\n      DO1: { device: CameraTrigger, targets: [Camera-Face] }\n", "has 2 CameraTrigger lines naming Camera-Face in targets (DO0, DO1)")]
        [DataRow("    wiring:\n      DO1: { device: CameraTrigger, targets: [Camera-Face] }\n", "trigger1Enabled is false")]
        [DataRow("    wiring:\n      DO2: { device: CameraTrigger, targets: [Camera-Face] }\n", "on DO2")]
        public void RefusesAnythingButOneEnabledTriggerLine(string wiring, string message)
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Find(wiring + Trigger));
            StringAssert.Contains(ex.Message, message);
        }

        [TestMethod]
        public void RefusesAnExposureTooLongForItsTrigger()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Find("    wiring:\n      DO0: { device: CameraTrigger, targets: [Camera-Face] }\n" + Trigger, 19500));
            StringAssert.Contains(ex.Message, "Camera-Face's ExposureTime is 19500 us, longer than 19000");
        }

        [TestMethod]
        public void RefusesABoardWithNoCameraTrigger()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Find("    wiring:\n      DO0: { device: CameraTrigger, targets: [Camera-Face] }\n"));
            StringAssert.Contains(ex.Message, "BehaviorBoard-Main in rig.yml has no cameraTriggerController");
        }

        [TestMethod]
        public void PassesOnlyTheTriggersEventPulses()
        {
            var pulse = HarpMessage.FromByte(Camera0Frame.Address, 2.5, MessageType.Event, 1);
            var dump = HarpMessage.FromByte(Camera0Frame.Address, 1.0, MessageType.Read, 1);
            var other = HarpMessage.FromByte(Camera1Frame.Address, 3.0, MessageType.Event, 1);
            var times = new CameraTriggerPulses { Output = 0 }.Process(new[] { dump, pulse, other }.ToObservable()).Select(t => t.Seconds).ToArray().Wait();
            CollectionAssert.AreEqual(new[] { 2.5 }, times);
        }

        // A PropertyMapping sets Output after the node is built and before it is subscribed: the pulses are of the
        // output it has then, as a DO1-wired camera's must be trigger 1's.
        [TestMethod]
        public void PulsesAreOfTheOutputSetBeforeSubscription()
        {
            var node = new CameraTriggerPulses();
            var pulses = node.Process(new[]
            {
                HarpMessage.FromByte(Camera0Frame.Address, 1.0, MessageType.Event, 1),
                HarpMessage.FromByte(Camera1Frame.Address, 2.0, MessageType.Event, 1)
            }.ToObservable());
            node.Output = 1;
            CollectionAssert.AreEqual(new[] { 2.0 }, pulses.Select(t => t.Seconds).ToArray().Wait());
        }
    }
}
