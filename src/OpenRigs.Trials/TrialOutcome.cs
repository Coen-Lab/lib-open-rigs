using Bonsai.Harp;
using System;
using System.Reflection;

namespace OpenRigs.Trials
{
    /// <summary>The trial log's outcome values, every task's (open_rigs.rigs.trials OUTCOMES), in the task's camelCase as its targets and trialType.</summary>
    public static class TrialOutcomes
    {
        /// <summary>The trial was answered as the task asks.</summary>
        public const string Correct = "correct";

        /// <summary>The trial was answered otherwise.</summary>
        public const string Incorrect = "incorrect";

        /// <summary>The trial ended with no answer.</summary>
        public const string NoResponse = "noResponse";

        /// <summary>The trial has no right answer, whatever the response: left out of performance.</summary>
        public const string Probe = "probe";

        /// <summary>The auto-reward answered the trial, with no response of the mouse's own: left out of the response rate.</summary>
        public const string Auto = "auto";

        /// <summary>Every outcome, in OUTCOMES' order.</summary>
        public static readonly string[] All = { Correct, Incorrect, NoResponse, Probe, Auto };
    }

    /// <summary>
    /// A trial's outcome, built the same way in every task from its filled row (<see cref="SharedTrialColumns.Fill"/>): the
    /// row stamped with its outcomeTime, the reward, whether it was penalised with the trial's penalty level, the timeout
    /// and the reward consumption (TrialOutcomeEffects and the trial loop read them). A task that sets off more at the outcome (its spouts) derives from it.
    /// </summary>
    /// <typeparam name="TRow">The task's trial row.</typeparam>
    public class TrialOutcome<TRow> : ISpoutOutcome where TRow : class
    {
        /// <summary>
        /// The outcome of <paramref name="row"/>, whose shared block is filled: the reward, when the row is rewarded,
        /// is its rewardOfferedUl at <paramref name="valveInstance"/> (as rig.yml's wiring names the reward valve), else null;
        /// <paramref name="penaltyLevelDbfs"/> the trial's penalty level (Penalties.For's LevelDbfs, <see cref="SoundLevels.Parse"/>: NaN when
        /// mute). Throws when the row's outcome is not one of <see cref="TrialOutcomes.All"/>.
        /// </summary>
        public TrialOutcome(TRow row, string valveInstance, double penaltyLevelDbfs)
        {
            if (row == null) throw new ArgumentNullException("row");
            var outcome = Get<string>(row, "outcome");
            if (Array.IndexOf(TrialOutcomes.All, outcome) < 0)
            {
                throw new InvalidOperationException(string.Format("The trial row's outcome is {0}, not one of {1}.", outcome ?? "null", string.Join(", ", TrialOutcomes.All)));
            }
            Row = Timestamped.Create(row, Get<double>(row, "outcomeTime"));
            Rewarded = Get<bool>(row, "rewarded");
            Reward = Rewarded ? Tuple.Create(valveInstance, Get<double>(row, "rewardOfferedUl")) : null;
            Penalised = Get<bool>(row, "penalised");
            PenaltyLevelDbfs = penaltyLevelDbfs;
            PenaltyTimeoutSeconds = Get<double>(row, "penaltyTimeoutSeconds");
            RewardConsumptionSeconds = Get<double>(row, "rewardConsumptionSeconds");
        }

        /// <summary>Gets the trial row, stamped with the outcome's Harp time, for LogTrials.</summary>
        public Timestamped<TRow> Row { get; private set; }

        /// <summary>Gets whether the trial is rewarded (the row's rewarded).</summary>
        public bool Rewarded { get; private set; }

        /// <summary>Gets the reward request (valve, microlitres) for RewardRequests; null without a reward.</summary>
        public Tuple<string, double> Reward { get; private set; }

        /// <summary>Gets whether the trial is penalised (the row's penalised): the penalty sound plays at PenaltyLevelDbfs.</summary>
        public bool Penalised { get; private set; }

        /// <summary>Gets the trial's penalty sound level in dBFS RMS, NaN when mute (the sound then never plays).</summary>
        public double PenaltyLevelDbfs { get; private set; }

        /// <summary>Gets the timeout after the trial, in seconds (the row's penaltyTimeoutSeconds).</summary>
        public double PenaltyTimeoutSeconds { get; private set; }

        /// <summary>Gets the time to drink the reward after the trial, in seconds (the row's rewardConsumptionSeconds): the next interval starts after it.</summary>
        public double RewardConsumptionSeconds { get; private set; }

        /// <summary>Gets where the spouts go at the outcome in a task whose spouts move (Spouts.AtOutcome): the rewarded valve's spout alone, else both out.</summary>
        public OpenRigs.Devices.LickSpoutCommand SpoutCommand { get { return Spouts.AtOutcome(Reward); } }

        static T Get<T>(TRow row, string name)
        {
            var field = row.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(T))
            {
                throw new InvalidOperationException(string.Format("The trial row {0} has no {1} {2} field.", row.GetType().Name, typeof(T).Name, name));
            }
            return (T)field.GetValue(row);
        }
    }
}
