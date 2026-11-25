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
            rig.Started(arm.Trial);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            var times = rig.Of(TrialEventKind.Outcome).Single().Outcome.Times;
            Assert.AreEqual(1.5, times.LaserPowerMw, "the Armed event's CappedPowerMw, not the trial's 2 mW");
        }

        // Audit 24 Nov 2026 C1: the trigger line went up but ZapitDaq never generated the trial (no Started; it ends Stopped
        // or Zeroed with 0 samples), so the row must not claim the laser fired.
        [TestMethod]
        public void ATrialZapitDaqNeverStartedLogsNoPower()
        {
            var rig = new LoopRig(Rules(), zapitDaq: true);
            var arm = rig.RunTo(TrialEventKind.Arm);
            rig.Armed(arm.Trial, 1.5);
            rig.RunTo(TrialEventKind.Trigger);
            rig.Loop.OnZapitEvent(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Zeroed, Trial = arm.Trial.TrialNumber });
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            var times = rig.Of(TrialEventKind.Outcome).Single().Outcome.Times;
            Assert.IsTrue(times.LaserFired, "the trigger line went up");
            Assert.AreEqual(0, times.LaserPowerMw);
        }

        // Audit C R4: an outcome within StartedGraceSeconds of the trigger (a trigger inside the response window) comes before
        // ZapitDaq's Started can, so the row logs the armed power; later with no Started it logs 0 mW.
        [TestMethod]
        public void AnOutcomeJustAfterTheTriggerLogsThePowerBeforeStartedComes()
        {
            foreach (var (wait, power) in new[] { (0.01, 1.5), (0.1, 0.0) })
            {
                var rules = Rules();
                rules.Plan = n => new TrialTiming { ItiSeconds = 2, LaserToStimulusSeconds = -0.2, Laser = true, ResponseWindowSeconds = 30 };
                var rig = new LoopRig(rules, zapitDaq: true);
                var arm = rig.RunTo(TrialEventKind.Arm);
                rig.Armed(arm.Trial, 1.5);
                rig.RunTo(TrialEventKind.Trigger);
                rig.Until(rig.Now + wait);
                rig.Lick(0);
                var times = rig.Of(TrialEventKind.Outcome).Single().Outcome.Times;
                Assert.AreEqual(power, times.LaserPowerMw, $"outcome {wait} s after the trigger");
            }
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
            var offTiming = LaserTiming.UntilStimulusOff(0.3, 5);
            Assert.IsTrue(offTiming.HardwareTriggered && offTiming.End == LaserEnd.AtStimulusOff && offTiming.UntilStopped);
            new TrialLoop<StubTrial, string, StubOutcome>(new StubRules { Laser = offTiming }, true, true);
        }

        // Audit F F1: the loop armed the next ForDuration trial at trigger + duration + ramp, while the real ZapitDaq, started
        // by the line's edge after the loop's trigger tick, was still playing its whole cycles and ramp: the workflow stopped
        // ("cannot arm while trial 1 still plays"). The twin is triggered from the loop's Trigger event a tick late, as a rig's
        // edge comes after the tick. The old moment is refused; the loop's (trigger + TrialSeconds + FreeMarginSeconds) is not.
        [TestMethod]
        public void TheNextTimedTrialArmsOnlyOnceZapitDaqHasPlayedTheLast()
        {
            var bank = ZapitDaqTrialTests.UnpackBank();
            double clock = 0;
            var device = new SimulatedZapitDevice { AutoTrigger = false, Clock = () => System.Threading.Volatile.Read(ref clock) };
            var node = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 3, EarlyStopLeadSeconds = 0.05, TriggerTerminal = "/Dev1/PFI0", CeilingMw = 1e9, LaserMaxVolts = 5, LaserCeilingVolts = 5, DeviceFactory = () => device };
            var commands = new Subject<ZapitDaqCommand>();
            var zapitEvents = new System.Collections.Concurrent.ConcurrentQueue<ZapitDaqEvent>();
            Exception error = null;
            var rules = new StubRules
            {
                Plan = n => new TrialTiming { ItiSeconds = 3, LaserToStimulusSeconds = 0.5, Laser = true, ResponseWindowSeconds = 30 },
                Laser = LaserTiming.ForDuration(0.25),
                OnLick = (t, s, x, u) => StubRules.Outcome("Lick" + s, 0, x),
            };
            var rig = new LoopRig(rules, zapitDaq: true);
            using (node.Process(commands).Subscribe(zapitEvents.Enqueue, e => error = e))
            {
                commands.OnNext(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.LoadBank, Path = bank });
                Func<TrialEventKind, TrialEvent<StubTrial, string, StubOutcome>> runTo = kind =>
                {
                    var seen = rig.Events.Count;
                    bool trigger = false;
                    while (!rig.Events.Skip(seen).Any(e => e.Kind == kind) && error == null)
                    {
                        var before = rig.Events.Count;
                        rig.Tick();
                        System.Threading.Volatile.Write(ref clock, rig.Now);
                        if (trigger) commands.OnNext(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Trigger });   // the edge, a tick late
                        trigger = false;
                        foreach (var e in rig.Events.Skip(before))
                        {
                            if (e.Kind == TrialEventKind.Arm) commands.OnNext(ZapitDaqTrial.Arm(e.Trial));
                            if (e.Kind == TrialEventKind.Trigger) trigger = true;
                        }
                        while (zapitEvents.TryDequeue(out var z)) rig.Loop.OnZapitEvent(z);
                    }
                    return rig.Events.Skip(seen).FirstOrDefault(e => e.Kind == kind);
                };
                runTo(TrialEventKind.Arm);
                var trigger1 = runTo(TrialEventKind.Stimulus).Seconds - 0.5;
                rig.Lick(0);
                var next = runTo(TrialEventKind.Arm);
                Assert.IsNull(error, error?.Message);
                Assert.AreEqual(trigger1 + 2.75 + ZapitDaqTrial.FreeMarginSeconds, next.Seconds, 0.0015, "trigger + 100 whole 25 ms cycles and the 250 ms ramp + the margin");
                runTo(TrialEventKind.Trigger);
                Assert.IsNull(error, error?.Message);
            }

            // The old moment, trigger + laserDurationSeconds + ramp, with the edge a tick after the trigger tick.
            var old = new SimulatedZapitDevice { AutoTrigger = false, Clock = () => System.Threading.Volatile.Read(ref clock) };
            var oldNode = new ZapitDaq { SampleRate = 100000, MaxTrialSeconds = 3, EarlyStopLeadSeconds = 0.05, TriggerTerminal = "/Dev1/PFI0", CeilingMw = 1e9, LaserMaxVolts = 5, LaserCeilingVolts = 5, DeviceFactory = () => old };
            Exception stop = null;
            using (oldNode.Process(commands).Subscribe(_ => { }, e => stop = e))
            {
                clock = 0;
                commands.OnNext(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.LoadBank, Path = bank });
                commands.OnNext(ZapitDaqTrial.Arm(new StubTrial { TrialNumber = 1, LaserCondition = 1, LaserOn = true, LaserPowerMw = 2 }));
                clock = 10.001;
                commands.OnNext(new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Trigger });
                clock = 10 + 2.5 + 0.25;
                commands.OnNext(ZapitDaqTrial.Arm(new StubTrial { TrialNumber = 2, LaserCondition = 2, LaserOn = false }));
            }
            Assert.IsNotNull(stop, "the old moment");
            StringAssert.Contains(stop.Message, "Trial 2 cannot arm while trial 1 still plays");
        }

        // Audit F F4: a Started before the trial's trigger (a stray edge, or a device that starts at arming) stops the
        // workflow; after it, the row says laserStarted.
        [TestMethod]
        public void AStartedBeforeTheTriggerStopsTheWorkflowAndOneAfterItIsLogged()
        {
            var early = new LoopRig(Rules(), zapitDaq: true);
            var arm = early.RunTo(TrialEventKind.Arm);
            early.Armed(arm.Trial, 1.5);
            StringAssert.Contains(Refusal(() => early.Started(arm.Trial)), "ZapitDaq started trial 1 before the workflow raised its trigger");

            var rig = new LoopRig(Rules(), zapitDaq: true);
            arm = rig.RunTo(TrialEventKind.Arm);
            rig.Armed(arm.Trial, 1.5);
            rig.RunTo(TrialEventKind.Trigger);
            rig.Started(arm.Trial);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            Assert.IsTrue(rig.Of(TrialEventKind.Outcome).Single().Outcome.Times.LaserStarted);
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
