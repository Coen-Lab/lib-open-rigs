using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>
    /// Reads a Zapit conditions file (YAML) without a YAML library. A condition is a top-level
    /// <c>stimLocationsNN:</c> key; Zapit numbers conditions by their position, so the numbers must be
    /// exactly 1 to N.
    /// </summary>
    public static class ZapitConditions
    {
        /// <summary>The shortest ramp-down allowed, in ms: a shorter one lets inhibition end abruptly (rebound excitation).</summary>
        public const double MinimumRampDownMs = 100;

        /// <summary>The longest ramp-down allowed, in ms: the masking light goes out 1 s after the outcome, after the ramp and Zapit's stop.</summary>
        public const double MaximumRampDownMs = 500;

        static readonly Regex ConditionKey = new Regex(@"^stimLocations(\d+)\s*:", RegexOptions.CultureInvariant);
        static readonly Regex TopLevelKey = new Regex(@"^([A-Za-z_]\w*)\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        static readonly Regex RampDown = new Regex(@"offRampDownDuration_ms\s*:\s*([0-9.eE+-]+)", RegexOptions.CultureInvariant);
        static readonly Regex ModulationFrequency = new Regex(@"stimModulationFreqHz\s*:\s*([0-9.eE+-]+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The number of conditions in the file, after checking they are numbered 1 to N and each
        /// ramps down over <see cref="MinimumRampDownMs"/> to <see cref="MaximumRampDownMs"/> and over
        /// more than one stimulus cycle. Throws, naming the file and every problem, otherwise.
        /// </summary>
        public static int Check(string path)
        {
            var text = Read(path);
            var numbers = new List<int>();
            foreach (var line in text.Split('\n'))
            {
                var match = ConditionKey.Match(line);
                if (match.Success) numbers.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            if (numbers.Count == 0)
            {
                throw new InvalidOperationException(string.Format(
                    "The Zapit conditions file '{0}' has no conditions: no line starts with a stimLocationsNN key.", path));
            }
            numbers.Sort();
            for (var i = 0; i < numbers.Count; i++)
            {
                if (numbers[i] != i + 1)
                {
                    throw new InvalidOperationException(string.Format(
                        "The Zapit conditions file '{0}' numbers its conditions {1}. They must be exactly 1 to {2}, " +
                        "because Zapit numbers conditions by their position in the file.",
                        path, string.Join(", ", numbers), numbers.Count));
                }
            }
            CheckRampDowns(path, text);
            return numbers.Count;
        }

        // MATLAB Zapit ramps down in whole cycles, and a ramp of one cycle or less writes zeros without stopping
        // the DAQ, so a hardware-triggered rig's next stimulus would start when armed, not at its trigger.
        static void CheckRampDowns(string path, string text)
        {
            var blocks = new Dictionary<string, string>();
            var keys = TopLevelKey.Matches(text);
            for (var i = 0; i < keys.Count; i++)
            {
                var end = i + 1 < keys.Count ? keys[i + 1].Index : text.Length;
                blocks[keys[i].Groups[1].Value] = text.Substring(keys[i].Index, end - keys[i].Index);
            }

            string block;
            var frequency = Match.Empty;
            if (blocks.TryGetValue("stimModulationFreqHz", out block)) frequency = ModulationFrequency.Match(block);
            if (!frequency.Success && blocks.TryGetValue("stimLocations01", out block)) frequency = ModulationFrequency.Match(block);
            var hz = frequency.Success ? double.Parse(frequency.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
            var cycleMs = frequency.Success ? 1000.0 / hz : 0;

            var problems = new List<string>();
            foreach (var pair in blocks)
            {
                if (!ConditionKey.IsMatch(pair.Key + ":")) continue;
                var ramp = RampDown.Match(pair.Value);
                if (!ramp.Success)
                {
                    problems.Add(pair.Key + " has no offRampDownDuration_ms");
                    continue;
                }
                var rampMs = double.Parse(ramp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (rampMs < MinimumRampDownMs)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} ramps down over {1} ms, less than {2} ms", pair.Key, rampMs, MinimumRampDownMs));
                else if (rampMs > MaximumRampDownMs)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} ramps down over {1} ms, more than {2} ms", pair.Key, rampMs, MaximumRampDownMs));
                else if (rampMs <= cycleMs)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} ramps down over {1} ms, not longer than one {2} Hz cycle ({3} ms)", pair.Key, rampMs, hz, cycleMs));
            }
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The Zapit conditions file '{0}': {1}. Each condition needs offRampDownDuration_ms of {2} to {3} ms and longer than one stimulus cycle.",
                    path, string.Join("; ", problems), MinimumRampDownMs, MaximumRampDownMs));
            }
        }

        static string Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("No Zapit conditions file was given; name the file loaded in Zapit.");
            }
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(string.Format("Could not read the Zapit conditions file '{0}': {1}", path, ex.Message), ex);
            }
        }
    }
}
