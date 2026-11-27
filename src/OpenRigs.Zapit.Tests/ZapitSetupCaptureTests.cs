using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;

namespace OpenRigs.Zapit.Tests
{
    [TestClass]
    public class ZapitSetupCaptureTests
    {
        sealed class Rig : IDisposable
        {
            public readonly string Pending = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "pending");
            public readonly Subject<string> Grids = new Subject<string>(), Stills = new Subject<string>(), Banks = new Subject<string>(), Aims = new Subject<string>();
            public readonly Subject<ZapitDaqEvent> Events = new Subject<ZapitDaqEvent>();
            public readonly Subject<IplImage> Frames = new Subject<IplImage>();
            public readonly List<ZapitSetupStep> Steps = new List<ZapitSetupStep>();
            public Exception Error;
            readonly IDisposable subscription;
            byte shade;

            public Rig(TimeSpan? frameTimeout = null, double ceilingMw = 10, double laserCeilingVolts = 5)
            {
                Directory.CreateDirectory(Pending);
                subscription = new ZapitSetupCapture { PendingFolder = Pending, FrameTimeout = frameTimeout ?? TimeSpan.FromSeconds(2), CeilingMw = ceilingMw, LaserMaxVolts = 5, LaserCeilingVolts = laserCeilingVolts }
                    .Process(Grids, Stills, Banks, Events, Frames, Aims)
                    .Subscribe(step => { Steps.Add(step); if (step.Command?.Kind == ZapitDaqCommandKind.Point) Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Pointed }); }, e => Error = e);
            }

            public string Run(string name, string grid)
            {
                var folder = Path.Combine(Pending, "ZapitScanner", name);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "grid.json"), grid);
                return folder;
            }

            public void Frame()
            {
                var image = new IplImage(new Size(8, 6), IplDepth.U8, 1);
                image.Set(Scalar.All(++shade));
                Frames.OnNext(image);
            }

            public void Dispose()
            {
                subscription.Dispose();
                Directory.Delete(Path.GetDirectoryName(Pending), true);
            }
        }

        // Audit 24 Nov 2026 L2: Go (the aims ending) during a grid run ends it, zeroed; no laser-on Point follows.
        [TestMethod]
        public void GoDuringAGridRunEndsItWithTheLinesZeroed()
        {
            using var rig = new Rig();
            var folder = rig.Run("Frames_T", "{\"volts\": [[1, 2], [-1, 0.5], [2, 2]], \"laserVolts\": 0.3, \"skipFrames\": 0, \"backgrounds\": 1}");
            rig.Grids.OnNext(folder);
            var before = rig.Steps.Count;
            rig.Grids.OnCompleted(); rig.Stills.OnCompleted(); rig.Banks.OnCompleted(); rig.Aims.OnCompleted();
            for (int i = 0; i < 6; i++) rig.Frame();
            var after = rig.Steps.Skip(before).ToList();
            Assert.AreEqual(1, after.Count, string.Join("; ", after));
            StringAssert.StartsWith(after[0].ToString(), "Refused Go came during a setup step");
            Assert.AreEqual(ZapitDaqCommandKind.Zero, after[0].Command.Kind);
        }

        // Audit 24 Nov 2026 L6: a grid's laserVolts is held to the aim's rules and refused, never sent to ZapitDaq.
        [TestMethod]
        public void AGridsLaserVoltsOutsideTheRigsLimitsAreRefused()
        {
            foreach (var (laser, ceilingMw, ceilingVolts, why) in new[] { ("7", 10.0, 5.0, "0 to 5 V"), ("-0.1", 10.0, 5.0, "0 to 5 V"), ("0.3", 0.0, 5.0, "ceilingMw is 0"), ("0.6", 10.0, 0.5, "ceiling's 0.5 V") })
            {
                using var rig = new Rig(ceilingMw: ceilingMw, laserCeilingVolts: ceilingVolts);
                rig.Grids.OnNext(rig.Run("Frames_T", "{\"volts\": [[1, 2]], \"laserVolts\": " + laser + "}"));
                Assert.IsNull(rig.Error);
                Assert.AreEqual("Refused", rig.Steps.Single().Stage);
                StringAssert.Contains(rig.Steps.Single().Detail, why);
                Assert.IsNull(rig.Steps.Single().Command);
            }
        }

        // Audit 24 Nov 2026 C3: a still with no camera frame is refused after FrameTimeout, and the next setup step is taken.
        [TestMethod]
        public void AStillWithNoFrameIsRefusedAndTheNextStepTaken()
        {
            using var rig = new Rig(frameTimeout: TimeSpan.FromMilliseconds(50));
            rig.Stills.OnNext(Path.Combine(rig.Pending, "still.png"));
            for (int i = 0; i < 100 && rig.Steps.Count < 2; i++) System.Threading.Thread.Sleep(10);
            StringAssert.StartsWith(rig.Steps.Last().ToString(), "Refused no camera frame came");
            rig.Stills.OnNext(Path.Combine(rig.Pending, "still2.png"));
            Assert.AreEqual("Pointing", rig.Steps.Last().Stage);
        }

        // Audit 24 Nov 2026 L1: an aim above the ceiling's volts (LaserCeilingVolts) is refused, below laserMaxVolts.
        [TestMethod]
        public void AnAimAboveTheCeilingsVoltsIsRefused()
        {
            using var rig = new Rig(laserCeilingVolts: 0.5);
            rig.Aims.OnNext("1,1,0.6");
            StringAssert.Contains(rig.Steps.Single().Detail, "ceiling's 0.5 V");
            rig.Aims.OnNext("1,1,0.5");
            Assert.AreEqual("Aiming", rig.Steps.Last().Stage);
        }

        // Audit C R5 / D R7 (probe Q3): the setup has ZapitDaq's 1e-4 V slack, so an aim or grid at the ceiling's full-precision
        // volts, the launch's rounded to 6 figures, is taken as the DAQ takes it; beyond the slack it is still refused.
        [TestMethod]
        public void LaserVoltsAtTheCeilingGetTheDaqsSlack()
        {
            using (var rig = new Rig(laserCeilingVolts: 3.14159))
            {
                rig.Aims.OnNext("0,0,3.14159");
                Assert.AreEqual("Aiming", rig.Steps.Last().Stage, rig.Steps.Last().ToString());
                rig.Aims.OnNext("0,0,3.1415927");
                Assert.AreEqual("Aiming", rig.Steps.Last().Stage, rig.Steps.Last().ToString());
                rig.Aims.OnNext("0,0,3.1418");
                Assert.AreEqual("Refused", rig.Steps.Last().Stage);
            }
            using (var rig = new Rig(laserCeilingVolts: 3.14159))
            {
                rig.Grids.OnNext(rig.Run("Frames_T", "{\"volts\": [[1, 2]], \"laserVolts\": 3.1415927}"));
                Assert.AreEqual("Pointing", rig.Steps.Single().Stage, rig.Steps.Single().ToString());
            }
        }

        // Audit C R6 (probe Q4): ZapitDaq's Refused for a bank (over the ceiling, as at LaserCeilingVolts 0) is reported as the
        // Refused stage with its reason; the session runs on.
        [TestMethod]
        public void ABankZapitDaqRefusesIsReportedRefused()
        {
            using var rig = new Rig();
            var bank = Path.Combine(rig.Pending, "Settings_ZapitBank_T.json");
            rig.Banks.OnNext(bank);
            Assert.AreEqual("Loading " + bank, rig.Steps.Last().ToString());
            rig.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Refused, Detail = "The Zapit bank's samples drive the laser to 5 V, above the ceiling's 0 V" });
            Assert.AreEqual("Refused The Zapit bank's samples drive the laser to 5 V, above the ceiling's 0 V", rig.Steps.Last().ToString());
            rig.Banks.OnNext(bank);
            rig.Events.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.BankLoaded });
            Assert.AreEqual("Loading", rig.Steps.Last().Stage);
            Assert.IsNull(rig.Error);
        }

        // Two points with one frame skipped, two backgrounds: each point's frame after the skip, the backgrounds laser off.
        [TestMethod]
        public void AGridRunSavesAFramePerPointThenTheBackgrounds()
        {
            using var rig = new Rig();
            var folder = rig.Run("Frames_T", "{\"volts\": [[1, 2], [-1, 0.5]], \"laserVolts\": 0.3, \"skipFrames\": 1, \"backgrounds\": 2}");
            rig.Grids.OnNext(folder);
            for (int i = 0; i < 8; i++) rig.Frame();
            Assert.IsNull(rig.Error, rig.Error?.ToString());
            CollectionAssert.AreEqual(new[] { "background_0.png", "background_1.png", "point_0000.png", "point_0001.png" },
                Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).OrderBy(n => n).ToArray());
            var points = rig.Steps.Where(s => s.Command?.Kind == ZapitDaqCommandKind.Point).Select(s => s.Command).ToList();
            CollectionAssert.AreEqual(new[] { 1.0, -1.0, -1.0 }, points.Select(c => c.GalvoXVolts).ToArray());
            CollectionAssert.AreEqual(new[] { 0.3, 0.3, 0.0 }, points.Select(c => c.LaserVolts).ToArray(), "backgrounds laser off");
            Assert.AreEqual(2, (int)CV.LoadImage(Path.Combine(folder, "point_0000.png"), LoadImageFlags.Grayscale)[0, 0].Val0, "the frame after the skipped one");
            var last = rig.Steps.Last();
            Assert.AreEqual("FramesSaved " + folder, last.ToString());
            Assert.AreEqual(ZapitDaqCommandKind.Zero, last.Command.Kind);
        }

        [TestMethod]
        public void AStillSavesTheNextFrameLaserOff()
        {
            using var rig = new Rig();
            var still = Path.Combine(rig.Pending, "still_T.png");
            rig.Stills.OnNext(still);
            Assert.AreEqual(ZapitDaqCommandKind.Zero, rig.Steps.Single().Command.Kind);
            rig.Frame();
            Assert.IsTrue(File.Exists(still));
            Assert.AreEqual("StillSaved " + still, rig.Steps.Last().ToString());
        }

        [TestMethod]
        public void RequestsOutsidePendingOrWhileBusyAreRefusedNotFatal()
        {
            using var rig = new Rig();
            rig.Grids.OnNext(Path.GetTempPath());
            rig.Stills.OnNext(Path.Combine(rig.Pending, "..", "elsewhere.png"));
            var folder = rig.Run("Frames_T", "{\"volts\": [[11, 0]], \"laserVolts\": 0.3}");
            rig.Grids.OnNext(folder);
            var good = rig.Run("Frames_U", "{\"volts\": [[1, 0]], \"laserVolts\": 0.3}");
            rig.Grids.OnNext(good);
            rig.Stills.OnNext(Path.Combine(rig.Pending, "still.png"));
            Assert.IsNull(rig.Error);
            var refused = rig.Steps.Where(s => s.Stage == "Refused").Select(s => s.Detail).ToList();
            Assert.AreEqual(4, refused.Count, string.Join("; ", refused));
            StringAssert.Contains(refused[2], "within +/-10 V");
            StringAssert.Contains(refused[3], "in progress");
        }

        [TestMethod]
        public void ABankInsidePendingIsLoadedOneOutsideIsRefusedNotFatal()
        {
            using var rig = new Rig();
            var bank = Path.Combine(rig.Pending, "Settings_ZapitBank_T.json");
            rig.Banks.OnNext("\"" + bank + "\"");
            rig.Banks.OnNext(Path.Combine(rig.Pending, "..", "Settings_ZapitBank_T.json"));
            rig.Banks.OnNext(Path.Combine(rig.Pending, "Settings_ZapitBank_T.bin"));
            Assert.IsNull(rig.Error);
            Assert.AreEqual(3, rig.Steps.Count, string.Join("; ", rig.Steps));
            Assert.AreEqual("Loading " + bank, rig.Steps[0].ToString());
            Assert.AreEqual(ZapitDaqCommandKind.LoadBank, rig.Steps[0].Command.Kind);
            Assert.AreEqual(bank, rig.Steps[0].Command.Path);
            Assert.IsTrue(rig.Steps.Skip(1).All(s => s.Stage == "Refused" && s.Command == null && s.Detail.Contains("inside " + rig.Pending)), string.Join("; ", rig.Steps));
        }

        // A stalled camera never holds the beam: no frame within FrameTimeout of Pointed refuses the run and zeroes the lines.
        [TestMethod]
        public void ARunWithNoFrameAfterPointedIsRefusedAndZeroed()
        {
            using var rig = new Rig(TimeSpan.FromMilliseconds(100));
            var folder = rig.Run("Frames_T", "{\"volts\": [[1, 2], [-1, 0.5]], \"laserVolts\": 0.3, \"skipFrames\": 1}");
            rig.Grids.OnNext(folder);
            rig.Frame();   // the skipped frame: the next must come within the timeout too
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !rig.Steps.Any(s => s.Stage == "Refused")) System.Threading.Thread.Sleep(10);
            Assert.IsNull(rig.Error, rig.Error?.ToString());
            var refused = rig.Steps.Last();
            Assert.AreEqual("Refused", refused.Stage, string.Join("; ", rig.Steps));
            StringAssert.Contains(refused.Detail, "no camera frame came within 0.1 s");
            Assert.AreEqual(ZapitDaqCommandKind.Zero, refused.Command.Kind);
            rig.Frame();
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.png").Length, "the refused run saves nothing more");
            rig.Grids.OnNext(rig.Run("Frames_U", "{\"volts\": [[1, 0]], \"laserVolts\": 0.3}"));
            Assert.AreEqual("Pointing", rig.Steps.Last(s => s.Command?.Kind == ZapitDaqCommandKind.Point || s.Stage == "Refused").Stage, "a new run may start");
        }

        // A run whose frames come in time is never refused, however long it lasts in all.
        [TestMethod]
        public void FramesInTimeKeepTheRunGoing()
        {
            using var rig = new Rig(TimeSpan.FromMilliseconds(300));
            var folder = rig.Run("Frames_T", "{\"volts\": [[1, 2], [-1, 0.5]], \"laserVolts\": 0.3, \"skipFrames\": 1, \"backgrounds\": 2}");
            rig.Grids.OnNext(folder);
            for (int i = 0; i < 8; i++) { System.Threading.Thread.Sleep(100); rig.Frame(); }
            System.Threading.Thread.Sleep(500);
            Assert.IsFalse(rig.Steps.Any(s => s.Stage == "Refused"), string.Join("; ", rig.Steps));
            Assert.AreEqual("FramesSaved " + folder, rig.Steps.Last().ToString());
        }

        // A path Windows cannot parse is refused, not an error that stops the workflow.
        [TestMethod]
        public void AnIllegalPathIsRefusedNotFatal()
        {
            using var rig = new Rig();
            rig.Banks.OnNext(rig.Pending + "\\a<b>|.json");
            rig.Stills.OnNext(rig.Pending + "\\a:b:c.png");
            rig.Grids.OnNext("\0");
            Assert.IsNull(rig.Error, rig.Error?.ToString());
            Assert.AreEqual(3, rig.Steps.Count(s => s.Stage == "Refused" && s.Command == null), string.Join("; ", rig.Steps));
        }

        // A grid.json camera block: the run's first step sets the grid's exposure and gain, its last puts the camera's own back.
        [TestMethod]
        public void AGridRunWithACameraBlockSetsItsExposureThenPutsTheCamerasBack()
        {
            using var rig = new Rig();
            var folder = rig.Run("Frames_T", "{\"volts\": [[1, 2], [-1, 0.5]], \"laserVolts\": 0.3, \"skipFrames\": 0, \"backgrounds\": 1, \"camera\": {\"exposureUs\": 2000, \"gainDb\": 0}}");
            rig.Grids.OnNext(folder);
            for (int i = 0; i < 4; i++) rig.Frame();
            Assert.IsNull(rig.Error, rig.Error?.ToString());
            var camera = rig.Steps.Where(s => s.Camera != null).ToList();
            Assert.AreEqual(2, camera.Count, string.Join("; ", rig.Steps));
            Assert.AreSame(rig.Steps[0], camera[0], "set with the first point, before its frames");
            Assert.AreEqual(2000.0, camera[0].Camera.ExposureUs);
            Assert.AreEqual(0.0, camera[0].Camera.GainDb);
            Assert.IsFalse(camera[0].Camera.Restore);
            Assert.AreEqual("FramesSaved " + folder, camera[1].ToString());
            Assert.IsTrue(camera[1].Camera.Restore);
        }

        // No camera block: the session's settings are left alone, as before.
        [TestMethod]
        public void AGridRunWithoutACameraBlockLeavesTheCameraAlone()
        {
            using var rig = new Rig();
            rig.Grids.OnNext(rig.Run("Frames_T", "{\"volts\": [[1, 2]], \"laserVolts\": 0.3, \"skipFrames\": 0, \"backgrounds\": 1}"));
            for (int i = 0; i < 3; i++) rig.Frame();
            Assert.AreEqual("FramesSaved", rig.Steps.Last().Stage);
            Assert.IsTrue(rig.Steps.All(s => s.Camera == null));
        }

        // A camera block out of range is refused; a run refused for a stalled camera puts the camera's settings back.
        [TestMethod]
        public void ABadCameraBlockIsRefusedAndAStalledRunPutsTheCameraBack()
        {
            using var rig = new Rig(TimeSpan.FromMilliseconds(100));
            rig.Grids.OnNext(rig.Run("Frames_T", "{\"volts\": [[1, 2]], \"laserVolts\": 0.3, \"camera\": {\"exposureUs\": 0, \"gainDb\": 0}}"));
            StringAssert.Contains(rig.Steps.Single().Detail, "exposureUs must be");
            rig.Grids.OnNext(rig.Run("Frames_U", "{\"volts\": [[1, 2]], \"laserVolts\": 0.3, \"camera\": {\"exposureUs\": 2000, \"gainDb\": 0}}"));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && rig.Steps.Count(s => s.Stage == "Refused") < 2) System.Threading.Thread.Sleep(10);
            var refused = rig.Steps.Last();
            Assert.AreEqual("Refused", refused.Stage, string.Join("; ", rig.Steps));
            Assert.IsTrue(refused.Camera.Restore);
        }

        sealed class Features : ICameraFeatures
        {
            public readonly Dictionary<string, object> Values;
            public readonly List<string> Writes = new List<string>();
            public Features(Dictionary<string, object> values) => Values = values;
            public bool Has(string name) => Values.ContainsKey(name);
            // Like a Basler camera: with an auto mode Continuous, the exposure (or gain) exists and reads but cannot be written.
            bool Auto(string name) => Values.TryGetValue(name, out var mode) && (string)mode == "Continuous";
            void CheckWritable(string name)
            {
                if ((name.StartsWith("ExposureTime") && Auto("ExposureAuto")) || (name.StartsWith("Gain") && Auto("GainAuto")))
                    throw new InvalidOperationException(name + " is not writable");
            }
            public string GetEnum(string name) => (string)Values[name];
            public void SetEnum(string name, string value) { Writes.Add(name + "=" + value); Values[name] = value; }
            public double GetFloat(string name) => (double)Values[name];
            public void SetFloat(string name, double value) { CheckWritable(name); Writes.Add(name + "=" + value); Values[name] = value; }
            public long GetInteger(string name) => (long)Values[name];
            public void SetInteger(string name, long value) { Writes.Add(name + "=" + value); Values[name] = value; }
        }

        // Auto exposure Continuous makes ExposureTime read-only: the auto modes go off first and are put back last.
        [TestMethod]
        public void CameraExposureTurnsAutoOffBeforeSettingAReadOnlyExposure()
        {
            var camera = new Features(new Dictionary<string, object> { ["ExposureTime"] = 31000.0, ["Gain"] = 2.0, ["DigitalShift"] = 4L, ["ExposureAuto"] = "Continuous", ["GainAuto"] = "Continuous" });
            var exposure = new CameraExposure(camera);
            exposure.Apply(new ZapitCameraSettings { ExposureUs = 2000, GainDb = 0 });
            Assert.AreEqual("Off", camera.Values["ExposureAuto"]);
            Assert.AreEqual(2000.0, camera.Values["ExposureTime"]);
            camera.Writes.Clear();
            exposure.Apply(new ZapitCameraSettings { Restore = true });
            Assert.AreEqual(31000.0, camera.Values["ExposureTime"]);
            Assert.AreEqual(2.0, camera.Values["Gain"]);
            Assert.AreEqual(4L, camera.Values["DigitalShift"]);
            CollectionAssert.AreEqual(new[] { "ExposureTime=31000", "Gain=2", "DigitalShift=4", "ExposureAuto=Continuous", "GainAuto=Continuous" }, camera.Writes);
        }

        // A session .pfs DigitalShift is 0 for the grid and back after.
        [TestMethod]
        public void CameraExposureZeroesDigitalShiftForTheGridAndPutsItBack()
        {
            var camera = new Features(new Dictionary<string, object> { ["ExposureTime"] = 31000.0, ["Gain"] = 2.0, ["DigitalShift"] = 4L });
            var exposure = new CameraExposure(camera);
            exposure.Apply(new ZapitCameraSettings { ExposureUs = 2000, GainDb = 0 });
            Assert.AreEqual(0L, camera.Values["DigitalShift"]);
            exposure.Apply(new ZapitCameraSettings { ExposureUs = 1500, GainDb = 1 });
            exposure.Apply(new ZapitCameraSettings { Restore = true });
            Assert.AreEqual(4L, camera.Values["DigitalShift"]);
        }

        // Auto off before the fixed values; the camera's own values back, then its auto modes; a second grid keeps the first saved.
        [TestMethod]
        public void CameraExposureSetsFixedValuesAutoOffAndPutsBackTheSessions()
        {
            var camera = new Features(new Dictionary<string, object> { ["ExposureTime"] = 31000.0, ["Gain"] = 2.0, ["ExposureAuto"] = "Continuous", ["GainAuto"] = "Off" });
            var exposure = new CameraExposure(camera);
            exposure.Apply(new ZapitCameraSettings { ExposureUs = 2000, GainDb = 0 });
            CollectionAssert.AreEqual(new[] { "ExposureAuto=Off", "GainAuto=Off", "ExposureTime=2000", "Gain=0" }, camera.Writes);
            exposure.Apply(new ZapitCameraSettings { ExposureUs = 1500, GainDb = 1 });
            exposure.Apply(new ZapitCameraSettings { Restore = true });
            Assert.AreEqual(31000.0, camera.Values["ExposureTime"]);
            Assert.AreEqual(2.0, camera.Values["Gain"]);
            Assert.AreEqual("Continuous", camera.Values["ExposureAuto"]);
            CollectionAssert.AreEqual(new[] { "ExposureTime=31000", "Gain=2", "ExposureAuto=Continuous", "GainAuto=Off" }, camera.Writes.Skip(8).ToArray());
            camera.Writes.Clear();
            exposure.Apply(new ZapitCameraSettings { Restore = true });
            Assert.AreEqual(0, camera.Writes.Count, "nothing to put back twice");
        }

        // An older GigE camera's names; a camera with neither stops the workflow naming them.
        [TestMethod]
        public void CameraExposureTakesGigENamesAndRefusesACameraWithNone()
        {
            var gige = new Features(new Dictionary<string, object> { ["ExposureTimeAbs"] = 9000.0, ["GainAbs"] = 1.0 });
            new CameraExposure(gige).Apply(new ZapitCameraSettings { ExposureUs = 2000, GainDb = 0 });
            Assert.AreEqual(2000.0, gige.Values["ExposureTimeAbs"]);
            var none = new CameraExposure(new Features(new Dictionary<string, object> { ["Gain"] = 0.0 }));
            var error = Assert.ThrowsException<InvalidOperationException>(() => none.Apply(new ZapitCameraSettings { ExposureUs = 2000 }));
            StringAssert.Contains(error.Message, "ExposureTime, ExposureTimeAbs");
        }

        // GridApplied (which picks the Mono12 frames' Mono8 scale) holds from a grid's settings until they are put back.
        [TestMethod]
        public void CameraExposureIsGridAppliedFromTheGridsSettingsUntilRestore()
        {
            var exposure = new CameraExposure(new Features(new Dictionary<string, object> { ["ExposureTime"] = 3000.0, ["Gain"] = 0.0 }));
            Assert.IsFalse(exposure.GridApplied);
            exposure.Apply(new ZapitCameraSettings { ExposureUs = 2000, GainDb = 0 });
            Assert.IsTrue(exposure.GridApplied);
            exposure.Apply(new ZapitCameraSettings { Restore = true });
            Assert.IsFalse(exposure.GridApplied);
        }

        static IplImage Row(IplDepth depth, params int[] values)
        {
            var image = new IplImage(new Size(values.Length, 1), depth, 1);
            for (int i = 0; i < values.Length; i++) image[0, i] = new Scalar(values[i]);
            return image;
        }

        static int[] Values(IplImage image) => Enumerable.Range(0, image.Width).Select(i => (int)image[0, i].Val0).ToArray();

        // Mono12 as Bonsai.Pylon gives it (16-bit, raw 0-4095 counts): session frames stretched 0 to the top to 255
        // (B2's dim view peaks near 46); a grid run's frames are Mono8's scale, counts over 16.
        [TestMethod]
        public void Mono12FramesAreStretchedMinToMaxAndAGridRunsOver16()
        {
            var dim = Row(IplDepth.U16, 10, 11, 28, 46);
            var session = ZapitFrameDepth.ToEightBit(dim, mono8Scale: false, top: 46);
            Assert.AreEqual(IplDepth.U8, session.Depth);
            Assert.AreEqual(1, session.Channels);
            CollectionAssert.AreEqual(new[] { 55, 61, 155, 255 }, Values(session));
            CollectionAssert.AreEqual(new[] { 7, 7 }, Values(ZapitFrameDepth.ToEightBit(Row(IplDepth.U16, 7, 7), false, 0)));
            var mono12 = Row(IplDepth.U16, 0, 1, 2, 45, 46, 255, 256, 4095);
            var grid = ZapitFrameDepth.ToEightBit(mono12, mono8Scale: true, top: 0);
            Assert.AreEqual(IplDepth.U8, grid.Depth);
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 3, 3, 16, 16, 255 }, Values(grid));
        }

        // The stretch's top is the frame averaged 8 x 8 at its WhitePercentile: one hot pixel (4000 in a field of 40) is
        // above it and leaves it at 40 (27 Nov 2026: the view brightened and dimmed with single pixels).
        [TestMethod]
        public void StretchTopIsAPercentileOfTheFrameAveragedEightByEight()
        {
            var frame = new IplImage(new Size(64, 64), IplDepth.U16, 1);

            frame.Set(new Scalar(40));
            frame[10, 10] = new Scalar(4000);
            var top = ZapitFrameDepth.StretchTop(frame);
            Assert.AreEqual(40, top, 0.01);   // the hot pixel's 8 x 8 cell is above the WhitePercentile
            CollectionAssert.AreEqual(new[] { 255 }, Values(ZapitFrameDepth.ToEightBit(Row(IplDepth.U16, 4000), false, top)));
        }

        // A near-dark frame (lights off, 1 to 5 counts) has its top held at MinTop, so it is not stretched to noise.
        [TestMethod]
        public void StretchTopHasAFloorSoADarkFrameIsNotAmplified()
        {
            var frame = new IplImage(new Size(64, 64), IplDepth.U16, 1);
            frame.Set(new Scalar(3));
            Assert.AreEqual(ZapitFrameDepth.MinTop, ZapitFrameDepth.StretchTop(frame), 0.01);
            CollectionAssert.AreEqual(new[] { 48 }, Values(ZapitFrameDepth.ToEightBit(Row(IplDepth.U16, 3), false, ZapitFrameDepth.StretchTop(frame))));   // 3 of 16, not 255
        }

        // The display and the measurement split (27 Nov 2026): a grid run's shown frame is the stretched picture, cut to
        // the crop, while its still stays whole at Mono8's scale for the spot detection; outside a grid run the still is
        // the stretched picture, whole.
        [TestMethod]
        public void AFrameIsShownStretchedAndCroppedItsStillWholeAtMono8sScaleInAGridRun()
        {
            var frame = new IplImage(new Size(64, 48), IplDepth.U16, 1);
            frame.Set(new Scalar(48));
            frame[20, 30] = new Scalar(1600);   // the beam
            var crop = new Rect(16, 8, 32, 24);
            ZapitPylonCapture.Split(frame, true, crop, out var shown, out var still, out _);
            Assert.AreEqual(new Size(32, 24), shown!.Size);
            Assert.AreEqual(255, (int)shown[20 - 8, 30 - 16].Val0);   // stretched: the beam white
            Assert.AreEqual(255, (int)shown[0, 0].Val0);               // and the skull's 48 at the stretch's top
            Assert.AreEqual(new Size(64, 48), still!.Size);
            Assert.AreEqual(100, (int)still[20, 30].Val0);             // counts over 16
            Assert.AreEqual(3, (int)still[0, 0].Val0);
            ZapitPylonCapture.Split(frame, false, default, out shown, out still, out _);
            Assert.AreEqual(new Size(64, 48), shown!.Size);
            Assert.AreSame(shown, still);
            var mono8 = new IplImage(new Size(64, 48), IplDepth.U8, 1);
            ZapitPylonCapture.Split(mono8, true, crop, out shown, out still, out _);
            Assert.AreSame(mono8, still);
            Assert.AreEqual(new Size(32, 24), shown!.Size);
        }

        // A crop is cut exactly as asked; none, or the whole frame, is the frame itself; one that does not fit the frame
        // is refused with its reason and the frame returned whole, never clamped.
        [TestMethod]
        public void ACropFitsInsideTheFrameElseIsRefused()
        {
            var frame = new IplImage(new Size(100, 60), IplDepth.U8, 1);
            frame.Set(new Scalar(0));
            frame[30, 50] = new Scalar(9);
            Assert.AreSame(frame, ZapitFrameDepth.Crop(frame, default, out var refusal));
            Assert.IsNull(refusal);
            Assert.AreSame(frame, ZapitFrameDepth.Crop(frame, new Rect(0, 0, 100, 60), out refusal));
            Assert.IsNull(refusal);
            var cut = ZapitFrameDepth.Crop(frame, new Rect(40, 20, 20, 16), out refusal);
            Assert.IsNull(refusal);
            Assert.AreEqual(new Size(20, 16), cut.Size);
            Assert.AreEqual(9, (int)cut[10, 10].Val0);
            Assert.AreEqual(new Size(30, 20), ZapitFrameDepth.Crop(frame, new Rect(70, 40, 30, 20), out refusal).Size);   // to the frame's edge
            Assert.IsNull(refusal);
            foreach (var bad in new[] { new Rect(3000, 2000, 800, 600), new Rect(70, 40, 64, 48), new Rect(-2, 0, 20, 20), new Rect(0, 0, 102, 60), new Rect(0, 0, 100, 62) })
            {
                Assert.AreSame(frame, ZapitFrameDepth.Crop(frame, bad, out refusal), bad.ToString());
                StringAssert.Contains(refusal, "100 x 60");
            }
        }

        // The camera answers a crop only from a frame cut to it: Cropped for the first frame at a new crop, Full Frame
        // for the whole frame put back, Refused (the crop dropped, once) for one outside the frame, nothing otherwise.
        [TestMethod]
        public void TheCropIsAnsweredFromTheFirstFrameCutToIt()
        {
            var state = new ZapitCropState();
            var frame = new IplImage(new Size(64, 48), IplDepth.U8, 1);
            Assert.IsNull(state.Finish(state.Current, null));   // no request, no answer
            var fits = new Rect(16, 8, 32, 24);
            state.Set(fits, 1);
            Assert.AreEqual("Cropped #1 16,8,32,24", Say(state.Finish(fits, ZapitFrameDepth.Refusal(frame.Size, fits))));
            Assert.IsNull(state.Finish(fits, null));   // once
            state.Set(default, 2);
            Assert.AreEqual("Full Frame #2", Say(state.Finish(default, null)));
            var off = new Rect(3000, 2000, 800, 600);
            state.Set(off, 3);
            var step = Say(state.Finish(off, ZapitFrameDepth.Refusal(frame.Size, off)));
            StringAssert.StartsWith(step, "Refused #3 the crop 3000,2000,800,600 does not fit");
            StringAssert.EndsWith(step, "; Now Full Frame");
            Assert.AreEqual(default(Rect), state.Current);   // reset once, to the whole frame
            Assert.IsNull(state.Finish(default, null));
            // A request that was no crop keeps the crop in force and says so, under its own number.
            state.Set(fits, 4);
            state.Finish(fits, null);
            state.Reject("a crop is x,y,w,h", 5);
            Assert.AreEqual("Refused #5 a crop is x,y,w,h; Now Cropped 16,8,32,24", Say(state.Finish(fits, null)));
            Assert.AreEqual(fits, state.Current);
            Assert.IsNull(state.Finish(fits, null));   // once
            state.Set(default, 6);
            state.Finish(default, null);
            state.Reject("a crop request is #<n> x,y,w,h", 0);
            Assert.AreEqual("Refused #0 a crop request is #<n> x,y,w,h; Now Full Frame", Say(state.Finish(default, null)));
        }

        // The answers as one text, in order, for a frame's reports.
        static string? Say(ZapitSetupStep[]? steps) => steps == null ? null : string.Join(" | ", steps.Select(step => step.ToString()));

        // A request refused before any frame has answered the crop set before it: the frame answers both, the crop first.
        [TestMethod]
        public void ARejectedRequestIsAnsweredAfterTheCropBeforeIt()
        {
            var state = new ZapitCropState();
            var a = new Rect(16, 8, 32, 24);
            state.Set(a, 1);
            state.Reject("a crop is x,y,w,h", 2);
            Assert.AreEqual("Cropped #1 16,8,32,24 | Refused #2 a crop is x,y,w,h; Now Cropped 16,8,32,24", Say(state.Finish(a, null)));
            Assert.IsNull(state.Finish(a, null));
            // A first crop outside the frame is dropped before the rejection is answered: that one is Now Full Frame.
            var off = new Rect(3000, 2000, 800, 600);
            state.Set(off, 3);
            state.Reject("a crop is x,y,w,h", 4);
            var both = Say(state.Finish(off, "does not fit"));
            Assert.AreEqual("Refused #3 does not fit; Now Full Frame | Refused #4 a crop is x,y,w,h; Now Full Frame", both);
        }

        // A crop sent after a rejected request, before a frame, is the one answered: Cropped, not the older Refused.
        [TestMethod]
        public void ACropAfterARejectedRequestIsAnsweredCropped()
        {
            var state = new ZapitCropState();
            var b = new Rect(16, 8, 32, 24);
            state.Reject("a crop is x,y,w,h", 1);
            state.Set(b, 2);
            Assert.AreEqual("Cropped #2 16,8,32,24", Say(state.Finish(b, null)));
            Assert.IsNull(state.Finish(b, null));
        }

        // A frame cut to a crop that has changed since is not that crop's answer, and does not reset the newer crop.
        [TestMethod]
        public void AnAnswerForAnOlderCropNeitherReportsNorResetsTheNewerOne()
        {
            var state = new ZapitCropState();
            var old = new Rect(3000, 2000, 800, 600);
            state.Set(old, 1);
            var taken = state.Current;
            var newer = new Rect(16, 8, 32, 24);
            state.Set(newer, 2);   // arrives while the frame cut to the old crop is in flight
            Assert.IsNull(state.Finish(taken, "does not fit"));
            Assert.AreEqual(newer, state.Current);
            Assert.AreEqual("Cropped #2 16,8,32,24", Say(state.Finish(newer, null)));
            state.Reset();
            Assert.AreEqual(default(Rect), state.Current);
            Assert.IsNull(state.Finish(default, null));
        }

        // Zapit-Main.Crop's args: #n x,y,w,h in pixels, or #n full for the whole frame; anything else is a request the camera
        // refuses (not fatal), numbered #0 without a valid number.
        [TestMethod]
        public void ACropRequestIsNumberedPixelsOrFullElseRefused()
        {
            var cropped = ZapitCropRequest.Parse("#7 392,232,1152,864");
            Assert.AreEqual("Cropping #7 392,232,1152,864", cropped.ToString());
            Assert.IsTrue(cropped.Camera!.SetCrop);
            Assert.AreEqual(7, cropped.Camera.CropRequest);
            Assert.AreEqual(1152, cropped.Camera.CropWidth);
            Assert.AreEqual("crop 392,232,1152,864", cropped.Camera.ToString());
            foreach (var full in new[] { "#3 full", " \"#3 Full\" " })
            {
                var step = ZapitCropRequest.Parse(full);
                Assert.AreEqual("Cropping #3 Full Frame", step.ToString());
                Assert.IsTrue(step.Camera!.SetCrop);
                Assert.AreEqual(3, step.Camera.CropRequest);
                Assert.AreEqual(0, step.Camera.CropWidth);
            }
            foreach (var bad in new[] { "#2 1,2,3", "#2 a,b,c,d", "#2 -2,0,64,48", "#2 0,0,63,48", "#2 0,0,8,8", "#2", "#2 " })
            {
                var step = ZapitCropRequest.Parse(bad);
                Assert.AreEqual("Cropping", step.Stage, bad);   // the camera alone knows the crop in force, and answers Refused
                Assert.IsTrue(step.Camera!.SetCrop, bad);
                Assert.AreEqual(2, step.Camera.CropRequest, bad);
                Assert.IsNotNull(step.Camera.RejectReason, bad);
            }
            foreach (var unnumbered in new[] { "", "full", "392,232,1152,864", "#0 full", "#-1 full", "#a full", "# full", "#1.5 full", "#99999999999 full" })
            {
                var step = ZapitCropRequest.Parse(unnumbered);
                Assert.IsTrue(step.ToString().StartsWith("Cropping #0 "), unnumbered + " -> " + step);
                Assert.AreEqual(0, step.Camera!.CropRequest, unnumbered);
                StringAssert.Contains(step.Camera.RejectReason, "#<n>", unnumbered);
            }
        }

        // A Mono8 .pfs (every other rig): the frame itself, untouched, in a grid run or not.
        [TestMethod]
        public void Mono8FramesPassThroughUnchanged()
        {
            var mono8 = Row(IplDepth.U8, 0, 3, 200, 255);
            Assert.AreSame(mono8, ZapitFrameDepth.ToEightBit(mono8, mono8Scale: false, top: 0));
            Assert.AreSame(mono8, ZapitFrameDepth.ToEightBit(mono8, mono8Scale: true, top: 0));
            CollectionAssert.AreEqual(new[] { 0, 3, 200, 255 }, Values(mono8));
        }

        // A colour or other depth (a .pfs set to RGB8 or Mono16 packed oddly) stops the workflow naming PixelFormat.
        [TestMethod]
        public void AFrameNeitherMono8NorMono12IsRefusedNamingPixelFormat()
        {
            var colour = new IplImage(new Size(2, 1), IplDepth.U16, 3);
            var error = Assert.ThrowsException<InvalidOperationException>(() => ZapitFrameDepth.ToEightBit(colour, false, 0));
            StringAssert.Contains(error.Message, "PixelFormat");
        }

        // Hand aiming: each aim points the beam; bad volts, a busy setup step and a disabled laser are refused, not fatal;
        // Go (the aims ending) with the beam on zeroes the lines.
        [TestMethod]
        public void AnAimPointsTheBeamAndGoZeroesIt()
        {
            using var rig = new Rig();
            rig.Aims.OnNext("1.5,-2,0.4");
            rig.Aims.OnNext("11,0,0.4");
            rig.Aims.OnNext("1,0,5.5");
            rig.Aims.OnNext("1;0");
            var folder = rig.Run("Frames_T", "{\"volts\": [[1, 2]], \"laserVolts\": 0.3}");
            rig.Grids.OnNext(folder);
            rig.Aims.OnNext("0,0,0.4");
            Assert.IsNull(rig.Error, rig.Error?.ToString());
            var aims = rig.Steps.Where(s => s.Aim).ToList();
            Assert.AreEqual("Aiming 1.5,-2,0.4", aims[0].ToString());
            Assert.AreEqual("Point(1.5, -2, laser=0.4)", aims[0].Command.ToString());
            CollectionAssert.AreEqual(new[] { "Aiming", "Refused", "Refused", "Refused", "Refused" }, aims.Select(s => s.Stage).ToArray(), string.Join("; ", aims));
            StringAssert.Contains(aims[1].Detail, "+/-10 V");
            StringAssert.Contains(aims[2].Detail, "0 to 5 V");
            StringAssert.Contains(aims[4].Detail, "in progress");
            Assert.IsTrue(aims.Skip(1).All(s => s.Command == null));
            Assert.IsFalse(rig.Steps.Where(s => !s.Aim).Any(s => s.Stage == "Refused"), "the grid run took the aim's Pointed as its own? " + string.Join("; ", rig.Steps));
            rig.Aims.OnCompleted();
            Assert.IsFalse(rig.Steps.Any(s => s.Stage == "Off"), "the grid run ended the aim: no Zero at Go");

            using var other = new Rig();
            other.Aims.OnNext("0,0,0.4");
            other.Aims.OnCompleted();
            Assert.AreEqual("Off Go", other.Steps.Last().ToString());
            Assert.AreEqual(ZapitDaqCommandKind.Zero, other.Steps.Last().Command.Kind);
        }

        [TestMethod]
        public void AnAimIsRefusedWithTheLaserDisabled()
        {
            using var rig = new Rig(ceilingMw: 0);
            rig.Aims.OnNext("0,0,0");
            var step = rig.Steps.Single();
            Assert.IsTrue(step.Aim);
            Assert.AreEqual("Refused", step.Stage);
            StringAssert.Contains(step.Detail, "laser disabled");
            Assert.IsNull(step.Command);
        }

        // Only Zero: nothing can send ZapitDaq an unchecked LoadBank.
        [TestMethod]
        public void CreateZapitDaqCommandMakesOnlyZero()
        {
            var commands = new List<ZapitDaqCommand>();
            using (new CreateZapitDaqCommand().Process(System.Reactive.Linq.Observable.Return("C:\\p\\Settings_ZapitBank_T.json")).Subscribe(commands.Add)) { }
            Assert.AreEqual(ZapitDaqCommandKind.Zero, commands.Single().Kind);
            Assert.IsNull(commands.Single().Path);
            Assert.IsNull(typeof(CreateZapitDaqCommand).GetProperty("Kind"));
        }
    }
}