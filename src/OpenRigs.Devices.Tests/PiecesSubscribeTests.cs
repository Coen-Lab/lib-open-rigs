using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// Each piece and core include, built and subscribed in this process as a rig runs it, for a few seconds against
    /// Pieces\rig.yml (filled_rig's rig with every port COM97 and camera serial 1, tests\rigs\test_pieces_rig.py): it
    /// may stop only because a port or camera is missing, never on a setting left at its XML default (UNMAPPED, a
    /// stage with no name) or an exception while it is torn down.
    /// </summary>
    [TestClass]
    public class PiecesSubscribeTests
    {
        const string Missing = @"port '?COM97'? does not exist|Spinnaker camera with serial number 1 was not found";
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(PiecesSubscribeTests).Assembly.Location), "Pieces");

        static string Solution
        {
            get
            {
                var directory = new DirectoryInfo(Folder);
                while (!File.Exists(Path.Combine(directory.FullName, "OpenRigs.sln"))) directory = directory.Parent;
                return directory.FullName;
            }
        }

        [TestMethod]
        public void BehaviorBoardWithItsLines() { Check(Run("BehaviorBoard.bonsai")); }

        // Rewards and both RewardValve pieces with no board: each loads its line and calibration and runs; a reward
        // asked of a valve fires its calibrated pulse into its board's TriggerCalibratedPulse (2 ul is 38 ms on the
        // fixture's curve); one asked of a valve no piece fires, or a valve with no calibration, stops the workflow.
        static List<string> RunValves(string valve, Func<string, string> editRig = null)
        {
            return Run("RewardValves.bonsai", editRig, edit: xml => xml.Replace("VALVE", valve)
                .Replace("SEND_AFTER", valve == null ? "PT1H" : "PT0.5S"));
        }

        [TestMethod]
        public void RewardValvesLoadTheirLines() { Assert.AreEqual("", string.Join(" || ", RunValves(null))); }

        [TestMethod]
        public void ARewardFiresItsValvesCalibratedPulse()
        {
            StringAssert.StartsWith(string.Join(" || ", RunValves("left")), "InvalidOperationException: fired DO1 38");
            StringAssert.StartsWith(string.Join(" || ", RunValves("Right")), "InvalidOperationException: fired DO2 38");
        }

        [TestMethod]
        public void ARewardForAValveNoPieceFiresStops()
        {
            StringAssert.Contains(string.Join(" || ", RunValves("centre")), "A reward from the valve 'centre', which no RewardValve piece in this workflow fires; its valves are left, right.");
        }

        [TestMethod]
        public void ARewardValveWithoutItsCalibrationStops()
        {
            var errors = RunValves(null, rig => rig.Replace("calibration/valve-DO2.json", "calibration/missing.json"));
            StringAssert.Contains(string.Join(" || ", errors), "RewardValve-Right (DO2 on BehaviorBoard-Main) has no valve calibration");
        }

        // ClockFromBoard reading the board's time unset (the valve calibration): its SetClock reaches SetHarpClock, and
        // the piece builds and runs with Ready waiting for HarpClockRead.
        [TestMethod]
        public void ClockFromBoardWithSetClockFalse()
        {
            var path = "OpenRigs.Devices:ClockFromBoard.bonsai";
            Func<string, string> unset = xml => xml
                .Replace("Path=\"" + path + "\">", "Path=\"" + path + "\"><SetClock>false</SetClock>")
                .Replace("<PieceStages>HarpClockSet</PieceStages>", "<PieceStages>HarpClockRead</PieceStages>");
            var given = unset(File.ReadAllText(Path.Combine(Folder, "BehaviorBoard.bonsai")));
            StringAssert.Contains(given, "<SetClock>false</SetClock>");
            StringAssert.Contains(given, "<PieceStages>HarpClockRead</PieceStages>");
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(given)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            builder.Workflow.Build();
            var clock = Included(builder.Workflow, path).Workflow.Select(node => ExpressionBuilder.GetWorkflowElement(node.Value)).OfType<SetHarpClock>().Single();
            Assert.IsFalse(clock.SetClock);
            Check(Run("BehaviorBoard.bonsai", edit: unset));
        }

        [TestMethod]
        public void ClockFromTimestampGenerator() { Check(Run("ClockFromTimestampGenerator.bonsai")); }

        [TestMethod]
        public void LicketySplit() { Check(Run("LicketySplit.bonsai")); }

        [TestMethod]
        public void LickSpoutStage() { Check(Run("LickSpoutStage.bonsai")); }

        [TestMethod]
        public void SpinnakerCameraAndCameraTriggers()
        {
            RequireSpinnaker();
            Check(Run("SpinnakerCamera.bonsai"));
        }

        // Without projector, so ProjectorDisplay stops at start before it opens a full-screen window.
        [TestMethod]
        public void DomeHardware()
        {
            RequireSpinnaker();
            Check(Run("DomeHardware.bonsai", rig => Regex.Replace(rig, @"(?ms)^projector:.*?(?=^\S)", "")),
                  "projector");
        }

        // The pieces' rig's face camera as the box's overhead one, on the same trigger.
        [TestMethod]
        public void BBoxHardware()
        {
            RequireSpinnaker();
            Check(Run("BBoxHardware.bonsai", rig => rig.Replace("  face:", "  overhead:").Replace("Camera-Face", "Camera-Overhead")));
        }

        // A workflow on a rig list (a thin entry per rig kind) gives the rig list its own stages, which the rig list
        // passes to its ReadyGate's watchdog, so Ready waits for them too: built as a rig builds it. Without the
        // <WorkflowStages /> written on the rig list's ReadyGate node, Bonsai 2.9 dropped the value silently (16 Nov 2026).
        [DataTestMethod]
        [DataRow("DomeHardware")]
        [DataRow("BBoxHardware")]
        public void EachRigListPassesTheWorkflowStagesToItsReadyGate(string rigList)
        {
            RequireSpinnaker();
            var path = "OpenRigs.Devices:" + rigList + ".bonsai";
            var xml = File.ReadAllText(Path.Combine(Folder, rigList + ".bonsai"));
            var given = xml.Replace("Path=\"" + path + "\" />", "Path=\"" + path + "\"><WorkflowStages>CalibrationReady</WorkflowStages></Expression>");
            Assert.AreNotEqual(xml, given);
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(given)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            builder.Workflow.Build();
            var gate = Included(Included(builder.Workflow, path).Workflow, "OpenRigs.Devices:ReadyGate.bonsai");
            var watchdog = gate.Workflow.Select(node => ExpressionBuilder.GetWorkflowElement(node.Value)).OfType<OpenRigs.Core.StartupWatchdog>().Single();
            Assert.AreEqual("CalibrationReady", watchdog.WorkflowStages);
        }

        // The box's Binning reaches its camera, as the workflow stages reach ReadyGate.
        [TestMethod]
        public void TheBehaviourBoxRigListPassesBinningToItsCamera()
        {
            RequireSpinnaker();
            var path = "OpenRigs.Devices:BBoxHardware.bonsai";
            var xml = File.ReadAllText(Path.Combine(Folder, "BBoxHardware.bonsai"))
                .Replace("Path=\"" + path + "\" />", "Path=\"" + path + "\"><Binning>2</Binning></Expression>");
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            builder.Workflow.Build();
            var camera = Included(Included(builder.Workflow, path).Workflow, "OpenRigs.Devices:SpinnakerCamera.bonsai");
            Assert.AreEqual("2", camera.PropertiesXml.Single(x => x.Name.LocalName == "Binning").Value);
        }

        static IncludeWorkflowBuilder Included(ExpressionBuilderGraph workflow, string path)
        {
            return workflow.Select(node => node.Value).OfType<IncludeWorkflowBuilder>().Single(b => b.Path == path);
        }

        // From the SDK's install, as on a rig: Bonsai.Spinnaker's own copy is not the SDK's build and does not load.
        static readonly string SpinnakerNet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Teledyne\Spinnaker\bin64\vs2015\SpinnakerNET_v140.dll");

        static PiecesSubscribeTests()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                new System.Reflection.AssemblyName(args.Name).Name == "SpinnakerNET_v140" && File.Exists(SpinnakerNet)
                    ? System.Reflection.Assembly.LoadFrom(SpinnakerNet) : null;
        }

        static void RequireSpinnaker()
        {
            try { Type.GetType("SpinnakerNET.ManagedSystem, SpinnakerNET_v140", true); }
            catch (Exception e) { Assert.Inconclusive("The Spinnaker SDK (4.2.0.83) cannot load here, so no camera piece can run: " + e.Message); }
        }

        static void Check(List<string> errors, string alsoAllowed = null)
        {
            var allowed = alsoAllowed == null ? Missing : Missing + "|" + alsoAllowed;
            Assert.AreNotEqual(0, errors.Count, "The workflow ran with no error, but no port it names exists.");
            foreach (var error in errors)
            {
                Assert.IsTrue(!error.StartsWith("teardown") && Regex.IsMatch(error, allowed), "Stopped other than on a missing port or camera: " + string.Join(" || ", errors));
            }
        }

        /// <summary>Subscribes the workflow for up to <paramref name="seconds"/>, then tears it down; every error, in order.</summary>
        static List<string> Run(string file, Func<string, string> editRig = null, double seconds = 4, Func<string, string> edit = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "PiecesSubscribeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
            var rig = File.ReadAllText(Path.Combine(Folder, "rig.yml"));
            File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), editRig == null ? rig : editRig(rig));
            // Each valve's calibration, measured on this machine, so a RewardValve piece loads its line (LoadRewardLine).
            Directory.CreateDirectory(Path.Combine(root, Environment.MachineName, "calibration"));
            var curve = File.ReadAllText(Path.Combine(Solution, "fixtures", "calibration", "valve-DO1.json")).Replace("RIG-NAME", Environment.MachineName);
            foreach (var line in new[] { "DO1", "DO2" }) File.WriteAllText(Path.Combine(root, Environment.MachineName, "calibration", "valve-" + line + ".json"), curve);
            var xml = File.ReadAllText(Path.Combine(Folder, file)).Replace("CONFIG_ROOT", SecurityElement.Escape(root));
            if (edit != null) xml = edit(xml);
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }

            var errors = new List<string>();
            var stopped = new ManualResetEventSlim();
            IDisposable subscription = null;
            try
            {
                subscription = builder.Workflow.BuildObservable().Subscribe(
                    _ => { },
                    e => { lock (errors) errors.Add(Describe(e)); stopped.Set(); },
                    stopped.Set);
            }
            catch (Exception e) { lock (errors) errors.Add("subscribe " + Describe(e)); }
            stopped.Wait(TimeSpan.FromSeconds(seconds));
            try { if (subscription != null) subscription.Dispose(); }
            catch (Exception e) { lock (errors) errors.Add("teardown " + Describe(e)); }
            try { Directory.Delete(root, true); } catch (IOException) { }
            lock (errors) return errors.ToList();
        }

        static string Describe(Exception e)
        {
            var parts = new List<string>();
            for (var x = e; x != null; x = x.InnerException) parts.Add(x.GetType().Name + ": " + x.Message);
            return string.Join(" <- ", parts);
        }
    }
}
