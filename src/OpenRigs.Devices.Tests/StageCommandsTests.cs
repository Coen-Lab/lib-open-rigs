using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class StageCommandsTests
    {
        [TestMethod]
        public void EachOnCommandBecomesTheCommandOfItsName()
        {
            var assembly = typeof(LickSpoutStage).Assembly;
            string text;
            using (var stream = assembly.GetManifestResourceStream("OpenRigs.Devices.HarpLickSpoutStage.bonsai"))
            using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            var heard = Regex.Matches(text, @"<orcl:Name>(\w+)</orcl:Name>").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            var sent = Regex.Matches(text, @"<dev:Command>(\w+)</dev:Command>").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.AreEqual(14, heard.Length);
            CollectionAssert.AreEqual(heard, sent.Take(heard.Length).ToArray(), "each OnCommand feeds the command of its own name");
            foreach (var name in heard) Assert.IsTrue(Enum.IsDefined(typeof(LickSpoutCommand), name), name);
            StringAssert.Contains(text, "The workflow declares the commands: " + string.Join(", ", heard) + ".");
        }
    }
}
