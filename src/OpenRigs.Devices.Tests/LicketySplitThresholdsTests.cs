using Bonsai;
using Bonsai.Expressions;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Xml;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// The LicketySplit piece writes a channel 0 threshold (register 33 trigger, 34 untrigger) only when its rig.yml
    /// entry sets it: 0/0, the template's values from 15 Nov 2026, hung the board about 1 s after start. Run on a copy
    /// of the piece whose device emits nothing, with one message injected into its Data as the board's first.
    /// </summary>
    [TestClass]
    public class LicketySplitThresholdsTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(LicketySplitThresholdsTests).Assembly.Location), "Pieces");

        const string Workflow = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkflowBuilder Version=""2.9.0"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                 xmlns:rx=""clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core""
                 xmlns:sys=""clr-namespace:System;assembly=mscorlib""
                 xmlns:harp=""clr-namespace:Bonsai.Harp;assembly=Bonsai.Harp""
                 xmlns:p1=""clr-namespace:AllenNeuralDynamics.LicketySplit;assembly=AllenNeuralDynamics.LicketySplit""
                 xmlns=""https://bonsai-rx.org/2018/workflow"">
  <Workflow>
    <Nodes>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Devices:ReadLabRig.bonsai""><ConfigRoot>CONFIG_ROOT</ConfigRoot></Expression>
      <Expression xsi:type=""rx:BehaviorSubject"" TypeArguments=""sys:String""><rx:Name>PathPrefix</rx:Name></Expression>
      <Expression xsi:type=""IncludeWorkflow"" Path=""OpenRigs.Devices:ReadyGate.bonsai""><PieceStages>LickDetector</PieceStages></Expression>
      <Expression xsi:type=""IncludeWorkflow"" Path=""PIECE"">
        <Name>LicketySplit-Left</Name>
        <CommandsSubjectName>LicketySplit-Left.Commands</CommandsSubjectName>
        <DataSubjectName>LicketySplit-Left.Data</DataSubjectName>
        <LickStateSubjectName>LickStateLeft</LickStateSubjectName>
      </Expression>
      <Expression xsi:type=""Combinator""><Combinator xsi:type=""rx:Timer""><rx:DueTime>PT0.5S</rx:DueTime><rx:Period>PT0S</rx:Period></Combinator></Expression>
      <Expression xsi:type=""p1:CreateMessage"">
        <harp:MessageType>Read</harp:MessageType>
        <harp:Payload xsi:type=""p1:CreateChannel0TriggerThresholdPayload""><p1:Channel0TriggerThreshold>7</p1:Channel0TriggerThreshold></harp:Payload>
      </Expression>
      <Expression xsi:type=""MulticastSubject""><Name>LicketySplit-Left.Data</Name></Expression>
      <Expression xsi:type=""SubscribeSubject""><Name>LicketySplit-Left.Commands</Name></Expression>
      <Expression xsi:type=""WorkflowOutput"" />
    </Nodes>
    <Edges>
      <Edge From=""4"" To=""5"" Label=""Source1"" />
      <Edge From=""5"" To=""6"" Label=""Source1"" />
      <Edge From=""7"" To=""8"" Label=""Source1"" />
    </Edges>
  </Workflow>
</WorkflowBuilder>";

        // Each write the piece sends its board in the first 2 s, "address=value", with the left entry's lines given.
        static string Writes(string thresholds)
        {
            var root = Path.Combine(Path.GetTempPath(), "LicketySplitThresholdsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, Environment.MachineName));
            try
            {
                var rig = File.ReadAllText(Path.Combine(Folder, "rig.yml"));
                var given = Regex.Replace(rig, @"(?m)^(licketySplit:\r?\n  left:\r?\n)", "$1" + thresholds);
                Assert.AreNotEqual(thresholds.Length > 0, rig == given, "the left entry was not found in Pieces\\rig.yml");
                File.WriteAllText(Path.Combine(root, Environment.MachineName, "rig.yml"), given);

                var source = Path.Combine(PiecesSubscribeTestsSolution(), "src", "OpenRigs.Devices", "LicketySplit.bonsai");
                var piece = File.ReadAllText(source);
                var silent = Regex.Replace(piece, @"<Combinator xsi:type=""p1:Device"">.*?</Combinator>", @"<Combinator xsi:type=""rx:IgnoreElements"" />", RegexOptions.Singleline)
                    .Replace(@"<Property Name=""PortName"" Selector=""Settings.PortName"" />", "");
                Assert.AreNotEqual(piece, silent);
                var piecePath = Path.Combine(root, "LicketySplit.bonsai");
                File.WriteAllText(piecePath, silent);

                var xml = Workflow.Replace("CONFIG_ROOT", SecurityElement.Escape(root)).Replace("PIECE", SecurityElement.Escape(piecePath));
                WorkflowBuilder builder;
                using (var reader = XmlReader.Create(new StringReader(xml)))
                {
                    builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                }
                var run = Expression.Lambda<Func<IObservable<HarpMessage>>>(builder.Workflow.Build()).Compile()();
                var writes = run.TakeUntil(Observable.Timer(TimeSpan.FromSeconds(2))).ToList().Wait();
                return string.Join(" ", writes.Where(m => m.MessageType == MessageType.Write).Select(m => m.Address + "=" + m.GetPayloadByte()));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        static string PiecesSubscribeTestsSolution()
        {
            var directory = new DirectoryInfo(Folder);
            while (!File.Exists(Path.Combine(directory.FullName, "OpenRigs.sln"))) directory = directory.Parent;
            return directory.FullName;
        }

        [TestMethod]
        public void UnsetThresholdsWriteNothingSoTheBoardKeepsItsOwn()
        {
            Assert.AreEqual("", Writes(""));
        }

        [TestMethod]
        public void SetThresholdsAreWrittenToRegisters33And34()
        {
            var writes = Writes("    channel0TriggerThreshold: 90\n    channel0UntriggerThreshold: 40\n");
            CollectionAssert.AreEquivalent(new[] { "33=90", "34=40" }, writes.Split(' '), writes);
        }
    }
}
