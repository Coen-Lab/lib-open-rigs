using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using OpenRigs.Shared;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.LiveView
{
    /// <summary>How one kind of device is streamed, and its defaults.</summary>
    class DeviceRecipe
    {
        public string Kind;      // the key in rig.yml, e.g. camera
        public string Template;  // Templates\<Template>.xml
        public string Stream;    // after the device's name: Camera-Box.video
        public double Rate;      // messages per second
        public int Width;        // video only
        public int Height;
        public Func<Type, string> Accepts;  // null if the data can be streamed, else why not

        public bool IsVideo { get { return Width > 0; } }

        public static readonly DeviceRecipe[] All =
        {
            new DeviceRecipe
            {
                Kind = "camera", Template = "Camera", Stream = "video", Rate = 5, Width = 320, Height = 240,
                Accepts = t => ImagePath(t) == null ? "its data (" + t.Name + ") holds no IplImage" : null,
            },
            new DeviceRecipe
            {
                Kind = "behaviorBoard", Template = "BehaviorBoard", Stream = "encoder", Rate = 10,
                Accepts = t => t.FullName == "Bonsai.Harp.HarpMessage" ? null : "its data (" + t.Name + ") is not HarpMessage",
            },
        };

        const string ImageType = "OpenCV.Net.IplImage";

        static bool IsImage(Type type)
        {
            for (; type != null; type = type.BaseType)
            {
                if (type.FullName == ImageType) return true;
            }
            return false;
        }

        /// <summary>
        /// The member path to the first IplImage in the data, breadth first and at most three
        /// members deep (Value.Image for a camera's frame paired with its trigger), "it" if the
        /// data is the image, or null if it holds none.
        /// </summary>
        public static string ImagePath(Type type)
        {
            if (IsImage(type)) return "it";
            var queue = new Queue<KeyValuePair<Type, string>>();
            queue.Enqueue(new KeyValuePair<Type, string>(type, string.Empty));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                var depth = item.Value.Length == 0 ? 0 : item.Value.Count(c => c == '.') + 1;
                if (depth >= 3) continue;
                var members = item.Key.GetProperties().Where(p => p.GetIndexParameters().Length == 0)
                    .Select(p => new KeyValuePair<string, Type>(p.Name, p.PropertyType))
                    .Concat(item.Key.GetFields().Select(f => new KeyValuePair<string, Type>(f.Name, f.FieldType)));
                foreach (var member in members)
                {
                    var path = item.Value.Length == 0 ? member.Key : item.Value + "." + member.Key;
                    if (IsImage(member.Value)) return path;
                    if (!member.Value.IsPrimitive && !member.Value.IsEnum && member.Value != typeof(string))
                        queue.Enqueue(new KeyValuePair<Type, string>(member.Value, path));
                }
            }
            return null;
        }
    }

    /// <summary>One device streamed, with its settings once rig.yml's liveView has been applied.</summary>
    class PlannedDevice
    {
        public string Name;
        public DeviceRecipe Recipe;
        public double Rate;
        public int Width;
        public int Height;
        public string ImagePath;

        public string Stream { get { return Name + "." + Recipe.Stream; } }

        public string Summary
        {
            get
            {
                return Recipe.IsVideo
                    ? string.Format(CultureInfo.InvariantCulture, "{0}.Data, {1} frames/s at {2} x {3}, JPEG, as {4}.", Name, Rate, Width, Height, Stream)
                    : string.Format(CultureInfo.InvariantCulture, "{0}.Data, {1}/s, as {2}.", Name, Rate, Stream);
            }
        }

        public Dictionary<string, string> TemplateValues()
        {
            return new Dictionary<string, string>
            {
                { "Subject", Name + ".Data" },
                { "Stream", Stream },
                { "Interval", XmlConvert.ToString(TimeSpan.FromSeconds(1.0 / Rate)) },
                { "Width", Width.ToString(CultureInfo.InvariantCulture) },
                { "Height", Height.ToString(CultureInfo.InvariantCulture) },
                { "ImagePath", ImagePath ?? string.Empty },
            };
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}x{3} {4}", Stream, Rate, Width, Height, ImagePath);
        }
    }

    /// <summary>
    /// What StreamDevices streams, decided at build from rig.yml and the subjects the workflow
    /// publishes. Never throws: the editor builds a workflow on open, on any desk, and a missing
    /// or broken rig.yml is the rig configuration's own node to report at Start.
    /// </summary>
    class LiveViewPlan
    {
        public string RigId;
        public bool HasSessionFolder;
        public readonly List<PlannedDevice> Streamed = new List<PlannedDevice>();
        public readonly List<string> Skipped = new List<string>();
        public readonly List<string> Notes = new List<string>();

        public static LiveViewPlan Create(string configRoot, Func<string, Type> publishedType)
        {
            var plan = new LiveViewPlan();
            // The machine name, not rig.yml's two-letter rigId, on purpose: the live-view windows
            // find a rig's streams by the machine they connect to, and rig.yml is read by it too.
            plan.RigId = Environment.GetEnvironmentVariable("COMPUTERNAME") ?? Environment.MachineName;
            var pathPrefix = publishedType("PathPrefix");
            plan.HasSessionFolder = pathPrefix == typeof(string);
            if (pathPrefix != null && pathPrefix != typeof(string))
                plan.Notes.Add("PathPrefix is " + pathPrefix.Name + ", not a path: the session key stays ready and elapsed is not sent");

            if (string.IsNullOrWhiteSpace(configRoot))
            {
                plan.Notes.Add("ConfigRoot is not set, so no rig.yml was read");
                return plan;
            }
            string problem;
            var devices = RigDevices.Read(Path.Combine(Environment.ExpandEnvironmentVariables(configRoot), Environment.MachineName, "rig.yml"), out problem);
            if (devices == null)
            {
                plan.Notes.Add(problem);
                return plan;
            }
            foreach (var device in devices)
            {
                plan.Consider(device.Name, device.Kind, device.Settings, publishedType);
            }
            return plan;
        }

        void Consider(string name, string kind, YamlMappingNode settings, Func<string, Type> publishedType)
        {
            var recipe = DeviceRecipe.All.FirstOrDefault(r => r.Kind == kind);
            if (recipe == null)
            {
                Skipped.Add(name + ": no live-view recipe for " + kind);
                return;
            }
            var device = new PlannedDevice { Name = name, Recipe = recipe, Rate = recipe.Rate, Width = recipe.Width, Height = recipe.Height };
            if (!ApplyOverrides(device, settings))
            {
                Skipped.Add(name + ": liveView enabled is false in rig.yml");
                return;
            }
            var data = publishedType(name + ".Data");
            if (data == null)
            {
                Skipped.Add(name + ": the workflow publishes no " + name + ".Data before this node");
                return;
            }
            var refusal = recipe.Accepts(data);
            if (refusal != null)
            {
                Skipped.Add(name + ": " + refusal);
                return;
            }
            if (recipe.IsVideo) device.ImagePath = DeviceRecipe.ImagePath(data);
            Streamed.Add(device);
        }

        // The device's liveView: settings, each optional: enabled, rate (per second), and for
        // video width and height. A bad value is noted and the default kept. False if disabled.
        bool ApplyOverrides(PlannedDevice device, YamlMappingNode settings)
        {
            YamlNode node;
            if (!settings.Children.TryGetValue(new YamlScalarNode("liveView"), out node)) return true;
            var liveView = node as YamlMappingNode;
            if (liveView == null)
            {
                // liveView: null, as a generated rig template writes it, is the same as leaving it out.
                var value = RigDevices.Scalar(node);
                if (!string.IsNullOrEmpty(value) && value != "null" && value != "~")
                    Notes.Add(device.Name + ": liveView is not a set of settings; ignored");
                return true;
            }
            foreach (var entry in liveView.Children)
            {
                var key = RigDevices.Scalar(entry.Key);
                var value = RigDevices.Scalar(entry.Value);
                // A setting left null (rate: null, as model_dump writes it) keeps its default.
                if (value == null || value == "null" || value == "~" || value.Length == 0) continue;
                double number;
                bool flag;
                switch (key)
                {
                    case "enabled":
                        if (bool.TryParse(value, out flag)) { if (!flag) return false; }
                        else Notes.Add(device.Name + ": liveView enabled '" + value + "' is not true or false; ignored");
                        break;
                    case "rate":
                        if (TryPositive(value, out number) && number <= 1000) device.Rate = number;
                        else Notes.Add(device.Name + ": liveView rate '" + value + "' is not a number from 0 to 1000 per second; " + device.Rate.ToString(CultureInfo.InvariantCulture) + " kept");
                        break;
                    case "width":
                    case "height":
                        if (!device.Recipe.IsVideo) Notes.Add(device.Name + ": liveView " + key + " applies to video only; ignored");
                        else if (TryPositive(value, out number) && number == Math.Floor(number) && number <= 4096)
                        {
                            if (key == "width") device.Width = (int)number; else device.Height = (int)number;
                        }
                        else Notes.Add(device.Name + ": liveView " + key + " '" + value + "' is not a whole number from 1 to 4096; ignored");
                        break;
                    default:
                        Notes.Add(device.Name + ": liveView has no setting '" + key + "' (enabled, rate, width, height); ignored");
                        break;
                }
            }
            return true;
        }

        static bool TryPositive(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number > 0;
        }

        /// <summary>Everything the inner workflow is made from, so it is remade only when this changes.</summary>
        public string Signature(int dataPort, int videoPort)
        {
            var text = new StringBuilder();
            text.Append(RigId).Append('|').Append(dataPort).Append('|').Append(videoPort).Append('|').Append(HasSessionFolder);
            foreach (var device in Streamed) text.Append("\n+").Append(device);
            foreach (var skipped in Skipped) text.Append("\n-").Append(skipped);
            foreach (var note in Notes) text.Append("\n!").Append(note);
            return text.ToString();
        }
    }
}
