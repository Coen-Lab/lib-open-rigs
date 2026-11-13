using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class FirePulsesTests
    {
        // A stand-in Harp clock: ticks are pushed by hand, and each pulse is recorded with the tick it fired on.
        class Run : IDisposable
        {
            public readonly Subject<int> Input = new Subject<int>();
            public readonly Subject<double> Clock = new Subject<double>();
            public readonly List<Tuple<double, int, int>> Pulses = new List<Tuple<double, int, int>>();
            public Exception Error;
            public bool Completed;
            double now;
            readonly IDisposable subscription;

            public Run(FirePulses operation)
            {
                subscription = operation.Process(Input, Clock).Subscribe(
                    pulse => Pulses.Add(Tuple.Create(now, pulse.Item1, pulse.Item2)),
                    error => Error = error,
                    () => Completed = true);
            }

            public void Tick(double seconds)
            {
                now = seconds;
                Clock.OnNext(seconds);
            }

            public void TickEveryMs(double from, double to)
            {
                for (var ms = (int)Math.Round(from * 1000); ms <= (int)Math.Round(to * 1000); ms++) Tick(ms / 1000.0);
            }

            public void Dispose()
            {
                subscription.Dispose();
            }
        }

        [TestMethod]
        public void FiresEachPulseTheGapAfterThePreviousOneCloses()
        {
            using (var run = new Run(new FirePulses { Line = 2, WidthMs = 30, Count = 3, PulseGapMs = 100 }))
            {
                run.Tick(10.0);
                run.Input.OnNext(0);
                run.TickEveryMs(10.001, 10.5);
                Assert.IsNull(run.Error);
                Assert.AreEqual(3, run.Pulses.Count);
                CollectionAssert.AreEqual(new[] { 10.0, 10.13, 10.26 }, run.Pulses.ConvertAll(p => Math.Round(p.Item1, 6)));
                Assert.IsTrue(run.Pulses.TrueForAll(p => p.Item2 == 2 && p.Item3 == 30));
            }
        }

        [TestMethod]
        public void ATrainThatArrivesWhileOneFiresWaitsForIt()
        {
            using (var run = new Run(new FirePulses { WidthMs = 10, Count = 2, PulseGapMs = 40 }))
            {
                run.Tick(0);
                run.Input.OnNext(0);
                run.Tick(0.02);
                run.Input.OnNext(0);
                run.TickEveryMs(0.021, 0.3);
                CollectionAssert.AreEqual(new[] { 0.0, 0.05, 0.1, 0.15 }, run.Pulses.ConvertAll(p => Math.Round(p.Item1, 6)));
            }
        }

        [TestMethod]
        public void AnInputBeforeAnyTickFiresOnTheFirstTick()
        {
            using (var run = new Run(new FirePulses { WidthMs = 5, Count = 1 }))
            {
                run.Input.OnNext(0);
                Assert.AreEqual(0, run.Pulses.Count);
                run.Tick(3.5);
                Assert.AreEqual(3.5, run.Pulses[0].Item1);
            }
        }

        [TestMethod]
        public void CompletesOnlyOnceTheLastPulseHasClosed()
        {
            using (var run = new Run(new FirePulses { WidthMs = 20, Count = 1 }))
            {
                run.Tick(1.0);
                run.Input.OnNext(0);
                run.Input.OnCompleted();
                run.Tick(1.019);
                Assert.IsFalse(run.Completed);
                run.Tick(1.02);
                Assert.IsTrue(run.Completed);
            }
        }

        [TestMethod]
        public void ACountOfZeroFiresNothing()
        {
            using (var run = new Run(new FirePulses { WidthMs = 20, Count = 0 }))
            {
                run.Tick(1.0);
                run.Input.OnNext(0);
                run.Input.OnCompleted();
                Assert.AreEqual(0, run.Pulses.Count);
                Assert.IsTrue(run.Completed);
            }
        }

        [TestMethod]
        public void RefusesAClockThatGoesBackwardsWithPulsesToFire()
        {
            using (var run = new Run(new FirePulses { WidthMs = 20, Count = 3, PulseGapMs = 100 }))
            {
                run.Tick(5.0);
                run.Input.OnNext(0);
                run.Tick(0.001);  // a clock resynchronised under the pulses
                Assert.IsInstanceOfType(run.Error, typeof(InvalidOperationException));
                StringAssert.Contains(run.Error.Message, "clock jumped back from 5 s to 0.001 s with 2 valve pulse(s) still to fire");
                Assert.AreEqual(1, run.Pulses.Count);
            }
        }

        [TestMethod]
        public void TakesAClockThatGoesBackwardsWhileIdleAsTheNewTime()
        {
            using (var run = new Run(new FirePulses { WidthMs = 20, Count = 2, PulseGapMs = 100 }))
            {
                run.Tick(5.0);
                run.Input.OnNext(0);
                run.TickEveryMs(5.001, 5.2);
                Assert.AreEqual(2, run.Pulses.Count);  // 5.0 and 5.12; the second closes at 5.14
                run.Tick(1.0);  // SynchronizeTimestamp sets the board's clock while nothing waits
                Assert.IsNull(run.Error);
                run.Input.OnNext(0);
                run.TickEveryMs(1.001, 1.3);
                // The last close moved back 4.2 s with the clock, to 0.94, so the next train waits for its gap
                CollectionAssert.AreEqual(new[] { 5.0, 5.12, 1.04, 1.16 }, run.Pulses.ConvertAll(p => Math.Round(p.Item1, 6)));
            }
        }

        [TestMethod]
        public void AClockThatGoesBackwardsJustAfterAPulseKeepsTheGap()
        {
            using (var run = new Run(new FirePulses { WidthMs = 20, Count = 1, PulseGapMs = 100 }))
            {
                run.Tick(5.0);
                run.Input.OnNext(0);
                run.Tick(5.03);
                run.Tick(1.0);  // 10 ms after the pulse closed, by the old clock
                run.Input.OnNext(0);
                run.TickEveryMs(1.001, 1.2);
                Assert.IsNull(run.Error);
                Assert.AreEqual(1.09, Math.Round(run.Pulses[1].Item1, 6));
            }
        }

        [TestMethod]
        public void RefusesAClockThatEndsWithPulsesToFire()
        {
            using (var run = new Run(new FirePulses { WidthMs = 20, Count = 2, PulseGapMs = 100 }))
            {
                run.Tick(0);
                run.Input.OnNext(0);
                run.Clock.OnCompleted();
                StringAssert.Contains(run.Error.Message, "clock ended");
            }
        }

        [TestMethod]
        public void RefusesAWidthItCannotFire()
        {
            using (var run = new Run(new FirePulses { WidthMs = 0, Count = 1 }))
            {
                run.Tick(0);
                run.Input.OnNext(0);
                Assert.IsInstanceOfType(run.Error, typeof(ArgumentOutOfRangeException));
                Assert.AreEqual(0, run.Pulses.Count);
            }
        }
    }
}
