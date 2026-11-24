using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;

namespace OpenRigs.Trials.Tests
{
    class BiasResponse : ISpoutBiasResponse
    {
        public string Target { get; set; }
        public string Choice { get; set; }
        public bool SingleSpoutOffered { get; set; }
        public int RepeatCount { get; set; }
        public bool LaserOn { get; set; }

        public static BiasResponse Of(string target, bool correct)
        {
            return new BiasResponse { Target = target, Choice = correct ? target : (target == "left" ? "right" : "left") };
        }
    }

    [TestClass]
    public class SpoutBiasTests
    {
        static LickSpoutCommand? Feed(SpoutBias bias, string target, bool correct, int times)
        {
            LickSpoutCommand? last = null;
            for (var i = 0; i < times; i++) last = bias.Respond(BiasResponse.Of(target, correct));
            return last;
        }

        [TestMethod]
        public void NothingMovesUntilBothBuffersAreFull()
        {
            var bias = new SpoutBias(3, 0.3, 100, 500, 0);
            Assert.IsNull(Feed(bias, "left", true, 5), "the left buffer full, the right empty");
            Assert.IsNull(Feed(bias, "right", false, 2));
            Assert.IsNull(bias.Bias);
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(bias, "right", false, 1), "both full: all left correct, no right");
            Assert.AreEqual(1.0, bias.Bias.Value, 1e-12);
            Assert.AreEqual(100, bias.OffsetSteps);
        }

