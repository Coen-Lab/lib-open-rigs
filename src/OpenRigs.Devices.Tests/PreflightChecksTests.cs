using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using OpenRigs.Control;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security;
using System.Threading;
using System.Xml;

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
                .Do(_ => ticks.OnNext(new DeviceTick { Device = "BehaviorBoard-Main" }))
                .Take(4).ToList().Wait().Skip(2).ToList();
            var board = reports.Single(r => r.Device == "BehaviorBoard-Main");
            var missing = reports.Single(r => r.Device == "LicketySplit-Left");
            Assert.IsTrue(board.Settled && board.Error == null && board.Count > 0);
            Assert.IsTrue(missing.Settled);
            StringAssert.Contains(missing.Error, "rig.yml has LicketySplit-Left but nothing has come from it");
        }

        // Tapped is not working: a device whose piece sends nothing has its error, as one the rig list lacks does.
        [TestMethod]
        public void ATappedDeviceThatSendsNothingHasItsError()
        {
            var devices = new[] { new PreflightDevice { Kind = "behaviorBoard", Name = "BehaviorBoard-Main", Measure = "messages" } };
            var ticks = new PreflightTap { Device = "BehaviorBoard-Main" }.Process(Observable.Never<int>());
            var report = new DeviceReports { OpenSeconds = 0 }.Process(Observable.Return(devices), ticks).Take(2).ToList().Wait().Last();
            Assert.IsTrue(report.Settled);
            Assert.IsNotNull(report.Error, "A device that sent nothing was reported as working.");
            StringAssert.Contains(report.Error, "nothing has come from it");
        }

        /// <summary>
        /// The check from Go to Finished, headless, through the controller as rig-control drives it: a board stand-in
        /// counted for countSeconds, its question answered with Confirm, then Finished with the raw file closed and whole.
        /// </summary>
        [TestMethod]
        [Timeout(30000)]
        public void TheCheckCountsAsksAndFinishesWithItsFileClosed()
        {
            var root = Path.Combine(Path.GetTempPath(), "PreflightChecksTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
            File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), "rootPath: C:\\nowhere\nrigId: D1\nbehaviorBoard:\n  main:\n    portName: COM97\n");
            File.WriteAllText(Path.Combine(root, "task.yml"), "countSeconds: 1\nquestions:\n  behaviorBoard: Did its light flash?\n");
            var folder = Path.Combine(Path.GetDirectoryName(typeof(PreflightChecksTests).Assembly.Location), "Pieces");
            var xml = File.ReadAllText(Path.Combine(folder, "PreflightCheckGo.bonsai")).Replace("CONFIG_ROOT", SecurityElement.Escape(root));
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);

            Exception error = null;
            var subscription = builder.Workflow.BuildObservable().Subscribe(_ => { }, e => error = e);
            try
            {
                using (var socket = new RequestSocket(">tcp://127.0.0.1:55762"))
                {
                    var clock = Stopwatch.StartNew();
                    Func<Func<JObject, bool>, JObject> waitFor = until =>
                    {
                        while (true)
                        {
                            var status = Ask(socket, new { cmd = "status" });
                            if (until(status)) return status;
                            if (error != null || clock.Elapsed > TimeSpan.FromSeconds(20))
                                Assert.Fail("Waited " + clock.Elapsed.TotalSeconds.ToString("0") + " s; status " + status + (error == null ? "" : "; error " + error));
                            Thread.Sleep(100);
                        }
                    };
                    waitFor(s => (bool)s["ok"]);
                    ControlState.Current.SetPhase(ExperimentPhase.Ready);
                    Assert.IsTrue((bool)Ask(socket, new { cmd = "go" })["ok"], "Go refused");
                    var asked = waitFor(s => (string)s["metrics"]["prompt"] == "BehaviorBoard-Main: Did its light flash?");
                    Assert.AreEqual("Recording", (string)asked["phase"]);
                    Assert.IsTrue((bool)Ask(socket, new { cmd = "command", name = "Confirm", args = "BehaviorBoard-Main yes" })["ok"], "Confirm refused");
                    waitFor(s => (string)s["phase"] == "Finished");

                    // Rows reach the file only as the writer closes (CsvWriter never flushes), so a whole file at Finished is a closed one.
                    var lines = File.ReadAllLines(Path.Combine(root, "preflight-raw.csv"));
                    Assert.AreEqual("device,measure,value,detail", lines[0]);
                    CollectionAssert.AreEqual(new[] { "messages", "seconds", "person" }, lines.Skip(1).Select(l => l.Split(',')[1]).ToArray(), string.Join(" | ", lines));
                    Assert.IsTrue(long.Parse(lines[1].Split(',')[2]) >= 5, "The board stand-in sends 10 a second: " + lines[1]);
                    Assert.AreEqual("BehaviorBoard-Main,person,1,", lines[3]);
                    Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10), "Took " + clock.Elapsed);
                }
            }
            finally
            {
                subscription.Dispose();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            Assert.IsNull(error, error == null ? "" : error.ToString());
        }

        static JObject Ask(RequestSocket socket, object request)
        {
            socket.SendFrame(JObject.FromObject(request).ToString(Newtonsoft.Json.Formatting.None));
            string reply;
            if (!socket.TryReceiveFrameString(TimeSpan.FromSeconds(5), out reply)) return new JObject { ["ok"] = false, ["why"] = "no reply in 5 s" };
            return JObject.Parse(reply);
        }
    }
}
