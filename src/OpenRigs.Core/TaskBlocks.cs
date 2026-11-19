using System.Collections.Generic;

namespace OpenRigs.Core
{
    /// <summary>
    /// The rules of the lab's task blocks in open_rigs.core.task (Iti, ResponseWindow, AutoReward), for a task to check its
    /// file with when it loads it: Bonsai reads the file without pydantic, so a hand-edited file reaches the task unchecked.
    /// Each returns what is wrong, empty when nothing is, named by the block's own keys; the task adds where they sit.
    /// QuietWait.LickProblems and WheelProblems are the LickQuietWait and WheelQuietWait blocks'.
    /// </summary>
    public static class TaskBlocks
    {
        /// <summary>The Iti block: itiMinSeconds and itiMaxSeconds each a finite number, 0 or more, and the max at least the min.</summary>
        public static List<string> ItiProblems(double itiMinSeconds, double itiMaxSeconds)
        {
            var problems = new List<string>();
            AtLeastZero(problems, "itiMinSeconds", itiMinSeconds);
            AtLeastZero(problems, "itiMaxSeconds", itiMaxSeconds);
            if (itiMaxSeconds < itiMinSeconds) problems.Add(string.Format("itiMaxSeconds ({0} s) is below itiMinSeconds ({1} s)", itiMaxSeconds, itiMinSeconds));
            return problems;
        }

        /// <summary>The ResponseWindow block: responseWindowSeconds a finite number above 0, or null (no limit; a task that
        /// always closes its window refuses null itself).</summary>
        public static List<string> ResponseWindowProblems(double? responseWindowSeconds)
        {
            var problems = new List<string>();
            if (responseWindowSeconds.HasValue)
            {
                var value = responseWindowSeconds.Value;
                if (!(value > 0) || double.IsInfinity(value)) problems.Add(string.Format("responseWindowSeconds is {0}, not a finite number above 0", value));
            }
            return problems;
        }

        /// <summary>The AutoReward block: autoRewardProbability from 0 to 1.</summary>
        public static List<string> AutoRewardProblems(double autoRewardProbability)
        {
            var problems = new List<string>();
            if (!(autoRewardProbability >= 0 && autoRewardProbability <= 1)) problems.Add(string.Format("autoRewardProbability is {0}, not from 0 to 1", autoRewardProbability));
            return problems;
        }

        /// <summary>A reward block's size (open_rigs.rigs.conditioned_task Reward): sizeUl above 0 and at most maxRewardUl (LoadRewardLine.DefaultMaxRewardUl).</summary>
        public static List<string> RewardSizeProblems(double sizeUl, double maxRewardUl)
        {
            var problems = new List<string>();
            if (!(sizeUl > 0 && sizeUl <= maxRewardUl)) problems.Add(string.Format("sizeUl must be above 0 and at most {0} ul", maxRewardUl));
            return problems;
        }

        /// <summary>The large-reward fields of a reward block (open_rigs.rigs.conditioned_task Reward): largeMultiplier above 1,
        /// largeProbability from 0 (the only off) to 1, and with largeProbability above 0 a large reward at most maxRewardUl, the
        /// most one reward may ask for.</summary>
        public static List<string> LargeRewardProblems(double sizeUl, double largeMultiplier, double largeProbability, double maxRewardUl)
        {
            var problems = new List<string>();
            if (!(largeMultiplier > 1) || double.IsInfinity(largeMultiplier)) problems.Add(string.Format("largeMultiplier is {0}, not above 1", largeMultiplier));
            if (!(largeProbability >= 0 && largeProbability <= 1)) problems.Add("largeProbability must be in [0, 1]");
            if (largeProbability > 0 && sizeUl * largeMultiplier > maxRewardUl + 1e-9)
            {
                problems.Add(string.Format("sizeUl ({0} ul) x largeMultiplier ({1}) is {2} ul, more than {3} ul, the most one reward may ask for", sizeUl, largeMultiplier, sizeUl * largeMultiplier, maxRewardUl));
            }
            return problems;
        }

        /// <summary>The Optogenetics block's laserOnProbability, from 0 to 1.</summary>
        public static List<string> LaserOnProbabilityProblems(double laserOnProbability)
        {
            var problems = new List<string>();
            if (!(laserOnProbability >= 0 && laserOnProbability <= 1)) problems.Add("laserOnProbability must be in [0, 1]");
            return problems;
        }

        /// <summary>A stimulus: the contrasts each finite, 0 or more, at least one; the sound's amplitude finite, 0 or more.</summary>
        public static List<string> StimulusProblems(IList<double> contrasts, double amplitude)
        {
            var problems = new List<string>();
            var any = false;
            var bad = false;
            foreach (var c in contrasts ?? new List<double>())
            {
                any = true;
                if (!(c >= 0) || double.IsInfinity(c)) bad = true;
            }
            if (!any || bad) problems.Add("contrasts must each be a finite number, 0 or more, at least one");
            if (!(amplitude >= 0) || double.IsInfinity(amplitude)) problems.Add("auditoryStimulus.amplitude must be a finite number, 0 or more");
            return problems;
        }

        static void AtLeastZero(List<string> problems, string name, double value)
        {
            if (!(value >= 0) || double.IsInfinity(value)) problems.Add(string.Format("{0} is {1}, not a finite number, 0 or more", name, value));
        }
    }
}
