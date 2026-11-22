using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Subjects;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    /// <summary>A follower's heartbeats against the rig's clock (Timestamp): agreeing, free-running from power-on, drifting after Go.</summary>
    [TestClass]
    public class HarpClockAgreesTests
    {
        const double Now = 3.8e9;  // 2024 in Harp seconds

        static HarpMessage Beat(double seconds) => HarpMessage.FromUInt32(TimestampSeconds.Address, seconds, MessageType.Event, (uint)seconds);

        // Each step a (clock time, follower heartbeat) pair, Go sent before step goAt (never if -1); returns what passed,
        // the error, if any, and what was written to Console.Error.
        static Tuple<List<double>, Exception, string> Run(int goAt, params double[][] steps)
        {
            var device = new Subject<HarpMessage>();
            var clock = new Subject<double>();
            var go = new Subject<Unit>();
            var passed = new List<double>();
            Exception error = null;
            var written = new StringWriter();
            var console = Console.Error;
            Console.SetError(written);
            try
            {
                using (new HarpClockAgrees { Name = "BehaviorBoard-Main" }.Process(device, clock, go).Subscribe(passed.Add, e => error = e))
                {
                    for (var i = 0; i < steps.Length; i++)
                    {
                        if (i == goAt) go.OnNext(Unit.Default);
                        clock.OnNext(steps[i][0]);
                        device.OnNext(Beat(steps[i][1]));
                    }
                }
            }
            finally { Console.SetError(console); }
            return Tuple.Create(passed, error, written.ToString());
        }

        static readonly double[][] Drift = { new[] { Now, Now }, new[] { Now + 1, Now + 1 }, new[] { Now + 2, Now + 2 },
            new[] { Now + 3, Now + 3.6 }, new[] { Now + 4, Now + 4.7 }, new[] { Now + 5, Now + 5.8 }, new[] { Now + 6, Now + 6.9 },
            new[] { Now + 7, Now + 7 }, new[] { Now + 8, Now + 8 }, new[] { Now + 9, Now + 9 } };

        [TestMethod]
        public void Agreeing_PassesFromTheThirdBeat()
        {
            var run = Run(-1, new[] { Now, Now + 0.01 }, new[] { Now + 1, Now + 1 }, new[] { Now + 2, Now + 1.6 }, new[] { Now + 3, Now + 3 });
            Assert.IsNull(run.Item2);
            CollectionAssert.AreEqual(new[] { Now + 1.6, Now + 3 }, run.Item1);
        }

        [TestMethod]
        public void FreeRunningBeforeGo_StopsNamingTheDeviceAndCable()
        {
            var run = Run(-1, new[] { Now, 12.0 }, new[] { Now + 1, 13.0 }, new[] { Now + 2, 14.0 });
            Assert.AreEqual(0, run.Item1.Count);
            StringAssert.StartsWith(run.Item2.Message, "BehaviorBoard-Main's Harp clock is -3799999988 s from the rig's clock");
            StringAssert.Contains(run.Item2.Message, "check its clock-sync cable");
        }

        // The clock set between two beats: one beat off, then agreeing again, is no fault.
        [TestMethod]
        public void OneBeatOffAtTheClockSet_IsNoFault()
        {
            var run = Run(-1, new[] { 100.0, 100 }, new[] { Now, 101 }, new[] { Now + 1, Now + 1 }, new[] { Now + 2, Now + 2 }, new[] { Now + 3, Now + 3 });
            Assert.IsNull(run.Item2);
            CollectionAssert.AreEqual(new[] { Now + 3 }, run.Item1);
        }

        [TestMethod]
        public void DriftBeforeGo_Stops()
        {
            var run = Run(-1, Drift);
            CollectionAssert.AreEqual(new[] { Now + 2 }, run.Item1);
            StringAssert.StartsWith(run.Item2.Message, "BehaviorBoard-Main's Harp clock is 0.8 s from the rig's clock");
        }

        // P1R-H1 (audit 22 Nov 2026): after Go a session already recording is worth more than its clock, so a
        // drift warns once each time the device falls out and never stops the workflow.
        [TestMethod]
        public void DriftAfterGo_WarnsOnceAndGoesOn()
        {
            var run = Run(1, Drift);
            Assert.IsNull(run.Item2);
            CollectionAssert.AreEqual(new[] { Now + 2, Now + 9 }, run.Item1);
            StringAssert.StartsWith(run.Item3, "HarpClockAgrees: warning: BehaviorBoard-Main's Harp clock is 0.8 s from the rig's clock");
            StringAssert.Contains(run.Item3, "check its clock-sync cable");
            Assert.AreEqual(1, run.Item3.Split(new[] { "warning:" }, StringSplitOptions.None).Length - 1, run.Item3);
        }

        [TestMethod]
        public void BeatsBeforeTheClock_AreNotJudged()
        {
            var device = new Subject<HarpMessage>();
            var passed = new List<double>();
            using (new HarpClockAgrees { Name = "LicketySplit-Left" }.Process(device, new Subject<double>(), new Subject<Unit>()).Subscribe(passed.Add))
            {
                for (var i = 0; i < 5; i++) device.OnNext(Beat(i));
            }
            Assert.AreEqual(0, passed.Count);
        }
    }
}
