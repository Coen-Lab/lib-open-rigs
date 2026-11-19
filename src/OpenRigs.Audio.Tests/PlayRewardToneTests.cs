using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Security;
using Bonsai;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;
using OpenRigs.Tests;

namespace OpenRigs.Audio.Tests
{
    /// <summary>A task's trial rules as PlayRewardTone reads them: the highest reward tone level any trial draws (MaxRewardToneLevelDbfs).</summary>
    public class ToneRules
    {
        public double MaxRewardToneLevelDbfs { get; set; }
    }

    /// <summary>The rules at Go, the highest reward tone level at MaxRewardToneLevelDbfs.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class ToneRulesSource
    {
        public double MaxRewardToneLevelDbfs { get; set; }

        public IObservable<ToneRules> Process()
        {
            return Observable.Defer(() => Observable.Return(new ToneRules { MaxRewardToneLevelDbfs = MaxRewardToneLevelDbfs }));
        }
    }

    /// <summary>Three rewarded trials' reward tone levels, 0.2 s apart from 0.5 s: -20, mute (NaN) and -26 dBFS.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class ToneTrialLevels
    {
        public IObservable<double> Process()
        {
            var levels = new[] { -20, double.NaN, -26 };
            return Observable.Timer(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.2)).Take(levels.Length).Select(i => levels[i]);
        }
    }

    /// <summary>The speakers of RigFile, as measured on TEST-RIG.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class ToneSpeakers
    {
        public string RigFile { get; set; }

        public IObservable<SpeakerSet> Process()
        {
            return Observable.Defer(() => Observable.Return(SpeakerSet.Load(RigFile, "TEST-RIG")));
        }
    }

    /// <summary>Each buffer as "tone rows x cols, channel 0 at L dBFS RMS".</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ToneDescribe
    {
        public IObservable<string> Process(IObservable<Mat> source)
        {
            return source.Select(buffer =>
            {
                var values = new float[buffer.Cols];
                using (var row = buffer.GetRow(0))
                using (var copy = new Mat(1, buffer.Cols, Depth.F32, 1))
                {
                    CV.Copy(row, copy);
                    Marshal.Copy(copy.Data, values, 0, values.Length);
                }
                var rms = Math.Sqrt(values.Average(v => (double)v * v));
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "tone {0}x{1}, channel 0 at {2:0} dBFS RMS", buffer.Rows, buffer.Cols, 20 * Math.Log10(rms));
            });
        }
    }

    /// <summary>PlayRewardTone, built and run in this process (Pieces\PlayRewardTone.bonsai).</summary>
    [TestClass]
    public class PlayRewardToneTests
    {
        static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(PlayRewardToneTests).Assembly.Location), "Pieces");

        // The tone is laid out on both speakers of a four-channel mixer at MaxRewardToneLevelDbfs, -20 dBFS; each rewarded trial
        // plays it at its own level, a mute trial not at all.
        [TestMethod]
        public void EachInputPlaysTheToneOnEverySpeakerAtItsTrialsLevel()
        {
            var folder = Path.Combine(Path.GetTempPath(), "tone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "calibration"));
            foreach (var name in new[] { "Left", "Right" })
            {
                File.WriteAllText(Path.Combine(folder, "calibration", "Speaker-" + name + ".json"),
                    "{\"calibratedAt\": \"2026-11-14T00:00:00Z\", \"calibratedBy\": \"test\", \"machineName\": \"TEST-RIG\", \"sampleRate\": 48000, \"taps\": [0.25, 0.5, 0.25]}");
            }
            var rig = Path.Combine(folder, "rig.yml");
            File.WriteAllText(rig, "speakerArray:\n  device:\n    sampleRate: 48000\n    channelCount: 4\n  speakers:\n" +
                "    left:\n      channel: 0\n      filter: calibration/Speaker-Left.json\n    right:\n      channel: 1\n      filter: calibration/Speaker-Right.json\n");
            var tone = Path.Combine(folder, "reward_tone.bin");
            using (var writer = new BinaryWriter(File.Create(tone)))
            {
                foreach (var i in Enumerable.Range(0, 2400)) writer.Write((float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0)));
            }
            var xml = File.ReadAllText(Path.Combine(Folder, "PlayRewardTone.bonsai"))
                .Replace("RIG_FILE", SecurityElement.Escape(rig)).Replace("TONE_FILE", SecurityElement.Escape(tone));
            var result = InProcessWorkflow.Run(xml, 3, count: 2);
            Directory.Delete(folder, true);
            Assert.IsNull(result.Item2, InProcessWorkflow.Innermost(result.Item2));
            CollectionAssert.AreEqual(new[] { "tone 4x2400, channel 0 at -20 dBFS RMS", "tone 4x2400, channel 0 at -26 dBFS RMS" }, result.Item1);
        }
    }
}
