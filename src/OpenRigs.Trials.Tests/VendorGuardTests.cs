using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Opto.ZapitDaq;
using OpenRigs.Opto.ZapitMatlab;
using OpenRigs.VendorGuard;

namespace OpenRigs.Trials.Tests
{
    /// <summary>
    /// The trial loop names no opto system: OpenRigs.Trials references no OpenRigs.Zapit type (nor an adapter's, nor a vendor's),
    /// so a workflow that takes Trials never gets the NI-DAQmx and pylon wrappers. Each opto system is an adapter package that
    /// depends on Trials and its own driver (OpenRigs.Opto.ZapitDaq on OpenRigs.Zapit, OpenRigs.Opto.ZapitMatlab on MATLAB Zapit's
    /// client in OpenRigs.Devices); the drivers never depend on Trials or an adapter. An adapter may name its driver's types, never a
    /// vendor's (OpenRigs.Zapit keeps those in its two vendor types, its own guard tests).
    /// </summary>
    [TestClass]
    public class VendorGuardTests
    {
        static readonly string[] Vendor = { "OpenRigs.Zapit", "Bonsai.DAQmx", "Bonsai.Pylon", "NationalInstruments.DAQmx", "NationalInstruments.Common", "Basler.Pylon" };

        static string[] References(Assembly assembly)
        {
            return assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        }

        [TestMethod]
        public void TrialsReferencesNoZapitAdapterOrVendorType()
        {
            var trials = typeof(TrialLoop).Assembly;
            var named = References(trials).Where(r => Vendor.Contains(r) || r.StartsWith("OpenRigs.Opto.")).ToList();
            Assert.AreEqual(0, named.Count,
                "OpenRigs.Trials may not reference an opto system's driver, adapter or vendor assembly (it knows only the Opto port, IOptoSystem): found "
                + string.Join(", ", named) + ". Put the system's code in its adapter package (OpenRigs.Opto.*).");
            var zapitTypes = trials.GetTypes().Where(t => t.Name.Contains("Zapit")).Select(t => t.FullName).ToList();
            Assert.AreEqual(0, zapitTypes.Count, "OpenRigs.Trials declares types named for Zapit, which belong in a Zapit adapter: " + string.Join(", ", zapitTypes));
        }

        [TestMethod]
        public void TrialsNamesNoVendorType()
        {
            var found = VendorReferences.Find(typeof(TrialLoop).Assembly.Location);
            Assert.AreEqual(0, found.Count,
                "OpenRigs.Trials may not name NationalInstruments, Basler, Bonsai.DAQmx or Bonsai.Pylon types, or a rig without "
                + "those drivers can no longer load it. Found:\n" + string.Join("\n", found.Select(h => "  " + h.Owner + ": " + h)));
        }

        // An adapter names its driver's types (ZapitDaq's messages and bank; MATLAB Zapit's replies), never a vendor's.
        [TestMethod]
        public void TheAdaptersNameNoVendorType()
        {
            foreach (var assembly in new[] { typeof(ZapitDaqOptoSystem).Assembly, typeof(ZapitMatlabOptoSystem).Assembly })
            {
                var found = VendorReferences.Find(assembly.Location);
                Assert.AreEqual(0, found.Count, assembly.GetName().Name + " names a vendor type:\n" + string.Join("\n", found.Select(h => "  " + h.Owner + ": " + h)));
            }
            CollectionAssert.Contains(References(typeof(ZapitDaqOptoSystem).Assembly), "OpenRigs.Zapit", "ZapitDaq's adapter no longer names its driver: the checks here would pass vacuously");
            var matlab = References(typeof(ZapitMatlabOptoSystem).Assembly);
            Assert.IsFalse(matlab.Any(r => Vendor.Contains(r) || r.StartsWith("OpenRigs.Opto.")), "MATLAB Zapit's adapter needs neither ZapitDaq, another adapter nor a vendor: " + string.Join(", ", matlab));
        }

        // The arrows point from an adapter to the loop and its driver, never back, and never adapter to adapter.
        [TestMethod]
        public void DriversAndTheLoopNeverReferenceAnAdapter()
        {
            var trials = typeof(TrialLoop).Assembly;
            var zapit = typeof(OpenRigs.Zapit.ZapitBank).Assembly;
            var devices = typeof(OpenRigs.Devices.Zapit.ZapitReply).Assembly;
            foreach (var assembly in new[] { zapit, devices })
            {
                var back = References(assembly).Where(r => r == "OpenRigs.Trials" || r.StartsWith("OpenRigs.Opto.")).ToList();
                Assert.AreEqual(0, back.Count, assembly.GetName().Name + " references " + string.Join(", ", back) + ": a driver never depends on the trial loop or an adapter.");
            }
            Assert.IsFalse(References(devices).Contains("OpenRigs.Zapit"), "OpenRigs.Devices references OpenRigs.Zapit: ZapitDaq's pieces belong in OpenRigs.Opto.ZapitDaq.");
            Assert.IsFalse(References(typeof(ZapitDaqOptoSystem).Assembly).Contains("OpenRigs.Opto.ZapitMatlab"), "one adapter references another");
            CollectionAssert.Contains(References(typeof(ZapitDaqOptoSystem).Assembly), trials.GetName().Name);
            CollectionAssert.Contains(References(typeof(ZapitMatlabOptoSystem).Assembly), trials.GetName().Name);
        }
    }
}
