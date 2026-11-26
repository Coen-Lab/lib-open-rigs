using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Security;
using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;
using OpenRigs.Audio;
using OpenRigs.Devices.Zapit;
using OpenRigs.Tests;

namespace OpenRigs.Trials.Tests
{
    /// <summary>A trial row with the shared block TrialOutcome reads.</summary>
    public class PieceRow
    {
        public int trialNumber;
        public string outcome;
        public double outcomeTime;
        public bool rewarded;
        public double rewardOfferedUl;
        public bool penalised;
        public double penaltyTimeoutSeconds;
        public double penaltyFlashBrightness;
    }

    /// <summary>A task's outcome type derived from the lib's, as each task's is: its members are inherited.</summary>
    public class PieceOutcome : TrialOutcome<PieceRow>
    {
        public PieceOutcome(PieceRow row, double penaltyLevelDbfs = double.NaN) : base(row, "Left", penaltyLevelDbfs, 10) { }
    }

    /// <summary>A valve that plans each reward request (Present), or none, as when the stop has ended the valve's path.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PieceValve
    {
        public bool Present { get; set; }

        /// <summary>When the board answers the valve's write after the request, as RewardDelivered comes on a rig.</summary>
        public int AnswerAfterMs { get; set; }

        public IObservable<OpenRigs.Core.RewardPlan> Process(IObservable<Tuple<string, double>> source)
        {
            return source.Where(_ => Present).SelectMany(r => Observable.Return(new OpenRigs.Core.RewardPlan { RewardLine = new OpenRigs.Core.RewardLine { Instance = r.Item1 }, RequestedUl = r.Item2 })
                .Delay(TimeSpan.FromMilliseconds(AnswerAfterMs)));
        }
    }

    /// <summary>A trial with a laser condition.</summary>
    public class PieceTrial : IOptoTrial
    {
        public int TrialNumber { get; set; }
        public int LaserCondition { get; set; }
        public string LaserPointsMm { get; set; }
        public bool LaserOn { get; set; }
        public double LaserPowerMw { get; set; }
    }

    /// <summary>Rules with only a laser timing, as ZapitTrials reads them.</summary>
    public class PieceRules
    {
        public LaserTiming Laser { get; set; }
    }

    /// <summary>A task's trial rules as PlayPenaltySound reads them: the highest penalty level any trial draws (MaxPenaltyLevelDbfs).</summary>
    public class PieceSoundRules
    {
        public double MaxPenaltyLevelDbfs { get; set; }
    }

    /// <summary>The rules at Go: MaxPenaltyLevelDbfs -30 dBFS.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceSoundRulesSource
    {
        public IObservable<PieceSoundRules> Process()
        {
            return Observable.Return(new PieceSoundRules { MaxPenaltyLevelDbfs = -30 });
        }
    }

    /// <summary>A task's trial rules as PlayPenaltyFlash reads them: its flashes (PenaltyFlashes).</summary>
    public class PieceFlashRules
    {
        public PenaltyFlashRules PenaltyFlashes { get; set; }
    }

    /// <summary>The rules at Go: a flash with a timeout of TimeoutSeconds.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceFlashRulesSource
    {
        public double TimeoutSeconds { get; set; }

        public IObservable<PieceFlashRules> Process()
        {
            return Observable.Defer(() => Observable.Return(new PieceFlashRules { PenaltyFlashes = new PenaltyFlashRules { ShortestTimeoutSeconds = TimeoutSeconds, Where = "penalty.incorrect.timeoutSeconds in the test" } }));
        }
    }

    /// <summary>The speakers of RigFile, as measured on TEST-RIG.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceSpeakers
    {
        public string RigFile { get; set; }

        public IObservable<SpeakerSet> Process()
        {
            return Observable.Defer(() => Observable.Return(SpeakerSet.Load(RigFile, "TEST-RIG")));
        }
    }

