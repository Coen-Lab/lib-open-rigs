using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Zapit;

namespace OpenRigs.Trials.Tests
{
    /// <summary>The trial loop with ZapitDaq arming its laser trials (TrialLoopTests covers MATLAB Zapit's).</summary>
    [TestClass]
    public class TrialLoopZapitDaqTests
    {
        static StubRules Rules(double hold = 0, bool laserOn = true)
        {
            return new StubRules
            {
                Plan = n => new TrialTiming { ItiSeconds = 2, LaserToStimulusSeconds = 0.5, Laser = true, ResponseWindowSeconds = 30 },
                Laser = LaserTiming.UntilOutcome(hold, double.PositiveInfinity),
                LaserOn = laserOn,
                OnLick = (t, s, x, u) => StubRules.Outcome("Lick" + s, 0, x),
            };
        }

        static string Refusal(Action action)
        {
            try { action(); }
            catch (InvalidOperationException e) { return e.Message; }
            catch (ArgumentException e) { return e.Message; }
            throw new AssertFailedException("no refusal");
        }

        [TestMethod]
        public void ATrialArmedAtItsIntervalLogsZapitDaqsCappedPower()
        {
            var rig = new LoopRig(Rules(), zapitDaq: true);
            var arm = rig.RunTo(TrialEventKind.Arm);
            Assert.AreEqual(100.001, arm.Seconds, 1e-9, "armed at the interval's start");
            rig.Armed(arm.Trial, 1.5);
            rig.RunTo(TrialEventKind.Trigger);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            var times = rig.Of(TrialEventKind.Outcome).Single().Outcome.Times;
            Assert.AreEqual(1.5, times.LaserPowerMw, "the Armed event's CappedPowerMw, not the trial's 2 mW");
        }

        [TestMethod]
        public void MatlabZapitLeavesThePowerToTheTrial()
        {
            var rig = new LoopRig(new StubRules
            {
                Plan = n => new TrialTiming { ItiSeconds = 2, LaserToStimulusSeconds = 0.5, Laser = true, ResponseWindowSeconds = 30 },
                Laser = LaserTiming.UntilOutcome(true, 0, double.PositiveInfinity),
                OnLick = (t, s, x, u) => StubRules.Outcome("Lick" + s, 0, x),
            });
            rig.Reply(rig.RunTo(TrialEventKind.Arm).Trial);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            Assert.IsTrue(double.IsNaN(rig.Of(TrialEventKind.Outcome).Single().Outcome.Times.LaserPowerMw));
        }

        [TestMethod]
        public void AnArmedEventNotAsAskedOrMissingStopsTheTrialInZapitDaqsWords()
        {
            var wrong = new LoopRig(Rules(), zapitDaq: true);
            wrong.RunTo(TrialEventKind.Arm);
            var refusal = Refusal(() => wrong.Loop.OnZapitEvent(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Armed, Trial = 1, Condition = 2, LaserOn = true }));
            StringAssert.Contains(refusal, "ZapitDaq armed trial 1 (condition 2, laser on) when trial 1 asked for condition 1, laser on");

            var silent = new LoopRig(Rules(), zapitDaq: true);
            silent.RunTo(TrialEventKind.Arm);
            silent.Loop.OnZapitEvent(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Zeroed });   // not the loop's
            StringAssert.Contains(Refusal(() => silent.RunTo(TrialEventKind.Trigger)), "ZapitDaq had not armed trial 1 by its trigger at 101.501 s");
        }

        [TestMethod]
        public void AnArmLessThanTheArmTimeBeforeItsTriggerNamesZapitDaqsArmTime()
        {
            var rig = new LoopRig(Rules(1.4), zapitDaq: true);
            rig.Armed(rig.RunTo(TrialEventKind.Arm).Trial, 2);
            rig.RunTo(TrialEventKind.Trigger);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            rig.RunTo(TrialEventKind.Arm);
            StringAssert.Contains(Refusal(() => rig.RunTo(TrialEventKind.Trigger)), "less than ZapitDaq's 0.2 s arm time");
        }

        [TestMethod]
        public void ZapitDaqRefusesMatlabZapitsSoftwareTriggering()
        {
            var rules = new StubRules { Laser = LaserTiming.UntilOutcome(false, 0, double.PositiveInfinity) };
            StringAssert.Contains(Refusal(() => new TrialLoop<StubTrial, string, StubOutcome>(rules, true, true)), "ZapitDaq arms every trial before its trigger");
            Assert.IsTrue(LaserTiming.UntilOutcome(0.3, 5).HardwareTriggered);
            Assert.IsTrue(LaserTiming.ForDuration(0.3).HardwareTriggered);
        }

        [TestMethod]
        public void ObserveTakesZapitDaqsEvents()
        {
            var rules = new Subject<StubRules>();
            var clock = new Subject<double>();
            var lick = new Subject<bool>();
            var zapit = new Subject<ZapitDaqEvent>();
            var events = new List<TrialEvent<StubTrial, string, StubOutcome>>();
            Exception error = null;
            using (TrialLoop.Observe(rules, clock, new[] { lick }, zapit).Subscribe(events.Add, e => error = e))
            {
                rules.OnNext(Rules());
                var now = 100.0;
                Action tick = () => { now = Math.Round(now + 0.001, 6); clock.OnNext(now); };
                while (!events.Any(e => e.Kind == TrialEventKind.Arm)) tick();
                var trial = events.Single(e => e.Kind == TrialEventKind.Arm).Trial;
                zapit.OnNext(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Armed, Trial = trial.TrialNumber, Condition = trial.LaserCondition, LaserOn = true, CappedPowerMw = 2 });
                while (!events.Any(e => e.Kind == TrialEventKind.Stimulus) && error == null) tick();
            }
            Assert.IsNull(error, error == null ? "" : error.Message);
            Assert.AreEqual(1, events.Count(e => e.Kind == TrialEventKind.Trigger));
        }
    }
}
