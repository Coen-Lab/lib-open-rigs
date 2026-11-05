using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Tests;

namespace OpenRigs.Logging.Tests
{
    [TestClass]
    public class LogHarpTests : LoggingTestBase
    {
        protected override string WorkflowFileName => "LogHarpTest.bonsai";

        [TestMethod]
        public void LogHarpDevice_SimulatedBehavior_WritesExpectedDirectoryStructure()
        {
            const string LogName = "SimulatedBehavior";
            var result = RunWorkflow(LogName, count: 3);
            var logFiles = FindLogFiles("*.bin", result);

            // Every register of the device shares a single folder named after the device.
            var deviceFolders = logFiles
                .Select(Path.GetDirectoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.AreEqual(
                1,
                deviceFolders.Length,
                $"Expected every register log to share one device folder, but found " +
                $"{deviceFolders.Length}: {string.Join(", ", deviceFolders)}.{result.Describe()}");

            var segments = AssertSessionFolders(deviceFolders[0], result);
            Assert.AreEqual(
                1,
                segments.Length,
                $"Expected the device folder to sit directly below the session folder, but found " +
                $"'{string.Join(Path.DirectorySeparatorChar.ToString(), segments)}'.{result.Describe()}");

            Assert.AreEqual(
                LogName,
                segments[0],
                $"Expected the device folder to be named after LogName.{result.Describe()}");

            // Each register still gets its own file, distinguished by the register address.
            foreach (var logFile in logFiles)
            {
                var address = GetRegisterAddress(logFile, LogName, result);
                Assert.IsTrue(
                    BehaviorDeviceSimulator.Registers.Any(register => register.Address == address),
                    $"Address {address} in '{Path.GetFileName(logFile)}' is not a register of the " +
                    $"simulated device.{result.Describe()}");
            }

            var addresses = logFiles.Select(logFile => GetRegisterAddress(logFile, LogName, result)).ToArray();
            CollectionAssert.AllItemsAreUnique(
                addresses,
                $"Expected one log file per register address.{result.Describe()}");
        }

        [DataTestMethod]
        [DataRow("TestData", 1)]
        [DataRow("SimulatedBehavior", 3)]
        [DataRow("SimulatedBehavior", 20)]
        [DataRow("TestData", 100)]
        public void LogHarpDevice_SimulatedBehavior_WritesRequestedSampleCountAcrossRegisters(
            string logName,
            int count)
        {
            var result = RunWorkflow(logName, count);
            var logFiles = FindLogFiles("*.bin", result);

            // Each register is logged to its own file, so the requested Count is spread across the
            // per-register logs rather than landing in any single one.
            var samplesPerRegister = logFiles.ToDictionary(
                logFile => GetRegisterAddress(logFile, logName, result),
                HarpBinaryLog.CountMessages);
            var totalSamples = samplesPerRegister.Values.Sum();

            var breakdown = string.Join(
                ", ",
                samplesPerRegister.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}"));
            Assert.AreEqual(
                count,
                totalSamples,
                $"Expected the samples across all register logs to add up to the requested Count. " +
                $"Found {logFiles.Length} logs: {breakdown}.{result.Describe()}");
        }

        int GetRegisterAddress(string logFile, string logName, BonsaiWorkflowResult result)
        {
            // Log files are named <LogName>_<address>_<timestamp>, where the timestamp is appended
            // by the writer rather than being part of the register name.
            var fileName = Path.GetFileName(logFile);
            var match = Regex.Match(fileName, $@"^{Regex.Escape(logName)}_(\d+)_");
            Assert.IsTrue(
                match.Success,
                $"Expected the log file name '{fileName}' to match {logName}_<address>_<timestamp>." +
                result.Describe());
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        BonsaiWorkflowResult RunWorkflow(string logName, int count)
        {
            return RunWorkflow(new Dictionary<string, string>
            {
                // The workflow should externalize LogHarpDevice's LogName under this display name.
                { "HarpLogName", logName },
                { "Count", count.ToString(CultureInfo.InvariantCulture) },
                // The workflow's 0.5 s default made 100 samples a 50 s wait; what is checked is the
                // count, not the rate.
                { "Period", "0.01" }
            });
        }
    }
}
