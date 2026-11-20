using Bonsai;
using OpenRigs.Devices;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Trials
{
    /// <summary>A trial of a task whose spouts move (its spouts block, open_rigs.core.task Spouts).</summary>
    public interface ISpoutTrial : ITrial
    {
        /// <summary>Gets whether the trial's spouts are always in (spouts.alwaysIn): every spout the task uses in for the whole trial and interval, no move but its SpoutsInCommand.</summary>
        bool SpoutsAlwaysIn { get; }

        /// <summary>Gets where the spouts go at the trial's SpoutsIn cue: both in, or one spout alone (the task's choice).</summary>
        LickSpoutCommand SpoutsInCommand { get; }
    }

    /// <summary>An outcome that moves the spouts: <see cref="TrialOutcome{TRow}"/> is one, by its reward's valve.</summary>
    public interface ISpoutOutcome : ITrialOutcome
    {
        /// <summary>Gets where the spouts go at the outcome, unless the trial's spouts are always in (<see cref="Spouts.AtOutcome"/>).</summary>
        LickSpoutCommand SpoutCommand { get; }
    }

    /// <summary>
    /// The lab's spouts block (open_rigs.core.task Spouts, the task file's spouts): the cues that move the spouts, where they
    /// go, and the block's rules, for every task whose spouts move. <see cref="SpoutCommands"/> makes the moves.
    /// </summary>
    public static class Spouts
    {
        /// <summary>The cue that brings the spouts in, spouts.stimulusToSpoutsInSeconds after the stimulus.</summary>
        public const string InCue = "SpoutsIn";

        /// <summary>The cue that draws the spouts out, spouts.rewardConsumptionSeconds into the next interval, unless the coming trial's spouts are always in.</summary>
        public const string OutCue = "SpoutsOut";

        /// <summary>spouts.rewardConsumptionSeconds' lab default, in seconds.</summary>
        public const double DefaultRewardConsumptionSeconds = 1.5;

        /// <summary>The two cues for a trial's TrialTiming: SpoutsIn from the stimulus, SpoutsOut from the next interval's start.</summary>
        public static TrialCue[] Cues(double stimulusToSpoutsInSeconds, double rewardConsumptionSeconds)
        {
            return new[]
            {
                new TrialCue(InCue, CueAnchor.Stimulus, stimulusToSpoutsInSeconds),
                new TrialCue(OutCue, CueAnchor.NextIti, rewardConsumptionSeconds),
            };
        }

        /// <summary>One spout alone, by rig.yml's wiring name: left LeftOnly, right RightOnly; throws otherwise.</summary>
        public static LickSpoutCommand Only(string spout)
        {
            if (spout == "left") return LickSpoutCommand.LeftOnly;
            if (spout == "right") return LickSpoutCommand.RightOnly;
            throw new InvalidOperationException(string.Format("Spouts: spout {0} is neither left nor right.", spout ?? "null"));
        }

        /// <summary>Where the spouts go at an outcome: the rewarded valve's spout alone (<see cref="Only"/>), both out with no reward.</summary>
        public static LickSpoutCommand AtOutcome(Tuple<string, double> reward)
        {
            return reward == null ? LickSpoutCommand.SpoutsOut : Only(reward.Item1);
        }

        /// <summary>One combination's spouts block against its trial structure: stimulusToSpoutsInSeconds and rewardConsumptionSeconds
        /// each a finite number, 0 or more; rewardConsumptionSeconds at most itiMinSeconds (the spouts go out inside the
        /// interval, before the stimulus); the response window (stimulusToResponseWindowSeconds) not before the spouts are in.</summary>
        public static List<string> Problems(double stimulusToSpoutsInSeconds, double rewardConsumptionSeconds, double itiMinSeconds, double stimulusToResponseWindowSeconds)
        {
            var problems = new List<string>();
            Finite(problems, "spouts.stimulusToSpoutsInSeconds", stimulusToSpoutsInSeconds);
            Finite(problems, "spouts.rewardConsumptionSeconds", rewardConsumptionSeconds);
            if (rewardConsumptionSeconds > itiMinSeconds + 1e-9) problems.Add(string.Format("spouts.rewardConsumptionSeconds ({0} s) is above itiMinSeconds ({1} s): the spouts are drawn out rewardConsumptionSeconds into the interval, before the stimulus", rewardConsumptionSeconds, itiMinSeconds));
            if (stimulusToResponseWindowSeconds < stimulusToSpoutsInSeconds - 1e-9) problems.Add(string.Format("stimulusToResponseWindowSeconds ({0} s) is below spouts.stimulusToSpoutsInSeconds ({1} s): the response window would open before the spouts arrive", stimulusToResponseWindowSeconds, stimulusToSpoutsInSeconds));
            return problems;
        }

        /// <summary>Between a trial and the next, each drawn from any weighted combination: the longest
        /// spouts.rewardConsumptionSeconds at most the shortest itiMinSeconds.</summary>
        public static List<string> BetweenTrialsProblems(IEnumerable<double> rewardConsumptionSeconds, IEnumerable<double> itiMinSeconds)
        {
            var problems = new List<string>();
            var consumption = rewardConsumptionSeconds.ToList();
            var iti = itiMinSeconds.ToList();
            if (consumption.Count == 0 || iti.Count == 0) return problems;
            if (consumption.Max() > iti.Min() + 1e-9) problems.Add(string.Format("spouts.rewardConsumptionSeconds {0} s exceeds itiMinSeconds {1} s of a combination that may come next", consumption.Max(), iti.Min()));
            return problems;
        }

        static void Finite(List<string> problems, string name, double value)
        {
            if (!(value >= 0) || double.IsInfinity(value)) problems.Add(name + " must be a finite number, 0 or more");
        }
    }

    /// <summary>Every spout move from the trial events, as LickSpoutStage takes them.</summary>
    [Combinator]
    [Description("Every spout move, from the trial events (OpenRigs.Trials Spouts): both out at trial 1's interval start and at each SpoutsOut cue, unless the coming trial's spouts are always in (spouts.alwaysIn, the trial drawn at its interval's start); at each SpoutsIn cue the trial's SpoutsInCommand; at the outcome, unless the trial's spouts are always in, the outcome's (the rewarded spout alone, else both out).")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SpoutCommands
    {
        /// <summary>The moves the events make, in order.</summary>
        public IObservable<LickSpoutCommand> Process<TTrial, TUpdate, TOutcome>(IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> source)
            where TTrial : ISpoutTrial
            where TOutcome : ISpoutOutcome
        {
            return Observable.Defer(() =>
            {
                var firstIti = true;
                ISpoutTrial coming = null;
                return source.SelectMany(e =>
                {
                    var command = Command(e.Kind, e.Cue, e.Trial, e.Outcome, firstIti, coming);
                    if (e.Kind == TrialEventKind.Iti)
                    {
                        firstIti = false;
                        coming = e.Trial;
                    }
                    return command.HasValue ? new[] { command.Value } : new LickSpoutCommand[0];
                });
            });
        }

        /// <summary>The move one event makes, if any. firstIti on trial 1's interval start, where the spouts go out; coming, the
        /// trial whose interval the SpoutsOut cue falls in (drawn at that interval's start), which decides whether they go out.</summary>
        public static LickSpoutCommand? Command(TrialEventKind kind, string cue, ISpoutTrial trial, ISpoutOutcome outcome, bool firstIti, ISpoutTrial coming)
        {
            switch (kind)
            {
                case TrialEventKind.Iti:
                    return firstIti && !trial.SpoutsAlwaysIn ? LickSpoutCommand.SpoutsOut : (LickSpoutCommand?)null;
                case TrialEventKind.Cue:
                    if (cue == Spouts.InCue) return trial.SpoutsInCommand;
                    if (cue == Spouts.OutCue) return coming != null && coming.SpoutsAlwaysIn ? (LickSpoutCommand?)null : LickSpoutCommand.SpoutsOut;
                    return null;
                case TrialEventKind.Outcome:
                    return trial.SpoutsAlwaysIn ? (LickSpoutCommand?)null : outcome.SpoutCommand;
                default:
                    return null;
            }
        }
    }
}
