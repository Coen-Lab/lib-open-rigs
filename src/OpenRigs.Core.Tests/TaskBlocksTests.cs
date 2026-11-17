using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class TaskBlocksTests
    {
        [TestMethod]
        public void ItiNeedsTwoFiniteTimesFromZeroInOrder()
        {
            CollectionAssert.AreEqual(new string[0], TaskBlocks.ItiProblems(0, 0));
            CollectionAssert.AreEqual(new string[0], TaskBlocks.ItiProblems(2, 3));
            CollectionAssert.AreEqual(new[] { "itiMinSeconds is -1, not a finite number, 0 or more" }, TaskBlocks.ItiProblems(-1, 3));
            CollectionAssert.AreEqual(new[] { "itiMaxSeconds (2 s) is below itiMinSeconds (3 s)" }, TaskBlocks.ItiProblems(3, 2));
            Assert.AreEqual(1, TaskBlocks.ItiProblems(1, double.PositiveInfinity).Count);
            Assert.AreEqual(1, TaskBlocks.ItiProblems(double.NaN, 1).Count);
        }

        [TestMethod]
        public void AResponseWindowIsAboveZeroOrNoLimit()
        {
            CollectionAssert.AreEqual(new string[0], TaskBlocks.ResponseWindowProblems(null));
            CollectionAssert.AreEqual(new string[0], TaskBlocks.ResponseWindowProblems(1.5));
            CollectionAssert.AreEqual(new[] { "responseWindowSeconds is 0, not a finite number above 0" }, TaskBlocks.ResponseWindowProblems(0));
            Assert.AreEqual(1, TaskBlocks.ResponseWindowProblems(double.NaN).Count);
            Assert.AreEqual(1, TaskBlocks.ResponseWindowProblems(double.PositiveInfinity).Count);
        }

        [TestMethod]
        public void AutoRewardIsAProbability()
        {
            CollectionAssert.AreEqual(new string[0], TaskBlocks.AutoRewardProblems(0));
            CollectionAssert.AreEqual(new string[0], TaskBlocks.AutoRewardProblems(1));
            CollectionAssert.AreEqual(new[] { "autoRewardProbability is 1.5, not from 0 to 1" }, TaskBlocks.AutoRewardProblems(1.5));
            Assert.AreEqual(1, TaskBlocks.AutoRewardProblems(-0.1).Count);
            Assert.AreEqual(1, TaskBlocks.AutoRewardProblems(double.NaN).Count);
        }
    }
}
