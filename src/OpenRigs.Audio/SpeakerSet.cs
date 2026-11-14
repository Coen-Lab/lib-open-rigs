using Newtonsoft.Json;
using OpenCV.Net;
using OpenRigs.Core.Calibration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Audio
{
    /// <summary>One speaker of rig.yml's speakerArray: its name, its mixer channel and its filter's taps.</summary>
    public class Speaker
    {
        /// <summary>The speaker's key in rig.yml, such as left.</summary>
        public string Name { get; internal set; }

        /// <summary>The mixer channel it plays on.</summary>
        public int Channel { get; internal set; }

        /// <summary>Its azimuth, in degrees.</summary>
        public double AzimuthDeg { get; internal set; }

        /// <summary>Its elevation, in degrees.</summary>
        public double ElevationDeg { get; internal set; }

        internal Mat Kernel;
    }

    /// <summary>
    /// A rig's speakers as rig.yml's speakerArray gives them, each with its filter (a SpeakerFilter artefact)
    /// checked and loaded, so any sound can be equalised for any speaker and laid out as the mixer's buffer.
    /// </summary>
    public class SpeakerSet
    {
        /// <summary>The audio device's rate, in Hz (speakerArray.device.sampleRate).</summary>
        public int SampleRate { get; private set; }

        /// <summary>The mixer's channels (speakerArray.device.channelCount): every buffer has this many rows.</summary>
        public int ChannelCount { get; private set; }

        /// <summary>The channel a stimulus's sync copy plays on (speakerArray.syncChannel), or null.</summary>
        public int? SyncChannel { get; private set; }

        /// <summary>The channel the error sound plays on (speakerArray.errorSoundChannel), or null.</summary>
        public int? ErrorSoundChannel { get; private set; }

        /// <summary>Every speaker, in rig.yml's order.</summary>
        public IList<Speaker> Speakers { get; private set; }

        /// <summary>The mixer channel of every speaker, in rig.yml's order.</summary>
        public int[] SpeakerChannels
        {
            get { return Speakers.Select(speaker => speaker.Channel).ToArray(); }
        }

        /// <summary>
        /// A sound (one row) equalised for the speaker on a channel: correlated with its filter's taps, centred on the
        /// middle tap and zero beyond the ends, so as long as the input and not delayed (the taps are symmetric, so this
        /// is the convolution too). Unchanged on a channel no speaker is on.
        /// </summary>
        public Mat Equalise(Mat sound, int channel)
        {
            var speaker = Speakers.FirstOrDefault(s => s.Channel == channel);
            return speaker == null ? ToFloat(sound) : Equalise(sound, speaker.Kernel);
        }

        /// <summary>
        /// The mixer's buffer (ChannelCount rows) playing a sound (one row) on each of the channels, equalised for its
        /// speaker (unless <paramref name="equalise"/> is false) and scaled by gain; every other channel silent. No
        /// channels: every speaker's.
        /// </summary>
        public Mat Buffer(Mat sound, IEnumerable<int> channels, double gain, bool equalise = true)
        {
            if (sound.Rows != 1) throw new InvalidOperationException("A sound for the speakers must be one row (mono); it has " + sound.Rows + ".");
            var list = (channels ?? new int[0]).ToArray();
            if (list.Length == 0) list = SpeakerChannels;
            var buffer = Mat.Zeros(ChannelCount, sound.Cols, Depth.F32, 1);
            foreach (var channel in list.Distinct())
            {
                if (channel < 0 || channel >= ChannelCount)
                {
                    throw new InvalidOperationException("Channel " + channel + " is not one of the audio device's " + ChannelCount + " (0 to " + (ChannelCount - 1) + ").");
                }
                using (var equalised = equalise ? Equalise(sound, channel) : ToFloat(sound))
                using (var row = buffer.GetRow(channel))
                {
                    CV.ConvertScale(equalised, row, gain, 0);
                }
            }
            return buffer;
        }

        static Mat ToFloat(Mat sound)
        {
            var output = new Mat(sound.Rows, sound.Cols, Depth.F32, 1);
            CV.Convert(sound, output);
            return output;
        }

        static Mat Equalise(Mat sound, Mat kernel)
        {
            int taps = kernel.Cols;
            int anchor = taps / 2;
            using (var input = ToFloat(sound))
            using (var padded = Mat.Zeros(sound.Rows, sound.Cols + taps - 1, Depth.F32, 1))
            using (var filtered = new Mat(padded.Size, Depth.F32, 1))
            {
                using (var middle = padded.GetSubRect(new Rect(anchor, 0, sound.Cols, sound.Rows)))
                {
                    CV.Copy(input, middle);
                }
                CV.Filter2D(padded, filtered, kernel, new Point(anchor, 0));
                var output = new Mat(sound.Rows, sound.Cols, Depth.F32, 1);
                using (var result = filtered.GetSubRect(new Rect(anchor, 0, sound.Cols, sound.Rows)))
                {
                    CV.Copy(result, output);
                }
                return output;
            }
        }

        /// <summary>
        /// rig.yml's speakerArray, each speaker's filter read relative to rig.yml's folder. Stops, naming every problem,
        /// on a speaker without a filter (RequireFilters), a filter missing, without taps,
        /// not symmetric, for another rate or measured on another machine.
        /// </summary>
        public static SpeakerSet Load(string rigFile, string machineName = null)
        {
            machineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName;
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rigFile ?? ""));
            if (!File.Exists(path)) throw new InvalidOperationException("No rig.yml at " + path + ", so there are no speakers to play on.");
            var yaml = new YamlStream();
            using (var reader = new StreamReader(path)) yaml.Load(reader);
            var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            var array = Child(root, "speakerArray") as YamlMappingNode;
            var device = Child(array, "device") as YamlMappingNode;
            var speakers = Child(array, "speakers") as YamlMappingNode;
            if (array == null || device == null || speakers == null || speakers.Children.Count == 0)
            {
                throw new InvalidOperationException(path + " has no speakerArray with its device and speakers, so there is nowhere to play sounds.");
            }
            var entries = speakers.Children.Select(entry => new
            {
                Name = Text(entry.Key),
                Settings = entry.Value as YamlMappingNode,
            }).ToList();
            RequireFilters(entries.Select(e => new KeyValuePair<string, string>(e.Name, Text(Child(e.Settings, "filter")))));

            var set = new SpeakerSet
            {
                SampleRate = Integer(Child(device, "sampleRate"), "speakerArray.device.sampleRate", path),
                ChannelCount = Integer(Child(device, "channelCount"), "speakerArray.device.channelCount", path),
                SyncChannel = OptionalInteger(Child(array, "syncChannel")),
                ErrorSoundChannel = OptionalInteger(Child(array, "errorSoundChannel")),
            };
            var problems = new List<string>();
            var loaded = new List<Speaker>();
            var folder = Path.GetDirectoryName(path);
            foreach (var entry in entries)
            {
                var position = Child(entry.Settings, "position") as YamlMappingNode;
                var speaker = new Speaker
                {
                    Name = entry.Name,
                    Channel = Integer(Child(entry.Settings, "channel"), "speaker '" + entry.Name + "' channel", path),
                    AzimuthDeg = Number(Child(position, "azimuth")),
                    ElevationDeg = Number(Child(position, "elevation")),
                };
                var filterPath = Text(Child(entry.Settings, "filter"));
                var full = Path.GetFullPath(Path.IsPathRooted(filterPath) ? filterPath : Path.Combine(folder, filterPath));
                if (!File.Exists(full))
                {
                    problems.Add("speaker '" + entry.Name + "' filter not found: " + full);
                    continue;
                }
                SpeakerFilter filter;
                try
                {
                    filter = JsonConvert.DeserializeObject<SpeakerFilter>(File.ReadAllText(full));
                }
                catch (Exception ex)
                {
                    problems.Add("speaker '" + entry.Name + "' filter is not a SpeakerFilter artefact (" + ex.Message + "): " + full);
                    continue;
                }
                if (filter == null || filter.Taps == null || filter.Taps.Count == 0)
                {
                    problems.Add("speaker '" + entry.Name + "' filter has no taps: " + full);
                    continue;
                }
                if (Math.Abs(filter.SampleRate - set.SampleRate) > 0.5)
                {
                    problems.Add("speaker '" + entry.Name + "' filter was designed for " + filter.SampleRate + " Hz, but the audio device runs at " + set.SampleRate + " Hz: " + full);
                    continue;
                }
                if (!IsSymmetric(filter.Taps))
                {
                    problems.Add("speaker '" + entry.Name + "' filter's taps are not symmetric, so equalising by correlation would not be its convolution: " + full);
                    continue;
                }
                if (!string.Equals(filter.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("speaker '" + entry.Name + "' filter was measured on '" + filter.MachineName + "', not on this machine (" + machineName + "): " + full);
                    continue;
                }
                speaker.Kernel = new Mat(1, filter.Taps.Count, Depth.F32, 1);
                Marshal.Copy(filter.Taps.Select(tap => (float)tap).ToArray(), 0, speaker.Kernel.Data, filter.Taps.Count);
                loaded.Add(speaker);
            }
            if (problems.Count > 0) throw new InvalidOperationException("The speakers cannot be used: " + string.Join("; ", problems) + ".");
            set.Speakers = loaded.AsReadOnly();
            return set;
        }

        /// <summary>
        /// Throws, naming each speaker without one, unless every speaker has a filter: a speaker is never played
        /// unequalised. <paramref name="filters"/> is each speaker's name and rig.yml filter.
        /// </summary>
        public static void RequireFilters(IEnumerable<KeyValuePair<string, string>> filters)
        {
            var missing = filters.Where(speaker => string.IsNullOrWhiteSpace(speaker.Value)).Select(speaker => speaker.Key).OrderBy(name => name).ToList();
            if (missing.Count == 0) return;
            throw new InvalidOperationException(string.Format(
                "rig.yml's speaker {0} has no filter, and every speaker is played equalised. Give each speaker in speakerArray.speakers " +
                "its SpeakerFilter artefact, as in filter: calibration/speaker-{1}.json.",
                string.Join(", ", missing.Select(name => "'" + name + "'")), missing[0]));
        }

        // Symmetric to within float precision of the largest tap, as a linear-phase FIR is.
        static bool IsSymmetric(IList<double> taps)
        {
            double tolerance = 1e-6 * taps.Max(tap => Math.Abs(tap));
            for (int i = 0; i < taps.Count / 2; i++)
            {
                if (Math.Abs(taps[i] - taps[taps.Count - 1 - i]) > tolerance) return false;
            }
            return true;
        }

        static YamlNode Child(YamlMappingNode node, string key)
        {
            YamlNode child;
            return node != null && node.Children.TryGetValue(new YamlScalarNode(key), out child) ? child : null;
        }

        static string Text(YamlNode node)
        {
            var scalar = node as YamlScalarNode;
            var value = scalar != null ? scalar.Value : null;
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            return value == "null" || value == "Null" || value == "NULL" || value == "~" ? null : value;
        }

        static int Integer(YamlNode node, string what, string path)
        {
            int value;
            if (!int.TryParse(Text(node), out value)) throw new InvalidOperationException(path + " has no whole number for " + what + ".");
            return value;
        }

        static int? OptionalInteger(YamlNode node)
        {
            int value;
            return int.TryParse(Text(node), out value) ? value : (int?)null;
        }

        static double Number(YamlNode node)
        {
            double value;
            return double.TryParse(Text(node), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : double.NaN;
        }
    }
}
