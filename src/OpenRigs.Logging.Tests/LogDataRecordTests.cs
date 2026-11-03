using System.Collections.Generic;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Tests;

namespace OpenRigs.Logging.Tests
{
    /// <summary>
    /// Logs a timestamped record with string, integer, boolean and nullable fields through LogData
    /// with an empty Selector, the way a rig logs a flat row of trial settings.
    /// Unlike LogDataTests, which logs numeric fields through an explicit Selector, this covers
    /// header naming for string fields and how booleans and nulls are written.
    /// </summary>
    [TestClass]
    public class LogDataRecordTests : LoggingTestBase
    {
        const int Count = 4;
        static readonly string[] ExpectedHeader = { "Seconds", "Label", "Count", "IsEven", "Maybe" };

        protected override string WorkflowFileName => "LogDataRecordTest.bonsai";

        [TestMethod]
        public void LogData_TimestampedRecord_NamesEveryColumnByField()
        {
            var result = RunWorkflow(new Dictionary<string, string>
            {
                { "LogName", "Record" },
                { "Count", Count.ToString(CultureInfo.InvariantCulture) }
            });
            var logFile = GetSingleLogFile("*.csv", result);

            var rows = ReadCsvLog(logFile, ExpectedHeader, Count, result);
            for (var sample = 0; sample < Count; sample++)
            {
                var columns = rows[sample];
                var index = sample.ToString(CultureInfo.InvariantCulture);
                var isEven = sample % 2 == 0;
                ParseDoubleColumn(columns, 0, ExpectedHeader, sample, result);
                Assert.AreEqual(index, columns[1], $"Unexpected Label in sample {sample}.{result.Describe()}");
                Assert.AreEqual(index, columns[2], $"Unexpected Count in sample {sample}.{result.Describe()}");
                Assert.AreEqual(isEven.ToString(), columns[3], $"Unexpected IsEven in sample {sample}.{result.Describe()}");
                Assert.AreEqual(isEven ? index : string.Empty, columns[4], $"Unexpected Maybe in sample {sample}.{result.Describe()}");
            }
        }
    }
}
