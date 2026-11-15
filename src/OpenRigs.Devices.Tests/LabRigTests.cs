using System;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
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

        [TestMethod]
        public void AKeyTheCSharpLacksIsRefused()
        {
            Assert.ThrowsException<YamlDotNet.Core.YamlException>(() => Deserialize(Template() + "\nrewardValves: { left: DO1, right: DO2 }\n"));
        }
    }
}
