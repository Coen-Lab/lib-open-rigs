using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpenRigs.Trials
{
    /// <summary>
    /// The lab's shared block of trial columns (open_rigs.rigs.trials CORE_COLUMNS, QUIET_WAIT_COLUMNS, TRIAL_END_COLUMNS, OPTOGENETICS_COLUMNS),
    /// filled the same way in every task from the trial loop's <see cref="TrialTimes"/> and the outcome's reward and penalty:
    /// a task's row is a class with one public field per column, in its schemas\trial-columns.yml order, and fills its own
    /// columns (trialType, outcome and the task's) itself.
    /// </summary>
    public static class SharedTrialColumns
    {
        /// <summary>The core columns this fills; every row has them (harpTime is the row's timestamp, and trialType and outcome the task's).</summary>
        public static readonly string[] Core = { "trialNumber", "itiStartTime", "itiSeconds", "trialStartTime", "stimulusOnsetTime", "stimulusOffTime", "outcomeTime", "rewardOfferedUl", "rewarded", "penaltyTimeoutSeconds", "penaltyFlashBrightness", "penalised" };

        /// <summary>The quietWait block: filled when the row has it.</summary>
        public static readonly string[] QuietWait = { "quietWaitSeconds" };

        /// <summary>The optogenetics block: filled when the row has it, all six or none.</summary>
        public static readonly string[] Optogenetics = { "laserCondition", "laserPointsMm", "laserPowerMw", "laserTriggerTime", "laserOn", "laserStarted" };

        /// <summary>The trialEnd block: filled when the row has it; a row whose trial's response window always runs in full
        /// (TrialTimes.ResponseWindowAlwaysFull) must have it.</summary>
        public static readonly string[] TrialEnd = { "trialEndTime" };

        /// <summary>
        /// Fills <paramref name="row"/>'s shared block at the trial's outcome (times.Now): its number; the loop's times (stimulusOffTime its StimulusOffTime, StimulusTiming.Time: NaN without a StimulusDurationSeconds);
        /// rewardOfferedUl, the microlitres a reward on this trial gives, on every trial; rewarded, whether the outcome
        /// delivers it (the same value the task's valve command is made from); penaltyTimeoutSeconds the trial's resolved
        /// timeout (<see cref="Penalties.For"/>: its kind's, penalty.incorrect or penalty.noResponse) when penalised, else 0; penaltyFlashBrightness its kind's
        /// flash.brightness (ResolvedPenalty.Flash) when penalised, else 0 (0: no flash played); penalised itself; with the trialEnd block, trialEndTime (TrialTimes.TrialEndTime), which a trial whose response window always runs in full must have; with the optogenetics block, the trial's laser condition, its points (IOptoTrial.LaserPointsMm,
        /// required when the condition is above 0; empty at condition 0), its power only if the
        /// laser was triggered (else 0 mW), the trigger's time (NaN if none), whether the laser was drawn on (laserOn) and whether
        /// the opto system confirmed its start (laserStarted, TrialTimes.LaserStarted), or 0, empty, 0, NaN, false and false for a trial that is not an
        /// IOptoTrial. Throws naming a missing or mistyped column. Returns the row.
        /// </summary>
        public static T Fill<T>(T row, ITrial trial, TrialTimes times, double rewardOfferedUl, bool rewarded, bool penalised, double penaltyTimeoutSeconds, double penaltyFlashBrightness) where T : class
        {
            if (row == null) throw new ArgumentNullException("row");
            if (trial == null) throw new ArgumentNullException("trial");
            if (times == null) throw new ArgumentNullException("times");
            var type = row.GetType();
            var values = new Dictionary<string, object>
            {
                { "trialNumber", trial.TrialNumber },
                { "itiStartTime", times.ItiStartTime },
                { "itiSeconds", times.ItiSeconds },
                { "trialStartTime", times.TrialStartTime },
                { "stimulusOnsetTime", times.StimulusOnsetTime },
                { "stimulusOffTime", times.StimulusOffTime },
                { "outcomeTime", times.Now },
                { "rewardOfferedUl", rewardOfferedUl },
                { "rewarded", rewarded },
                { "penaltyTimeoutSeconds", penalised ? penaltyTimeoutSeconds : 0.0 },
                { "penaltyFlashBrightness", penalised ? penaltyFlashBrightness : 0.0 },
                { "penalised", penalised },
            };
            var missing = new List<string>();
            foreach (var name in Core) if (Field(type, name) == null) missing.Add(name);
            if (missing.Count > 0) throw new InvalidOperationException(string.Format("The trial row {0} has no {1}: every row has the lab's core columns as public fields.", type.Name, string.Join(", ", missing.ToArray())));
            if (Field(type, "quietWaitSeconds") != null) values["quietWaitSeconds"] = times.QuietWaitSeconds;
            if (Field(type, "trialEndTime") != null) values["trialEndTime"] = times.TrialEndTime;
            else if (times.ResponseWindowAlwaysFull) throw new InvalidOperationException(string.Format("The trial row {0} has no trialEndTime, but trial {1}'s response window always runs in full, so its next interval does not start at its outcome: the row needs the trialEnd block.", type.Name, trial.TrialNumber));
            var optogenetics = Array.FindAll(Optogenetics, name => Field(type, name) != null);
            if (optogenetics.Length > 0)
            {
                if (optogenetics.Length < Optogenetics.Length) throw new InvalidOperationException(string.Format("The trial row {0} has {1} of the optogenetics block, not all of {2}.", type.Name, string.Join(", ", optogenetics), string.Join(", ", Optogenetics)));
                var laser = trial as IOptoTrial;
                values["laserCondition"] = laser != null ? laser.LaserCondition : 0;
                var points = laser != null && laser.LaserCondition > 0 ? laser.LaserPointsMm : "";
                if (string.IsNullOrEmpty(points) && laser != null && laser.LaserCondition > 0) throw new InvalidOperationException(string.Format("Trial {0} has laser condition {1} but no laser points: its draw sets LaserPointsMm from the conditions file (ZapitTrial.Draw).", trial.TrialNumber, laser.LaserCondition));
                values["laserPointsMm"] = points;
                values["laserPowerMw"] = laser != null && times.LaserFired ? (double.IsNaN(times.LaserPowerMw) ? laser.LaserPowerMw : times.LaserPowerMw) : 0.0;   // answered before its trigger: no laser
                values["laserTriggerTime"] = times.LaserTriggerTime;
                values["laserOn"] = laser != null && laser.LaserCondition > 0 && laser.LaserOn;
                values["laserStarted"] = times.LaserStarted;
            }
            foreach (var value in values)
            {
                var field = Field(type, value.Key);
                if (field.FieldType != value.Value.GetType()) throw new InvalidOperationException(string.Format("The trial row {0}'s {1} is a {2}, not a {3}.", type.Name, value.Key, field.FieldType.Name, value.Value.GetType().Name));
                field.SetValue(row, value.Value);
            }
            return row;
        }

        static FieldInfo Field(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        }
    }
}
