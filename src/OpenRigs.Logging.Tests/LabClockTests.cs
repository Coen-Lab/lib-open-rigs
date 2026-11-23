using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Logging.Tests
{
    /// <summary>
    /// The lab clock check (LabClock), at Start (LabClockReady) and at Go (LogController): lab.yml, the zone and the
    /// last time sync, each state judged from what was read, so none needs this machine's clock changed.
    /// </summary>
    [TestClass]
    public class LabClockTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 22, 12, 0, 0);
        static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

        static LabClock.SyncState Status(string lastSync, DateTime? registry = null) =>
            LabClock.JudgeSync(true, null, new[] { "Leap Indicator: 0(no warning)", "Last Successful Sync Time: " + lastSync, "Source: time.windows.com" }, registry, Now, Uk);

        static string Faults(LabClock.SyncState state, bool disabled = false) => string.Join("; ", LabClock.SyncFaults(disabled, state, 7));

        [TestMethod]
        public void ARecentSync_IsFine()
        {
            var state = Status("21/11/2026 09:00:00");
            Assert.AreEqual(1.1, state.AgeDays);
            Assert.AreEqual("", Faults(state));
        }

        [TestMethod]
        public void AStaleSync_IsRefused() => StringAssert.Contains(Faults(Status("01/11/2026 12:00:00")), "last synced the clock 21 days ago");

        [TestMethod]
        public void UnspecifiedWithNoRegistryValue_IsNeverSynced() => StringAssert.Contains(Faults(Status("unspecified")), "never reported a successful time sync");

        // A trigger-started w32time says unspecified in the minutes after it starts again: the registry gives the last sync.
        [TestMethod]
        public void UnspecifiedWithARegistryValue_UsesTheRegistry()
        {
            var state = Status("unspecified", Now.AddDays(-2));
            Assert.AreEqual(2.0, state.AgeDays);
            Assert.AreEqual("", Faults(state));
        }

        // A stopped service makes w32tm fail: the registry answers; with nothing there it is unknown, not never synced.
        [TestMethod]
        public void AStoppedService_FallsBackToTheRegistry_ElseUnknown()
        {
            var fallback = LabClock.JudgeSync(false, "The following error occurred: The service has not been started.", null, Now.AddHours(-5), Now, Uk);
            Assert.IsTrue(fallback.Known);
            Assert.AreEqual("", Faults(fallback));
            var unknown = LabClock.JudgeSync(false, "The service has not been started.", null, null, Now, Uk);
            Assert.IsFalse(unknown.Known);
            StringAssert.Contains(Faults(unknown), "could not be read: The service has not been started.");
            Assert.IsFalse(Faults(unknown).Contains("never"));
        }

        [TestMethod]
        public void AnUnreadableDate_IsUnknown() => StringAssert.Contains(Faults(Status("sometime")), "'sometime', not a readable date");

        [TestMethod]
        public void AClockBehindItsLastSync_IsRefused() => StringAssert.Contains(Faults(Status("30/11/2026 12:00:00")), "behind its own last sync by 8 days");

        [TestMethod]
        public void ADisabledService_IsRefused() => StringAssert.Contains(Faults(Status("21/11/2026 09:00:00"), disabled: true), "Windows Time service is disabled");

        [TestMethod]
        public void TheZone_AndDaylightSaving_AreEachNamed()
        {
            Assert.AreEqual(0, LabClock.ZoneFaults("GMT Standard Time", false, "GMT Standard Time").Count);
            var both = LabClock.ZoneFaults("Greenwich Standard Time", true, "GMT Standard Time");
            Assert.AreEqual(2, both.Count);
            StringAssert.Contains(both[0], "'Greenwich Standard Time', not the lab's 'GMT Standard Time'");
            StringAssert.Contains(both[1], "daylight saving is switched off");
        }

        // fixtures\lab-settings\v1: each <case>.yml with its expected answer in <case>.json, the share's other reader's set
        // copied byte for byte, so both readers take and refuse the same lab.yml, word for word.
        [TestMethod]
        public void ParseSettings_HoldsEveryFixtureCase()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "fixtures", "lab-settings", "v1"))) dir = dir.Parent;
            Assert.IsNotNull(dir, @"no fixtures\lab-settings\v1 above " + AppDomain.CurrentDomain.BaseDirectory);
            var cases = Directory.GetFiles(Path.Combine(dir.FullName, "fixtures", "lab-settings", "v1"), "*.yml");
            Assert.IsTrue(cases.Length >= 20, cases.Length.ToString());
            foreach (var yml in cases)
            {
                var want = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.ChangeExtension(yml, ".json")));
                LabClock.Settings settings;
                var refused = LabClock.ParseSettings(File.ReadAllText(yml), out settings);
                var name = Path.GetFileName(yml);
                if ((bool)want["ok"])
                {
                    Assert.IsNull(refused, name);
                    Assert.AreEqual((string)want["timeZone"], settings.TimeZone, name);
                    Assert.AreEqual((int)want["maxSyncAgeDays"], settings.MaxSyncAgeDays, name);
                }
                else
                {
                    Assert.AreEqual((string)want["detail"], refused, name);
                    Assert.IsNull(settings, name);
                }
            }
        }

        [TestMethod]
        public void ReadSettings_Missing_SaysWhereItComesFrom()
        {
            var root = Path.Combine(Path.GetTempPath(), "LabClockTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            try
            {
                var missing = Assert.ThrowsException<InvalidOperationException>(() => LabClock.ReadSettings(root));
                StringAssert.Contains(missing.Message, Path.Combine(root, "shared", "lab.yml"));
                StringAssert.Contains(missing.Message, @"comes from the share's config\shared when the rig is idle");
                var missingAtStart = Assert.ThrowsException<InvalidOperationException>(() =>
                    new LabClockAgrees().Process(Observable.Return(root)).ToEnumerable().ToList());
                StringAssert.StartsWith(missingAtStart.Message, "Recording refused: the lab settings");
                File.WriteAllText(Path.Combine(root, "shared", "lab.yml"), "# lab\ntimeZone: GMT Standard Time   # the zone\nmaxSyncAgeDays: 7   # days\n");
                var settings = LabClock.ReadSettings(root);
                Assert.AreEqual("GMT Standard Time", settings.TimeZone);
                Assert.AreEqual(7, settings.MaxSyncAgeDays);
                File.WriteAllText(Path.Combine(root, "shared", "lab.yml"), "timeZone: GMT Standard Time\nmaxSyncAgeDays: 7\nrigs:\n  timeZone: UTC\n");
                var refused = Assert.ThrowsException<InvalidOperationException>(() => LabClock.ReadSettings(root));
                StringAssert.Contains(refused.Message, "are refused: line 3 sets 'rigs'; lab.yml takes only timeZone and maxSyncAgeDays.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        // P1R-L4: LabClockReady, run headless as a workflow has it, stops the workflow at Start, before Ready and Go.
        [TestMethod]
        public void LabClockReady_StopsTheWorkflowAtStart()
        {
            var root = Path.Combine(Path.GetTempPath(), "LabClockTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            try
            {
                var result = OpenRigs.Tests.BonsaiWorkflowRunner.Run("LabClockReadyTest.bonsai",
                    new System.Collections.Generic.Dictionary<string, string> { { "ConfigRoot", root } }, TimeSpan.FromSeconds(60));
                StringAssert.Contains(result.StandardError + result.StandardOutput, "Recording refused: the lab settings", result.Describe());
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        // The check names every fault, here at least the zone, which no machine has as this id.
        [TestMethod]
        public void Check_RefusesNamingTheFaultAndTheFix()
        {
            var root = Path.Combine(Path.GetTempPath(), "LabClockTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            try
            {
                File.WriteAllText(Path.Combine(root, "shared", "lab.yml"), "timeZone: No Such Zone\nmaxSyncAgeDays: 7\n");
                var refused = Assert.ThrowsException<InvalidOperationException>(() => LabClock.Check(root));
                StringAssert.Contains(refused.Message, "not the lab's 'No Such Zone'");
                StringAssert.Contains(refused.Message, "set the time zone to 'No Such Zone'");
                var operatorRefused = Assert.ThrowsException<InvalidOperationException>(() =>
                    new CheckLabClock { ConfigRoot = root }.Process(System.Reactive.Linq.Observable.Return(1)).ToEnumerable().ToList());
                StringAssert.StartsWith(operatorRefused.Message, "Recording refused");
                Assert.AreEqual(1, new CheckLabClock().Process(System.Reactive.Linq.Observable.Return(1)).ToEnumerable().Single(), "no ConfigRoot: nothing checked");
                var atStart = Assert.ThrowsException<InvalidOperationException>(() =>
                    new LabClockAgrees().Process(Observable.Return(root)).ToEnumerable().ToList());
                StringAssert.Contains(atStart.Message, "set the time zone to 'No Such Zone'");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
