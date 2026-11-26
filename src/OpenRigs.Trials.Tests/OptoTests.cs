using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Zapit;
using OpenRigs.Opto.ZapitDaq;
using OpenRigs.Opto.ZapitMatlab;
using OpenRigs.Zapit;

namespace OpenRigs.Trials.Tests
{
    /// <summary>An opto system that is not Zapit, as a future adapter's would be: the base's words, its own numbers.</summary>
    class OtherOptoSystem : OptoSystem
    {
        public override string Name { get { return "Other"; } }
        public override double ArmLeadSeconds { get { return 0.1; } }
        public override bool ConfirmsStart { get { return true; } }
        public override bool EndNeedsReply { get { return true; } }
        public override double EndReplyMarginSeconds { get { return 0.05; } }
    }

    /// <summary>The Opto port: the loop runs any IOptoSystem, and the two Zapit adapters map their own messages into it.</summary>
    [TestClass]
    public class OptoTests
    {
        static readonly IOptoSystem Other = new OtherOptoSystem();

        static StubRules Rules()
        {
            return new StubRules
            {
                Plan = n => new TrialTiming { ItiSeconds = 2, LaserToStimulusSeconds = 0.5, Laser = true, ResponseWindowSeconds = 1.5 },
                Laser = LaserTiming.UntilOutcome(true, 0.3, 10),
            };
        }

        static OptoFeedback Armed(StubTrial trial, double powerMw)
        {
            return new OptoFeedback { Kind = OptoFeedbackKind.Armed, Trial = trial.TrialNumber, Condition = trial.LaserCondition, On = trial.LaserOn, PowerMw = powerMw };
        }

        static string Refusal(Action action)
        {
            try { action(); }
            catch (Exception e) { return e.Message; }
            Assert.Fail("nothing was refused");
            return null;
        }

        // A system the loop has never heard of runs a laser trial end to end: armed in the interval, its Armed checked, its
        // Started logging the power it armed, its end-now reply holding the next Arm for its margin. TrialLoop needs no change.
        [TestMethod]
        public void TheLoopRunsAnOptoSystemThatIsNotZapit()
        {
            var rig = new LoopRig(Rules(), opto: Other) { StopReplySeconds = null };
            var arm = rig.RunTo(TrialEventKind.Arm);
            Assert.AreEqual(100.001, arm.Seconds, 1e-9, "armed at the interval's start, the last laser long done");
            Assert.AreEqual(0, arm.LaserSeconds, "a system's Arm of a laser until stopped carries its cap only when it says so (ArmCarriesLaserSeconds)");
            rig.Loop.OnOpto(Armed(arm.Trial, 1.5));
            var trigger = rig.RunTo(TrialEventKind.Trigger);
            rig.Loop.OnOpto(new OptoFeedback { Kind = OptoFeedbackKind.Started, Trial = trigger.Trial.TrialNumber });
            rig.Until(trigger.Seconds + 0.6);
            rig.Lick(0);
            var outcome = rig.Of(TrialEventKind.Outcome).Single().Outcome;
            Assert.IsTrue(outcome.Times.LaserStarted, "the system confirmed the start");
            Assert.AreEqual(1.5, outcome.Times.LaserPowerMw, 1e-9, "the power the system armed");
            // The outcome ended the laser now (UntilOutcome): the next Arm waits for the system's reply, then its 0.05 s margin.
            rig.Until(rig.Now + 1);
            Assert.AreEqual(1, rig.Of(TrialEventKind.Arm).Count(), "no Arm before the end's reply");
            Assert.IsTrue(rig.Loop.AwaitingStopReply);
            var reply = rig.Now;
            rig.Loop.OnOpto(new OptoFeedback { Kind = OptoFeedbackKind.Ended });
            var next = rig.RunTo(TrialEventKind.Arm);
            Assert.AreEqual(reply + 0.001 + 0.05, next.Seconds, 1e-9, "the reply, then the margin from the next tick");
        }

