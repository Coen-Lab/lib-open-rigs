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
        /// How long before its trigger Zapit's armed reply must be in, in seconds (open_rigs.rigs.dome
        /// ZAPIT_REPLY_MARGIN_SECONDS): every dome task leaves this much between arming and the trigger.
        /// </summary>
        public const double ReplyMarginSeconds = 0.2;

        /// <summary>
        /// Throws unless the rig's Zapit is hardware-triggered or the mouse is the test mouse (SubjectId
        /// test): without the trigger line Zapit starts the laser when the command arrives, with no onset in
        /// the Harp log.
        /// </summary>
        public static void RequireHardwareTrigger(bool hardwareTriggered, string subjectId)
        {
            if (hardwareTriggered || (subjectId ?? "").Trim().ToLowerInvariant() == "test") return;
            throw new InvalidOperationException(string.Format(
                "Optogenetics with a real mouse ({0}) needs a hardware-triggered rig, and this rig's rig.yml has zapit.hardwareTriggered false. " +
                "Cable the ZapitTrigger line to the Zapit DAQ trigger input and set hardwareTriggered: true. Rigs without hardware triggering are for the test mouse only.",
                subjectId));
        }

        /// <summary>
        /// Throws on an error reply, or on an IsStimConfigLoaded reply saying no conditions file is loaded:
        /// carrying on would log trials Zapit did not run.
        /// </summary>
        public static void CheckReply(ZapitReply reply)
        {
            if (reply.IsError)
            {
                throw new InvalidOperationException(string.Format(
                    "Zapit answered with an error ({0}). Is the conditions file the task names loaded in Zapit, is Zapit calibrated, and is the DAQ working?", reply));
            }
            if (reply.Command == ZapitCommandType.IsStimConfigLoaded && reply.Value == 0)
            {
                throw new InvalidOperationException("Zapit has no conditions file loaded. Load the task's conditions file in Zapit, then start again.");
            }
        }

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

    /// <summary>Stops the workflow on an error reply or on Zapit having no conditions file loaded (<see cref="ZapitTrial.CheckReply"/>).</summary>
    [Combinator]
    [Description("Stops the workflow on any Zapit error reply, or an IsStimConfigLoaded reply saying no conditions file is loaded. Passes the reply on.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class CheckZapitReply
    {
        /// <summary>Checks each reply.</summary>
        public IObservable<ZapitReply> Process(IObservable<ZapitReply> source)
        {
            return source.Do(ZapitTrial.CheckReply);
        }
    }

    /// <summary>Stops the workflow for a real mouse on a rig without hardware triggering (<see cref="ZapitTrial.RequireHardwareTrigger"/>).</summary>
    [Combinator]
    [Description("Stops the workflow unless rig.yml's zapit.hardwareTriggered is true or the mouse is the test mouse. Input: (hardwareTriggered, SubjectId).")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class RequireZapitHardwareTrigger
    {
        /// <summary>Checks each (hardwareTriggered, SubjectId) pair.</summary>
        public IObservable<Tuple<bool, string>> Process(IObservable<Tuple<bool, string>> source)
        {
            return source.Do(input => ZapitTrial.RequireHardwareTrigger(input.Item1, input.Item2));
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
