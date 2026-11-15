using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using OpenRigs.Tests;

namespace OpenRigs.Logging.Tests
{
    // A run's Settings JSON is written once at the start; Bonsai killed later (a crash, or a kill
    // after a stop that did not close) must still leave the whole file (audit, 15 Nov 2026: an empty
    // Settings JSON from a run that exited before the writer's buffer reached the disk).
    [TestClass]
    public class LogDataSchemaKillTests : LoggingTestBase
    {
        protected override string WorkflowFileName => "LogDataSchemaKillTest.bonsai";

        [TestMethod]
        public void LogDataSchema_BonsaiKilledAfterTheWrite_TheJsonIsWhole()
        {
            const string Json = "[1,2.5,true,null,[],[0]]";
            using (var bonsai = BonsaiWorkflowRunner.Start(WorkflowFileName, new Dictionary<string, string>
            {
                { "SubjectId", SubjectId }, { "SessionId", SessionId }, { "Path", LogRoot },
                { "Name", "Schema" }, { "Json", Json }
            }))
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    while (!Directory.Exists(LogRoot) || !Directory.EnumerateFiles(LogRoot, "*.json", SearchOption.AllDirectories).Any())
                    {
                        if (bonsai.HasExited) Assert.Fail("Bonsai ended before writing the JSON (exit " + bonsai.ExitCode + ").");
                        if (clock.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail("No JSON was written within 60 s.");
                        Thread.Sleep(50);
                    }
                    Thread.Sleep(2000);  // the write long done, as at a run's start
                    Assert.IsFalse(bonsai.HasExited, "Bonsai kept running, as a run does");
                }
                finally
                {
                    KillTree(bonsai.Id);
                    bonsai.WaitForExit(10000);
                }
            }

            var file = Directory.GetFiles(LogRoot, "*.json", SearchOption.AllDirectories).Single();
            var text = File.ReadAllText(file);
            Assert.IsTrue(text.Length > 0 && JToken.DeepEquals(JToken.Parse(Json), JToken.Parse(text)),
                "Expected '" + file + "' to hold the JSON written in, but found '" + text + "'.");
        }

        // Bonsai.exe and the workflow's own Bonsai.exe it starts, killed together.
        static void KillTree(int id)
        {
            using (var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", "/F /T /PID " + id) { UseShellExecute = false, CreateNoWindow = true }))
            {
                taskkill.WaitForExit();
            }
        }
    }
}
