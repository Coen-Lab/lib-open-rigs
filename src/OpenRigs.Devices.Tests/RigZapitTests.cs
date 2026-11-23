using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Rigs;

namespace OpenRigs.Devices.Tests
{
    /// <summary>rig.yml's zapit block as the workflow reads it at Start: the four rigs' files as they are on the share (MATLAB
    /// Zapit's host, port and hardwareTriggered alone), and a blue rig carrying ZapitDaq's fields beside them.</summary>
    [TestClass]
    public class RigZapitTests
    {
        static LabRig Fixture(string rig)
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (!File.Exists(Path.Combine(folder.FullName, "OpenRigs.sln"))) folder = folder.Parent;
            return LabRigTests.Deserialize(File.ReadAllText(Path.Combine(folder.FullName, "fixtures", "rig-yml", rig + ".yml")));
        }

        [TestMethod]
        public void TheRigsFilesAsTheyAreHaveMatlabZapitsBlockAlone()
        {
            foreach (var rig in new[] { "B1", "B2", "G1", "G2" })
            {
                var zapit = Fixture(rig).Zapit;
                Assert.AreEqual(1488, zapit.Port, rig);
                Assert.IsNull(zapit.Device, rig + ": no ZapitDaq, so ConnectForTaskZapitDaq opens nothing");
            }
            Assert.IsTrue(Fixture("B2").Zapit.HardwareTriggered);
        }

        // ZapitDaq's channels joined from the device and each line in one place (Rigs.Zapit), MATLAB Zapit's endpoint beside them.
        [TestMethod]
        public void ABlueRigCarriesBothAndGivesZapitDaqsChannelPaths()
        {
            var zapit = Fixture("B2-both").Zapit;
            Assert.AreEqual("127.0.0.1", zapit.Host);
            Assert.IsTrue(zapit.HardwareTriggered);
            Assert.AreEqual("Dev1/ao0", zapit.GalvoXChannelPath);
            Assert.AreEqual("Dev1/ao1", zapit.GalvoYChannelPath);
            Assert.AreEqual("Dev1/ao2", zapit.LaserChannelPath);
            Assert.AreEqual("Dev1/ao3", zapit.LedChannelPath);
            Assert.AreEqual("Dev1/ai0", zapit.PhotodiodeChannelPath);
            CollectionAssert.AreEqual(new[] { 0.3, -0.12, 0.25 }, zapit.BlankingTimesMs.ToArray());
            Assert.AreEqual("/Dev1/PFI0", zapit.TriggerTerminal);
            Assert.AreEqual(100000.0, zapit.SampleRate);
            Assert.AreEqual("software", zapit.SafetyGate.Layer);
            Assert.AreEqual("calibration/Zapit-Session.pfs", zapit.Camera.SessionPfsPath);
        }
    }
}
