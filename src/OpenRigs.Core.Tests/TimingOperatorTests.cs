using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class HarpDelayTests
    {
        class Run : IDisposable
        {
            public readonly Subject<string> Input = new Subject<string>();
            public readonly Subject<double> Clock = new Subject<double>();
            public readonly List<Timestamped<string>> Output = new List<Timestamped<string>>();
            public Exception Error;
            public bool Completed;
            readonly IDisposable subscription;

            public Run(HarpDelay delay)
            {
                subscription = delay.Process(Input, Clock).Subscribe(Output.Add, e => Error = e, () => Completed = true);
            }

            public void TickEveryMs(int from, int to)
            {
                for (var ms = from; ms <= to; ms++) Clock.OnNext(ms / 1000.0);
            }

            public void Dispose()
            {
                subscription.Dispose();
            }
        }

        [TestMethod]
        public void EmitsEachInputDueSecondsOfHarpTimeLater()
        {
            var delay = new HarpDelay { DueSeconds = 0.25 };
            using (var run = new Run(delay))
            {
                run.TickEveryMs(1000, 1000);
                run.Input.OnNext("a");
                run.TickEveryMs(1001, 1100);
                delay.DueSeconds = 0.05;  // read per input, as an InputMapping sets it
                run.Input.OnNext("b");
                run.Input.OnCompleted();
                run.TickEveryMs(1101, 1249);
                Assert.AreEqual(1, run.Output.Count);
                Assert.AreEqual("b", run.Output[0].Value);
                Assert.AreEqual(1.15, run.Output[0].Seconds, 1e-9);
                Assert.IsFalse(run.Completed);
                run.TickEveryMs(1250, 1250);
                Assert.AreEqual("a", run.Output[1].Value);
                Assert.AreEqual(1.25, run.Output[1].Seconds, 1e-9);
                Assert.IsTrue(run.Completed);
            }
        }

        [TestMethod]
        public void AnInputBeforeAnyTickIsTimedFromTheFirstTick()
        {
            using (var run = new Run(new HarpDelay { DueSeconds = 0.002 }))
            {
                run.Input.OnNext("a");
                run.TickEveryMs(5, 6);
                Assert.AreEqual(0, run.Output.Count);
                run.TickEveryMs(7, 7);
                Assert.AreEqual(0.007, run.Output[0].Seconds, 1e-9);
            }
        }

        [TestMethod]
        public void RefusesAClockThatGoesBackwardsWhileAnInputWaits()
        {
            using (var run = new Run(new HarpDelay { DueSeconds = 1 }))
            {
                run.TickEveryMs(500, 500);
                run.Input.OnNext("a");
                run.TickEveryMs(10, 10);
                StringAssert.Contains(run.Error.Message, "clock jumped back from 0.5 s to 0.01 s while 1 timed event(s) were waiting");
                Assert.AreEqual(0, run.Output.Count);
            }
        }

        [TestMethod]
        public void TakesAClockThatGoesBackwardsWithNothingWaitingAsTheNewTime()
        {
            using (var run = new Run(new HarpDelay { DueSeconds = 0.005 }))
            {
                run.TickEveryMs(500, 500);
                run.TickEveryMs(10, 10);  // SynchronizeTimestamp sets the board's clock before anything waits
                Assert.IsNull(run.Error);
                run.Input.OnNext("a");
                run.TickEveryMs(11, 15);
                Assert.AreEqual(1, run.Output.Count);
                Assert.AreEqual(0.015, run.Output[0].Seconds, 1e-9);
            }
        }

        [TestMethod]
        public void RefusesANegativeDelay()
        {
            using (var run = new Run(new HarpDelay { DueSeconds = -1 }))
            {
                run.Input.OnNext("a");
                Assert.IsInstanceOfType(run.Error, typeof(ArgumentOutOfRangeException));
            }
        }

        [TestMethod]
        public void AnInputFromAnotherThreadThatEndsTheDelayDoesNotDeadlockTheClock()
        {
            // As on a dome (BLUE-OPTO-2, 14 Nov 2026): the clock ticks on its own thread under its subject's
            // lock (Bonsai's BehaviorSubject is a ReplaySubject, which calls subscribers under its lock), and
            // a lick on another thread passes a DueSeconds 0 delay whose output ends the trial, unsubscribing
            // from the clock. Sending under HarpDelay's lock froze both threads within a few hundred trials.
            var clock = new ReplaySubject<double>(1);
            var ticking = true;
            var ticker = new Thread(() =>
            {
                for (var ms = 0L; Volatile.Read(ref ticking); ms++) clock.OnNext(ms / 1000.0);
            }) { IsBackground = true };
            clock.OnNext(0);
            ticker.Start();
            var trials = Task.Run(() =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    var input = new Subject<int>();
                    var done = new ManualResetEventSlim();
                    using (new HarpDelay { DueSeconds = 0 }.Process(input, clock).Take(1).Subscribe(_ => { }, () => done.Set()))
                    {
                        input.OnNext(i);
                        if (!done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("trial " + i + " never ended");
                    }
                }
            });
            var finished = trials.Wait(TimeSpan.FromSeconds(60));
            Volatile.Write(ref ticking, false);
            Assert.IsTrue(finished, "HarpDelay deadlocked with its clock's thread");
        }
    }

    [TestClass]
    public class StartupWatchdogTests
    {
        [TestMethod]
        public void CompletesOnceEveryStageHasArrived()
        {
            var stages = new[] { "Board", "Steppers", "Board" }.ToObservable();
            var watchdog = new StartupWatchdog { Expected = "Steppers, Board", TimeoutSeconds = 5 };
            Assert.IsTrue(watchdog.Process(stages).IsEmpty().Timeout(TimeSpan.FromSeconds(2)).Wait());
        }

        [TestMethod]
        public void NamesTheStagesThatNeverArrived()
        {
            var watchdog = new StartupWatchdog { Expected = "Board, Steppers, Camera", TimeoutSeconds = 0.1, What = "Zeroing" };
            var ex = Assert.ThrowsException<TimeoutException>(() => watchdog.Process(Observable.Return("Board")).Wait());
            Assert.AreEqual("Zeroing did not finish within 0.1 s. Arrived: Board. Never arrived: Steppers, Camera.", ex.Message);
        }

        [TestMethod]
        public void RefusesToWatchNothing()
        {
            Assert.ThrowsException<InvalidOperationException>(() => new StartupWatchdog().Process(Observable.Never<string>()).Wait());
        }

        [TestMethod]
        public void ThrowErrorStopsWithTheMessage()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => new ThrowError().Process(Observable.Return("the board never answered")).Wait());
            Assert.AreEqual("the board never answered", ex.Message);
        }
    }
}
