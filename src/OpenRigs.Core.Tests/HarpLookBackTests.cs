using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class HarpLookBackTests
    {
        static HarpMessage At(double seconds) => HarpMessage.FromByte(44, seconds, MessageType.Event, 1);

        // (Harp times are in 32 us ticks.) 3 s of 1 kHz messages before the start: only the last 0.5 s (by Harp time) come out, then every
        // later one (on a rig, a camera frame's trigger is ~75 ms older than the frame).
        [TestMethod]
        public void HeldMessages_AreTheLastLookBackSecondsByHarpTime_ThenLive()
        {
            var source = new Subject<HarpMessage>();
            var start = new Subject<string>();
            var output = new List<double>();
            new HarpLookBack { LookBackSeconds = 0.5 }.Process(source, start).Subscribe(m => output.Add(m.GetTimestamp()));
            for (var i = 0; i <= 3000; i++) source.OnNext(At(100 + i * 0.001));
            Assert.AreEqual(0, output.Count, "nothing passes before the start");
            start.OnNext("path");
            source.OnNext(At(103.001));
            start.OnNext("again");
            Assert.AreEqual(102.5, output.First(), 1e-3);
            Assert.AreEqual(103.001, output.Last(), 1e-4);
            Assert.IsTrue(output.Count >= 500 && output.Count <= 503, "count " + output.Count);
            CollectionAssert.AreEqual(output.OrderBy(t => t).ToList(), output, "in order");
        }

        // The source completing ends the output (an AsyncSubject or a log's writer waits on it).
        [TestMethod]
        public void SourceCompletion_EndsTheOutput()
        {
            var source = new Subject<HarpMessage>();
            var start = new Subject<int>();
            var completed = false;
            new HarpLookBack().Process(source, start).Subscribe(_ => { }, () => completed = true);
            start.OnNext(1);
            source.OnCompleted();
            Assert.IsTrue(completed);
        }
    }
}
