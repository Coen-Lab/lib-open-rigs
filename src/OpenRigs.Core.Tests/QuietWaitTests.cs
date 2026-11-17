using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class QuietWaitTests
    {
        [TestMethod]
        public void WithBothWaitsOffGoIsTheEarliest()
        {
            var wait = new QuietWait(0, 5, 0, 0, 0, double.NaN, false);
            Assert.AreEqual(5, wait.TrialStartTime, 1e-12);
            Assert.AreEqual(5, wait.Earliest, 1e-12);
        }

        [TestMethod]
        public void AHeldSpoutBlocksGoAndItsReleaseRestartsTheLickWait()
        {
            var wait = new QuietWait(0, 1, 2, 0, 0, double.NaN, false);
            Assert.AreEqual(2, wait.TrialStartTime, 1e-12);  // released since the interval's start, 2 s needed
            wait.Lick(true, 1.5);
            Assert.AreEqual(double.PositiveInfinity, wait.TrialStartTime);
            wait.Lick(false, 3);
            Assert.AreEqual(5, wait.TrialStartTime, 1e-12);
        }

        [TestMethod]
        public void ASpoutHeldAtTheStartBlocksGo()
        {
            var wait = new QuietWait(0, 1, 2, 0, 0, double.NaN, true);
            Assert.AreEqual(double.PositiveInfinity, wait.TrialStartTime);
        }

        [TestMethod]
        public void TravelPastTheLimitRestartsTheWheelWaitAtTheMoveThatTookItPast()
        {
            var wait = new QuietWait(0, 1, 0, 1, 2, 0, false);
            Assert.AreEqual(1, wait.TrialStartTime, 1e-12);
            wait.Wheel(1, 0.5);    // 1 cm in the window: within 2 cm
            Assert.AreEqual(1, wait.TrialStartTime, 1e-12);
            wait.Wheel(2.5, 0.8);  // 2.5 cm: past, so the wait restarts at the move dropped to bring it back, at 0.5 s
            Assert.AreEqual(1.5, wait.TrialStartTime, 1e-12);
            wait.Wheel(2.5, 2.0);  // still: the window empties, the wait stands
            Assert.AreEqual(1.5, wait.TrialStartTime, 1e-12);
        }

        [TestMethod]
        public void SettingsOutOfRangeOrAHalfSetWheelPairAreRefusedByName()
        {
            CollectionAssert.AreEqual(new string[0], QuietWait.LickProblems(10));
            CollectionAssert.AreEqual(new string[0], QuietWait.WheelProblems(10, 20));
            CollectionAssert.AreEqual(new[] { "lickQuietSeconds is -1, not from 0 to 10 s" }, QuietWait.LickProblems(-1));
            CollectionAssert.AreEqual(new[] { "wheelQuietCm is 25, not from 0 to 20 cm" }, QuietWait.WheelProblems(1, 25));
            CollectionAssert.AreEqual(new[] { "wheelQuietSeconds 1 and wheelQuietCm 0 are both above 0 (the wheel's quiet wait) or both 0 (off), not one of each" },
                QuietWait.WheelProblems(1, 0));
            Assert.AreEqual(1, QuietWait.LickProblems(double.NaN).Count);
            var error = Assert.ThrowsException<System.ArgumentException>(() => new QuietWait(0, double.PositiveInfinity, 11, 0, 0, double.NaN, false));
            StringAssert.Contains(error.Message, "lickQuietSeconds is 11");
            StringAssert.Contains(error.Message, "the earliest start is");
        }

        [TestMethod]
        public void ANaNWheelReadingIsRefused()
        {
            var wait = new QuietWait(0, 1, 0, 1, 2, 0, false);
            Assert.ThrowsException<System.ArgumentException>(() => wait.Wheel(double.NaN, 0.5));
        }

        [TestMethod]
        public void TheFirstWheelReadingIsNotAMove()
        {
            var wait = new QuietWait(0, 1, 0, 1, 2, double.NaN, false);
            wait.Wheel(100, 0.5);
            Assert.AreEqual(1, wait.TrialStartTime, 1e-12);
        }
    }
}
