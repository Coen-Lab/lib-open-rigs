using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Trials.Tests
{
    [TestClass]
    public class GoldenTaskFilesTests
    {
        [TestMethod]
        public void EachGoldenFileIsReadInOrderWithItsSeedSetTo1AndAFileWithoutOneNullSeedIsRefused()
        {
            var folder = Path.Combine(Path.GetTempPath(), "golden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "b.yml"), "rngSeed: null\nx: 2");
            File.WriteAllText(Path.Combine(folder, "a.yml"), "rngSeed: null\nx: 1");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a task file");
            var read = GoldenTaskFiles.Read(folder);
            CollectionAssert.AreEqual(new[] { "a.yml", "b.yml" }, read.Select(g => g.Key).ToList());
            Assert.AreEqual("rngSeed: 1\nx: 1", read[0].Value);
            File.WriteAllText(Path.Combine(folder, "c.yml"), "rngSeed: 3");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => GoldenTaskFiles.Read(folder)).Message, "c.yml must have rngSeed: null once");
            Directory.Delete(folder, true);
        }

        [TestMethod]
        public void ACombinationDrawnAwayFromItsShareIsNamed()
        {
            // Weights 3 and 1, the first with two combinations: shares 3/8, 3/8 and 1/4.
            var built = new List<BuiltTrialCondition<string>>
            {
                new BuiltTrialCondition<string> { Weight = 3, Where = "trialConditions[0]", Combinations = new List<TrialConditionCombination<string>>
                {
                    new TrialConditionCombination<string> { Id = 0, Picks = "a" }, new TrialConditionCombination<string> { Id = 1, Picks = "b" },
                } },
                new BuiltTrialCondition<string> { Weight = 1, Where = "trialConditions[1]", Combinations = new List<TrialConditionCombination<string>>
                {
                    new TrialConditionCombination<string> { Id = 2, Picks = "" },
                } },
            };
            Assert.AreEqual(0, GoldenTaskFiles.ShareProblems("f.yml", built, new Dictionary<int, int> { { 0, 1880 }, { 1, 1870 }, { 2, 1250 } }, 5000).Count);
            CollectionAssert.AreEqual(new[] { "f.yml trialConditions[0] b: share 0.25, expected 0.375", "f.yml trialConditions[1] : share 0.375, expected 0.25" },
                GoldenTaskFiles.ShareProblems("f.yml", built, new Dictionary<int, int> { { 0, 1875 }, { 1, 1250 }, { 2, 1875 } }, 5000));
        }
    }
}
