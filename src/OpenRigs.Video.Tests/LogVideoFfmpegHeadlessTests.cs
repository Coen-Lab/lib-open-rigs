using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Tests;

namespace OpenRigs.Video.Tests
{
    /// <summary>
    /// FfmpegVideoWriter, alone and in LogVideoFfmpeg, run by headless Bonsai (--no-editor), as a
    /// rig runs it, which returns from Main as soon as the workflow completes: the video and its
    /// CSV must hold every frame anyway, also when the writer is only told to end (a Stop) and
    /// its thread still has frames to write. libx264 and the ffmpeg.exe on PATH; skipped where
    /// there is none.
    /// </summary>
    [TestClass]
    public class LogVideoFfmpegHeadlessTests
    {
        string folder;
        string ffmpeg;

        [TestInitialize]
        public void Initialize()
        {
            folder = Path.Combine(Path.GetTempPath(), "ffmpeg-headless-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try { ffmpeg = VideoEncoder.FindFfmpeg(null, "rig.yml", Environment.GetEnvironmentVariable("PATH")); }
            catch (InvalidOperationException) { ffmpeg = null; }
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // The frames end, and the writer closes the file before the workflow completes.
        [DataTestMethod]
        [DataRow(3, 320, 240, 1)]
        [DataRow(20, 640, 480, 1)]
        [DataRow(200, 322, 240, 1)]
        [DataRow(200, 640, 480, 3)]
        public void Writer_FramesEnd_EveryFrameInTheVideoAndCsv(int count, int width, int height, int channels)
        {
            Check(WriterWorkflow, count, width, height, channels, takeCount: 1000000);
        }

        // The workflow completes downstream of the writer at its last frame, as at a Stop, so the
        // writer is disposed with frames still queued and Bonsai returns at once.
        [DataTestMethod]
        [DataRow(3, 320, 240, 1)]
        [DataRow(20, 640, 480, 1)]
        [DataRow(200, 322, 240, 1)]
        [DataRow(200, 640, 480, 3)]
        public void Writer_StoppedAtTheLastFrame_EveryFrameInTheVideoAndCsv(int count, int width, int height, int channels)
        {
            Check(WriterWorkflow, count, width, height, channels, takeCount: count);
        }

        // The same through the include, grouped by the hour, its file name from PathPrefix. Stopped,
        // the hour's writer is disposed by the include's Switch.
        [DataTestMethod]
        [DataRow(3, 320, 240, 1)]
        [DataRow(200, 640, 480, 3)]
        public void LogVideoFfmpeg_FramesEnd_EveryFrameInTheVideoAndCsv(int count, int width, int height, int channels)
        {
            Check(IncludeWorkflow, count, width, height, channels, takeCount: 1000000);
        }

        [DataTestMethod]
        [DataRow(3, 320, 240, 1)]
        [DataRow(200, 640, 480, 3)]
        public void LogVideoFfmpeg_StoppedAtTheLastFrame_EveryFrameInTheVideoAndCsv(int count, int width, int height, int channels)
        {
            Check(IncludeWorkflow, count, width, height, channels, takeCount: count);
        }

        const string WriterWorkflow = "FfmpegVideoWriterHeadless.bonsai";
        const string IncludeWorkflow = "LogVideoFfmpegHeadless.bonsai";

        void Check(string workflow, int count, int width, int height, int channels, int takeCount)
        {
            if (ffmpeg == null) Assert.Inconclusive("ffmpeg.exe is not on PATH.");
            var data = Path.Combine(folder, "data");
            var configRoot = Path.Combine(folder, "config");  // no rig.yml: libx264, ffmpeg.exe on PATH
            Directory.CreateDirectory(configRoot);

            var result = BonsaiWorkflowRunner.Run(workflow, new Dictionary<string, string>
            {
                { workflow == WriterWorkflow ? "FileName" : "PathPrefix", workflow == WriterWorkflow ? Path.Combine(data, "Camera-Test", "Camera-Test.mkv") : data },
                { "ConfigRoot", configRoot },
                { "FrameCount", count.ToString(CultureInfo.InvariantCulture) },
                { "Width", width.ToString(CultureInfo.InvariantCulture) },
                { "Height", height.ToString(CultureInfo.InvariantCulture) },
                { "Channels", channels.ToString(CultureInfo.InvariantCulture) },
                { "TakeCount", takeCount.ToString(CultureInfo.InvariantCulture) },
            });

            var files = Directory.Exists(data) ? Directory.GetFiles(data, "*", SearchOption.AllDirectories) : new string[0];
            var listing = Environment.NewLine + "Files: " + string.Join(", ", files.Select(Path.GetFileName)) + result.Describe();
            var errors = files.Where(f => f.EndsWith(".error.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(0, errors.Length, "A .error.txt: " + string.Join(" | ", errors.Select(File.ReadAllText)) + listing);
            var videos = files.Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, videos.Length, "Expected one .mkv." + listing);
            var csv = Path.ChangeExtension(videos[0], ".csv");
            Assert.IsTrue(File.Exists(csv), "Expected the .csv beside the video." + listing);

            var rows = File.ReadAllLines(csv);
            Assert.AreEqual(FfmpegRecording.CsvHeader, rows.FirstOrDefault(), "The CSV's header." + listing);
            Assert.AreEqual(count, rows.Length - 1, "Expected a CSV row per frame." + listing);
            for (var i = 1; i < rows.Length; i++)
            {
                Assert.AreEqual((i - 1).ToString(CultureInfo.InvariantCulture), rows[i].Split(',')[1], "frameId of row " + i + "." + listing);
            }
            Assert.AreEqual(count, DecodedFrames(videos[0]), "Expected the video to decode to every frame." + listing);
        }

        // ffprobe beside ffmpeg.exe, decoding every frame.
        int DecodedFrames(string video)
        {
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe");
            if (!File.Exists(ffprobe)) Assert.Inconclusive("No ffprobe.exe beside " + ffmpeg + ".");
            var info = new ProcessStartInfo(ffprobe,
                "-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 \"" + video + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(info))
            {
                var stderr = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                Assert.IsTrue(int.TryParse(output, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames),
                    "ffprobe gave '" + output + "': " + stderr.Result);
                return frames;
            }
        }
    }
}
