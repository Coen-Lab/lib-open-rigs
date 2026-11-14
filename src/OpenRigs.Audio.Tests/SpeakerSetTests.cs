using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;

namespace OpenRigs.Audio.Tests
{
    [TestClass]
    public class SpeakerSetTests
    {
        static string Rig(string speakers = null, string extra = "  syncChannel: 31\n  errorSoundChannel: 2\n", double[] taps = null, string machine = "TEST-RIG")
        {
            var folder = Path.Combine(Path.GetTempPath(), "audio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "calibration"));
            taps = taps ?? new[] { 0.25, 0.5, 0.25 };
            foreach (var name in new[] { "left", "right", "centre" })
            {
                File.WriteAllText(Path.Combine(folder, "calibration", "speaker-" + name + ".json"),
                    "{\"calibratedAt\": \"2026-11-14T00:00:00Z\", \"calibratedBy\": \"test\", \"machineName\": \"" + machine + "\", \"sampleRate\": 48000, \"taps\": [" +
                    string.Join(", ", taps.Select(t => t.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]}");
            }
            speakers = speakers ??
                "    left:\n      position: {azimuth: -90.0, elevation: 15.0, radius: 1.0}\n      channel: 0\n      filter: calibration/speaker-left.json\n" +
                "    right:\n      position: {azimuth: 90.0, elevation: 15.0, radius: 1.0}\n      channel: 1\n      filter: calibration/speaker-right.json\n" +
                "    centre:\n      position: {azimuth: 0.0, elevation: 15.0, radius: 1.0}\n      channel: 2\n      filter: calibration/speaker-centre.json\n";
            var path = Path.Combine(folder, "rig.yml");
            File.WriteAllText(path, "speakerArray:\n  device:\n    sampleRate: 48000\n    channelCount: 32\n  speakers:\n" + speakers + extra);
            return path;
        }

        static Mat Row(params float[] values)
        {
            var mat = new Mat(1, values.Length, Depth.F32, 1);
            Marshal.Copy(values, 0, mat.Data, values.Length);
            return mat;
        }

        static float[] Values(Mat mat, int row)
        {
            var values = new float[mat.Cols];
            using (var r = mat.GetRow(row))
            using (var copy = new Mat(1, mat.Cols, Depth.F32, 1))
            {
                CV.Copy(r, copy);
                Marshal.Copy(copy.Data, values, 0, values.Length);
            }
            return values;
        }

        [TestMethod]
        public void LoadsEverySpeakerWithItsChannelAndTheArraysChannels()
        {
            var set = SpeakerSet.Load(Rig(), "TEST-RIG");
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, set.SpeakerChannels);
            Assert.AreEqual(32, set.ChannelCount);
            Assert.AreEqual(31, set.SyncChannel);
            Assert.AreEqual(2, set.ErrorSoundChannel);
            Assert.AreEqual(-90.0, set.Speakers[0].AzimuthDeg);
        }

        [TestMethod]
        public void ABufferPlaysOnlyOnItsChannelsEqualisedAndScaled()
        {
            var set = SpeakerSet.Load(Rig(), "TEST-RIG");
            var buffer = set.Buffer(Row(0, 0, 4, 0, 0), new[] { 1 }, 0.5);
            Assert.AreEqual(32, buffer.Rows);
            // An impulse through the [0.25, 0.5, 0.25] filter, centred (not delayed), at half gain.
            CollectionAssert.AreEqual(new float[] { 0, 0.5f, 1, 0.5f, 0 }, Values(buffer, 1));
            CollectionAssert.AreEqual(new float[5], Values(buffer, 0));
            CollectionAssert.AreEqual(new float[5], Values(buffer, 31));  // nothing on the sync channel
        }

        [TestMethod]
        public void NoChannelsMeansEverySpeakerAndNoOtherChannel()
        {
            var buffer = SpeakerSet.Load(Rig(), "TEST-RIG").Buffer(Row(0, 4, 0), null, 1);
            foreach (var channel in new[] { 0, 1, 2 }) CollectionAssert.AreEqual(new float[] { 1, 2, 1 }, Values(buffer, channel));
            foreach (var channel in Enumerable.Range(3, 29)) CollectionAssert.AreEqual(new float[3], Values(buffer, channel));
        }

        [TestMethod]
        public void ABackgroundIsPlayedAsItIsOnEverySpeaker()
        {
            var buffer = SpeakerSet.Load(Rig(), "TEST-RIG").Buffer(Row(0, 4, 0), null, 0.5, equalise: false);
            foreach (var channel in new[] { 0, 1, 2 }) CollectionAssert.AreEqual(new float[] { 0, 2, 0 }, Values(buffer, channel));
            CollectionAssert.AreEqual(new float[3], Values(buffer, 31));
        }

        [TestMethod]
        public void TheErrorSoundPlaysOnTheErrorChannelAloneAtTheLabsLevel()
        {
            var set = SpeakerSet.Load(Rig(taps: new[] { 0.0, 1.0, 0.0 }), "TEST-RIG");
            var buffer = ErrorSound.Buffer(set, 2.0);
            using (var tone = ErrorSound.Read(48000))
            {
                Assert.AreEqual(48000, buffer.Cols);  // one second at 48 kHz
                var expected = Values(tone, 0).Select(v => (float)(v * ErrorSound.Level * 2.0)).ToArray();
                var actual = Values(buffer, 2);
                for (int i = 0; i < expected.Length; i += 997) Assert.AreEqual(expected[i], actual[i], 1e-6);
            }
            Assert.AreEqual(0f, Values(buffer, 0).Max(Math.Abs));
            Assert.AreEqual(0f, Values(buffer, 31).Max(Math.Abs));
        }

        [TestMethod]
        public void EveryFilterProblemIsNamed()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(Rig(taps: new[] { 0.1, 0.5, 0.3 }, machine: "OTHER"), "TEST-RIG"));
            StringAssert.Contains(ex.Message, "speaker 'left' filter's taps are not symmetric");
            var missing = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(Rig(), "ANOTHER-RIG"));
            StringAssert.Contains(missing.Message, "was measured on 'TEST-RIG', not on this machine (ANOTHER-RIG)");
            var unfiltered = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(Rig(
                "    left:\n      channel: 0\n      filter: calibration/speaker-left.json\n    right:\n      channel: 1\n"), "TEST-RIG"));
            StringAssert.Contains(unfiltered.Message, "speaker 'right' has no filter");
        }

        [TestMethod]
        public void TheErrorSoundNeedsAnErrorChannel()
        {
            var set = SpeakerSet.Load(Rig(extra: "  syncChannel: 31\n"), "TEST-RIG");
            var ex = Assert.ThrowsException<InvalidOperationException>(() => ErrorSound.Buffer(set, 1));
            StringAssert.Contains(ex.Message, "no errorSoundChannel");
        }

        [TestMethod]
        public void AWaveAtAnotherRateIsRefused()
        {
            var ex = Assert.ThrowsException<InvalidDataException>(() => ErrorSound.Read(44100));
            StringAssert.Contains(ex.Message, "is 48000 Hz, but the audio device runs at 44100 Hz");
        }
    }
}
