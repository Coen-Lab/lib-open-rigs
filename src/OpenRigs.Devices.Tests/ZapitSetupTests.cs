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
using System.IO.Compression;
using System.Reactive.Linq;
using System.Security;
using System.Threading;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// ZapitSetup's bank load through the controller, as the launching app drives it (Pieces\ZapitSetupBank.bonsai, a
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
            var xml = File.ReadAllText(Path.Combine(Folder, "ZapitSetupBank.bonsai")).Replace("CONFIG_ROOT", SecurityElement.Escape(root));
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);

            Exception error = null;
            var subscription = builder.Workflow.BuildObservable().Subscribe(_ => { }, e => error = e);
            try
            {
                using (var socket = new RequestSocket(">tcp://127.0.0.1:55763"))
                {
                    var clock = Stopwatch.StartNew();
                    JObject status;
                    while (!(bool)(status = Ask(socket, new { cmd = "status" }))["ok"])
                    {
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

                    // Hand aiming reports on its own metric, so the Setup metric keeps BankLoaded; rig.yml's laser maximum refuses 6 V.
                    Assert.AreEqual("Aiming 1,-2,0.5", Command(socket, "Zapit-Main.Aim", "1,-2,0.5", "Zapit-Main.Aim", "Aiming 1,-2"));
                    StringAssert.StartsWith(Command(socket, "Zapit-Main.Aim", "1,-2,6", "Zapit-Main.Aim", "Refused "), "Refused the laser volts must be 0 to 5 V");
                    Assert.AreEqual("BankLoaded " + bank, (string)Ask(socket, new { cmd = "status" })["metrics"]["Zapit-Main.Setup"]);

                    // The shown frame's crop on its own metric too: pixels, the whole frame, or refused (the workflow runs on).
                    Assert.AreEqual("Cropped 392,232,1152,864", Command(socket, "Zapit-Main.Crop", "392,232,1152,864", "Zapit-Main.Crop", "Cropped"));
                    Assert.AreEqual("Full Frame", Command(socket, "Zapit-Main.Crop", "full", "Zapit-Main.Crop", "Full"));
                    StringAssert.StartsWith(Command(socket, "Zapit-Main.Crop", "1,2", "Zapit-Main.Crop", "Refused "), "Refused a crop is x,y,w,h");
                }
            }
            finally
            {
                subscription.Dispose();
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            Assert.IsNull(error, error == null ? "" : error.ToString());
        }

        // Sends a command, then reads the metric until it starts as expected (5 s at most).
        static string Command(RequestSocket socket, string name, string args, string metricName, string expected)
        {
            var sent = Ask(socket, new { cmd = "command", name, args });
            Assert.IsTrue((bool)sent["ok"], name + " refused: " + sent);
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

    /// <summary>A Camera-Zapit.Data element as ZapitSetup reads it (Value.Image); no frame is sent.</summary>
    public class ZapitCameraFrame
    {
        /// <summary>The grab.</summary>
        public StandInGrab Value { get; set; }
    }
}
