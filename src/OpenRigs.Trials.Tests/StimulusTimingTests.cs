using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Trials.Tests
{
    /// <summary>Rules whose stimulus goes on after the outcome: each call an update naming the trial and the wheel.</summary>
    class AfterOutcomeRules : StubRules, IStimulusAfterOutcome<StubTrial, string>
    {
        public void AfterOutcome(StubTrial trial, TrialTimes times, double wheelCm, ICollection<string> updates)
        {
            updates.Add("after " + trial.TrialNumber + " " + wheelCm);
        }
    }

    [TestClass]
    public class StimulusTimingTests
    {
        // 2 s intervals from 100.001 s: trial 1's stimulus at 102.001 s, its window closing at 103.501 s.
        static Func<int, TrialTiming> Timed(double duration, double? responseToStimulusOff, double lead = 0)
        {
            return n => new TrialTiming { ItiSeconds = 2, LaserToStimulusSeconds = lead, ResponseWindowSeconds = 1.5, StimulusDurationSeconds = duration, ResponseToStimulusOffSeconds = responseToStimulusOff };
        }

        static LoopRig LickedAt(StubRules rules, double seconds, double until)
        {
            var rig = new LoopRig(rules);
            rig.Until(seconds);
            rig.Lick(0);
            rig.Until(until);
            return rig;
        }

        static IEnumerable<TrialEvent<StubTrial, string, StubOutcome>> Of(LoopRig rig, TrialEventKind kind, int trial)
        {
            return rig.Of(kind).Where(e => e.Trial.TrialNumber == trial);
        }

        [TestMethod]
        public void TheOffTimeIsTheEarlierOfTheDurationAndTheResponsesOff()
        {
            Assert.AreEqual(12, StimulusTiming.Time(10, 2, 11, null), "null: the response does not end it");
            Assert.AreEqual(11, StimulusTiming.Time(10, 2, 11, 0), "0: off at the response");
            Assert.AreEqual(11.5, StimulusTiming.Time(10, 2, 11, 0.5));
            Assert.AreEqual(12, StimulusTiming.Time(10, 2, 11.8, 0.5), "never past the duration");
            Assert.IsTrue(double.IsNaN(StimulusTiming.Time(double.NaN, 2, 11, 0)), "no stimulus, no off");
            Assert.AreEqual(11.5, StimulusTiming.Time(10, double.NaN, 11, 0.5), "no duration: no cap");
            Assert.IsTrue(double.IsNaN(StimulusTiming.Time(10, double.NaN, 11, null)), "neither: no off timed");
        }

        [TestMethod]
        public void TheChecksNameTheStimulusKeys()
        {
            Assert.AreEqual(0, StimulusTiming.Problems(1, null, 1, double.PositiveInfinity, 0, 0).Count);
            Assert.AreEqual(0, StimulusTiming.Problems(1, 1, 1, 1.5, 0, 0).Count, "off by the next stimulus even with no timeout");
            CollectionAssert.AreEqual(new[] { "stimulus.durationSeconds must be null or a finite number above 0", "stimulus.responseToStimulusOffSeconds must be null or a finite number, 0 or more" }, StimulusTiming.Problems(0, -1, 3, 3, 0, 0));
            CollectionAssert.AreEqual(new[] { "stimulus.durationSeconds must be null or a finite number above 0", "stimulus.responseToStimulusOffSeconds must be null or a finite number, 0 or more" }, StimulusTiming.Problems(double.NaN, double.PositiveInfinity, 3, 3, 0, 0).Take(2).ToArray());
            CollectionAssert.AreEqual(new[] { "stimulus.durationSeconds (2 s) runs 2 s past the soonest outcome, longer than the shortest outcome to next interval (0 s: timeout or reward consumption) plus itiMinSeconds (1.5 s): the stimulus could still be on at the next trial's" }, StimulusTiming.Problems(2, 0, 1.5, 3, 0, 0));
            Assert.AreEqual(0, StimulusTiming.Problems(null, 0, 1, double.PositiveInfinity, 0, 0).Count, "no duration: its checks skipped");
            Assert.AreEqual(1, StimulusTiming.Problems(null, 2.5, 2, double.PositiveInfinity, 0, 0).Count, "the response's checks still apply");
            CollectionAssert.AreEqual(new[] { "stimulus.durationSeconds and stimulus.responseToStimulusOffSeconds are both null: the stimulus would never end" }, StimulusTiming.Problems(null, null, 2, double.PositiveInfinity, 0, 0));
            CollectionAssert.AreEqual(new[] { "stimulus.durationSeconds (2 s) is longer than the stimulus to the response window's close (1.5 s)" }, StimulusTiming.Problems(2, null, 3, 1.5, 0, 0));
            CollectionAssert.AreEqual(new[] { "stimulus.responseToStimulusOffSeconds (2.5 s) is longer than the shortest outcome to next interval (0 s: timeout or reward consumption) plus itiMinSeconds (2 s): the stimulus could still be on at the next trial's" }, StimulusTiming.Problems(1, 2.5, 2, 3, 0, 0));
            Assert.AreEqual(0, StimulusTiming.Problems(1, 2.5, 2, 3, 0, 1).Count, "a timeout or consumption on every outcome leaves room");
            Assert.AreEqual(0, StimulusTiming.BetweenTrialsProblems(new double?[] { 1.0, 1.5, null }, new[] { 0.0, 0, 0 }, new double?[] { null, 1.5 }, new[] { 1.5, 3.0 }, 0).Count);
            CollectionAssert.AreEqual(new[] { "stimulus.durationSeconds (2 s) runs 2 s past the soonest outcome, longer than the shortest outcome to next interval (0 s: timeout or reward consumption) plus itiMinSeconds (1.5 s) of a combination that may come next: the stimulus could still be on at the next trial's" }, StimulusTiming.BetweenTrialsProblems(new double?[] { 1.0, 2.0 }, new[] { 0.0, 0 }, new double?[] { null }, new[] { 1.5, 3.0 }, 0));
            CollectionAssert.AreEqual(new[] { "stimulus.responseToStimulusOffSeconds (2 s) is longer than the shortest outcome to next interval (0 s: timeout or reward consumption) plus itiMinSeconds (1.5 s) of a combination that may come next: the stimulus could still be on at the next trial's" }, StimulusTiming.BetweenTrialsProblems(new double?[] { 1.0 }, new[] { 0.0 }, new double?[] { 0, 2 }, new[] { 1.5, 3.0 }, 0));
        }

        [TestMethod]
        public void TheStimulusMayRunPastTheIntervalByTheSoonestOutcome()
        {
            // duration 1.5 s, the window opening 0.5 s after the stimulus, itiMinSeconds 1, probes possible (0 after the outcome):
            // the next stimulus at the soonest 0.5 + 0 + 1 = 1.5 s after this one. The next trial's lead moves its start, not its stimulus.
            Assert.AreEqual(0, StimulusTiming.Problems(1.5, null, 1, 3, 0.5, 0).Count);
            Assert.AreEqual(1, StimulusTiming.Problems(1.6, null, 1, 3, 0.5, 0).Count);
            Assert.AreEqual(0, StimulusTiming.Problems(2.5, null, 1, 3, 0.5, 1).Count, "a 1 s consumption or timeout on every outcome");
            Assert.AreEqual(0, StimulusTiming.BetweenTrialsProblems(new double?[] { 1.5, 2.0 }, new[] { 0.5, 1.0 }, new double?[] { null }, new[] { 1.0, 2.0 }, 0).Count, "each duration against its own soonest outcome");
            Assert.AreEqual(1, StimulusTiming.BetweenTrialsProblems(new double?[] { 1.5, 2.0 }, new[] { 0.5, 0.5 }, new double?[] { null }, new[] { 1.0, 2.0 }, 0).Count);
        }

        [TestMethod]
        public void WithoutADurationNothingChanges()
        {
            var rig = LickedAt(new AfterOutcomeRules(), 102.3, 106);
            Assert.AreEqual(0, rig.Of(TrialEventKind.StimulusOff).Count());
            Assert.IsTrue(double.IsNaN(rig.Of(TrialEventKind.Outcome).First().Outcome.Times.StimulusOffTime));
            Assert.IsFalse(rig.Of(TrialEventKind.Update).Any(e => e.Update.StartsWith("after")), "no stimulus after the outcome");
        }

        [TestMethod]
        public void WithNoResponseOffTheStimulusRunsItsFullDuration()
        {
            var rig = LickedAt(new StubRules { Plan = Timed(1, null) }, 102.3, 106);
            var outcome = Of(rig, TrialEventKind.Outcome, 1).Single();
            var off = Of(rig, TrialEventKind.StimulusOff, 1).Single();
            Assert.AreEqual(103.001, off.Seconds, 1e-9, "onset + durationSeconds");
            Assert.AreEqual(103.001, outcome.Outcome.Times.StimulusOffTime, 1e-9, "known at the outcome");
        }

        [TestMethod]
        public void AtZeroTheStimulusGoesOffRightAfterTheOutcome()
        {
            var rig = LickedAt(new StubRules { Plan = Timed(1, 0) }, 102.3, 106);
            var outcome = rig.Events.FindIndex(e => e.Kind == TrialEventKind.Outcome);
            var off = rig.Events[outcome + 1];
            Assert.AreEqual(TrialEventKind.StimulusOff, off.Kind, "before the next trial's interval");
            Assert.AreEqual(rig.Events[outcome].Seconds, off.Seconds);
            Assert.AreEqual(off.Seconds, rig.Events[outcome].Outcome.Times.StimulusOffTime);
            Assert.AreEqual(TrialEventKind.Iti, rig.Events[outcome + 2].Kind);
            Assert.AreEqual(1, Of(rig, TrialEventKind.StimulusOff, 1).Count(), "never twice");
        }

        [TestMethod]
        public void AfterTheOutcomeTheStimulusGoesOnUntilItsOff()
        {
            var rules = new AfterOutcomeRules { Plan = Timed(1, 0.2) };
            var rig = LickedAt(rules, 102.3, 102.4);
            rig.Wheel(4);
            rig.Until(106);
            var off = Of(rig, TrialEventKind.StimulusOff, 1).Single();
            Assert.AreEqual(102.5, off.Seconds, 1e-9);
            var after = Of(rig, TrialEventKind.Update, 1).Where(e => e.Update.StartsWith("after")).ToList();
            Assert.AreEqual(102.301, after.First().Seconds, 1e-9, "from the first tick after the outcome");
            Assert.AreEqual(102.499, after.Last().Seconds, 1e-9, "never at or past the off");
            Assert.AreEqual(199 + 1, after.Count, "each tick, and the wheel");
            Assert.IsTrue(after.Any(e => e.Update == "after 1 4"), "the wheel's distance");
            Assert.IsTrue(rig.Events.IndexOf(after.Last()) < rig.Events.IndexOf(off));
        }

        [TestMethod]
        public void AStimulusOffBeforeTheOutcomeComesOnce()
        {
            var rig = LickedAt(new AfterOutcomeRules { Plan = Timed(0.5, 0) }, 103, 106);
            Assert.AreEqual(102.501, Of(rig, TrialEventKind.StimulusOff, 1).Single().Seconds, 1e-9);
            Assert.AreEqual(102.501, Of(rig, TrialEventKind.Outcome, 1).Single().Outcome.Times.StimulusOffTime, 1e-9);
            Assert.IsFalse(rig.Of(TrialEventKind.Update).Any(e => e.Update.StartsWith("after 1")));
        }

        [TestMethod]
        public void AnOutcomeBeforeTheStimulusHasNoOff()
        {
            // A 0.5 s lead: the start at 101.501 s, the stimulus due at 102.001 s; a lick at 101.7 s ends the trial first.
            var rig = LickedAt(new StubRules { Plan = Timed(1, 0, 0.5) }, 101.7, 101.8);
            Assert.AreEqual(1, Of(rig, TrialEventKind.Outcome, 1).Count());
            Assert.AreEqual(0, Of(rig, TrialEventKind.StimulusOff, 1).Count());
        }

        [TestMethod]
        public void TheStimulusAfterTheOutcomeStopsAtTheNextTrialsStart()
        {
            // A lick at 102.101 s: trial 2's interval from then, its start at 103.601 s (a 0.5 s lead), trial 1's off at 104.001 s.
            var rig = LickedAt(new AfterOutcomeRules { Plan = Timed(2, null, 0.5) }, 102.101, 104.5);
            var start = Of(rig, TrialEventKind.Start, 2).Single();
            Assert.AreEqual(103.601, start.Seconds, 1e-9);
            Assert.AreEqual(103.6, Of(rig, TrialEventKind.Update, 1).Last(e => e.Update.StartsWith("after")).Seconds, 1e-9);
            Assert.AreEqual(104.001, Of(rig, TrialEventKind.StimulusOff, 1).Single().Seconds, 1e-9, "the off still comes, before trial 2's stimulus");
            Assert.IsTrue(Of(rig, TrialEventKind.Stimulus, 2).Single().Seconds > 104.001);
        }

        [TestMethod]
        public void TheLoopRefusesABadStimulusTiming()
        {
            Func<TrialTiming, string> refused = timing =>
            {
                try { new LoopRig(new StubRules { Plan = n => timing }).Tick(); }
                catch (InvalidOperationException e) { return e.Message; }
                throw new AssertFailedException("no refusal");
            };
            StringAssert.Contains(refused(new TrialTiming { StimulusDurationSeconds = 0 }), "its stimulus duration is 0 s");
            StringAssert.Contains(refused(new TrialTiming { StimulusDurationSeconds = double.PositiveInfinity }), "not a finite number above 0");
            StringAssert.Contains(refused(new TrialTiming { StimulusDurationSeconds = 1, ResponseToStimulusOffSeconds = -1 }), "its responseToStimulusOffSeconds is -1 s");
            var overlap = new LoopRig(new StubRules { Plan = Timed(3, null) });
            overlap.Until(102.1);
            overlap.Lick(0);
            try { overlap.Until(106); Assert.Fail("no refusal"); }
            catch (InvalidOperationException e) { StringAssert.Contains(e.Message, "Trial 2's stimulus came at 104.1 s while trial 1's was still on until 105.001 s"); }
        }

        [TestMethod]
        public void WithNoDurationAndZeroTheStimulusGoesOffAtTheOutcome()
        {
            var rig = LickedAt(new StubRules { Plan = Timed(double.NaN, 0) }, 102.3, 106);
            var outcome = Of(rig, TrialEventKind.Outcome, 1).Single();
            Assert.AreEqual(outcome.Seconds, Of(rig, TrialEventKind.StimulusOff, 1).Single().Seconds);
            Assert.AreEqual(outcome.Seconds, outcome.Outcome.Times.StimulusOffTime);
        }

        [TestMethod]
        public void WithNoDurationTheStimulusRunsOnXSecondsAfterTheResponse()
        {
            // No cap: a miss at the window's end (103.501 s) counts as the response; off 0.7 s later, the stimulus updated until then.
            var rig = new LoopRig(new AfterOutcomeRules { Plan = Timed(double.NaN, 0.7) });
            rig.Until(106);
            var outcome = Of(rig, TrialEventKind.Outcome, 1).Single();
            Assert.AreEqual(103.501, outcome.Seconds, 1e-9);
            Assert.AreEqual(104.201, Of(rig, TrialEventKind.StimulusOff, 1).Single().Seconds, 1e-9);
            Assert.AreEqual(104.201, outcome.Outcome.Times.StimulusOffTime, 1e-9);
            Assert.AreEqual(104.2, Of(rig, TrialEventKind.Update, 1).Last(e => e.Update.StartsWith("after")).Seconds, 1e-9);
        }

        [TestMethod]
        public void WithNoDurationAndNoResponseOffNoOffIsTimed()
        {
            var rig = LickedAt(new AfterOutcomeRules { Plan = Timed(double.NaN, null) }, 102.3, 106);
            Assert.AreEqual(0, rig.Of(TrialEventKind.StimulusOff).Count());
            Assert.IsTrue(double.IsNaN(Of(rig, TrialEventKind.Outcome, 1).Single().Outcome.Times.StimulusOffTime));
            Assert.IsFalse(rig.Of(TrialEventKind.Update).Any(e => e.Update.StartsWith("after")));
        }
    }
}
