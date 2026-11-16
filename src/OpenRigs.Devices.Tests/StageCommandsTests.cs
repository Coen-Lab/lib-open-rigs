using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Control;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class StageCommandsTests
    {
        static string Resource(string name)
        {
            using (var stream = typeof(LickSpoutStage).Assembly.GetManifestResourceStream("OpenRigs.Devices." + name))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        [TestMethod]
        public void EachOnCommandBecomesTheCommandOfItsName()
        {
            var text = Resource("LickSpoutStage.bonsai");
            var heard = Regex.Matches(text, @"""orcl:OnCommand"">\s*<orcl:Name>(\w+)</orcl:Name>").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            var sent = Regex.Matches(text, @"<dev:Command>(\w+)</dev:Command>").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.AreEqual(15, heard.Length);
            CollectionAssert.AreEqual(heard, sent.Take(heard.Length).ToArray(), "each OnCommand feeds the command of its own name");
            foreach (var name in heard) Assert.IsTrue(Enum.IsDefined(typeof(LickSpoutCommand), name), name);
        }

        // A piece declares exactly the commands and parameters it takes (DeclareControls, read as Bonsai reads it),
        // so no workflow declares them for it.
        [DataTestMethod]
        [DataRow("LickSpoutStage.bonsai", "LickSpoutStage")]
        [DataRow("BehaviorBoard.ManualPulses.bonsai", "ManualPulses")]
        public void ThePieceDeclaresWhatItTakes(string file, string piece)
        {
            var text = Resource(file);
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(text))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            var declared = builder.Workflow.Select(n => ExpressionBuilder.GetWorkflowElement(n.Value)).OfType<DeclareControls>().Single();
            Assert.AreEqual(piece, declared.Piece);
            Func<string, string[]> names = type => Regex.Matches(text, @"""orcl:" + type + @""">\s*<orcl:Name>(\w+)</orcl:Name>").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(names("OnCommand"), declared.Commands.Select(c => c.Name).OrderBy(n => n).ToArray());
            CollectionAssert.AreEqual(names("OnParameterSet").Union(names("ParameterDefault")).OrderBy(n => n).ToArray(), declared.Parameters.Select(p => p.Name).OrderBy(n => n).ToArray());
            Assert.IsTrue(declared.Commands.All(c => !string.IsNullOrEmpty(c.Description)) && declared.Parameters.All(p => !string.IsNullOrEmpty(p.Description)), "every declaration says what it does");
        }
    }
}
