using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    /// <summary>A follower's heartbeats against the rig's clock (Timestamp): agreeing, free-running from power-on, drifting mid-run.</summary>
    [TestClass]
    public class HarpClockAgreesTests
    {
        const double Now = 3.8e9;  // 2024 in Harp seconds

        static HarpMessage Beat(double seconds) => HarpMessage.FromUInt32(TimestampSeconds.Address, seconds, MessageType.Event, (uint)seconds);

        // Each step a (clock time, follower heartbeat) pair; returns what passed and the error, if any.
        static Tuple<List<double>, Exception> Run(params double[][] steps)
        {
            var device = new Subject<HarpMessage>();
            var clock = new Subject<double>();
            var passed = new List<double>();
            Exception error = null;
            using (new HarpClockAgrees { Name = "BehaviorBoard-Main" }.Process(device, clock).Subscribe(passed.Add, e => error = e))
            {
                foreach (var step in steps)
                {
                    clock.OnNext(step[0]);
                    device.OnNext(Beat(step[1]));
                }
            }
            return Tuple.Create(passed, error);
        }

        [TestMethod]
        public void Agreeing_PassesFromTheThirdBeat()
        {
            var run = Run(new[] { Now, Now + 0.01 }, new[] { Now + 1, Now + 1 }, new[] { Now + 2, Now + 1.6 }, new[] { Now + 3, Now + 3 });
            Assert.IsNull(run.Item2);
            CollectionAssert.AreEqual(new[] { Now + 1.6, Now + 3 }, run.Item1);
        }

        [TestMethod]
        public void FreeRunningFromPowerOn_StopsNamingTheDeviceAndCable()
        {
            var run = Run(new[] { Now, 12.0 }, new[] { Now + 1, 13.0 }, new[] { Now + 2, 14.0 });
            Assert.AreEqual(0, run.Item1.Count);
            StringAssert.StartsWith(run.Item2.Message, "BehaviorBoard-Main's Harp clock is -3799999988 s from the rig's clock");
            StringAssert.Contains(run.Item2.Message, "check its clock-sync cable");
        }

        // The clock set between two beats: one beat off, then agreeing again, is no fault.
        [TestMethod]
        public void OneBeatOffAtTheClockSet_IsNoFault()
        {
            var run = Run(new[] { 100.0, 100 }, new[] { Now, 101 }, new[] { Now + 1, Now + 1 }, new[] { Now + 2, Now + 2 }, new[] { Now + 3, Now + 3 });
            Assert.IsNull(run.Item2);
            CollectionAssert.AreEqual(new[] { Now + 3 }, run.Item1);
        }

        [TestMethod]
        public void DriftPastToleranceMidRun_Stops()
        {
            var run = Run(new[] { Now, Now }, new[] { Now + 1, Now + 1 }, new[] { Now + 2, Now + 2 },
                new[] { Now + 3, Now + 3.6 }, new[] { Now + 4, Now + 4.7 }, new[] { Now + 5, Now + 5.8 });
            CollectionAssert.AreEqual(new[] { Now + 2 }, run.Item1);
            StringAssert.StartsWith(run.Item2.Message, "BehaviorBoard-Main's Harp clock is 0.8 s from the rig's clock");
        }

        [TestMethod]
        public void BeatsBeforeTheClock_AreNotJudged()
        {
            var device = new Subject<HarpMessage>();
            var passed = new List<double>();
            using (new HarpClockAgrees { Name = "LicketySplit-Left" }.Process(device, new Subject<double>()).Subscribe(passed.Add))
            {
                for (var i = 0; i < 5; i++) device.OnNext(Beat(i));
            }
            Assert.AreEqual(0, passed.Count);
        }
    }
}
