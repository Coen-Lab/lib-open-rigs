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
    /// LogTrials writes a trial record into WorkflowData_Trials with every field as a column, the way
    /// LogData does with an empty Selector.
    /// </summary>
    [TestClass]
    public class LogTrialsTests : LoggingTestBase
    {
        const int Count = 5;
        static readonly string[] ExpectedHeader = { "harpTime", "Label", "Count", "IsEven", "Maybe" };

        protected override string WorkflowFileName => "LogTrialsTest.bonsai";

        [TestMethod]
        public void LogTrials_WritesOneRowPerTrialIntoWorkflowDataTrials()
        {
            var result = RunWorkflow(new Dictionary<string, string>
            {
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
            var logFile = GetSingleLogFile("*.csv", result);

            var below = AssertSessionFolders(logFile, result);
            // FormatFileName names the folder by the log name up to its first underscore, so both files share WorkflowData.
            Assert.AreEqual("WorkflowData", below[0], $"Expected the WorkflowData folder.{result.Describe()}");
            // One file for the experiment, named by the experiment folder's start time.
            var session = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(logFile)));
            Assert.AreEqual("WorkflowData_Trials_" + session.Substring(session.LastIndexOf('_') + 1) + ".csv", Path.GetFileName(logFile), result.Describe());

            var rows = ReadCsvLog(logFile, ExpectedHeader, Count, result);
            for (var trial = 0; trial < Count; trial++)
            {
                Assert.AreEqual(trial.ToString(CultureInfo.InvariantCulture), rows[trial][2], $"Unexpected Count in row {trial}.{result.Describe()}");
            }
        }
    }

    /// <summary>
    /// LogEvents declares Events and writes every event sent to it into WorkflowData_Events, as
    /// harpTime,Event,Detail,Value.
    /// </summary>
    [TestClass]
    public class LogEventsTests : LoggingTestBase
    {
        const int Count = 4;
        static readonly string[] ExpectedHeader = { "harpTime", "Event", "Detail", "Value" };

        protected override string WorkflowFileName => "LogEventsTest.bonsai";

        [TestMethod]
        public void LogEvents_WritesEveryEventIntoWorkflowDataEvents()
        {
            var result = RunWorkflow(new Dictionary<string, string>());
            var logFile = GetSingleLogFile("*.csv", result);

            var below = AssertSessionFolders(logFile, result);
            Assert.AreEqual("WorkflowData", below[0], $"Expected the WorkflowData folder.{result.Describe()}");
            StringAssert.StartsWith(Path.GetFileName(logFile), "WorkflowData_Events_", result.Describe());

            var rows = ReadCsvLog(logFile, ExpectedHeader, Count, result);
            var previous = double.NegativeInfinity;
            for (var row = 0; row < Count; row++)
            {
                var seconds = ParseDoubleColumn(rows[row], 0, ExpectedHeader, row, result);
                Assert.IsTrue(seconds > previous, $"Expected rising harpTime in row {row}.{result.Describe()}");
                previous = seconds;
                Assert.AreEqual("RewardLeft", rows[row][1], result.Describe());
                // A comma in Detail would split the row, so it is written as a semicolon.
                Assert.AreEqual("Manual; test", rows[row][2], result.Describe());
                Assert.AreEqual(row, ParseIntegerColumn(rows[row], 3, ExpectedHeader, row, result), result.Describe());
            }
        }
    }

    /// <summary>
    /// LogRngSeed logs only the first seed it is given, as the RngSeed row, and leaves Events
    /// open: a Take(1) before its multicast would close Events and lose every later row.
    /// </summary>
    [TestClass]
    public class LogRngSeedTests : LoggingTestBase
    {
        static readonly string[] ExpectedHeader = { "harpTime", "Event", "Detail", "Value" };

        protected override string WorkflowFileName => "LogRngSeedTest.bonsai";

        [TestMethod]
        public void LogRngSeed_LogsTheFirstSeedOnce_AndLaterEventsStillArrive()
        {
            var result = RunWorkflow(new Dictionary<string, string>());
            var rows = ReadCsvLog(GetSingleLogFile("*.csv", result), ExpectedHeader, 4, result);
            Assert.AreEqual("RngSeed", rows[0][1], result.Describe());
            Assert.AreEqual(0, ParseIntegerColumn(rows[0], 3, ExpectedHeader, 0, result), result.Describe());
            CollectionAssert.AreEqual(new[] { "Later", "Later", "Later" }, rows.Skip(1).Select(r => r[1]).ToArray(), result.Describe());
        }
    }

    /// <summary>
    /// TaskLogs keeps logging trials after Settings_Task is written: its input (the task file at Go) sends
    /// once and completes, and an include whose output completes disposes its side branches, so a
    /// WorkflowOutput fed by Settings_Task ended LogTrials and Zapit's logs before trial 1.
    /// </summary>
    [TestClass]
    public class TaskLogsTests : LoggingTestBase
    {
        const int Count = 10;

        protected override string WorkflowFileName => "TaskLogsTest.bonsai";

        [TestMethod]
        public void TaskLogs_LogsEveryTrial_AfterSettingsTaskCompletes()
        {
            var result = RunWorkflow(new Dictionary<string, string> { { "Count", Count.ToString(CultureInfo.InvariantCulture) } });
            Assert.AreEqual(1, FindLogFiles("Settings_Task_*.json", result).Length, result.Describe());
            ReadCsvLog(GetSingleLogFile("WorkflowData_Trials_*.csv", result), new[] { "harpTime", "Trial" }, Count, result);
        }
    }
    /// <summary>
    /// ConditionedTaskLogs: TaskLogs' logs and Settings_Combinations from TrialRules' CombinationsJson, once.
    /// </summary>
    [TestClass]
    public class ConditionedTaskLogsTests : LoggingTestBase
    {
        protected override string WorkflowFileName => "ConditionedTaskLogsTest.bonsai";

        [TestMethod]
        public void ConditionedTaskLogs_WritesSettingsCombinationsBesideTaskLogs()
        {
            var result = RunWorkflow(new Dictionary<string, string> { { "Count", "10" } });
            Assert.AreEqual(1, FindLogFiles("Settings_Task_*.json", result).Length, result.Describe());
            var combinations = FindLogFiles("Settings_Combinations_*.json", result);
            Assert.AreEqual(1, combinations.Length, result.Describe());
            Assert.AreEqual("[0]", System.Text.RegularExpressions.Regex.Replace(System.IO.File.ReadAllText(combinations[0]), @"\s", ""), result.Describe());
            ReadCsvLog(GetSingleLogFile("WorkflowData_Trials_*.csv", result), new[] { "harpTime", "Trial" }, 10, result);
        }
    }

    /// <summary>TaskLogsZapitDaq: TaskLogs with ZapitDaq's events (Zapit.Events) in place of ZapitLogs.</summary>
    [TestClass]
    public class TaskLogsZapitDaqTests : TaskLogsTests
    {
        protected override string WorkflowFileName => "TaskLogsZapitDaqTest.bonsai";
    }

    /// <summary>ConditionedTaskLogsZapitDaq: ConditionedTaskLogs on TaskLogsZapitDaq.</summary>
    [TestClass]
    public class ConditionedTaskLogsZapitDaqTests : ConditionedTaskLogsTests
    {
        protected override string WorkflowFileName => "ConditionedTaskLogsZapitDaqTest.bonsai";
    }

    /// <summary>
    /// CreateEvent and EventRow without Bonsai: what each kind of element becomes.
    /// </summary>
    [TestClass]
    public class EventRowTests
    {
        static EventRow Make<T>(T element, string detail = null)
        {
            var create = new CreateEvent { Event = "Flush", Detail = detail };
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
            var e = new EventRow("A,B", "x\r\ny", 1);
            Assert.AreEqual("A;B", e.Event);
            Assert.IsFalse(e.Detail.Contains("\n") || e.Detail.Contains("\r"));
            Assert.AreEqual("A;B,x  y,1", e.ToString());
        }

        [TestMethod]
        public void AZapitReply_IsAZapitReplyRow_ItsResultTheValue()
        {
            // ZapitLogs' row: everything the reply carries, in one Events row.
            var reply = new OpenRigs.Devices.Zapit.ZapitReply
            {
                Command = OpenRigs.Devices.Zapit.ZapitCommandType.StartStimulus, Value = 3, LaserOn = 1, ServerDatenum = 739000.5
            };
            var e = new CreateEvent { Event = "ZapitReply" }.Process(Observable.Return(reply)).Wait();
            Assert.AreEqual("ZapitReply", e.Event);
            Assert.AreEqual(3.0, e.Value);
            Assert.AreEqual("StartStimulus laserOn=1 serverDatenum=739000.5", e.Detail);
            reply.IsError = true;
            Assert.AreEqual("StartStimulus laserOn=1 serverDatenum=739000.5 error", reply.Detail);
        }
    }

    /// <summary>
    /// What CsvWriter's AutoFlush and PublishRows do to each row, without Bonsai: LoggedRows.WriteRow
    /// is what the write CsvWriter builds calls when either is on.
    /// </summary>
    [TestClass]
    public class LoggedRowsTests
    {
        string experiment;

        [TestInitialize]
        public void MakeSession()
        {
            experiment = Path.Combine(Path.GetTempPath(), "OpenRigs.Logging.Tests", Guid.NewGuid().ToString("N"),
                "sub-test", "ses-001D1_2026-11-05T08-53-26");
            Directory.CreateDirectory(Path.Combine(experiment, "WorkflowData"));
        }

        [TestCleanup]
        public void RemoveSession()
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(experiment)), true); }
            catch (IOException) { }
        }

        // FormatFileName's layout: <log up to its first _>\<log>_<time>.csv.
        string LogFile(string log, string time)
        {
            var folder = Directory.CreateDirectory(Path.Combine(experiment, log.Split('_')[0])).FullName;
            return Path.Combine(folder, log + "_" + time + ".csv");
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
            var path = LogFile("WorkflowData_Trials", "2026-11-05T08-00-00");
            using (var writer = new StreamWriter(path))
            {
                LoggedRows.WriteRow(writer, "1.5,Correct", "harpTime,response", flush: true, publish: false);
                Assert.AreEqual("1.5,Correct" + Environment.NewLine, ReadShared(path));
            }
        }

        [TestMethod]
        public void WithoutAutoFlush_ARowWaitsInTheBuffer()
        {
            // What the trial log did before: a reader saw nothing until 4 KB had built up or the file closed.
            var path = LogFile("WorkflowData_Trials", "2026-11-05T08-00-00");
            using (var writer = new StreamWriter(path))
            {
                LoggedRows.WriteRow(writer, "1.5,Correct", "harpTime,response", flush: false, publish: false);
                Assert.AreEqual(string.Empty, ReadShared(path));
            }
        }

        [TestMethod]
        public void PublishRows_NamesTheSessionAndLog_AndNumbersRowsAcrossALogsFiles()
        {
            var rows = new List<LoggedRow>();
            using (new LoggedRows().Generate().Subscribe(rows.Add))
            {
                // An hourly log (LogData) with PublishRows on, in two files.
                using (var first = new StreamWriter(LogFile("Wheel_Rows", "2026-11-05T08-00-00")))
                {
                    LoggedRows.WriteRow(first, "1,Correct", "harpTime,response", true, true);
                    LoggedRows.WriteRow(first, "2,Incorrect", "harpTime,response", true, true);
                }
                using (var events = new StreamWriter(LogFile("WorkflowData_Events", "2026-11-05T08-53-26")))
                {
                    LoggedRows.WriteRow(events, "1.2,Go,,", "harpTime,Event,Detail,Value", true, true);
                }
                using (var second = new StreamWriter(LogFile("Wheel_Rows", "2026-11-05T09-00-00")))
                {
                    LoggedRows.WriteRow(second, "3,noResponse", "harpTime,response", true, true);
                }
                // A log with PublishRows off is written and not published.
                using (var quiet = new StreamWriter(LogFile("WorkflowData_Other", "2026-11-05T08-00-00")))
                {
                    LoggedRows.WriteRow(quiet, "4,x", "harpTime,a", true, false);
                }
            }

            Assert.AreEqual(4, rows.Count);
            Assert.IsTrue(rows.All(r => r.Session == "sub-test/ses-001D1_2026-11-05T08-53-26"), rows[0].Session);
            CollectionAssert.AreEqual(new[] { "Wheel_Rows", "Wheel_Rows", "WorkflowData_Events", "Wheel_Rows" }, rows.Select(r => r.Log).ToArray());
            // The hourly log's rows go on from 0 to 2 across its two files; the events log counts its own.
            CollectionAssert.AreEqual(new long[] { 0, 1, 0, 2 }, rows.Select(r => r.Row).ToArray());
            Assert.AreEqual("3,noResponse", rows[3].Line);
            Assert.AreEqual("harpTime,Event,Detail,Value", rows[2].Header);
        }

        [TestMethod]
        public void LogName_IsTheFileNameBeforeItsTime()
        {
            Assert.AreEqual("WorkflowData_Trials", LoggedRows.LogName(@"C:\s\WorkflowData\WorkflowData_Trials_2026-11-05T08-00-00.csv"));
            Assert.AreEqual("Data", LoggedRows.LogName(@"C:\s\Data\Data.csv"));
        }
    }

    /// <summary>
    /// LogTrials' and LogEvents' AutoFlush and PublishRows reach the CsvWriter only through two
    /// ExternalizedMappings in LogWorkflowData: a run reads the CSV after it has
    /// closed, so a dropped mapping would pass every other test while each row waited in the buffer.
    /// </summary>
    [TestClass]
    public class LogTrialsEventsMappingTests
    {
        static readonly System.Xml.Linq.XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        static readonly string[] Settings = { "AutoFlush", "PublishRows" };

        static System.Xml.Linq.XDocument Embedded(string name)
        {
            using var stream = typeof(LoggedRows).Assembly.GetManifestResourceStream($"OpenRigs.Logging.{name}");
            Assert.IsNotNull(stream, $"Missing embedded resource '{name}'.");
            return System.Xml.Linq.XDocument.Load(stream);
        }

        // Each Expression of a Workflow element's Nodes, by index, and its Edges as (from, to).
        static (List<System.Xml.Linq.XElement> Nodes, List<(int From, int To)> Edges) Graph(System.Xml.Linq.XElement workflow)
        {
            var ns = workflow.Name.Namespace;
            var nodes = workflow.Element(ns + "Nodes").Elements(ns + "Expression").ToList();
            var edges = workflow.Element(ns + "Edges").Elements(ns + "Edge")
                .Select(e => ((int)e.Attribute("From"), (int)e.Attribute("To"))).ToList();
            return (nodes, edges);
        }

        static string Type(System.Xml.Linq.XElement node) => (string)node.Attribute(Xsi + "type");

        // The ExternalizedMapping naming both settings is wired into the node the predicate picks.
        static void AssertMappedInto(System.Xml.Linq.XElement workflow, Func<System.Xml.Linq.XElement, bool> target, string where)
        {
            var (nodes, edges) = Graph(workflow);
            var mapping = nodes.FindIndex(n => Type(n) == "ExternalizedMapping" &&
                Settings.All(s => n.Elements().Any(p => p.Name.LocalName == "Property" && (string)p.Attribute("Name") == s)));
            Assert.IsTrue(mapping >= 0, $"{where}: no ExternalizedMapping names AutoFlush and PublishRows.");
            var into = edges.Where(e => e.From == mapping).Select(e => nodes[e.To]).ToList();
            Assert.IsTrue(into.Any(target), $"{where}: the mapping of AutoFlush and PublishRows is wired into {string.Join(", ", into.Select(Type))}.");
        }

        [DataTestMethod]
        [DataRow("LogTrials.bonsai")]
        [DataRow("LogEvents.bonsai")]
        public void AutoFlushAndPublishRows_AreOnInTheInclude(string file)
        {
            var include = Embedded(file).Descendants().Single(e => Type(e) == "IncludeWorkflow" && (string)e.Attribute("Path") == "OpenRigs.Logging:LogWorkflowData.bonsai");
            foreach (var setting in Settings)
                Assert.AreEqual("true", (string)include.Elements().SingleOrDefault(e => e.Name.LocalName == setting), $"{file}'s {setting}.");
        }

        [DataTestMethod]
        [DataRow("LogData.bonsai")]
        [DataRow("LogWorkflowData.bonsai")]
        public void MapsAutoFlushAndPublishRows_OntoItsCsvWriter(string file)
        {
            var outer = Embedded(file).Root.Elements().Single(e => e.Name.LocalName == "Workflow");
            var create = outer.Descendants().First(e => Type(e) == "rx:CreateObservable");
            AssertMappedInto(outer, n => n == create, file);
            var inner = create.Elements().Single(e => e.Name.LocalName == "Workflow");
            AssertMappedInto(inner, n => Type(n) == "orl:CsvWriter", file + "'s CreateObservable");
        }
    }

    /// <summary>
    /// GroupByExperiment without Bonsai: every row of an experiment, over any number of hours, in
    /// one group keyed by the experiment folder's start time.
    /// </summary>
    [TestClass]
    public class GroupByExperimentTests
    {
        [TestMethod]
        public void RowsOverSeveralHours_AreOneGroup_KeyedByTheFolderStart()
        {
            var hour = 3600.0;
            var rows = new[] { 1.0, hour + 1, 2 * hour + 1 }.Select(s => Bonsai.Harp.Timestamped.Create(s.ToString(CultureInfo.InvariantCulture), s));
            var keyed = new GroupByExperiment()
                .Process(rows.ToObservable(), Observable.Return(@"D:\Data\sub-AM009\ses-01201_2026-11-16T04-14-17"))
                .SelectMany((group, index) => group.Select(row => Tuple.Create(index, group.Key)))
                .ToList().Wait();
            Assert.AreEqual(3, keyed.Count);
            Assert.IsTrue(keyed.All(k => k.Item1 == 0 && k.Item2 == new DateTime(2026, 11, 16, 4, 14, 17)));
        }

        [TestMethod]
        public void AFolderWithoutAStart_IsRefusedByName()
        {
            var error = Assert.ThrowsException<InvalidOperationException>(() => GroupByExperiment.Start(@"D:\Data\sub-x\scratch"));
            StringAssert.Contains(error.Message, "'scratch'");
        }
    }
}
