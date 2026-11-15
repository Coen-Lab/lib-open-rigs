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
    [Description("The mixer's buffer for a sound (one row) at LevelDbfs: on each of Channels (empty: every rig.yml speaker), equalised for that speaker with its filter; every other channel silent. Stops the workflow when a peak reaches -1 dBFS after equalisation. Input: (the speakers, a sound) or (the speakers, a list of sounds), each sound laid out the same way, each at the level.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SpeakerBuffer
    {
        /// <summary>The channels to play on; empty for every speaker in rig.yml.</summary>
        [Description("The mixer channels to play on. Empty: every speaker in rig.yml.")]
        public int[] Channels { get; set; }

        /// <summary>The sound's level in dBFS RMS (SoundLevel): map it from the task file.</summary>
        [Description("The sound's level in dBFS RMS, the RMS of the sound over its whole length as given (before equalisation): map it from the task file's levelDbfs. The lab's stimulus default, -30, until mapped.")]
        public double LevelDbfs { get; set; } = SoundLevel.StimulusDbfs;

        /// <summary>Whether each channel's copy is equalised for its speaker.</summary>
        [Description("Whether each channel's copy is equalised for its speaker with its filter (a stimulus); false plays the sound as it is on each (a background noise).")]
        public bool Equalise { get; set; } = true;

        /// <summary>Gets or sets whether each buffer carries the sound's sync copy on rig.yml's syncChannel.</summary>
        [Description("Whether each buffer also carries the sound's sync copy: rig.yml's syncGain on its syncChannel for the sound's length, for alignment with acquisition (a stimulus). Stops the workflow on a rig.yml with no syncChannel.")]
        public bool SyncCopy { get; set; }

        /// <summary>One sound's buffer.</summary>
        public IObservable<Mat> Process(IObservable<Tuple<SpeakerSet, Mat>> source)
        {
            return source.Select(input => input.Item1.Buffer(input.Item2, Channels, LevelDbfs, Equalise, SyncCopy));
        }

        /// <summary>Each sound's buffer, in order.</summary>
        public IObservable<IList<Mat>> Process(IObservable<Tuple<SpeakerSet, IList<Mat>>> source)
        {
            return source.Select(input => (IList<Mat>)input.Item2.Select((sound, i) => input.Item1.Buffer(
                sound, Channels, LevelDbfs, Equalise, SyncCopy, "Sound " + i + " of " + input.Item2.Count + " at " +
                LevelDbfs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " dBFS RMS")).ToList());
        }
    }

    /// <summary>The lab's penalty sound as the mixer's buffer (<see cref="PenaltySound.Buffer"/>).</summary>
    [Combinator]
    [Description("The lab's penalty sound (OpenRigs.Audio's penalty_tone.wav, the same in every task) as the mixer's buffer: on rig.yml's penaltySoundChannel alone, equalised for that speaker, at LevelDbfs (the task's penalty.sound.levelDbfs). Stops the workflow when its peak reaches -1 dBFS after equalisation. Input: the speakers.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PenaltySoundBuffer
    {
        /// <summary>The penalty sound's level in dBFS RMS: map it from the task file.</summary>
        [Description("The penalty sound's level in dBFS RMS (the task's penalty.sound.levelDbfs). The lab's default, -30, until mapped.")]
        public double LevelDbfs { get; set; } = SoundLevel.PenaltyDbfs;

        /// <summary>The buffer for each input.</summary>
        public IObservable<Mat> Process(IObservable<SpeakerSet> source)
        {
            return source.Select(speakers => PenaltySound.Buffer(speakers, LevelDbfs));
        }
    }

    /// <summary>Stops the workflow on a sound sent before the mixer has started, which the mixer would not play.</summary>
    [Combinator]
    [Description("Passes on each sound once the mixer has started, and stops the workflow, naming Sound and the mixer's boot delay, on a sound sent before: the mixer would not play it while the trial log records it as sent. Input: (the sounds, the mixer). SpeakerMixer runs StimulusSounds and PenaltySounds through it.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class CheckMixerStarted
    {
        /// <summary>The sound's subject, named in the error.</summary>
        [Description("The sound's subject, named in the error, such as StimulusSounds.")]
        public string Sound { get; set; }

        /// <summary>Passes on each sound sent after the mixer's first value; stops on one sent before.</summary>
        public IObservable<TSound> Process<TSound, TMixer>(IObservable<TSound> source, IObservable<TMixer> mixer)
        {
            return Observable.Create<TSound>(observer =>
            {
                var started = false;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var gate = new object();
                var mixerSubscription = mixer.Take(1).Subscribe(_ => { lock (gate) started = true; }, observer.OnError);
                var soundSubscription = source.Subscribe(sound =>
                {
                    bool ready;
                    lock (gate) ready = started;
                    if (ready) observer.OnNext(sound);
                    else observer.OnError(new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "A sound was sent on {0} {1:0.0} s after Start, before the speaker mixer started (3 s after Start, or rig.yml's " +
                        "speakerArray.device.bootDelay if longer), so it would not play while the trial log records it as sent. " +
                        "Press Go no sooner than that.", Sound, clock.Elapsed.TotalSeconds)));
                }, observer.OnError, observer.OnCompleted);
                return new System.Reactive.Disposables.CompositeDisposable(mixerSubscription, soundSubscription);
            });
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
