using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using OpenRigs.Core;
using OpenRigs.Devices.Rigs;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>A camera with the Behavior board trigger that drives it.</summary>
    public class CameraOnTrigger
    {
        /// <summary>Creates a camera on a trigger.</summary>
        public CameraOnTrigger(NamedDevice<SpinnakerCamera> camera, int output, double frequency)
        {
            Camera = camera;
            Output = output;
            Frequency = frequency;
        }

        /// <summary>Gets the camera's name and rig.yml entry.</summary>
        [Description("The camera's name and rig.yml entry.")]
        public NamedDevice<SpinnakerCamera> Camera { get; private set; }

        /// <summary>Gets the board's camera trigger output, 0 or 1.</summary>
        [Description("The board's camera trigger output that drives the camera, 0 (DO0) or 1 (DO1).")]
        public int Output { get; private set; }

        /// <summary>Gets the trigger's frequency, in Hz.</summary>
        [Description("The trigger's frequency in Hz, the camera's frame rate.")]
        public double Frequency { get; private set; }
    }

    /// <summary>
    /// Finds the board trigger that drives a camera: the line rig.yml's wiring gives CameraTrigger with
    /// the camera in its targets. Stops the workflow at start unless exactly one line names the camera,
    /// that line is DO0 or DO1, its trigger is enabled, and the camera's exposure fits the trigger
    /// period. A camera still exposing at the next trigger skips it, so it runs at a fraction of the
    /// trigger rate with no gap in its FrameID, and every frame is paired with the wrong trigger time.
    /// </summary>
    [Combinator]
    [Description("The Behavior board trigger that drives the camera, from the board's wiring in rig.yml (the CameraTrigger line whose targets name the camera). Input: the camera and the board, as RigDevice gives them. Stops the workflow unless exactly one enabled trigger line names the camera and its ExposureTime plus MarginMicroseconds fits the trigger period.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CameraTrigger
    {
        /// <summary>Gets or sets the time the camera needs between an exposure's end and the next trigger, in microseconds.</summary>
        [Description("Time the camera needs between the end of one exposure and the next trigger, in microseconds.")]
        public double MarginMicroseconds { get; set; } = 1000;

        /// <summary>Finds each camera's trigger.</summary>
        public IObservable<CameraOnTrigger> Process(IObservable<Tuple<NamedDevice<SpinnakerCamera>, NamedDevice<BehaviorBoard>>> source)
        {
            var margin = MarginMicroseconds;
            return source.Select(pair => Find(pair.Item1, pair.Item2, margin));
        }

        /// <summary>The trigger that drives <paramref name="camera"/>, or an exception saying why there is none.</summary>
        public static CameraOnTrigger Find(NamedDevice<SpinnakerCamera> camera, NamedDevice<BehaviorBoard> board, double margin)
        {
            var wiring = board.Settings.Wiring;
            var lines = wiring == null ? new string[0] : (
                from property in typeof(BoardWiring).GetProperties()
                where property.PropertyType == typeof(WiredLine)
                let line = (WiredLine)property.GetValue(wiring)
                where line != null && line.Device == "CameraTrigger" && line.Targets != null && line.Targets.Contains(camera.Name)
                select property.Name).ToArray();
            if (lines.Length != 1)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in rig.yml has {1} CameraTrigger lines naming {2} in targets ({3}); it needs exactly one, such as DO0: {{ device: CameraTrigger, targets: [{2}] }}.",
                    board.Name, lines.Length, camera.Name, lines.Length == 0 ? "none" : string.Join(", ", lines)));
            }
            int output;
            if (lines[0] == "DO0") output = 0;
            else if (lines[0] == "DO1") output = 1;
            else throw new InvalidOperationException(string.Format(
                "{0}'s wiring in rig.yml puts {1}'s CameraTrigger on {2}; the board's camera triggers are on DO0 and DO1.", board.Name, camera.Name, lines[0]));

            var trigger = board.Settings.CameraTriggerController;
            if (trigger == null) throw new InvalidOperationException(board.Name + " in rig.yml has no cameraTriggerController, which drives " + camera.Name + ".");
            var enabled = output == 0 ? trigger.Trigger0Enabled : trigger.Trigger1Enabled;
            if (!enabled)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s camera trigger {1} drives {2} (wiring {3}), but its cameraTriggerController.trigger{1}Enabled is false in rig.yml.", board.Name, output, camera.Name, lines[0]));
            }
            var frequency = output == 0 ? trigger.Trigger0Frequency : trigger.Trigger1Frequency;
            CheckExposure(camera.Name, camera.Settings.ExposureTime, frequency, margin);
            return new CameraOnTrigger(camera, output, frequency);
        }

        /// <summary>Throws, saying the longest exposure allowed, unless exposure + margin fits in one trigger period.</summary>
        public static void CheckExposure(string camera, double exposureTime, double frequency, double margin)
        {
            if (frequency <= 0)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0}'s trigger frequency in rig.yml is {1} Hz; it must be above 0.", camera, frequency));
            }
            var limit = 1e6 / frequency - margin;
            if (exposureTime > limit)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0}'s ExposureTime is {1} us, longer than {2} us, the most that fits its {3} Hz trigger (a period of {4} us, less {5} us " +
                    "for the camera). The camera would skip triggers and every frame would get the wrong time. Set rig.yml's exposureTime to {2} or less.",
                    camera, exposureTime, limit, frequency, 1e6 / frequency, margin));
            }
        }
    }

    /// <summary>
    /// The times of one camera trigger's pulses: the board's Camera0Frame or Camera1Frame Event
    /// messages only. The board's messages also carry the register dump's Read reply of those
    /// registers, which is not a pulse and would pair every later frame with the wrong time.
    /// </summary>
    [Combinator]
    [Description("The times of the board's camera trigger Output's pulses (its Camera0Frame or Camera1Frame Event messages; the register dump's Read replies are not pulses), from the board's messages.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CameraTriggerPulses
    {
        /// <summary>Gets or sets the board's camera trigger output, 0 or 1.</summary>
        [Description("The board's camera trigger output, 0 or 1.")]
        public int Output { get; set; }

        /// <summary>Each pulse of the trigger, timestamped.</summary>
        public IObservable<Timestamped<HarpMessage>> Process(IObservable<HarpMessage> source)
        {
            // Output is read at subscription, after a mapping has set it, not when the workflow is built.
            return Observable.Defer(() =>
            {
                var output = Output;
                if (output != 0 && output != 1) throw new InvalidOperationException("CameraTriggerPulses' Output is " + output + "; it must be 0 or 1.");
                var address = output == 0 ? Camera0Frame.Address : Camera1Frame.Address;
                return source
                    .Where(message => message.MessageType == MessageType.Event && message.Address == address)
                    .Select(message => Timestamped.Create(message, message.GetTimestamp()));
            });
        }
    }
}
