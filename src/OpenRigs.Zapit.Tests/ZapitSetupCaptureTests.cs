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