        // The stops are worded from the system's name (OptoSystem's defaults), and its Armed is checked.
        [TestMethod]
        public void AnOptoSystemsStopsNameIt()
        {
            var silent = new LoopRig(Rules(), opto: Other) { StopReplySeconds = null };
            StringAssert.Contains(Refusal(() => silent.RunTo(TrialEventKind.Trigger)), "Other had not armed trial 1 by its trigger");
            var wrong = new LoopRig(Rules(), opto: Other) { StopReplySeconds = null };
            var arm = wrong.RunTo(TrialEventKind.Arm);
            var asked = Armed(arm.Trial, 1);
            asked.Condition = 2;
            StringAssert.Contains(Refusal(() => wrong.Loop.OnOpto(asked)), "Other armed trial 1 (condition 2, light on) when trial 1 asked for condition 1, light on");
        }

        // A loop with no opto system refuses a laser trial, and ignores any opto message.
        [TestMethod]
        public void ALoopWithNoOptoSystemRefusesALaserTrial()
        {
            var loop = new TrialLoop<StubTrial, string, StubOutcome>(Rules(), true);
            loop.OnOpto(new OptoFeedback { Kind = OptoFeedbackKind.Ended });
            StringAssert.Contains(Refusal(() => loop.OnClock(100)), "it is a laser trial, but the loop has no opto system");
            Exception error = null;
            using (TrialLoop.Observe(Observable.Return(Rules()), new[] { 100.0 }.ToObservable(), new[] { Observable.Never<bool>() }).Subscribe(e => { }, e => error = e)) { }
            StringAssert.Contains(error.Message, "no opto system");
            Assert.ThrowsException<ArgumentNullException>(() => TrialLoop.Observe(Observable.Return(Rules()), Observable.Never<double>(), new[] { Observable.Never<bool>() }, null, Observable.Never<OptoFeedback>()));
        }

        [TestMethod]
        public void ZapitDaqsEventsMapToTheLoopsFeedback()
        {
            var armed = ZapitDaqOpto.ToOptoFeedback(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Armed, Trial = 4, Condition = 3, LaserOn = true, PowerMw = 5, CappedPowerMw = 4.2, TrialSeconds = 2.75 });
            Assert.AreEqual(OptoFeedbackKind.Armed, armed.Kind);
            Assert.AreEqual(4, armed.Trial);
            Assert.AreEqual(3, armed.Condition);
            Assert.IsTrue(armed.On);
            Assert.AreEqual(4.2, armed.PowerMw, 0, "the capped power, what ZapitDaq delivers");
            Assert.AreEqual(2.75, armed.PlaySeconds, 0);
            Assert.AreEqual(OptoFeedbackKind.Started, ZapitDaqOpto.ToOptoFeedback(new ZapitDaqEvent { Kind = ZapitDaqEventKind.Started, Trial = 4 }).Kind);
            foreach (var kind in new[] { ZapitDaqEventKind.Ended, ZapitDaqEventKind.Stopped, ZapitDaqEventKind.Zeroed })
                Assert.AreEqual(OptoFeedbackKind.Ended, ZapitDaqOpto.ToOptoFeedback(new ZapitDaqEvent { Kind = kind, Trial = 4 }).Kind, kind.ToString());
            foreach (var kind in new[] { ZapitDaqEventKind.BankLoaded, ZapitDaqEventKind.Refused, ZapitDaqEventKind.Pointed })
                Assert.IsNull(ZapitDaqOpto.ToOptoFeedback(new ZapitDaqEvent { Kind = kind }), kind.ToString());
            Assert.IsNull(ZapitDaqOpto.ToOptoFeedback(null));
        }

