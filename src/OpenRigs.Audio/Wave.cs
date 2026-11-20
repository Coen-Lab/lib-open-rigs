using OpenCV.Net;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenRigs.Audio
{
    /// <summary>Mono WAV files, 16-bit PCM or 32-bit float, the form a task's sounds are kept in.</summary>
    public static class Wave
    {
        /// <summary>A WAV file's sample rate, from its fmt chunk.</summary>
        public static int SampleRate(string path)
        {
            using (var stream = File.OpenRead(path)) return Read(stream, path, 0, false).Item1;
        }

        /// <summary>
        /// A mono WAV as one row of floats: 16-bit PCM divided by 32768 (digital silence
        /// reads exactly 0, so SoundLevel can find silent stretches), 32-bit float as stored; stops unless it is at the audio device's rate, as nothing converts a rate.
        /// </summary>
        public static Mat Read(string path, int sampleRate)
        {
            using (var stream = File.OpenRead(path)) return Read(stream, path, sampleRate, true).Item2;
        }

        /// <summary>The same, from a stream (an embedded file), named for its errors.</summary>
        public static Mat Read(Stream stream, string name, int sampleRate)
        {
            return Read(stream, name, sampleRate, true).Item2;
        }

        static Tuple<int, Mat> Read(Stream stream, string name, int sampleRate, bool samples)
        {
            var reader = new BinaryReader(stream);
            // A chunk id is four bytes, read as bytes: ReadChars decodes UTF-8 and can take more than four.
            string Id() => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (Id() != "RIFF") throw new InvalidDataException("not a WAV file: " + name);
            reader.ReadInt32();
            if (Id() != "WAVE") throw new InvalidDataException("not a WAV file: " + name);
            short format = 0, channels = 0, bits = 0;
            int rate = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string id = Id();
                int size = reader.ReadInt32();
                if (id == "fmt ")
                {
                    if (size < 16) throw new InvalidDataException("its fmt chunk is " + size + " bytes, short of PCM's 16 (no bit depth): " + name);
                    format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                    reader.BaseStream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
                    if (!samples) return Tuple.Create(rate, (Mat)null);
                }
                else if (id == "data")
                {
                    bool pcm = format == 1 && bits == 16, ieee = format == 3 && bits == 32;
                    if (channels != 1 || !(pcm || ieee))
                    {
                        throw new InvalidDataException("must be mono 16-bit PCM or 32-bit float (it is format " + format + ", " + channels + " channel(s), " + bits + " bit): " + name);
                    }
                    if (rate != sampleRate)
                    {
                        throw new InvalidDataException("is " + rate + " Hz, but the audio device runs at " + sampleRate + " Hz: resample it (duration and level kept): " + name);
                    }
                    int count = size / (bits / 8);
                    var values = new float[count];
                    for (int i = 0; i < count; i++) values[i] = ieee ? reader.ReadSingle() : reader.ReadInt16() / 32768f;
                    var sound = new Mat(1, count, Depth.F32, 1);
                    Marshal.Copy(values, 0, sound.Data, count);
                    return Tuple.Create(rate, sound);
                }
                else
                {
                    reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
            throw new InvalidDataException((samples ? "has no audio data: " : "has no fmt chunk: ") + name);
        }
    }

    /// <summary>A lab-wide sound rig.yml's speakerArray names, read whole as one row.</summary>
    static class RigSound
    {
        /// <summary>The sound, one row as stored; throws, naming the file, when rig.yml names none, it is missing or at another rate.</summary>
        public static Mat Read(string path, string key, string what, string file, int sampleRate)
        {
            if (path == null)
            {
                throw new InvalidOperationException("rig.yml's speakerArray has no " + key + ", " + what + ". " +
                    "Add it: " + key + ": ../shared/sounds/" + file + " (the rig's upkeep copies it from the server's config\\shared).");
            }
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("The sound rig.yml's speakerArray." + key + " names is missing: " + path +
                    ". The rig's upkeep copies it from the server's config\\shared; run it, or copy the file there.");
            }
            return Wave.Read(path, sampleRate);
        }
    }

    /// <summary>The lab's background noise, the same file in every task, named by rig.yml's speakerArray.backgroundNoise.</summary>
    public static class BackgroundNoiseSound
    {
        /// <summary>The noise, one row as stored; throws, naming the file, when rig.yml names none, it is missing or at another rate.</summary>
        public static Mat Read(SpeakerSet speakers)
        {
            return RigSound.Read(speakers.BackgroundNoiseFile, "backgroundNoise", "the lab's background noise every task plays", "background_noise.wav", speakers.SampleRate);
        }
    }

    /// <summary>The lab's penalty sound, the same file in every task, named by rig.yml's speakerArray.penaltySound.</summary>
    public static class PenaltySound
    {
        /// <summary>The penalty sound, one row as stored; throws, naming the file, when rig.yml names none, it is missing or at another rate.</summary>
        public static Mat Read(SpeakerSet speakers)
        {
            return RigSound.Read(speakers.PenaltySoundFile, "penaltySound", "the lab's penalty sound every task plays", "penalty_tone.wav", speakers.SampleRate);
        }

        /// <summary>The penalty sound's length in seconds, as stored; NaN when rig.yml names none. Throws, naming the file, when it is missing or at another rate.</summary>
        public static double Seconds(SpeakerSet speakers)
        {
            if (speakers.PenaltySoundFile == null) return double.NaN;
            using (var sound = Read(speakers)) return sound.Cols / (double)speakers.SampleRate;
        }

        /// <summary>The mixer's buffer for the penalty sound: on rig.yml's penaltySoundChannel alone, equalised for that speaker, at the task's level in dBFS RMS.</summary>
        public static Mat Buffer(SpeakerSet speakers, double levelDbfs)
        {
            if (!speakers.PenaltySoundChannel.HasValue)
            {
                throw new InvalidOperationException("rig.yml's speakerArray has no penaltySoundChannel, the speaker the penalty sound plays on.");
            }
            using (var sound = Read(speakers))
            {
                return speakers.Buffer(sound, new[] { speakers.PenaltySoundChannel.Value }, levelDbfs, what: "The penalty sound at " +
                    levelDbfs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " dBFS RMS");
            }
        }
    }
}
