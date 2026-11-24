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

    /// <summary>An outcome that may carry a move of the spouts' bias offset (<see cref="SpoutBias"/>): <see cref="TrialOutcome{TRow}"/> is one.</summary>
    public interface ISpoutBiasOutcome : ISpoutOutcome
    {
        /// <summary>Gets whether the trial is rewarded: its bias move waits for the end of the drink (the SpoutsOut cue).</summary>
        bool Rewarded { get; }

        /// <summary>Gets the bias move the outcome makes (BiasLeft or BiasRight, the task's <see cref="SpoutBias.Respond"/>), or null for none.</summary>
        LickSpoutCommand? SpoutBiasCommand { get; }
    }

    /// <summary>
    /// The lab's spouts block (open_rigs.core.task Spouts, the task file's spouts): the cues that move the spouts, where they
    /// go, and the block's rules, for every task whose spouts move. <see cref="SpoutCommands"/> makes the moves.
    /// </summary>
    public static class Spouts
    {
        /// <summary>The cue that brings the spouts in, spouts.stimulusToSpoutsInSeconds after the stimulus (before it when negative).</summary>
        public const string InCue = "SpoutsIn";

        /// <summary>The cue that draws the spouts out, spouts.rewardConsumptionSeconds into the next interval, unless the coming trial's spouts are always in.</summary>
        public const string OutCue = "SpoutsOut";

        /// <summary>spouts.rewardConsumptionSeconds' lab default, in seconds.</summary>
        public const double DefaultRewardConsumptionSeconds = 1.5;

        /// <summary>The two cues for a trial's TrialTiming: SpoutsIn stimulusToSpoutsInSeconds from the stimulus, SpoutsOut
        /// rewardConsumptionSeconds into the next interval (time to drink a reward; nothing waits for it). A trial whose spouts
        /// are always in (spouts.alwaysIn) has no lead from them: its SpoutsIn comes at the stimulus when stimulusToSpoutsInSeconds
        /// is negative, so the trial starts as it would without spouts.</summary>
        public static TrialCue[] Cues(double stimulusToSpoutsInSeconds, bool alwaysIn, double rewardConsumptionSeconds)
        {
            return new[]
            {
                new TrialCue(InCue, CueAnchor.Stimulus, alwaysIn ? Math.Max(stimulusToSpoutsInSeconds, 0) : stimulusToSpoutsInSeconds),
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

        /// <summary>One combination's spouts block against its trial structure: stimulusToSpoutsInSeconds a finite number,
        /// signed (negative: the spouts in that long before the stimulus, which leads the trial start, TrialLoop.Lead; none when the
        /// spouts are always in, Cues); rewardConsumptionSeconds a finite number, 0 or more, at most itiMinSeconds (the
        /// spouts go out inside the interval) and, plus the spouts' lead, at most itiMinSeconds (they go out before they come
        /// back in); with a positive stimulusToSpoutsInSeconds, the response window (stimulusToResponseWindowSeconds) not
        /// before the spouts are in. Across combinations, BetweenTrialsProblems.</summary>
        public static List<string> Problems(double stimulusToSpoutsInSeconds, bool alwaysIn, double rewardConsumptionSeconds, double itiMinSeconds, double stimulusToResponseWindowSeconds)
        {
            var problems = new List<string>();
            if (double.IsNaN(stimulusToSpoutsInSeconds) || double.IsInfinity(stimulusToSpoutsInSeconds)) problems.Add("spouts.stimulusToSpoutsInSeconds must be a finite number");
            if (!(rewardConsumptionSeconds >= 0) || double.IsInfinity(rewardConsumptionSeconds)) problems.Add("spouts.rewardConsumptionSeconds must be a finite number, 0 or more");
            var lead = alwaysIn ? 0 : Math.Max(-stimulusToSpoutsInSeconds, 0);
            if (rewardConsumptionSeconds > itiMinSeconds + 1e-9) problems.Add(string.Format("spouts.rewardConsumptionSeconds ({0} s) is above itiMinSeconds ({1} s): the spouts are drawn out rewardConsumptionSeconds into the interval, before the stimulus", rewardConsumptionSeconds, itiMinSeconds));
            else if (rewardConsumptionSeconds + lead > itiMinSeconds + 1e-9) problems.Add(string.Format("spouts.rewardConsumptionSeconds ({0} s) plus the {1} s the spouts come in before the stimulus (spouts.stimulusToSpoutsInSeconds {2} s) is above itiMinSeconds ({3} s): the spouts would come in before they are drawn out", rewardConsumptionSeconds, lead, stimulusToSpoutsInSeconds, itiMinSeconds));
            if (stimulusToSpoutsInSeconds > 0 && stimulusToResponseWindowSeconds < stimulusToSpoutsInSeconds - 1e-9) problems.Add(string.Format("stimulusToResponseWindowSeconds ({0} s) is below spouts.stimulusToSpoutsInSeconds ({1} s): the response window would open before the spouts arrive", stimulusToResponseWindowSeconds, stimulusToSpoutsInSeconds));
            return problems;
        }

        /// <summary>Between a trial and the next, each drawn from any weighted combination: the longest
        /// spouts.rewardConsumptionSeconds plus the longest lead of the spouts before the stimulus (a negative
        /// stimulusToSpoutsInSeconds on a combination whose spouts are not always in, the lists paired by combination) at most
        /// the shortest itiMinSeconds.</summary>
        public static List<string> BetweenTrialsProblems(IEnumerable<double> stimulusToSpoutsInSeconds, IEnumerable<bool> alwaysIn, IEnumerable<double> rewardConsumptionSeconds, IEnumerable<double> itiMinSeconds)
        {
            var problems = new List<string>();
            var consumption = rewardConsumptionSeconds.ToList();
            var iti = itiMinSeconds.ToList();
            if (consumption.Count == 0 || iti.Count == 0) return problems;
            var lead = Math.Max(stimulusToSpoutsInSeconds.Zip(alwaysIn, (v, always) => always ? 0 : -v).DefaultIfEmpty(0).Max(), 0);
            if (consumption.Max() + lead <= iti.Min() + 1e-9) return problems;
            if (lead == 0) problems.Add(string.Format("spouts.rewardConsumptionSeconds {0} s exceeds itiMinSeconds {1} s of a combination that may come next", consumption.Max(), iti.Min()));
            else problems.Add(string.Format("spouts.rewardConsumptionSeconds {0} s plus the spouts' longest lead before the stimulus {1} s exceeds itiMinSeconds {2} s of a combination that may come next", consumption.Max(), lead, iti.Min()));
            return problems;
        }
    }

    /// <summary>Every spout move from the trial events, as LickSpoutStage takes them.</summary>
    [Combinator]
    [Description("Every spout move, from the trial events (OpenRigs.Trials Spouts): both out at trial 1's interval start and at each SpoutsOut cue, unless the coming trial's spouts are always in (spouts.alwaysIn, the trial drawn at its interval's start); at each SpoutsIn cue the trial's SpoutsInCommand; at the outcome, unless the trial's spouts are always in, the outcome's (the rewarded spout alone, else both out). An outcome's bias move (ISpoutBiasOutcome.SpoutBiasCommand, spouts.biasCorrection) follows the outcome's own move on an unrewarded trial, and on a rewarded one waits for the SpoutsOut cue, after its own move if any, so the spout never shifts while the mouse drinks.")]
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
                LickSpoutCommand? biasAtSpoutsOut = null;
                return source.SelectMany(e =>
                {
                    var moves = new List<LickSpoutCommand>(2);
                    var command = Command(e.Kind, e.Cue, e.Trial, e.Outcome, firstIti, coming);
                    if (command.HasValue) moves.Add(command.Value);
                    if (e.Kind == TrialEventKind.Iti)
                    {
                        firstIti = false;
                        coming = e.Trial;
                    }
                    var biased = (object)e.Outcome as ISpoutBiasOutcome;
                    if (e.Kind == TrialEventKind.Outcome && biased != null && biased.SpoutBiasCommand.HasValue)
                    {
                        if (biased.Rewarded) biasAtSpoutsOut = biased.SpoutBiasCommand;
                        else moves.Add(biased.SpoutBiasCommand.Value);
                    }
                    if (e.Kind == TrialEventKind.Cue && e.Cue == Spouts.OutCue && biasAtSpoutsOut.HasValue)
                    {
                        moves.Add(biasAtSpoutsOut.Value);
                        biasAtSpoutsOut = null;
                    }
                    return moves;
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
