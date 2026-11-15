using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenRigs.Shared;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Video
{
    /// <summary>
    /// How this rig encodes camera video: rig.yml's <c>videoEncoder</c> section, its codec and the
    /// ffmpeg.exe to run. A rig without the section, or without a rig.yml, encodes with libx264 and
    /// the ffmpeg.exe found on PATH.
    /// </summary>
    internal sealed class VideoEncoder
    {
        /// <summary>The codec when rig.yml names none: software, so it runs on any machine.</summary>
        public const string DefaultCodec = "libx264";

        /// <summary>The section's key in rig.yml.</summary>
        public const string Section = "videoEncoder";

        // Each codec's output arguments, tuned for 8-bit mono cameras at 50 Hz: no B-frames, so every
        // frame leaves the encoder as soon as it is encoded and nothing waits at Stop; quality-targeted
        // rate control, so a noisy camera costs bytes, not frames. yuv420p (nv12 for Quick Sync) is the
        // input every H.264 decoder takes; a mono frame's chroma is flat and costs almost nothing. Full
        // range (-color_range pc, flagged in the stream): limited range (16 to 235) kept 220 of a Mono8
        // camera's 256 grey levels.
        static readonly Dictionary<string, string> Codecs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "libx264", "-c:v libx264 -preset veryfast -crf 20 -bf 0 -pix_fmt yuv420p -color_range pc" },
            { "h264_nvenc", "-c:v h264_nvenc -preset p4 -rc vbr -cq 20 -b:v 0 -bf 0 -pix_fmt yuv420p -color_range pc" },
            { "h264_qsv", "-c:v h264_qsv -preset veryfast -global_quality 20 -bf 0 -pix_fmt nv12 -color_range pc" },
        };

        /// <summary>The codecs rig.yml may name.</summary>
        public static IEnumerable<string> KnownCodecs => Codecs.Keys;

        readonly string? fixedArguments;

        /// <summary>An encoder; tests give fixed arguments to run a stand-in for ffmpeg.exe.</summary>
        internal VideoEncoder(string codec, string ffmpegPath, string? fixedArguments = null)
        {
            Codec = codec;
            FfmpegPath = ffmpegPath;
            this.fixedArguments = fixedArguments;
        }

        /// <summary>The FFmpeg encoder, such as h264_nvenc.</summary>
        public string Codec { get; }

        /// <summary>The full path of the ffmpeg.exe that encodes.</summary>
        public string FfmpegPath { get; }

        /// <summary>The codec's ffmpeg output arguments.</summary>
        public string CodecArguments => Codecs[Codec];

        /// <summary>
        /// Why a frame size cannot be encoded, or null when it can: every codec here encodes 4:2:0
        /// (yuv420p, nv12), one chroma sample per 2 x 2 pixels, so width and height must be even.
        /// </summary>
        public string? RefuseSize(int width, int height)
        {
            if (width % 2 == 0 && height % 2 == 0) return null;
            return "frames are " + width.ToString(CultureInfo.InvariantCulture) + " x " + height.ToString(CultureInfo.InvariantCulture) +
                   ", but " + Codec + " encodes 4:2:0 colour, one sample per 2 x 2 pixels, so the width and height must both be even: " +
                   "set the camera's region of interest to an even size";
        }

        /// <summary>The encoder for this machine's rig.yml under a config root, with ffmpeg.exe found; throws naming what is missing.</summary>
        public static VideoEncoder ForThisRig(string configRoot)
        {
            return Read(RigDevices.PathFor(string.IsNullOrWhiteSpace(configRoot) ? RigDevices.DefaultConfigRoot : configRoot),
                Environment.GetEnvironmentVariable("PATH"));
        }

        /// <summary>
        /// The encoder a rig.yml names, with ffmpeg.exe found at its ffmpegPath or else on a PATH
        /// (a ;-separated list of folders). Throws naming the problem: an unknown codec or key, an
        /// ffmpegPath that is not a file, or no ffmpeg.exe on PATH.
        /// </summary>
        public static VideoEncoder Read(string rigFile, string? pathVariable)
        {
            string codec = DefaultCodec;
            string? ffmpegPath = null;
            var section = ReadSection(rigFile);
            if (section != null)
            {
                foreach (var entry in section.Children)
                {
                    var key = RigDevices.Scalar(entry.Key);
                    if (key == "codec") codec = RigDevices.Value(entry.Value) ?? DefaultCodec;
                    else if (key == "ffmpegPath") ffmpegPath = RigDevices.Value(entry.Value);
                    else
                    {
                        throw new InvalidOperationException(rigFile + "'s " + Section + " has '" + key +
                            "', which is not one of its keys (codec, ffmpegPath): delete it or correct its name.");
                    }
                }
            }
            if (!Codecs.ContainsKey(codec))
            {
                throw new InvalidOperationException(rigFile + "'s " + Section + ".codec is '" + codec + "', not one of " +
                    string.Join(", ", Codecs.Keys) + ": camera video cannot be recorded.");
            }
            return new VideoEncoder(codec, FindFfmpeg(ffmpegPath, rigFile, pathVariable));
        }

        // rig.yml's videoEncoder mapping; null when the file or the section is not there.
        static YamlMappingNode? ReadSection(string rigFile)
        {
            if (!File.Exists(rigFile)) return null;
            var yaml = new YamlStream();
            try
            {
                using (var reader = new StreamReader(rigFile)) yaml.Load(reader);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(rigFile + " could not be read, so the video encoder is unknown: " + ex.Message, ex);
            }
            var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            if (root == null || !root.Children.TryGetValue(new YamlScalarNode(Section), out var node)) return null;
            if (node is YamlScalarNode scalar && RigDevices.Value(scalar) == null) return null;
            return node as YamlMappingNode ?? throw new InvalidOperationException(
                rigFile + "'s " + Section + " is not a set of settings; write it as `" + Section + ": { codec: " + DefaultCodec + " }`.");
        }

        /// <summary>ffmpeg.exe: the path rig.yml gives, which must be a file, or else the first on PATH.</summary>
        internal static string FindFfmpeg(string? configured, string rigFile, string? pathVariable)
        {
            if (configured != null)
            {
                var full = Environment.ExpandEnvironmentVariables(configured);
                if (File.Exists(full)) return Path.GetFullPath(full);
                throw new InvalidOperationException(rigFile + "'s " + Section + ".ffmpegPath is " + configured +
                    ", which is not a file: camera video cannot be recorded. Correct it, or delete it to use the ffmpeg.exe on PATH.");
            }
            foreach (var folder in (pathVariable ?? "").Split(';'))
            {
                var trimmed = folder.Trim().Trim('"');
                if (trimmed.Length == 0) continue;
                string candidate;
                try { candidate = Path.Combine(Environment.ExpandEnvironmentVariables(trimmed), "ffmpeg.exe"); }
                catch (ArgumentException) { continue; }  // a folder with characters a path cannot hold
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new InvalidOperationException("ffmpeg.exe is not on PATH, so camera video cannot be recorded. Install FFmpeg " +
                "(winget install Gyan.FFmpeg puts it on PATH), or give its full path as " + Section + ".ffmpegPath in " + rigFile + ".");
        }

        /// <summary>
        /// ffmpeg's command line for raw frames of this size and format on stdin, written to a
        /// Matroska file: errors only on stderr, never overwriting, a keyframe every two seconds,
        /// and a new cluster (the unit a killed file keeps) every second.
        /// </summary>
        public string Arguments(int width, int height, string pixelFormat, double frameRate, string fileName)
        {
            if (fixedArguments != null) return fixedArguments;
            var rate = frameRate.ToString("R", CultureInfo.InvariantCulture);
            var gop = Math.Max(1, (int)Math.Round(2 * frameRate)).ToString(CultureInfo.InvariantCulture);
            return "-hide_banner -loglevel error -nostats -n " +
                "-f rawvideo -pix_fmt " + pixelFormat + " -video_size " + width.ToString(CultureInfo.InvariantCulture) + "x" +
                height.ToString(CultureInfo.InvariantCulture) + " -framerate " + rate + " -i pipe:0 -an " +
                CodecArguments + " -g " + gop + " -f matroska -cluster_time_limit 1000 -flush_packets 1 \"" + fileName + "\"";
        }
    }
}
