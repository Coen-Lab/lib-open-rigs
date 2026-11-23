using Bonsai;
using OpenRigs.Zapit;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Trials
{
    /// <summary>The rules every task's laser trials share with ZapitDaq (MATLAB Zapit's are OpenRigs.Devices.Zapit ZapitTrial), and the checks the loop makes of its Armed events.</summary>
    public static class ZapitDaqTrial
    {
        /// <summary>The most ZapitDaq's Arm may take, in seconds (its Armed event's ArmSeconds): every task arms at least this
        /// long before the trigger (open_rigs.components.zapit ZAPIT_ARM_SECONDS, held equal by a test). To be measured on a rig.</summary>
        public const double ArmSeconds = 0.2;

        /// <summary>A trial's Arm command: its condition, the laser on at its power or off, for its laser duration (an
        /// ILaserDurationTrial) or until an early stop (0).</summary>
        public static ZapitDaqCommand Arm(IZapitTrial trial)
        {
            if (trial.LaserCondition < 1) throw new InvalidOperationException(string.Format("Trial {0} has laser condition {1}; a laser trial's is from 1.", trial.TrialNumber, trial.LaserCondition));
            var timed = trial as ILaserDurationTrial;
            return new ZapitDaqCommand
            {
                Kind = ZapitDaqCommandKind.Arm,
                Trial = trial.TrialNumber,
                Condition = trial.LaserCondition,
                LaserOn = trial.LaserOn,
                PowerMw = trial.LaserOn ? trial.LaserPowerMw : 0,
                DurationSeconds = timed != null ? timed.LaserDurationSeconds : 0,
            };
        }

        /// <summary>Throws unless an Armed event is the trial's, as asked: its number, condition and laser.</summary>
        public static void CheckArmed(ZapitDaqEvent armed, int trialNumber, int laserCondition, bool laserOn)
        {
            if (armed.Trial != trialNumber || armed.Condition != laserCondition || armed.LaserOn != laserOn)
            {
                throw new InvalidOperationException(string.Format(
                    "ZapitDaq armed trial {0} (condition {1}, laser {2}) when trial {3} asked for condition {4}, laser {5}, so the workflow stopped.",
                    armed.Trial, armed.Condition, armed.LaserOn ? "on" : "off", trialNumber, laserCondition, laserOn ? "on" : "off"));
            }
        }

        /// <summary>
        /// Why the task file's lead is refused, or null: the trigger comes laserToStimulusSeconds before the stimulus, and
        /// ZapitDaq needs <see cref="ArmSeconds"/> before the trigger, all inside the shortest interval
        /// (open_rigs.core.task Optogenetics.check_lead_fits, the same rule).
        /// </summary>
        public static string LeadProblem(double laserToStimulusSeconds, double itiMinSeconds)
        {
            if (laserToStimulusSeconds + ArmSeconds <= itiMinSeconds + 1e-9) return null;
            return string.Format(
                "optogenetics.laserToStimulusSeconds ({0} s) plus Zapit's {1} s arm time must be at most trialStructure.itiMinSeconds ({2} s), the shortest interval",
                laserToStimulusSeconds, ArmSeconds, itiMinSeconds);
        }

        /// <summary>The stop for a trial whose trigger came before ZapitDaq armed it: the task stops rather than move the trigger.</summary>
        public static InvalidOperationException NotArmedByTrigger(int trialNumber, double triggerSeconds)
        {
            return new InvalidOperationException(string.Format(
                "ZapitDaq had not armed trial {0} by its trigger at {1} s on the Harp clock, so the workflow stopped rather than move the trigger.",
                trialNumber, triggerSeconds));
        }

        /// <summary>
        /// One optogenetics trial's draw: a condition from the bank, each equally likely, with its points
        /// (<see cref="ZapitBankCondition.LaserPointsMm"/>), then the laser on with laserOnProbability once the first
        /// trialsBeforeLaser trials are past, and the power with it (0 mW when off).
        /// </summary>
        public static void Draw(Random random, int trialNumber, ZapitBank bank, int trialsBeforeLaser, double laserOnProbability, double laserPowerMw,
            out int condition, out string pointsMm, out bool laserOn, out double powerMw)
        {
            condition = random.Next(bank.Conditions.Count) + 1;
            pointsMm = bank.Conditions[condition - 1].LaserPointsMm;
            laserOn = trialNumber > trialsBeforeLaser && random.NextDouble() < laserOnProbability;
            powerMw = laserOn ? laserPowerMw : 0;
        }
    }

    /// <summary>Each trial's Arm for ZapitDaq (<see cref="ZapitDaqTrial.Arm"/>), sent as the loop arms it during the interval.</summary>
    [Combinator]
    [Description("Each laser trial's ZapitDaq Arm: its condition, the laser on at its power or off (galvos and masking light either way), " +
        "for the trial's laser duration (an ILaserDurationTrial), else until an early stop. Input: the trial at its Arm event.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitDaqTrialCommand
    {
        /// <summary>Each trial's Arm.</summary>
        public IObservable<ZapitDaqCommand> Process<TTrial>(IObservable<TTrial> source) where TTrial : IZapitTrial
        {
            return source.Select(trial => ZapitDaqTrial.Arm(trial));
        }
    }

    /// <summary>An early stop of the trial ZapitDaq plays: its ramp, then laser and LED at 0 V.</summary>
    [Combinator]
    [Description("An EarlyStop of the input trial for ZapitDaq: its ramp, then laser and LED at 0 V; nothing if the trial is over.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitEarlyStop
    {
        /// <summary>Each trial's EarlyStop.</summary>
        public IObservable<ZapitDaqCommand> Process<TTrial>(IObservable<TTrial> source) where TTrial : ITrial
        {
            return source.Select(trial => new ZapitDaqCommand { Kind = ZapitDaqCommandKind.EarlyStop, Trial = trial.TrialNumber });
        }
    }
}
