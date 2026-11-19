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
        public bool penaltySoundOn;
        public double penaltyTimeoutSeconds;
    }

    /// <summary>A task's outcome type derived from the lib's, as each dome's is: its members are inherited.</summary>
    public class PieceOutcome : TrialOutcome<PieceRow>
    {
        public PieceOutcome(PieceRow row) : base(row, "Left") { }
    }

    /// <summary>A trial with a laser condition.</summary>
    public class PieceTrial : IZapitTrial
    {
        public int TrialNumber { get; set; }
        public int LaserCondition { get; set; }
        public bool LaserOn { get; set; }
        public double LaserPowerMw { get; set; }
    }

    /// <summary>Rules with only a laser timing, as ZapitTrials reads them.</summary>
    public class PieceRules
    {
        public LaserTiming Laser { get; set; }
    }

    /// <summary>A task file as PlayPenaltySound reads it.</summary>
    public class PieceTaskConfig
    {
        public PieceTaskConfig() { TaskParameters = new PieceParameters(); }
        public PieceParameters TaskParameters { get; set; }
    }

    public class PieceParameters
    {
        public PieceParameters() { Penalty = new PiecePenalty(); }
        public PiecePenalty Penalty { get; set; }
    }

    public class PiecePenalty
    {
        public PiecePenalty() { Sound = new PieceSound(); }
        public PieceSound Sound { get; set; }
    }

    public class PieceSound
    {
        public double LevelDbfs { get; set; }
    }

    /// <summary>The task file at Go: a penalty level of -30 dBFS.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceTaskConfigSource
    {
        public IObservable<PieceTaskConfig> Process()
        {
            var config = new PieceTaskConfig();
            config.TaskParameters.Penalty.Sound.LevelDbfs = -30;
            return Observable.Return(config);
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

    /// <summary>Three outcomes 0.2 s apart from 0.5 s: trial 1 rewarded, 2 with the penalty sound, 3 with neither.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceOutcomes
    {
        public IObservable<PieceOutcome> Process()
        {
            var rows = new[]
            {
                new PieceRow { trialNumber = 1, outcome = TrialOutcomes.Correct, rewarded = true, rewardOfferedUl = 2.5 },
                new PieceRow { trialNumber = 2, outcome = TrialOutcomes.Incorrect, penaltySoundOn = true },
                new PieceRow { trialNumber = 3, outcome = TrialOutcomes.NoResponse },
            };
            return Observable.Timer(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.2)).Take(rows.Length)
                .Select(i => new PieceOutcome(rows[i]));
        }
    }

    /// <summary>
    /// The rules at 0.1 s, then trial 1's Arm (0.3 s), Trigger (0.4 s), Outcome (OutcomeAt s, none when 0) and TriggerOff
    /// (0.9 s), as TrialEvents. Stop: Duration (LaserTiming.ForDuration, 0.5 s), UntilOutcome (LaserMaxSeconds cap), or None.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceZapitRun
    {
        public string Stop { get; set; }
        public bool HardwareTriggered { get; set; }
        public double LaserMaxSeconds { get; set; }

        public IObservable<PieceRules> Process()
        {
            return Observable.Defer(() =>
            {
                var laser = Stop == "Duration" ? LaserTiming.ForDuration(HardwareTriggered, 0.5, 0.5)
                    : Stop == "UntilOutcome" ? LaserTiming.UntilOutcome(HardwareTriggered, 0, LaserMaxSeconds) : null;
                return Observable.Timer(TimeSpan.FromSeconds(0.1)).Select(_ => new PieceRules { Laser = laser });
            });
        }
    }

    /// <summary>Trial 1's events for PieceZapitRun's timeline.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class PieceZapitEvents
    {
        public double OutcomeAt { get; set; }

        public IObservable<TrialEvent<PieceTrial, string, PieceOutcome>> Process()
        {
            return Observable.Defer(() =>
            {
                var trial = new PieceTrial { TrialNumber = 1, LaserCondition = 3, LaserOn = true, LaserPowerMw = 2 };
                var outcome = new PieceOutcome(new PieceRow { trialNumber = 1, outcome = TrialOutcomes.Correct });
                var events = new List<Tuple<double, TrialEventKind>>
                {
                    Tuple.Create(0.3, TrialEventKind.Arm), Tuple.Create(0.4, TrialEventKind.Trigger), Tuple.Create(0.9, TrialEventKind.TriggerOff),
                };
                if (OutcomeAt > 0) events.Add(Tuple.Create(OutcomeAt, TrialEventKind.Outcome));
                return events.OrderBy(e => e.Item1).ToObservable().SelectMany(e => Observable.Timer(TimeSpan.FromSeconds(e.Item1)).Select(_ => Event(e.Item2, e.Item1, trial, outcome)));
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

        public IObservable<string> Process(IObservable<Timestamped<PieceRow>> source) { return source.Select(r => "row " + r.Value.trialNumber); }

        public IObservable<string> Process(IObservable<Tuple<string, double>> source) { return source.Select(r => "reward " + r.Item1 + " " + r.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        public IObservable<string> Process(IObservable<Mat> source) { return source.Select(m => "penalty " + m.Rows); }

        public IObservable<string> Process(IObservable<ZapitCommand> source)
        {
            return source.Select(c => "zapit " + c.Command + (c.Command == ZapitCommandType.StartStimulus
                ? " condition " + c.ConditionNumber + " hardware " + c.HardwareTriggered + " duration " + c.StimDuration.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
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

        // Each outcome's row goes into TrialRow; trial 1's reward into RewardRequests; trial 2's penalty sound into
        // PenaltySounds (the mixer's four channels); every outcome is passed on.
        [TestMethod]
        public void TrialOutcomeEffectsSendsTheRowTheRewardAndThePenaltySoundAndPassesTheOutcomeOn()
        {
            var rig = Rig();
            var outputs = Run("TrialOutcomeEffects.bonsai", 2, xml => xml.Replace("RIG_FILE", SecurityElement.Escape(rig)));
            Directory.Delete(Path.GetDirectoryName(rig), true);
            CollectionAssert.AreEquivalent(new[] { "row 1", "reward Left 2.5", "out 1", "row 2", "penalty 4", "out 2", "row 3", "out 3" }, outputs, string.Join(", ", outputs));
            Assert.IsTrue(outputs.IndexOf("penalty 4") > outputs.IndexOf("row 2"), "The penalty sound played before trial 2: " + string.Join(", ", outputs));
        }

        static List<string> RunZapit(string stop, bool hardwareTriggered, double laserMaxSeconds, double outcomeAt)
        {
            return Run("ZapitTrials.bonsai", 1.6, xml => xml.Replace("STOP", stop).Replace("HARDWARE", hardwareTriggered ? "true" : "false")
                .Replace("LASER_MAX", laserMaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("OUTCOME_AT", outcomeAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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

        // No outcome and no LaserCap: LaserBackstop stops it laserMaxSeconds after the trigger on the computer's clock.
        [TestMethod]
        public void ZapitTrialsUntilOutcomeWithNoOutcomeStopsAtTheBackstop()
        {
            CollectionAssert.AreEquivalent(new[] { "zapit StartStimulus condition 3 hardware True duration 0", "line set", "zapit StopStimulus", "line clear" },
                RunZapit("UntilOutcome", true, 0.2, 0));
        }

        // Rules with no laser: no Zapit command at all.
        [TestMethod]
        public void ZapitTrialsWithoutALaserSendsNoCommand()
        {
            CollectionAssert.AreEquivalent(new[] { "line set", "line clear" }, RunZapit("None", true, 0, 0.6));
        }
    }
}
