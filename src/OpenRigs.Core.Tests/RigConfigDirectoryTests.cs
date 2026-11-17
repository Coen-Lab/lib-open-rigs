using System;
using System.IO;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class RigConfigDirectoryTests
    {
        // No root of its own: an unset ConfigRoot stops at start naming where to set it, never another folder.
        [TestMethod]
        public void AnUnsetConfigRootNamesReadLabRig()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => new ResolveRigConfigDirectory().Generate().Wait());
            StringAssert.Contains(ex.Message, "ReadLabRig's ConfigRoot");
        }

        // rig.yml's files are relative to its folder, <ConfigRoot>\<MACHINE>, on every rig.
        [TestMethod]
        public void ARelativePathResolvesInTheRigsFolder()
        {
            var rig = Path.Combine(Path.GetTempPath(), "RigConfigDirectoryTests", Guid.NewGuid().ToString("N"), "RIG");
            Directory.CreateDirectory(Path.Combine(rig, "calibration"));
            File.WriteAllText(Path.Combine(rig, "calibration", "Projector-Mesh.csv"), "");
            try
            {
                Assert.AreEqual(Path.Combine(rig, "calibration", "Projector-Mesh.csv"), Resolve(rig, "calibration/Projector-Mesh.csv"));
            }
            finally { Directory.Delete(Path.GetDirectoryName(rig), true); }
        }

        [TestMethod]
        public void AFullPathIsRefused_NamingTheSetting()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Resolve(@"C:\Configs\RIG", @"C:\RigConfigs\RIG\calibration\Projector-Mesh.csv"));
            StringAssert.Contains(ex.Message, "rig.yml's projector.meshPath is a full path, C:\\RigConfigs\\RIG\\calibration\\Projector-Mesh.csv");
            StringAssert.Contains(ex.Message, "as calibration/Projector-Mesh.csv");
        }

        [TestMethod]
        public void AMissingFileIsRefused_NamingWhereItWasLookedFor()
        {
            var rig = Path.Combine(Path.GetTempPath(), "RigConfigDirectoryTests", Guid.NewGuid().ToString("N"), "RIG");
            var ex = Assert.ThrowsException<InvalidOperationException>(() => Resolve(rig, "calibration/Projector-Mesh.csv"));
            StringAssert.Contains(ex.Message, "rig.yml's projector.meshPath names calibration/Projector-Mesh.csv");
            StringAssert.Contains(ex.Message, Path.Combine(rig, "calibration", "Projector-Mesh.csv"));
        }

        static string Resolve(string directory, string path)
        {
            var resolve = new ResolveRigFile { Setting = "projector.meshPath" };
            return resolve.Process(Observable.Return(Tuple.Create(directory, path))).Wait();
        }
    }
}
