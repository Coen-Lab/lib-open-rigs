using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices;

namespace OpenRigs.Trials.Tests
{
    class SpoutTrial : ISpoutTrial
    {
        public int TrialNumber { get; set; }
        public bool SpoutsAlwaysIn { get; set; }
        public LickSpoutCommand SpoutsInCommand { get; set; }
    }

    /// <summary>A two-spout task's rules: 2 s intervals, the window 0.5 s after the stimulus for 1.5 s, the spouts in 0.5 s
    /// after it; a lick in the window rewarded at its spout, the spouts out RewardConsumptionSeconds into the next interval, else noResponse after Timeout.</summary>
    class SpoutRules : ITrialRules<SpoutTrial, string, TrialOutcome<PieceRow>>
    {
        public Func<int, bool> AlwaysIn = n => false;
        public Func<int, LickSpoutCommand> InCommand = n => LickSpoutCommand.SpoutsIn;
        public double RewardConsumptionSeconds = 1.5;
        public double Timeout;
        public bool AlwaysFull;                                 // TrialTiming.ResponseWindowAlwaysFull
        public string LickOutcome = TrialOutcomes.Correct;   // correct rewarded, incorrect penalised with Timeout, probe neither
        public int Licks;                                    // the licks the rules were given
        public Func<int, LickSpoutCommand?> Bias = n => null; // each trial's outcome's SpoutBiasCommand
        int drawn;

        public LaserTiming Laser { get { return null; } }
        public SpoutTrial Draw(TrialOutcome<PieceRow> previous) { var n = ++drawn; return new SpoutTrial { TrialNumber = n, SpoutsAlwaysIn = AlwaysIn(n), SpoutsInCommand = InCommand(n) }; }
        public TrialTiming Timing(SpoutTrial trial)
        {
            return new TrialTiming
            {
                ItiSeconds = 2, ResponseDelaySeconds = 0.5, ResponseWindowSeconds = 1.5, ResponseWindowAlwaysFull = AlwaysFull,
                Cues = Spouts.Cues(0.5, trial.SpoutsAlwaysIn, RewardConsumptionSeconds),
            };
        }
        public void Start(SpoutTrial trial, TrialTimes times, double wheelCm) { }
        public void Stimulus(SpoutTrial trial, TrialTimes times, ICollection<string> updates) { }
        public TrialOutcome<PieceRow> Tick(SpoutTrial trial, TrialTimes times, ICollection<string> updates) { return null; }
        public TrialOutcome<PieceRow> Wheel(SpoutTrial trial, double cm, TrialTimes times, ICollection<string> updates) { return null; }
        public TrialOutcome<PieceRow> Lick(SpoutTrial trial, int spout, TrialTimes times, ICollection<string> updates)
        {
            Licks++;
            if (!times.Responding) return null;
            var penalised = LickOutcome == TrialOutcomes.Incorrect;
            var row = new PieceRow { trialNumber = trial.TrialNumber, outcome = LickOutcome, outcomeTime = times.Now, rewarded = LickOutcome == TrialOutcomes.Correct, rewardOfferedUl = 3, penalised = penalised, penaltyTimeoutSeconds = penalised ? Timeout : 0 };
            return new TrialOutcome<PieceRow>(row, spout == 0 ? "left" : "right", double.NaN, 10) { SpoutBiasCommand = Bias(trial.TrialNumber) };
        }
        public TrialOutcome<PieceRow> ResponseEnd(SpoutTrial trial, TrialTimes times)
        {
            var row = new PieceRow { trialNumber = trial.TrialNumber, outcome = TrialOutcomes.NoResponse, outcomeTime = times.Now, penalised = Timeout > 0, penaltyTimeoutSeconds = Timeout };
            return new TrialOutcome<PieceRow>(row, "left", double.NaN, 10) { SpoutBiasCommand = Bias(trial.TrialNumber) };
        }
    }

    [TestClass]
    public class SpoutsTests
    {
        readonly List<TrialEvent<SpoutTrial, string, TrialOutcome<PieceRow>>> events = new List<TrialEvent<SpoutTrial, string, TrialOutcome<PieceRow>>>();
        TrialLoop<SpoutTrial, string, TrialOutcome<PieceRow>> loop;
        double now = 100;

