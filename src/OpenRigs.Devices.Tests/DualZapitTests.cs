using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// Both Zapits in one task workflow, chosen per session by the task file (26 Nov 2026: decision-making's Zapit-Bonsai and
    /// Zapit-MATLAB boxes): Pieces\DualZapit.bonsai gives ConnectForTask, ZapitLogs and ConnectForTaskZapitDaq each its own
    /// switch on a task whose optogenetics block has no Enabled, ConnectForTask's subject another name and
    /// ConnectForTaskZapitDaq its ClosedBy; built, never run.
    /// </summary>
    [TestClass]
    public class DualZapitTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(DualZapitTests).Assembly.Location), "Pieces");

        const string BothBlocks = "zapit:\n  host: 127.0.0.1\n  port: 1488\n  device: Dev1\n  camera:\n    serialNumber: \"00000000\"\n";

        [TestMethod]
        public void EachPieceTakesItsOwnSwitchAndTheWorkflowBuilds()
        {
            var builder = Built(BothBlocks);
            var native = ZapitLiveViewTests.Included(builder.Workflow, "OpenRigs.Devices:ConnectForTaskZapitDaq.bonsai");
            var matlab = ZapitLiveViewTests.Included(builder.Workflow, "OpenRigs.Devices:ConnectForTask.bonsai");
            var logs = ZapitLiveViewTests.Included(builder.Workflow, "OpenRigs.Devices:ZapitLogs.bonsai");
            CollectionAssert.AreEquivalent(new[] { "TaskParameters.Optogenetics.ZapitBonsai", "TaskParameters.Optogenetics.ZapitBonsai" }, Selectors(native.Workflow, "Optogenetics.ZapitBonsai"));
            CollectionAssert.AreEquivalent(new[] { "TaskParameters.Optogenetics.ZapitMatlab", "TaskParameters.Optogenetics.ZapitMatlab" }, Selectors(matlab.Workflow, "Optogenetics.ZapitMatlab"));
            CollectionAssert.AreEquivalent(new[] { "TaskParameters.Optogenetics.ZapitMatlab" }, Selectors(logs.Workflow, "Optogenetics.ZapitMatlab"));
            // Each switch is published under its own name: ConnectForTaskZapitDaq's Optogenetics (ZapitCamera and ExpectedFiles read
            // it), ConnectForTask's OptogeneticsMatlab.
            Assert.AreEqual(1, Declared(builder.Workflow, "Optogenetics"));
            Assert.AreEqual(1, Declared(builder.Workflow, "OptogeneticsMatlab"));
            var closedBy = ZapitLiveViewTests.Nested(native.Workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<CombinatorBuilder>().Select(c => c.Combinator).OfType<TaskFileSwitch>().Single();
            Assert.AreEqual("optogenetics.zapitMatlab", closedBy.Path);
            var require = ZapitLiveViewTests.Nested(native.Workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<CombinatorBuilder>().Select(c => c.Combinator).OfType<OpenRigs.Core.RequireValue>().Single();
            StringAssert.StartsWith(require.Message, "Zapit-Bonsai is ticked");
        }

        // The defaults are unchanged for a workflow that includes one Zapit (coen-dome-navigation: ConnectForTask on optogenetics.enabled).
        [TestMethod]
        public void ThePiecesDefaultsAreTheEnabledSwitchAndNoClosedBy()
        {
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(Path.Combine(Folder, "ZapitLiveView.bonsai")))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            var native = ZapitLiveViewTests.Included(builder.Workflow, "OpenRigs.Devices:ConnectForTaskZapitDaq.bonsai");
            CollectionAssert.AreEquivalent(new[] { "TaskParameters.Optogenetics.Enabled", "TaskParameters.Optogenetics.Enabled" }, Selectors(native.Workflow, "Optogenetics.Enabled"));
            Assert.AreEqual("", ZapitLiveViewTests.Nested(native.Workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<CombinatorBuilder>().Select(c => c.Combinator).OfType<TaskFileSwitch>().Single().Path);
        }

        [TestMethod]
        public void TaskFileSwitchReadsTheBoolAtItsPath()
        {
            var file = Path.Combine(Path.GetTempPath(), "TaskFileSwitchTests-" + Guid.NewGuid().ToString("N") + ".yml");
            try
            {
                File.WriteAllText(file, "taskParameters:\n  optogenetics:\n    zapitBonsai: false\n    zapitMatlab: true\n    laserPowerMw: 2\n");
                Assert.IsTrue(TaskFileSwitch.Read(file, "optogenetics.zapitMatlab"));
                Assert.IsFalse(TaskFileSwitch.Read(file, "optogenetics.zapitBonsai"));
                Assert.IsFalse(TaskFileSwitch.Read(file, "optogenetics.missing"), "a bool the file leaves out is false");
                Assert.IsFalse(TaskFileSwitch.Read(file, "spouts.alwaysIn"), "a block the file leaves out is false");
                Assert.IsFalse(TaskFileSwitch.Read(Path.Combine(Path.GetTempPath(), "no-such-task-file.yml"), ""), "no path: the file is not read");
                StringAssert.Contains(Refusal(() => TaskFileSwitch.Read(file, "optogenetics.laserPowerMw")), "is 2, not true or false");
                StringAssert.Contains(Refusal(() => TaskFileSwitch.Read(file, "optogenetics")), "is not true or false");
                StringAssert.Contains(Refusal(() => TaskFileSwitch.Read(file + ".missing", "optogenetics.zapitMatlab")), "could not be read for optogenetics.zapitMatlab");
            }
            finally
            {
                File.Delete(file);
            }
        }

        static string Refusal(Action read)
        {
            try { read(); }
            catch (InvalidOperationException e) { return e.Message; }
            Assert.Fail("not refused");
            return null;
        }

        // Every MemberSelector's selector ending in the switch, however deep.
        static string[] Selectors(ExpressionBuilderGraph workflow, string containing)
        {
            return ZapitLiveViewTests.Nested(workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<MemberSelectorBuilder>()
                .Select(m => m.Selector).Where(s => s != null && s.EndsWith(containing)).ToArray();
        }

        // How many subjects of this name the built workflow declares, however deep.
        static int Declared(ExpressionBuilderGraph workflow, string name)
        {
            return ZapitLiveViewTests.Nested(workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<SubjectExpressionBuilder>().Count(s => s.Name == name);
        }

        // DualZapit.bonsai on a rig.yml whose zapit block is this one, built (the camera a stand-in).
        static WorkflowBuilder Built(string zapit)
        {
            var root = Path.Combine(Path.GetTempPath(), "DualZapitTests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
                var rig = File.ReadAllText(Path.Combine(Folder, "rig.yml")).Replace("\r\n", "\n");
                var at = rig.IndexOf("\nzapit:\n", StringComparison.Ordinal);
                var end = rig.IndexOf("\n\n", at + 1, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0 && end > at, "Pieces\\rig.yml has no zapit block");
                File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), rig.Substring(0, at + 1) + zapit + rig.Substring(end + 1));
                var xml = File.ReadAllText(Path.Combine(Folder, "DualZapit.bonsai")).Replace("CONFIG_ROOT", SecurityElement.Escape(root));
                WorkflowBuilder builder;
                using (var reader = XmlReader.Create(new StringReader(xml)))
                {
                    builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                }
                var camera = ZapitLiveViewTests.Included(ZapitLiveViewTests.Included(builder.Workflow, "OpenRigs.Devices:ConnectForTaskZapitDaq.bonsai").Workflow, "OpenRigs.Devices:ZapitCamera.bonsai").Workflow;
                var pylon = ZapitLiveViewTests.Nested(camera).Select(n => n.Value).OfType<CombinatorBuilder>().Single(c => c.Combinator.GetType().FullName == "OpenRigs.Zapit.ZapitPylonCapture");
                pylon.Combinator = new StandInPylonCapture();
                builder.Workflow.Build();
                return builder;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }

    /// <summary>A task file whose optogenetics block chooses between Zapits with two bools and has no Enabled (decision-making's).</summary>
    public class DualZapitTask
    {
        /// <summary>The task's parameters.</summary>
        public DualZapitTaskParameters TaskParameters { get; set; }
    }

    /// <summary>The task's parameters the Zapit pieces read.</summary>
    public class DualZapitTaskParameters
    {
        /// <summary>The task's optogenetics block.</summary>
        public DualZapitOptogenetics Optogenetics { get; set; }
    }

    /// <summary>An optogenetics block with a box for each Zapit.</summary>
    public class DualZapitOptogenetics
    {
        /// <summary>An optogenetics experiment on ZapitDaq.</summary>
        public bool ZapitBonsai { get; set; }

        /// <summary>An optogenetics experiment on MATLAB Zapit.</summary>
        public bool ZapitMatlab { get; set; }

        /// <summary>The Zapit conditions file.</summary>
        public string ZapitConditionsPath { get; set; }

        /// <summary>The laser power, in mW.</summary>
        public double LaserPowerMw { get; set; }
    }
}
