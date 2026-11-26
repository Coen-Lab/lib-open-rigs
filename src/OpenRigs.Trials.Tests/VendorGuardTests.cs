using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.VendorGuard;

namespace OpenRigs.Trials.Tests
{
    /// <summary>
    /// OpenRigs.Trials depends on OpenRigs.Zapit, which carries the NI-DAQmx and pylon wrappers. A workflow on a rig
    /// without those drivers loads Trials safely only while Trials names no vendor type itself.
    /// </summary>
    [TestClass]
    public class VendorGuardTests
    {
        [TestMethod]
        public void TrialsNamesNoVendorType()
        {
            var found = VendorReferences.Find(typeof(TrialLoop).Assembly.Location);
            Assert.AreEqual(0, found.Count,
                "OpenRigs.Trials may not name NationalInstruments, Basler, Bonsai.DAQmx or Bonsai.Pylon types, or a rig without "
                + "those drivers can no longer load it. Found:\n" + string.Join("\n", found.Select(h => "  " + h.Owner + ": " + h)));
        }
    }
}
