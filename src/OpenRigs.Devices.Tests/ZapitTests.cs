using System;
using System.IO;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Rigs;
using OpenRigs.Devices.Zapit;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class ZapitTests
    {
        static string Repo()
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (!File.Exists(Path.Combine(folder.FullName, "OpenRigs.sln"))) folder = folder.Parent;
            return folder.FullName;
        }

        static LabRig Fixture(string rig)
        {
            var text = File.ReadAllText(Path.Combine(Repo(), "fixtures", "rig-yml", rig + ".yml"));
            return LabRigTests.Deserialize(text);
        }

        [TestMethod]
        public void LineMessagesSetAndClearTheLine()
        {
            var set = ZapitLineMessage.Message(Harp.Behavior.DigitalOutputs.DO3, true);
            Assert.AreEqual(Harp.Behavior.OutputSet.Address, set.Address);
            Assert.AreEqual(Harp.Behavior.DigitalOutputs.DO3, Harp.Behavior.OutputSet.GetPayload(set));
            var clear = new ZapitLineMessage().Process(Observable.Return(Harp.Behavior.DigitalOutputs.DO3)).Wait();
            Assert.AreEqual(Harp.Behavior.OutputClear.Address, clear.Address);
            Assert.AreEqual(Bonsai.Harp.MessageType.Write, clear.MessageType);
        }

        // The opto rigs' rig.yml (B1, B2) under the native block, as the workflow reads it at Start; ZapitDaq's channels joined from
        // the device and each line in one place (Rigs.Zapit).
        [TestMethod]
        public void TheNativeBlockIsReadAndGivesZapitDaqsChannelPaths()
        {
            var zapit = Fixture("B2").Zapit;
            Assert.AreEqual("Dev1/ao0", zapit.GalvoXChannelPath);
            Assert.AreEqual("Dev1/ao1", zapit.GalvoYChannelPath);
            Assert.AreEqual("Dev1/ao2", zapit.LaserChannelPath);
            Assert.AreEqual("Dev1/ao3", zapit.LedChannelPath);
            Assert.AreEqual("Dev1/ai0", zapit.PhotodiodeChannelPath);
            Assert.AreEqual("/Dev1/PFI0", zapit.TriggerTerminal);
            Assert.AreEqual(100000, zapit.SampleRate);
            Assert.AreEqual("software", zapit.SafetyGate.Layer);
            Assert.AreEqual("calibration/Zapit-Session.pfs", zapit.Camera.SessionPfsPath);
            CollectionAssert.AreEqual(new[] { -0.04, 5.0 }, new System.Collections.Generic.List<double>(zapit.LaserMinMaxControlVolts));
            Assert.IsNotNull(Fixture("B1").Zapit);
        }

        [TestMethod]
        public void ARigWithoutZapitHasNoBlock()
        {
            Assert.IsNull(Fixture("G1").Zapit);
            Assert.IsNull(Fixture("G2").Zapit);
        }

        [TestMethod]
        public void TheOldServerBlockStopsTheWorkflowAtStart()
        {
            var text = File.ReadAllText(Path.Combine(Repo(), "fixtures", "rig-yml", "G1.yml")) + "\nzapit:\n  host: 127.0.0.1\n  port: 1488\n";
            Exception error = null;
            try { LabRigTests.Deserialize(text); }
            catch (Exception e) { error = e; }
            Assert.IsNotNull(error, "a key the classes lack stops the workflow");
        }
    }
}
