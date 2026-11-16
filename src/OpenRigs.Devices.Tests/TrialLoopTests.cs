using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Zapit;

namespace OpenRigs.Devices.Tests
{
    class StubTrial : IZapitTrial
    {
        public int TrialNumber { get; set; }
        public int LaserCondition { get; set; }
        public bool LaserOn { get; set; }
        public double LaserPowerMw { get; set; }
        public TrialTiming Timing;
    }

    class StubOutcome : ITrialOutcome
    {
        public string Choice;
        public double TimeoutSeconds { get; set; }
        public TrialTimes Times;
    }

    /// <summary>A task's rules made of delegates, each test giving the ones it needs; updates are strings.</summary>
    class StubRules : ITrialRules<StubTrial, string, StubOutcome>
    {
        public LaserTiming Laser { get; set; }
        public Func<int, TrialTiming> Plan = n => new TrialTiming { IntervalSeconds = 2, ResponseSeconds = 1.5 };
        public Func<StubTrial, TrialTimes, ICollection<string>, StubOutcome> OnTick = (t, x, u) => null;
        public Func<StubTrial, int, TrialTimes, ICollection<string>, StubOutcome> OnLick = (t, s, x, u) => Outcome("Lick" + s, 0, x);
        public Func<StubTrial, double, TrialTimes, ICollection<string>, StubOutcome> OnWheel = (t, cm, x, u) => null;
        public Func<StubTrial, TrialTimes, StubOutcome> OnResponseEnd = (t, x) => Outcome("NoResponse", 0, x);
        public bool LaserOn = true;
        public readonly List<StubOutcome> Previous = new List<StubOutcome>();
        public readonly List<double> StartWheels = new List<double>();
        int drawn;

        public static StubOutcome Outcome(string choice, double timeout, TrialTimes times)
        {
            return new StubOutcome { Choice = choice, TimeoutSeconds = timeout, Times = times };
        }

        public StubTrial Draw(StubOutcome previous)
        {
            Previous.Add(previous);
            var number = ++drawn;
            var timing = Plan(number);
            return new StubTrial { TrialNumber = number, Timing = timing, LaserCondition = timing.Laser ? 1 : 0, LaserOn = timing.Laser && LaserOn, LaserPowerMw = timing.Laser && LaserOn ? 2 : 0 };
        }

        public TrialTiming Timing(StubTrial trial) { return trial.Timing; }
        public void Start(StubTrial trial, TrialTimes times, double wheelCm) { StartWheels.Add(wheelCm); }
        public void Stimulus(StubTrial trial, TrialTimes times, ICollection<string> updates) { updates.Add("on " + trial.TrialNumber); }
        public StubOutcome Tick(StubTrial trial, TrialTimes times, ICollection<string> updates) { return OnTick(trial, times, updates); }
        public StubOutcome Lick(StubTrial trial, int spout, TrialTimes times, ICollection<string> updates) { return OnLick(trial, spout, times, updates); }
        public StubOutcome Wheel(StubTrial trial, double cm, TrialTimes times, ICollection<string> updates) { return OnWheel(trial, cm, times, updates); }
        public StubOutcome ResponseEnd(StubTrial trial, TrialTimes times) { return OnResponseEnd(trial, times); }
    }

    /// <summary>The loop on a stand-in Harp clock, 1 ms a tick from First, the licks, wheel and Zapit's replies put in by the test.</summary>
    class LoopRig
    {
        public readonly TrialLoop<StubTrial, string, StubOutcome> Loop;
        public readonly List<TrialEvent<StubTrial, string, StubOutcome>> Events = new List<TrialEvent<StubTrial, string, StubOutcome>>();
        public double Now;

        public LoopRig(StubRules rules, double first = 100.001)
        {
            Loop = new TrialLoop<StubTrial, string, StubOutcome>(rules);
            Now = Math.Round(first - 0.001, 6);
        }

        public void Tick(double step = 0.001)
        {
            Now = Math.Round(Now + step, 6);
            Events.AddRange(Loop.OnClock(Now));
        }

        public void Until(double seconds, Func<bool> stop = null)
        {
            while (Now < seconds - 1e-9 && (stop == null || !stop())) Tick();
        }

        public TrialEvent<StubTrial, string, StubOutcome> RunTo(TrialEventKind kind, double limit = 30)
        {
            var seen = Events.Count;
            Until(Now + limit, () => Events.Skip(seen).Any(e => e.Kind == kind));
            return Events.Skip(seen).First(e => e.Kind == kind);
        }

