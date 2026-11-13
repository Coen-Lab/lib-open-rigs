using System;
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
        public static readonly string[] AxisNames = { "baseTransverse", "leftRadial", "rightRadial", "leftElevation", "rightElevation" };

        /// <summary>Gets or sets the base's transverse axis (StepperDriver-A motor 0).</summary>
        public int BaseTransverse { get; set; }

        /// <summary>Gets or sets the left spout's radial axis (StepperDriver-A motor 1).</summary>
        public int LeftRadial { get; set; }

        /// <summary>Gets or sets the right spout's radial axis (StepperDriver-A motor 2).</summary>
        public int RightRadial { get; set; }

        /// <summary>Gets or sets the left spout's elevation axis (StepperDriver-B motor 0).</summary>
        public int LeftElevation { get; set; }

        /// <summary>Gets or sets the right spout's elevation axis (StepperDriver-B motor 1).</summary>
        public int RightElevation { get; set; }

        /// <summary>The axes in <see cref="AxisNames"/> order.</summary>
        public int[] ToArray()
        {
            return new[] { BaseTransverse, LeftRadial, RightRadial, LeftElevation, RightElevation };
        }

        /// <summary>Positions from values in <see cref="AxisNames"/> order.</summary>
        public static SpoutPositions FromArray(int[] values)
        {
            return new SpoutPositions
            {
                BaseTransverse = values[0],
                LeftRadial = values[1],
                RightRadial = values[2],
                LeftElevation = values[3],
                RightElevation = values[4]
            };
        }

        /// <summary>These positions with the named spouts drawn back and down by <paramref name="steps"/> on both their axes.</summary>
        public SpoutPositions Retract(bool left, bool right, int steps)
        {
            return new SpoutPositions
            {
                BaseTransverse = BaseTransverse,
                LeftRadial = left ? LeftRadial - steps : LeftRadial,
                RightRadial = right ? RightRadial - steps : RightRadial,
                LeftElevation = left ? LeftElevation - steps : LeftElevation,
                RightElevation = right ? RightElevation - steps : RightElevation
            };
        }

        /// <summary>Reads a positions file: one line, the five axes comma separated in <see cref="AxisNames"/> order.</summary>
        public static SpoutPositions Load(string path)
        {
            var lines = File.ReadAllLines(path).Where(line => line.Trim().Length > 0).ToArray();
            var parts = lines.Length == 1 ? lines[0].Split(',') : new string[0];
            int value;
            if (parts.Length != 5 || !parts.All(part => int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)))
            {
                throw new InvalidDataException(string.Format(
                    "The spout positions file {0} must be one line of five whole numbers, {1}, comma separated.",
                    path, string.Join(", ", AxisNames)));
            }
            return FromArray(parts.Select(part => int.Parse(part.Trim(), CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>Writes a positions file, making its folder if needed.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, string.Join(",", ToArray().Select(v => v.ToString(CultureInfo.InvariantCulture))) + Environment.NewLine);
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
                throw new InvalidDataException(rigFile + " has no lickSpoutDefaultPosition, the in position of a mouse with no positions file.");
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
