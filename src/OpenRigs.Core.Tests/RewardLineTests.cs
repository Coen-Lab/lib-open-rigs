using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class RewardLineTests
    {
        const string Machine = "RIG-NAME";  // the fixture's machineName

        const string Rig =
            "behaviorBoard:\n" +
            "  main:\n" +
            "    portName: COM3\n" +
            "    wiring:\n" +
            "      DO1: {device: RewardValve, side: left}\n" +
            "      DO2: {device: RewardValve, side: right}\n" +
            "      DO3: {device: RewardValve, side: middle}\n" +
            "    pulseController:\n" +
            "      outputs:\n" +
            "        DO1: {discriminatorType: CalibratedPulse, artefact: calibration/valve-DO1.json}\n" +
            "        DO2: {discriminatorType: FixedPulse, pulseWidthMs: 30}\n" +
            "        DO3: {discriminatorType: CalibratedPulse}\n";

        string folder;

        static string Fixture
        {
            get
            {
                var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (!File.Exists(Path.Combine(directory.FullName, "OpenRigs.sln"))) directory = directory.Parent;
                return File.ReadAllText(Path.Combine(directory.FullName, "fixtures", "calibration", "valve-DO1.json"));
            }
        }

        [TestInitialize]
        public void CreateFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "RewardLineTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "calibration"));
        }

        [TestCleanup]
        public void DeleteFolder()
        {
            Directory.Delete(folder, true);
        }

        string Write(string rig, string calibration)
        {
            File.WriteAllText(Path.Combine(folder, "rig.yml"), rig);
            if (calibration != null) File.WriteAllText(Path.Combine(folder, "calibration", "valve-DO1.json"), calibration);
            return Path.Combine(folder, "rig.yml");
        }

        RewardLine Load(string rig, string calibration, string side = "left", MissingCalibration missing = MissingCalibration.Refuse)
        {
            return LoadRewardLine.Load(Write(rig, calibration), side, "", Machine, missing);
        }

        string Refusal(string rig, string calibration, string side = "left")
        {
            try
            {
                Load(rig, calibration, side);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
            Assert.Fail("expected a refusal");
            return null;
        }

        static string Edit(string json, Action<JObject> edit)
        {
            var fields = JObject.Parse(json, new JsonLoadSettings());
            edit(fields);
            return fields.ToString();
        }

        RewardLine Calibrated()
        {
            return Load(Rig, Fixture);
        }

        [TestMethod]
        public void TheFixtureRoundTrips()
        {
            var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
            var curve = JsonConvert.DeserializeObject<CalibrationCurve>(Fixture);
            Assert.AreEqual(new DateTimeOffset(2026, 9, 26, 10, 30, 0, TimeSpan.Zero), curve.CalibratedAt);
            Assert.AreEqual(100, curve.PulseGapMs);
            Assert.AreEqual(0.055, curve.LinearFit.Slope);
            var original = JsonConvert.DeserializeObject<JObject>(Fixture, settings);
            var written = JsonConvert.DeserializeObject<JObject>(JsonConvert.SerializeObject(curve), settings);
            original.Remove("calibratedAt");
            written.Remove("calibratedAt");
            Assert.IsTrue(JToken.DeepEquals(Numbers(original), Numbers(written)), written.ToString());
        }

        // 20 and 20.0 are one number to the fixture's readers.
        static JObject Numbers(JObject token)
        {
            foreach (var value in token.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.Integer).ToList())
            {
                value.Value = Convert.ToDouble(value.Value);
            }
            return token;
        }

        [TestMethod]
        public void TheGeneratedTypeRefusesAFileWithoutTheFitOrTheGap()
        {
            foreach (var key in new[] { "linearFit", "pulseGapMs" })
            {
                var json = Edit(Fixture, fields => fields.Remove(key));
                var ex = Assert.ThrowsException<JsonSerializationException>(() => JsonConvert.DeserializeObject<CalibrationCurve>(json));
                StringAssert.Contains(ex.Message, key);
            }
        }

        [TestMethod]
        public void LoadsTheSidesCalibratedLine()
        {
            var line = Calibrated();
            Assert.AreEqual("BehaviorBoard-Main", line.Board);
            Assert.AreEqual(1, line.Line);
            Assert.IsTrue(line.Calibrated);
            Assert.IsNull(line.Uncalibrated);
            Assert.AreEqual(100, line.PulseGapMs);
            Assert.AreEqual(Fixture, line.CalibrationJson);
            Assert.AreEqual(Path.Combine(folder, "calibration", "valve-DO1.json"), line.CalibrationPath);
        }

        [TestMethod]
        public void TheWidthComesFromTheFitAndTheVolumeFromTheRoundedWidth()
        {
            // The fixture: 0.055 ul/ms - 0.1 ul, 20 to 60 ms. 2 ul: (2 + 0.1) / 0.055 = 38.2 -> 38 ms, 1.99 ul.
            var plan = PlanReward.Plan(Calibrated(), 2.0);
            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(38, plan.WidthMs);
            Assert.AreEqual(1.99, plan.DeliveredUl, 1e-9);
            Assert.AreEqual(1, plan.Line);
            Assert.AreEqual(100, plan.PulseGapMs);
            Assert.IsFalse(plan.Clamped);
        }

        [TestMethod]
        public void AVolumeAboveTheLargestPulseIsSplitIntoEqualPulses()
        {
            // At most 3.2 ul a pulse: 6.4 ul is two pulses at 60 ms, a little more is three.
            var two = PlanReward.Plan(Calibrated(), 6.4);
            Assert.AreEqual(2, two.Count);
            Assert.AreEqual(60, two.WidthMs);
            Assert.AreEqual(6.4, two.DeliveredUl, 1e-9);
            var three = PlanReward.Plan(Calibrated(), 6.5);
            Assert.AreEqual(3, three.Count);
            Assert.AreEqual(41, three.WidthMs);  // (6.5 / 3 + 0.1) / 0.055 = 41.2
            Assert.AreEqual(3 * (0.055 * 41 - 0.1), three.DeliveredUl, 1e-9);
        }

        [TestMethod]
        public void AVolumeBelowTheRangeIsRaisedToTheShortestWidthAndSaysSo()
        {
            var plan = PlanReward.Plan(Calibrated(), 0.5);
            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(20, plan.WidthMs);
            Assert.IsTrue(plan.Clamped);
            Assert.AreEqual(1.0, plan.DeliveredUl, 1e-9);
        }

        [TestMethod]
        public void NothingAskedOrAnUncalibratedLinePlansNoPulses()
        {
            foreach (var ul in new[] { 0.0, -1.0, double.NaN })
            {
                var plan = PlanReward.Plan(Calibrated(), ul);
                Assert.AreEqual(0, plan.Count);
                Assert.AreEqual(0.0, plan.DeliveredUl);
            }
            File.Delete(Path.Combine(folder, "calibration", "valve-DO1.json"));
            var uncalibrated = Load(Rig, null, missing: MissingCalibration.DeliverNothing);
            Assert.IsFalse(uncalibrated.Calibrated);
            StringAssert.Contains(uncalibrated.Uncalibrated, "deliver nothing");
            var none = PlanReward.Plan(uncalibrated, 2.0);
            Assert.AreEqual(0, none.Count);
            Assert.AreEqual(0.0, none.DeliveredUl);
        }

        [TestMethod]
        public void RefusesAMissingCalibration()
        {
            var message = Refusal(Rig, null);
            StringAssert.Contains(message, "Reward line DO1");
            StringAssert.Contains(message, "no valve calibration");
        }

        [TestMethod]
        public void RefusesAnUnreadableCalibration()
        {
            StringAssert.Contains(Refusal(Rig, "{ not json"), "is unreadable");
            StringAssert.Contains(Refusal(Rig, Edit(Fixture, f => f.Remove("pulseGapMs"))), "is unreadable");
            StringAssert.Contains(Refusal(Rig, Edit(Fixture, f => f["yUnit"] = "mg")), "not ul against ms");
            StringAssert.Contains(Refusal(Rig, Edit(Fixture, f => f["linearFit"]["slope"] = 0)), "cannot be planned with");
        }

        [TestMethod]
        public void RefusesACalibrationWithoutALinearFit()
        {
            StringAssert.Contains(Refusal(Rig, Edit(Fixture, f => f.Remove("linearFit"))), "has no linearFit");
        }

        [TestMethod]
        public void RefusesACalibrationFromAnotherMachine()
        {
            var message = Refusal(Rig, Edit(Fixture, f => f["machineName"] = "OTHER-RIG"));
            StringAssert.Contains(message, "measured on OTHER-RIG, not on " + Machine);
        }

        [TestMethod]
        public void RefusesALineThatIsNotACalibratedPulse()
        {
            StringAssert.Contains(Refusal(Rig, Fixture, "right"), "Reward line DO2 (the right RewardValve on BehaviorBoard-Main) is a FixedPulse, not a CalibratedPulse");
            StringAssert.Contains(Refusal(Rig, Fixture, "middle"), "names no artefact");
        }

        [TestMethod]
        public void RefusesASideTheWiringDoesNotGiveExactlyOneValve()
        {
            StringAssert.Contains(Refusal(Rig, Fixture, "back"), "has no RewardValve with side back");
            var twice = Rig.Replace("side: right", "side: left");
            StringAssert.Contains(Refusal(twice, Fixture), "2 RewardValves with side left");
            var notPulsed = Rig.Replace("DO1: {device", "DO0: {device");
            StringAssert.Contains(Refusal(notPulsed, Fixture), "is not a pulse line");
            StringAssert.Contains(Refusal("behaviorBoard: [", Fixture), "could not be read");
        }

        [TestMethod]
        public void DeliverNothingAppliesOnlyToAMissingFile()
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                Load(Rig, Edit(Fixture, f => f.Remove("linearFit")), missing: MissingCalibration.DeliverNothing));
        }
    }
}
