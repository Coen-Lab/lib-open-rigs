using Bonsai;
using Harp.Behavior;
using OpenRigs.Devices.Rigs;
using OpenRigs.Shared;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Devices
{
    /// <summary>
    /// The Behavior board output line rig.yml's <c>wiring</c> gives one device, such as the
    /// ZapitTrigger, read once at start. Stops the workflow, naming rig.yml, unless exactly one line on
    /// the named board has that device and it is DO1, DO2 or DO3.
    /// </summary>
    [Combinator]
    [Description("The board's DO line rig.yml's wiring gives Device (such as ZapitTrigger), for each input. Stops the workflow unless exactly one line on Board has it and that line is DO1, DO2 or DO3.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class WiredOutput
    {
        /// <summary>Gets or sets the path of rig.yml.</summary>
        [Description("The path of rig.yml.")]
        public string RigFile { get; set; }

        /// <summary>Gets or sets the board's name, Kind-Instance.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; } = "BehaviorBoard-Main";

        /// <summary>Gets or sets the device wired to the line.</summary>
        [Description("The device on the line, as wiring names it, such as ZapitTrigger.")]
        public string Device { get; set; } = "ZapitTrigger";

        /// <summary>Reads the line once per input.</summary>
        public IObservable<DigitalOutputs> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(_ => Find(RigFile, Board, Device));
        }

        /// <summary>The line wired to <paramref name="device"/> on <paramref name="board"/>, or an exception saying why not.</summary>
        public static DigitalOutputs Find(string rigFile, string board, string device)
        {
            string problem;
            var devices = RigDevices.Read(rigFile, out problem);
            if (devices == null) throw new InvalidOperationException("No " + device + " line: " + problem + ".");
            var entry = devices.FirstOrDefault(d => d.Name == board);
            if (entry == null) throw new InvalidOperationException("No " + device + " line: " + rigFile + " has no board " + board + ".");

            YamlNode wiringNode;
            var wiring = entry.Settings.Children.TryGetValue(new YamlScalarNode("wiring"), out wiringNode) ? wiringNode as YamlMappingNode : null;
            var lines = wiring == null ? new string[0] : (
                from line in wiring.Children
                let settings = line.Value as YamlMappingNode
                where settings != null && settings.Children.Any(c => RigDevices.Scalar(c.Key) == "device" && RigDevices.Value(c.Value) == device)
                select RigDevices.Scalar(line.Key)).ToArray();
            if (lines.Length != 1)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in {1} has {2} lines with device {3} ({4}); it needs exactly one.",
                    board, rigFile, lines.Length, device, lines.Length == 0 ? "none" : string.Join(", ", lines)));
            }
            if (lines[0] != "DO1" && lines[0] != "DO2" && lines[0] != "DO3")
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s {1} is on {2} in {3}; it must be on DO1, DO2 or DO3.", board, device, lines[0], rigFile));
            }
            return (DigitalOutputs)Enum.Parse(typeof(DigitalOutputs), lines[0]);
        }
    }

    /// <summary>
    /// Every Behavior board output line rig.yml's <c>wiring</c> gives one device, such as the
    /// RewardValves, from the lab rig model. Stops the workflow, naming the board, unless at least one
    /// line on it has that device and every such line is DO1, DO2 or DO3.
    /// </summary>
    [Combinator]
    [Description("The board's DO lines rig.yml's wiring gives Device (such as RewardValve), from the lab rig model (RigConfigs), as one set of lines. Stops the workflow unless Board has at least one and every one is DO1, DO2 or DO3.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class WiredOutputs
    {
        /// <summary>Gets or sets the board's name, Kind-Instance.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; } = "BehaviorBoard-Main";

        /// <summary>Gets or sets the device wired to the lines.</summary>
        [Description("The device on the lines, as wiring names it, such as RewardValve.")]
        public string Device { get; set; } = "RewardValve";

        /// <summary>The lines of each rig.</summary>
        public IObservable<DigitalOutputs> Process(IObservable<LabRig> source)
        {
            return source.Select(rig => Find(rig, Board, Device));
        }

        /// <summary>The lines wired to <paramref name="device"/> on <paramref name="board"/>, or an exception saying why not.</summary>
        public static DigitalOutputs Find(LabRig rig, string board, string device)
        {
            var entry = OpenRigs.Core.RigDevice.Find(rig.BehaviorBoard, board);
            var lines = entry.Wiring == null ? new string[0] : (
                from property in typeof(BoardWiring).GetProperties()
                where property.PropertyType == typeof(WiredLine)
                let line = (WiredLine)property.GetValue(entry.Wiring)
                where line != null && line.Device == device
                select property.Name).ToArray();
            if (lines.Length == 0)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in rig.yml has no line with device {1}: add each one's line, such as DO1: {{ device: {1} }}.", board, device));
            }
            var outputs = default(DigitalOutputs);
            foreach (var line in lines)
            {
                if (line != "DO1" && line != "DO2" && line != "DO3")
                {
                    throw new InvalidOperationException(string.Format(
                        "{0}'s wiring in rig.yml puts a {1} on {2}; it must be on DO1, DO2 or DO3.", board, device, line));
                }
                outputs |= (DigitalOutputs)Enum.Parse(typeof(DigitalOutputs), line);
            }
            return outputs;
        }
    }

    /// <summary>
    /// The lines a Behavior board pulses: rig.yml's pulseController.outputPulseEnable without any line
    /// wiring gives the ZapitTrigger (held high by Zapit, and cleared when the workflow stops). Stops the
    /// workflow, naming the line, when a line wired as a RewardValve is not among them: TriggerPulse would
    /// then open the valve and leave it open.
    /// </summary>
    [Combinator]
    [Description("The board's pulse-enabled lines, rig.yml's pulseController.outputPulseEnable without the ZapitTrigger's line, from the lab rig model (RigConfigs). Stops the workflow if a RewardValve line is not pulse-enabled.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PulseEnabledLines
    {
        /// <summary>Gets or sets the board's name, Kind-Instance.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; }

        /// <summary>The pulse-enabled lines of each rig.</summary>
        public IObservable<DigitalOutputs> Process(IObservable<LabRig> source)
        {
            return source.Select(rig => Find(rig, Board));
        }

        /// <summary>The lines <paramref name="board"/> pulses, or an exception naming a valve line it would not.</summary>
        public static DigitalOutputs Find(LabRig rig, string board)
        {
            var entry = OpenRigs.Core.RigDevice.Find(rig.BehaviorBoard, board);
            var enabled = entry.PulseController == null ? default(DigitalOutputs) : entry.PulseController.OutputPulseEnable;
            var pulsed = enabled & ~WiredLines.Find(entry, "ZapitTrigger");
            var unpulsed = WiredLines.Find(entry, "RewardValve") & ~pulsed;
            if (unpulsed != default(DigitalOutputs))
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in rig.yml puts a RewardValve on {1}, which its pulseController.outputPulseEnable does not pulse: a reward would open the valve and leave it open. Add {1} to outputPulseEnable.",
                    board, unpulsed));
            }
            return pulsed;
        }
    }

    /// <summary>The lines rig.yml's wiring gives one device on a Behavior board, none or more.</summary>
    [Combinator]
    [Description("The lines the board's wiring in rig.yml gives Device (such as ZapitTrigger), none or more, from the board's named entry (its AsyncSubject).")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class WiredLines
    {
        /// <summary>Gets or sets the device wired to the lines.</summary>
        [Description("The device on the lines, as wiring names it, such as ZapitTrigger.")]
        public string Device { get; set; } = "ZapitTrigger";

        /// <summary>The lines of each board entry.</summary>
        public IObservable<DigitalOutputs> Process(IObservable<OpenRigs.Core.NamedDevice<BehaviorBoard>> source)
        {
            return source.Select(board => Find(board.Settings, Device));
        }

        /// <summary>The lines <paramref name="entry"/>'s wiring gives <paramref name="device"/>.</summary>
        public static DigitalOutputs Find(BehaviorBoard entry, string device)
        {
            var lines = default(DigitalOutputs);
            if (entry.Wiring == null) return lines;
            foreach (var property in typeof(BoardWiring).GetProperties().Where(p => p.PropertyType == typeof(WiredLine)))
            {
                var line = (WiredLine)property.GetValue(entry.Wiring);
                DigitalOutputs output;
                if (line != null && line.Device == device && Enum.TryParse(property.Name, out output)) lines |= output;
            }
            return lines;
        }
    }
}