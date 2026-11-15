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
        static string Rig(string speakers = null, string extra = "  syncChannel: 31\n  penaltySoundChannel: 2\n", double[] taps = null, string machine = "TEST-RIG")
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
            Assert.AreEqual(2, set.PenaltySoundChannel);
            Assert.AreEqual(-90.0, set.Speakers[0].AzimuthDeg);
        }

        [TestMethod]
        public void ABufferPlaysOnlyOnItsChannelsEqualisedAndScaled()
        {
            var set = SpeakerSet.Load(Rig(), "TEST-RIG");
            var buffer = set.Layout(Row(0, 0, 4, 0, 0), new[] { 1 }, 0.5);
            Assert.AreEqual(32, buffer.Rows);
            // An impulse through the [0.25, 0.5, 0.25] filter, centred (not delayed), at half gain.
            CollectionAssert.AreEqual(new float[] { 0, 0.5f, 1, 0.5f, 0 }, Values(buffer, 1));
            CollectionAssert.AreEqual(new float[5], Values(buffer, 0));
            CollectionAssert.AreEqual(new float[5], Values(buffer, 31));  // nothing on the sync channel
        }

        [TestMethod]
        public void NoChannelsMeansEverySpeakerAndNoOtherChannel()
        {
            var buffer = SpeakerSet.Load(Rig(), "TEST-RIG").Layout(Row(0, 4, 0), null, 1);
            foreach (var channel in new[] { 0, 1, 2 }) CollectionAssert.AreEqual(new float[] { 1, 2, 1 }, Values(buffer, channel));
            foreach (var channel in Enumerable.Range(3, 29)) CollectionAssert.AreEqual(new float[3], Values(buffer, channel));
        }

        [TestMethod]
        public void ABackgroundIsPlayedAsItIsOnEverySpeaker()
        {
            var buffer = SpeakerSet.Load(Rig(), "TEST-RIG").Layout(Row(0, 4, 0), null, 0.5, equalise: false);
            foreach (var channel in new[] { 0, 1, 2 }) CollectionAssert.AreEqual(new float[] { 0, 2, 0 }, Values(buffer, channel));
            CollectionAssert.AreEqual(new float[3], Values(buffer, 31));
        }

        [TestMethod]
        public void ThePenaltySoundPlaysOnThePenaltyChannelAloneAtTheTasksLevel()
        {
            var set = SpeakerSet.Load(Rig(taps: new[] { 0.0, 1.0, 0.0 }), "TEST-RIG");
            var buffer = PenaltySound.Buffer(set, -44.6);
            using (var tone = PenaltySound.Read(48000))
            using (var row = buffer.GetRow(2))
            {
                Assert.AreEqual(48000, buffer.Cols);  // one second at 48 kHz
                Assert.AreEqual(-44.6, SoundLevel.RmsDbfs(row), 1e-4);
                // The level the tasks played it at until 15 Nov 2026 (half the file), so their task files keep it.
                Assert.AreEqual(-44.6, 20 * Math.Log10(0.5) + SoundLevel.RmsDbfs(tone), 0.05);
            }
            Assert.AreEqual(0f, Values(buffer, 0).Max(Math.Abs));
            Assert.AreEqual(0f, Values(buffer, 31).Max(Math.Abs));
        }

        [TestMethod]
        public void ASoundPlaysAtItsLevelInDbfsRmsOfTheSoundAsGiven()
        {
            var set = SpeakerSet.Load(Rig(), "TEST-RIG");
            // A square wave of amplitude 0.5 is -6.02 dBFS RMS; at -20 it plays 10^(-1) / 0.5 = 0.2 times as loud.
            var sound = Row(0.5f, -0.5f, 0.5f, -0.5f);
            Assert.AreEqual(-6.0206, SoundLevel.RmsDbfs(sound), 1e-4);
            var buffer = set.Buffer(sound, new[] { 1 }, -20, equalise: false);
            CollectionAssert.AreEqual(new[] { 0.1f, -0.1f, 0.1f, -0.1f }, Values(buffer, 1).Select(v => (float)Math.Round(v, 6)).ToArray());
            Assert.AreEqual(Math.Pow(10, -1) / 0.5, SoundLevel.Gain(sound, -20), 1e-12);
        }

        [TestMethod]
        public void APeakAtTheLimitAfterEqualisationStops()
        {
            // One impulse in four samples is -6.02 dBFS RMS with its peak 6.02 dB above: at -5 dBFS RMS it peaks at +1 dBFS
            // as it is, and at -5 dBFS through the [0.25, 0.5, 0.25] filter, which halves it.
            var set = SpeakerSet.Load(Rig(), "TEST-RIG");
            var ex = Assert.ThrowsException<InvalidOperationException>(() => set.Buffer(Row(0, 0, 1, 0), new[] { 1 }, -5, equalise: false, what: "The test tone"));
            StringAssert.Contains(ex.Message, "The test tone peaks at 1.02 dBFS on channel 1 after equalisation, at or above the lab's -1 dBFS limit: lower its levelDbfs by more than 2.02 dB.");
            var equalised = set.Buffer(Row(0, 0, 1, 0), new[] { 1 }, -5);
            Assert.AreEqual(-5, 20 * Math.Log10(Values(equalised, 1).Max()), 1e-4);
            Assert.ThrowsException<InvalidOperationException>(() => set.Buffer(Row(0, 0, 0, 0), new[] { 1 }, -30));  // silent: no level
            Assert.ThrowsException<InvalidOperationException>(() => set.Buffer(Row(0, 0, 1, 0), new[] { 1 }, 0));  // above the limit
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
        public void EveryChannelProblemIsNamed()
        {
            const string twoSpeakers =
                "    left:\n      channel: 0\n      filter: calibration/speaker-left.json\n" +
                "    right:\n      channel: 1\n      filter: calibration/speaker-right.json\n";
            // speakers.md's old example: speakers on 0 and 1, the penalty sound on 2, where it would play unequalised.
            var unheard = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(Rig(twoSpeakers), "TEST-RIG"));
            StringAssert.Contains(unheard.Message, "penaltySoundChannel 2 has no speaker on it, and the penalty sound is never played unequalised: set it to a speaker's channel (left 0, right 1)");
            var shared = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(Rig(
                twoSpeakers.Replace("channel: 1", "channel: 0"), "  penaltySoundChannel: 0\n"), "TEST-RIG"));
            StringAssert.Contains(shared.Message, "speakers 'left', 'right' share channel 0: each speaker needs its own");
            var outside = Assert.ThrowsException<InvalidOperationException>(() => SpeakerSet.Load(Rig(
                twoSpeakers.Replace("channel: 1", "channel: 32"), "  syncChannel: 40\n  penaltySoundChannel: 0\n"), "TEST-RIG"));
            StringAssert.Contains(outside.Message, "speaker 'right' channel 32 is not one of the audio device's 32 channels (0 to 31, speakerArray.device.channelCount)");
            StringAssert.Contains(outside.Message, "syncChannel 40 is not one of the audio device's 32 channels");
        }

        [TestMethod]
        public void TheSyncCopyIsSyncGainOnTheSyncChannelForTheSoundsLength()
        {
            var set = SpeakerSet.Load(Rig(extra: "  syncChannel: 31\n  syncGain: 0.3\n  penaltySoundChannel: 2\n"), "TEST-RIG");
            var buffer = set.Layout(Row(0, 1, 0, -1), new[] { 1 }, 1, false, true);
            CollectionAssert.AreEqual(new[] { 0.3f, 0.3f, 0.3f, 0.3f }, Values(buffer, 31));
            CollectionAssert.AreEqual(new[] { 0f, 1f, 0f, -1f }, Values(buffer, 1));
            CollectionAssert.AreEqual(new[] { 0f, 0f, 0f, 0f }, Values(set.Layout(Row(0, 1, 0, -1), new[] { 1 }, 1, false), 31));
            Assert.AreEqual(SpeakerSet.DefaultSyncGain, SpeakerSet.Load(Rig(), "TEST-RIG").SyncGain);
        }

        [TestMethod]
        public void ASyncCopyNeedsASyncChannel()
        {
            var set = SpeakerSet.Load(Rig(extra: "  penaltySoundChannel: 2\n"), "TEST-RIG");
            var error = Assert.ThrowsException<InvalidOperationException>(() => set.Layout(Row(1), new[] { 1 }, 1, false, true));
            StringAssert.Contains(error.Message, "no syncChannel");
            StringAssert.Contains(error.Message, "Add it (such as 31).");
        }

        [TestMethod]
        public void ThePenaltySoundNeedsAPenaltyChannel()
        {
            var set = SpeakerSet.Load(Rig(extra: "  syncChannel: 31\n"), "TEST-RIG");
            var ex = Assert.ThrowsException<InvalidOperationException>(() => PenaltySound.Buffer(set, -30));
            StringAssert.Contains(ex.Message, "no penaltySoundChannel");
        }

        [TestMethod]
        public void AWaveAtAnotherRateIsRefused()
        {
            var ex = Assert.ThrowsException<InvalidDataException>(() => PenaltySound.Read(44100));
            StringAssert.Contains(ex.Message, "is 48000 Hz, but the audio device runs at 44100 Hz");
        }
    }
}
