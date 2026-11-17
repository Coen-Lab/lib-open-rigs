using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Devices
{
    /// <summary>
    /// The lick spout stage's five axes, in motor steps from each axis's home switch. As positions
    /// they are a mouse's in position; the stage's placements are derived from them.
    /// </summary>
    public class SpoutPositions
    {
        /// <summary>The axes in file and rig.yml order.</summary>
        public static readonly string[] AxisNames = { "baseTransverse", "radialLeft", "radialRight", "elevationLeft", "elevationRight" };

        /// <summary>Gets or sets the base's transverse axis (StepperDriver-A Motor0).</summary>
        public int BaseTransverse { get; set; }

        /// <summary>Gets or sets the left spout's radial axis (StepperDriver-A Motor1).</summary>
        public int RadialLeft { get; set; }

        /// <summary>Gets or sets the right spout's radial axis (StepperDriver-A Motor2).</summary>
        public int RadialRight { get; set; }

        /// <summary>Gets or sets the left spout's elevation axis (StepperDriver-B Motor0).</summary>
        public int ElevationLeft { get; set; }

        /// <summary>Gets or sets the right spout's elevation axis (StepperDriver-B Motor1).</summary>
        public int ElevationRight { get; set; }

        /// <summary>The axes in <see cref="AxisNames"/> order.</summary>
        public int[] ToArray()
        {
            return new[] { BaseTransverse, RadialLeft, RadialRight, ElevationLeft, ElevationRight };
        }

        /// <summary>Positions from values in <see cref="AxisNames"/> order.</summary>
        public static SpoutPositions FromArray(int[] values)
        {
            return new SpoutPositions
            {
                BaseTransverse = values[0],
                RadialLeft = values[1],
                RadialRight = values[2],
                ElevationLeft = values[3],
                ElevationRight = values[4]
            };
        }

        /// <summary>These positions with the named spouts drawn back and down by <paramref name="steps"/> on both their axes.</summary>
        public SpoutPositions Retract(bool left, bool right, int steps)
        {
            return new SpoutPositions
            {
                BaseTransverse = BaseTransverse,
                RadialLeft = left ? RadialLeft - steps : RadialLeft,
                RadialRight = right ? RadialRight - steps : RadialRight,
                ElevationLeft = left ? ElevationLeft - steps : ElevationLeft,
                ElevationRight = right ? ElevationRight - steps : ElevationRight
            };
        }

        /// <summary>The positions list's header: the mouse, then the axes in <see cref="AxisNames"/> order.</summary>
        public static readonly string Header = "mouse," + string.Join(",", AxisNames);

        /// <summary>
        /// The mouse's row of a rig's positions list (LickSpoutPositions.csv beside rig.yml); null when the list
        /// does not exist or has no row for the mouse. Throws, naming the list, when it is malformed.
        /// </summary>
        public static SpoutPositions Load(string path, string mouse)
        {
            if (!File.Exists(path)) return null;
            int[] found;
            return Rows(path).TryGetValue(mouse, out found) ? FromArray(found) : null;
        }

        /// <summary>
        /// Writes these positions as the mouse's row of a rig's positions list, replacing its old row and keeping the
        /// others: the whole list goes to a temporary file beside it, which then takes the list's place, so a list
        /// is never half written. Makes the list, with its header, if there is none.
        /// </summary>
        public void Save(string path, string mouse)
        {
            if (string.IsNullOrWhiteSpace(mouse) || mouse.IndexOfAny(new[] { ',', '\r', '\n' }) >= 0)
                throw new ArgumentException("A spout positions row needs a mouse name without commas or line breaks, not '" + mouse + "'.");
            var rows = File.Exists(path) ? Rows(path) : new Dictionary<string, int[]>(StringComparer.Ordinal);
            rows[mouse] = ToArray();
            var lines = new List<string> { Header };
            lines.AddRange(rows.OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .Select(row => row.Key + "," + string.Join(",", row.Value.Select(v => v.ToString(CultureInfo.InvariantCulture)))));
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var temporary = full + ".tmp";
            File.WriteAllText(temporary, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            if (File.Exists(full)) File.Replace(temporary, full, null);
            else File.Move(temporary, full);
        }

        static Dictionary<string, int[]> Rows(string path)
        {
            var lines = File.ReadAllLines(path).Where(line => line.Trim().Length > 0).ToArray();
            if (lines.Length == 0 || lines[0].Trim() != Header) throw Malformed(path, "its first line must be " + Header);
            var rows = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',').Select(part => part.Trim()).ToArray();
                int value;
                if (parts.Length != 6 || parts[0].Length == 0 || !parts.Skip(1).All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)))
                    throw Malformed(path, "each row must be a mouse and five whole numbers, not '" + line + "'");
                if (rows.ContainsKey(parts[0])) throw Malformed(path, parts[0] + " has more than one row");
                rows[parts[0]] = parts.Skip(1).Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            }
            return rows;
        }

        static InvalidDataException Malformed(string path, string why)
        {
            return new InvalidDataException(string.Format("The spout positions list {0} is malformed: {1}.", path, why));
        }

        /// <summary>Reads rig.yml's <c>lickSpoutDefaultPosition</c>; throws naming the file and what is missing.</summary>
        public static SpoutPositions FromRigFile(string rigFile)
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(rigFile)) yaml.Load(reader);
            var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            YamlNode node = null;
            if (root == null || !root.Children.TryGetValue(new YamlScalarNode("lickSpoutDefaultPosition"), out node) || !(node is YamlMappingNode))
            {
                throw new InvalidDataException(rigFile + " has no lickSpoutDefaultPosition, the in position of a mouse with no row in LickSpoutPositions.csv.");
            }
            var entries = (YamlMappingNode)node;
            return FromArray(AxisNames.Select(axis =>
            {
                YamlNode value;
                int steps;
                if (!entries.Children.TryGetValue(new YamlScalarNode(axis), out value) ||
                    !int.TryParse(((value as YamlScalarNode) ?? new YamlScalarNode("")).Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out steps))
                {
                    throw new InvalidDataException(string.Format("{0}: lickSpoutDefaultPosition.{1} must be a whole number of steps.", rigFile, axis));
                }
                return steps;
            }).ToArray());
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Join(",", ToArray().Select(v => v.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
