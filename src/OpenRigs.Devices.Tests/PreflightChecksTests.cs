using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class PreflightChecksTests
    {
        [TestMethod]
        public void TheRowsAreRigYmlsDevicesInKindOrder()
        {
            var rig = LabRigTests.Deserialize(
                "camera:\n  overhead: { serialNumber: '1' }\nbehaviorBoard:\n  main: { portName: COM1 }\n" +
                "stepperDriver:\n  a: { portName: COM2 }\n");
            var rows = PreflightDevices.List(rig, string.Join(",", PreflightDevices.AllKinds));
            CollectionAssert.AreEqual(new[] { "BehaviorBoard-Main", "StepperDriver-A", "Camera-Overhead" }, rows.Select(d => d.Name).ToArray());
            CollectionAssert.AreEqual(new[] { "messages", "messages", "frames" }, rows.Select(d => d.Measure).ToArray());
        }

        [TestMethod]
        public void TheTaskFileGivesTheCountAndTheQuestions()
        {
            var task = PreflightTask.Parse("# written at Go\ncountSeconds: 5\nquestions:\n  speakerArray: Did each speaker play?\n");
            Assert.AreEqual(5, task.CountSeconds);
            Assert.AreEqual("Did each speaker play?", task.Questions["speakerArray"]);
            Assert.AreEqual(0, PreflightTask.Parse("countSeconds: 2\n").Questions.Count);
        }

        [DataTestMethod]
        [DataRow("countSeconds: 5\nseconds: 5\n", "seconds")]
        [DataRow("countSeconds: 0\n", "give 1 to 60")]
        [DataRow("countSeconds: 5\nquestions:\n  wheel: Did it turn?\n", "asks about wheel")]
        public void TheTaskFileRefusesWhatItDoesNotKnow(string text, string message)
        {
            var error = Assert.ThrowsException<Exception>(() =>
            {
                try { PreflightTask.Parse(text); }
                catch (Exception e) { throw new Exception(e.Message + " " + (e.InnerException == null ? "" : e.InnerException.Message)); }
            });
            StringAssert.Contains(error.Message, message);
        }

        [TestMethod]
        public void ADeviceTheRunDoesNotOpenStillHasItsRow()
        {
            var devices = new[]
            {
                new PreflightDevice { Kind = "behaviorBoard", Name = "BehaviorBoard-Main", Measure = "messages" },
                new PreflightDevice { Kind = "licketySplit", Name = "LicketySplit-Left", Measure = "messages" },
            };
            var ticks = new Subject<DeviceTick>();
            var reports = new DeviceReports { OpenSeconds = 0 }.Process(Observable.Return(devices), ticks)
                .Do(_ => ticks.OnNext(new DeviceTick { Device = "BehaviorBoard-Main", Data = true }))
                .Take(4).ToList().Wait().Skip(2).ToList();
            var board = reports.Single(r => r.Device == "BehaviorBoard-Main");
            var missing = reports.Single(r => r.Device == "LicketySplit-Left");
            Assert.IsTrue(board.Settled && board.Error == null && board.Count > 0);
            Assert.IsTrue(missing.Settled);
            StringAssert.Contains(missing.Error, "rig.yml has LicketySplit-Left but this preflight opens none");
        }

        [TestMethod]
        public void ATapSaysItsDeviceIsOpenBeforeItSendsAnything()
        {
            var tick = new PreflightTap { Device = "Camera-Face" }.Process(Observable.Never<int>()).Take(1).Wait();
            Assert.AreEqual("Camera-Face", tick.Device);
            Assert.IsFalse(tick.Data);
        }
    }
}
