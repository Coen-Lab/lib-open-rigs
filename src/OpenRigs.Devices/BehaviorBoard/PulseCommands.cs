using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Harp;
using OpenRigs.Devices.Rigs;

namespace OpenRigs.Devices
{
    /// <summary>
    /// A person's Pulse and Reset for one Behavior board, as Harp writes for the board's Commands subject.
    /// Pulse ("Board Line [ms]", such as "BehaviorBoard-Main DO1" or "BehaviorBoard-Main DO1 10000"): the board
    /// times every line, DO0 to DO3 and DOPort0 to DOPort2 (its PulseDO or PulseDOPort width, the line added to the
    /// pulsed lines, then OutputSet), and nothing clears it from the PC, so a pulse ends however the workflow stops.
    /// Its width: given, 1 to 60000 ms, else the line's pulseWidthMs in rig.yml, else 50 ms. Never on a line wiring
    /// gives a CameraTrigger or the ZapitTrigger, which their own pieces drive. Reset (empty, or the board's name):
    /// every DO and DOPort line but a camera trigger's cleared, each fixed line's PulseDO width and the pulsed lines
    /// put back as rig.yml has them.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("A person's Pulse and Reset for one Behavior board, as writes for its Commands subject. Inputs: RigConfigs, the Pulse texts, the Reset texts.")]
    public class PulseCommands
    {
        const int OutputSet = 34;
        const int OutputClear = 35;
        const int OutputPulseEnable = 45;
        const int PulseDOPort0 = 46;
        const int PulseDO0 = 56;
        // DOPort0-2 and DO0-3: every line a Pulse may raise.
        const ushort OutputLines = 0x3C07;

        /// <summary>Gets or sets the board's name, such as BehaviorBoard-Main.</summary>
        [Description("The board's name, such as BehaviorBoard-Main.")]
        public string Board { get; set; }

        /// <summary>The writes for each Pulse and Reset naming this board.</summary>
        public IObservable<HarpMessage> Process(IObservable<LabRig> rigs, IObservable<string> pulses, IObservable<string> resets)
        {
            var board = Board;
            return rigs.Take(1).SelectMany(rig =>
            {
                var lines = new Lines(board, OpenRigs.Core.RigDevice.Find(rig.BehaviorBoard, board));
                ushort pulsedHere = 0;
                var pulsed = pulses.SelectMany(text => lines.Pulse(text, ref pulsedHere).ToObservable(ImmediateScheduler.Instance));
                return pulsed.Merge(resets.SelectMany(text => lines.Reset(text).ToObservable(ImmediateScheduler.Instance)));
            });
        }

        // Harp.Behavior's DigitalOutputs bits: DOPort0-2 are 1, 2, 4; DO0-3 are 0x400 to 0x2000.
        static ushort Mask(string line)
        {
            int n;
            var upper = line.ToUpperInvariant();
            if (upper.StartsWith("DOPORT") && int.TryParse(upper.Substring(6), out n) && n >= 0 && n <= 2) return (ushort)(1 << n);
            if (upper.StartsWith("DO") && int.TryParse(upper.Substring(2), out n) && n >= 0 && n <= 3) return (ushort)(0x400 << n);
            return 0;
        }

        static string[] Words(string text)
        {
            return (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        class Lines
        {
            readonly string name;
            readonly ushort cameras, zapit, pulsed;
            readonly Dictionary<string, int> widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public Lines(string name, BehaviorBoard settings)
            {
                this.name = name;
                cameras = (ushort)WiredLines.Find(settings, "CameraTrigger");
                zapit = (ushort)WiredLines.Find(settings, "ZapitTrigger");
                var controller = settings.PulseController;
                pulsed = controller == null ? (ushort)0 : (ushort)((ushort)controller.OutputPulseEnable & ~zapit);
                var outputs = controller == null ? null : controller.Outputs;
                if (outputs == null) return;
                foreach (var line in new[] { Tuple.Create("DO1", (object)outputs.DO1), Tuple.Create("DO2", (object)outputs.DO2), Tuple.Create("DO3", (object)outputs.DO3) })
                {
                    var fixedPulse = line.Item2 as FixedPulse;
                    if (fixedPulse != null && fixedPulse.PulseWidthMs > 0) widths[line.Item1] = fixedPulse.PulseWidthMs;
                }
            }

            // The pulse's writes: its width, the line among the pulsed ones, the line set. Nothing, said on stderr,
            // for a command it cannot take; nothing for another board's.
            public List<HarpMessage> Pulse(string text, ref ushort pulsedHere)
            {
                var writes = new List<HarpMessage>();
                var parts = Words(text);
                if (parts.Length == 0 || parts[0] != name) return writes;
                double ms;
                var mask = parts.Length >= 2 ? Mask(parts[1]) : (ushort)0;
                if (parts.Length < 2 || parts.Length > 3 || mask == 0)
                    return Ignored(writes, text, "give the board, a line (DO0 to DO3 or DOPort0 to DOPort2) and, optionally, a width in ms");
                if ((mask & (cameras | zapit)) != 0)
                    return Ignored(writes, text, parts[1] + " is wired to a " + ((mask & cameras) != 0 ? "CameraTrigger" : "ZapitTrigger") + ", which its piece drives");
                if (parts.Length == 3)
                {
                    if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ms) || ms < 1 || ms > 60000)
                        return Ignored(writes, text, "the width is 1 to 60000 ms");
                }
                else
                {
                    int width;
                    ms = widths.TryGetValue(parts[1], out width) ? width : 50;
                }
                pulsedHere |= mask;
                writes.Add(HarpMessage.FromUInt16(PulseRegister(mask), MessageType.Write, Width(ms)));
                writes.Add(HarpMessage.FromUInt16(OutputPulseEnable, MessageType.Write, (ushort)(pulsed | pulsedHere)));
                writes.Add(HarpMessage.FromUInt16(OutputSet, MessageType.Write, mask));
                return writes;
            }

            public List<HarpMessage> Reset(string text)
            {
                var writes = new List<HarpMessage>();
                var named = (text ?? string.Empty).Trim();
                if (named.Length > 0 && named != name) return writes;
                writes.Add(HarpMessage.FromUInt16(OutputClear, MessageType.Write, (ushort)(OutputLines & ~cameras)));
                foreach (var pair in widths) writes.Add(HarpMessage.FromUInt16(PulseRegister(Mask(pair.Key)), MessageType.Write, Width(pair.Value)));
                writes.Add(HarpMessage.FromUInt16(OutputPulseEnable, MessageType.Write, pulsed));
                Console.Error.WriteLine("PulseCommands: " + name + " reset: DO and DOPort lines closed, pulse widths and pulsed lines as rig.yml has them");
                return writes;
            }

            static List<HarpMessage> Ignored(List<HarpMessage> writes, string text, string why)
            {
                Console.Error.WriteLine("PulseCommands: Pulse '" + text + "' ignored: " + why);
                return writes;
            }

            // PulseDOPort0-2 (46-48) for DOPort0-2, PulseDO0-3 (56-59) for DO0-3.
            static int PulseRegister(ushort mask)
            {
                var first = mask >= 0x400 ? 0x400 : 1;
                var n = 0;
                while ((first << n) != mask) n++;
                return (mask >= 0x400 ? PulseDO0 : PulseDOPort0) + n;
            }

            static ushort Width(double ms)
            {
                return (ushort)Math.Max(1, Math.Min(65535, Math.Round(ms)));
            }
        }
    }
}
