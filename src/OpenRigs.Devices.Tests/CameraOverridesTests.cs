using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;
using OpenRigs.Devices.Rigs;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class CameraOverridesTests
    {
        static NamedDevice<SpinnakerCamera> Camera()
        {
            return new NamedDevice<SpinnakerCamera>("Camera-Overhead", new SpinnakerCamera { SerialNumber = "1", Binning = 2, ExposureUs = 19000, PixelFormat = "Mono8" });
        }

        [TestMethod]
        public void BlankKeepsRigYmlsBinningAndASetValueReplacesItOnACopy()
        {
            var camera = Camera();
            Assert.AreEqual(2, new CameraOverrides().Apply(camera).Settings.Binning);
            var set = new CameraOverrides { Binning = 1 }.Apply(camera);
            Assert.AreEqual(1, set.Settings.Binning);
            Assert.AreEqual("Camera-Overhead", set.Name);
            Assert.AreEqual("1", set.Settings.SerialNumber);
            Assert.AreEqual("Mono8", set.Settings.PixelFormat);
            Assert.AreEqual(2, camera.Settings.Binning, "rig.yml's entry is left as read");
        }

        [TestMethod]
        public void ABinningBelowOneIsRefusedNamingTheCamera()
        {
            var error = Assert.ThrowsException<InvalidOperationException>(() => new CameraOverrides { Binning = 0 }.Apply(Camera()));
            StringAssert.Contains(error.Message, "Camera-Overhead's Binning is 0");
        }
    }
}
