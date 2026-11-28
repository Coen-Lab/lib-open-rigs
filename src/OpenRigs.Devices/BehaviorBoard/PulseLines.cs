using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using OpenRigs.Devices.Rigs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>The Behavior board's lines that can pulse, DO0 to DO3 and DOPort0 to DOPort2, as Harp writes.</summary>
    static class PulseLines
    {
        internal const int OutputSet = 34;
        internal const int OutputClear = 35;
        internal const int OutputPulseEnable = 45;
        const int PulseDOPort0 = 46;
        const int PulseDO0 = 56;

        /// <summary>Harp.Behavior's DigitalOutputs bit of a line (DOPort0-2 are 1, 2, 4; DO0-3 are 0x400 to 0x2000), or 0 for any other.</summary>
        internal static ushort Mask(string line)
        {
            int n;
            var upper = (line ?? string.Empty).ToUpperInvariant();
            if (upper.StartsWith("DOPORT") && int.TryParse(upper.Substring(6), out n) && n >= 0 && n <= 2) return (ushort)(1 << n);
            if (upper.StartsWith("DO") && int.TryParse(upper.Substring(2), out n) && n >= 0 && n <= 3) return (ushort)(0x400 << n);
            return 0;
        }

        /// <summary>The width register of one line's bit: PulseDOPort0-2 (46-48) or PulseDO0-3 (56-59).</summary>
        internal static int PulseRegister(ushort mask)
        {
            var first = mask >= 0x400 ? 0x400 : 1;
            var n = 0;
            while ((first << n) != mask) n++;
            return (mask >= 0x400 ? PulseDO0 : PulseDOPort0) + n;
        }
    }

    /// <summary>
    /// A Behavior board's pulsed lines put in pulse mode, for each rig: the writes that close DO1 to DO3 and the pulsed
    /// lines (PulseEnabledLines; a line left high by a run that died), set the ZapitTrigger line's PulseDO width to its
    /// FixedPulse in rig.yml, and then pulse-enable the lines, so a line in pulse mode ends each pulse itself: the
    /// ZapitTrigger's after its width, unless the workflow clears it first.
    /// </summary>
    [Combinator]
    [Description("The board's pulsed lines (rig.yml's pulseController.outputPulseEnable, from the lab rig model, RigConfigs) as writes for its Commands subject: DO1 to DO3 and the lines closed (OutputClear), the ZapitTrigger line's width (its FixedPulse), then the lines pulse-enabled (OutputPulseEnable). Stops the workflow if a RewardValve or ZapitTrigger line is not pulse-enabled or the ZapitTrigger line has no width.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PulseSetupWrites
    {
        /// <summary>Gets or sets the board's name, Kind-Instance.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; }

        /// <summary>The writes for each rig.</summary>
        public IObservable<HarpMessage> Process(IObservable<LabRig> source)
        {
            var board = Board;
            return source.SelectMany(rig => Writes(rig, board));
        }

        /// <summary>The writes that put <paramref name="board"/>'s pulsed lines in pulse mode, or an exception naming why not.</summary>
        public static HarpMessage[] Writes(LabRig rig, string board)
        {
            var lines = PulseEnabledLines.Find(rig, board);
            var entry = OpenRigs.Core.RigDevice.Find(rig.BehaviorBoard, board);
            var zapit = WiredLines.Find(entry, "ZapitTrigger");
            var writes = new List<HarpMessage>
            {
                HarpMessage.FromUInt16(PulseLines.OutputClear, MessageType.Write, (ushort)(lines | DigitalOutputs.DO1 | DigitalOutputs.DO2 | DigitalOutputs.DO3)),
            };
            if (zapit != default(DigitalOutputs))
            {
                writes.Add(HarpMessage.FromUInt16(PulseLines.PulseRegister((ushort)zapit), MessageType.Write, (ushort)PulseEnabledLines.ZapitPulseWidthMs(entry, board, zapit)));
            }
            writes.Add(HarpMessage.FromUInt16(PulseLines.OutputPulseEnable, MessageType.Write, (ushort)lines));
            return writes.ToArray();
        }
    }

    /// <summary>
    /// Each calibrated pulse, a (line, width in ms) pair such as (DO1, 38), as the writes that fire it on a Behavior
    /// board: the line's width register (PulseDO or PulseDOPort), then the line set (OutputSet), in that order, so the
    /// board ends the pulse after that width. Any line the board can pulse, DO0 to DO3 and DOPort0 to DOPort2.
    /// </summary>
    [Combinator]
    [Description("Each (line, pulseWidthMs) pulse as two writes for the board's Commands subject: the line's PulseDO or PulseDOPort width, then OutputSet. Lines DO0 to DO3 and DOPort0 to DOPort2; any other, or a width outside 1 to 65535 ms, stops the workflow.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CalibratedPulseWrites
    {
        /// <summary>The writes for each pulse.</summary>
        public IObservable<HarpMessage> Process(IObservable<Tuple<string, int>> source)
        {
            return source.SelectMany(pulse => Writes(pulse.Item1, pulse.Item2));
        }

        /// <summary>The two writes that fire <paramref name="line"/> for <paramref name="widthMs"/>, or an exception naming why not.</summary>
        public static HarpMessage[] Writes(string line, int widthMs)
        {
            var mask = PulseLines.Mask(line);
            if (mask == 0)
            {
                throw new InvalidOperationException("A calibrated pulse on '" + line + "', which is no line a Behavior board can pulse: DO0 to DO3 or DOPort0 to DOPort2.");
            }
            if (widthMs < 1 || widthMs > ushort.MaxValue)
            {
                throw new InvalidOperationException("A calibrated pulse of " + widthMs + " ms on " + line + ": a pulse is 1 to 65535 ms.");
            }
            return new[]
            {
                HarpMessage.FromUInt16(PulseLines.PulseRegister(mask), MessageType.Write, (ushort)widthMs),
                HarpMessage.FromUInt16(PulseLines.OutputSet, MessageType.Write, mask),
            };
        }
    }
}
