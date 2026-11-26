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
using System.Text.RegularExpressions;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// A launch's ConfigRoot, a workflow property mapped onto ReadLabRig alone, is the one root every node that reads
    /// rig.yml takes: ReadLabRig's subjects at run time, and StreamDevices, RecordHarpDevices and ExperimentManifest when
    /// the workflow is built (23 Nov 2026: a scratch root reached ReadLabRig only, so the live view and the manifest read
    /// the rig's own rig.yml from a root written into each). The root written into ReadLabRig is a decoy whose rig.yml
    /// names the Behavior board decoy; the launch's names main.
    /// </summary>
    [TestClass]
    public class WorkflowConfigRootTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(WorkflowConfigRootTests).Assembly.Location), "Pieces");
        const string Readers = "hd:RecordHarpDevices|mf:ExperimentManifest|lv:StreamDevices";

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

        string Xml()
        {
            return File.ReadAllText(Path.Combine(Folder, "ConfigRootEverywhere.bonsai")).Replace("DECOY_ROOT", SecurityElement.Escape(decoyRoot));
        }

        static WorkflowBuilder Deserialize(string xml)
        {
            using (var reader = XmlReader.Create(new StringReader(xml))) return (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
        }

        WorkflowBuilder Load()
        {
            var builder = Deserialize(Xml());
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

        // A reader has no root of its own to fall back on: built with no ReadLabRig before it, the build stops, naming it.
        [DataTestMethod]
        [DataRow("hd:RecordHarpDevices", "RecordHarpDevices")]
        [DataRow("mf:ExperimentManifest", "ExperimentManifest")]
        [DataRow("lv:StreamDevices", "StreamDevices")]
        public void AReaderWithNoReadLabRigBeforeItStopsTheBuild(string type, string name)
        {
            var xml = Regex.Replace(Xml(), "<Expression xsi:type=\"(ExternalizedMapping|IncludeWorkflow)\".*?</Expression>", "", RegexOptions.Singleline);
            xml = Regex.Replace(xml, "<Expression xsi:type=\"(" + Readers + ")\"( />|>.*?</Expression>)",
                m => m.Groups[1].Value == type ? m.Value : "", RegexOptions.Singleline).Replace("<Edge From=\"0\" To=\"1\" Label=\"Source1\" />", "");
            StringAssert.Contains(xml, type);
            Assert.IsFalse(xml.Contains("ReadLabRig.bonsai"));
            var stopped = Assert.ThrowsException<WorkflowBuildException>(() => Deserialize(xml).Workflow.Build());
            StringAssert.StartsWith(stopped.Message, name + " reads rig.yml from the workflow's ConfigRoot, and none was built before it");
            StringAssert.Contains(stopped.Message, "include OpenRigs.Devices' ReadLabRig before this node");
        }

        // The readers and ExperimentRecords have no ConfigRoot of their own: a root a workflow still writes into one (the
        // decoy, as the lab's workflows did) is no property of it, and the launch's root is the one read.
        [TestMethod]
        public void NoReaderHasAConfigRootAndOneStillWrittenInIsNotRead()
        {
            var xml = Xml();
            foreach (var type in Readers.Split('|'))
            {
                var prefix = type.Split(':')[0];
                xml = Regex.Replace(xml, "<Expression xsi:type=\"" + type + "\"( />|>)", m =>
                    "<Expression xsi:type=\"" + type + "\"><" + prefix + ":ConfigRoot>" + SecurityElement.Escape(decoyRoot) + "</" + prefix + ":ConfigRoot>" +
                    (m.Groups[1].Value == " />" ? "</Expression>" : ""));
                StringAssert.Contains(xml, prefix + ":ConfigRoot>");
            }
            var builder = Deserialize(xml);
            builder.Workflow.SetWorkflowProperty("ConfigRoot", launchRoot);
            foreach (var node in builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).Where(b => Regex.IsMatch(b.GetType().Name, "^(RecordHarpDevices|ExperimentManifest|StreamDevices)$")))
            {
                Assert.IsNull(System.ComponentModel.TypeDescriptor.GetProperties(node)["ConfigRoot"], node.GetType().Name + " has a ConfigRoot");
            }
            AssertEveryReaderTakesTheLaunchsRoot(builder);

            var records = (IncludeWorkflowBuilder)Deserialize(Regex.Replace(xml, "<Expression xsi:type=\"(" + Readers + ")\"( />|>.*?</Expression>)", "", RegexOptions.Singleline)
                .Replace("</Nodes>", "<Expression xsi:type=\"IncludeWorkflow\" Path=\"OpenRigs.Logging:ExperimentRecords.bonsai\"><ConfigRoot>x</ConfigRoot><IngestScript>test</IngestScript></Expression></Nodes>"))
                .Workflow.Select(n => n.Value).OfType<IncludeWorkflowBuilder>().Single(b => b.Path.EndsWith("ExperimentRecords.bonsai"));
            Assert.IsNotNull(records.ArgumentRange);
            var properties = System.ComponentModel.TypeDescriptor.GetProperties(records);
            Assert.IsNotNull(properties["IngestScript"], "ExperimentRecords' workflow did not load");
            Assert.IsNotNull(properties["IngestScripts"], "ExperimentRecords takes no IngestScripts");
            // The include above sets the old one-name IngestScript, which still loads.
            Assert.IsNull(properties["ConfigRoot"], "ExperimentRecords has a ConfigRoot");
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

        void AssertEveryReaderTakesTheLaunchsRoot(WorkflowBuilder builder)
        {
            var built = builder.Workflow.Build();

            var streamed = Serialize(Node<OpenRigs.LiveView.StreamDevices>(builder).Workflow);
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
