using Bonsai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenRigs.Core.Calibration;
using OpenRigs.Shared;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Core
{
    /// <summary>What <see cref="LoadRewardLine"/> does when the line's calibration file is not there.</summary>
    public enum MissingCalibration
    {
        /// <summary>Stop the workflow, naming the file.</summary>
        Refuse,

        /// <summary>Load the line uncalibrated: every reward on it plans no pulses and says why.</summary>
        DeliverNothing,
    }

    /// <summary>One side's reward valve line, as rig.yml wires it, and its valve calibration.</summary>
    public class RewardLine
    {
        /// <summary>Gets or sets the side, as rig.yml's wiring names it, such as left.</summary>
        public string Side { get; set; }

        /// <summary>Gets or sets the board's device name, such as BehaviorBoard-Main.</summary>
        public string Board { get; set; }

        /// <summary>Gets or sets the board's DO line, 1 to 3.</summary>
        public int Line { get; set; }

        /// <summary>Gets or sets why the line is uncalibrated; null when it is calibrated.</summary>
        public string Uncalibrated { get; set; }

        /// <summary>Gets or sets the full path of the calibration file.</summary>
        public string CalibrationPath { get; set; }

        /// <summary>Gets or sets the calibration file's text as read, for the experiment's copy; null when uncalibrated.</summary>
        public string CalibrationJson { get; set; }

        /// <summary>Gets or sets the valve calibration; null when uncalibrated.</summary>
        public CalibrationCurve Calibration { get; set; }

        /// <summary>Gets whether the line has a valve calibration.</summary>
        public bool Calibrated { get { return Calibration != null; } }

        /// <summary>Gets or sets the most microlitres one reward on the line may ask for; PlanReward refuses more.</summary>
        public double MaxRewardUl { get; set; } = LoadRewardLine.DefaultMaxRewardUl;

        /// <summary>Gets the off-to-on time between the pulses of one reward, in ms: the calibration's own; 0 when uncalibrated.</summary>
        public int PulseGapMs { get { return Calibration != null ? Calibration.PulseGapMs : 0; } }
    }

    /// <summary>
    /// Loads one side's reward line once, at start: the line rig.yml's wiring gives the side's
    /// RewardValve, which must be a CalibratedPulse in its board's pulseController.outputs, and that
    /// line's valve calibration with its linearFit. Every refusal names the line and the reason.
    /// </summary>
    /// <remarks>
    /// rig.yml is read untyped, so the operator works with any workflow's rig model. The calibration
    /// file's path is the output entry's <c>artefact</c>, relative to <see cref="CalibrationDirectory"/>.
    /// </remarks>
    [DefaultProperty("Side")]
    [Description("Loads one side's reward line once, at start: the line rig.yml's wiring gives the side's RewardValve, its CalibratedPulse entry and that valve calibration's linearFit. Stops the workflow, naming the line and the reason, if the calibration is unreadable, has no linearFit, is from another machine, or the line is not a CalibratedPulse; a missing file refuses or, with DeliverNothing, loads the line uncalibrated. Carries MaxRewardUl, the most one reward may ask for, to PlanReward.")]
    public class LoadRewardLine : Source<RewardLine>
    {
        /// <summary>The default <see cref="MaxRewardUl"/>, in ul.</summary>
        public const double DefaultMaxRewardUl = 20;

        /// <summary>Gets or sets the path of rig.yml.</summary>
        [Description("The path of this machine's rig.yml.")]
        [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string RigFile { get; set; }

        /// <summary>Gets or sets the side whose reward line to load.</summary>
        [Description("The side whose RewardValve to load, as rig.yml's wiring names it, such as left.")]
        public string Side { get; set; }

        /// <summary>Gets or sets the folder the output entry's artefact path is relative to.</summary>
        [Description("The folder the line's artefact path (such as calibration/valve-DO1.json) is relative to. Empty: rig.yml's folder.")]
        [Editor("Bonsai.Design.FolderNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string CalibrationDirectory { get; set; }

        /// <summary>Gets or sets the machine the calibration must have been measured on.</summary>
        [Description("The machine the calibration must have been measured on. Empty: this machine.")]
        public string MachineName { get; set; }

        /// <summary>Gets or sets what to do when the calibration file is not there.</summary>
        [Description("Refuse stops the workflow when the calibration file is not there; DeliverNothing loads the line uncalibrated, so its rewards deliver nothing.")]
        public MissingCalibration MissingCalibration { get; set; }

        /// <summary>Gets or sets the most microlitres one reward may ask for.</summary>
        [Description("The most microlitres one reward may ask for, 20 by default; a larger request (30 typed for 3.0) stops the workflow instead of opening the valve for it.")]
        public double MaxRewardUl { get; set; } = DefaultMaxRewardUl;

        /// <summary>Loads the line once, when subscribed.</summary>
        public override IObservable<RewardLine> Generate()
        {
            return Observable.Defer(() => Observable.Return(Load(
                RigFile, Side, CalibrationDirectory,
                string.IsNullOrEmpty(MachineName) ? Environment.MachineName : MachineName,
                MissingCalibration, MaxRewardUl)));
        }

        /// <summary>Loads one side's reward line, or throws naming the line and the reason.</summary>
        public static RewardLine Load(string rigFile, string side, string calibrationDirectory, string machineName, MissingCalibration missing, double maxRewardUl)
        {
            if (string.IsNullOrWhiteSpace(side)) throw new ArgumentException("LoadRewardLine needs a Side, as rig.yml's wiring names it, such as left.");
            if (!(maxRewardUl > 0) || double.IsInfinity(maxRewardUl))
            {
                throw new ArgumentOutOfRangeException(null, "The largest reward allowed (MaxRewardUl) is " + maxRewardUl.ToString(CultureInfo.InvariantCulture) +
                    " ul; set it to the most microlitres one reward may give, such as 20.");
            }
            string problem;
            var devices = RigDevices.Read(rigFile, out problem);
            if (devices == null) throw new InvalidOperationException("No " + side + " reward line: " + problem + ".");

            var valves = (
                from device in devices
                where device.Kind == "behaviorBoard"
                let wiring = Child(device.Settings, "wiring") as YamlMappingNode
                where wiring != null
                from entry in wiring.Children
                let settings = entry.Value as YamlMappingNode
                where settings != null &&
                      RigDevices.Value(Child(settings, "device")) == "RewardValve" &&
                      RigDevices.Value(Child(settings, "side")) == side
                select new { Board = device, Line = RigDevices.Scalar(entry.Key) }).ToList();
            if (valves.Count == 0)
            {
                throw new InvalidOperationException(
                    "No " + side + " reward line: rig.yml's wiring has no RewardValve with side " + side + " on any behaviorBoard (" + rigFile + ").");
            }
            if (valves.Count > 1)
            {
                throw new InvalidOperationException(
                    "No " + side + " reward line: rig.yml's wiring has " + valves.Count + " RewardValves with side " + side + " (" +
                    string.Join(", ", valves.Select(v => v.Board.Name + " " + v.Line)) + "); give the side one.");
            }

            var valve = valves[0];
            var line = new RewardLine { Side = side, Board = valve.Board.Name, MaxRewardUl = maxRewardUl };
            var where = "Reward line " + valve.Line + " (the " + side + " RewardValve on " + valve.Board.Name + ")";
            int number;
            if (valve.Line == null || !valve.Line.StartsWith("DO", StringComparison.Ordinal) ||
                !int.TryParse(valve.Line.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1 || number > 3)
            {
                throw new InvalidOperationException(where + " is not a pulse line: a RewardValve goes on DO1, DO2 or DO3.");
            }
            line.Line = number;

            var output = Child(Child(Child(valve.Board.Settings, "pulseController") as YamlMappingNode, "outputs") as YamlMappingNode, valve.Line) as YamlMappingNode;
            if (output == null) throw new InvalidOperationException(where + " has no entry in " + valve.Board.Name + "'s pulseController.outputs.");
            var type = RigDevices.Value(Child(output, "discriminatorType"));
            if (type != "CalibratedPulse")
            {
                throw new InvalidOperationException(where + " is a " + (type ?? "pulse output of no type") + ", not a CalibratedPulse, so its rewards cannot be calibrated.");
            }
            var artefact = RigDevices.Value(Child(output, "artefact"));
            if (artefact == null) throw new InvalidOperationException(where + " is a CalibratedPulse that names no artefact.");

            var directory = string.IsNullOrEmpty(calibrationDirectory) ? Path.GetDirectoryName(Path.GetFullPath(rigFile)) : calibrationDirectory;
            line.CalibrationPath = Path.GetFullPath(Path.IsPathRooted(artefact) ? artefact : Path.Combine(directory, artefact));
            if (!File.Exists(line.CalibrationPath))
            {
                var reason = where + " has no valve calibration: there is no " + line.CalibrationPath;
                if (missing == MissingCalibration.DeliverNothing)
                {
                    line.Uncalibrated = reason + ", so its rewards deliver nothing.";
                    return line;
                }
                throw new InvalidOperationException(reason + ". Calibrate the valve on this machine.");
            }

            var refuse = where + "'s valve calibration " + line.CalibrationPath;
            string json;
            JObject fields;
            try
            {
                json = File.ReadAllText(line.CalibrationPath);
                fields = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(refuse + " is unreadable: " + ex.Message.Split('\n')[0].Trim());
            }
            if (fields["linearFit"] == null) throw new InvalidOperationException(refuse + " has no linearFit. Calibrate the valve again to fit it.");
            CalibrationCurve calibration;
            try
            {
                calibration = JsonConvert.DeserializeObject<CalibrationCurve>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(refuse + " is unreadable: " + ex.Message.Split('\n')[0].Trim());
            }

            if (calibration.XUnit != "ms" || calibration.YUnit != "ul")
            {
                throw new InvalidOperationException(refuse + " is unreadable: it gives " + calibration.YName + " in " + calibration.YUnit +
                    " against " + calibration.XName + " in " + calibration.XUnit + ", not ul against ms.");
            }
            var fit = calibration.LinearFit;
            if (fit == null || !(fit.Slope > 0) || double.IsInfinity(fit.Slope) || double.IsNaN(fit.Intercept) || double.IsInfinity(fit.Intercept) ||
                fit.MinX < 1 || fit.MaxX < fit.MinX || fit.MaxX > ushort.MaxValue || !(fit.Slope * fit.MinX + fit.Intercept > 0))
            {
                throw new InvalidOperationException(refuse + "'s linearFit cannot be planned with: it needs a positive slope, 1 <= minX <= maxX <= 65535 ms and a positive volume at minX.");
            }
            if (!string.Equals(calibration.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(refuse + " was measured on " + calibration.MachineName + ", not on " + machineName + ". Calibrate the valve on this machine.");
            }

            line.Calibration = calibration;
            line.CalibrationJson = json;
            return line;
        }

        static YamlNode Child(YamlMappingNode map, string key)
        {
            YamlNode node;
            return map != null && map.Children.TryGetValue(new YamlScalarNode(key), out node) ? node : null;
        }
    }

    /// <summary>The pulses that deliver one reward on one line, and what they deliver.</summary>
    public class RewardPlan
    {
        /// <summary>Gets or sets the line the reward is for.</summary>
        public RewardLine RewardLine { get; set; }

        /// <summary>Gets or sets the volume asked for, in microlitres.</summary>
        public double RequestedUl { get; set; }

        /// <summary>Gets or sets the number of pulses.</summary>
        public int Count { get; set; }

        /// <summary>Gets or sets the width of each pulse, in milliseconds.</summary>
        public int WidthMs { get; set; }

        /// <summary>Gets or sets the volume each pulse delivers at that width, in microlitres.</summary>
        public double VolumePerPulseUl { get; set; }

        /// <summary>Gets or sets whether the width was raised to the calibrated range's shortest, because the volume per pulse asked for was below it.</summary>
        public bool Clamped { get; set; }

        /// <summary>Gets the volume the pulses deliver, in microlitres.</summary>
        public double DeliveredUl { get { return Count * VolumePerPulseUl; } }

        /// <summary>Gets the board's DO line, 1 to 3.</summary>
        public int Line { get { return RewardLine.Line; } }

        /// <summary>Gets the off-to-on time between pulses, in milliseconds: the calibration's.</summary>
        public int PulseGapMs { get { return RewardLine.PulseGapMs; } }
    }

    /// <summary>
    /// Plans each reward on its line: a volume in microlitres becomes equal pulses of one whole
    /// width inside the calibration's range. An uncalibrated line, or a volume of 0 or less, plans
    /// no pulses; a volume above the line's <see cref="RewardLine.MaxRewardUl"/>, or one that would
    /// take more than 65535 pulses, stops the workflow.
    /// </summary>
    [Combinator]
    [Description("Plans each reward, a (microlitres, RewardLine) pair: the fewest equal pulses inside the calibrated width range, width = (ul per pulse - intercept) / slope rounded to whole ms, raised to the shortest calibrated width (Clamped) when the volume per pulse is below it. DeliveredUl comes from the rounded width. An uncalibrated line, or 0 ul or less, plans no pulses; more than the line's MaxRewardUl stops the workflow.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PlanReward
    {
        /// <summary>Plans each (microlitres, line) pair.</summary>
        public IObservable<RewardPlan> Process(IObservable<Tuple<double, RewardLine>> source)
        {
            return source.Select(input => Plan(input.Item2, input.Item1));
        }

        /// <summary>Plans one reward of <paramref name="requestedUl"/> on <paramref name="line"/>.</summary>
        public static RewardPlan Plan(RewardLine line, double requestedUl)
        {
            var plan = new RewardPlan { RewardLine = line, RequestedUl = requestedUl };
            if (requestedUl > line.MaxRewardUl)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The task asked for a {0} ul reward on the {1} spout, more than the {2} ul one reward may give, so the workflow stopped " +
                    "without opening the valve. Check the reward size in the task's settings (30 typed for 3.0?), or raise DeliverReward's MaxRewardUl.",
                    requestedUl, line.Side, line.MaxRewardUl));
            }
            if (!line.Calibrated || !(requestedUl > 0) || double.IsInfinity(requestedUl)) return plan;

            var fit = line.Calibration.LinearFit;
            var largestUl = fit.Slope * fit.MaxX + fit.Intercept;
            var count = Math.Ceiling(requestedUl / largestUl - 1e-9);
            if (!(count <= ushort.MaxValue))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "A {0} ul reward on the {1} spout would take {2} pulses of at most {3} ul each, more than {4}; " +
                    "the valve calibration's linearFit gives too little per pulse. Calibrate the valve again.",
                    requestedUl, line.Side, count, largestUl, ushort.MaxValue));
            }
            plan.Count = Math.Max(1, (int)count);
            var width = (int)Math.Round((requestedUl / plan.Count - fit.Intercept) / fit.Slope, MidpointRounding.AwayFromZero);
            plan.Clamped = width < fit.MinX;
            plan.WidthMs = Math.Min(fit.MaxX, Math.Max(fit.MinX, width));
            plan.VolumePerPulseUl = fit.Slope * plan.WidthMs + fit.Intercept;
            return plan;
        }
    }
}
