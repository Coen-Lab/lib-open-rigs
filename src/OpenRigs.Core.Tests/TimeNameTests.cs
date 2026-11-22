using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    /// <summary>
    /// The time in data names (FormatDate) and the chunks GroupByTime keys by it (specs/logging.md, sections 2 and 3).
    /// </summary>
    [TestClass]
    public class TimeNameTests
    {
        static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        static readonly TimeZoneInfo Arizona = TimeZoneInfo.FindSystemTimeZoneById("US Mountain Standard Time");

        static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        static double HarpSeconds(DateTime utc) => (utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        [DataTestMethod]
        [DataRow("2026-11-20T14:32:56.789Z", "GMT Standard Time", "2026-11-20T14-32-56+00-00", DisplayName = "winter, the fraction cut")]
        [DataRow("2026-07-20T14:32:56Z", "GMT Standard Time", "2026-07-20T15-32-56+01-00", DisplayName = "summer")]
        [DataRow("2026-10-25T00:30:00Z", "GMT Standard Time", "2026-10-25T01-30-00+01-00", DisplayName = "October's repeated hour, first")]
        [DataRow("2026-10-25T01:30:00Z", "GMT Standard Time", "2026-10-25T01-30-00+00-00", DisplayName = "October's repeated hour, second")]
        [DataRow("2026-11-20T14:32:56Z", "US Mountain Standard Time", "2026-11-20T07-32-56-07-00", DisplayName = "a negative offset")]
        public void Format_IsLocalTimeAndItsOffset_AndParsesBack(string utc, string zone, string token)
        {
            var instant = Utc(utc);
            Assert.AreEqual(token, FormatDate.Format(instant, TimeZoneInfo.FindSystemTimeZoneById(zone)));
            Assert.AreEqual(instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerSecond)), FormatDate.Parse(token));
            Assert.AreEqual(DateTimeKind.Utc, FormatDate.Parse(token).Kind);
        }

        [TestMethod]
        public void Operator_UsesItsZone_AndReadsUnspecifiedAsUtc()
        {
            var unspecified = new DateTime(2026, 7, 20, 14, 32, 56, DateTimeKind.Unspecified);
            var made = new FormatDate { TimeZone = London }.Process(Observable.Return(unspecified)).Single();
            Assert.AreEqual("2026-07-20T15-32-56+01-00", made);
            Assert.AreEqual(TimeZoneInfo.Local, new FormatDate().TimeZone, "the machine's zone by default");
        }

        [TestMethod]
        public void OldNames_AreUtc_AndOtherTextIsRefused()
        {
            Assert.AreEqual(Utc("2026-11-14T10:00:00Z"), FormatDate.Parse("2026-11-14T10-00-00"));
            foreach (var bad in new[] { "2026-11-14T10:00:00", "2026-11-14T10-00-00+0100", "2026-11-14T10-00-00*01-00", "", null })
            {
                Assert.ThrowsException<FormatException>(() => FormatDate.Parse(bad), bad ?? "null");
            }
        }

        [TestMethod]
        public void FolderStart_ReadsTheExperimentFolder_OldOrNew()
        {
            Assert.AreEqual(Utc("2026-07-20T14:32:56Z"), FormatDate.FolderStart(@"D:\Data\sub-AM006\ses-004B1_2026-07-20T15-32-56+01-00\"));
            Assert.AreEqual(Utc("2026-07-20T14:32:56Z"), FormatDate.FolderStart(@"D:\Data\sub-AM006\ses-004B1_2026-07-20T14-32-56"));
            var refused = Assert.ThrowsException<InvalidOperationException>(() => FormatDate.FolderStart(@"D:\Data\sub-AM006\ses-004B1"));
            StringAssert.Contains(refused.Message, "ses-004B1");
        }

        [TestMethod]
        public void ChunkStart_CountsChunksFromTheStart_EarlySamplesInChunkZero()
        {
            var start = Utc("2026-10-25T00:30:00Z");
            Assert.AreEqual(start, GroupByTime.ChunkStart(start, HarpSeconds(start) - 10, 1), "before the start: chunk 0");
            Assert.AreEqual(start, GroupByTime.ChunkStart(start, HarpSeconds(start.AddMinutes(59)), 1));
            Assert.AreEqual(start.AddHours(1), GroupByTime.ChunkStart(start, HarpSeconds(start.AddHours(1)), 1));
            Assert.AreEqual(start.AddHours(2), GroupByTime.ChunkStart(start, HarpSeconds(start.AddHours(3.5)), 2));
            Assert.ThrowsException<InvalidOperationException>(() => GroupByTime.ChunkStart(start, 0, 0));
        }

        // The first chunk is named as the folder; one an hour later, after the October change, carries +00-00.
        [TestMethod]
        public void Chunks_AreKeyedByTheirStart_NamedWithTheOffsetAtTheirStart()
        {
            var source = new Subject<Timestamped<int>>();
            var pathPrefix = new Subject<string>();
            var keys = new List<DateTime>();
            new GroupByTime().Process(source, pathPrefix).Subscribe(g => keys.Add(g.Key));
            var folderTime = "2026-10-25T01-30-00+01-00";
            var start = FormatDate.Parse(folderTime);
            source.OnNext(Timestamped.Create(0, HarpSeconds(start) - 100));
            Assert.AreEqual(0, keys.Count, "a sample before PathPrefix is dropped");
            pathPrefix.OnNext(@"D:\Data\sub-A\ses-001_" + folderTime);
            pathPrefix.OnCompleted();
            source.OnNext(Timestamped.Create(1, HarpSeconds(start) - 0.5));
            source.OnNext(Timestamped.Create(2, HarpSeconds(start) + 10));
            source.OnNext(Timestamped.Create(3, HarpSeconds(start) + 3601));
            var names = keys.Select(k => FormatDate.Format(k, London)).ToList();
            CollectionAssert.AreEqual(new[] { folderTime, "2026-10-25T01-30-00+00-00" }, names);
        }

        [TestMethod]
        public void HeartbeatsCloseAChunkAfterItsEndPlusClosingDuration()
        {
            var source = new Subject<HarpMessage>();
            var beats = new Subject<HarpMessage>();
            var pathPrefix = new BehaviorSubject<string>(@"D:\sub-A\ses-001_2026-11-20T14-00-00+00-00");
            var closed = new List<DateTime>();
            new GroupByTime { ClosingDuration = TimeSpan.FromSeconds(5) }.Process(source, beats, pathPrefix)
                .Subscribe(g => g.Subscribe(_ => { }, () => closed.Add(g.Key)));
            var start = HarpSeconds(Utc("2026-11-20T14:00:00Z"));
            source.OnNext(HarpMessage.FromByte(44, start + 1, MessageType.Event, 1));
            beats.OnNext(HarpMessage.FromByte(44, start + 3604, MessageType.Event, 1));
            Assert.AreEqual(0, closed.Count, "still within the closing duration");
            beats.OnNext(HarpMessage.FromByte(44, start + 3606, MessageType.Event, 1));
            CollectionAssert.AreEqual(new[] { Utc("2026-11-20T14:00:00Z") }, closed);
        }
    }
}
