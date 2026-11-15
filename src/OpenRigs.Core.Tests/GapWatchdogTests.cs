using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    // Values are sent from the test's own thread, never a thread-pool timer, so a loaded runner
    // (CI, 15 Nov 2026) cannot make a value miss its gap.
    [TestClass]
    public class GapWatchdogTests
    {
        const string Message = "No frame from Camera-Body for 0.5 s.";

        [TestMethod]
        public void PassesValuesThatComeInTime()
        {
            var dog = new GapWatchdog { GapSeconds = 5, Message = Message };
            var source = new Subject<int>();
            var seen = new List<int>();
            Exception error = null;
            var completed = false;
            using (dog.Process(source).Subscribe(seen.Add, e => error = e, () => completed = true))
            {
                for (var i = 0; i < 8; i++) source.OnNext(i);
                source.OnCompleted();
            }
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, seen);
            Assert.IsNull(error);
            Assert.IsTrue(completed);
        }

        [TestMethod]
        public void StopsWithItsMessageAfterAGapMidRun()
        {
            var dog = new GapWatchdog { GapSeconds = 0.5, Message = Message };
            var source = new Subject<int>();
            var seen = 0;
            var stopped = new System.Threading.ManualResetEventSlim();
            Exception error = null;
            using (dog.Process(source).Subscribe(_ => seen++, e => { error = e; stopped.Set(); }))
            {
                for (var i = 0; i < 3; i++) source.OnNext(i);
                Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(30)), "the gap never stopped it");
            }
            Assert.IsInstanceOfType(error, typeof(TimeoutException));
            Assert.AreEqual(Message, error.Message);
            Assert.AreEqual(3, seen, "the values before the gap pass through");
        }

        [TestMethod]
        public void StopsWhenNothingComesFromTheStart()
        {
            var dog = new GapWatchdog { GapSeconds = 0.2, Message = Message };
            var ex = Assert.ThrowsException<TimeoutException>(() => dog.Process(Observable.Never<int>()).Wait());
            Assert.AreEqual(Message, ex.Message);
        }

        [DataTestMethod]
        [DataRow(0.0, Message, "GapSeconds of 0")]
        [DataRow(1.0, " ", "has no Message")]
        public void RefusesAMissingGapOrMessage(double gapSeconds, string message, string expected)
        {
            var dog = new GapWatchdog { GapSeconds = gapSeconds, Message = message };
            var ex = Assert.ThrowsException<InvalidOperationException>(() => dog.Process(Observable.Return(1)).Wait());
            StringAssert.Contains(ex.Message, expected);
        }
    }
}
