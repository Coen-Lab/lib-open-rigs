using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Trials.Tests
{
    [TestClass]
    public class SharedTrialColumnsTests
    {
        // A task's row: the shared block in trial-columns.yml's order, then its own.
        class OptogeneticsRow
        {
            public int trialNumber;
            public string trialType;
            public string outcome;
            public double itiStartTime;
            public double itiSeconds;
            public double quietWaitSeconds;
            public double trialStartTime;
            public double stimulusOnsetTime;
            public double stimulusOffTime;
            public double outcomeTime;
            public double rewardOfferedUl;
            public bool rewarded;
            public double rewardConsumptionSeconds;
            public double penaltyTimeoutSeconds;
            public bool penalised;
            public int laserCondition;
            public double laserPowerMw;
            public double laserTriggerTime;
            public string choice;
        }

        class CoreRow
        {
            public int trialNumber;
            public double itiStartTime;
            public double itiSeconds;
            public double trialStartTime;
            public double stimulusOnsetTime;
            public double stimulusOffTime;
            public double outcomeTime;
            public double rewardOfferedUl;
            public bool rewarded;
            public double rewardConsumptionSeconds;
            public double penaltyTimeoutSeconds;
            public bool penalised;
        }

        class HalfOptogenetics : CoreRow { public int laserCondition; }

        class NoTimeout { public int trialNumber; }

        class FloatTimes
        {
            public int trialNumber;
            public double itiStartTime;
            public float itiSeconds;
            public double trialStartTime;
            public double stimulusOnsetTime;
            public double stimulusOffTime;
            public double outcomeTime;
            public double rewardOfferedUl;
            public bool rewarded;
            public double rewardConsumptionSeconds;
            public double penaltyTimeoutSeconds;
            public bool penalised;
        }

        static TrialTimes Times(double trigger)
        {
            return new TrialTimes { ItiStartTime = 10, ItiSeconds = 2.5, QuietWaitSeconds = 0.25, TrialStartTime = 12.25, StimulusOnsetTime = 12.751, StimulusOffTime = 13.751, LaserTriggerTime = trigger, Now = 13.5 };
        }

        static StubTrial Laser() { return new StubTrial { TrialNumber = 7, LaserCondition = 3, LaserOn = true, LaserPowerMw = 4.5 }; }

        [TestMethod]
        public void FillsTheSharedBlockFromTheLoopsTimes()
        {
            var row = SharedTrialColumns.Fill(new OptogeneticsRow { choice = "Left" }, Laser(), Times(12.25), 3, false, 1.5, true, 6);
            Assert.AreEqual(7, row.trialNumber);
            Assert.AreEqual(10, row.itiStartTime);
            Assert.AreEqual(2.5, row.itiSeconds);
            Assert.AreEqual(0.25, row.quietWaitSeconds);
            Assert.AreEqual(12.25, row.trialStartTime);
            Assert.AreEqual(12.751, row.stimulusOnsetTime);
            Assert.AreEqual(13.751, row.stimulusOffTime);
            Assert.AreEqual(13.5, row.outcomeTime, "the outcome is Now");
            Assert.AreEqual(3, row.rewardOfferedUl, "the offer, on a trial not rewarded too");
            Assert.IsFalse(row.rewarded);
            Assert.AreEqual(0, row.rewardConsumptionSeconds, "no reward, no consumption");
            Assert.AreEqual(6, row.penaltyTimeoutSeconds);
            Assert.IsTrue(row.penalised);
            Assert.AreEqual(3, row.laserCondition);
            Assert.AreEqual(4.5, row.laserPowerMw);
            Assert.AreEqual(12.25, row.laserTriggerTime);
            Assert.AreEqual("Left", row.choice, "the task's own columns untouched");
            Assert.IsNull(row.outcome);
        }

        [TestMethod]
        public void AnUnpenalisedOrUntriggeredTrialLogsNoTimeoutSoundOrPower()
        {
            var row = SharedTrialColumns.Fill(new OptogeneticsRow(), Laser(), Times(double.NaN), 3, true, 1.5, false, 6);
            Assert.IsTrue(row.rewarded);
            Assert.AreEqual(1.5, row.rewardConsumptionSeconds);
            Assert.IsFalse(row.penalised);
            Assert.AreEqual(0, row.penaltyTimeoutSeconds);
            Assert.AreEqual(0, row.laserPowerMw, "answered before its trigger: no laser");
            Assert.IsTrue(double.IsNaN(row.laserTriggerTime));

            var core = SharedTrialColumns.Fill(new CoreRow(), Laser(), Times(1), 3, false, 1.5, true, 6);
            Assert.AreEqual(7, core.trialNumber, "a row without the opt-in blocks");
        }

        [TestMethod]
        public void ARowMissingOrMistypingAColumnIsRefused()
        {
            Func<Action, string> refusal = action =>
            {
                try { action(); }
                catch (InvalidOperationException e) { return e.Message; }
                throw new AssertFailedException("no refusal");
            };
            StringAssert.Contains(refusal(() => SharedTrialColumns.Fill(new NoTimeout(), Laser(), Times(1), 0, false, 1.5, false, 0)), "has no itiStartTime, itiSeconds");
            StringAssert.Contains(refusal(() => SharedTrialColumns.Fill(new NoTimeout(), Laser(), Times(1), 0, false, 1.5, false, 0)), "rewardOfferedUl, rewarded, rewardConsumptionSeconds, penaltyTimeoutSeconds, penalised");
            StringAssert.Contains(refusal(() => SharedTrialColumns.Fill(new HalfOptogenetics(), Laser(), Times(1), 0, false, 1.5, false, 0)), "not all of laserCondition, laserPowerMw, laserTriggerTime");
            StringAssert.Contains(refusal(() => SharedTrialColumns.Fill(new FloatTimes(), Laser(), Times(1), 0, false, 1.5, false, 0)), "itiSeconds is a Single, not a Double");
        }

        [TestMethod]
        public void AnOutcomeIsBuiltFromItsFilledRow()
        {
            var rewarded = new TrialOutcome<OptogeneticsRow>(SharedTrialColumns.Fill(new OptogeneticsRow { outcome = TrialOutcomes.Correct }, Laser(), Times(1), 3, true, 1.5, false, 6), "right", -45);
            Assert.AreEqual(13.5, rewarded.Row.Seconds, "stamped with its outcomeTime");
            Assert.IsTrue(rewarded.Rewarded);
            Assert.AreEqual(Tuple.Create("right", 3.0), rewarded.Reward);
            Assert.IsFalse(rewarded.Penalised);
            Assert.AreEqual(0, rewarded.PenaltyTimeoutSeconds);
            Assert.AreEqual(1.5, rewarded.RewardConsumptionSeconds);

            var penalised = new TrialOutcome<OptogeneticsRow>(SharedTrialColumns.Fill(new OptogeneticsRow { outcome = TrialOutcomes.Incorrect }, Laser(), Times(1), 3, false, 1.5, true, 6), "left", double.NaN);
            Assert.IsNull(penalised.Reward, "no reward unless rewarded, in every task");
            Assert.IsTrue(penalised.Penalised);
            Assert.IsTrue(double.IsNaN(penalised.PenaltyLevelDbfs), "a mute penalty: the trial's level, NaN");
            Assert.AreEqual(-45, rewarded.PenaltyLevelDbfs);
            Assert.AreEqual(6, penalised.PenaltyTimeoutSeconds);
            Assert.AreEqual(0, penalised.RewardConsumptionSeconds);
        }

        [TestMethod]
        public void AnOutcomeOutsideTheLabsIsRefused()
        {
            try
            {
                new TrialOutcome<OptogeneticsRow>(SharedTrialColumns.Fill(new OptogeneticsRow { outcome = "Hit" }, Laser(), Times(1), 3, true, 1.5, false, 6), "left", -45);
                Assert.Fail("no refusal");
            }
            catch (InvalidOperationException e)
            {
                StringAssert.Contains(e.Message, "outcome is Hit, not one of correct, incorrect, noResponse, probe, auto");
            }
        }
    }
}
