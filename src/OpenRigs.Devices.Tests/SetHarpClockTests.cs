using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// SetHarpClock writes UTC to the clock device until one of its heartbeats agrees, and stops the
    /// workflow, naming the device, if that never happens or the heartbeats stop mid-run.
    /// </summary>
    [TestClass]
    public class SetHarpClockTests
    {
        static readonly DateTime HarpEpoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static HarpMessage Heartbeat(double secondsFromNow)
        {
            var seconds = (DateTime.UtcNow - HarpEpoch).TotalSeconds + secondsFromNow;
            return HarpMessage.FromUInt32(TimestampSeconds.Address, seconds, MessageType.Event, (uint)seconds);
        }

        static SetHarpClock Clock(double timeout = 5, double gap = 5)
        {
            return new SetHarpClock { Name = "TimestampGeneratorGen3-Main", TimeoutSeconds = timeout, GapSeconds = gap };
        }

        [TestMethod]
        public void WritesAtStartAndEachHeartbeat_UntilOneAgreesWithUtc()
        {
            var device = new Subject<HarpMessage>();
            var writes = new List<HarpMessage>();
            Exception error = null;
            using (Clock().Process(device).Subscribe(writes.Add, e => error = e))
            {
                Assert.AreEqual(1, writes.Count, "the write at start");
                device.OnNext(Heartbeat(-3600));  // the write was lost: still counting from power-on
                Assert.AreEqual(2, writes.Count, "written again at the heartbeat");
                device.OnNext(Heartbeat(0.2));
                device.OnNext(Heartbeat(1.2));
                Assert.AreEqual(2, writes.Count, "no write once a heartbeat agrees");
            }
            Assert.IsNull(error);
            Assert.AreEqual(TimestampSeconds.Address, writes[0].Address);
            Assert.AreEqual(MessageType.Write, writes[0].MessageType);
        }

        [TestMethod]
        public void StopsNamingTheDevice_WhenTheClockIsNotSetInTime()
        {
            var device = new Subject<HarpMessage>();
            var failed = new ManualResetEventSlim();
            Exception error = null;
            using (Clock(timeout: 0.3).Process(device).Subscribe(_ => { }, e => { error = e; failed.Set(); }))
            {
                device.OnNext(Heartbeat(-3600));
                Assert.IsTrue(failed.Wait(TimeSpan.FromSeconds(5)), "no error");
            }
            StringAssert.Contains(error.Message, "TimestampGeneratorGen3-Main's Harp clock was not set within 0.3 s");
        }

        [TestMethod]
        public void StopsNamingTheDevice_WhenHeartbeatsStopMidRun()
        {
            var device = new Subject<HarpMessage>();
            var failed = new ManualResetEventSlim();
            Exception error = null;
            using (Clock(gap: 0.3).Process(device).Subscribe(_ => { }, e => { error = e; failed.Set(); }))
            {
                Thread.Sleep(500);
                Assert.IsFalse(failed.IsSet, "the gap counts only from the first heartbeat");
                device.OnNext(Heartbeat(0));
                Assert.IsTrue(failed.Wait(TimeSpan.FromSeconds(5)), "no error");
            }
            StringAssert.Contains(error.Message, "TimestampGeneratorGen3-Main's Harp clock stopped: no heartbeat for 0.3 s");
        }
    }
}
