using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;

namespace OpenRigs.Trials.Tests
{
    [TestClass]
    public class RewardedAsDeliveredTests
    {
        static PieceOutcome Correct(double time) =>
            new PieceOutcome(new PieceRow { outcome = TrialOutcomes.Correct, outcomeTime = time, rewarded = true, rewardOfferedUl = 2.5 });

        static RewardPlan Delivered(bool manual) =>
            new RewardPlan { RewardLine = new RewardLine { Instance = "left" }, RequestedUl = 2.5, Manual = manual };

        static List<PieceRow> Run(params Action<Subject<TrialOutcome<PieceRow>>, Subject<RewardPlan>>[] steps)
        {
            var outcomes = new Subject<TrialOutcome<PieceRow>>();
            var delivered = new Subject<RewardPlan>();
            var rows = new List<PieceRow>();
            using (new RewardedAsDelivered().Process(outcomes, delivered).Subscribe(row => rows.Add(row.Value)))
            {
                foreach (var step in steps) step(outcomes, delivered);
                outcomes.OnCompleted();
            }
            return rows;
        }

        // A manual drop from the same valve and volume just before a rewarded outcome is not that trial's reward: the
        // row waits for its own delivery, and goes out not rewarded when none comes.
        [TestMethod]
        public void ManualRewardBeforeOutcome_DoesNotCount()
        {
            var rows = Run((o, d) => d.OnNext(Delivered(true)), (o, d) => o.OnNext(Correct(1)));
            Assert.AreEqual(1, rows.Count);
            Assert.IsFalse(rows[0].rewarded);

            rows = Run((o, d) => d.OnNext(Delivered(false)), (o, d) => o.OnNext(Correct(1)));
            Assert.IsTrue(rows[0].rewarded, "the trial's own delivery, come first, still counts");
        }

        [TestMethod]
        public void ManualRewardAfterOutcome_DoesNotCount()
        {
            var rows = Run((o, d) => o.OnNext(Correct(1)), (o, d) => d.OnNext(Delivered(true)));
            Assert.IsFalse(rows[0].rewarded);
        }
    }
}