        public IEnumerable<TrialEvent<StubTrial, string, StubOutcome>> Of(TrialEventKind kind) { return Events.Where(e => e.Kind == kind); }
        public void Touch(int spout, bool touched) { Events.AddRange(Loop.OnLick(spout, touched)); }
        public void Lick(int spout) { Touch(spout, true); Touch(spout, false); }
        public void Wheel(double cm) { Events.AddRange(Loop.OnWheel(cm)); }

        public void Reply(StubTrial trial)
        {
            Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trial.LaserCondition, LaserOn = trial.LaserOn ? 1 : 0 });
        }
    }

    [TestClass]
    public class TrialLoopTests
    {
        static TrialTiming Timing(double lead = 0, bool laser = false, double response = 1.5)
        {
            return new TrialTiming { IntervalSeconds = 2, LaserToStimulusSeconds = lead, Laser = laser, ResponseSeconds = response };
        }

        static string Refusal(Action action)
        {
            try { action(); }
            catch (InvalidOperationException e) { return e.Message; }
            catch (ArgumentException e) { return e.Message; }
            throw new AssertFailedException("no refusal");
        }

        [TestMethod]
        public void TheFirstIntervalStartsAtTheFirstTickAndTheStimulusIsTheIntervalAfterIt()
        {
            var rules = new StubRules { Plan = n => new TrialTiming { IntervalSeconds = 2.0004, LaserToStimulusSeconds = 0.5, ResponseSeconds = 1.5 } };
            var rig = new LoopRig(rules);
            rig.Tick();
            var interval = rig.Of(TrialEventKind.Interval).Single();
            Assert.AreEqual(100.001, interval.Seconds, 1e-9);
            Assert.IsNull(rules.Previous[0], "the first draw has no previous outcome");
            var start = rig.RunTo(TrialEventKind.Start);
            Assert.AreEqual(100.001 + 2.0004 - 0.5, start.Seconds, 1e-9, "the start, exactly: the interval's end less the lead");
            Assert.AreEqual(101.502, rig.Now, 1e-9, "seen on the first tick after it");
            var stimulus = rig.RunTo(TrialEventKind.Stimulus);
            Assert.AreEqual(102.002, stimulus.Seconds, 1e-9, "the first tick at or after the interval after its start");
            var outcome = rig.RunTo(TrialEventKind.Outcome);
            Assert.AreEqual("NoResponse", outcome.Outcome.Choice);
            Assert.AreEqual(103.502, outcome.Seconds, 1e-9, "the window closes stimulusTime + 1.5 s, on the tick at or after it");
            var times = outcome.Outcome.Times;
            Assert.AreEqual(100.001, times.IntervalStartTime, 1e-9);
            Assert.AreEqual(2.0004, times.IntervalSeconds, 1e-12);
            Assert.AreEqual(0, times.QuietWaitSeconds, 1e-9);
            Assert.AreEqual(101.5014, times.TrialStartTime, 1e-9);
            Assert.AreEqual(102.0014, times.StimulusTime, 1e-9);
            Assert.AreEqual(102.002, times.StimulusOnsetTime, 1e-9);
            Assert.AreEqual(103.502, times.Now, 1e-9);
            Assert.IsTrue(double.IsNaN(times.LaserTriggerTime) && !times.LaserFired);
        }

        [TestMethod]
        public void TheSignedLeadPutsTheTriggerBeforeAtOrAfterTheStimulusAndAddsNoTime()
        {
            var plain = new LoopRig(new StubRules { Plan = n => Timing(0.5) }).RunTo(TrialEventKind.Start);
            foreach (var lead in new[] { 0.5, 0.0, -0.5 })
            {
                var rules = new StubRules { Plan = n => Timing(lead, true, double.PositiveInfinity), Laser = LaserTiming.UntilOutcome(true, 0, double.PositiveInfinity) };
                var rig = new LoopRig(rules);
                var arm = rig.RunTo(TrialEventKind.Arm);
                Assert.AreEqual(100.001, arm.Seconds, 1e-9, "armed at the interval's start");
                rig.Reply(arm.Trial);
                var start = rig.RunTo(TrialEventKind.Start);
                Assert.AreEqual(100.001 + 2 - Math.Max(lead, 0), start.Seconds, 1e-9);
                rig.Until(rig.Now + 1.0);
                var stimulus = rig.Of(TrialEventKind.Stimulus).Single().Seconds;
                var trigger = rig.Of(TrialEventKind.Trigger).Single().Seconds;
                Assert.AreEqual(102.001, stimulus, 1e-9, "the stimulus: the interval after its start, laser or not");
                Assert.AreEqual(102.001 - lead, trigger, 1e-9, "the trigger: laserToStimulusSeconds before the stimulus");
                if (lead == 0.5) Assert.AreEqual(plain.Seconds, start.Seconds, 1e-9, "the same start as without the laser");
            }
        }

        [TestMethod]
        public void ALickRestartsTheLickWaitOnEitherSpoutAndAHeldContactHoldsTheStart()
        {
            Func<TrialTiming> quiet = () => new TrialTiming { IntervalSeconds = 2, LaserToStimulusSeconds = 0.5, LickQuietSeconds = 1, ResponseSeconds = 1.5 };
            var still = new LoopRig(new StubRules { Plan = n => quiet() }, 10.001);
            Assert.AreEqual(11.501, still.RunTo(TrialEventKind.Start).Seconds, 1e-9, "quiet from the interval's start");

            var licked = new LoopRig(new StubRules { Plan = n => quiet() }, 10.001);
            licked.Until(11.0);
            licked.Touch(0, true);
            licked.Until(11.2);
            licked.Touch(0, false);
            licked.Until(11.4);
            licked.Lick(1);                                     // the right spout restarts it too
            var start = licked.RunTo(TrialEventKind.Start);
            Assert.AreEqual(12.4, start.Seconds, 1e-9);
            var outcome = licked.RunTo(TrialEventKind.Outcome).Outcome.Times;
            Assert.AreEqual(12.4 - 11.501, outcome.QuietWaitSeconds, 1e-9);
            Assert.AreEqual(12.9, outcome.StimulusTime, 1e-9, "the lead stays exact after the wait");

            var held = new LoopRig(new StubRules { Plan = n => quiet() }, 10.001);
            held.Until(10.5);
            held.Touch(0, true);
            held.Until(13.0);
            Assert.IsFalse(held.Of(TrialEventKind.Start).Any(), "no start while the spout is held");
            held.Touch(0, false);
            Assert.AreEqual(14.0, held.RunTo(TrialEventKind.Start).Seconds, 1e-9);

            var off = new LoopRig(new StubRules { Plan = n => Timing(0.5) }, 10.001);
            off.Until(10.5);
            off.Touch(0, true);
            Assert.AreEqual(11.501, off.RunTo(TrialEventKind.Start).Seconds, 1e-9, "with the lick wait off a held contact does not matter");
        }

        [TestMethod]
        public void TheWheelWaitCountsTheLastWheelQuietSeconds()
        {
            Func<TrialTiming> quiet = () => new TrialTiming { IntervalSeconds = 2, LaserToStimulusSeconds = 0.5, WheelQuietSeconds = 1, WheelQuietCm = 1, ResponseSeconds = 1.5 };
            var rock = new LoopRig(new StubRules { Plan = n => quiet() }, 10.001);
            rock.Wheel(0);
            rock.Until(11.0);
            rock.Wheel(0.6);
            rock.Until(11.3);
            rock.Wheel(0);                                       // net 0, but 1.2 cm within a second: the start a second after the first swing
            Assert.AreEqual(12.0, rock.RunTo(TrialEventKind.Start).Seconds, 1e-9);

            var fidget = new LoopRig(new StubRules { Plan = n => quiet() }, 10.001);
            fidget.Wheel(0);
            for (int i = 1; i <= 4; i++)
            {
                fidget.Until(Math.Round(9.8 + 0.4 * i, 3));
                fidget.Wheel(0.3 * i);                           // 1.2 cm in all, never more than 0.9 cm within a second
            }
            Assert.AreEqual(11.501, fidget.RunTo(TrialEventKind.Start).Seconds, 1e-9);
        }

        [TestMethod]
        public void AHardwareTriggeredTrialMustBeArmedByItsTriggerAsItAsked()
        {
            Func<StubRules> rules = () => new StubRules { Plan = n => Timing(0.5, true), Laser = LaserTiming.UntilOutcome(true, 0, double.PositiveInfinity) };
            var silent = new LoopRig(rules());
            silent.RunTo(TrialEventKind.Arm);
            silent.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StopStimulus });   // not the loop's
            StringAssert.Contains(Refusal(() => silent.RunTo(TrialEventKind.Trigger)), "had not answered trial 1's StartStimulus by its trigger at 101.501 s");

            var wrong = new LoopRig(rules());
            wrong.RunTo(TrialEventKind.Arm);
            var refusal = Refusal(() => wrong.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 2, LaserOn = 1 }));
            StringAssert.Contains(refusal, "Zapit answered trial 1's StartStimulus (condition 1, laserOn 1)", "ZapitTrial.CheckArmed's words");
        }

        [TestMethod]
        public void WithoutHardwareTriggeringTheTriggerSendsAndItsReplyIsCheckedBeforeTheNext()
        {
            var rules = new StubRules { Plan = n => Timing(0.5, true), Laser = LaserTiming.UntilOutcome(false, 0, double.PositiveInfinity) };
            var rig = new LoopRig(rules);
            var trigger = rig.RunTo(TrialEventKind.Trigger);
            Assert.IsFalse(rig.Of(TrialEventKind.Arm).Any(), "never armed ahead: Zapit would start the laser on receipt");
            rig.Reply(trigger.Trial);
            rig.RunTo(TrialEventKind.Outcome);
            rig.RunTo(TrialEventKind.Trigger);                   // trial 2's reply never comes
            StringAssert.Contains(Refusal(() => rig.RunTo(TrialEventKind.Trigger)), "Zapit never answered trial 2's StartStimulus");

            var wrong = new LoopRig(new StubRules { Plan = n => Timing(0.5, true), Laser = LaserTiming.UntilOutcome(false, 0, double.PositiveInfinity) });
            wrong.RunTo(TrialEventKind.Trigger);
            StringAssert.Contains(Refusal(() => wrong.Loop.OnZapitReply(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 1, LaserOn = 0 })), "laserOn=0");
        }

        [TestMethod]
        public void ATrialAnsweredBeforeItsTriggerGetsNoLaserAndALaserOffTrialIsTriggered()
        {
            var rules = new StubRules { Plan = n => Timing(-0.5, true), Laser = LaserTiming.UntilOutcome(true, 0, double.PositiveInfinity) };
            var rig = new LoopRig(rules);
            rig.Reply(rig.RunTo(TrialEventKind.Arm).Trial);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Until(rig.Now + 0.1);
            rig.Lick(0);
            var answered = rig.Of(TrialEventKind.Outcome).Single().Outcome.Times;
            Assert.IsFalse(answered.LaserFired);
            Assert.IsTrue(double.IsNaN(answered.LaserTriggerTime));
            rig.Until(rig.Now + 1);
            Assert.IsFalse(rig.Of(TrialEventKind.Trigger).Any(e => e.Trial.TrialNumber == 1), "no trigger after the answer");

            var off = new StubRules { Plan = n => Timing(-0.5, true), Laser = LaserTiming.UntilOutcome(true, 0, double.PositiveInfinity), LaserOn = false };
            var dark = new LoopRig(off);
            dark.Reply(dark.RunTo(TrialEventKind.Arm).Trial);
            var trigger = dark.RunTo(TrialEventKind.Trigger);
            Assert.IsFalse(trigger.Trial.LaserOn);
            dark.Lick(0);
            var times = dark.Of(TrialEventKind.Outcome).Single().Outcome.Times;
            Assert.IsTrue(times.LaserFired);
            Assert.AreEqual(trigger.Seconds, times.LaserTriggerTime, 1e-12, "the line rose, at 0 mW: the galvos and masking light");
        }

        [TestMethod]
        public void UntilOutcomeTheLineFallsTheHoldAfterTheOutcomeAndTheNextIsArmedThen()
        {
            var rules = new StubRules { Plan = n => Timing(0.1, true, 30), Laser = LaserTiming.UntilOutcome(true, 0.3, 12.5) };
            var rig = new LoopRig(rules);
            rig.Reply(rig.RunTo(TrialEventKind.Arm).Trial);
            var trigger = rig.RunTo(TrialEventKind.Trigger);
            var cap = rig.RunTo(TrialEventKind.LaserCap);
            Assert.AreEqual(trigger.Seconds + 12.5, cap.Seconds, 1e-9, "the laser's longest run, on the Harp clock");
            rig.Lick(0);
            var outcome = rig.Of(TrialEventKind.Outcome).Single();
            Assert.AreEqual(1, rig.Of(TrialEventKind.LaserCap).Count(), "one cap");
            var next = rig.RunTo(TrialEventKind.Arm);
            var off = rig.Of(TrialEventKind.TriggerOff).Single();
            Assert.AreEqual(outcome.Seconds + 0.3, off.Seconds, 1e-9, "the line held triggerHoldSeconds past the outcome");
            Assert.AreEqual(outcome.Seconds + 0.3, next.Seconds, 1e-9, "the next armed once the line has fallen");
            Assert.AreEqual(2, next.Trial.TrialNumber);
            Assert.AreEqual(1, off.Trial.TrialNumber);
        }

        [TestMethod]
        public void UntilOutcomeTheLineFallsOnATrialEndedBeforeItsTrigger()
        {
            var rig = new LoopRig(new StubRules { Plan = n => Timing(-0.5, true), Laser = LaserTiming.UntilOutcome(true, 0.3, 12.5) });
            rig.Reply(rig.RunTo(TrialEventKind.Arm).Trial);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            var outcome = rig.Of(TrialEventKind.Outcome).Single();
            Assert.AreEqual(outcome.Seconds + 0.3, rig.RunTo(TrialEventKind.TriggerOff).Seconds, 1e-9, "every optogenetics outcome clears the line");
        }

        [TestMethod]
        public void ForDurationTheLineFallsAfterItAndTheNextIsArmedAfterTheRampDown()
        {
            var rules = new StubRules
            {
                Plan = n => new TrialTiming { IntervalSeconds = 5, LaserToStimulusSeconds = 0.5, Laser = true, ResponseSeconds = 1.5 },
                Laser = LaserTiming.ForDuration(true, 2.5, 0.5),
            };
            var rig = new LoopRig(rules);
            rig.Reply(rig.RunTo(TrialEventKind.Arm).Trial);
            var trigger = rig.RunTo(TrialEventKind.Trigger);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Until(rig.Now + 0.2);
            rig.Lick(1);                                         // answered 0.7 s into the laser, no timeout
            var outcome = rig.Of(TrialEventKind.Outcome).Single();
            Assert.AreEqual(outcome.Seconds, rig.Of(TrialEventKind.Interval).Last().Seconds, 1e-12, "the next interval at the outcome");
            var off = rig.RunTo(TrialEventKind.TriggerOff);
            Assert.AreEqual(trigger.Seconds + 2.5, off.Seconds, 1e-9, "the line up laserDurationSeconds, outcome or not");
            var next = rig.RunTo(TrialEventKind.Arm);
            Assert.AreEqual(trigger.Seconds + 3.0, next.Seconds, 1e-9, "the next armed once the ramp-down has played");
            Assert.IsFalse(rig.Of(TrialEventKind.LaserCap).Any());
        }

        [TestMethod]
        public void ForDurationATrialAnsweredBeforeItsTriggerFreesZapitAtItsTriggerMoment()
        {
            var rules = new StubRules
            {
                Plan = n => new TrialTiming { IntervalSeconds = 0.4, LaserToStimulusSeconds = -0.5, Laser = true, ResponseSeconds = 1.5 },
                Laser = LaserTiming.ForDuration(true, 2.5, 0.5),
            };
            var rig = new LoopRig(rules);
            rig.Reply(rig.RunTo(TrialEventKind.Arm).Trial);
            var stimulus = rig.RunTo(TrialEventKind.Stimulus);
            rig.Until(rig.Now + 0.1);
            rig.Lick(0);
            var next = rig.RunTo(TrialEventKind.Arm);
            Assert.AreEqual(stimulus.Seconds + 0.5, next.Seconds, 1e-9, "armed once the unanswered trigger's moment has passed");
            Assert.IsFalse(rig.Of(TrialEventKind.TriggerOff).Any(), "the line never rose");
        }

        [TestMethod]
        public void LicksCountOnlyInTheResponseWindowAndItsEndIsTheRulesOutcome()
        {
            var seen = new List<bool>();
            var rules = new StubRules
            {
                Plan = n => new TrialTiming { IntervalSeconds = 2, ResponseDelaySeconds = 0.5, ResponseSeconds = 1.5 },
                OnLick = (t, s, x, u) => { seen.Add(x.Responding); return x.Responding ? StubRules.Outcome("Left", 0, x) : null; },
            };
            var rig = new LoopRig(rules);
            var stimulus = rig.RunTo(TrialEventKind.Stimulus);
            rig.Until(stimulus.Seconds + 0.2);
            rig.Lick(0);
            rig.Until(stimulus.Seconds + 0.6);
            rig.Lick(0);
            CollectionAssert.AreEqual(new[] { false, true }, seen);
            var outcome = rig.Of(TrialEventKind.Outcome).Single();
            Assert.AreEqual(stimulus.Seconds + 0.6, outcome.Seconds, 1e-9, "the outcome at the latest tick");
            Assert.AreEqual(stimulus.Seconds + 0.5, outcome.Outcome.Times.ResponseStartTime, 1e-9);

            var miss = new LoopRig(new StubRules { Plan = n => new TrialTiming { IntervalSeconds = 2, ResponseDelaySeconds = 0.5, ResponseSeconds = 1.5 } });
            var shown = miss.RunTo(TrialEventKind.Stimulus);
            Assert.AreEqual(shown.Seconds + 2.0, miss.RunTo(TrialEventKind.Outcome).Seconds, 1e-9, "no response: the window's end");

            var endless = new LoopRig(new StubRules { Plan = n => Timing(0, false, double.PositiveInfinity) });
            endless.Until(200);
            Assert.IsFalse(endless.Of(TrialEventKind.Outcome).Any(), "no limit, no outcome");
        }

        [TestMethod]
        public void TheNextIntervalStartsExactlyAtTheOutcomePlusItsTimeout()
        {
            var rules = new StubRules { OnLick = (t, s, x, u) => StubRules.Outcome("Incorrect", 4.0004, x) };
            var rig = new LoopRig(rules);
            rig.RunTo(TrialEventKind.Stimulus);
            rig.Lick(0);
            var outcome = rig.Of(TrialEventKind.Outcome).Single();
            rig.Until(outcome.Seconds + 4.0);
            Assert.AreEqual(1, rig.Of(TrialEventKind.Interval).Count(), "the timeout first");
            var interval = rig.RunTo(TrialEventKind.Interval);
            Assert.AreEqual(outcome.Seconds + 4.0004, interval.Seconds, 1e-9, "exactly, not the tick it was drawn on");
            Assert.AreEqual(outcome.Seconds + 4.001, rig.Now, 1e-9);
            Assert.AreSame(outcome.Outcome, rules.Previous[1], "the draw sees the last outcome");
            var start = rig.RunTo(TrialEventKind.Start);
            Assert.AreEqual(outcome.Seconds + 4.0004 + 2, start.Seconds, 1e-9);

            var quick = new LoopRig(new StubRules());
            quick.RunTo(TrialEventKind.Stimulus);
            quick.Lick(0);
            var events = quick.Events.Skip(quick.Events.Count - 2).ToList();
            Assert.AreEqual(TrialEventKind.Outcome, events[0].Kind);
            Assert.AreEqual(TrialEventKind.Interval, events[1].Kind, "no timeout: drawn at once");
            Assert.AreEqual(events[0].Seconds, events[1].Seconds, 1e-12);
            Assert.AreEqual(2, events[1].Trial.TrialNumber);
        }

        [TestMethod]
        public void CuesComeAtTheirTimesOutcomeOrNot()
        {
            var rules = new StubRules
            {
                Plan = n => new TrialTiming
                {
                    IntervalSeconds = 3,
                    ResponseSeconds = 1.5,
                    Cues = new[] { new TrialCue("SpoutsIn", CueAnchor.Stimulus, 0.5), new TrialCue("GratingOff", CueAnchor.Stimulus, 1.5), new TrialCue("Drink", CueAnchor.NextInterval, 1.5) },
                },
                OnLick = (t, s, x, u) => StubRules.Outcome("Left", 1, x),
            };
            var rig = new LoopRig(rules);
            var stimulus = rig.RunTo(TrialEventKind.Stimulus);
            rig.Until(stimulus.Seconds + 0.6);
            rig.Lick(0);
            var outcome = rig.Of(TrialEventKind.Outcome).Single();
            rig.Until(outcome.Seconds + 3);
            var cues = rig.Of(TrialEventKind.Cue).Where(e => e.Trial.TrialNumber == 1).ToDictionary(e => e.Cue, e => e.Seconds);
            Assert.AreEqual(stimulus.Seconds + 0.5, cues["SpoutsIn"], 1e-9);
            Assert.AreEqual(stimulus.Seconds + 1.5, cues["GratingOff"], 1e-9, "after the outcome too");
            Assert.AreEqual(outcome.Seconds + 1 + 1.5, cues["Drink"], 1e-9, "from the next interval's start");

            var early = new StubRules
            {
                Plan = n => new TrialTiming { IntervalSeconds = 2, LaserToStimulusSeconds = 0.5, ResponseSeconds = 1.5, Cues = new[] { new TrialCue("SpoutsIn", CueAnchor.Stimulus, 0), new TrialCue("Drink", CueAnchor.NextInterval, 0.2) } },
                OnWheel = (t, cm, x, u) => cm > 10 ? StubRules.Outcome("EndOfTrack", 0, x) : null,
            };
            var ended = new LoopRig(early);
            ended.RunTo(TrialEventKind.Start);
            ended.Wheel(20);                                     // ended before its stimulus
            ended.Until(ended.Now + 1);
            Assert.IsTrue(double.IsNaN(ended.Of(TrialEventKind.Outcome).Single().Outcome.Times.StimulusOnsetTime));
            CollectionAssert.AreEqual(new[] { "Drink" }, ended.Of(TrialEventKind.Cue).Where(e => e.Trial.TrialNumber == 1).Select(e => e.Cue).ToList(), "no stimulus, no stimulus cues");
        }

        [TestMethod]
        public void TheStimulusComesBeforeItsUpdatesAndTheOutcomeAfterThem()
        {
            var rules = new StubRules { Plan = n => Timing(0.5), OnTick = (t, x, u) => { u.Add("tick"); return StubRules.Outcome("Reached", 0, x); } };
            var rig = new LoopRig(rules);
            var stimulus = rig.RunTo(TrialEventKind.Stimulus);
            var tick = rig.Events.Where(e => e.Seconds == stimulus.Seconds && e.Trial.TrialNumber == 1).Select(e => e.Kind + (e.Update ?? "")).ToList();
            CollectionAssert.AreEqual(new[] { "Stimulus", "Updateon 1", "Updatetick", "Outcome" }, tick);
        }

        [TestMethod]
        public void AClosedLoopSeesEveryTickFromTheStimulusAndTheWheelFromTheStart()
        {
            var ticks = new List<double>();
            var rules = new StubRules
            {
                Plan = n => Timing(0.1, false, double.PositiveInfinity),
                OnTick = (t, x, u) => { ticks.Add(x.Now); return null; },
                OnWheel = (t, cm, x, u) => { u.Add("at " + cm); return null; },
            };
            var rig = new LoopRig(rules);
            rig.Wheel(40);
            var start = rig.RunTo(TrialEventKind.Start);
            Assert.AreEqual(40, rules.StartWheels.Single(), "the track starts from the wheel's latest distance");
            rig.Wheel(41);
            Assert.AreEqual("at 41", rig.Events.Last().Update, "the wheel reaches the rules from the start, before the stimulus");
            rig.Until(start.Seconds + 0.2);
            Assert.AreEqual(start.Seconds + 0.1, ticks[0], 1e-9);
            Assert.AreEqual(101, ticks.Count);
            rig.Tick(0.3);                                       // a gap in the clock: one tick
            Assert.AreEqual(102, ticks.Count);
        }

        [TestMethod]
        public void ABackwardClockOrABadWheelOrTimingStopsTheTask()
        {
            var rig = new LoopRig(new StubRules());
            rig.Tick();
            StringAssert.Contains(Refusal(() => rig.Loop.OnClock(99)), "went backwards");
            StringAssert.Contains(Refusal(() => rig.Loop.OnWheel(double.NaN)), "NaN");

            Func<TrialTiming, string> refused = timing => Refusal(() => new LoopRig(new StubRules { Plan = n => timing }).Tick());
            StringAssert.Contains(refused(new TrialTiming { IntervalSeconds = 0.4, LaserToStimulusSeconds = 0.5 }), "longer than its interval");
            StringAssert.Contains(refused(new TrialTiming { IntervalSeconds = double.NaN }), "its interval is NaN");
            StringAssert.Contains(refused(new TrialTiming { IntervalSeconds = 2, Laser = true }), "no LaserTiming");
            StringAssert.Contains(refused(new TrialTiming { IntervalSeconds = 2, ResponseSeconds = 0 }), "response window");
            StringAssert.Contains(refused(new TrialTiming { IntervalSeconds = 2, WheelQuietSeconds = 1 }), "wheelQuietSeconds");
            StringAssert.Contains(Refusal(() => LaserTiming.UntilOutcome(true, -1, 10)), "triggerHoldSeconds");
            StringAssert.Contains(Refusal(() => LaserTiming.ForDuration(true, 0, 0.5)), "laserDurationSeconds");
            StringAssert.Contains(Refusal(() => new TrialCue("Drink", CueAnchor.NextInterval, double.NaN)), "Drink");

            var forever = new LoopRig(new StubRules { OnLick = (t, s, x, u) => StubRules.Outcome("Left", double.PositiveInfinity, x) });
            forever.RunTo(TrialEventKind.Stimulus);
            StringAssert.Contains(Refusal(() => forever.Lick(0)), "Trial 1's outcome has a timeout of");
            var silent = new LoopRig(new StubRules { OnResponseEnd = (t, x) => null });
            StringAssert.Contains(Refusal(() => silent.RunTo(TrialEventKind.Outcome)), "no outcome when its response window closed");
        }

        [TestMethod]
        public void ObserveRunsTheLoopOnTheWorkflowsStreams()
        {
            var rules = new Subject<StubRules>();   // a task's own rules type, as its RunTrials passes them: the type arguments are inferred
            var clock = new Subject<double>();
            var wheel = new Subject<double>();
            var left = new Subject<bool>();
            var right = new Subject<bool>();
            var replies = new Subject<ZapitReply>();
            var events = new List<TrialEvent<StubTrial, string, StubOutcome>>();
            Exception error = null;
            var stub = new StubRules { Plan = n => Timing(0.5, true), Laser = LaserTiming.UntilOutcome(true, 0, double.PositiveInfinity) };
            using (TrialLoop.Observe(rules, clock, wheel, left, right, replies).Subscribe(events.Add, e => error = e))
            {
                rules.OnNext(stub);
                var now = 50.0;
                Action until = () => { now = Math.Round(now + 0.001, 6); clock.OnNext(now); };
                while (!events.Any(e => e.Kind == TrialEventKind.Arm)) until();
                var trial = events.Single(e => e.Kind == TrialEventKind.Arm).Trial;
                replies.OnNext(new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = trial.LaserCondition, LaserOn = 1 });
                while (!events.Any(e => e.Kind == TrialEventKind.Stimulus)) until();
                right.OnNext(true);
                Assert.AreEqual("Lick1", events.Single(e => e.Kind == TrialEventKind.Outcome).Outcome.Choice, "the right detector is spout 1");
                CollectionAssert.AreEqual(new[] { TrialEventKind.Interval, TrialEventKind.Arm, TrialEventKind.Start, TrialEventKind.Trigger, TrialEventKind.Stimulus },
                    events.Where(e => e.Trial.TrialNumber == 1 && e.Kind != TrialEventKind.Update && e.Kind != TrialEventKind.Outcome).Select(e => e.Kind).ToList());
                clock.OnNext(1);
                StringAssert.Contains(error.Message, "went backwards");
            }
        }

        [TestMethod]
        public void PickTrialEventsPassesOneKindOrOneCue()
        {
            var rig = new LoopRig(new StubRules { Plan = n => new TrialTiming { IntervalSeconds = 2, ResponseSeconds = 1.5, Cues = new[] { new TrialCue("SpoutsIn", CueAnchor.Stimulus, 0.5), new TrialCue("GratingOff", CueAnchor.Stimulus, 1.5) } } });
            rig.Until(106);
            var source = rig.Events.ToObservable();
            Assert.AreEqual(rig.Of(TrialEventKind.Start).Count(), new PickTrialEvents { Kind = TrialEventKind.Start }.Process(source).ToEnumerable().Count());
            var spouts = new PickTrialEvents { Kind = TrialEventKind.Cue, Cue = "SpoutsIn" }.Process(source).ToEnumerable().ToList();
            Assert.IsTrue(spouts.Count > 0 && spouts.All(e => e.Cue == "SpoutsIn"));
            StringAssert.Contains(Refusal(() => new PickTrialEvents { Kind = TrialEventKind.Start, Cue = "SpoutsIn" }.Process(source).ToEnumerable().ToList()), "not Cue");
        }
    }
}
