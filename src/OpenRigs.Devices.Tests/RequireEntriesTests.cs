using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class RequireEntriesTests
    {
        [TestMethod]
        public void PassesExactlyTheCount()
        {
            RequireEntries.Check(new[] { "face", "pupil", "body" }, "camera", 3, "dome rig model, open_rigs.rigs.dome");
        }

        [TestMethod]
        public void RefusesAnotherCountNamingTheRule()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                RequireEntries.Check(new[] { "face", "body" }, "camera", 3, "dome rig model, open_rigs.rigs.dome"));
            Assert.AreEqual("rig.yml lists 2 camera entries (face, body); it needs exactly 3 (dome rig model, open_rigs.rigs.dome).", ex.Message);
        }
    }
}