        void Run(SpoutRules rules, double until, params double[] leftLicksAt)
        {
            loop = loop ?? new TrialLoop<SpoutTrial, string, TrialOutcome<PieceRow>>(rules, false);
            while (now < until - 1e-9)
            {
                now = Math.Round(now + 0.001, 6);
                events.AddRange(loop.OnClock(now));
                if (leftLicksAt.Any(t => Math.Abs(t - now) < 1e-9)) Lick(0);
            }
        }

        void Lick(int spout)
        {
            events.AddRange(loop.OnLick(spout, true));
            events.AddRange(loop.OnLick(spout, false));
        }

        IList<LickSpoutCommand> Commands()
        {
            return new SpoutCommands().Process(events.ToObservable()).ToList().Wait();
        }

        /// <summary>Each event with the moves it made.</summary>
        List<Tuple<TrialEvent<SpoutTrial, string, TrialOutcome<PieceRow>>, LickSpoutCommand[]>> Moves()
        {
            var subject = new Subject<TrialEvent<SpoutTrial, string, TrialOutcome<PieceRow>>>();
            var made = new List<LickSpoutCommand>();
            var moves = new List<Tuple<TrialEvent<SpoutTrial, string, TrialOutcome<PieceRow>>, LickSpoutCommand[]>>();
            using (new SpoutCommands().Process(subject).Subscribe(made.Add))
            {
                foreach (var e in events)
                {
                    made.Clear();
                    subject.OnNext(e);
                    moves.Add(Tuple.Create(e, made.ToArray()));
                }
            }
            return moves;
        }

