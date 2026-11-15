using System;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class GapWatchdogTests
    {
        const string Message = "No frame from Camera-Body for 0.3 s.";

        [TestMethod]
        public void PassesValuesThatComeInTime()
        {
            var dog = new GapWatchdog { GapSeconds = 0.3, Message = Message };
            var values = dog.Process(Observable.Interval(TimeSpan.FromMilliseconds(50)).Take(8)).ToList().Wait();
            CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => (long)i).ToList(), values.ToList());
        }

        [TestMethod]
        public void StopsWithItsMessageAfterAGapMidRun()
        {
            var dog = new GapWatchdog { GapSeconds = 0.3, Message = Message };
            var stream = Observable.Interval(TimeSpan.FromMilliseconds(50)).Take(3).Concat(Observable.Never<long>());
            var seen = 0;
            var ex = Assert.ThrowsException<TimeoutException>(() => dog.Process(stream).Do(_ => seen++).Wait());
            Assert.AreEqual(Message, ex.Message);
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
