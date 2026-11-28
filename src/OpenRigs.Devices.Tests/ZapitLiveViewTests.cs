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
    /// The Zapit camera in the live view (23 Nov 2026): ConnectForTaskZapitDaq and StreamDevices, built as
    /// a task workflow has them (Pieces\ZapitLiveView.bonsai), never run. Camera-Zapit.Data published inside the Zapit
    /// SelectMany, a subject scope of its own, was invisible to StreamDevices, which streamed nothing for it.
    /// </summary>
    [TestClass]
    public class ZapitLiveViewTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(ZapitLiveViewTests).Assembly.Location), "Pieces");

        const string ZapitDaq = "zapit:\n  host: 127.0.0.1\n  port: 1488\n  device: Dev1\n  camera:\n    serialNumber: \"00000000\"\n";

        [TestMethod]
        public void TheZapitCameraIsStreamedOnARigWithZapitDaq()
        {
            CollectionAssert.Contains(Streams(ZapitDaq), "Camera-Zapit.video");
        }

        // The window gets the viewed frame, cropped and stretched (View); the mp4 records it whole (Image), never the frame
        // the setup saves (Still): the live view takes a member named View before any other image.
        [TestMethod]
        public void TheZapitStreamIsTheShownImageNotTheStill()
        {
            Assert.AreEqual("Value.View", StreamImages(ZapitDaq)["Camera-Zapit.video"]);
        }

        // MATLAB Zapit's block alone opens no camera, so the live view shows none.
        [TestMethod]
        public void NoZapitCameraIsStreamedWithoutZapitDaq()
        {
            CollectionAssert.DoesNotContain(Streams("zapit:\n  host: 127.0.0.1\n  port: 1488\n"), "Camera-Zapit.video");
        }

        // Audit 24 Nov 2026 L1 and C6: the include's Simulated (false by default) reaches ZapitDaq, so the wiring can run the
        // twin, and its LaserCeilingVolts (0 by default, refusing any laser) reaches ZapitDaq and ZapitSetupCapture.
        [TestMethod]
        public void SimulatedAndTheCeilingsVoltsReachZapitDaqAndTheSetup()
        {
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(Path.Combine(Folder, "ZapitLiveView.bonsai")))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            var include = Included(builder.Workflow, "OpenRigs.Opto.ZapitBonsai:ConnectForTaskZapitDaq.bonsai");
            var properties = System.ComponentModel.TypeDescriptor.GetProperties(include);
            var daq = Nested(include.Workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<CombinatorBuilder>().Select(c => c.Combinator).OfType<OpenRigs.Zapit.ZapitDaq>().Single();
            var ceiling = Nested(include.Workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<CombinatorBuilder>().Select(c => c.Combinator).OfType<DoubleProperty>().Single();
            Assert.IsFalse(daq.Simulated);
            Assert.AreEqual(0.0, ceiling.Value);
            properties["Simulated"].SetValue(include, true);
            properties["LaserCeilingVolts"].SetValue(include, 1.25);
            Assert.IsTrue(daq.Simulated);
            Assert.AreEqual(1.25, ceiling.Value);
            Included(include.Workflow, "OpenRigs.Opto.ZapitBonsai:ZapitSetup.bonsai");
            Assert.IsTrue(MappedFrom(include.Workflow, typeof(OpenRigs.Zapit.ZapitDaq), "LaserCeilingVolts", "Zapit.LaserCeilingVolts"));
            Assert.IsTrue(MappedFrom(include.Workflow, typeof(OpenRigs.Zapit.ZapitSetupCapture), "LaserCeilingVolts", "Zapit.LaserCeilingVolts"));
        }

        // Play Condition: ZapitPlay takes ZapitDaq's own limits from ZapitRig (so it refuses what ZapitDaq's Arm would stop the
        // workflow over), and the setup declares Zapit-Main.Play and Zapit-Main.Trigger for the agent. The workflow builds
        // with them (TheZapitCameraIsStreamedOnARigWithZapitDaq builds it whole).
        [TestMethod]
        public void PlayConditionIsWiredWithZapitDaqsLimitsAndItsCommandsDeclared()
        {
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(Path.Combine(Folder, "ZapitLiveView.bonsai")))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }
            var include = Included(builder.Workflow, "OpenRigs.Opto.ZapitBonsai:ConnectForTaskZapitDaq.bonsai");
            Included(include.Workflow, "OpenRigs.Opto.ZapitBonsai:ZapitSetup.bonsai");
            Assert.IsTrue(MappedFrom(include.Workflow, typeof(OpenRigs.Zapit.ZapitPlay), "MaxTrialSeconds", "ZapitRig"));
            Assert.IsTrue(MappedFrom(include.Workflow, typeof(OpenRigs.Zapit.ZapitDaq), "MaxTrialSeconds", "ZapitRig"));
            Assert.IsTrue(MappedFrom(include.Workflow, typeof(OpenRigs.Zapit.ZapitPlay), "CeilingMw", "ZapitRig"));
            var declared = Nested(include.Workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<CombinatorBuilder>().Select(c => c.Combinator)
                .OfType<OpenRigs.Control.DeclareDeviceControls>().Single(d => d.Name == "Zapit-Main");
            var names = declared.Commands.Select(c => c.Name).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "Play", "Trigger", "LaserOff" }, names);
        }

        // Whether the combinator's property is mapped from the subject (SubscribeSubject, PropertyMapping, the combinator), at any depth.
        static bool MappedFrom(ExpressionBuilderGraph workflow, Type combinator, string property, string subject)
        {
            var graphs = new[] { workflow }.Concat(Nested(workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<IWorkflowExpressionBuilder>().Where(b => b.Workflow != null).Select(b => b.Workflow));
            return graphs.Any(graph => graph.Any(target => ExpressionBuilder.Unwrap(target.Value) is CombinatorBuilder c && c.Combinator.GetType() == combinator
                && graph.Any(mapping => mapping.Successors.Any(s => s.Target == target) && ExpressionBuilder.Unwrap(mapping.Value) is PropertyMappingBuilder m && m.PropertyMappings.Any(p => p.Name == property)
                    && graph.Any(source => source.Successors.Any(s => s.Target == mapping) && ExpressionBuilder.Unwrap(source.Value) is SubscribeSubject named && named.Name == subject))));
        }

        // The streams StreamDevices plans from a rig.yml whose zapit block is this one.
        static string[] Streams(string zapit) => StreamImages(zapit).Keys.ToArray();

        // Each stream StreamDevices plans from a rig.yml whose zapit block is this one, with the member its image is read from.
        static System.Collections.Generic.Dictionary<string, string> StreamImages(string zapit)
        {
            var root = Path.Combine(Path.GetTempPath(), "ZapitLiveViewTests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
                var rig = File.ReadAllText(Path.Combine(Folder, "rig.yml")).Replace("\r\n", "\n");
                var at = rig.IndexOf("\nzapit:\n", StringComparison.Ordinal);
                var end = rig.IndexOf("\n\n", at + 1, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0 && end > at, "Pieces\\rig.yml has no zapit block");
                File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), rig.Substring(0, at + 1) + zapit + rig.Substring(end + 1));
                var xml = File.ReadAllText(Path.Combine(Folder, "ZapitLiveView.bonsai")).Replace("CONFIG_ROOT", SecurityElement.Escape(root));
                WorkflowBuilder builder;
                using (var reader = XmlReader.Create(new StringReader(xml)))
                {
                    builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                }
                var camera = Included(Included(builder.Workflow, "OpenRigs.Opto.ZapitBonsai:ConnectForTaskZapitDaq.bonsai").Workflow, "OpenRigs.Opto.ZapitBonsai:ZapitCamera.bonsai").Workflow;
                var pylon = Nested(camera).Select(n => n.Value).OfType<CombinatorBuilder>().Single(c => c.Combinator.GetType().FullName == "OpenRigs.Zapit.ZapitPylonCapture");
                pylon.Combinator = new StandInPylonCapture();
                builder.Workflow.Build();
                var liveView = builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<OpenRigs.LiveView.StreamDevices>().Single();
                var generated = liveView.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<GroupWorkflowBuilder>().Single();
                return generated.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<GroupWorkflowBuilder>().ToDictionary(g => g.Name,
                    g => g.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<MemberSelectorBuilder>().Select(m => m.Selector).FirstOrDefault());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        // An include anywhere in the workflow, loaded (its public ArgumentRange loads it), so its nodes can be changed before the build.
        internal static IncludeWorkflowBuilder Included(ExpressionBuilderGraph workflow, string path)
        {
            var include = Nested(workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<IncludeWorkflowBuilder>().Single(b => b.Path == path);
            var range = include.ArgumentRange;
            Assert.IsNotNull(include.Workflow, path + " did not load");
            return include;
        }

        // Every node, however deep in groups and loaded includes.
        internal static System.Collections.Generic.IEnumerable<Bonsai.Dag.Node<ExpressionBuilder, ExpressionBuilderArgument>> Nested(ExpressionBuilderGraph workflow)
        {
            return workflow.SelectMany(n =>
            {
                var nested = ExpressionBuilder.Unwrap(n.Value) as IWorkflowExpressionBuilder;
                return new[] { n }.Concat(nested != null && nested.Workflow != null ? Nested(nested.Workflow) : Enumerable.Empty<Bonsai.Dag.Node<ExpressionBuilder, ExpressionBuilderArgument>>());
            });
        }
    }

    /// <summary>
    /// PylonCapture's place in ZapitCamera with the members ZapitCamera reads: Bonsai.Pylon's types need Basler's pylon
    /// runtime, which a desk does not have, and only the frame's members matter to the build.
    /// </summary>
    public class StandInPylonCapture : Bonsai.Source<StandInZapitFrame>
    {
        /// <summary>Mapped from rig.yml's zapit.camera.serialNumber.</summary>
        public string SerialNumber { get; set; }

        /// <summary>Mapped from rig.yml's zapit.camera.sessionPfsPath.</summary>
        public string ParameterFile { get; set; }

        /// <summary>No frames: the workflow is only built, with the grid's camera settings as its input as ZapitPylonCapture takes them.</summary>
        /// <param name="settings">Camera-Zapit.Settings.</param>
        /// <returns>A sequence that never produces a frame.</returns>
        public IObservable<StandInZapitFrame> Generate(IObservable<OpenRigs.Zapit.ZapitCameraSettings> settings)
        {
            return System.Reactive.Linq.Observable.Never<StandInZapitFrame>();
        }

        /// <summary>No frames: the workflow is only built.</summary>
        /// <returns>A sequence that never produces a frame.</returns>
        public override IObservable<StandInZapitFrame> Generate()
        {
            return System.Reactive.Linq.Observable.Never<StandInZapitFrame>();
        }
    }

    /// <summary>A frame's members ZapitCamera reads, as OpenRigs.Zapit's ZapitFrame has them.</summary>
    public class StandInZapitFrame
    {
        /// <summary>The viewed image, streamed (ZapitFrame.View).</summary>
        public OpenCV.Net.IplImage View { get; set; }

        /// <summary>The recorded image, whole (ZapitFrame.Image).</summary>
        public OpenCV.Net.IplImage Image { get; set; }

        /// <summary>The whole frame Zapit's setup saves (ZapitFrame.Still).</summary>
        public OpenCV.Net.IplImage Still { get; set; }

        /// <summary>The camera's answer to a crop (ZapitFrame.CropReports).</summary>
        public OpenRigs.Zapit.ZapitSetupStep[] CropReports { get; set; }

        /// <summary>The grab's ID.</summary>
        public long FrameID { get; set; }

        /// <summary>The camera's timestamp of the grab.</summary>
        public long Timestamp { get; set; }
    }

    /// <summary>The task file's shape ConnectForTaskZapitDaq reads from TaskConfig.</summary>
    public class ZapitTask
    {
        /// <summary>The task's parameters.</summary>
        public ZapitTaskParameters TaskParameters { get; set; }
    }

    /// <summary>The task's parameters ConnectForTaskZapitDaq reads.</summary>
    public class ZapitTaskParameters
    {
        /// <summary>The task's optogenetics block.</summary>
        public ZapitOptogenetics Optogenetics { get; set; }
    }

    /// <summary>The task's optogenetics block.</summary>
    public class ZapitOptogenetics
    {
        /// <summary>Whether the experiment is an optogenetics one.</summary>
        public bool Enabled { get; set; }

        /// <summary>The Zapit conditions file.</summary>
        public string ZapitConditionsPath { get; set; }

        /// <summary>The laser power, in mW.</summary>
        public double LaserPowerMw { get; set; }
    }
}
