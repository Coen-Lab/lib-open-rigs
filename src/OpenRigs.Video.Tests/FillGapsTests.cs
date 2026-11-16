using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Video.Tests
{
    [TestClass]
    public class FillGapsTests
    {
        static IObservable<int?> Frames(params int[] counters)
        {
            return counters.Select(c => (int?)c).ToObservable().FillGaps(f => f.Value, (previous, current) => current - previous - 1);
        }

        // A dropped frame keeps its place as a default, so each later frame still pairs with its own trigger.
        [TestMethod]
        public void ADroppedFrameKeepsItsPlace()
        {
            CollectionAssert.AreEqual(new int?[] { 0, 1, null, null, 4 }, Frames(0, 1, 4).ToArray().Wait());
        }

        // Nothing comes before the first frame to say it came late, so it is taken as the first trigger's.
        [TestMethod]
        public void TheFirstFrameIsTakenAsTheFirst()
        {
            CollectionAssert.AreEqual(new int?[] { 3, 4 }, Frames(3, 4).ToArray().Wait());
        }

        // A counter that goes back stops the frames there: nothing after it is passed on.
        [TestMethod]
        public void ACounterThatGoesBackStopsTheFrames()
        {
            var seen = new System.Collections.Generic.List<int?>();
            Exception error = null;
            Frames(5, 6, 2, 3).Subscribe(seen.Add, e => error = e);
            CollectionAssert.AreEqual(new int?[] { 5, 6 }, seen);
            Assert.IsInstanceOfType(error, typeof(InvalidOperationException));
        }

        // The camera's own error (not found, unplugged) reaches the workflow instead of being thrown on its thread.
        [TestMethod]
        public void TheSourcesErrorIsPassedOn()
        {
            var failure = new InvalidOperationException("Spinnaker camera with serial number 1 was not found.");
            Exception error = null;
            Observable.Throw<int>(failure).FillGaps((previous, current) => current - previous - 1).Subscribe(_ => { }, e => error = e);
            Assert.AreSame(failure, error);
        }

        [TestMethod]
        public void CompletionIsPassedOn()
        {
            var completed = false;
            Frames(0, 1).Subscribe(_ => { }, () => completed = true);
            Assert.IsTrue(completed);
        }
    }
}
