using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Bonsai.Harp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;

namespace OpenRigs.Video.Tests
{
    /// <summary>FfmpegVideoWriter with real ffmpeg.exe (libx264, so no GPU is needed); skipped where ffmpeg.exe is not on PATH.</summary>
    [TestClass]
    public class FfmpegVideoWriterTests
    {
        const int Width = 322;  // not a multiple of 4: each row of the image is padded, and must be written without it
        const int Height = 240;
        const double FrameRate = 50;

        string folder;
        string ffmpeg;

        [TestInitialize]
        public void Initialize()
        {
            folder = Path.Combine(Path.GetTempPath(), "ffmpeg-writer-" + Guid.NewGuid().ToString("N"));
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

        void RequireFfmpeg()
        {
            if (ffmpeg == null) Assert.Inconclusive("ffmpeg.exe is not on PATH.");
        }

        // A config root holding this machine's rig.yml with these lines.
        string ConfigRoot(params string[] lines)
        {
            var root = Path.Combine(folder, "config");
            var machine = Path.Combine(root, Environment.MachineName);
            Directory.CreateDirectory(machine);
            File.WriteAllLines(Path.Combine(machine, "rig.yml"), new[] { "rootPath: C:\\Data" }.Concat(lines));
            return root;
        }

        static IplImage Noise(Random random, int width = Width, int height = Height, int channels = 1)
        {
            var image = new IplImage(new Size(width, height), IplDepth.U8, channels);
            var row = new byte[width * channels];
            for (var y = 0; y < height; y++)
            {
                random.NextBytes(row);
                Marshal.Copy(row, 0, image.ImageData + y * image.WidthStep, row.Length);
            }
            return image;
        }

        static Timestamped<VideoDataFrame> Frame(int id, IplImage image)
        {
            return Timestamped.Create(new VideoDataFrame(image, new VideoChunkData(id, 1000000L + id * 20000000L, 19000.5, 0)), 3900000000.0 + id / FrameRate);
        }

        Timestamped<VideoDataFrame>[] Frames(int count, int channels = 1)
        {
            var random = new Random(count);
            return Enumerable.Range(0, count).Select(id => Frame(id, Noise(random, channels: channels))).ToArray();
        }

        string Video => Path.Combine(folder, "Camera-Test", "Camera-Test_2026-11-14T10-00-00.mkv");

        // The frames ffprobe decodes from a video: every frame in the file, not what an index says.
        int DecodedFrames(string video)
        {
            var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe"),
                "-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 \"" + video + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return int.Parse(output.Trim().Split(',')[0], CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void WritesEveryFrameToTheVideoAndARowForEachToTheCsv()
        {
            RequireFfmpeg();
            var frames = Frames(75);
            var writer = new FfmpegVideoWriter { FileName = Video, FrameRate = FrameRate, ConfigRoot = ConfigRoot("videoEncoder:", "  codec: libx264") };
            var passed = writer.Process(frames.ToObservable()).ToArray().Wait();

            Assert.AreEqual(75, passed.Length, "every frame is passed on");
            Assert.AreEqual(75, DecodedFrames(Video));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(Video, ".error.txt")));
            var lines = File.ReadAllLines(Path.ChangeExtension(Video, ".csv"));
            Assert.AreEqual("harpTime,frameId,cameraClockNs,exposureUs,gainDb", lines[0]);
            Assert.AreEqual(76, lines.Length);
            for (var id = 0; id < 75; id++)
            {
                var columns = lines[id + 1].Split(',');
                Assert.AreEqual(frames[id].Seconds, double.Parse(columns[0], CultureInfo.InvariantCulture));
                Assert.AreEqual(id.ToString(CultureInfo.InvariantCulture), columns[1]);
                Assert.AreEqual((1000000L + id * 20000000L).ToString(CultureInfo.InvariantCulture), columns[2]);
                Assert.AreEqual("19000.5", columns[3]);
                Assert.AreEqual("0", columns[4]);
            }
        }

        // OpenRigs.Devices' TriggeredSpinnaker include gives a type of its own with VideoDataFrame's
        // members, not a VideoDataFrame; the writer reads it by member name and passes it on as it came.
        [TestMethod]
        public void TakesAnyFrameTypeWithImageAndChunkData()
        {
            RequireFfmpeg();
            var frames = Frames(30).Select(f => Timestamped.Create(new
            {
                Image = f.Value.Image,
                ChunkData = new { FrameID = f.Value.ChunkData.FrameID, Timestamp = f.Value.ChunkData.Timestamp, ExposureTime = 19000.5, Gain = 1.5, Width = Width },
            }, f.Seconds)).ToArray();
            var writer = new FfmpegVideoWriter { FileName = Video, FrameRate = FrameRate, ConfigRoot = ConfigRoot() };
            var passed = writer.Process(frames.ToObservable()).ToArray().Wait();

            CollectionAssert.AreEqual(frames, passed, "each frame is passed on as it came");
            Assert.AreEqual(30, DecodedFrames(Video));
            var lines = File.ReadAllLines(Path.ChangeExtension(Video, ".csv"));
            Assert.AreEqual(31, lines.Length);
            Assert.AreEqual(string.Join(",", frames[7].Seconds.ToString("R", CultureInfo.InvariantCulture), "7", (1000000L + 7 * 20000000L).ToString(CultureInfo.InvariantCulture), "19000.5", "1.5"), lines[8]);
        }

        [TestMethod]
        public void AFrameTypeWithoutExposureOrGain_LogsNaN()
        {
            RequireFfmpeg();
            var frames = Frames(5).Select(f => Timestamped.Create(new
            {
                Image = f.Value.Image,
                ChunkData = new { FrameID = (int)f.Value.ChunkData.FrameID, Timestamp = f.Value.ChunkData.Timestamp },
            }, f.Seconds)).ToArray();
            var writer = new FfmpegVideoWriter { FileName = Video, FrameRate = FrameRate, ConfigRoot = ConfigRoot() };
            writer.Process(frames.ToObservable()).LastOrDefaultAsync().Wait();
            var columns = File.ReadAllLines(Path.ChangeExtension(Video, ".csv"))[3].Split(',');
            Assert.AreEqual("2", columns[1], "an int FrameID is taken too");
            Assert.AreEqual("NaN", columns[3]);
            Assert.AreEqual("NaN", columns[4]);
        }

        [TestMethod]
        public void AFrameTypeWithoutImage_IsRefusedNamingIt()
        {
            var writer = new FfmpegVideoWriter { FileName = Video, ConfigRoot = ConfigRoot() };
            var frames = new[] { Timestamped.Create(new { Picture = (IplImage)null, ChunkData = new { FrameID = 1L, Timestamp = 2L } }, 0.0) };
            var error = Assert.ThrowsException<InvalidOperationException>(() => writer.Process(frames.ToObservable()));
            StringAssert.Contains(error.Message, "has no Image");
        }

        [TestMethod]
        public void WritesColourFramesToo()
        {
            RequireFfmpeg();
            var writer = new FfmpegVideoWriter { FileName = Video, ConfigRoot = ConfigRoot() };
            writer.Process(Frames(20, channels: 3).ToObservable()).LastOrDefaultAsync().Wait();
            Assert.AreEqual(20, DecodedFrames(Video));
        }

        [TestMethod]
        public void RefusesToWriteOverAVideo()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Video));
            File.WriteAllText(Video, "");
            var writer = new FfmpegVideoWriter { FileName = Video, ConfigRoot = ConfigRoot("videoEncoder: { ffmpegPath: C:\\Windows\\System32\\PING.EXE }") };
            var error = Assert.ThrowsException<IOException>(() => writer.Process(Frames(1).ToObservable()).Wait());
            StringAssert.Contains(error.Message, "already exists");
        }

        [TestMethod]
        public void DisposedBeforeTheFramesEnd_ReturnsAtOnceAndStillClosesTheFile()
        {
            RequireFfmpeg();
            var recording = new FfmpegRecording(Video, FrameRate, VideoEncoder.Read(Path.Combine(folder, "none.yml"), Path.GetDirectoryName(ffmpeg)),
                FfmpegVideoWriter.DefaultQueueCapacity, TimeSpan.FromSeconds(20), TextWriter.Null);
            foreach (var frame in Frames(120)) recording.Add(frame);
            var clock = Stopwatch.StartNew();
            recording.End();
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(0.5), "End returns at once: " + clock.Elapsed);
            Assert.IsTrue(recording.Wait(TimeSpan.FromSeconds(20)));
            Assert.AreEqual(0, recording.ExitCode);
            Assert.AreEqual(120, recording.Written);
            Assert.AreEqual(120, DecodedFrames(Video));
            Assert.AreEqual(121, File.ReadAllLines(recording.CsvFileName).Length);
        }

        // An ffmpeg that never reads its stdin and never exits (PING.EXE, which ignores stdin): the
        // writer blocks on its first frame, the queue fills, and the end comes at the stop timeout.
        FfmpegRecording Stuck(int capacity, TimeSpan stopTimeout)
        {
            var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            var encoder = new VideoEncoder(VideoEncoder.DefaultCodec, ping, "-n 120 127.0.0.1");
            return new FfmpegRecording(Video, FrameRate, encoder, capacity, stopTimeout, TextWriter.Null);
        }

        [TestMethod]
        public void AnFfmpegThatNeverFinishes_IsKilledAtTheStopTimeoutAndReported()
        {
            var recording = Stuck(capacity: 5, stopTimeout: TimeSpan.FromSeconds(2));
            foreach (var frame in Frames(20)) recording.Add(frame);
            var clock = Stopwatch.StartNew();
            var problem = recording.Finish();
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3.5), "Finish waits about the stop timeout: " + clock.Elapsed);
            Assert.IsNotNull(problem);
            StringAssert.Contains(problem, "was killed");
            StringAssert.Contains(problem, "frames were dropped");
            Assert.IsTrue(recording.Dropped >= 14, "the queue held 5, the writer 1: " + recording.Dropped);
            Assert.AreEqual(problem + Environment.NewLine, File.ReadAllText(recording.ErrorFileName));
        }

        [TestMethod]
        public void AFullQueue_NeverBlocksTheCamerasThread()
        {
            var recording = Stuck(capacity: 3, stopTimeout: TimeSpan.FromSeconds(1));
            var frames = Frames(50);
            var clock = Stopwatch.StartNew();
            foreach (var frame in frames) recording.Add(frame);
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(1), "50 frames added in " + clock.Elapsed);
            Assert.AreEqual(50, recording.Received);
            Assert.IsTrue(recording.Dropped >= 46);
            recording.Finish();
        }

        [TestMethod]
        public void AnFfmpegThatFails_StopsTheWorkflowNamingIt()
        {
            var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            var writer = new FfmpegVideoWriter
            {
                FileName = Video,
                ConfigRoot = ConfigRoot("videoEncoder:", "  ffmpegPath: " + ping),
                QueueCapacity = 2,
                StopTimeout = TimeSpan.FromSeconds(1),
            };
            // PING.EXE given ffmpeg's arguments refuses them and exits at once, non-zero: the error names it.
            var error = Assert.ThrowsException<InvalidOperationException>(() => writer.Process(Frames(30).ToObservable()).Wait());
            StringAssert.Contains(error.Message, "ffmpeg stopped while recording (exit code 1)");
        }

        // ffmpeg killed mid-run (the whole process tree killed): the file is still a readable video,
        // short by at most the open cluster (1 s) and what the encoder held.
        [TestMethod]
        public void AKilledFfmpegLeavesAReadableVideo()
        {
            RequireFfmpeg();
            var recording = new FfmpegRecording(Video, FrameRate, VideoEncoder.Read(Path.Combine(folder, "none.yml"), Path.GetDirectoryName(ffmpeg)),
                FfmpegVideoWriter.DefaultQueueCapacity, TimeSpan.FromSeconds(5), TextWriter.Null);
            var frames = Frames(50);
            var clock = Stopwatch.StartNew();
            Exception failed = null;
            for (var n = 0; n < 250 && failed == null; n++)  // 5 s at 50 Hz, ffmpeg killed after 3
            {
                while (clock.Elapsed.TotalSeconds < n / FrameRate) Thread.Sleep(1);
                if (n == 150)
                {
                    while (recording.Written < 140) Thread.Sleep(1);
                    Process.GetProcessById(recording.FfmpegProcessId.Value).Kill();
                }
                try { recording.Add(Frame(n, frames[n % frames.Length].Value.Image)); }
                catch (InvalidOperationException ex) { failed = ex; }
            }
            Assert.IsNotNull(failed, "frames after ffmpeg died are refused");
            StringAssert.Contains(failed.Message, "ffmpeg");
            Assert.IsNotNull(recording.Finish());
            var decoded = DecodedFrames(Video);
            Console.WriteLine("killed after " + recording.Written + " frames written; the file holds " + decoded);
            // Lost: the open cluster (at most 1 s) and the frames inside ffmpeg (20 of 150 measured).
            Assert.IsTrue(decoded >= 140 - FrameRate && decoded <= recording.Written, decoded + " frames of " + recording.Written);
        }

        // Not a test: how fast this machine encodes three Pupil-like cameras (1440 x 1080 Mono8 at
        // 50 Hz, a smooth image under frame-to-frame noise of about 3.5 grey levels). Runs only with
        // OPENRIGS_VIDEO_BENCHMARK set to a codec (such as h264_nvenc); prints the numbers.
        [TestMethod]
        public void Benchmark()
        {
            var codec = Environment.GetEnvironmentVariable("OPENRIGS_VIDEO_BENCHMARK");
            if (string.IsNullOrEmpty(codec)) Assert.Inconclusive("Set OPENRIGS_VIDEO_BENCHMARK to a codec to run it.");
            RequireFfmpeg();
            const int Cameras = 3, Pool = 50;
            var seconds = double.Parse(Environment.GetEnvironmentVariable("OPENRIGS_VIDEO_BENCHMARK_SECONDS") ?? "20", CultureInfo.InvariantCulture);
            var size = new Size(1440, 1080);
            var images = new IplImage[Cameras][];
            ulong rng = 12345;
            using (var scene = new IplImage(size, IplDepth.U8, 1))
            using (var noise = new IplImage(size, IplDepth.U8, 1))
            {
                scene.Set(Scalar.All(113));
                CV.Circle(scene, new Point(720, 540), 300, Scalar.All(160), -1);
                CV.Circle(scene, new Point(720, 540), 120, Scalar.All(25), -1);
                CV.Smooth(scene, scene, SmoothMethod.Gaussian, 31, 31, 0, 0);
                for (var c = 0; c < Cameras; c++)
                {
                    images[c] = new IplImage[Pool];
                    for (var i = 0; i < Pool; i++)
                    {
                        CV.RandArr(ref rng, noise, RandDistribution.Normal, Scalar.All(128), Scalar.All(3.5));
                        images[c][i] = new IplImage(size, IplDepth.U8, 1);
                        CV.AddWeighted(scene, 1, noise, 1, -128, images[c][i]);
                    }
                }
            }
            var encoder = VideoEncoder.Read(ConfigRoot("videoEncoder: { codec: " + codec + " }") + "\\" + Environment.MachineName + "\\rig.yml", Path.GetDirectoryName(ffmpeg));
            foreach (var paced in new[] { true, false })
            {
                var recordings = Enumerable.Range(0, Cameras).Select(c => new FfmpegRecording(
                    Path.Combine(folder, (paced ? "paced" : "flat-out") + c + ".mkv"), FrameRate, encoder,
                    FfmpegVideoWriter.DefaultQueueCapacity, FfmpegVideoWriter.DefaultStopTimeout, Console.Out)).ToArray();
                var host = Process.GetCurrentProcess();
                var hostStart = host.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                var mostWaiting = 0;
                var n = 0;
                for (; clock.Elapsed.TotalSeconds < seconds; n++)
                {
                    if (paced) while (clock.Elapsed.TotalSeconds < n / FrameRate) Thread.Sleep(1);
                    else while (recordings.Any(r => r.Waiting >= FfmpegVideoWriter.DefaultQueueCapacity - 1)) Thread.Sleep(1);
                    for (var c = 0; c < Cameras; c++) recordings[c].Add(Frame(n, images[c][n % Pool]));
                    mostWaiting = Math.Max(mostWaiting, recordings.Max(r => r.Waiting));
                }
                var ids = recordings.Select(r => r.FfmpegProcessId.Value).ToArray();
                var encoders = ids.Select(Process.GetProcessById).ToArray();
                foreach (var p in encoders) _ = p.Handle;  // held open, so its CPU time is read after it exits
                var problems = recordings.Select(r => r.Finish()).ToArray();
                var elapsed = clock.Elapsed.TotalSeconds;
                var ffmpegCpu = encoders.Sum(p => p.TotalProcessorTime.TotalSeconds);
                var hostCpu = (host.TotalProcessorTime - hostStart).TotalSeconds;
                var bytes = recordings.Sum(r => new FileInfo(Path.Combine(folder, Path.GetFileNameWithoutExtension(r.CsvFileName) + ".mkv")).Length);
                var written = recordings.Sum(r => r.Written);
                Console.WriteLine("{0} {1}: {2} frames a camera in {3:F1} s = {4:F1} fps a camera; dropped {5}; most waiting {6}; " +
                    "ffmpeg CPU {7:F0}% of one core (all three), writer process {8:F0}%; {9:F0} KB a frame; problems: {10}",
                    codec, paced ? "paced at 50 Hz" : "flat out", n, elapsed, written / Cameras / elapsed, recordings.Sum(r => r.Dropped), mostWaiting,
                    100 * ffmpegCpu / elapsed, 100 * hostCpu / elapsed, bytes / 1024.0 / written, string.Join(" ", problems.Where(p => p != null)));
            }
        }

        [TestMethod]
        public void NoFfmpegOnPath_StopsAtStart()
        {
            var writer = new FfmpegVideoWriter { FileName = Video, ConfigRoot = ConfigRoot() };
            var path = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", folder);
            try
            {
                var error = Assert.ThrowsException<InvalidOperationException>(() => writer.Process(Frames(1).ToObservable()).Wait());
                StringAssert.Contains(error.Message, "ffmpeg.exe is not on PATH");
            }
            finally { Environment.SetEnvironmentVariable("PATH", path); }
        }

        [TestMethod]
        public void AnFfmpegPathThatIsNotAFile_StopsAtStart()
        {
            var writer = new FfmpegVideoWriter { FileName = Video, ConfigRoot = ConfigRoot("videoEncoder:", "  ffmpegPath: C:\\nowhere\\ffmpeg.exe") };
            var error = Assert.ThrowsException<InvalidOperationException>(() => writer.Process(Frames(1).ToObservable()).Wait());
            StringAssert.Contains(error.Message, "ffmpegPath is C:\\nowhere\\ffmpeg.exe, which is not a file");
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(Video)), "nothing is written");
        }

        [TestMethod]
        public void RigYmlNamesTheCodec()
        {
            var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            string Rig(params string[] lines) => Path.Combine(ConfigRoot(lines), Environment.MachineName, "rig.yml");
            // A PATH whose second folder holds an ffmpeg.exe (an empty file: only found, never run).
            var bin = Path.Combine(folder, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "ffmpeg.exe"), "");
            var path = Path.GetDirectoryName(ping) + "; \"" + bin + "\"";
            var none = VideoEncoder.Read(Path.Combine(folder, "none.yml"), path);
            Assert.AreEqual("libx264", none.Codec, "no rig.yml");
            Assert.AreEqual(Path.Combine(bin, "ffmpeg.exe"), none.FfmpegPath, "the first ffmpeg.exe on PATH");
            Assert.AreEqual("libx264", VideoEncoder.Read(Rig(), path).Codec, "no section");
            Assert.AreEqual("libx264", VideoEncoder.Read(Rig("videoEncoder:"), path).Codec, "an empty section");
            var nvenc = VideoEncoder.Read(Rig("videoEncoder: { codec: h264_nvenc, ffmpegPath: " + ping + " }"), null);
            Assert.AreEqual("h264_nvenc", nvenc.Codec);
            Assert.AreEqual(ping, nvenc.FfmpegPath, true, CultureInfo.InvariantCulture);
            StringAssert.Contains(nvenc.Arguments(1440, 1080, "gray", 50, "a.mkv"),
                "-f rawvideo -pix_fmt gray -video_size 1440x1080 -framerate 50 -i pipe:0 -an -c:v h264_nvenc");
            StringAssert.Contains(VideoEncoder.Read(Rig("videoEncoder: { codec: h264_qsv }"), path).CodecArguments, "h264_qsv");
        }

        [TestMethod]
        public void RigYmlRefusesAnUnknownCodecOrKey()
        {
            string Rig(params string[] lines) => Path.Combine(ConfigRoot(lines), Environment.MachineName, "rig.yml");
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var codec = Assert.ThrowsException<InvalidOperationException>(() => VideoEncoder.Read(Rig("videoEncoder: { codec: h265 }"), system));
            StringAssert.Contains(codec.Message, "codec is 'h265', not one of libx264, h264_nvenc, h264_qsv");
            var key = Assert.ThrowsException<InvalidOperationException>(() => VideoEncoder.Read(Rig("videoEncoder: { preset: fast }"), system));
            StringAssert.Contains(key.Message, "has 'preset'");
        }
    }
}