        [TestMethod]
        public void MovingSpoutsStartOutAndEachOutcomeAndSpoutsOutMoveThem()
        {
            // Trial 1's interval from 100.001 s: the spouts out; SpoutsIn at 102.501 s; a left lick at 102.7 s leaves the left
            // spout alone in; trial 2's interval starts at the outcome, and SpoutsOut draws the spout out 1.5 s into it.
            Run(new SpoutRules(), 104.5, 102.7);
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, LickSpoutCommand.SpoutsIn, LickSpoutCommand.LeftOnly, LickSpoutCommand.SpoutsOut }, Commands().ToArray());
            var outCue = events.Single(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue);
            var iti = events.Single(e => e.Kind == TrialEventKind.Iti && e.Trial.TrialNumber == 2).Seconds;
            Assert.AreEqual(102.7, iti, 1.5e-3, "the interval starts at the outcome: consumption delays nothing");
            Assert.AreEqual(iti + 1.5, outCue.Seconds, 1.5e-3, "rewardConsumptionSeconds into the interval");
        }

        [TestMethod]
        public void ARightRewardLeavesTheRightSpoutAlone()
        {
            Run(new SpoutRules(), 102.7);
            Lick(1);
            Assert.AreEqual(LickSpoutCommand.RightOnly, Commands().Last());
        }

        [TestMethod]
        public void AfterATimeoutSpoutsOutComesRewardConsumptionIntoTheNextInterval()
        {
            // A miss at 104.001 s and a 4 s timeout: the next interval at 108.001 s, SpoutsOut 1.5 s into it.
            Run(new SpoutRules { Timeout = 4 }, 110);
            var outcome = events.Single(e => e.Kind == TrialEventKind.Outcome);
            Assert.AreEqual(outcome.Seconds + 4, events.Single(e => e.Kind == TrialEventKind.Iti && e.Trial.TrialNumber == 2).Seconds, 1.5e-3);
            Assert.AreEqual(outcome.Seconds + 4 + 1.5, events.Single(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue).Seconds, 1.5e-3);
        }

        [TestMethod]
        public void WhenTheSpoutsAlwaysInOnlyTheSpoutsInCueMovesThem()
        {
            Run(new SpoutRules { AlwaysIn = n => true }, 120);
            var spoutsIn = events.Where(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.InCue).ToList();
            Assert.IsTrue(spoutsIn.Count >= 2 && events.Any(e => e.Cue == Spouts.OutCue));
            CollectionAssert.AreEqual(spoutsIn.Select(e => LickSpoutCommand.SpoutsIn).ToArray(), Commands().ToArray());
        }

        [TestMethod]
        public void ATrialWhoseSpoutsAlwaysInBetweenMovingOnesIsNeverWithdrawn()
        {
            // Every third trial is always in, from trial 2, every trial a miss: an always-in trial's outcome moves nothing and its
            // SpoutsIn sends its own command; each SpoutsOut cue draws the spouts out only before a trial that moves.
            Run(new SpoutRules { AlwaysIn = n => n % 3 == 2, InCommand = n => n % 2 == 0 ? LickSpoutCommand.SpoutsIn : LickSpoutCommand.RightOnly }, 150);
            SpoutTrial coming = null;
            var outs = 0;
            foreach (var m in Moves())
            {
                var e = m.Item1;
                var expected = new LickSpoutCommand[0];
                if (e.Kind == TrialEventKind.Iti && e.Trial.TrialNumber == 1) expected = new[] { LickSpoutCommand.SpoutsOut };
                if (e.Kind == TrialEventKind.Cue && e.Cue == Spouts.InCue) expected = new[] { e.Trial.SpoutsInCommand };
                if (e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue && !coming.SpoutsAlwaysIn) { expected = new[] { LickSpoutCommand.SpoutsOut }; outs++; }
                if (e.Kind == TrialEventKind.Outcome && !e.Trial.SpoutsAlwaysIn) expected = new[] { LickSpoutCommand.SpoutsOut };
                CollectionAssert.AreEqual(expected, m.Item2, e.Kind + " " + e.Cue + " of trial " + e.Trial.TrialNumber);
                if (e.Kind == TrialEventKind.Iti) coming = e.Trial;
            }
            Assert.IsTrue(coming.TrialNumber > 10 && outs > 3);
            Assert.IsTrue(events.Any(e => e.Cue == Spouts.OutCue && e.Trial.TrialNumber == 1), "trial 1's SpoutsOut falls before trial 2, which is always in");
        }

        [TestMethod]
        public void ATrialOneAlwaysInIsNotDrawnOutAtItsIntervalStart()
        {
            Run(new SpoutRules { AlwaysIn = n => n == 1 }, 103);
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsIn }, Commands().ToArray());
        }

        [TestMethod]
        public void AtRewardConsumptionZeroTheNextTrialIsDrawnBeforeTheSpoutsOutCue()
        {
            // SpoutsOut is due at the next interval's start; the loop draws that interval's trial first (its Iti event), so the
            // coming trial is the one whose alwaysIn decides.
            Run(new SpoutRules { RewardConsumptionSeconds = 0, AlwaysIn = n => n == 2 }, 106);
            var cue = events.FindIndex(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue && e.Trial.TrialNumber == 1);
            var next = events.FindIndex(e => e.Kind == TrialEventKind.Iti && e.Trial.TrialNumber == 2);
            Assert.IsTrue(next >= 0 && next < cue);
            Assert.AreEqual(0, Moves()[cue].Item2.Length, "trial 2 is always in");
        }

        [TestMethod]
        public void OneSpoutAloneIsEveryMoveInAOneSpoutTask()
        {
            // Navigation: the left spout alone, rewarded at the left valve.
            Run(new SpoutRules { InCommand = n => LickSpoutCommand.LeftOnly }, 110, 102.7, 106.0);
            var commands = Commands();
            Assert.IsTrue(commands.Count(c => c == LickSpoutCommand.LeftOnly) >= 3);
            Assert.IsFalse(commands.Any(c => c == LickSpoutCommand.SpoutsIn || c == LickSpoutCommand.RightOnly));
        }

        [TestMethod]
        public void TheOutcomeMovesTheRewardedValvesSpoutAlone()
        {
            Assert.AreEqual(LickSpoutCommand.LeftOnly, Spouts.AtOutcome(Tuple.Create("left", 3.0)));
            Assert.AreEqual(LickSpoutCommand.RightOnly, Spouts.AtOutcome(Tuple.Create("right", 3.0)));
            Assert.AreEqual(LickSpoutCommand.SpoutsOut, Spouts.AtOutcome(null));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => Spouts.Only("centre")).Message, "neither left nor right");
            var missed = new TrialOutcome<PieceRow>(new PieceRow { outcome = TrialOutcomes.Incorrect }, "right", double.NaN, 10);
            Assert.AreEqual(LickSpoutCommand.SpoutsOut, missed.SpoutCommand);
        }

        [TestMethod]
        public void TheCuesAreSpoutsInFromTheStimulusAndSpoutsOutFromTheNextInterval()
        {
            var cues = Spouts.Cues(0.5, false, 1.5);
            CollectionAssert.AreEqual(new[] { "SpoutsIn", "SpoutsOut" }, cues.Select(c => c.Name).ToArray());
            CollectionAssert.AreEqual(new[] { CueAnchor.Stimulus, CueAnchor.NextIti }, cues.Select(c => c.Anchor).ToArray());
            CollectionAssert.AreEqual(new[] { 0.5, 1.5 }, cues.Select(c => c.Seconds).ToArray(), "out rewardConsumptionSeconds into the interval");
            Assert.IsFalse(cues.Any(c => c.DroppedAtOutcome));
            CollectionAssert.AreEqual(new[] { -0.3, 1.5 }, Spouts.Cues(-0.3, false, 1.5).Select(c => c.Seconds).ToArray());
            CollectionAssert.AreEqual(new[] { 0.0, 1.5 }, Spouts.Cues(-0.3, true, 1.5).Select(c => c.Seconds).ToArray(), "always in: no lead");
            CollectionAssert.AreEqual(new[] { 0.5, 0.0 }, Spouts.Cues(0.5, true, 0).Select(c => c.Seconds).ToArray());
        }

        [TestMethod]
        public void TheChecksNameTheSpoutsKeys()
        {
            Assert.AreEqual(0, Spouts.Problems(0.5, false, 1.5, 1.5, 0.5).Count);
            CollectionAssert.AreEqual(new[] { "stimulusToResponseWindowSeconds (0.4 s) is below spouts.stimulusToSpoutsInSeconds (0.5 s): the response window would open before the spouts arrive" }, Spouts.Problems(0.5, false, 1.5, 3, 0.4));
            CollectionAssert.AreEqual(new[] { "spouts.stimulusToSpoutsInSeconds must be a finite number" }, Spouts.Problems(double.NaN, false, 1.5, 3, 1));
            CollectionAssert.AreEqual(new[] { "spouts.rewardConsumptionSeconds must be a finite number, 0 or more" }, Spouts.Problems(0.5, false, -1, 3, 1));
            CollectionAssert.AreEqual(new[] { "spouts.rewardConsumptionSeconds (2 s) is above itiMinSeconds (1.5 s): the spouts are drawn out rewardConsumptionSeconds into the interval, before the stimulus" }, Spouts.Problems(0.5, true, 2, 1.5, 1));
            // Signed: before the stimulus; the spouts go out rewardConsumptionSeconds into the interval before they come in.
            Assert.AreEqual(0, Spouts.Problems(-1, false, 1.5, 2.5, 0).Count);
            CollectionAssert.AreEqual(new[] { "spouts.rewardConsumptionSeconds (1.5 s) plus the 1.1 s the spouts come in before the stimulus (spouts.stimulusToSpoutsInSeconds -1.1 s) is above itiMinSeconds (2.5 s): the spouts would come in before they are drawn out" }, Spouts.Problems(-1.1, false, 1.5, 2.5, 0));
            Assert.AreEqual(0, Spouts.Problems(-1.1, true, 1.5, 2.5, 0).Count, "always in: no lead");
        }

        [TestMethod]
        public void BetweenTrialsTheLongestConsumptionAndLeadFitTheShortestInterval()
        {
            Assert.AreEqual(0, Spouts.BetweenTrialsProblems(new[] { -1.0, 0.5 }, new[] { false, false }, new[] { 1.5, 1.0 }, new[] { 2.5, 3.0 }).Count);
            CollectionAssert.AreEqual(new[] { "spouts.rewardConsumptionSeconds 2 s exceeds itiMinSeconds 1.5 s of a combination that may come next" }, Spouts.BetweenTrialsProblems(new[] { 0.5, 0.5 }, new[] { false, false }, new[] { 1.0, 2.0 }, new[] { 1.5, 3.0 }));
            CollectionAssert.AreEqual(new[] { "spouts.rewardConsumptionSeconds 1.5 s plus the spouts' longest lead before the stimulus 1.1 s exceeds itiMinSeconds 2.5 s of a combination that may come next" }, Spouts.BetweenTrialsProblems(new[] { -1.1, 0.5 }, new[] { false, false }, new[] { 1.5, 1.0 }, new[] { 2.5, 3.0 }));
            Assert.AreEqual(0, Spouts.BetweenTrialsProblems(new[] { -1.1, 0.5 }, new[] { true, false }, new[] { 1.5, 1.0 }, new[] { 2.5, 3.0 }).Count, "an always-in combination leads nothing");
        }

        LickSpoutCommand[] MovesAt(TrialEventKind kind, int trialNumber, string cue = null)
        {
            return Moves().Single(m => m.Item1.Kind == kind && m.Item1.Trial.TrialNumber == trialNumber && (cue == null || m.Item1.Cue == cue)).Item2;
        }

        [TestMethod]
        public void WithoutBiasMovesEveryEventMovesAsCommandSays()
        {
            // Rewards, misses and always-in trials mixed, no outcome with a bias move: each event's moves are exactly
            // SpoutCommands.Command's, the stream as it was before bias correction.
            Run(new SpoutRules { AlwaysIn = n => n % 4 == 3, InCommand = n => n % 2 == 0 ? LickSpoutCommand.SpoutsIn : LickSpoutCommand.RightOnly }, 160, 102.7, 110.0, 121.5, 133.3);
            SpoutTrial coming = null;
            var first = true;
            foreach (var m in Moves())
            {
                var e = m.Item1;
                var expected = SpoutCommands.Command(e.Kind, e.Cue, e.Trial, e.Outcome, first, coming);
                CollectionAssert.AreEqual(expected.HasValue ? new[] { expected.Value } : new LickSpoutCommand[0], m.Item2, e.Kind + " " + e.Cue + " of trial " + e.Trial.TrialNumber);
                if (e.Kind == TrialEventKind.Iti) { first = false; coming = e.Trial; }
            }
            Assert.IsTrue(events.Count(e => e.Kind == TrialEventKind.Outcome && e.Outcome.Rewarded) >= 2 && events.Count(e => e.Kind == TrialEventKind.Outcome && !e.Outcome.Rewarded) >= 3);
        }

        [TestMethod]
        public void AnUnrewardedTrialsBiasMoveFollowsItsOutcomesMove()
        {
            Run(new SpoutRules { Bias = n => n == 1 ? LickSpoutCommand.BiasLeft : (LickSpoutCommand?)null }, 107);
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, LickSpoutCommand.BiasLeft }, MovesAt(TrialEventKind.Outcome, 1), "the miss draws both out, then the bias shifts them");
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut }, MovesAt(TrialEventKind.Cue, 1, Spouts.OutCue));
            Assert.AreEqual(1, Commands().Count(c => c == LickSpoutCommand.BiasLeft));
        }

        [TestMethod]
        public void ARewardedTrialsBiasMoveWaitsForTheEndOfTheDrink()
        {
            Run(new SpoutRules { Bias = n => n == 1 ? LickSpoutCommand.BiasRight : (LickSpoutCommand?)null }, 104.5, 102.7);
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.LeftOnly }, MovesAt(TrialEventKind.Outcome, 1), "the rewarded spout stays in front of the mouse");
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, LickSpoutCommand.BiasRight }, MovesAt(TrialEventKind.Cue, 1, Spouts.OutCue), "after the SpoutsOut cue's own move");
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, LickSpoutCommand.SpoutsIn, LickSpoutCommand.LeftOnly, LickSpoutCommand.SpoutsOut, LickSpoutCommand.BiasRight }, Commands().ToArray());
        }

        [TestMethod]
        public void AlwaysInSpoutsTakeTheBiasMoveAtTheSameTimesWithNoMovesOfTheirOwn()
        {
            Run(new SpoutRules { AlwaysIn = n => true, Bias = n => n == 1 ? LickSpoutCommand.BiasLeft : (LickSpoutCommand?)null }, 104.5, 102.7);
            CollectionAssert.AreEqual(new LickSpoutCommand[0], MovesAt(TrialEventKind.Outcome, 1), "rewarded: nothing while the mouse drinks");
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.BiasLeft }, MovesAt(TrialEventKind.Cue, 1, Spouts.OutCue), "no SpoutsOut, the bias move still at its time");

            loop = null; events.Clear(); now = 100;
            Run(new SpoutRules { AlwaysIn = n => true, Bias = n => n == 1 ? LickSpoutCommand.BiasRight : (LickSpoutCommand?)null }, 107);
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.BiasRight }, MovesAt(TrialEventKind.Outcome, 1), "unrewarded: the spouts shift at once");
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsIn, LickSpoutCommand.BiasRight, LickSpoutCommand.SpoutsIn }, Commands().ToArray());
        }

        double NextIti()
        {
            return events.Single(e => e.Kind == TrialEventKind.Iti && e.Trial.TrialNumber == 2).Seconds;
        }

        double OutCue()
        {
            return events.Single(e => e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue && e.Trial.TrialNumber == 1).Seconds;
        }

        [TestMethod]
        public void WhenTheWindowIsAlwaysFullAChoiceWaitsForTheWindowsEndAndTheDrinkStaysInTheNextInterval()
        {
            // The choice at 102.7 s rewards and moves the spouts then; a lick at 103 s is not the rules'; the window closes at
            // 104.001 s and the next interval starts then, the spouts drawn out 1.5 s into it, at 105.501 s.
            var rules = new SpoutRules { AlwaysFull = true };
            Run(rules, 106.2, 102.7, 103.0);
            var outcome = events.Single(e => e.Kind == TrialEventKind.Outcome && e.Trial.TrialNumber == 1);
            Assert.AreEqual(102.7, outcome.Seconds, 1e-9, "the outcome at the choice, as without the option");
            Assert.AreEqual(1, rules.Licks, "a lick after the choice changes nothing");
            Assert.AreEqual(104.001, NextIti(), 1e-9, "the window's end, exactly");
            Assert.AreEqual(NextIti() + 1.5, OutCue(), 1.5e-3, "the spouts out rewardConsumptionSeconds into the interval");
            CollectionAssert.AreEqual(new[] { LickSpoutCommand.SpoutsOut, LickSpoutCommand.SpoutsIn, LickSpoutCommand.LeftOnly, LickSpoutCommand.SpoutsOut }, Commands().ToArray());
        }

        [TestMethod]
        public void WhenTheWindowIsAlwaysFullAPenaltyTimeoutStartsAtItsEndAndANeitherTrialsIntervalThen()
        {
            Run(new SpoutRules { AlwaysFull = true, LickOutcome = TrialOutcomes.Incorrect, Timeout = 4 }, 110, 102.7);
            Assert.AreEqual(102.7, events.Single(e => e.Kind == TrialEventKind.Outcome && e.Trial.TrialNumber == 1).Seconds, 1e-9);
            Assert.AreEqual(104.001 + 4, NextIti(), 1e-9, "the timeout from the window's end");
            Assert.AreEqual(NextIti() + 1.5, OutCue(), 1.5e-3);
            Assert.AreEqual(LickSpoutCommand.SpoutsOut, Moves().Single(m => m.Item1.Kind == TrialEventKind.Outcome && m.Item1.Trial.TrialNumber == 1).Item2.Single(), "both out at the choice");

            loop = null; events.Clear(); now = 100;
            Run(new SpoutRules { AlwaysFull = true, LickOutcome = TrialOutcomes.Probe, Timeout = 4 }, 106, 102.7);
            Assert.AreEqual(104.001, NextIti(), 1e-9, "neither rewarded nor penalised: the window's end");
            Assert.AreEqual(NextIti() + 1.5, OutCue(), 1.5e-3);
        }

        [TestMethod]
        public void WhenTheWindowIsAlwaysFullAMissIsTimedAsWithoutIt()
        {
            Run(new SpoutRules { Timeout = 4 }, 110);
            var plain = NextIti();
            loop = null; events.Clear(); now = 100;
            Run(new SpoutRules { Timeout = 4, AlwaysFull = true }, 110);
            var outcome = events.Single(e => e.Kind == TrialEventKind.Outcome && e.Trial.TrialNumber == 1).Seconds;
            Assert.AreEqual(outcome + 4, NextIti(), 1e-9, "the outcome plus the timeout");
            Assert.AreEqual(plain, NextIti(), 1e-9);
        }
    }

    [TestClass]
    public class SpoutsLeadTests
    {
        // 2 s intervals from 100.001 s: the stimulus due at 102.001 s; the spouts in 0.3 s before it.
        static LoopRig Rig(double laserLead, bool laser, double lickQuiet = 0, bool alwaysIn = false)
        {
            var rules = new StubRules
            {
                Laser = laser ? LaserTiming.UntilOutcome(false, 0, double.PositiveInfinity) : null,
                Plan = n => new TrialTiming { ItiSeconds = 2, LaserToStimulusSeconds = laserLead, Laser = laser, LickQuietSeconds = lickQuiet, ResponseWindowSeconds = 1.5, Cues = Spouts.Cues(-0.3, alwaysIn, 1.5) },
            };
            return new LoopRig(rules);
        }

        static double At(LoopRig rig, TrialEventKind kind, string cue = null)
        {
            return rig.Events.Single(e => e.Kind == kind && e.Trial.TrialNumber == 1 && (cue == null || e.Cue == cue)).Seconds;
        }

        [TestMethod]
        public void ANegativeSpoutsLeadStartsTheTrialThatMuchBeforeTheStimulus()
        {
            var rig = Rig(0, false);
            rig.Until(102.2);
            Assert.AreEqual(101.701, At(rig, TrialEventKind.Start), 1e-9);
            var start = rig.Events.FindIndex(e => e.Kind == TrialEventKind.Start);
            Assert.AreEqual(Spouts.InCue, rig.Events[start + 1].Cue, "the spouts in at the start's own tick");
            Assert.AreEqual(101.701, At(rig, TrialEventKind.Cue, Spouts.InCue), 1e-9);
            Assert.AreEqual(102.001, At(rig, TrialEventKind.Stimulus), 1e-9);
            Assert.AreEqual(0.3, TrialLoop.Lead(new TrialTiming { Cues = Spouts.Cues(-0.3, false, 1.5) }), 1e-12);
            Assert.AreEqual(0, TrialLoop.Lead(new TrialTiming { Cues = Spouts.Cues(0.3, false, 1.5) }), "a positive one leaves the lead alone");
        }

        [TestMethod]
        public void AnAlwaysInTrialHasNoSpoutsLeadSoItStartsAtItsStimulusOrTheLasersLead()
        {
            var rig = Rig(0, false, alwaysIn: true);
            rig.Until(102.2);
            Assert.AreEqual(102.001, At(rig, TrialEventKind.Start), 1e-9);
            Assert.AreEqual(102.002, At(rig, TrialEventKind.Cue, Spouts.InCue), 1e-9, "a cue at the stimulus, on the tick after it");
            Assert.AreEqual(102.001, At(rig, TrialEventKind.Stimulus), 1e-9);
            Assert.AreEqual(0.1, TrialLoop.Lead(new TrialTiming { LaserToStimulusSeconds = 0.1, Cues = Spouts.Cues(-0.3, true, 1.5) }), 1e-12, "the laser's lead alone");
        }

        [TestMethod]
        public void ALargerLaserLeadKeepsTheTrialStartAndTheSpoutsComeInAtTheirOwnTime()
        {
            var rig = Rig(0.5, true);
            rig.Until(102.2);
            Assert.AreEqual(101.501, At(rig, TrialEventKind.Start), 1e-9);
            Assert.AreEqual(101.501, At(rig, TrialEventKind.Trigger), 1e-9);
            Assert.AreEqual(101.701, At(rig, TrialEventKind.Cue, Spouts.InCue), 1e-9);
            Assert.AreEqual(102.001, At(rig, TrialEventKind.Stimulus), 1e-9);
        }

        [TestMethod]
        public void ASmallerLaserLeadTriggersItsOwnLeadBeforeTheStimulus()
        {
            var rig = Rig(0.1, true);
            rig.Until(102.2);
            Assert.AreEqual(101.701, At(rig, TrialEventKind.Start), 1e-9);
            Assert.AreEqual(101.701, At(rig, TrialEventKind.Cue, Spouts.InCue), 1e-9);
            Assert.AreEqual(101.901, At(rig, TrialEventKind.Trigger), 1e-9);
            Assert.AreEqual(102.001, At(rig, TrialEventKind.Stimulus), 1e-9);
        }

        [TestMethod]
        public void TheQuietWaitsHoldTheStartAndTheSpoutsWithIt()
        {
            // A touch held to 101.5 s with a 1 s lick quiet wait: the start at 102.5 s, the spouts in then, the stimulus 0.3 s later.
            var rig = Rig(0, false, 1);
            rig.Until(101);
            rig.Touch(0, true);
            rig.Until(101.5);
            rig.Touch(0, false);
            rig.Until(103);
            Assert.AreEqual(102.5, At(rig, TrialEventKind.Start), 1e-9);
            Assert.AreEqual(102.5, At(rig, TrialEventKind.Cue, Spouts.InCue), 1e-9);
            Assert.AreEqual(102.8, At(rig, TrialEventKind.Stimulus), 1e-9);
        }
    }
}
