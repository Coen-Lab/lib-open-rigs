using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class OnNextTickTests
    {
        [TestMethod]
        public void AValueWaitsForTheNextTickAndIsStampedWithIt()
        {
            var source = new Subject<string>();
            var clock = new Subject<double>();
            var output = new List<Timestamped<string>>();
            using (new OnNextTick().Process(source, clock).Subscribe(output.Add))
            {
                clock.OnNext(1.000);
                source.OnNext("folder");
                Assert.AreEqual(0, output.Count, "given before a tick");
                clock.OnNext(1.001);
                clock.OnNext(1.002);
                Assert.AreEqual(1, output.Count);
                Assert.AreEqual("folder", output[0].Value);
                Assert.AreEqual(1.001, output[0].Seconds, 1e-9);
            }
        }

        // What follows a value may wait on another thread that feeds the operator: with a lock held it would never get through.
        [TestMethod]
        public void TheValueIsGivenWithNoLockHeld()
        {
            var source = new Subject<string>();
            var clock = new Subject<double>();
            var otherThreadGotThrough = false;
            using (new OnNextTick().Process(source, clock).Subscribe(value =>
            {
                if (value.Value != "first") return;
                otherThreadGotThrough = Task.Run(() => source.OnNext("second")).Wait(2000);
            }))
            {
                source.OnNext("first");
                clock.OnNext(5.0);
                Assert.IsTrue(otherThreadGotThrough);
            }
        }
    }
}
