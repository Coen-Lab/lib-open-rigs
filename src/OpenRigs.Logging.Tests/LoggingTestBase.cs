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
    /// <summary>
    /// Shared scaffolding for tests which run a logging workflow headlessly and inspect what it wrote.
    /// </summary>
    public abstract class LoggingTestBase
    {
        protected const string SubjectId = "TestSubject";
        protected const string SessionId = "001";

        /// <summary>
        /// File name of the workflow under test, relative to the test output directory.
        /// </summary>
        protected abstract string WorkflowFileName { get; }

        /// <summary>
        /// A directory unique to the current test, holding the log root and any input fixtures.
        /// </summary>
        protected string TestDirectory { get; private set; }

        /// <summary>
        /// The root folder the workflow logs into. Nothing but logs is ever written here, so any
        /// file found below it can be attributed to the workflow.
        /// </summary>
        protected string LogRoot { get; private set; }

        [TestInitialize]
        public void InitializeTestDirectory()
        {
            // Log outside the repository so that a failed run never leaves files in the working tree.
            TestDirectory = Path.Combine(Path.GetTempPath(), "OpenRigs.Logging.Tests", Guid.NewGuid().ToString("N"));
            LogRoot = Path.Combine(TestDirectory, "logs");
            Directory.CreateDirectory(LogRoot);
        }

        [TestCleanup]
        public void CleanupTestDirectory()
        {
            if (TestDirectory == null || !Directory.Exists(TestDirectory))
            {
                return;
            }

            try
            {
                Directory.Delete(TestDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Leaving a stray temp directory behind should not fail the run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Runs the workflow under test against the log root, with the subject and session every
        /// logging workflow takes, plus any properties specific to the test.
        /// </summary>
        protected BonsaiWorkflowResult RunWorkflow(IEnumerable<KeyValuePair<string, string>> properties)
        {
            var allProperties = new Dictionary<string, string>
            {
                { "SubjectId", SubjectId },
                { "SessionId", SessionId },
                { "Path", LogRoot }
            };

            foreach (var property in properties)
            {
                allProperties.Add(property.Key, property.Value);
            }

            return BonsaiWorkflowRunner.Run(WorkflowFileName, allProperties);
        }

        /// <summary>
        /// Finds every log matching the pattern below the log root, requiring at least one. Log file
        /// names embed a timestamp, so they are searched for rather than reconstructed.
        /// </summary>
        protected string[] FindLogFiles(string searchPattern, BonsaiWorkflowResult result)
        {
            var logFiles = Directory.GetFiles(LogRoot, searchPattern, SearchOption.AllDirectories);
            Assert.AreNotEqual(
                0,
                logFiles.Length,
                $"Expected the workflow to write at least one '{searchPattern}' log file under '{LogRoot}'." +
                result.Describe());
            return logFiles;
        }

        /// <summary>
        /// Finds the one log matching the pattern below the log root.
        /// </summary>
        protected string GetSingleLogFile(string searchPattern, BonsaiWorkflowResult result)
        {
            var logFiles = Directory.GetFiles(LogRoot, searchPattern, SearchOption.AllDirectories);
            Assert.AreEqual(
                1,
                logFiles.Length,
                $"Expected the workflow to write exactly one '{searchPattern}' log file under '{LogRoot}'." +
                result.Describe());
            return logFiles[0];
        }

        /// <summary>
        /// Checks that a path sits below the subject and session folders laid out by LogController,
        /// and returns the segments of the path below the session folder.
        /// </summary>
        protected string[] AssertSessionFolders(string path, BonsaiWorkflowResult result)
        {
            var relativePath = path.Substring(LogRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            var segments = relativePath.Split(Path.DirectorySeparatorChar);
            Assert.IsTrue(
                segments.Length > 2,
                $"Expected '{relativePath}' to sit below the subject and session folders.{result.Describe()}");

            Assert.AreEqual(
                $"sub-{SubjectId}",
                segments[0],
                $"Expected the subject folder to be named sub-<subject>.{result.Describe()}");

            // LogController lays out the session folder as ses-<session>_<datetime> (ucl-open/roadmap#37), where the
            // datetime is a round-trip UTC timestamp with the time separators replaced.
            var sessionFolder = segments[1];
            var sessionMatch = Regex.Match(
                sessionFolder,
                $@"^ses-{Regex.Escape(SessionId)}_(\d{{4}}-\d{{2}}-\d{{2}}T\d{{2}}-\d{{2}}-\d{{2}})$");
            Assert.IsTrue(
                sessionMatch.Success,
                $"Expected the session folder to match ses-{SessionId}_<datetime> but found " +
                $"'{sessionFolder}'.{result.Describe()}");

            Assert.IsTrue(
                DateTime.TryParseExact(
                    sessionMatch.Groups[1].Value,
                    "yyyy-MM-ddTHH-mm-ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var sessionDate),
                $"Could not parse the session date from '{sessionFolder}'.{result.Describe()}");

            // Guards against the session folder picking up a fixed epoch rather than the run time.
            var age = DateTime.UtcNow - sessionDate;
            Assert.IsTrue(
                age > TimeSpan.FromMinutes(-10) && age < TimeSpan.FromMinutes(10),
                $"Expected the session date '{sessionDate:O}' to be close to the time of the run " +
                $"({DateTime.UtcNow:O}).{result.Describe()}");

            return segments.Skip(2).ToArray();
        }

        /// <summary>
        /// Reads a CSV log, checking its header and that it holds the expected number of rows of the
        /// same width, and returns the columns of each row below the header.
        /// </summary>
        protected static string[][] ReadCsvLog(
            string fileName,
            string[] expectedHeader,
            int expectedRows,
            BonsaiWorkflowResult result)
        {
            var lines = File.ReadAllLines(fileName);
            Assert.AreEqual(
                expectedRows + 1,
                lines.Length,
                $"Expected a single header row followed by {expectedRows} rows in '{fileName}'." +
                result.Describe());

            CollectionAssert.AreEqual(
                expectedHeader,
                lines[0].Split(','),
                $"Unexpected header row in '{fileName}'.{result.Describe()}");

            var rows = new string[expectedRows][];
            for (var row = 0; row < expectedRows; row++)
            {
                var line = lines[row + 1];
                rows[row] = line.Split(',');
                Assert.AreEqual(
                    expectedHeader.Length,
                    rows[row].Length,
                    $"Unexpected column count in row {row} ('{line}') of '{fileName}'.{result.Describe()}");
            }

            return rows;
        }

        protected static double ParseDoubleColumn(
            string[] columns,
            int column,
            string[] header,
            int row,
            BonsaiWorkflowResult result)
        {
            Assert.IsTrue(
                double.TryParse(columns[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value),
                $"Expected '{header[column]}' in row {row} to be a number but found " +
                $"'{columns[column]}'.{result.Describe()}");
            return value;
        }

        protected static int ParseIntegerColumn(
            string[] columns,
            int column,
            string[] header,
            int row,
            BonsaiWorkflowResult result)
        {
            Assert.IsTrue(
                int.TryParse(columns[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value),
                $"Expected '{header[column]}' in row {row} to be an integer but found " +
                $"'{columns[column]}'.{result.Describe()}");
            return value;
        }
    }
}
