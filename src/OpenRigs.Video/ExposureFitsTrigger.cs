using Bonsai;
using OpenRigs.Core;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;

namespace OpenRigs.Video
{
    /// <summary>
    /// Stops the workflow at start when a camera's exposure does not fit inside the period of the
    /// board trigger that drives it. A camera still exposing at the next trigger skips it, so it runs
    /// at a fraction of the trigger rate with no gap in its FrameID, and every frame is paired with the
    /// wrong trigger time, with no error.
    /// </summary>
    [Combinator]
    [Description("Stops the workflow if the camera's ExposureTime plus MarginMicroseconds does not fit inside its trigger period (the board's CameraTriggerController Trigger<n>Frequency). Input: the camera and the board, as RigDevice gives them. Passes the camera on.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ExposureFitsTrigger
    {
        /// <summary>Gets or sets the board output that triggers the camera, 0 or 1.</summary>
        [Description("The board's camera trigger output: 0 for Trigger0Frequency, 1 for Trigger1Frequency.")]
        public int TriggerOutput { get; set; }

        /// <summary>Gets or sets the time the camera needs between an exposure's end and the next trigger, in microseconds.</summary>
        [Description("Time the camera needs between the end of one exposure and the next trigger, in microseconds.")]
        public double MarginMicroseconds { get; set; } = 1000;

        /// <summary>Checks each camera against its board's trigger.</summary>
        public IObservable<NamedDevice<TCamera>> Process<TCamera, TBoard>(IObservable<Tuple<NamedDevice<TCamera>, NamedDevice<TBoard>>> source)
        {
            var output = TriggerOutput;
            var margin = MarginMicroseconds;
            return source.Select(pair =>
            {
                var exposure = Read(pair.Item1.Settings, pair.Item1.Name, "ExposureTime");
                var trigger = Member(pair.Item2.Settings, pair.Item2.Name, "CameraTriggerController");
                var frequency = Read(trigger, pair.Item2.Name, "Trigger" + output + "Frequency");
                Check(pair.Item1.Name, exposure, frequency, margin);
                return pair.Item1;
            });
        }

        /// <summary>Throws, saying the longest exposure allowed, unless exposure + margin fits in one trigger period.</summary>
        public static void Check(string camera, double exposureTime, double frequency, double margin)
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

        static object Member(object settings, string device, string name)
        {
            var property = settings == null ? null : settings.GetType().GetProperty(name);
            var value = property == null ? null : property.GetValue(settings);
            if (value == null) throw new InvalidOperationException(string.Format("{0} in rig.yml has no {1}.", device, name));
            return value;
        }

        static double Read(object settings, string device, string name)
        {
            return Convert.ToDouble(Member(settings, device, name), CultureInfo.InvariantCulture);
        }
    }
}
