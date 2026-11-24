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

        /// <summary>How long after a timed trial's last sample is due the loop waits before arming the next, in seconds: the
        /// trigger line's edge comes after the loop's trigger tick, so the DAQ's last sample comes after the loop's reckoning
        /// (open_rigs.components.zapit ZAPIT_FREE_MARGIN_SECONDS, held equal by a test). The loop frees ZapitDaq at the trigger
        /// plus the trial's whole length (its Armed event's TrialSeconds) plus this; Go's limits on laserDurationSeconds take it too.</summary>
        public const double FreeMarginSeconds = 0.05;

        /// <summary>The most a timed ZapitDaq trial outlasts its laserDurationSeconds, in seconds: up to one scan cycle more
        /// (whole cycles), the bank's longest ramp and <see cref="FreeMarginSeconds"/>. Go's limits subtract it.</summary>
        public static double FreeAfterLaserSeconds(ZapitBank bank)
        {
            if (bank == null) throw new ArgumentNullException("bank");
            double cycle = 0;
            foreach (var condition in bank.Conditions) cycle = Math.Max(cycle, condition.ScanCycle.Length / bank.SampleRate);
            return cycle + bank.LongestRampSeconds + FreeMarginSeconds;
        }

        /// <summary>The stop for a Started that came before the trial's trigger: a stray edge on the trigger line, or a
        /// device that starts at arming, would put the laser in the interval while the row says it played at the trigger.</summary>
        public static InvalidOperationException StartedBeforeTrigger(int trialNumber)
        {
            return new InvalidOperationException(string.Format(
                "ZapitDaq started trial {0} before the workflow raised its trigger, so the laser (or its masking light) played outside the trial and the workflow stopped. Check the Zapit trigger line's wiring for a stray edge.",
                trialNumber));
        }

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

    /// <summary>ZapitDaq's Trigger with each trigger the loop raises: the simulated twin's edge (it has no line); the NI DAQ starts on the line and ignores it.</summary>
    [Combinator]
    [Description("A ZapitDaq Trigger for each input (the loop's Trigger events): the simulated twin's trigger edge, as the line's on a rig, where the NI DAQ ignores it.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitTwinTrigger
    {
        /// <summary>Each input's Trigger.</summary>
        public IObservable<ZapitDaqCommand> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(_ => new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Trigger });
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
