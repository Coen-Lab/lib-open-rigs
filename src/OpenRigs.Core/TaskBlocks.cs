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

        static void AtLeastZero(List<string> problems, string name, double value)
        {
            if (!(value >= 0) || double.IsInfinity(value)) problems.Add(string.Format("{0} is {1}, not a finite number, 0 or more", name, value));
        }
    }
}