        [TestMethod]
        public void FavouringTheLeftMovesLeftAndTheRightMovesRight()
        {
            var left = new SpoutBias(2, 0.3, 100, 500, 0);
            Feed(left, "left", true, 2);
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(left, "right", false, 2));
            var right = new SpoutBias(2, 0.3, 100, 500, 0);
            Feed(right, "left", false, 2);
            Assert.AreEqual(LickSpoutCommand.BiasRight, Feed(right, "right", true, 2));
            Assert.AreEqual(-100, right.OffsetSteps);
            Assert.AreEqual(-1.0, right.Bias.Value, 1e-12);
        }

        [TestMethod]
        public void EveryCountedTrialWhileBiasedMovesOnceUpToTheLimit()
        {
            var bias = new SpoutBias(2, 0.3, 100, 250, 0);
            Feed(bias, "left", true, 2);
            Feed(bias, "right", false, 1);
            var moves = Enumerable.Range(0, 5).Select(i => bias.Respond(BiasResponse.Of(i % 2 == 0 ? "right" : "left", i % 2 == 1))).ToArray();
            CollectionAssert.AreEqual(new LickSpoutCommand?[] { LickSpoutCommand.BiasLeft, LickSpoutCommand.BiasLeft, LickSpoutCommand.BiasLeft, null, null }, moves,
                "one move a trial, the last held to 250, then none past it");
            Assert.AreEqual(250, bias.OffsetSteps);
        }

        [TestMethod]
        public void AfterAMoveEachSideTakesNewCountedTrialsBeforeTheNext()
        {
            // The first move as soon as both buffers are full; then none until 3 new counted trials a side, uncounted ones not among them.
            var bias = new SpoutBias(2, 0.3, 100, 500, 3);
            Feed(bias, "left", true, 2);
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(bias, "right", false, 2), "before the first move, as with 0");
            Assert.IsNull(Feed(bias, "left", true, 3), "still biased, but the right has no new trial");
            Assert.IsNull(Feed(bias, "right", false, 2));
            foreach (var uncounted in new[]
            {
                new BiasResponse { Target = "right", Choice = "noResponse" },
                new BiasResponse { Target = "right", Choice = "left", RepeatCount = 1 },
                new BiasResponse { Target = "right", Choice = "left", SingleSpoutOffered = true },
                new BiasResponse { Target = "right", Choice = "left", LaserOn = true },
                new BiasResponse { Target = "noReward", Choice = "left" },
            })
            {
                Assert.IsNull(bias.Respond(uncounted));
            }
            Assert.AreEqual(100, bias.OffsetSteps);
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(bias, "right", false, 1), "the third new counted right trial");
            Assert.AreEqual(200, bias.OffsetSteps);
            Assert.IsNull(Feed(bias, "left", true, 1), "the count starts again at the move");
        }

        [TestMethod]
        public void ZeroNewTrialsAfterAMoveIsTheEveryTrialRule()
        {
            var every = new SpoutBias(2, 0.3, 100, 500, 0);
            Feed(every, "left", true, 2);
            Feed(every, "right", false, 2);
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(every, "left", true, 1), "the very next counted trial moves again");
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(every, "right", false, 1));
            Assert.AreEqual(300, every.OffsetSteps);
        }

        [TestMethod]
        public void ABalancedMouseLeavesTheOffsetWhereItIs()
        {
            var bias = new SpoutBias(2, 0.3, 100, 500, 0);
            Feed(bias, "left", true, 2);
            Feed(bias, "right", false, 2);
            Assert.AreEqual(100, bias.OffsetSteps);
            Assert.IsNull(Feed(bias, "right", true, 2), "one right correct: still biased, a move; both: balanced");
            Assert.AreEqual(0.0, bias.Bias.Value, 1e-12);
            Assert.AreEqual(200, bias.OffsetSteps);
            Feed(bias, "left", true, 3);
            Feed(bias, "right", true, 3);
            Assert.AreEqual(200, bias.OffsetSteps, "no drift back to 0");
        }

        [TestMethod]
        public void TheThresholdIsStrictInCounts()
        {
            // 10 a side, threshold 0.3: 3 more correct on the left (0.7 - 0.4, 0.8 - 0.5 alike) is not above it; 4 is.
            foreach (var leftCorrect in new[] { 7, 8, 10 })
            {
                var bias = new SpoutBias(10, 0.3, 100, 500, 0);
                Feed(bias, "left", true, leftCorrect);
                Feed(bias, "left", false, 10 - leftCorrect);
                Feed(bias, "right", false, 10 - (leftCorrect - 3));
                Assert.IsNull(Feed(bias, "right", true, leftCorrect - 3), leftCorrect + " left correct");
            }
            var above = new SpoutBias(10, 0.3, 100, 500, 0);
            Feed(above, "left", true, 8);
            Feed(above, "left", false, 2);
            Feed(above, "right", true, 4);
            Assert.AreEqual(LickSpoutCommand.BiasLeft, Feed(above, "right", false, 6));
        }

        [TestMethod]
        public void OnlyASideTargetWithBothSpoutsAChoiceNoRepeatAndNoLaserIsCounted()
        {
            var refused = new[]
            {
                new BiasResponse { Target = "random", Choice = "left" },
                new BiasResponse { Target = "noReward", Choice = "right" },
                new BiasResponse { Target = "go", Choice = "go" },
                new BiasResponse { Target = "right", Choice = "right", SingleSpoutOffered = true },
                new BiasResponse { Target = "right", Choice = "noResponse" },
                new BiasResponse { Target = "right", Choice = null },
                new BiasResponse { Target = "right", Choice = "right", RepeatCount = 1 },
                new BiasResponse { Target = "right", Choice = "right", LaserOn = true },
            };
            var bias = new SpoutBias(2, 0.3, 100, 500, 0);
            Feed(bias, "left", true, 2);
            Feed(bias, "right", false, 1);
            foreach (var response in refused)
            {
                Assert.IsFalse(SpoutBias.Counts(response), response.Target + " " + response.Choice);
                Assert.IsNull(bias.Respond(response));
            }
            Assert.IsNull(bias.Bias, "none filled the right buffer");
            Assert.IsFalse(SpoutBias.Counts(null));
            Assert.IsTrue(SpoutBias.Counts(new BiasResponse { Target = "right", Choice = "left" }), "a wrong choice counts, as an auto-rewarded trial counts by its choice");
            Assert.AreEqual(LickSpoutCommand.BiasLeft, bias.Respond(BiasResponse.Of("right", false)));
        }

        [TestMethod]
        public void EachStartsAt0AndGivesTheStageItsStepAndLimit()
        {
            var bias = new SpoutBias(10, 0.3, 50, 400, 0);
            Assert.AreEqual(0, bias.OffsetSteps);
            Assert.IsNull(bias.Bias);
            Assert.AreEqual(50, bias.StageSettings.StepSteps);
            Assert.AreEqual(400, bias.StageSettings.MaxSteps);
        }

        [TestMethod]
        public void ValuesOutsideTheSchemasBoundsAreRefused()
        {
            Assert.AreEqual(0, SpoutBias.Problems(10, 0.3, 100, 500, 0).Count);
            Assert.AreEqual(0, SpoutBias.Problems(1, 1, 1000, 1000, 0).Count);
            CollectionAssert.AreEqual(new[]
            {
                "spouts.biasCorrection.trialsPerSide (0) must be 1 or more",
                "spouts.biasCorrection.threshold (0) must be above 0 and at most 1",
                "spouts.biasCorrection.maxSteps (1001) must be 1 to 1000 steps",
                "spouts.biasCorrection.stepSteps (0) must be 1 or more",
            }, SpoutBias.Problems(0, 0, 0, 1001, 0));
            Assert.AreEqual("spouts.biasCorrection.stepSteps (200) is above maxSteps (100): one move would go past the furthest", SpoutBias.Problems(10, 0.3, 200, 100, 0).Single());
            Assert.AreEqual(1, SpoutBias.Problems(10, double.NaN, 100, 500, 0).Count);
            Assert.AreEqual(1, SpoutBias.Problems(10, 1.1, 100, 500, 0).Count);
            Assert.AreEqual(2, SpoutBias.Problems(10, 0.3, 100, 0, 0).Count, "maxSteps 0, and stepSteps above it");
            Assert.AreEqual(0, SpoutBias.Problems(10, 0.3, 100, 500, 100).Count);
            Assert.AreEqual("spouts.biasCorrection.newTrialsAfterMove (101) must be 0 to 100", SpoutBias.Problems(10, 0.3, 100, 500, 101).Single());
            Assert.AreEqual(1, SpoutBias.Problems(10, 0.3, 100, 500, -1).Count);
            StringAssert.Contains(Assert.ThrowsException<ArgumentException>(() => new SpoutBias(10, 0.3, 600, 500, 0)).Message, "stepSteps (600) is above maxSteps (500)");
        }
    }
}
