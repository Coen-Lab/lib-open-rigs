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

        // MATLAB Zapit's block alone opens no camera, so the live view shows none.
        [TestMethod]
        public void NoZapitCameraIsStreamedWithoutZapitDaq()
        {
            CollectionAssert.DoesNotContain(Streams("zapit:\n  host: 127.0.0.1\n  port: 1488\n"), "Camera-Zapit.video");
        }

        // The streams StreamDevices plans from a rig.yml whose zapit block is this one.
        static string[] Streams(string zapit)
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
                var camera = Included(Included(builder.Workflow, "OpenRigs.Devices:ConnectForTaskZapitDaq.bonsai").Workflow, "OpenRigs.Devices:ZapitCamera.bonsai").Workflow;
                var pylon = Nested(camera).Select(n => n.Value).OfType<CombinatorBuilder>().Single(c => c.Combinator.GetType().FullName == "Bonsai.Pylon.PylonCapture");
                pylon.Combinator = new StandInPylonCapture();
                builder.Workflow.Build();
                var liveView = builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<OpenRigs.LiveView.StreamDevices>().Single();
                var generated = liveView.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<GroupWorkflowBuilder>().Single();
                return generated.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<GroupWorkflowBuilder>().Select(g => g.Name).ToArray();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        // An include anywhere in the workflow, loaded (its public ArgumentRange loads it), so its nodes can be changed before the build.
        static IncludeWorkflowBuilder Included(ExpressionBuilderGraph workflow, string path)
        {
            var include = Nested(workflow).Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<IncludeWorkflowBuilder>().Single(b => b.Path == path);
            var range = include.ArgumentRange;
            Assert.IsNotNull(include.Workflow, path + " did not load");
            return include;
        }

        // Every node, however deep in groups and loaded includes.
        static System.Collections.Generic.IEnumerable<Bonsai.Dag.Node<ExpressionBuilder, ExpressionBuilderArgument>> Nested(ExpressionBuilderGraph workflow)
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
    public class StandInPylonCapture : Bonsai.Source<StandInGrab>
    {
        /// <summary>Mapped from rig.yml's zapit.camera.serialNumber.</summary>
        public string SerialNumber { get; set; }

        /// <summary>Mapped from rig.yml's zapit.camera.sessionPfsPath.</summary>
        public string ParameterFile { get; set; }

        /// <summary>No frames: the workflow is only built.</summary>
        /// <returns>A sequence that never produces a frame.</returns>
        public override IObservable<StandInGrab> Generate()
        {
            return System.Reactive.Linq.Observable.Never<StandInGrab>();
        }
    }

    /// <summary>A grab's members ZapitCamera reads, as Bonsai.Pylon's frame has them.</summary>
    public class StandInGrab
    {
        /// <summary>The image.</summary>
        public OpenCV.Net.IplImage Image { get; set; }

        /// <summary>The grab's ID and camera timestamp.</summary>
        public StandInGrabResult GrabResult { get; set; }
    }

    /// <summary>A grab result's ID and camera timestamp.</summary>
    public class StandInGrabResult
    {
        /// <summary>The grab's ID.</summary>
        public long ID { get; set; }

        /// <summary>The camera's timestamp.</summary>
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
