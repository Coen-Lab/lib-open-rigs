using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Logging.Tests
{
    /// <summary>
    /// CsvWriter.ColumnNames gives the header's columns for an element type without writing, so
    /// check-workflow-builds can hold BonsaiData_Trial to lab_names.yml.
    /// </summary>
    [TestClass]
    public class ColumnNamesTests
    {
        public class Trial
        {
            public int trialNumber { get; set; }
            public string outcome { get; set; }
            public double? rewardSize { get; set; }
            public Inner stimulus { get; set; }
        }

        public class Inner
        {
            public double azimuth { get; set; }
        }

        [TestMethod]
        public void ColumnNames_NamesEveryMemberByItsPath()
        {
            CollectionAssert.AreEqual(
                new[] { "trialNumber", "outcome", "rewardSize", "stimulus.azimuth" },
                CsvWriter.ColumnNames(typeof(Trial)));
        }

        [TestMethod]
        public void ColumnNames_TimestampedPutsSecondsFirstWithoutValue()
        {
            var stamped = typeof(Bonsai.Harp.Timestamped<>).MakeGenericType(typeof(Trial));
            CollectionAssert.AreEqual(
                new[] { "Seconds", "trialNumber", "outcome", "rewardSize", "stimulus.azimuth" },
                CsvWriter.ColumnNames(stamped));
        }

        [TestMethod]
        public void ColumnNames_FollowsTheSelector()
        {
            CollectionAssert.AreEqual(new[] { "outcome" }, CsvWriter.ColumnNames(typeof(Trial), "outcome"));
        }
    }
}
