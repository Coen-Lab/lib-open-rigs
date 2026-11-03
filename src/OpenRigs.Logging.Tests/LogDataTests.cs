using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Tests;

namespace OpenRigs.Logging.Tests
{
    [TestClass]
    public class LogDataTests : LoggingTestBase
    {
        static readonly string[] ExpectedHeader = { "Seconds", "X", "Y", "Z" };

        protected override string WorkflowFileName => "LogDataTest.bonsai";

        [DataTestMethod]
        [DataRow("Point3Data", 3)]
        [DataRow("TestData", 5)]
        public void LogData_TimestampedPoint3d_WritesRequestedSamplesToNamedLog(string logName, int count)
        {
            var result = RunWorkflow(logName, count);
            var logFile = GetSingleLogFile("*.csv", result);

            var logFileName = Path.GetFileName(logFile);
            Assert.IsTrue(
                logFileName.StartsWith(logName, StringComparison.Ordinal),
                $"Expected the log file name '{logFileName}' to be prefixed with the requested " +
                $"LogName '{logName}'.{result.Describe()}");

            var rows = ReadCsvLog(logFile, ExpectedHeader, count, result);
            for (var sample = 0; sample < count; sample++)
            {
                var columns = rows[sample];
                ParseDoubleColumn(columns, 0, ExpectedHeader, sample, result);

                // The workflow maps the element index onto all three axes of the logged point.
                for (var axis = 1; axis < ExpectedHeader.Length; axis++)
                {
                    Assert.AreEqual(
                        sample,
                        ParseIntegerColumn(columns, axis, ExpectedHeader, sample, result),
                        $"Unexpected '{ExpectedHeader[axis]}' in sample {sample}.{result.Describe()}");
                }
            }
        }

        [TestMethod]
        public void LogData_TimestampedPoint3d_WritesExpectedDirectoryStructure()
        {
            const string LogName = "Point3Data";
            var result = RunWorkflow(LogName, count: 3);
            var logFile = GetSingleLogFile("*.csv", result);

            var segments = AssertSessionFolders(logFile, result);
            Assert.AreEqual(
                2,
                segments.Length,
                $"Expected the log to sit one folder below the session folder, but found " +
                $"'{string.Join(Path.DirectorySeparatorChar.ToString(), segments)}'.{result.Describe()}");

            Assert.AreEqual(
                LogName,
                segments[0],
                $"Expected the log subfolder to be named after LogName.{result.Describe()}");

            StringAssert.Matches(
                segments[1],
                new Regex($@"^{Regex.Escape(LogName)}_.+\.csv$"),
                $"Expected the data file to sit inside the '{LogName}' folder and be prefixed with " +
                $"it.{result.Describe()}");
        }

        BonsaiWorkflowResult RunWorkflow(string logName, int count)
        {
            return RunWorkflow(new Dictionary<string, string>
            {
                { "LogName", logName },
                { "Count", count.ToString(CultureInfo.InvariantCulture) }
            });
        }
    }
}
