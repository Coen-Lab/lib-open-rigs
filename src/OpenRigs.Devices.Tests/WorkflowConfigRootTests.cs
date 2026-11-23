using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// A launch's ConfigRoot, a workflow property mapped onto ReadLabRig alone, reaches every node that reads rig.yml:
    /// ReadLabRig's subjects at run time, and StreamDevices, RecordHarpDevices and ExperimentManifest when the workflow
    /// is built, over the lab's root written into each (BLUE-OPTO-2, 23 Nov 2026: app-rig-control's scratch root reached
    /// ReadLabRig only, so the live view and the manifest read the rig's own rig.yml). The written-in root's rig.yml names
    /// the Behavior board decoy, the launch's main.
    /// </summary>
    [TestClass]
    public class WorkflowConfigRootTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(WorkflowConfigRootTests).Assembly.Location), "Pieces");

        string launchRoot;
        string decoyRoot;

        [TestInitialize]
        public void WriteBothRoots()
        {
            var temp = Path.Combine(Path.GetTempPath(), "WorkflowConfigRootTests", Guid.NewGuid().ToString("N"));
            launchRoot = Path.Combine(temp, "launch");
            decoyRoot = Path.Combine(temp, "decoy");
            var rig = File.ReadAllText(Path.Combine(Folder, "rig.yml"));
            var decoy = rig.Replace("behaviorBoard:\n  main:", "behaviorBoard:\n  decoy:").Replace("behaviorBoard:\r\n  main:", "behaviorBoard:\r\n  decoy:");
            Assert.AreNotEqual(rig, decoy);
            foreach (var root in new[] { launchRoot, decoyRoot }) Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
            File.WriteAllText(Path.Combine(launchRoot, Environment.MachineName, "rig.yml"), rig);
            File.WriteAllText(Path.Combine(decoyRoot, Environment.MachineName, "rig.yml"), decoy);
        }

        [TestCleanup]
        public void DeleteBothRoots()
        {
            try { Directory.Delete(Path.GetDirectoryName(launchRoot), true); } catch (IOException) { }
        }

        WorkflowBuilder Load()
        {
            var xml = File.ReadAllText(Path.Combine(Folder, "ConfigRootEverywhere.bonsai")).Replace("DECOY_ROOT", SecurityElement.Escape(decoyRoot));
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            builder.Workflow.SetWorkflowProperty("ConfigRoot", launchRoot);
            return builder;
        }

        [TestMethod]
        public void EveryNodeThatReadsRigYmlAtBuildTakesTheLaunchsConfigRoot()
        {
            AssertEveryReaderTakesTheLaunchsRoot(Load());
        }

        // The editor builds its own copy of the workflow (and saves one), which keeps the launch's root.
        [TestMethod]
        public void TheEditorsCopyTakesTheLaunchsConfigRootToo()
        {
            AssertEveryReaderTakesTheLaunchsRoot(new WorkflowBuilder(Load().Workflow.ToInspectableGraph().FromInspectableGraph()));
        }

        void AssertEveryReaderTakesTheLaunchsRoot(WorkflowBuilder builder)
        {
            var built = builder.Workflow.Build();

            var streamDevices = Node<OpenRigs.LiveView.StreamDevices>(builder);
            var streamed = Serialize(streamDevices.Workflow);
            Assert.IsTrue(streamed.Contains("BehaviorBoard-Main.Data"), "StreamDevices streams the launch's rig.yml's board");
            Assert.IsFalse(streamed.Contains("BehaviorBoard-Decoy"), "StreamDevices read the written-in root's rig.yml");

            var recorded = Serialize(Node<OpenRigs.HarpDevices.RecordHarpDevices>(builder).Workflow);
            Assert.IsTrue(recorded.Contains("BehaviorBoard-Main.Data"), "RecordHarpDevices records the launch's rig.yml's board");
            Assert.IsFalse(recorded.Contains("BehaviorBoard-Decoy"), "RecordHarpDevices read the written-in root's rig.yml");

            var recorder = Constants(built).Single(value => value != null && value.GetType().FullName == "OpenRigs.Manifest.ManifestRecorder");
            Assert.AreEqual(launchRoot, Field(recorder, "configRoot"), "the manifest records the launch's rig.yml");
            var expected = ((System.Collections.IEnumerable)Field(Field(recorder, "expected"), "Entries")).Cast<object>().Select(e => (string)Field(e, "Path")).ToList();
            CollectionAssert.Contains(expected, "BehaviorBoard-Main/", "the manifest expects the launch's rig.yml's devices: " + string.Join(", ", expected));
            CollectionAssert.DoesNotContain(expected, "BehaviorBoard-Decoy/");
        }

        // ReadLabRig's own subjects, which every reader of rig.yml at run time takes (a camera's video encoder, the clock check).
        [TestMethod]
        public void ReadLabRigPublishesTheLaunchsConfigRootAndRigFile()
        {
            var builder = Load();
            var workflow = new ExpressionBuilderGraph();
            workflow.Add(builder.Workflow.Single(node => ExpressionBuilder.Unwrap(node.Value) is ExternalizedMappingBuilder).Value);
            var mapping = workflow.Single();
            var include = workflow.Add(builder.Workflow.Single(node => ExpressionBuilder.Unwrap(node.Value) is IncludeWorkflowBuilder).Value);
            workflow.AddEdge(mapping, include, new ExpressionBuilderArgument(0));
            var configRoot = workflow.Add(new SubscribeSubject { Name = "ConfigRoot" });
            var rigFile = workflow.Add(new SubscribeSubject { Name = "RigFile" });
            var zip = workflow.Add(new CombinatorBuilder { Combinator = new Bonsai.Reactive.Zip() });
            workflow.AddEdge(configRoot, zip, new ExpressionBuilderArgument(0));
            workflow.AddEdge(rigFile, zip, new ExpressionBuilderArgument(1));
            workflow.AddEdge(zip, workflow.Add(new WorkflowOutputBuilder()), new ExpressionBuilderArgument(0));
            var published = workflow.BuildObservable<Tuple<string, string>>().FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Wait();
            Assert.AreEqual(launchRoot, published.Item1);
            Assert.AreEqual(Path.Combine(launchRoot, Environment.MachineName, "rig.yml"), published.Item2);
        }

        static T Node<T>(WorkflowBuilder builder) where T : ExpressionBuilder
        {
            return builder.Workflow.Select(node => ExpressionBuilder.Unwrap(node.Value)).OfType<T>().Single();
        }

        static string Serialize(ExpressionBuilderGraph workflow)
        {
            var text = new StringBuilder();
            using (var writer = XmlWriter.Create(text)) WorkflowBuilder.Serializer.Serialize(writer, new WorkflowBuilder(workflow));
            return text.ToString();
        }

        static object Field(object owner, string name)
        {
            return owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);
        }

        static List<object> Constants(Expression expression)
        {
            var found = new ConstantFinder();
            found.Visit(expression);
            return found.Values;
        }

        class ConstantFinder : ExpressionVisitor
        {
            public readonly List<object> Values = new List<object>();

            protected override Expression VisitConstant(ConstantExpression node)
            {
                Values.Add(node.Value);
                return base.VisitConstant(node);
            }
        }
    }
}
