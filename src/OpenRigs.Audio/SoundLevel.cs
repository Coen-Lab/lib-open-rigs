using OpenCV.Net;
using System;
using System.Globalization;

namespace OpenRigs.Audio
{
    /// <summary>
    /// Sound levels in dBFS RMS, the lab's measure of how loud a sound plays: the RMS of the sound over its whole length,
    /// as the task gives it (before equalisation), against full scale 1, so a full-scale square wave is 0 dBFS and a
    /// full-scale sine -3 dBFS. Every sound a task plays is given its level, never a gain, and every buffer's peak stays
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
        public const double RewardToneDbfs = -36;

        /// <summary>The lab's default level of the background noise, in dBFS RMS.</summary>
        public const double BackgroundDbfs = -50;

        /// <summary>The highest peak any buffer may reach after equalisation, in dBFS: headroom against clipping.</summary>
        public const double PeakLimitDbfs = -1;

        /// <summary>A sound's level: the RMS of every sample, in dBFS (negative infinity for silence).</summary>
        public static double RmsDbfs(Mat sound)
        {
            double count = sound.Rows * sound.Cols;
            return 20 * Math.Log10(CV.Norm(sound, null, NormTypes.L2) / Math.Sqrt(count));
        }

        /// <summary>The gain that plays a sound at a level in dBFS RMS. Stops on a silent sound or a level that is not a number at most the peak limit.</summary>
        public static double Gain(Mat sound, double levelDbfs)
        {
            if (double.IsNaN(levelDbfs) || double.IsInfinity(levelDbfs) || levelDbfs > PeakLimitDbfs)
            {
                throw new InvalidOperationException("A sound's levelDbfs must be a number of dBFS RMS at most " + Format(PeakLimitDbfs) + "; it is " + Format(levelDbfs) + ".");
            }
            var rms = RmsDbfs(sound);
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
