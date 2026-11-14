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
            Assert.AreEqual(5, wait.GoTime, 1e-12);
            Assert.AreEqual(5, wait.Earliest, 1e-12);
        }

        [TestMethod]
        public void AHeldSpoutBlocksGoAndItsReleaseRestartsTheLickWait()
        {
            var wait = new QuietWait(0, 1, 2, 0, 0, double.NaN, false);
            Assert.AreEqual(2, wait.GoTime, 1e-12);  // released since the interval's start, 2 s needed
            wait.Lick(true, 1.5);
            Assert.AreEqual(double.PositiveInfinity, wait.GoTime);
            wait.Lick(false, 3);
            Assert.AreEqual(5, wait.GoTime, 1e-12);
        }

        [TestMethod]
        public void ASpoutHeldAtTheStartBlocksGo()
        {
            var wait = new QuietWait(0, 1, 2, 0, 0, double.NaN, true);
            Assert.AreEqual(double.PositiveInfinity, wait.GoTime);
        }

        [TestMethod]
        public void TravelPastTheLimitRestartsTheWheelWaitAtTheMoveThatTookItPast()
        {
            var wait = new QuietWait(0, 1, 0, 1, 2, 0, false);
            Assert.AreEqual(1, wait.GoTime, 1e-12);
            wait.Wheel(1, 0.5);    // 1 cm in the window: within 2 cm
            Assert.AreEqual(1, wait.GoTime, 1e-12);
            wait.Wheel(2.5, 0.8);  // 2.5 cm: past, so the wait restarts at the move dropped to bring it back, at 0.5 s
            Assert.AreEqual(1.5, wait.GoTime, 1e-12);
            wait.Wheel(2.5, 2.0);  // still: the window empties, the wait stands
            Assert.AreEqual(1.5, wait.GoTime, 1e-12);
        }

        [TestMethod]
        public void TheFirstWheelReadingIsNotAMove()
        {
            var wait = new QuietWait(0, 1, 0, 1, 2, double.NaN, false);
            wait.Wheel(100, 0.5);
            Assert.AreEqual(1, wait.GoTime, 1e-12);
        }
    }
}
