using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>What a task sends Zapit and the board for each optogenetics trial.</summary>
    public static class ZapitTrial
    {
        /// <summary>
        /// The trial's StartStimulus: its condition, the laser on or off (the galvos and masking light
        /// run either way), the power on a laser-on trial and 0 mW on a laser-off one, Zapit's own log
        /// on, and no start delay.
        /// </summary>
        /// <param name="trialNumber">The trial's number, for the refusal.</param>
        /// <param name="conditionNumber">The trial's condition in the conditions file, 1 to 254.</param>
        /// <param name="laserOn">Whether the laser fires.</param>
        /// <param name="laserPowerMw">The laser power on a laser-on trial, in mW.</param>
        /// <param name="hardwareTriggered">Whether the DAQ waits for the ZapitTrigger line.</param>
        /// <param name="durationSeconds">How long the stimulus lasts, in seconds; 0 plays until StopStimulus.</param>
        public static ZapitCommand Command(int trialNumber, int conditionNumber, bool laserOn, double laserPowerMw, bool hardwareTriggered, double durationSeconds)
        {
            if (conditionNumber < 1 || conditionNumber > 254)
            {
                throw new InvalidOperationException(string.Format(
                    "Trial {0} has Zapit condition {1}, but an optogenetics trial needs one from the conditions file, 1 to 254. " +
                    "Check the task's laser conditions.", trialNumber, conditionNumber));
            }
            if (!(durationSeconds >= 0) || double.IsInfinity(durationSeconds))
            {
                throw new InvalidOperationException(string.Format(
                    "Trial {0}'s Zapit stimulus lasts {1} s; it must be 0 (until stopped) or more seconds.", trialNumber, durationSeconds));
            }
            return new ZapitCommand
            {
                Command = ZapitCommandType.StartStimulus,
                ConditionNumber = conditionNumber,
                LaserOn = laserOn,
                HardwareTriggered = hardwareTriggered,
                Logging = true,
                StimDuration = (float)durationSeconds,
                LaserPower = laserOn ? (float)laserPowerMw : 0f,
                StartDelay = 0f,
            };
        }

        /// <summary>A Behavior board write that sets (<paramref name="set"/> true) or clears <paramref name="line"/>.</summary>
        public static HarpMessage LineMessage(DigitalOutputs line, bool set)
        {
            return set
                ? OutputSet.FromPayload(MessageType.Write, line)
                : OutputClear.FromPayload(MessageType.Write, line);
        }
    }

    /// <summary>A Behavior board write per input line that sets or clears it, such as the ZapitTrigger line.</summary>
    [Combinator]
    [Description("A Behavior board write that sets (Set true) or clears the input line, such as the ZapitTrigger line.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitLineMessage
    {
        /// <summary>Gets or sets whether the line is set; false clears it.</summary>
        [Description("True sets the line high, false clears it.")]
        public bool Set { get; set; }

        /// <summary>The write for each line.</summary>
        public IObservable<HarpMessage> Process(IObservable<DigitalOutputs> source)
        {
            var set = Set;
            return source.Select(line => ZapitTrial.LineMessage(line, set));
        }
    }
}
