using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OpenRigs.Trials
{
    /// <summary>
    /// The golden task files a conditioned task's tests draw from (its tests\fixtures\handover, the files the experiment launcher
    /// writes at launch), and the check that each combination is drawn at its share: one copy for every task's tests.
    /// </summary>
    public static class GoldenTaskFiles
    {
        /// <summary>
        /// Each *.yml in <paramref name="folder"/> (or in the folder HANDOVER_DIR names, as a mutation check sets it) by
        /// name in ordinal order, its text with its one "rngSeed: null" (the launcher leaves the seed for the workflow
        /// to draw) set to 1. Throws on a folder with no file or a file without exactly one "rngSeed: null".
        /// </summary>
        public static List<KeyValuePair<string, string>> Read(string folder)
        {
            var other = Environment.GetEnvironmentVariable("HANDOVER_DIR");
            if (!string.IsNullOrEmpty(other)) folder = other;
            var files = Directory.GetFiles(folder, "*.yml").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count == 0) throw new InvalidOperationException("No golden task files in " + folder + ".");
            return files.Select(f =>
            {
                var text = File.ReadAllText(f);
                if (text.Split(new[] { "rngSeed: null" }, StringSplitOptions.None).Length != 2)
                {
                    throw new InvalidOperationException(f + " must have rngSeed: null once.");
                }
                return new KeyValuePair<string, string>(Path.GetFileName(f), text.Replace("rngSeed: null", "rngSeed: 1"));
            }).ToList();
        }

        /// <summary>
        /// Each combination whose share of <paramref name="draws"/> new draws (<paramref name="counts"/>, by
        /// trialCombinationId) is more than 4.5 standard errors from its condition's weight over the sum of weights over its
        /// number of combinations, named with <paramref name="name"/> (the file's): none when every share holds.
        /// </summary>
        public static List<string> ShareProblems<TTask>(string name, IList<TrialConditionDraw<TTask>> built, IDictionary<int, int> counts, int draws)
        {
            var problems = new List<string>();
            double total = built.Sum(c => c.Weight);
            foreach (var condition in built)
            {
                foreach (var combination in condition.Combinations)
                {
                    var p = condition.Weight / total / condition.Combinations.Count;
                    int count;
                    var share = (counts.TryGetValue(combination.Id, out count) ? count : 0) / (double)draws;
                    if (Math.Abs(share - p) > 4.5 * Math.Sqrt(p * (1 - p) / draws))
                    {
                        problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}: share {3}, expected {4}", name, condition.Where, combination.Picks, share, p));
                    }
                }
            }
            return problems;
        }
    }
}
