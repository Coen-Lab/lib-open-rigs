using System;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Rigs;

namespace OpenRigs.Devices.Tests
{
    /// <summary>The lab's rig template read as every lab workflow reads rig.yml: the generated DeserializeFromYaml,
    /// whose objects refuse a key the C# lacks, so a template a workflow would stop on at Start fails here.</summary>
    [TestClass]
    public class LabRigTests
    {
        internal static LabRig Deserialize(string text)
        {
            var process = typeof(DeserializeFromYaml).GetMethod("Process", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(LabRig));
            return ((IObservable<LabRig>)process.Invoke(null, new object[] { Observable.Return(text) })).Wait();
        }

        static string Template()
        {
            var folder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (!File.Exists(Path.Combine(folder.FullName, "src", "open_rigs", "rigs", "rig-template.yml"))) folder = folder.Parent;
            return File.ReadAllText(Path.Combine(folder.FullName, "src", "open_rigs", "rigs", "rig-template.yml"));
        }

        [TestMethod]
        public void TheTemplateDeserializes()
        {
            var rig = Deserialize(Template());
            Assert.AreEqual(3, rig.Camera.Count);
            Assert.AreEqual(2, rig.SpeakerArray.PenaltySoundChannel);
            Assert.AreEqual("../shared/sounds/background_noise.wav", rig.SpeakerArray.BackgroundNoise);
            Assert.IsNotNull(rig.Zapit);
        }

        [TestMethod]
        public void EachVideoEncoderCodecIsReadByItsFFmpegName()
        {
            // A C# enum here took only its C# names (H264Nvenc), so rig.yml's h264_nvenc stopped the workflow at Start.
            foreach (var codec in new[] { "libx264", "h264_nvenc", "h264_qsv" })
            {
                Assert.AreEqual(codec, Deserialize(Template().Replace("codec: h264_nvenc", "codec: " + codec)).VideoEncoder.Codec);
            }
        }

        // A third valve on a port line, and a valve on a second board: each valve's name and its line's calibrated output
        // read by the generated C#, as at Start (DO0 and DOPort0 to 2 are outputs only when given).
        [TestMethod]
        public void ValvesOnAnyPulsedLineOfAnyBoardDeserialize()
        {
            var rig = Deserialize(Template()
                .Replace("      DO3: { discriminatorType: FixedPulse, pulseWidthMs: 10000 }",
                         "      DO3: { discriminatorType: FixedPulse, pulseWidthMs: 10000 }\n        DOPort0: { discriminatorType: CalibratedPulse, artefact: calibration/RewardValve-Centre.json }")
                .Replace("      DO2: { device: RewardValve, name: right }",
                         "      DO2: { device: RewardValve, name: right }\n      DOPort0: { device: RewardValve, name: centre }"));
            var main = rig.BehaviorBoard["main"];
            Assert.AreEqual("centre", main.Wiring.DOPort0.Name);
            Assert.AreEqual("left", main.Wiring.DO1.Name);
            Assert.IsInstanceOfType(main.PulseController.Outputs.DOPort0, typeof(CalibratedPulse));
            Assert.IsNull(main.PulseController.Outputs.DO0);
            Assert.AreEqual(DigitalOutputs.DOPort0, WiredOutputs.Find(rig, "BehaviorBoard-Main", "RewardValve") & DigitalOutputs.DOPort0);
        }

        [TestMethod]
        public void AKeyTheCSharpLacksIsRefused()
        {
            Assert.ThrowsException<YamlDotNet.Core.YamlException>(() => Deserialize(Template() + "\nrewardValves: { left: DO1, right: DO2 }\n"));
        }
    }
}
