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

        // The experiment started an hour before HourEnds, so its first chunk ends 2 s (100 frames) into
        // the run: the include's Switch disposes the first chunk's writer at the next chunk's first
        // frame, and Bonsai exits soon after; both files must still hold every frame of their chunk.
        [TestMethod]
        public void LogVideoFfmpeg_AnHourRollover_EachHoursFramesInItsOwnVideoAndCsv()
        {
            if (ffmpeg == null) Assert.Inconclusive("ffmpeg.exe is not on PATH.");
            var data = Path.Combine(folder, "data");
            var configRoot = Path.Combine(folder, "config");
            Directory.CreateDirectory(configRoot);
            const double HourEnds = 1083334 * 3600.0;  // 3900002400, the first whole hour after 3900000000
            var result = BonsaiWorkflowRunner.Run(IncludeWorkflow, new Dictionary<string, string>
            {
                { "PathPrefix", Experiment(data, HourEnds - 3600) },
                { "ConfigRoot", configRoot },
                { "FrameCount", "250" },
                { "Width", "320" },
                { "Height", "240" },
                { "Channels", "1" },
                { "StartSeconds", (HourEnds - 2).ToString("R", CultureInfo.InvariantCulture) },
                { "TakeCount", "250" },
            });

            var files = Directory.Exists(data) ? Directory.GetFiles(data, "*", SearchOption.AllDirectories) : new string[0];
            var listing = Environment.NewLine + "Files: " + string.Join(", ", files.Select(Path.GetFileName)) + result.Describe();
            Assert.AreEqual(0, files.Count(f => f.EndsWith(".error.txt", StringComparison.OrdinalIgnoreCase)), "A .error.txt." + listing);
            var videos = files.Where(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.AreEqual(2, videos.Length, "Expected a .mkv for each hour." + listing);
            var first = 0;
            foreach (var (video, frames) in new[] { (videos[0], 100), (videos[1], 150) })
            {
                var rows = File.ReadAllLines(Path.ChangeExtension(video, ".csv")).Skip(1).ToArray();
                Assert.AreEqual(frames, rows.Length, Path.GetFileName(video) + "'s CSV rows." + listing);
                Assert.AreEqual(first.ToString(CultureInfo.InvariantCulture), rows[0].Split(',')[1], Path.GetFileName(video) + "'s first frameId." + listing);
                Assert.AreEqual(frames, DecodedFrames(video), Path.GetFileName(video) + "'s decoded frames." + listing);
                first += frames;
            }
        }

        // Bonsai's workflow process killed mid-run, as a crash would: ffmpeg, a child left
        // running, reads its stdin to the end and finishes the file, and the CSV holds a row for
        // every frame in it (at most the frame in hand without one).
        [TestMethod]
        public void BonsaiKilled_FfmpegFinishesTheVideoAndTheCsvHasEveryFramesRow()
        {
            var (video, rows) = KillBonsai(tree: false);
            var decoded = DecodedFrames(video);
            Assert.IsTrue(decoded >= rows && decoded <= rows + 1, decoded + " frames decoded, " + rows + " CSV rows");
        }

        // Bonsai and its child ffmpeg killed together (taskkill /T): the video stays readable, short
        // of the CSV by at most the open cluster (1 s) and what the encoder held.
        [TestMethod]
        public void BonsaiTreeKilled_TheVideoIsReadableAndShortOfTheCsvByAtMostTheOpenCluster()
        {
            var (video, rows) = KillBonsai(tree: true);
            var decoded = DecodedFrames(video);
            Console.WriteLine(rows + " CSV rows, " + decoded + " frames decoded");
            Assert.IsTrue(decoded > 0 && decoded <= rows + 1 && decoded >= rows - 2 * 50, decoded + " frames decoded, " + rows + " CSV rows");
        }

        // Runs the writer at 50 Hz, kills Bonsai (and with tree, ffmpeg) after 150 CSV rows, waits
        // for the video to be closed, and gives it and the CSV's frame rows.
        (string Video, int Rows) KillBonsai(bool tree)
        {
            if (ffmpeg == null) Assert.Inconclusive("ffmpeg.exe is not on PATH.");
            var video = Path.Combine(folder, "data", "Camera-Test", "Camera-Test.mkv");
            var csv = Path.ChangeExtension(video, ".csv");
            var configRoot = Path.Combine(folder, "config");
            Directory.CreateDirectory(configRoot);
            using (var bonsai = BonsaiWorkflowRunner.Start(WriterWorkflow, new Dictionary<string, string>
            {
                { "FileName", video },
                { "ConfigRoot", configRoot },
                { "FrameCount", "100000" },
                { "Width", "640" },
                { "Height", "480" },
                { "Channels", "1" },
                { "IntervalMs", "20" },
            }))
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    while (CsvRows(csv) < 150)
                    {
                        if (bonsai.HasExited) Assert.Fail("Bonsai exited (code " + bonsai.ExitCode + ") before 150 frames were written.");
                        if (clock.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail("150 frames were not written within 60 s.");
                        System.Threading.Thread.Sleep(20);
                    }
                    if (tree) KillTree(bonsai.Id);
                    else
                    {
                        // Bonsai.exe is a launcher: the workflow runs in a child Bonsai.exe (--no-boot),
                        // which a kill of the launcher alone leaves running.
                        var children = WorkflowProcesses(bonsai.Id);
                        Assert.AreEqual(1, children.Length, "Expected one workflow Bonsai.exe under the launcher");
                        using (var workflow = Process.GetProcessById(children[0])) workflow.Kill();
                    }
                    Assert.IsTrue(bonsai.WaitForExit(10000), "Bonsai was killed");
                }
                finally
                {
                    if (!bonsai.HasExited) KillTree(bonsai.Id);  // a failed test leaves nothing running
                }
            }
            // ffmpeg holds the video open for writing until it has finished it (or was killed).
            var closing = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using (new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.Read)) break;
                }
                catch (IOException) when (closing.Elapsed < TimeSpan.FromSeconds(20)) { System.Threading.Thread.Sleep(50); }
            }
            Assert.IsFalse(File.Exists(Path.ChangeExtension(video, ".error.txt")), "a killed Bonsai writes no .error.txt");
            return (video, CsvRows(csv));
        }

        static void KillTree(int id)
        {
            using (var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", "/F /T /PID " + id) { UseShellExecute = false, CreateNoWindow = true }))
            {
                taskkill.WaitForExit();
            }
        }

        // The Bonsai.exe processes a launcher started.
        static int[] WorkflowProcesses(int launcher)
        {
            using (var search = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE Name = 'Bonsai.exe' AND ParentProcessId = " + launcher))
            using (var found = search.Get())
            {
                return found.Cast<System.Management.ManagementObject>().Select(p => Convert.ToInt32(p["ProcessId"], CultureInfo.InvariantCulture)).ToArray();
            }
        }

        // The CSV's frame rows, read while it may still be written.
        static int CsvRows(string csv)
        {
            if (!File.Exists(csv)) return 0;
            try
            {
                using (var reader = new StreamReader(new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                {
                    var rows = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null) rows++;
                    return Math.Max(0, rows - 1);
                }
            }
            catch (IOException) { return 0; }
        }

        // An experiment folder under data started at Harp time startSeconds (UTC), as LogController names it.
        static string Experiment(string data, double startSeconds)
        {
            var start = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(startSeconds);
            return Path.Combine(data, "sub-Test", "ses-001_" + start.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture) + "+00-00");
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
                { workflow == WriterWorkflow ? "FileName" : "PathPrefix", workflow == WriterWorkflow ? Path.Combine(data, "Camera-Test", "Camera-Test.mkv") : Experiment(data, 3900000000.0) },
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
