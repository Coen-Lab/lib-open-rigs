using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// ProjectorDisplay and RawProjectorDisplay share one window (ProjectorWindow) and differ only in the corrections
    /// after it; each refuses a display that is not there, naming itself, before the window opens.
    /// </summary>
    [TestClass]
    public class ProjectorDisplayTests
    {
        static readonly XNamespace Bonsai = "https://bonsai-rx.org/2018/workflow";
        static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        static readonly XNamespace Vision = "clr-namespace:OpenRigs.Vision;assembly=OpenRigs.Vision";

        [TestMethod]
        public void BothDisplaysCheckThenDisposeTheOneSharedWindow()
        {
            foreach (var name in new[] { "ProjectorDisplay", "RawProjectorDisplay" })
            {
                var display = Embedded(name);
                Assert.IsFalse(display.Descendants(Bonsai + "Combinator").Any(c => ((string)c.Attribute(Xsi + "type")).EndsWith(":CreateWindow")),
                    name + " has a window of its own");
                Assert.AreEqual(1, display.Descendants(Bonsai + "Expression")
                    .Count(e => (string)e.Attribute("Path") == "OpenRigs.Vision:ProjectorWindow.bonsai"), name + " includes ProjectorWindow once");

                var nodes = display.Root.Element(Bonsai + "Workflow").Element(Bonsai + "Nodes").Elements(Bonsai + "Expression").ToList();
                var edges = display.Root.Element(Bonsai + "Workflow").Element(Bonsai + "Edges").Elements(Bonsai + "Edge").ToList();
                int Index(string type) => nodes.FindIndex(n => (string)n.Element(Bonsai + "Combinator")?.Attribute(Xsi + "type") == type);
                var check = Index("vis:RequireDisplay");
                var dispose = Index("vis:DisposeOnRenderThread");
                Assert.AreEqual(name, (string)nodes[check].Element(Bonsai + "Combinator").Element(Vision + "Name"), name + "'s refusal names it");
                Assert.IsTrue(edges.Any(e => (int)e.Attribute("From") == check && (int)e.Attribute("To") == dispose),
                    name + " checks the display inside its render-thread disposal");
            }
        }

        // A display index past the last one connected: the refusal names RawProjectorDisplay and that index, and comes before
        // any window (64 px and windowed here, should the check ever let one through).
        [TestMethod]
        public void RawProjectorDisplayRefusesAMissingDisplay_NamingItself()
        {
            var missing = new[] { DisplayIndex.Second, DisplayIndex.Third, DisplayIndex.Fourth, DisplayIndex.Fifth, DisplayIndex.Sixth }
                .Where(d => DisplayDevice.GetDisplay(d) == null).Cast<DisplayIndex?>().FirstOrDefault();
            if (missing == null) Assert.Inconclusive("Six displays are connected here, so none is missing.");

            var xml = $@"<WorkflowBuilder Version=""2.9.0"" xmlns:xsi=""{Xsi}"" xmlns=""{Bonsai}"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Vision:RawProjectorDisplay.bonsai"">
        <DisplayDevice>{missing}</DisplayDevice>
        <WindowState>Normal</WindowState>
        <Width>64</Width>
        <Height>64</Height>
      </Expression>
    </Nodes>
    <Edges />
  </Workflow>
</WorkflowBuilder>";
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }

            Exception error = null;
            var stopped = new ManualResetEventSlim();
            using (builder.Workflow.BuildObservable().Subscribe(_ => { }, e => { error = e; stopped.Set(); }, stopped.Set))
            {
                Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(10)), "RawProjectorDisplay ran on a missing display.");
            }

            for (; error?.InnerException != null && !(error is InvalidOperationException); error = error.InnerException) { }
            Assert.IsNotNull(error, "RawProjectorDisplay completed instead of refusing.");
            StringAssert.Contains(error.Message, "RawProjectorDisplay");
            StringAssert.Contains(error.Message, "DisplayDevice " + missing + " is not a display");
        }

        // rig.yml's projector files are relative to its folder. A full path, or a relative
        // one naming no file in <ConfigRoot>\<MACHINE>, stops ProjectorDisplay at Start naming the key, as built from rig.yml
        // by ReadLabRig (64 px and windowed on the first display, should a refusal ever let it through).
        [DataTestMethod]
        [DataRow(@"C:\RigConfigs\RIG\calibration\MeshMap.csv", "rig.yml's projectionCalibration.meshMapPath is a full path")]
        [DataRow("calibration/MeshMap.csv", "rig.yml's projectionCalibration.meshMapPath names calibration/MeshMap.csv, and there is no such file: {0}")]
        public void ProjectorDisplayRefusesAFullOrMissingPath_AtStart(string meshMapPath, string refusal)
        {
            if (DisplayDevice.GetDisplay(DisplayIndex.First) == null) Assert.Inconclusive("No display here, so ProjectorDisplay stops on that first.");
            var root = Path.Combine(Path.GetTempPath(), "ProjectorDisplayTests", Guid.NewGuid().ToString("N"));
            var rig = Path.Combine(root, Environment.MachineName);
            Directory.CreateDirectory(Path.Combine(rig, "calibration"));
            File.WriteAllText(Path.Combine(rig, "calibration", "gammalut.bmp"), "");
            File.WriteAllText(Path.Combine(rig, "rig.yml"),
                "rootPath: " + root + "\nprojectionCalibration:\n  meshMapPath: " + meshMapPath + "\n  gammaLutPath: calibration/gammalut.bmp\n");
            var xml = $@"<WorkflowBuilder Version=""2.9.0"" xmlns:xsi=""{Xsi}"" xmlns=""{Bonsai}"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Devices:ReadLabRig.bonsai"">
        <ConfigRoot>{System.Security.SecurityElement.Escape(root)}</ConfigRoot>
      </Expression>
      <Expression xsi:type=""SubscribeSubject"">
        <Name>RigConfigs</Name>
      </Expression>
      <Expression xsi:type=""MemberSelector"">
        <Selector>ProjectionCalibration</Selector>
      </Expression>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Vision:ProjectorDisplay.bonsai"">
        <DisplayDevice>First</DisplayDevice>
        <WindowState>Normal</WindowState>
        <Width>64</Width>
        <Height>64</Height>
      </Expression>
    </Nodes>
    <Edges>
      <Edge From=""1"" To=""2"" Label=""Source1"" />
      <Edge From=""2"" To=""3"" Label=""Source1"" />
    </Edges>
  </Workflow>
</WorkflowBuilder>";
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }

            Exception error = null;
            var stopped = new ManualResetEventSlim();
            try
            {
                using (builder.Workflow.BuildObservable().Subscribe(_ => { }, e => { error = e; stopped.Set(); }, stopped.Set))
                {
                    Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(10)), "ProjectorDisplay ran on " + meshMapPath + ".");
                }
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }

            for (; error?.InnerException != null && !(error is InvalidOperationException); error = error.InnerException) { }
            Assert.IsNotNull(error, "ProjectorDisplay completed instead of refusing.");
            StringAssert.Contains(error.Message, string.Format(refusal, Path.Combine(rig, "calibration", "MeshMap.csv")));
        }

        static XDocument Embedded(string name)
        {
            using (var stream = typeof(OpenRigs.Vision.RequireDisplay).Assembly.GetManifestResourceStream("OpenRigs.Vision." + name + ".bonsai"))
            {
                Assert.IsNotNull(stream, name + ".bonsai is not embedded in OpenRigs.Vision.");
                return XDocument.Load(stream);
            }
        }
    }
}
