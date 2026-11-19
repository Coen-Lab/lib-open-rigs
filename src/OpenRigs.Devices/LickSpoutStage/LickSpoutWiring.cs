using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Devices
{
    /// <summary>One axis's motor: its driver, its motor number and how far from home it may go.</summary>
    public class AxisMotor
    {
        /// <summary>Gets the driver the motor is on.</summary>
        public LickSpoutDriver Driver { get; internal set; }

        /// <summary>Gets the motor's number on its driver, 0 to 3; its home switch is the input of the same number.</summary>
        public int Motor { get; internal set; }

        /// <summary>Gets the farthest the motor may go from its home switch, in steps, or null for no limit.</summary>
        public int? MaxSteps { get; internal set; }
    }

    /// <summary>
    /// Which motor of which driver drives each axis of the stage, read from the <c>wiring</c> of
    /// rig.yml's <c>stepperDriver</c> entries a and b. A motor wired to anything else is left alone.
    /// </summary>
    public class LickSpoutWiring
    {
        // Each axis's device and side in wiring, in SpoutPositions.AxisNames order; its home switch is the device + "Home".
        static readonly string[] Devices = { "BaseTransverse", "Radial", "Radial", "Elevation", "Elevation" };
        static readonly string[] Sides = { null, "left", "right", "left", "right" };

        /// <summary>Gets each axis's motor, in <see cref="SpoutPositions.AxisNames"/> order.</summary>
        public AxisMotor[] Axes { get; private set; }

        /// <summary>The axes' motors on <paramref name="driver"/>, with each axis's index.</summary>
        public IEnumerable<KeyValuePair<int, AxisMotor>> On(LickSpoutDriver driver)
        {
            return Axes.Select((axis, i) => new KeyValuePair<int, AxisMotor>(i, axis)).Where(pair => pair.Value.Driver == driver);
        }

        /// <summary>Reads the wiring; throws naming the file and what is wrong.</summary>
        public static LickSpoutWiring FromRigFile(string rigFile)
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(rigFile)) yaml.Load(reader);
            var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            var axes = new AxisMotor[Devices.Length];
            foreach (var driver in new[] { LickSpoutDriver.A, LickSpoutDriver.B })
            {
                var entry = driver.ToString().ToLowerInvariant();
                var wiring = Child(Child(Child(root, "stepperDriver"), entry), "wiring");
                if (wiring == null) throw Wrong(rigFile, string.Format("stepperDriver.{0} has no wiring: which motor drives which spout axis", entry));
                for (var motor = 0; motor < 4; motor++)
                {
                    var line = Child(wiring, "Motor" + motor);
                    var axis = Enumerable.Range(0, Devices.Length).Where(i => Text(line, "device") == Devices[i] && Text(line, "side") == Sides[i]).DefaultIfEmpty(-1).First();
                    if (axis < 0) continue;
                    var name = string.Format("stepperDriver.{0}.wiring.Motor{1}", entry, motor);
                    if (axes[axis] != null) throw Wrong(rigFile, string.Format("{0} and stepperDriver.{1}.wiring.Motor{2} both drive the {3} axis", name, axes[axis].Driver.ToString().ToLowerInvariant(), axes[axis].Motor, SpoutPositions.AxisNames[axis]));
                    var home = Child(wiring, "Input" + motor);
                    if (Text(home, "device") != Devices[axis] + "Home" || Text(home, "side") != Sides[axis])
                    {
                        throw Wrong(rigFile, string.Format("{0} drives the {1} axis, so its home switch must be Input{2}: {{ device: {3}Home{4} }}", name, SpoutPositions.AxisNames[axis], motor, Devices[axis], Sides[axis] == null ? "" : ", side: " + Sides[axis]));
                    }
                    int? maxSteps = null;
                    var max = Text(line, "maxSteps");
                    if (max != null)
                    {
                        int steps;
                        if (!int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out steps) || steps < 1)
                        {
                            throw Wrong(rigFile, name + ".maxSteps must be a whole number of steps, 1 or more");
                        }
                        maxSteps = steps;
                    }
                    axes[axis] = new AxisMotor { Driver = driver, Motor = motor, MaxSteps = maxSteps };
                }
            }
            var missing = Enumerable.Range(0, Devices.Length).Where(i => axes[i] == null).ToList();
            if (missing.Count > 0)
            {
                throw Wrong(rigFile, "no stepperDriver motor is wired to " + string.Join(", ", missing.Select(i =>
                    string.Format("{0} ({{ device: {1}{2} }})", SpoutPositions.AxisNames[i], Devices[i], Sides[i] == null ? "" : ", side: " + Sides[i]))));
            }
            return new LickSpoutWiring { Axes = axes };
        }

        /// <summary>Each value held to its axis's limit.</summary>
        public SpoutPositions Limit(SpoutPositions positions)
        {
            return SpoutPositions.FromArray(positions.ToArray().Select((steps, i) => Axes[i].MaxSteps.HasValue ? System.Math.Min(steps, Axes[i].MaxSteps.Value) : steps).ToArray());
        }

        static YamlMappingNode Child(YamlMappingNode node, string key)
        {
            YamlNode child = null;
            return node != null && node.Children.TryGetValue(new YamlScalarNode(key), out child) ? child as YamlMappingNode : null;
        }

        static string Text(YamlMappingNode node, string key)
        {
            YamlNode child = null;
            return node != null && node.Children.TryGetValue(new YamlScalarNode(key), out child) && child is YamlScalarNode ? ((YamlScalarNode)child).Value : null;
        }

        static InvalidDataException Wrong(string rigFile, string why)
        {
            return new InvalidDataException(string.Format("{0}: {1}.", rigFile, why));
        }
    }
}
