using System;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Tests;

namespace OpenRigs.Core.Tests
{
    /// <summary>TaskFileText, built and run in this process (Pieces\TaskFileText.bonsai).</summary>
    [TestClass]
    public class TaskFileTextTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(TaskFileTextTests).Assembly.Location), "Pieces");

        // The file is read at Go, not at Start: written after Start, its text at Go is what comes, once, though Go fires twice.
        [TestMethod]
        public void ReadsTheTaskFileOnceAtTheFirstGoIntoTaskText()
        {
            var path = Path.Combine(Path.GetTempPath(), "task-" + Guid.NewGuid().ToString("N") + ".yml");
            File.WriteAllText(path, "at launch");
            var xml = File.ReadAllText(Path.Combine(Folder, "TaskFileText.bonsai"))
                .Replace("TASK_PATH", SecurityElement.Escape(path)).Replace("GO_AFTER", "PT1S");
            System.Threading.Tasks.Task.Delay(300).ContinueWith(_ => File.WriteAllText(path, "rngSeed: 7"));
            var result = InProcessWorkflow.Run(xml, 5);
            File.Delete(path);
            Assert.IsNull(result.Item2, InProcessWorkflow.Innermost(result.Item2));
            CollectionAssert.AreEquivalent(new[] { "text rngSeed: 7", "subject rngSeed: 7" }, result.Item1.ToArray());
        }
    }
}
