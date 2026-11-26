using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.VendorGuard;

namespace OpenRigs.Zapit.Tests
{
    /// <summary>
    /// A workflow that takes the Zapit-Bonsai adapter (OpenRigs.Opto.ZapitBonsai) also gets OpenRigs.Zapit, with the NI-DAQmx and
    /// pylon wrappers (OpenRigs.Trials no longer does: OpenRigs.Trials.Tests' VendorGuardTests). It stays safe on a rig without
    /// those drivers only while no vendor type is reachable from what it loads: the adapter names Zapit's message and bank types
    /// only, and in Zapit the vendor types sit in NiZapitDevice (reached through ZapitDaq's NoInlining CreateNi, only when not
    /// simulated) and ZapitPylonCapture.
    /// </summary>
    [TestClass]
    public class VendorGuardTests
    {
        // The only Zapit types allowed to name vendor types (nested types count as part of them).
        static readonly string[] VendorTypes = { "OpenRigs.Zapit.NiZapitDevice", "OpenRigs.Zapit.ZapitPylonCapture" };

        static readonly string ZapitDll = typeof(ZapitDaq).Assembly.Location;

        [TestMethod]
        public void ZapitNamesVendorTypesOnlyInTheTwoVendorTypes()
        {
            var stray = VendorReferences.Find(ZapitDll).Where(h => !VendorTypes.Contains(h.Owner)).ToList();
            Assert.AreEqual(0, stray.Count,
                "OpenRigs.Zapit may name NationalInstruments, Basler, Bonsai.DAQmx or Bonsai.Pylon types only in "
                + string.Join(" and ", VendorTypes) + ", or a rig without those drivers can no longer load Zapit. Found:\n"
                + string.Join("\n", stray.Select(h => "  " + h.Owner + ": " + h)));
        }

        // Without this the test above could pass because the scan sees nothing.
        [TestMethod]
        public void TheScanSeesTheVendorTypesItExempts()
        {
            var owners = VendorReferences.Find(ZapitDll).Select(h => h.Owner).Distinct().ToList();
            foreach (var type in VendorTypes)
                CollectionAssert.Contains(owners, type, type + " no longer names a vendor type, or the scan stopped seeing it; update VendorTypes or the scan.");
        }

        [TestMethod]
        public void ZapitDaqReachesTheNiDeviceOnlyThroughANoInliningCreateNi()
        {
            var create = typeof(ZapitDaq).GetMethod("CreateNi", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(create, "ZapitDaq.CreateNi is gone: the one place ZapitDaq builds NiZapitDevice. If it moved, guard the new one the same way.");
            Assert.IsTrue((create.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) != 0,
                "ZapitDaq.CreateNi lost [MethodImpl(MethodImplOptions.NoInlining)]: the JIT may then inline NiZapitDevice into ZapitDaq's "
                + "subscribe code and load NationalInstruments.DAQmx even when Simulated is true, which fails on a rig without NI-DAQmx.");
        }
    }
}
