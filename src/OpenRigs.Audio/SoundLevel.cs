using OpenCV.Net;
using System;
using System.Globalization;

namespace OpenRigs.Audio
{
    /// <summary>
    /// Sound levels in dBFS RMS, the lab's measure of how loud a sound plays: the RMS of the sound over its samples outside
    /// silent stretches (a silent stretch a run of exactly-zero samples at least <see cref="SilentStretchSeconds"/> long, such as
    /// a pulse train's gaps or zero padding; lone zeros and ramps count), as the task gives it (before equalisation), against
    /// full scale 1, so a full-scale square wave is 0 dBFS and a full-scale sine -3 dBFS. Every sound a task plays is given its
    /// level, never a gain, and every buffer's peak stays
    /// under <see cref="PeakLimitDbfs"/> after equalisation. Sounds overlapping on a speaker share its headroom: each
    /// buffer's peak is checked, not their mix. The lab's defaults are here and in open_rigs.core.task.
    /// </summary>
    public static class SoundLevel
    {
        /// <summary>The lab's default level of a stimulus, in dBFS RMS.</summary>
        public const double StimulusDbfs = -30;

        /// <summary>The lab's default level of the penalty sound, in dBFS RMS.</summary>
        public const double PenaltyDbfs = -30;

        /// <summary>The lab's default level of a reward tone, in dBFS RMS.</summary>
        public const double RewardToneDbfs = -35;

        /// <summary>The background noise's level in every task, in dBFS RMS: a lab constant, no task setting.</summary>
        public const double BackgroundDbfs = -60;

        /// <summary>The shortest run of exactly-zero samples that is a silent stretch, left out of a level, in seconds.</summary>
        public const double SilentStretchSeconds = 0.001;

        /// <summary>The highest peak any buffer may reach after equalisation, in dBFS: headroom against clipping.</summary>
        public const double PeakLimitDbfs = -1;

        /// <summary>A sound's level at its sample rate: the RMS of its samples outside silent stretches, in dBFS (negative
        /// infinity for silence). Every row is one channel; a run of zeros is silent only where every channel is zero.</summary>
        public static double RmsDbfs(Mat sound, int sampleRate)
        {
            if (!(sampleRate > 0)) throw new ArgumentException("A sound's level needs its sample rate, above 0; it is " + sampleRate + ".");
            var shortest = (int)Math.Ceiling(SilentStretchSeconds * sampleRate - 1e-9);
            var samples = new float[sound.Rows * sound.Cols];
            using (var floats = new Mat(sound.Rows, sound.Cols, Depth.F32, 1))
            {
                CV.Convert(sound, floats);
                System.Runtime.InteropServices.Marshal.Copy(floats.Data, samples, 0, samples.Length);
            }
            double sum = 0;
            long counted = 0, zeros = 0;
            for (int column = 0; column <= sound.Cols; column++)
            {
                double square = 0;
                if (column < sound.Cols) for (int row = 0; row < sound.Rows; row++) square += (double)samples[row * sound.Cols + column] * samples[row * sound.Cols + column];
                if (column < sound.Cols && square == 0) { zeros++; continue; }
                if (zeros < shortest) counted += zeros * sound.Rows;   // lone zeros count, a silent stretch does not
                zeros = 0;
                if (column == sound.Cols) break;
                sum += square;
                counted += sound.Rows;
            }
            return counted == 0 ? double.NegativeInfinity : 10 * Math.Log10(sum / counted);
        }

        /// <summary>The gain that plays a sound (at its sample rate) at a level in dBFS RMS. Stops on a silent sound or a level that is not a number at most the peak limit.</summary>
        public static double Gain(Mat sound, double levelDbfs, int sampleRate)
        {
            if (double.IsNaN(levelDbfs) || double.IsInfinity(levelDbfs) || levelDbfs > PeakLimitDbfs)
            {
                throw new InvalidOperationException("A sound's levelDbfs must be a number of dBFS RMS at most " + Format(PeakLimitDbfs) + "; it is " + Format(levelDbfs) + ".");
            }
            var rms = RmsDbfs(sound, sampleRate);
            if (double.IsInfinity(rms)) throw new InvalidOperationException("A sound to play at " + Format(levelDbfs) + " dBFS RMS is silent, so it has no level to set.");
            return Math.Pow(10, (levelDbfs - rms) / 20);
        }

        /// <summary>
        /// Stops unless every sample of a buffer (every channel) stays under <see cref="PeakLimitDbfs"/>, naming the sound,
        /// its peak and its channel, and how far to lower its level.
        /// </summary>
        public static void CheckPeak(Mat buffer, string what)
        {
            double limit = Math.Pow(10, PeakLimitDbfs / 20);
            for (int channel = 0; channel < buffer.Rows; channel++)
            {
                double min, max;
                Point minLoc, maxLoc;
                using (var row = buffer.GetRow(channel)) CV.MinMaxLoc(row, out min, out max, out minLoc, out maxLoc);
                double peak = Math.Max(Math.Abs(min), Math.Abs(max));
                if (peak >= limit)
                {
                    double peakDbfs = 20 * Math.Log10(peak);
                    throw new InvalidOperationException(what + " peaks at " + Format(peakDbfs) + " dBFS on channel " + channel +
                        " after equalisation, at or above the lab's " + Format(PeakLimitDbfs) + " dBFS limit: lower its levelDbfs by more than " +
                        Format(peakDbfs - PeakLimitDbfs) + " dB.");
                }
            }
        }

        static string Format(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
