using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Logging.Tests
{
    /// <summary>
    /// LogTrial writes a trial record into BonsaiData_Trial with every field as a column, the way
    /// LogData does with an empty Selector.
    /// </summary>
    [TestClass]
    public class LogTrialTests : LoggingTestBase
    {
        const int Count = 5;
        static readonly string[] ExpectedHeader = { "Seconds", "Label", "Count", "IsEven", "Maybe" };

        protected override string WorkflowFileName => "LogTrialTest.bonsai";

        [TestMethod]
        public void LogTrial_WritesOneRowPerTrialIntoBonsaiDataTrial()
        {
            var result = RunWorkflow(new Dictionary<string, string>
            {
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
            var logFile = GetSingleLogFile("*.csv", result);

            var below = AssertSessionFolders(logFile, result);
            // FormatFileName names the folder by the log name up to its first underscore, so both files share BonsaiData.
            Assert.AreEqual("BonsaiData", below[0], $"Expected the BonsaiData folder.{result.Describe()}");
            StringAssert.StartsWith(Path.GetFileName(logFile), "BonsaiData_Trial_", result.Describe());

            var rows = ReadCsvLog(logFile, ExpectedHeader, Count, result);
            for (var trial = 0; trial < Count; trial++)
            {
                Assert.AreEqual(trial.ToString(CultureInfo.InvariantCulture), rows[trial][2], $"Unexpected Count in row {trial}.{result.Describe()}");
            }
        }
    }

    /// <summary>
    /// LogAsync declares AsyncEvents and writes every event sent to it into BonsaiData_Async, as
    /// Seconds,Event,Detail,Value.
    /// </summary>
    [TestClass]
    public class LogAsyncTests : LoggingTestBase
    {
        const int Count = 4;
        static readonly string[] ExpectedHeader = { "Seconds", "Event", "Detail", "Value" };

        protected override string WorkflowFileName => "LogAsyncTest.bonsai";

        [TestMethod]
        public void LogAsync_WritesEveryEventIntoBonsaiDataAsync()
        {
            var result = RunWorkflow(new Dictionary<string, string>());
            var logFile = GetSingleLogFile("*.csv", result);

            var below = AssertSessionFolders(logFile, result);
            Assert.AreEqual("BonsaiData", below[0], $"Expected the BonsaiData folder.{result.Describe()}");
            StringAssert.StartsWith(Path.GetFileName(logFile), "BonsaiData_Async_", result.Describe());

            var rows = ReadCsvLog(logFile, ExpectedHeader, Count, result);
            var previous = double.NegativeInfinity;
            for (var row = 0; row < Count; row++)
            {
                var seconds = ParseDoubleColumn(rows[row], 0, ExpectedHeader, row, result);
                Assert.IsTrue(seconds > previous, $"Expected rising Seconds in row {row}.{result.Describe()}");
                previous = seconds;
                Assert.AreEqual("RewardLeft", rows[row][1], result.Describe());
                // A comma in Detail would split the row, so it is written as a semicolon.
                Assert.AreEqual("Manual; test", rows[row][2], result.Describe());
                Assert.AreEqual(row, ParseIntegerColumn(rows[row], 3, ExpectedHeader, row, result), result.Describe());
            }
        }
    }

    /// <summary>
    /// CreateAsyncEvent and AsyncEvent without Bonsai: what each kind of element becomes.
    /// </summary>
    [TestClass]
    public class AsyncEventTests
    {
        static AsyncEvent Make<T>(T element, string detail = null)
        {
            var create = new CreateAsyncEvent { Event = "Flush", Detail = detail };
            return create.Process(Observable.Return(element)).Wait();
        }

        [TestMethod]
        public void Numbers_AreTheValue()
        {
            Assert.AreEqual(2.5, Make(2.5).Value);
            Assert.AreEqual(7.0, Make(7).Value);
            Assert.AreEqual(3.0, Make(3f).Value);
            Assert.AreEqual(9.0, Make(9L).Value);
        }

        [TestMethod]
        public void Booleans_AreOneOrZero()
        {
            Assert.AreEqual(1.0, Make(true).Value);
            Assert.AreEqual(0.0, Make(false).Value);
        }

        [TestMethod]
        public void Text_IsTheDetailWhenDetailIsEmpty_AndHasNoValue()
        {
            var e = Make("Left, 3 pulses");
            Assert.AreEqual("Left; 3 pulses", e.Detail);
            Assert.IsNull(e.Value);
            Assert.AreEqual("Fixed", Make("ignored", "Fixed").Detail);
        }

        [TestMethod]
        public void OtherElements_HaveNoValue_AndNaNIsNone()
        {
            Assert.IsNull(Make(new object()).Value);
            Assert.IsNull(Make(double.NaN).Value);
            Assert.AreEqual(string.Empty, Make(new object()).Detail);
        }

        [TestMethod]
        public void Records_FillEventDetailAndValueFromTheirFields()
        {
            var e = Make(new { Event = "Go", Detail = "a, b", Value = 3 });
            Assert.AreEqual("Go", e.Event);
            Assert.AreEqual("a; b", e.Detail);
            Assert.AreEqual(3.0, e.Value);

            // The properties fill what the record lacks; a record without Value carries none.
            var partial = Make(new { Detail = "Left" });
            Assert.AreEqual("Flush", partial.Event);
            Assert.AreEqual("Left", partial.Detail);
            Assert.IsNull(partial.Value);
            Assert.IsNull(Make(new { Event = "X", Value = (double?)null }).Value);
        }

        [TestMethod]
        public void LineBreaksAndCommas_NeverSplitTheRow()
        {
            var e = new AsyncEvent("A,B", "x\r\ny", 1);
            Assert.AreEqual("A;B", e.Event);
            Assert.IsFalse(e.Detail.Contains("\n") || e.Detail.Contains("\r"));
            Assert.AreEqual("A;B,x  y,1", e.ToString());
        }
    }

    /// <summary>
    /// What CsvWriter's AutoFlush and PublishRows do to each row, without Bonsai: LoggedRows.WriteRow
    /// is what the write CsvWriter builds calls when either is on.
    /// </summary>
    [TestClass]
    public class LoggedRowsTests
    {
        string session;

        [TestInitialize]
        public void MakeSession()
        {
            session = Path.Combine(Path.GetTempPath(), "OpenRigs.Logging.Tests", Guid.NewGuid().ToString("N"),
                "sub-test", "ses-001D1_2026-11-05T08-53-26");
            Directory.CreateDirectory(Path.Combine(session, "BonsaiData"));
        }

        [TestCleanup]
        public void RemoveSession()
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(session)), true); }
            catch (IOException) { }
        }

        string LogFile(string log, string hour)
        {
            return Path.Combine(session, "BonsaiData", log + "_" + hour + ".csv");
        }

        // The file as another reader sees it while the writer still holds it open.
        static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        [TestMethod]
        public void AutoFlush_PutsEachRowOnDiskBeforeTheFileCloses()
        {
            var path = LogFile("BonsaiData_Trial", "2026-11-05T08-00-00");
            using (var writer = new StreamWriter(path))
            {
                LoggedRows.WriteRow(writer, "1.5,Correct", "Seconds,response", flush: true, publish: false);
                Assert.AreEqual("1.5,Correct" + Environment.NewLine, ReadShared(path));
            }
        }

        [TestMethod]
        public void WithoutAutoFlush_ARowWaitsInTheBuffer()
        {
            // What the trial log did before: a reader saw nothing until 4 KB had built up or the file closed.
            var path = LogFile("BonsaiData_Trial", "2026-11-05T08-00-00");
            using (var writer = new StreamWriter(path))
            {
                LoggedRows.WriteRow(writer, "1.5,Correct", "Seconds,response", flush: false, publish: false);
                Assert.AreEqual(string.Empty, ReadShared(path));
            }
        }

        [TestMethod]
        public void PublishRows_NamesTheSessionAndLog_AndNumbersRowsAcrossHourlyFiles()
        {
            var rows = new List<LoggedRow>();
            using (new LoggedRows().Generate().Subscribe(rows.Add))
            {
                using (var first = new StreamWriter(LogFile("BonsaiData_Trial", "2026-11-05T08-00-00")))
                {
                    LoggedRows.WriteRow(first, "1,Correct", "Seconds,response", true, true);
                    LoggedRows.WriteRow(first, "2,Incorrect", "Seconds,response", true, true);
                }
                using (var async = new StreamWriter(LogFile("BonsaiData_Async", "2026-11-05T08-00-00")))
                {
                    LoggedRows.WriteRow(async, "1.2,Go,,", "Seconds,Event,Detail,Value", true, true);
                }
                using (var second = new StreamWriter(LogFile("BonsaiData_Trial", "2026-11-05T09-00-00")))
                {
                    LoggedRows.WriteRow(second, "3,NoResponse", "Seconds,response", true, true);
                }
                // A log with PublishRows off is written and not published.
                using (var quiet = new StreamWriter(LogFile("BonsaiData_Other", "2026-11-05T08-00-00")))
                {
                    LoggedRows.WriteRow(quiet, "4,x", "Seconds,a", true, false);
                }
            }

            Assert.AreEqual(4, rows.Count);
            Assert.IsTrue(rows.All(r => r.Session == "sub-test/ses-001D1_2026-11-05T08-53-26"), rows[0].Session);
            CollectionAssert.AreEqual(new[] { "BonsaiData_Trial", "BonsaiData_Trial", "BonsaiData_Async", "BonsaiData_Trial" }, rows.Select(r => r.Log).ToArray());
            // The trial log's rows go on from 0 to 2 across its two hourly files; the async log counts its own.
            CollectionAssert.AreEqual(new long[] { 0, 1, 0, 2 }, rows.Select(r => r.Row).ToArray());
            Assert.AreEqual("3,NoResponse", rows[3].Line);
            Assert.AreEqual("Seconds,Event,Detail,Value", rows[2].Header);
        }

        [TestMethod]
        public void LogName_IsTheFileNameBeforeItsHour()
        {
            Assert.AreEqual("BonsaiData_Trial", LoggedRows.LogName(@"C:\s\BonsaiData\BonsaiData_Trial_2026-11-05T08-00-00.csv"));
            Assert.AreEqual("Data", LoggedRows.LogName(@"C:\s\Data\Data.csv"));
        }
    }
}