    /// <summary>Three outcomes 0.2 s apart from 0.5 s: trial 1 rewarded, 2 penalised at -36 dBFS, 3 penalised with the sound mute.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceOutcomes
    {
        public IObservable<PieceOutcome> Process()
        {
            var rows = new[]
            {
                new PieceRow { trialNumber = 1, outcome = TrialOutcomes.Correct, rewarded = true, rewardOfferedUl = 2.5 },
                new PieceRow { trialNumber = 2, outcome = TrialOutcomes.Incorrect, penalised = true, penaltyFlashBrightness = 1 },
                new PieceRow { trialNumber = 3, outcome = TrialOutcomes.NoResponse, penalised = true },
            };
            var levels = new[] { -36, -36, double.NaN };
            return Observable.Timer(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.2)).Take(rows.Length)
                .Select(i => new PieceOutcome(rows[i], levels[i]));
        }
    }

    /// <summary>
    /// The rules at 0.1 s, then trial 1's Arm (0.3 s), Trigger (0.4 s), Outcome (OutcomeAt s, none when 0) and TriggerOff
    /// (0.9 s), as TrialEvents. Stop: Duration (LaserTiming.ForDuration, 0.5 s), UntilOutcome or UntilStimulusOff (LaserCapSeconds cap), or None.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceZapitRun
    {
        public string Stop { get; set; }
        public bool HardwareTriggered { get; set; }
        public bool ZapitDaq { get; set; }
        public double LaserCapSeconds { get; set; }

        public IObservable<PieceRules> Process()
        {
            return Observable.Defer(() =>
            {
                var hardwareTriggered = ZapitDaq || HardwareTriggered;
                var laser = Stop == "Duration" ? LaserTiming.ForDuration(hardwareTriggered, 0.5)
                    : Stop == "UntilOutcome" ? LaserTiming.UntilOutcome(hardwareTriggered, 0, LaserCapSeconds)
                    : Stop == "UntilStimulusOff" ? LaserTiming.UntilStimulusOff(hardwareTriggered, 0, LaserCapSeconds) : null;
                return Observable.Timer(TimeSpan.FromSeconds(0.1)).Select(_ => new PieceRules { Laser = laser });
            });
        }
    }

    /// <summary>A laser trial with its own laser duration, 0.5 s (LaserTiming.ForDuration).</summary>
    public class PieceTimedTrial : PieceTrial, IOptoDurationTrial
    {
        public double LaserDurationSeconds { get { return 0.5; } }
    }

    /// <summary>Trial 1's events for PieceZapitRun's timeline.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceZapitEvents
    {
        public double OutcomeAt { get; set; }

        /// <summary>Whether trial 1 has its own laser duration (a ForDuration task's trial).</summary>
        public bool Timed { get; set; }

        /// <summary>When trial 1's LaserOff comes, in seconds; none when 0.</summary>
        public double LaserOffAt { get; set; }

        /// <summary>When trial 1's LaserCap comes, in seconds; none when 0.</summary>
        public double LaserCapAt { get; set; }

        // Events at the same time come in one tick, in the order listed (as the loop emits Trigger, then LaserOff).
        public IObservable<TrialEvent<PieceTrial, string, PieceOutcome>> Process()
        {
            return Observable.Defer(() =>
            {
                var trial = Timed ? new PieceTimedTrial() : new PieceTrial();
                trial.TrialNumber = 1; trial.LaserCondition = 3; trial.LaserOn = true; trial.LaserPowerMw = 2;
                var outcome = new PieceOutcome(new PieceRow { trialNumber = 1, outcome = TrialOutcomes.Correct });
                var events = new List<Tuple<double, TrialEventKind>>
                {
                    Tuple.Create(0.3, TrialEventKind.Arm), Tuple.Create(0.4, TrialEventKind.Trigger), Tuple.Create(0.9, TrialEventKind.TriggerOff),
                };
                if (OutcomeAt > 0) events.Add(Tuple.Create(OutcomeAt, TrialEventKind.Outcome));
                if (LaserOffAt > 0) events.Add(Tuple.Create(LaserOffAt, TrialEventKind.LaserOff));
                if (LaserCapAt > 0) events.Add(Tuple.Create(LaserCapAt, TrialEventKind.LaserCap));
                return events.OrderBy(e => e.Item1).GroupBy(e => e.Item1).ToObservable()
                    .SelectMany(g => Observable.Timer(TimeSpan.FromSeconds(g.Key)).SelectMany(_ => g.Select(e => Event(e.Item2, e.Item1, trial, outcome))));
            });
        }

        static TrialEvent<PieceTrial, string, PieceOutcome> Event(TrialEventKind kind, double seconds, PieceTrial trial, PieceOutcome outcome)
        {
            var constructor = typeof(TrialEvent<PieceTrial, string, PieceOutcome>).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            return (TrialEvent<PieceTrial, string, PieceOutcome>)constructor.Invoke(new object[] { kind, seconds, trial, null, null, kind == TrialEventKind.Outcome ? outcome : null });
        }
    }

    /// <summary>The board's ZapitTrigger line, DO1.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceTriggerLine
    {
        public IObservable<DigitalOutputs> Process() { return Observable.Return(DigitalOutputs.DO1); }
    }

    /// <summary>Each thing a piece did, as text a test compares.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PieceDescribe
    {
        public IObservable<string> Process(IObservable<PieceOutcome> source) { return source.Select(o => "out " + o.Row.Value.trialNumber); }

        public IObservable<string> Process(IObservable<Timestamped<PieceRow>> source) { return source.Select(r => "row " + r.Value.trialNumber + (r.Value.rewarded ? " rewarded" : "")); }

        public IObservable<string> Process(IObservable<Tuple<string, double>> source) { return source.Select(r => "reward " + r.Item1 + " " + r.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        public IObservable<string> Process(IObservable<Mat> source) { return source.Select(m => "penalty " + m.Rows); }

        public IObservable<string> Process(IObservable<TimeSpan> source) { return source.Select(t => "flash lasts " + t.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        public IObservable<string> Process(IObservable<ZapitCommand> source)
        {
            return source.Select(c => "zapit " + c.Command + (c.Command == ZapitCommandType.StartStimulus
                ? " condition " + c.ConditionNumber + " hardware " + c.HardwareTriggered + " duration " + c.StimDuration.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
        }

        public IObservable<string> Process(IObservable<OpenRigs.Zapit.ZapitDaqCommand> source)
        {
            return source.Select(c => "zapit " + c.Kind + " trial " + c.Trial + (c.Kind == OpenRigs.Zapit.ZapitDaqCommandKind.Arm
                ? " condition " + c.Condition + " duration " + c.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
        }

        public IObservable<string> Process(IObservable<HarpMessage> source) { return source.Select(m => m.Address == OutputSet.Address ? "line set" : m.Address == OutputClear.Address ? "line clear" : "board " + m.Address); }
    }

    /// <summary>The Trials workflow pieces, each built and run in this process from Pieces\.</summary>
    [TestClass]
    public class PiecesTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(PiecesTests).Assembly.Location), "Pieces");

        // A rig.yml with two speakers (filters measured on TEST-RIG) and the penalty sound on channel 1, in a fresh folder.
        static string Rig()
        {
            var folder = Path.Combine(Path.GetTempPath(), "pieces-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "calibration"));
            foreach (var name in new[] { "Left", "Right" })
            {
                File.WriteAllText(Path.Combine(folder, "calibration", "Speaker-" + name + ".json"),
                    "{\"calibratedAt\": \"2026-11-14T00:00:00Z\", \"calibratedBy\": \"test\", \"machineName\": \"TEST-RIG\", \"sampleRate\": 48000, \"taps\": [0.25, 0.5, 0.25]}");
            }
            using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "penalty_tone.wav"))))
            {
                var samples = Enumerable.Range(0, 4800).Select(i => (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0))).ToArray();
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + 4 * samples.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)3); writer.Write((short)1); writer.Write(48000); writer.Write(48000 * 4); writer.Write((short)4); writer.Write((short)32);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(4 * samples.Length);
                foreach (var sample in samples) writer.Write(sample);
            }
            var rig = Path.Combine(folder, "rig.yml");
            File.WriteAllText(rig, "speakerArray:\n  device:\n    sampleRate: 48000\n    channelCount: 4\n  speakers:\n" +
                "    left:\n      position: {azimuth: -90.0, elevation: 15.0, radius: 1.0}\n      channel: 0\n      filter: calibration/Speaker-Left.json\n" +
                "    right:\n      position: {azimuth: 90.0, elevation: 15.0, radius: 1.0}\n      channel: 1\n      filter: calibration/Speaker-Right.json\n" +
                "  penaltySoundChannel: 1\n  penaltySound: penalty_tone.wav\n");
            return rig;
        }

        static List<string> Run(string file, double seconds, Func<string, string> edit)
        {
            var result = InProcessWorkflow.Run(edit(File.ReadAllText(Path.Combine(Folder, file))), seconds);
            Assert.IsNull(result.Item2, InProcessWorkflow.Innermost(result.Item2));
            return result.Item1;
        }

        static List<string> RunEffects(bool valve, int answerAfterMs = 10)
        {
            var rig = Rig();
            var outputs = Run("TrialOutcomeEffects.bonsai", 2, xml => xml.Replace("RIG_FILE", SecurityElement.Escape(rig)).Replace("VALVE_PRESENT", valve ? "true" : "false")
                .Replace("ANSWER_MS", answerAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Directory.Delete(Path.GetDirectoryName(rig), true);
            return outputs;
        }

        // Trial 1's reward goes into RewardRequests before its row goes into TrialRow; the row waits for the valve's
        // RewardDelivered, the board's answer 10 ms later, and is rewarded; trial 2's penalty sound into PenaltySounds (the
        // mixer's four channels); every outcome is passed on.
        [TestMethod]
        public void TrialOutcomeEffectsSendsTheRewardThenTheRowAndThePenaltySoundAndPassesTheOutcomeOn()
        {
            var outputs = RunEffects(true);
            CollectionAssert.AreEquivalent(new[] { "reward Left 2.5", "row 1 rewarded", "out 1", "row 2", "penalty 4", "out 2", "row 3", "out 3" }, outputs, string.Join(", ", outputs));
            Assert.IsTrue(outputs.IndexOf("reward Left 2.5") < outputs.IndexOf("row 1 rewarded"), "The row went before the reward: " + string.Join(", ", outputs));
            Assert.IsTrue(outputs.IndexOf("row 1 rewarded") < outputs.IndexOf("row 2"), "The rows went out of order: " + string.Join(", ", outputs));
            Assert.IsTrue(outputs.IndexOf("penalty 4") > outputs.IndexOf("row 2"), "The penalty sound played before trial 2: " + string.Join(", ", outputs));
        }

        // F12: a rewarded outcome no valve planned logs rewarded false.
        [TestMethod]
        public void TrialOutcomeEffectsLogsARewardNoValvePlannedAsNotRewarded()
        {
            var outputs = RunEffects(false);
            CollectionAssert.AreEquivalent(new[] { "reward Left 2.5", "row 1", "out 1", "row 2", "penalty 4", "out 2", "row 3", "out 3" }, outputs, string.Join(", ", outputs));
        }

        // F12: the valve planned the reward but the board never answered (a stop at the outcome) before the next outcome:
        // the row goes out then, not rewarded.
        [TestMethod]
        public void TrialOutcomeEffectsLogsARewardTheBoardNeverAnsweredAsNotRewarded()
        {
            var outputs = RunEffects(true, 60000);
            CollectionAssert.AreEquivalent(new[] { "reward Left 2.5", "row 1", "out 1", "row 2", "penalty 4", "out 2", "row 3", "out 3" }, outputs, string.Join(", ", outputs));
            Assert.IsTrue(outputs.IndexOf("row 1") < outputs.IndexOf("row 2"), "The rows went out of order: " + string.Join(", ", outputs));
        }

        static Tuple<List<string>, Exception> RunFlash(double timeoutSeconds)
        {
            var rig = Rig();
            var xml = File.ReadAllText(Path.Combine(Folder, "PlayPenaltyFlash.bonsai")).Replace("RIG_FILE", SecurityElement.Escape(rig))
                .Replace("TIMEOUT", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = InProcessWorkflow.Run(xml, 1.5);
            Directory.Delete(Path.GetDirectoryName(rig), true);
            return result;
        }

        // PlayPenaltyFlash, built as at Start with BonVision's DrawQuad: the penalty sound's 0.1 s fits the 4 s timeout, so
        // the flash lasts 0.1 s (PenaltyFlashDuration) and every outcome is passed on, trial 2's flash started (it draws
        // only with a window, which a test has none of).
        [TestMethod]
        public void PlayPenaltyFlashLastsThePenaltySoundAndPassesTheOutcomeOn()
        {
            var result = RunFlash(4);
            Assert.IsNull(result.Item2, InProcessWorkflow.Innermost(result.Item2));
            CollectionAssert.AreEquivalent(new[] { "flash lasts 0.1", "out 1", "out 2", "out 3" }, result.Item1, string.Join(", ", result.Item1));
        }

        // A timeout shorter than the penalty sound, with a flash: refused at Go, naming both.
        [TestMethod]
        public void PlayPenaltyFlashRefusesASoundLongerThanTheTimeout()
        {
            var result = RunFlash(0.05);
            StringAssert.Contains(InProcessWorkflow.Innermost(result.Item2), "the penalty sound lasts 0.1 s, longer than penalty.incorrect.timeoutSeconds in the test (0.05 s) with a flash");
        }

        static List<string> RunZapit(string stop, bool hardwareTriggered, double laserCapSeconds, double outcomeAt, double laserOffAt = 0, double laserCapAt = 0)
        {
            return Run("ZapitTrials.bonsai", 1.6, xml => xml.Replace("TIMED", stop == "Duration" || stop == "UntilStimulusOff" ? "true" : "false").Replace("STOP", stop).Replace("HARDWARE", hardwareTriggered ? "true" : "false")
                .Replace("LASER_CAP_AT", laserCapAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("LASER_CAP", laserCapSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("OUTCOME_AT", outcomeAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("LASER_OFF_AT", laserOffAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // ForDuration: the StartStimulus carries the duration, at Arm on a hardware-triggered rig; nothing stops it at the outcome.
        [TestMethod]
        public void ZapitTrialsForDurationSendsTheDurationAndNoStop()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit StartStimulus condition 3 hardware True duration 0.5", "line set", "line clear" },
                RunZapit("Duration", true, 0, 0.6));
        }

        // UntilOutcome on a rig without hardware triggering: the StartStimulus at the trigger, with no duration, and one
        // StopZapit at the outcome.
        [TestMethod]
        public void ZapitTrialsUntilOutcomeStopsAtTheOutcome()
        {
            CollectionAssert.AreEquivalent(new[] { "line set", "zapit StartStimulus condition 3 hardware False duration 0", "zapit StopStimulus", "line clear" },
                RunZapit("UntilOutcome", false, 10, 0.6));
        }

        // No outcome and no LaserCap: LaserBackstop stops it LaserCapSeconds after the trigger on the computer's clock.
        [TestMethod]
        public void ZapitTrialsUntilOutcomeWithNoOutcomeStopsAtTheBackstop()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit StartStimulus condition 3 hardware True duration 0", "line set", "zapit StopStimulus", "line clear" },
                RunZapit("UntilOutcome", true, 0.2, 0));
        }

        // UntilStimulusOff: the StartStimulus with no duration, though the trial has its own; the outcome does not stop it, its LaserOff does.
        [TestMethod]
        public void ZapitTrialsUntilStimulusOffStopsAtLaserOffNotTheOutcome()
        {
            CollectionAssert.AreEquivalent(new[] { "line set", "zapit StartStimulus condition 3 hardware False duration 0", "line clear" },
                RunZapit("UntilStimulusOff", false, 10, 0.6));
            CollectionAssert.AreEquivalent(new[] { "line set", "zapit StartStimulus condition 3 hardware False duration 0", "zapit StopStimulus", "line clear" },
                RunZapit("UntilStimulusOff", false, 10, 0.6, 0.7));
        }

        // UntilStimulusOff with the stimulus still on at the LaserCap: the cap stops it, and the LaserOff after it sends nothing more.
        [TestMethod]
        public void ZapitTrialsUntilStimulusOffStopsOnceAtTheLaserCap()
        {
            CollectionAssert.AreEquivalent(new[] { "line set", "zapit StartStimulus condition 3 hardware False duration 0", "zapit StopStimulus", "line clear" },
                RunZapit("UntilStimulusOff", false, 10, 0.5, 0.7, 0.6));
        }

        // UntilStimulusOff on a hardware-triggered rig: the StartStimulus at Arm with no duration, stopped at its LaserOff.
        [TestMethod]
        public void ZapitTrialsUntilStimulusOffOnAHardwareTriggeredRigArmsWithNoDuration()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit StartStimulus condition 3 hardware True duration 0", "line set", "zapit StopStimulus", "line clear" },
                RunZapit("UntilStimulusOff", true, 10, 0.5, 0.7));
        }

        // A stimulus already off at the trigger: the loop's Trigger and LaserOff in one tick; the laser is started, then stopped.
        [TestMethod]
        public void ZapitTrialsUntilStimulusOffStopsALaserOffInTheTriggersTick()
        {
            var outputs = RunZapit("UntilStimulusOff", false, 10, 0.6, 0.4);
            CollectionAssert.AreEquivalent(new[] { "line set", "zapit StartStimulus condition 3 hardware False duration 0", "zapit StopStimulus", "line clear" }, outputs, string.Join(", ", outputs));
            Assert.IsTrue(outputs.IndexOf("zapit StartStimulus condition 3 hardware False duration 0") < outputs.IndexOf("zapit StopStimulus"), "Stopped before it was started: " + string.Join(", ", outputs));
        }

        // Rules with no laser: no Zapit command, and the trigger line left alone even at a Trigger event, so a workflow can include
        // ZapitTrials and ZapitDaqTrials and give its rules to one per session (26 Nov 2026; it was written at every Trigger).
        [TestMethod]
        public void ZapitTrialsWithoutALaserSendsNoCommand()
        {
            CollectionAssert.AreEquivalent(new string[0], RunZapit("None", true, 0, 0.6));
        }

        // Every laser trial has its own duration (TIMED), so an Arm's duration 0 proves UntilStopped sent none.
        static List<string> RunZapitDaq(string stop, double laserCapSeconds, double outcomeAt, double laserOffAt = 0, double laserCapAt = 0)
        {
            return Run("ZapitDaqTrials.bonsai", 1.6, xml => xml.Replace("TIMED", stop != "None" ? "true" : "false").Replace("STOP", stop)
                .Replace("LASER_CAP_AT", laserCapAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("LASER_CAP", laserCapSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("OUTCOME_AT", outcomeAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("LASER_OFF_AT", laserOffAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // ZapitDaqTrials, ForDuration: the Arm carries the duration, at the Arm event; nothing stops it at the outcome. Every
        // trigger also sends ZapitDaq's Trigger (the simulated twin's edge; Audit F F4).
        [TestMethod]
        public void ZapitDaqTrialsForDurationArmsWithTheDurationAndNoStop()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0.5", "line set", "zapit Trigger trial 0", "line clear" }, RunZapitDaq("Duration", 0, 0.6));
        }

        // UntilStimulusOff: the Arm with no duration, though the trial has its own; the outcome does not stop it, its LaserOff does.
        [TestMethod]
        public void ZapitDaqTrialsUntilStimulusOffStopsAtLaserOffNotTheOutcome()
        {
            var outputs = RunZapitDaq("UntilStimulusOff", 10, 0.6);
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "line clear" }, outputs, string.Join(", ", outputs));
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "zapit EarlyStop trial 1", "line clear" }, RunZapitDaq("UntilStimulusOff", 10, 0.6, 0.7));
        }

        // UntilStimulusOff with the stimulus still on at the LaserCap: the cap stops it, and the LaserOff after it sends nothing more.
        [TestMethod]
        public void ZapitDaqTrialsUntilStimulusOffStopsOnceAtTheLaserCap()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "zapit EarlyStop trial 1", "line clear" }, RunZapitDaq("UntilStimulusOff", 10, 0.5, 0.7, 0.6));
        }

        // A stimulus already off at the trigger: the loop's Trigger and LaserOff in one tick; the early stop still comes.
        [TestMethod]
        public void ZapitDaqTrialsUntilStimulusOffStopsALaserOffInTheTriggersTick()
        {
            var outputs = RunZapitDaq("UntilStimulusOff", 10, 0.6, 0.4);
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "zapit EarlyStop trial 1", "line clear" }, outputs, string.Join(", ", outputs));
        }

        // UntilOutcome: the Arm with no duration, though the trial has its own, and one EarlyStop at the outcome.
        [TestMethod]
        public void ZapitDaqTrialsUntilOutcomeStopsEarlyAtTheOutcome()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "zapit EarlyStop trial 1", "line clear" }, RunZapitDaq("UntilOutcome", 10, 0.6));
        }

        // No outcome and no LaserCap: LaserBackstop stops it LaserCapSeconds after the trigger on the computer's clock.
        [TestMethod]
        public void ZapitDaqTrialsUntilOutcomeWithNoOutcomeStopsAtTheBackstop()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "zapit EarlyStop trial 1", "line clear" }, RunZapitDaq("UntilOutcome", 0.2, 0));
        }

        // No outcome, a 10 s cap: nothing stops it within the run. LaserBackstop was a Timer whose mapped DueTime never took,
        // so it fired at the trigger and every until-outcome laser stopped as it started (found 25 Nov 2026).
        [TestMethod]
        public void ZapitDaqTrialsBackstopWaitsForTheLaserCap()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit Arm trial 1 condition 3 duration 0", "line set", "zapit Trigger trial 0", "line clear" }, RunZapitDaq("UntilOutcome", 10, 0));
        }

        // As ZapitTrialsWithoutALaserSendsNoCommand: no Arm, no twin's Trigger and no line.
        [TestMethod]
        public void ZapitDaqTrialsWithoutALaserSendsNoCommand()
        {
            CollectionAssert.AreEquivalent(new string[0], RunZapitDaq("None", 0, 0.6));
        }
    }
}
