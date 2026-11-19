using Bonsai;
using OpenRigs.Devices.Zapit;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Trials
{
    /// <summary>What <see cref="ZapitTrialCommand"/> reads from a task's trial: the trial's number (ITrial) and its laser.</summary>
    public interface IZapitTrial : ITrial
    {
        /// <summary>Gets the trial's condition in the conditions file, 1 to 254.</summary>
        int LaserCondition { get; }

        /// <summary>Gets whether the laser fires on this trial.</summary>
        bool LaserOn { get; }

        /// <summary>Gets the laser power on a laser-on trial, in mW.</summary>
        double LaserPowerMw { get; }
    }

    /// <summary>A laser trial with its own laser duration (LaserTiming.ForDuration: each trial's, from its combination).</summary>
    public interface ILaserDurationTrial : IZapitTrial
    {
        /// <summary>Gets how long the laser runs from the trigger, in seconds, above 0.</summary>
        double LaserDurationSeconds { get; }
    }

    /// <summary>A trial's StartStimulus (<see cref="ZapitTrial.Command"/>), sent when the rig's triggering needs it.</summary>
    [Combinator]
    [Description("A trial's Zapit StartStimulus: its condition, the laser on or off (galvos and masking light either way, 0 mW off). " +
        "Inputs: the trial as its laser may be armed, the trial at its trigger. On a hardware-triggered rig it is sent from the first, " +
        "as the DAQ then waits for the trigger line; otherwise from the second, as Zapit then starts the laser on receipt. " +
        "The stimulus lasts the trial's laser duration (an ILaserDurationTrial), else until StopStimulus.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class ZapitTrialCommand
    {
        /// <summary>Gets or sets whether the Zapit DAQ waits for the trigger line.</summary>
        [Description("Whether the Zapit DAQ waits for the trigger line. Map it from the rig file's zapit.hardwareTriggered.")]
        public bool HardwareTriggered { get; set; }

        /// <summary>Each trial's StartStimulus, from the input the rig's triggering needs; the properties are read at subscription.</summary>
        public IObservable<ZapitCommand> Process<TTrial>(IObservable<TTrial> arm, IObservable<TTrial> trigger) where TTrial : IZapitTrial
        {
            return Observable.Defer(() =>
            {
                var hardwareTriggered = HardwareTriggered;
                return SendAt(arm, trigger, hardwareTriggered).Select(trial =>
                    ZapitTrial.Command(trial.TrialNumber, trial.LaserCondition, trial.LaserOn, trial.LaserPowerMw, hardwareTriggered, DurationOf(trial)));
            });
        }

        /// <summary>How long a trial's stimulus lasts, in seconds: its laser duration, or 0 (until StopStimulus) for a trial without one.</summary>
        public static double DurationOf(IZapitTrial trial)
        {
            var timed = trial as ILaserDurationTrial;
            return timed != null ? timed.LaserDurationSeconds : 0;
        }

        /// <summary>When the command goes: as the trial's laser may be armed on a hardware-triggered rig, else at the trigger.</summary>
        public static IObservable<TTrial> SendAt<TTrial>(IObservable<TTrial> arm, IObservable<TTrial> trigger, bool hardwareTriggered)
        {
            return hardwareTriggered ? arm : trigger;
        }
    }
}
