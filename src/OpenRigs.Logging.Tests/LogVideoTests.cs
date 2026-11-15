using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;
using Capture = OpenCV.Net.Capture;
using OpenRigs.Tests;

namespace OpenRigs.Logging.Tests
{
    [TestClass]
    public class LogVideoTests : LoggingTestBase
    {
        const double SourceFrameRate = 30;

        // Solid frames survive the lossy codec almost unchanged, but each encode can still shift the
        // mean of a channel by a few levels, and the logged video has been encoded twice.
        const double ColorTolerance = 12;

        static readonly string[] ExpectedHeader = { "Seconds", "Value.Width", "Value.Height" };

        protected override string WorkflowFileName => "LogVideoTest.bonsai";

        // Kept beside the log root rather than in it, so that it is never mistaken for a log.
        string SourceVideo => Path.Combine(TestDirectory, "source.avi");

        // Frame sizes are kept at 160x120 or above, since the FMP4 encoder writes unreadable videos at e.g. 64x48.
        [DataTestMethod]
        [DataRow("TestVideo", 10, 320, 240)]
        [DataRow("Video", 25, 160, 120)]
        public void LogRawVideo_FileCapture_WritesEveryFrameToVideoAndMetadata(
            string logName,
            int frameCount,
            int width,
            int height)
        {
            var colors = GenerateSourceVideo(frameCount, width, height);
            var result = RunWorkflow(logName);
            var logFiles = GetLogFiles(result);

            var frames = ReadFrameColors(logFiles.Video, width, height, result);
            Assert.AreEqual(
                frameCount,
                frames.Count,
                $"Expected every frame of the source video to be written to '{logFiles.Video}'." +
                result.Describe());

            // Each source frame is a distinct solid color, so matching them in order shows that no
            // frames were dropped, duplicated or reordered along the way.
            for (var frame = 0; frame < frameCount; frame++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var expected = GetChannel(colors[frame], channel);
                    var actual = GetChannel(frames[frame], channel);
                    Assert.IsTrue(
                        Math.Abs(expected - actual) <= ColorTolerance,
                        $"Expected channel {channel} of frame {frame} to be close to {expected} but found " +
                        $"{actual:F1}.{result.Describe()}");
                }
            }

            var rows = ReadCsvLog(logFiles.Metadata, ExpectedHeader, frameCount, result);
            var previousSeconds = double.NegativeInfinity;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var columns = rows[frame];
                var seconds = ParseDoubleColumn(columns, 0, ExpectedHeader, frame, result);

                // The test time base only ever counts up, so the frame timestamps must too.
                Assert.IsTrue(
                    seconds >= previousSeconds,
                    $"Expected '{ExpectedHeader[0]}' to be non-decreasing, but frame {frame} has {seconds} " +
                    $"after {previousSeconds}.{result.Describe()}");
                previousSeconds = seconds;

                Assert.AreEqual(
                    width,
                    ParseIntegerColumn(columns, 1, ExpectedHeader, frame, result),
                    $"Unexpected '{ExpectedHeader[1]}' in frame {frame}.{result.Describe()}");
                Assert.AreEqual(
                    height,
                    ParseIntegerColumn(columns, 2, ExpectedHeader, frame, result),
                    $"Unexpected '{ExpectedHeader[2]}' in frame {frame}.{result.Describe()}");
            }
        }

        [TestMethod]
        public void LogRawVideo_FileCapture_WritesExpectedDirectoryStructure()
        {
            const string LogName = "TestVideo";
            GenerateSourceVideo(frameCount: 5, width: 160, height: 120);
            var result = RunWorkflow(LogName);
            var logFiles = GetLogFiles(result);

            // The video and its metadata describe the same frames, so they are expected to be
            // written side by side under one name, differing only by extension.
            Assert.AreEqual(
                Path.ChangeExtension(logFiles.Video, null),
                Path.ChangeExtension(logFiles.Metadata, null),
                $"Expected the video and metadata logs to share a folder and base name.{result.Describe()}");

            var segments = AssertSessionFolders(logFiles.Video, result);
            Assert.AreEqual(
                2,
                segments.Length,
                $"Expected the logs to sit one folder below the session folder, but found " +
                $"'{string.Join(Path.DirectorySeparatorChar.ToString(), segments)}'.{result.Describe()}");

            Assert.AreEqual(
                LogName,
                segments[0],
                $"Expected the log subfolder to be named after LogName.{result.Describe()}");

            StringAssert.Matches(
                segments[1],
                new Regex($@"^{Regex.Escape(LogName)}_.+\.avi$"),
                $"Expected the video file to sit inside the '{LogName}' folder and be prefixed with " +
                $"it.{result.Describe()}");
        }

        /// <summary>
        /// Writes a video of solid, randomly colored frames to the source path, and returns the
        /// color of each frame in BGR order.
        /// </summary>
        Scalar[] GenerateSourceVideo(int frameCount, int width, int height)
        {
            // Seeded so that a failing run can be reproduced exactly.
            var random = new Random(frameCount);
            var colors = new Scalar[frameCount];
            var size = new Size(width, height);
            using (var writer = new VideoWriter(SourceVideo, VideoWriter.FourCC('F', 'M', 'P', '4'), SourceFrameRate, size))
            using (var image = new IplImage(size, IplDepth.U8, 3))
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    colors[frame] = Scalar.Rgb(random.Next(256), random.Next(256), random.Next(256));
                    image.Set(colors[frame]);
                    writer.WriteFrame(image);
                }
            }

            // Confirms the fixture itself is sound, so a failure later on points at the logger.
            var decodedFrames = 0;
            using (var capture = Capture.CreateFileCapture(SourceVideo))
            {
                Assert.IsNotNull(capture, $"Could not open the generated source video '{SourceVideo}'.");
                while (capture.QueryFrame() != null)
                {
                    decodedFrames++;
                }
            }

            Assert.AreEqual(
                frameCount,
                decodedFrames,
                $"Expected the generated source video '{SourceVideo}' to decode to every frame written.");
            return colors;
        }

        /// <summary>
        /// Decodes every frame of a video, checking its size and returning its mean color.
        /// </summary>
        static List<Scalar> ReadFrameColors(string fileName, int width, int height, BonsaiWorkflowResult result)
        {
            var colors = new List<Scalar>();
            using (var capture = Capture.CreateFileCapture(fileName))
            {
                Assert.IsNotNull(capture, $"Could not open '{fileName}' for reading.{result.Describe()}");

                // Frames returned by the capture are owned by it and must not be disposed here.
                IplImage image;
                while ((image = capture.QueryFrame()) != null)
                {
                    Assert.AreEqual(
                        new Size(width, height),
                        image.Size,
                        $"Unexpected size of frame {colors.Count} in '{fileName}'.{result.Describe()}");
                    colors.Add(CV.Avg(image));
                }
            }

            return colors;
        }

        static double GetChannel(Scalar scalar, int channel)
        {
            switch (channel)
            {
                case 0: return scalar.Val0;
                case 1: return scalar.Val1;
                case 2: return scalar.Val2;
                default: return scalar.Val3;
            }
        }

        BonsaiWorkflowResult RunWorkflow(string logName)
        {
            return RunWorkflow(new Dictionary<string, string>
            {
                { "FileName", SourceVideo },
                { "PlaybackRate", SourceFrameRate.ToString(CultureInfo.InvariantCulture) },
                { "LogName", logName }
            });
        }

        (string Video, string Metadata) GetLogFiles(BonsaiWorkflowResult result)
        {
            return (GetSingleLogFile("*.avi", result), GetSingleLogFile("*.csv", result));
        }
    }
}
