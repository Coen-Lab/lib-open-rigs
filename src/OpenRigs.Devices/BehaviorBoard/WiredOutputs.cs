using Bonsai;
using Harp.Behavior;
using OpenRigs.Devices.Rigs;
using OpenRigs.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Devices
{
    /// <summary>
    /// The Behavior board output line rig.yml's <c>wiring</c> gives one device, such as the
    /// ZapitTrigger, read once at start, or None when no line has it. Stops the workflow, naming rig.yml,
    /// when more than one line on the named board has that device or its line is not DO1, DO2 or DO3.
    /// </summary>
    [Combinator]
    [Description("The board's DO line rig.yml's wiring gives Device (such as ZapitTrigger), for each input, or None when no line has it. Stops the workflow if more than one line on Board has it or its line is not DO1, DO2 or DO3.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class WiredOutput
    {
        /// <summary>Gets or sets the path of rig.yml.</summary>
        [Description("The path of rig.yml.")]
        public string RigFile { get; set; }

        /// <summary>Gets or sets the board's name, Kind-Instance.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; }

        /// <summary>Gets or sets the device wired to the line.</summary>
        [Description("The device on the line, as wiring names it, such as ZapitTrigger.")]
        public string Device { get; set; } = "ZapitTrigger";

        /// <summary>Reads the line once per input.</summary>
        public IObservable<DigitalOutputs> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(_ => Find(RigFile, Board, Device));
        }

        /// <summary>The line wired to <paramref name="device"/> on <paramref name="board"/>, None for no line, or an exception saying why not.</summary>
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
            if (lines.Length == 0) return default(DigitalOutputs);
            if (lines.Length > 1)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in {1} has {2} lines with device {3} ({4}); it needs at most one.",
                    board, rigFile, lines.Length, device, string.Join(", ", lines)));
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
    /// line on it has that device and every such line is one the board can pulse, DO0 to DO3 or DOPort0 to DOPort2.
    /// </summary>
    [Combinator]
    [Description("The board's lines rig.yml's wiring gives Device (such as RewardValve), from the lab rig model (RigConfigs), as one set of lines. Stops the workflow unless Board has at least one and every one is a line the board can pulse, DO0 to DO3 or DOPort0 to DOPort2.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class WiredOutputs
    {
        /// <summary>Gets or sets the board's name, Kind-Instance.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; }

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
                if (PulseLines.Mask(line) == 0)
                {
                    throw new InvalidOperationException(string.Format(
                        "{0}'s wiring in rig.yml puts a {1} on {2}; it must be on a line the board can pulse, DO0 to DO3 or DOPort0 to DOPort2.", board, device, line));
                }
                outputs |= (DigitalOutputs)Enum.Parse(typeof(DigitalOutputs), line);
            }
            return outputs;
        }
    }

    /// <summary>One reward valve as rig.yml wires it: its piece, its board and its line.</summary>
    public class RewardValveWiring
    {
        /// <summary>Gets or sets the valve's piece, such as RewardValve-Left.</summary>
        public string Valve { get; set; }

        /// <summary>Gets or sets the board it is wired on, such as BehaviorBoard-Main.</summary>
        public string Board { get; set; }

        /// <summary>Gets or sets its line, such as DO1.</summary>
        public DigitalOutputs Line { get; set; }
    }

    /// <summary>
    /// A reward valve found by its piece name (RewardValve-Left) on whichever Behavior board rig.yml's
    /// wiring puts it, as LabRig.reward_valves() finds it in Python. Stops the workflow, naming the valves
    /// there are, unless exactly one board wires it, on a line the board can pulse, and that board is
    /// Board when Board is set.
    /// </summary>
    [Combinator]
    [Description("The board and line rig.yml's wiring gives the RewardValve Valve (such as RewardValve-Left), on any board, from the lab rig model (RigConfigs). Stops the workflow unless exactly one board wires it, on a line the board can pulse, and, when Board is set, that board is Board.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class WiredRewardValve
    {
        /// <summary>Gets or sets the valve's piece.</summary>
        [Description("The valve's piece, RewardValve- and its name in rig.yml's wiring: name: left is RewardValve-Left.")]
        public string Valve { get; set; }

        /// <summary>Gets or sets the board the workflow fires the valve through, or empty for any.</summary>
        [Description("The board the workflow fires the valve through, such as BehaviorBoard-Main; a valve on another board stops the workflow. Empty: any board.")]
        public string Board { get; set; }

        /// <summary>The valve's wiring in each rig.</summary>
        public IObservable<RewardValveWiring> Process(IObservable<LabRig> source)
        {
            return source.Select(rig => Find(rig, Valve, Board));
        }

        const string ValvePrefix = "RewardValve-";

        /// <summary>The wiring of <paramref name="valve"/>, or an exception saying why not.</summary>
        public static RewardValveWiring Find(LabRig rig, string valve, string board = null)
        {
            if (string.IsNullOrWhiteSpace(valve) || !valve.StartsWith(ValvePrefix, StringComparison.Ordinal) || valve.Length == ValvePrefix.Length)
            {
                throw new ArgumentException("WiredRewardValve's Valve is '" + valve + "': it needs the valve's piece, RewardValve- and its name in rig.yml's wiring, such as RewardValve-Left.");
            }
            var name = valve.Substring(ValvePrefix.Length);
            var valves = (
                from entry in rig.BehaviorBoard ?? new Dictionary<string, BehaviorBoard>()
                where entry.Value != null && entry.Value.Wiring != null
                from property in typeof(BoardWiring).GetProperties()
                where property.PropertyType == typeof(WiredLine)
                let line = (WiredLine)property.GetValue(entry.Value.Wiring)
                where line != null && line.Device == "RewardValve" && line.Name != null
                select new
                {
                    Valve = ValvePrefix + UpperFirst(line.Name),
                    Board = "BehaviorBoard-" + UpperFirst(entry.Key),
                    Line = property.Name,
                }).ToList();
            var found = valves.Where(v => string.Equals(v.Valve, valve, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count != 1)
            {
                throw new InvalidOperationException(string.Format(
                    "rig.yml's wiring has {0} RewardValve named {1} ({2}); it needs exactly one. Its valves: {3}.",
                    found.Count == 0 ? "no" : found.Count.ToString(), name.ToLowerInvariant(), valve,
                    valves.Count == 0 ? "none" : string.Join(", ", valves.Select(v => v.Valve + " (" + v.Line + " on " + v.Board + ")"))));
            }
            var wired = found[0];
            var where = wired.Valve + " (" + wired.Line + " on " + wired.Board + ")";
            if (!string.IsNullOrEmpty(board) && wired.Board != board)
            {
                throw new InvalidOperationException(where + " is not on " + board + ", the board this workflow fires valves through.");
            }
            if (PulseLines.Mask(wired.Line) == 0)
            {
                throw new InvalidOperationException(where + " is not on a line the board can pulse, DO0 to DO3 or DOPort0 to DOPort2.");
            }
            return new RewardValveWiring { Valve = wired.Valve, Board = wired.Board, Line = (DigitalOutputs)Enum.Parse(typeof(DigitalOutputs), wired.Line) };
        }

        static string UpperFirst(string text)
        {
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }

    /// <summary>
    /// The lines a Behavior board pulses, rig.yml's pulseController.outputPulseEnable, checked against its wiring: a
    /// line wired as a RewardValve must be among them, or a reward would open the valve and leave it open, and so must
    /// the ZapitTrigger's, with a FixedPulse width above 0 ms, or a workflow that died with it high would leave it on.
    /// </summary>
    public static class PulseEnabledLines
    {
        /// <summary>The lines <paramref name="board"/> pulses, or an exception naming a valve or Zapit line it would not.</summary>
        public static DigitalOutputs Find(LabRig rig, string board)
        {
            var entry = OpenRigs.Core.RigDevice.Find(rig.BehaviorBoard, board);
            var pulsed = entry.PulseController == null ? default(DigitalOutputs) : entry.PulseController.OutputPulseEnable;
            var unpulsed = WiredLines.Find(entry, "RewardValve") & ~pulsed;
            if (unpulsed != default(DigitalOutputs))
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in rig.yml puts a RewardValve on {1}, which its pulseController.outputPulseEnable does not pulse: a reward would open the valve and leave it open. Add {1} to outputPulseEnable.",
                    board, unpulsed));
            }
            var zapit = WiredLines.Find(entry, "ZapitTrigger");
            if ((zapit & ~pulsed) != default(DigitalOutputs))
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s wiring in rig.yml puts the ZapitTrigger on {1}, which its pulseController.outputPulseEnable does not pulse: a workflow that died with it high would leave it, and any masking light on it, on. Add {1} to outputPulseEnable.",
                    board, zapit & ~pulsed));
            }
            if (zapit != default(DigitalOutputs)) ZapitPulseWidthMs(entry, board, zapit);
            return pulsed;
        }

        /// <summary>The width in ms the board ends the ZapitTrigger line <paramref name="zapit"/> after, its FixedPulse in
        /// rig.yml's pulseController.outputs, or an exception when it has none above 0 ms.</summary>
        public static int ZapitPulseWidthMs(BehaviorBoard entry, string board, DigitalOutputs zapit)
        {
            var outputs = entry.PulseController == null ? null : entry.PulseController.Outputs;
            var property = typeof(PulseOutputs).GetProperty(zapit.ToString());
            var fixedPulse = outputs == null || property == null ? null : property.GetValue(outputs) as FixedPulse;
            if (fixedPulse == null || fixedPulse.PulseWidthMs < 1)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}'s ZapitTrigger on {1} needs a FixedPulse above 0 ms in rig.yml's pulseController.outputs, at least the longest a trial holds the line, so the board ends it if the workflow dies. Add it, as in {1}: {{ discriminatorType: FixedPulse, pulseWidthMs: 10000 }}.",
                    board, zapit));
            }
            return fixedPulse.PulseWidthMs;
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