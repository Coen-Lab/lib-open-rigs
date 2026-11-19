using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpenRigs.Trials
{
    /// <summary>
    /// The lab's shared block of trial columns (open_rigs.rigs.trials CORE_COLUMNS, QUIET_WAIT_COLUMNS, OPTOGENETICS_COLUMNS),
    /// filled the same way in every task from the trial loop's <see cref="TrialTimes"/> and the outcome's reward and penalty:
    /// a task's row is a class with one public field per column, in its schemas\trial-columns.yml order, and fills its own
    /// columns (trialType, outcome and the task's) itself.
    /// </summary>
    public static class SharedTrialColumns
    {
        /// <summary>The core columns this fills; every row has them (harpTime is the row's timestamp, and trialType and outcome the task's).</summary>
        public static readonly string[] Core = { "trialNumber", "itiStartTime", "itiSeconds", "trialStartTime", "stimulusOnsetTime", "outcomeTime", "rewardOfferedUl", "rewarded", "penaltyTimeoutSeconds", "penalised" };

        /// <summary>The quietWait block: filled when the row has it.</summary>
        public static readonly string[] QuietWait = { "quietWaitSeconds" };

        /// <summary>The optogenetics block: filled when the row has it, all three or none.</summary>
        public static readonly string[] Optogenetics = { "laserCondition", "laserPowerMw", "laserTriggerTime" };

        /// <summary>
        /// Fills <paramref name="row"/>'s shared block at the trial's outcome (times.Now): its number; the loop's times;
        /// rewardOfferedUl, the microlitres a reward on this trial gives, on every trial; rewarded, whether the outcome
        /// delivers it (the same value the task's valve command is made from); penaltyTimeoutSeconds the task's timeout when
        /// penalised, else 0;
        /// penalised itself; with the optogenetics block, the trial's laser condition, its power only if the
        /// laser was triggered (else 0 mW) and the trigger's time (NaN if none), or 0, 0 and NaN for a trial that is not an
        /// IZapitTrial. Throws naming a missing or mistyped column. Returns the row.
        /// </summary>
        public static T Fill<T>(T row, ITrial trial, TrialTimes times, double rewardOfferedUl, bool rewarded, bool penalised, double penaltyTimeoutSeconds) where T : class
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
                { "outcomeTime", times.Now },
                { "rewardOfferedUl", rewardOfferedUl },
                { "rewarded", rewarded },
                { "penaltyTimeoutSeconds", penalised ? penaltyTimeoutSeconds : 0.0 },
                { "penalised", penalised },
            };
            var missing = new List<string>();
            foreach (var name in Core) if (Field(type, name) == null) missing.Add(name);
            if (missing.Count > 0) throw new InvalidOperationException(string.Format("The trial row {0} has no {1}: every row has the lab's core columns as public fields.", type.Name, string.Join(", ", missing.ToArray())));
            if (Field(type, "quietWaitSeconds") != null) values["quietWaitSeconds"] = times.QuietWaitSeconds;
            var optogenetics = Array.FindAll(Optogenetics, name => Field(type, name) != null);
            if (optogenetics.Length > 0)
            {
                if (optogenetics.Length < Optogenetics.Length) throw new InvalidOperationException(string.Format("The trial row {0} has {1} of the optogenetics block, not all of {2}.", type.Name, string.Join(", ", optogenetics), string.Join(", ", Optogenetics)));
                var laser = trial as IZapitTrial;
                values["laserCondition"] = laser != null ? laser.LaserCondition : 0;
                values["laserPowerMw"] = laser != null && times.LaserFired ? laser.LaserPowerMw : 0.0;   // answered before its trigger: no laser
                values["laserTriggerTime"] = times.LaserTriggerTime;
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
