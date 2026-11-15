using Bonsai;
using OpenCV.Net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Audio
{
    /// <summary>Loads rig.yml's speakers and their filters (<see cref="SpeakerSet.Load"/>).</summary>
    [Combinator]
    [Description("Loads rig.yml's speakerArray for each input (the path of this machine's rig.yml): each speaker's channel and its filter (relative to rig.yml's folder), checked. Stops the workflow, naming every problem, on a speaker without a filter or a filter missing, without taps, not symmetric, for another rate or measured on another machine.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class LoadSpeakers
    {
        /// <summary>The machine the filters must have been measured on; empty for this one.</summary>
        [Description("The machine the filters must have been measured on. Empty: this machine.")]
        public string MachineName { get; set; }

        /// <summary>The speakers of each rig.yml.</summary>
        public IObservable<SpeakerSet> Process(IObservable<string> source)
        {
            return source.Select(rigFile => SpeakerSet.Load(rigFile, MachineName));
        }
    }

    /// <summary>Lays a sound out as the mixer's buffer on chosen speakers, each equalised (<see cref="SpeakerSet.Buffer"/>).</summary>
    [Combinator]
    [Description("The mixer's buffer for a sound (one row): on each of Channels (empty: every rig.yml speaker), equalised for that speaker with its filter, times Gain; every other channel silent. Input: (the speakers, a sound) or (the speakers, a list of sounds), each sound laid out the same way.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SpeakerBuffer
    {
        /// <summary>The channels to play on; empty for every speaker in rig.yml.</summary>
        [Description("The mixer channels to play on. Empty: every speaker in rig.yml.")]
        public int[] Channels { get; set; }

        /// <summary>The gain on every channel.</summary>
        [Description("The gain on every channel, 1 for the sound as it is.")]
        public double Gain { get; set; } = 1;

        /// <summary>Whether each channel's copy is equalised for its speaker.</summary>
        [Description("Whether each channel's copy is equalised for its speaker with its filter (a stimulus); false plays the sound as it is on each (a background noise).")]
        public bool Equalise { get; set; } = true;

        /// <summary>One sound's buffer.</summary>
        public IObservable<Mat> Process(IObservable<Tuple<SpeakerSet, Mat>> source)
        {
            return source.Select(input => input.Item1.Buffer(input.Item2, Channels, Gain, Equalise));
        }

        /// <summary>Each sound's buffer, in order.</summary>
        public IObservable<IList<Mat>> Process(IObservable<Tuple<SpeakerSet, IList<Mat>>> source)
        {
            return source.Select(input => (IList<Mat>)input.Item2.Select(sound => input.Item1.Buffer(sound, Channels, Gain, Equalise)).ToList());
        }
    }

    /// <summary>The lab's penalty sound as the mixer's buffer (<see cref="PenaltySound.Buffer"/>).</summary>
    [Combinator]
    [Description("The lab's penalty sound (OpenRigs.Audio's penalty_tone.wav, the same in every task) as the mixer's buffer: on rig.yml's penaltySoundChannel alone, equalised for that speaker, at PenaltySound.Level times Gain (the task's penalty.sound.gain). Input: the speakers.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PenaltySoundBuffer
    {
        /// <summary>The task's gain on the penalty sound.</summary>
        [Description("The task's gain on the penalty sound (penalty.sound.gain), 1 for the lab's level.")]
        public double Gain { get; set; } = 1;

        /// <summary>The buffer for each input.</summary>
        public IObservable<Mat> Process(IObservable<SpeakerSet> source)
        {
            return source.Select(speakers => PenaltySound.Buffer(speakers, Gain));
        }
    }

    /// <summary>Stops the workflow on a WAV file not at the audio device's rate.</summary>
    [Combinator]
    [Description("Stops the workflow if a WAV file the mixer plays is not at the audio device's rate: nothing converts it, so it would play at the wrong pitch and speed. Input: the device's sample rate; passed on.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CheckWaveRate
    {
        /// <summary>The WAV file, relative to the workflow's folder.</summary>
        [Description("The WAV file, relative to the workflow's folder (src).")]
        public string FileName { get; set; }

        /// <summary>Checks the file against each rate.</summary>
        public IObservable<int> Process(IObservable<int> source)
        {
            return source.Select(rate =>
            {
                Check(FileName, rate);
                return rate;
            });
        }

        /// <summary>Throws unless the file is at the rate.</summary>
        public static void Check(string fileName, int deviceRate)
        {
            var path = System.IO.Path.GetFullPath(fileName);
            var rate = Wave.SampleRate(path);
            if (rate != deviceRate)
            {
                throw new InvalidOperationException(path + " is " + rate + " Hz, but the audio device runs at " + deviceRate + " Hz: resample it to " + deviceRate + " Hz (duration and level kept).");
            }
        }
    }
}
