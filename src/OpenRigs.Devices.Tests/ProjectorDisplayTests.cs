using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// ProjectorDisplay and RawProjectorDisplay open the same window (ProjectorWindow's) and differ only in the corrections
    /// after it; each refuses a display that is not there, naming itself, before the window opens.
    /// </summary>
    [TestClass]
    public class ProjectorDisplayTests
    {
        static readonly XNamespace Bonsai = "https://bonsai-rx.org/2018/workflow";
        static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        static readonly XNamespace Vision = "clr-namespace:OpenRigs.Vision;assembly=OpenRigs.Vision";

        // RawProjectorDisplay includes ProjectorWindow; ProjectorDisplay keeps the same window in one group with its corrections
        // (fed from ProjectorWindow's output, they left the window never created), so its window must stay ProjectorWindow's.
        [TestMethod]
        public void BothDisplaysCheckThenDisposeTheSameWindow()
        {
            XElement[] WindowChain(XDocument d) => d.Descendants(Bonsai + "Combinator")
                .Where(c => ((string)c.Attribute(Xsi + "type")).EndsWith(":CreateWindow") || ((string)c.Attribute(Xsi + "type")) == "rx:SubscribeOn")
                .ToArray();
            var shared = WindowChain(Embedded("ProjectorWindow"));
            Assert.AreEqual(2, shared.Length, "ProjectorWindow has one CreateWindow and one SubscribeOn");
            Assert.AreEqual(1, Embedded("RawProjectorDisplay").Descendants(Bonsai + "Expression")
                .Count(e => (string)e.Attribute("Path") == "OpenRigs.Vision:ProjectorWindow.bonsai"), "RawProjectorDisplay includes ProjectorWindow once");
            var own = WindowChain(Embedded("ProjectorDisplay"));
            string Settings(XElement[] chain) => string.Join("\n", chain.SelectMany(c => c.DescendantsAndSelf())
                .Select(e => e.Name + "=" + (string)e.Attribute(Xsi + "type") + (string)e.Attribute(Xsi + "nil") + (e.HasElements ? "" : e.Value)));
            Assert.AreEqual(Settings(shared), Settings(own), "ProjectorDisplay's window differs from ProjectorWindow's");

            foreach (var name in new[] { "ProjectorDisplay", "RawProjectorDisplay" })
            {
                var display = Embedded(name);
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
        [DataRow(@"C:\RigConfigs\RIG\calibration\Projector-Mesh.csv", "rig.yml's projector.meshPath is a full path")]
        [DataRow("calibration/Projector-Mesh.csv", "rig.yml's projector.meshPath names calibration/Projector-Mesh.csv, and there is no such file: {0}")]
        public void ProjectorDisplayRefusesAFullOrMissingPath_AtStart(string meshPath, string refusal)
        {
            if (DisplayDevice.GetDisplay(DisplayIndex.First) == null) Assert.Inconclusive("No display here, so ProjectorDisplay stops on that first.");
            var root = Path.Combine(Path.GetTempPath(), "ProjectorDisplayTests", Guid.NewGuid().ToString("N"));
            var rig = Path.Combine(root, Environment.MachineName);
            Directory.CreateDirectory(Path.Combine(rig, "calibration"));
            File.WriteAllText(Path.Combine(rig, "calibration", "Projector-Gamma.bmp"), "");
            File.WriteAllText(Path.Combine(rig, "rig.yml"),
                "rootPath: " + root + "\nprojector:\n  meshPath: " + meshPath + "\n  gammaPath: calibration/Projector-Gamma.bmp\n");
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
        <Selector>Projector</Selector>
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
                    Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(10)), "ProjectorDisplay ran on " + meshPath + ".");
                }
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }

            for (; error?.InnerException != null && !(error is InvalidOperationException); error = error.InnerException) { }
            Assert.IsNotNull(error, "ProjectorDisplay completed instead of refusing.");
            StringAssert.Contains(error.Message, string.Format(refusal, Path.Combine(rig, "calibration", "Projector-Mesh.csv")));
        }

        // A projector with a mesh and a gamma file, as a projector rig's rig.yml has it: the window opens and renders frames on
        // DrawStimuli (64 px and windowed on the first display). With the corrections fed from ProjectorWindow's
        // output across an include, no window was created and no frame came, with no error.
        [TestMethod]
        public void ProjectorDisplayRendersFrames_WithMeshAndGamma()
        {
            if (DisplayDevice.GetDisplay(DisplayIndex.First) == null) Assert.Inconclusive("No display here, so no window can open.");
            RequireHardwareGL();
            var root = Path.Combine(Path.GetTempPath(), "ProjectorDisplayTests", Guid.NewGuid().ToString("N"));
            var rig = Path.Combine(root, Environment.MachineName);
            Directory.CreateDirectory(Path.Combine(rig, "calibration"));
            WriteIdentityMesh(Path.Combine(rig, "calibration", "Projector-Mesh.csv"));
            WriteGammaRamp(Path.Combine(rig, "calibration", "Projector-Gamma.bmp"));
            File.WriteAllText(Path.Combine(rig, "rig.yml"), "rootPath: " + root
                + "\nprojector:\n  meshPath: calibration/Projector-Mesh.csv\n  gammaPath: calibration/Projector-Gamma.bmp\n");
            var xml = $@"<WorkflowBuilder Version=""2.9.0"" xmlns:xsi=""{Xsi}"" xmlns:t=""clr-namespace:OpenRigs.Devices.Tests;assembly=OpenRigs.Devices.Tests"" xmlns=""{Bonsai}"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Devices:ReadLabRig.bonsai"">
        <ConfigRoot>{System.Security.SecurityElement.Escape(root)}</ConfigRoot>
      </Expression>
      <Expression xsi:type=""SubscribeSubject"">
        <Name>RigConfigs</Name>
      </Expression>
      <Expression xsi:type=""MemberSelector"">
        <Selector>Projector</Selector>
      </Expression>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Vision:ProjectorDisplay.bonsai"">
        <DisplayDevice>First</DisplayDevice>
        <WindowState>Normal</WindowState>
        <Width>64</Width>
        <Height>64</Height>
      </Expression>
      <Expression xsi:type=""SubscribeSubject"">
        <Name>DrawStimuli</Name>
      </Expression>
      <Expression xsi:type=""Combinator"">
        <Combinator xsi:type=""t:{nameof(FrameSeen)}"" />
      </Expression>
    </Nodes>
    <Edges>
      <Edge From=""1"" To=""2"" Label=""Source1"" />
      <Edge From=""2"" To=""3"" Label=""Source1"" />
      <Edge From=""4"" To=""5"" Label=""Source1"" />
    </Edges>
  </Workflow>
</WorkflowBuilder>";
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }

            FrameSeen.Seen.Reset();
            Exception error = null;
            bool rendered;
            try
            {
                using (builder.Workflow.BuildObservable().Subscribe(_ => { }, e => error = e))
                {
                    rendered = FrameSeen.Seen.Wait(TimeSpan.FromSeconds(20));
                }
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }

            Assert.IsNull(error, "ProjectorDisplay stopped: " + error);
            Assert.IsTrue(rendered, "ProjectorDisplay rendered no frame in 20 s: no window was created.");
        }

        // GitHub's runner has only Windows' software OpenGL 1.1 ("GDI Generic"), where the first GL.GenBuffers is an
        // access violation that kills the test host; ProjectorDisplay needs OpenGL 3.3, so read the version in a hidden window first.
        static void RequireHardwareGL()
        {
            string version, renderer;
            using (var window = new GameWindow(1, 1))
            {
                window.MakeCurrent();
                version = GL.GetString(StringName.Version) ?? "";
                renderer = GL.GetString(StringName.Renderer) ?? "";
            }
            var number = System.Text.RegularExpressions.Regex.Match(version, @"^\d+\.\d+");
            if (renderer == "GDI Generic" || !number.Success
                || Version.Parse(number.Value) < new Version(3, 3))
                Assert.Inconclusive($"OpenGL {version} on {renderer} here; ProjectorDisplay needs 3.3 on graphics hardware.");
        }

        // BonVision's mesh: 30 by 30 rows of x, y, u, v, intensity, u fastest; here the identity map.
        static void WriteIdentityMesh(string path)
        {
            const int n = 30;
            var rows = from v in Enumerable.Range(0, n) from u in Enumerable.Range(0, n)
                       select string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3},1",
                           2.0 * u / (n - 1) - 1, 2.0 * v / (n - 1) - 1, (double)u / (n - 1), (double)v / (n - 1));
            File.WriteAllLines(path, rows);
        }

        // A 256 by 1, 24-bit gamma lookup: the identity ramp.
        static void WriteGammaRamp(string path)
        {
            const int width = 256, header = 54, size = header + width * 3;
            var bmp = new byte[size];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(size).CopyTo(bmp, 2);
            BitConverter.GetBytes(header).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);
            BitConverter.GetBytes(width).CopyTo(bmp, 18);
            BitConverter.GetBytes(1).CopyTo(bmp, 22);
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
            BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
            BitConverter.GetBytes(width * 3).CopyTo(bmp, 34);
            for (var i = 0; i < width; i++) bmp[header + 3 * i] = bmp[header + 3 * i + 1] = bmp[header + 3 * i + 2] = (byte)i;
            File.WriteAllBytes(path, bmp);
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

    /// <summary>Sets <see cref="Seen"/> on the first value: here, the first frame drawn on DrawStimuli.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class FrameSeen
    {
        internal static readonly ManualResetEventSlim Seen = new ManualResetEventSlim();

        /// <summary>Passes the sequence on, setting <see cref="Seen"/> at its first value.</summary>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return System.Reactive.Linq.Observable.Do(source, _ => Seen.Set());
        }
    }
}