        [TestMethod]
        public void MatlabZapitsRepliesMapToTheLoopsFeedback()
        {
            var reply = new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = 3, LaserOn = 1 };
            var armed = ZapitMatlabOpto.ToOptoFeedback(reply);
            Assert.AreEqual(OptoFeedbackKind.Armed, armed.Kind);
            Assert.AreEqual(0, armed.Trial, "MATLAB Zapit's reply names no trial");
            Assert.AreEqual(3, armed.Condition);
            Assert.IsTrue(armed.On);
            Assert.AreSame(reply, armed.Source, "its CheckArmed reads the reply itself (an error reply is refused)");
            Assert.AreEqual(OptoFeedbackKind.Ended, ZapitMatlabOpto.ToOptoFeedback(new ZapitReply { Command = ZapitCommandType.StopStimulus }).Kind);
            Assert.IsNull(ZapitMatlabOpto.ToOptoFeedback(new ZapitReply { Command = ZapitCommandType.GetState }));
            Assert.IsNull(ZapitMatlabOpto.ToOptoFeedback(null));
        }

        // Each system supplies its Go numbers; the lead rule is one rule, worded as each system's own was.
        [TestMethod]
        public void EachZapitSuppliesItsGoLimits()
        {
            foreach (var lead in new[] { 3.1, 3.2, 3.3 })
            {
                Assert.AreEqual(ZapitTrial.LeadProblem(lead, 3.4), ZapitMatlabOptoSystem.Limits(0.5).LeadProblem(lead, 3.4));
                Assert.AreEqual(ZapitDaqTrial.LeadProblem(lead, 3.4), ZapitDaqOptoSystem.Limits(0.5).LeadProblem(lead, 3.4));
            }
            var matlab = ZapitMatlabOptoSystem.Limits(0.5);
            Assert.AreEqual(0.5 + ZapitTrial.ReplyMarginSeconds, matlab.OverheadSeconds, 1e-12, "the ramp-down plus the reply margin");
            var path = ZapitDaqTrialTests.UnpackBank();
            var bank = ZapitBank.Load(path);
            System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path), true);
            Assert.AreEqual(ZapitDaqTrial.FreeAfterLaserSeconds(bank), ZapitDaqOptoSystem.Limits(bank, false).BusyAfterEndSeconds, 0);
            Assert.AreEqual(ZapitDaqTrial.FreeAfterLaserSeconds(bank) + ZapitDaq.EarlyStopLeadSeconds, ZapitDaqOptoSystem.Limits(bank, true).BusyAfterEndSeconds, 0);
            Assert.AreEqual(ZapitDaqOptoSystem.Limits(bank, true).BusyAfterEndSeconds + ZapitDaqTrial.ArmSeconds, ZapitDaqOptoSystem.Limits(bank, true).OverheadSeconds, 1e-12);
        }

        // The two Zapits' policies, as the loop had them hard-coded before the port.
        [TestMethod]
        public void TheZapitsPoliciesAreTheLoopsOldOnes()
        {
            var daq = ZapitDaqOptoSystem.Instance;
            Assert.AreEqual(ZapitDaqTrial.ArmSeconds, daq.ArmLeadSeconds);
            Assert.IsTrue(daq.ConfirmsStart && daq.ArmCarriesLaserSeconds && !daq.EndNeedsReply);
            Assert.AreEqual(0.05, daq.StartedGraceSeconds);
            Assert.AreEqual(10 + Math.Max(2.0, 1.5 + 0.3) + ZapitDaqTrial.FreeMarginSeconds, daq.FreeAt(10, 1.5, 0.3, 2.0), 0);
            StringAssert.Contains(daq.TimingProblem(LaserTiming.ForDuration(false, 0.3)), "ZapitDaq arms every trial before its trigger");
            Assert.IsNull(daq.TimingProblem(LaserTiming.ForDuration(0.3)));
            var matlab = ZapitMatlabOptoSystem.Instance;
            Assert.AreEqual(ZapitTrial.ReplyMarginSeconds, matlab.ArmLeadSeconds);
            Assert.IsTrue(!matlab.ConfirmsStart && !matlab.ArmCarriesLaserSeconds && matlab.EndNeedsReply);
            Assert.AreEqual(0.25, matlab.EndReplyMarginSeconds);
            Assert.AreEqual(10 + 1.5 + 0.3, matlab.FreeAt(10, 1.5, 0.3, 2.0), 0);
            Assert.IsNull(matlab.TimingProblem(LaserTiming.ForDuration(false, 0.3)), "MATLAB Zapit runs on a rig without hardware triggering (the test mouse)");
        }
    }
}
