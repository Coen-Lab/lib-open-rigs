using Bonsai;
using Bonsai.Expressions;
using Bonsai.Harp;
using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using OpenRigs.Control;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive.Linq;
using System.Security;
using System.Threading;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// ZapitSetup's commands through the controller, as the launching app drives them (Pieces\ZapitSetupBank.bonsai, a
    /// simulated ZapitDaq). The simulated load answers BankLoaded while Loading is still being passed on, so with the
    /// metric reported after the command, Loading overwrote BankLoaded and the agent waited out its 60 s (on a rig, 24 Nov 2026).
    /// </summary>
    [TestClass]
    public class ZapitSetupTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(ZapitSetupTests).Assembly.Location), "Pieces");

        [TestMethod]
        [Timeout(30000)]
        public void LoadBankEndsWithBankLoadedAsTheMetric()
        {
            RunPiece((socket, bank) =>
            {
                // Hand aiming reports on its own metric, so the Setup metric keeps BankLoaded; rig.yml's laser maximum refuses 6 V.
                Assert.AreEqual("Aiming 1,-2,0.5", Command(socket, "Zapit-Main.Aim", "1,-2,0.5", "Zapit-Main.Aim", "Aiming 1,-2"));
                StringAssert.StartsWith(Command(socket, "Zapit-Main.Aim", "1,-2,6", "Zapit-Main.Aim", "Refused "), "Refused the laser volts must be 0 to 5 V");
                Assert.AreEqual("BankLoaded " + bank, (string)Ask(socket, new { cmd = "status" })["metrics"]["Zapit-Main.Setup"]);

                // The shown frame's crop on its own metric too, each request numbered and the number echoed: Cropping #n when
                // taken, then the camera's answer from its next frame (a 64 x 48 simulated frame): Cropped, Full Frame, or
                // Refused for one outside the frame or no crop, saying which crop is now in force (the workflow runs on).
                // The controller answers each request on one thread after the command before it has run through the workflow,
                // so this status read sees anything the command itself answered; only a frame may answer it.
                Assert.AreEqual("Cropping #1 16,16,32,16", Command(socket, "Zapit-Main.Crop", "#1 16,16,32,16", "Zapit-Main.Crop", "Cropping"));
                Assert.AreEqual("Cropping #1 16,16,32,16", (string)Ask(socket, new { cmd = "status" })["metrics"]["Zapit-Main.Crop"], "Cropped before any frame");
                SimulatedZapitCamera.SendFrame();
                Assert.AreEqual("Cropped #1 16,16,32,16", Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Cropped"));
                Assert.AreEqual("Cropping #2 3000,2000,800,600", Command(socket, "Zapit-Main.Crop", "#2 3000,2000,800,600", "Zapit-Main.Crop", "Cropping"));
                SimulatedZapitCamera.SendFrame();
                Assert.AreEqual("Refused #2 the crop 3000,2000,800,600 does not fit the camera's 64 x 48 frame; Now Full Frame", Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Refused "));
                Assert.AreEqual("Cropping #3 Full Frame", Command(socket, "Zapit-Main.Crop", "#3 full", "Zapit-Main.Crop", "Cropping"));
                SimulatedZapitCamera.SendFrame();
                Assert.AreEqual("Full Frame #3", Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Full"));
                // A request that is no crop is answered by the camera too, under its number, keeping the crop in force.
                Assert.AreEqual("Cropping #4 1,2", Command(socket, "Zapit-Main.Crop", "#4 1,2", "Zapit-Main.Crop", "Cropping"));
                SimulatedZapitCamera.SendFrame();
                var whole = Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Refused ");
                StringAssert.StartsWith(whole, "Refused #4 a crop is x,y,w,h");
                StringAssert.EndsWith(whole, "; Now Full Frame");
                Command(socket, "Zapit-Main.Crop", "#5 16,16,32,16", "Zapit-Main.Crop", "Cropping");
                SimulatedZapitCamera.SendFrame();
                Assert.AreEqual("Cropped #5 16,16,32,16", Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Cropped"));
                Command(socket, "Zapit-Main.Crop", "#6 0,0,63,48", "Zapit-Main.Crop", "Cropping");
                SimulatedZapitCamera.SendFrame();
                var kept = Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Refused ");
                StringAssert.StartsWith(kept, "Refused #6 a crop's x and y are from 0");
                StringAssert.EndsWith(kept, "; Now Cropped 16,16,32,16");
                // No valid number: #0, the crop in force kept.
                Assert.AreEqual("Cropping #0 16,16,32,16", Command(socket, "Zapit-Main.Crop", "16,16,32,16", "Zapit-Main.Crop", "Cropping"));
                SimulatedZapitCamera.SendFrame();
                var unnumbered = Command(socket, "Zapit-Main.Crop", null, "Zapit-Main.Crop", "Refused ");
                StringAssert.StartsWith(unnumbered, "Refused #0 a crop request is #<n> x,y,w,h");
                StringAssert.EndsWith(unnumbered, "; Now Cropped 16,16,32,16");
            });
        }

        // Play Condition's lines as ZapitSetup routes them (open_rigs.zapit.play relies on it): Trigger on raises the
        // ZapitTrigger line (DO3) and sends the simulated twin its trigger, which starts the armed play; the play's end
        // clears the line; Trigger off clears it without a twin trigger; LaserOff clears it with nothing armed; Go clears
        // it, raised with nothing armed.
        [TestMethod]
        [Timeout(30000)]
        public void PlayTriggerAndLaserOffRaiseAndClearTheTriggerLine()
        {
            RunPiece((socket, bank) =>
            {
                Assert.AreEqual("Armed 2", Command(socket, "Zapit-Main.Play", "2,2,0.3", "Zapit-Main.Play", "Armed"));
                Assert.AreEqual("Refused ZapitDaq cannot Point while Play Condition is armed", Command(socket, "Zapit-Main.Aim", "1,1,0.3", "Zapit-Main.Aim", "Refused "), "an aim with a play armed points no beam");
                Assert.AreEqual("", Lines(), "no line written before the trigger");
                Assert.AreEqual(0, TwinTriggers(), "no twin trigger before Trigger on");

                Assert.AreEqual("set DO3", WaitForLines(socket, "Zapit-Main.Trigger", "on", "set DO3"), "Trigger on raises the line");
                Assert.AreEqual("Ended 2 Ended", Command(socket, "Zapit-Main.Play", null, "Zapit-Main.Play", "Ended"), "the twin's trigger starts the play, which ends on its own");
                Assert.AreEqual(1, TwinTriggers(), "one twin trigger for Trigger on");
                Assert.AreEqual("set DO3, clear DO3", WaitForLines(socket, null, null, "set DO3, clear DO3"), "the play's end clears the line");

                Assert.AreEqual("set DO3, clear DO3, clear DO3", WaitForLines(socket, "Zapit-Main.Trigger", "off", "set DO3, clear DO3, clear DO3"), "Trigger off clears the line");
                Assert.AreEqual(1, TwinTriggers(), "no twin trigger for Trigger off");

                Assert.AreEqual("set DO3, clear DO3, clear DO3, clear DO3", WaitForLines(socket, "Zapit-Main.LaserOff", "", "set DO3, clear DO3, clear DO3, clear DO3"), "LaserOff clears the line");
                Assert.AreEqual(1, TwinTriggers());

                Assert.AreEqual("set DO3, clear DO3, clear DO3, clear DO3, set DO3", WaitForLines(socket, "Zapit-Main.Trigger", "on", "set DO3, clear DO3, clear DO3, clear DO3, set DO3"), "Trigger on with nothing armed raises the line");
                Assert.IsTrue((bool)Ask(socket, new { cmd = "go" })["ok"], "Go refused");
                Assert.AreEqual("set DO3, clear DO3, clear DO3, clear DO3, set DO3, clear DO3", WaitForLines(socket, null, null, "set DO3, clear DO3, clear DO3, clear DO3, set DO3, clear DO3"), "Go clears the line, no play armed");
            });
        }

        // Laser Off during a grid run ends it (Refused on the Setup metric, the lines zeroed): the next camera frame saves
        // nothing and points no laser-on Point.
        [TestMethod]
        [Timeout(30000)]
        public void LaserOffEndsAGridRun()
        {
            RunPiece((socket, bank) =>
            {
                var folder = Path.Combine(Path.GetDirectoryName(bank), "ZapitScanner", "Frames_T");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "grid.json"), "{\"volts\": [[1, 2], [-1, 0.5], [2, 2]], \"laserVolts\": 0.3, \"skipFrames\": 0, \"backgrounds\": 1}");
                Assert.AreEqual("Pointing " + folder, Command(socket, "Zapit-Main.Setup", folder, "Zapit-Main.Setup", "Pointing"));
                Thread.Sleep(200);   // Pointed: the run takes the next frame
                SimulatedZapitCamera.SendFrame();
                for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(5) && Points() < 2; Thread.Sleep(50)) { }
                Assert.AreEqual(2, Points(), "the first point saved, the second pointed");
                Assert.IsTrue(File.Exists(Path.Combine(folder, "point_0000.png")));

                StringAssert.StartsWith(Command(socket, "Zapit-Main.LaserOff", "", "Zapit-Main.Setup", "Refused "), "Refused Laser Off came during a setup step: the lines are zeroed and nothing more is saved for " + folder);
                SimulatedZapitCamera.SendFrame();   // runs the frame through the setup on this thread, so any Point is recorded on return
                Assert.AreEqual(2, Points(), "no Point after Laser Off");
                Assert.IsFalse(File.Exists(Path.Combine(folder, "point_0001.png")));
                Assert.AreEqual("zapit Zero", RecordedCommands.Seen.Last(s => s.StartsWith("zapit ", StringComparison.Ordinal)));

                // With no step running, Laser Off still sends the setup's own Zero after whatever Point it was sending.
                Assert.AreEqual("Aiming 1,1,0.3", Command(socket, "Zapit-Main.Aim", "1,1,0.3", "Zapit-Main.Aim", "Aiming"));
                Assert.AreEqual("Off Laser Off", Command(socket, "Zapit-Main.LaserOff", "", "Zapit-Main.Aim", "Off"));
                Assert.AreEqual("zapit Zero", RecordedCommands.Seen.Last(s => s.StartsWith("zapit ", StringComparison.Ordinal)));
            });
        }

        static int Points() => RecordedCommands.Seen.Count(s => s == "zapit Point");

        // The piece with CONFIG_ROOT and its controller on a free port, the bank loaded (BankLoaded the Setup metric), then
        // drive. A port found free can be taken before the controller binds it: then a new one, 3 tries.
        static void RunPiece(Action<RequestSocket, string> drive)
        {
            for (var attempt = 1; ; attempt++)
            {
                var error = RunPiece(TestPorts.Free(), drive);
                if (attempt < 3 && TestPorts.Taken(error)) continue;
                Assert.IsNull(error, error == null ? "" : error.ToString());
                return;
            }
        }

        // The workflow's error, or null.
        static Exception RunPiece(int port, Action<RequestSocket, string> drive)
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapitSetupTests", Guid.NewGuid().ToString("N"));
            var pending = Path.Combine(root, Environment.MachineName, "pending");
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), "rootPath: C:\\nowhere\nrigId: D1\nbehaviorBoard:\n  main:\n    portName: COM97\n" +
                "zapit:\n  device: Dev1\n  ceilingMw: 30\n  maxTrialSeconds: 6\n  laserMinMaxControlVolts: [0.0, 5.0]\n");
            var bank = Path.Combine(pending, "Settings_ZapitBank_T.json");
            File.Copy(Path.Combine(Folder, "Settings_ZapitBank_T.json"), bank);
            using (var input = new GZipStream(File.OpenRead(Path.Combine(Folder, "Settings_ZapitBank_T.bin.gz")), CompressionMode.Decompress))
            using (var output = File.Create(Path.Combine(pending, "Settings_ZapitBank_T.bin")))
            {
                input.CopyTo(output);
            }
            var xml = File.ReadAllText(Path.Combine(Folder, "ZapitSetupBank.bonsai")).Replace("CONFIG_ROOT", SecurityElement.Escape(root)).Replace("CONTROL_PORT", port.ToString());
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);

            Exception error = null;
            while (RecordedCommands.Seen.TryDequeue(out _)) { }
            var subscription = builder.Workflow.BuildObservable().Subscribe(_ => { }, e => error = e);
            try
            {
                using (var socket = new RequestSocket(">tcp://127.0.0.1:" + port))
                {
                    var clock = Stopwatch.StartNew();
                    JObject status;
                    while (!(bool)(status = Ask(socket, new { cmd = "status" }))["ok"])
                    {
                        if (TestPorts.Taken(error)) return error;
                        if (error != null || clock.Elapsed > TimeSpan.FromSeconds(15)) Assert.Fail("No controller: " + status + "; " + error);
                        Thread.Sleep(100);
                    }
                    ControlState.Current.SetPhase(ExperimentPhase.Ready);
                    var sent = Ask(socket, new { cmd = "command", name = "Zapit-Main.LoadBank", args = bank });
                    Assert.IsTrue((bool)sent["ok"], "LoadBank refused: " + sent);
                    var metric = "";
                    for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(5); Thread.Sleep(100))
                    {
                        metric = (string)Ask(socket, new { cmd = "status" })["metrics"]?["Zapit-Main.Setup"];
                        if (metric != null && !metric.StartsWith("Loading ", StringComparison.Ordinal)) break;
                    }
                    Assert.AreEqual("BankLoaded " + bank, metric);
                    drive(socket, bank);
                }
            }
            finally
            {
                subscription.Dispose();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            return error;
        }

        // The ZapitTrigger line's writes so far, in order ("set DO3, clear DO3").
        static string Lines() => string.Join(", ", RecordedCommands.Seen.Where(s => !s.StartsWith("zapit ", StringComparison.Ordinal)));

        static int TwinTriggers() => RecordedCommands.Seen.Count(s => s == "zapit Trigger");

        // Sends a command (none for a null name), then waits for the line writes to read as expected (5 s at most).
        static string WaitForLines(RequestSocket socket, string name, string args, string expected)
        {
            if (name != null)
            {
                var sent = Ask(socket, new { cmd = "command", name, args });
                Assert.IsTrue((bool)sent["ok"], name + " refused: " + sent);
            }
            for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(5) && Lines() != expected; Thread.Sleep(50)) { }
            // And nothing more from the command: the controller answers this status only after the command has run through
            // the workflow. A write a later ZapitDaq event makes has no such barrier; the next call's expected lines, which
            // hold every write so far, catch it.
            Ask(socket, new { cmd = "status" });
            return Lines();
        }

        // Sends a command, then reads the metric until it starts as expected (5 s at most).
        static string Command(RequestSocket socket, string name, string args, string metricName, string expected)
        {
            if (args != null)
            {
                var sent = Ask(socket, new { cmd = "command", name, args });
                Assert.IsTrue((bool)sent["ok"], name + " refused: " + sent);
            }
            string metric = null;
            for (var waited = Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(5); Thread.Sleep(100))
            {
                metric = (string)Ask(socket, new { cmd = "status" })["metrics"]?[metricName];
                if (metric != null && metric.StartsWith(expected, StringComparison.Ordinal)) break;
            }
            return metric;
        }

        static JObject Ask(RequestSocket socket, object request)
        {
            socket.SendFrame(JObject.FromObject(request).ToString(Newtonsoft.Json.Formatting.None));
            string reply;
            if (!socket.TryReceiveFrameString(TimeSpan.FromSeconds(5), out reply)) return new JObject { ["ok"] = false, ["why"] = "no reply in 5 s" };
            return JObject.Parse(reply);
        }
    }

    /// <summary>
    /// The Zapit camera on 64 x 48 frames sent by the test (SendFrame), each made by ZapitPylonCapture's own per-frame code
    /// (ZapitFrameDepth.Frame): each Camera-Zapit.Settings crop is kept (ZapitCropState), and a frame is cut to it,
    /// answering from that frame (ZapitFrame.CropReports).
    /// </summary>
    public class SimulatedZapitCamera : Combinator<OpenRigs.Zapit.ZapitCameraSettings, ZapitCameraFrame>
    {
        static readonly System.Reactive.Subjects.Subject<int> Frames = new System.Reactive.Subjects.Subject<int>();

        /// <summary>One frame from the camera, cut to the crop it holds.</summary>
        public static void SendFrame() => Frames.OnNext(0);

        /// <summary>The camera's frames, each carrying the crop's answer.</summary>
        public override IObservable<ZapitCameraFrame> Process(IObservable<OpenRigs.Zapit.ZapitCameraSettings> source)
        {
            return Observable.Create<ZapitCameraFrame>(observer =>
            {
                var state = new OpenRigs.Zapit.ZapitCropState();
                var settings = source.Subscribe(s => state.Apply(s));
                var frames = Frames.Subscribe(_ =>
                {
                    var whole = new OpenCV.Net.IplImage(new OpenCV.Net.Size(64, 48), OpenCV.Net.IplDepth.U8, 1);
                    observer.OnNext(new ZapitCameraFrame { Value = OpenRigs.Zapit.ZapitFrameDepth.Frame(whole, false, state, 0, 0) });
                });
                return new System.Reactive.Disposables.CompositeDisposable(settings, frames);
            });
        }
    }

    /// <summary>A Camera-Zapit.Data element as ZapitSetup reads it: Value.Still for the stills, Value.CropReports for the crop's answer.</summary>
    public class ZapitCameraFrame
    {
        /// <summary>The camera's frame.</summary>
        public OpenRigs.Zapit.ZapitFrame Value { get; set; }
    }

    /// <summary>The board's ZapitTrigger line, DO3.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class StandInTriggerLine
    {
        /// <summary>DO3, once.</summary>
        public IObservable<DigitalOutputs> Process() => Observable.Return(DigitalOutputs.DO3);
    }

    /// <summary>Keeps what reaches the board's commands ("set DO3", "clear DO3") and ZapitDaq's ("zapit Trigger"), in order.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class RecordedCommands
    {
        /// <summary>Everything kept since the test cleared it.</summary>
        public static readonly ConcurrentQueue<string> Seen = new ConcurrentQueue<string>();

        /// <summary>Each line write, as set or clear and its lines.</summary>
        public IObservable<HarpMessage> Process(IObservable<HarpMessage> source) => source.Do(m => Seen.Enqueue(
            m.Address == OutputSet.Address ? "set " + OutputSet.GetPayload(m) : m.Address == OutputClear.Address ? "clear " + OutputClear.GetPayload(m) : "board " + m.Address));

        /// <summary>Each command to ZapitDaq, by kind.</summary>
        public IObservable<OpenRigs.Zapit.ZapitDaqCommand> Process(IObservable<OpenRigs.Zapit.ZapitDaqCommand> source) => source.Do(c => Seen.Enqueue("zapit " + c.Kind));
    }
}
