using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRigs.Shared;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.HarpDevices
{
    /// <summary>One line a workflow uses, from its schemas\board-lines.yml, resolved to the board's channel.</summary>
    class BoardLineUse
    {
        public string Line;          // the channel, DO1
        public string From;          // as the workflow file gives it, rewardValves.left
        public string Device;        // RewardValve
        public string Side;          // left, or null
        public List<string> Targets; // Camera-Face..., or null
    }

    /// <summary>What the wiring check found for one board: whether it applied, what it compared, and every disagreement.</summary>
    class WiringResult
    {
        public bool Applies;
        public string Summary;
        public readonly List<string> Problems = new List<string>();

        public string Refusal(string board)
        {
            if (Problems.Count == 0) return null;
            // One line: rig-control keeps only the first line of a workflow's error (audit, 8 Nov 2026).
            return board + "'s wiring in rig.yml does not agree with the lines this workflow uses (" + WiringCheck.LinesFile + "), so it does not start: " +
                string.Join("; ", Problems) +
                ". Fix whichever is wrong: rig.yml's wiring says what is plugged in on this rig, the workflow's file what it drives or reads on each line.";
        }
    }

    /// <summary>
    /// Compares a Behavior board's wiring in rig.yml (what is plugged into each line) with the lines the
    /// workflow says it uses (schemas\board-lines.yml in its repository; Pip, 7 Nov 2026). Every line
    /// the workflow uses must be in the wiring with the same device, side and targets; a line it does not
    /// use may be anything or unknown. A workflow without the file, or a board the file does not list,
    /// is not checked; a file that cannot be read, or a board's entry that is not a list, refuses.
    /// Nothing yet holds a device word to a lab list: wiredDevices in the device dictionary, checked by
    /// rig-control's names check before launch, is planned (DECISIONS.md, 7 Nov 2026), so Start never
    /// depends on the share.
    /// </summary>
    static class WiringCheck
    {
        public const string LinesFile = @"schemas\board-lines.yml";

        public static readonly string[] Channels =
        {
            "DIPort0", "DIPort1", "DIPort2", "DI3", "DIO0", "DIO1", "DIO2", "DO0", "DO1", "DO2", "DO3",
            "DOPort0", "DOPort1", "DOPort2", "SupplyPort0", "SupplyPort1", "SupplyPort2",
            "Led0", "Led1", "Rgb0", "Rgb1", "AnalogInput0", "AnalogInput1",
        };

        /// <summary>The check for one board; <paramref name="linesYaml"/> null when the workflow has no board-lines.yml.</summary>
        public static WiringResult Check(YamlMappingNode rig, RigDevice board, IEnumerable<string> rigDeviceNames, string linesYaml, string workflow)
        {
            var result = new WiringResult();
            if (linesYaml == null)
            {
                result.Summary = "not checked: " + (workflow ?? "the workflow") + " has no " + LinesFile;
                return result;
            }
            YamlNode root;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(linesYaml));
                root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode : null;
            }
            catch (Exception ex)
            {
                return Unreadable(ex);
            }
            // A file or a board's key there in the wrong shape refuses, rather than turning the check off (L2-L1).
            var lines = root as YamlMappingNode;
            if (root != null && lines == null)
            {
                result.Applies = true;
                result.Problems.Add(LinesFile + " must map each board's name to a list of entries");
                result.Summary = "refused: " + result.Problems[0];
                return result;
            }
            var listed = lines == null ? null : Child(lines, board.Name);
            if (listed == null)
            {
                result.Summary = "not checked: " + LinesFile + " names no lines for " + board.Name;
                return result;
            }
            result.Applies = true;
            var entries = listed as YamlSequenceNode;
            if (entries == null)
            {
                result.Problems.Add(LinesFile + "'s " + board.Name + " must be a list of entries");
                result.Summary = "refused: " + result.Problems[0];
                return result;
            }
            var uses = new List<BoardLineUse>();
            foreach (var node in entries)
            {
                var entry = node as YamlMappingNode;
                var from = entry == null ? null : RigDevices.Value(Child(entry, "line"));
                var device = entry == null ? null : RigDevices.Value(Child(entry, "device"));
                if (from == null || device == null)
                {
                    result.Problems.Add(LinesFile + ": every entry for " + board.Name + " needs line and device");
                    continue;
                }
                string why;
                var line = Resolve(from, rig, board.Settings, out why);
                if (why != null) { result.Problems.Add(why); continue; }
                if (line == null) continue;  // a camera trigger rig.yml does not enable, or a Zapit TTL not hardware triggered: the line is not used
                uses.Add(new BoardLineUse
                {
                    Line = line,
                    From = from,
                    Device = device,
                    Side = RigDevices.Value(Child(entry, "side")),
                    Targets = Strings(Child(entry, "targets")),
                });
            }
            foreach (var clash in uses.GroupBy(u => u.Line).Where(g => g.Select(u => u.Device + "/" + u.Side).Distinct().Count() > 1))
            {
                result.Problems.Add(clash.Key + " is used for two things by the workflow: " + string.Join(" and ", clash.Select(Describe)));
            }

            var wiring = Child(board.Settings, "wiring") as YamlMappingNode;
            var names = new HashSet<string>(rigDeviceNames, StringComparer.Ordinal);
            foreach (var group in uses.GroupBy(u => u.Line))
            {
                // Entries for one line agree on device and side (above); every entry's targets are needed.
                var use = group.First();
                var needed = group.SelectMany(u => u.Targets ?? new List<string>()).Distinct().ToList();
                var wired = wiring == null ? null : Child(wiring, use.Line) as YamlMappingNode;
                var where = use.From == use.Line ? use.Line : use.Line + " (" + use.From + ")";
                if (wired == null)
                {
                    result.Problems.Add(where + " is used as " + Describe(use) + ", but rig.yml's wiring does not say what is on " + use.Line);
                    continue;
                }
                var device = RigDevices.Value(Child(wired, "device"));
                var side = RigDevices.Value(Child(wired, "side"));
                var targets = Strings(Child(wired, "targets")) ?? new List<string>();
                if (device != use.Device)
                    result.Problems.Add(where + " is used as " + Describe(use) + ", but rig.yml's wiring has " + (device ?? "no device") + " on it");
                else if (use.Side != null && side != use.Side)
                    result.Problems.Add(where + " is used as the " + use.Side + " " + use.Device + ", but rig.yml's wiring has the " + (side ?? "unsided") + " one on it");
                foreach (var missing in needed.Where(t => !targets.Contains(t)))
                    result.Problems.Add(where + " must drive or read " + missing + ", which rig.yml's wiring for " + use.Line + " does not list");
                foreach (var unknown in targets.Where(t => !names.Contains(t)))
                    result.Problems.Add("rig.yml's wiring for " + use.Line + " names " + unknown + ", which is not a device in this rig.yml");
            }
            result.Summary = result.Problems.Count == 0
                ? "checked: " + string.Join(", ", uses.Select(u => u.Line + " " + Describe(u)).Distinct()) + " agree with rig.yml's wiring"
                : "refused: " + result.Problems.Count + " disagreement(s)";
            return result;
        }

        /// <summary>
        /// The channel a workflow's line names: a channel itself (DIPort0); cameraTriggerController.trigger0 or
        /// trigger1, DO0 or DO1 when rig.yml enables it, else null (not used); zapit.triggerLine only while
        /// rig.yml's zapit.hardwareTriggered is true, else null (not cabled); or a key in rig.yml, the board's
        /// entry first and then the top level, holding a DO line (rewardValves.left: DO1; zapit.triggerLine: 3).
        /// </summary>
        public static string Resolve(string from, YamlMappingNode rig, YamlMappingNode board, out string why)
        {
            why = null;
            var channel = Channel(from);
            if (channel != null) return channel;
            if (from == "cameraTriggerController.trigger0" || from == "cameraTriggerController.trigger1")
            {
                var n = from[from.Length - 1];
                var controller = Child(board, "cameraTriggerController") as YamlMappingNode;
                if (controller == null) return null;
                var enabled = RigDevices.Value(Child(controller, "trigger" + n + "Enabled"));
                // open-rigs' defaults: trigger 0 on, trigger 1 off.
                var on = enabled == null ? n == '0' : string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);
                return on ? "DO" + n : null;
            }
            if (from == "zapit.triggerLine")
            {
                // The Zapit TTL is cabled only where hardwareTriggered is true (audit, 8 Nov 2026).
                var triggered = Path(board, "zapit.hardwareTriggered") ?? Path(rig, "zapit.hardwareTriggered");
                if (!string.Equals(triggered, "true", StringComparison.OrdinalIgnoreCase)) return null;
            }
            var value = Path(board, from) ?? Path(rig, from);
            if (value == null)
            {
                why = "the workflow uses the line rig.yml gives as " + from + ", but this rig.yml has no " + from;
                return null;
            }
            var line = Channel(value) ?? (value.All(char.IsDigit) ? Channel("DO" + value) : null);
            if (line == null) why = "rig.yml's " + from + " is " + value + ", which is not a line of the board";
            return line;
        }

        /// <summary>The refusal when board-lines.yml is there and cannot be read or parsed.</summary>
        public static WiringResult Unreadable(Exception ex)
        {
            var result = new WiringResult { Applies = true };
            result.Problems.Add(LinesFile + " could not be read: " + ex.Message.Split('\n')[0]);
            result.Summary = "refused: " + result.Problems[0];
            return result;
        }

        static string Channel(string text)
        {
            return Channels.FirstOrDefault(c => string.Equals(c, (text ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        }

        static string Path(YamlMappingNode root, string dotted)
        {
            YamlNode node = root;
            foreach (var segment in dotted.Split('.'))
            {
                var map = node as YamlMappingNode;
                if (map == null) return null;
                node = Child(map, segment);
                if (node == null) return null;
            }
            return RigDevices.Value(node);
        }

        static YamlNode Child(YamlMappingNode map, string key)
        {
            YamlNode node;
            return map != null && map.Children.TryGetValue(new YamlScalarNode(key), out node) ? node : null;
        }

        static List<string> Strings(YamlNode node)
        {
            var sequence = node as YamlSequenceNode;
            return sequence == null ? null : sequence.Select(RigDevices.Value).Where(v => v != null).ToList();
        }

        static string Describe(BoardLineUse use)
        {
            return (use.Side != null ? use.Side + " " : "") + use.Device;
        }

        /// <summary>The workflow's board-lines.yml as text, and the repository's name; null text when there is none.</summary>
        public static string ReadLines(string repositoryRoot, out string workflow)
        {
            workflow = repositoryRoot == null ? null : System.IO.Path.GetFileName(repositoryRoot.TrimEnd('\\', '/'));
            if (repositoryRoot == null) return null;
            var file = System.IO.Path.Combine(repositoryRoot, LinesFile);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }

        /// <summary>rig.yml's top level, for keys outside the board's entry (rewardValves, zapit); null if it cannot be read.</summary>
        public static YamlMappingNode ReadRig(string path)
        {
            try
            {
                var stream = new YamlStream();
                using (var reader = new StreamReader(path)) stream.Load(reader);
                return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
