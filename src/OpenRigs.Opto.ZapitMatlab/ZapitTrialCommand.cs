using Bonsai;
using OpenRigs.Devices.Zapit;
using OpenRigs.Trials;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Opto.ZapitMatlab
{
    /// <summary>A trial's StartStimulus (<see cref="ZapitTrial.Command"/>), sent when the rig's triggering needs it.</summary>
    [Combinator]
    [Description("A trial's Zapit StartStimulus: its condition, the laser on or off (galvos and masking light either way, 0 mW off). " +
        "Inputs: the trial as its laser may be armed, the trial at its trigger. On a hardware-triggered rig it is sent from the first, " +
        "as the DAQ then waits for the trigger line; otherwise from the second, as Zapit then starts the laser on receipt. " +
        "The stimulus lasts the trial's laser duration (an IOptoDurationTrial), else, or with UntilStopped, until StopStimulus.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class ZapitTrialCommand
    {
        /// <summary>Gets or sets whether the Zapit DAQ waits for the trigger line.</summary>
        [Description("Whether the Zapit DAQ waits for the trigger line. Map it from the rig file's zapit.hardwareTriggered.")]
        public bool HardwareTriggered { get; set; }

        /// <summary>Gets or sets whether every stimulus plays until StopStimulus, no duration sent, even a trial's with its own laser duration.</summary>
        [Description("Whether every stimulus plays until StopStimulus (no duration sent), even a trial's with its own laser duration. Map it from the rules' LaserTiming.UntilStopped.")]
        public bool UntilStopped { get; set; }

        /// <summary>Each trial's StartStimulus, from the input the rig's triggering needs; the properties are read at subscription.</summary>
        public IObservable<ZapitCommand> Process<TTrial>(IObservable<TTrial> arm, IObservable<TTrial> trigger) where TTrial : IOptoTrial
        {
            return Observable.Defer(() =>
            {
                var hardwareTriggered = HardwareTriggered;
                var untilStopped = UntilStopped;
                return SendAt(arm, trigger, hardwareTriggered).Select(trial =>
                    ZapitTrial.Command(trial.TrialNumber, trial.LaserCondition, trial.LaserOn, trial.LaserPowerMw, hardwareTriggered, untilStopped ? 0 : DurationOf(trial)));
            });
        }

        /// <summary>How long a trial's stimulus lasts, in seconds: its laser duration, or 0 (until StopStimulus) for a trial without one.</summary>
        public static double DurationOf(IOptoTrial trial)
        {
            var timed = trial as IOptoDurationTrial;
            return timed != null ? timed.LaserDurationSeconds : 0;
        }

        /// <summary>When the command goes: as the trial's laser may be armed on a hardware-triggered rig, else at the trigger.</summary>
        public static IObservable<TTrial> SendAt<TTrial>(IObservable<TTrial> arm, IObservable<TTrial> trigger, bool hardwareTriggered)
        {
            return hardwareTriggered ? arm : trigger;
        }
    }
}
