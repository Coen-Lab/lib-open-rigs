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
            public readonly Subject<string> Grids = new Subject<string>(), Stills = new Subject<string>(), Banks = new Subject<string>();
            public readonly Subject<ZapitDaqEvent> Events = new Subject<ZapitDaqEvent>();
            public readonly Subject<IplImage> Frames = new Subject<IplImage>();
            public readonly List<ZapitSetupStep> Steps = new List<ZapitSetupStep>();
            public Exception Error;
            readonly IDisposable subscription;
            byte shade;

            public Rig(TimeSpan? frameTimeout = null)
            {
                Directory.CreateDirectory(Pending);
                subscription = new ZapitSetupCapture { PendingFolder = Pending, FrameTimeout = frameTimeout ?? TimeSpan.FromSeconds(2) }.Process(Grids, Stills, Banks, Events, Frames)
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
            public string GetEnum(string name) => (string)Values[name];
            public void SetEnum(string name, string value) { Writes.Add(name + "=" + value); Values[name] = value; }
            public double GetFloat(string name) => (double)Values[name];
            public void SetFloat(string name, double value) { Writes.Add(name + "=" + value); Values[name] = value; }
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