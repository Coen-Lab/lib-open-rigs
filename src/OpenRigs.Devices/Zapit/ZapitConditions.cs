using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>What <see cref="ZapitConditions.Check"/> reads from a conditions file that passes.</summary>
    public sealed class ZapitConditionsFile
    {
        /// <summary>A conditions file's points by condition and its max ramp-down, as Check reads them (or a test's stand-in for a file).</summary>
        public ZapitConditionsFile(IList<string> pointsMm, double maxRampDownSeconds)
        {
            if (pointsMm == null || pointsMm.Count == 0) throw new ArgumentException("A conditions file has at least one condition.", "pointsMm");
            PointsMm = new List<string>(pointsMm).AsReadOnly();
            MaxRampDownSeconds = maxRampDownSeconds;
        }

        /// <summary>Gets the number of conditions, 1 to N.</summary>
        public int Count { get { return PointsMm.Count; } }

        /// <summary>Gets each condition's points, condition 1 first, as the trial log's laserPointsMm: in mm as the file
        /// writes them, each point its AP then its ML separated by a space, points in the file's order separated by ;
        /// (ML [1.5, -1.5] and AP [-0.7, -0.7] is "-0.7 1.5;-0.7 -1.5").</summary>
        public IList<string> PointsMm { get; private set; }

        /// <summary>Gets the highest offRampDownDuration_ms of any condition, in seconds: the masking tail every trial's laser
        /// ends with, so a laser held until the outcome holds its trigger line this long past it (LaserTiming.UntilOutcome).</summary>
        public double MaxRampDownSeconds { get; private set; }
    }

    /// <summary>
    /// Reads a Zapit conditions file (YAML) without a YAML library. A condition is a top-level
    /// <c>stimLocationsNN:</c> key; Zapit numbers conditions by their position, so the numbers must be
    /// exactly 1 to N.
    /// </summary>
    public static class ZapitConditions
    {
        /// <summary>The shortest ramp-down allowed, in seconds: a shorter one lets inhibition end abruptly (rebound excitation).</summary>
        public const double MinimumRampDownSeconds = 0.1;

        /// <summary>The longest ramp-down allowed, in seconds: the masking light goes out 1 s after the outcome, after the ramp and Zapit's stop.</summary>
        public const double MaximumRampDownSeconds = 0.5;

        static readonly Regex ConditionKey = new Regex(@"^stimLocations(\d+)\s*:", RegexOptions.CultureInvariant);
        static readonly Regex TopLevelKey = new Regex(@"^([A-Za-z_]\w*)\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        // A value runs to its line's end, a YAML comment, or the end of its flow mapping's entry ("," then the next key, "}" or
        // "]"), so "250,5" or ".nan" is read whole and refused, not as 250 or ".".
        const string Value = @"[ \t]*:[ \t]*([^\r\n#,}\]]*(?:,[ \t]*[0-9][^\r\n#,}\]]*)*)";
        static readonly Regex RampDown = new Regex("offRampDownDuration_ms" + Value, RegexOptions.CultureInvariant);
        static readonly Regex Coordinate = new Regex(@"^[ \t]+(ML|AP)[ \t]*:[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        static readonly Regex ModulationFrequency = new Regex("stimModulationFreqHz" + Value, RegexOptions.CultureInvariant);

        // A finite number written in invariant culture (a decimal point, no thousands separator), or null.
        static double? Finite(string value)
        {
            double parsed;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed)) return null;
            return parsed;
        }

        /// <summary>
        /// The conditions in the file, each with its points, and its max ramp-down, after checking they are numbered 1 to N,
        /// each gives its ML and AP as one number or a [list] of numbers, as many of each, and
        /// each ramps down over <see cref="MinimumRampDownSeconds"/> to <see cref="MaximumRampDownSeconds"/> and over
        /// more than one stimulus cycle. Throws, naming the file and every problem, otherwise.
        /// </summary>
        public static ZapitConditionsFile Check(string path)
        {
            var text = Read(path);
            var numbers = new List<int>();
            foreach (var line in text.Split('\n'))
            {
                var match = ConditionKey.Match(line);
                if (!match.Success) continue;
                int number;
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "The Zapit conditions file '{0}' has a condition stimLocations{1}, a number too large: conditions must be numbered 1, 2, 3 ... by their position in the file.", path, match.Groups[1].Value));
                }
                numbers.Add(number);
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
            var maxRampDownSeconds = CheckRampDowns(path, text);
            return new ZapitConditionsFile(ReadPoints(path, text, numbers.Count), maxRampDownSeconds);
        }

        // Each condition's points (ZapitConditionsFile.PointsMm), condition 1 first; numbers is 1 to count, checked.
        static List<string> ReadPoints(string path, string text, int count)
        {
            var points = new string[count];
            var problems = new List<string>();
            foreach (var pair in Blocks(text))
            {
                var key = ConditionKey.Match(pair.Key + ":");
                if (!key.Success) continue;
                string[] ml = null, ap = null;
                var unreadable = false;
                foreach (Match line in Coordinate.Matches(pair.Value))
                {
                    var values = Numbers(line.Groups[2].Value);
                    if (values == null)
                    {
                        problems.Add(string.Format("{0}'s {1} is '{2}', not a number or a [list] of numbers", pair.Key, line.Groups[1].Value, line.Groups[2].Value));
                        unreadable = true;
                    }
                    else if (line.Groups[1].Value == "ML") ml = values;
                    else ap = values;
                }
                if (unreadable) continue;
                if (ml == null || ap == null)
                {
                    problems.Add(pair.Key + " has no " + (ml == null && ap == null ? "ML or AP" : ml == null ? "ML" : "AP"));
                    continue;
                }
                if (ml.Length != ap.Length)
                {
                    problems.Add(string.Format("{0} has {1} ML and {2} AP", pair.Key, ml.Length, ap.Length));
                    continue;
                }
                var each = new string[ml.Length];
                for (var i = 0; i < ml.Length; i++) each[i] = ap[i] + " " + ml[i];
                points[int.Parse(key.Groups[1].Value, CultureInfo.InvariantCulture) - 1] = string.Join(";", each);
            }
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Format(
                    "The Zapit conditions file '{0}': {1}. Each condition gives where the laser goes as ML and AP, each one number or a [list], as many of each.",
                    path, string.Join("; ", problems)));
            }
            return new List<string>(points);
        }

        // A coordinate's value as written: one number, or a flow list of them; null for anything else.
        static string[] Numbers(string value)
        {
            var list = value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal);
            var items = (list ? value.Substring(1, value.Length - 2) : value).Split(',');
            if (!list && items.Length != 1) return null;
            double parsed;
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = items[i].Trim();
                if (!double.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return null;
            }
            return items;
        }

        // Each top-level key's text, from its key to the next.
        static Dictionary<string, string> Blocks(string text)
        {
            var blocks = new Dictionary<string, string>();
            var keys = TopLevelKey.Matches(text);
            for (var i = 0; i < keys.Count; i++)
            {
                var end = i + 1 < keys.Count ? keys[i + 1].Index : text.Length;
                blocks[keys[i].Groups[1].Value] = text.Substring(keys[i].Index, end - keys[i].Index);
            }
            return blocks;
        }

        // MATLAB Zapit ramps down in whole cycles, and a ramp of one cycle or less writes zeros without stopping
        // the DAQ, so a hardware-triggered rig's next stimulus would start when armed, not at its trigger.
        static double CheckRampDowns(string path, string text)
        {
            var maxMs = 0.0;
            var blocks = Blocks(text);

            string block;
            var frequency = Match.Empty;
            if (blocks.TryGetValue("stimModulationFreqHz", out block)) frequency = ModulationFrequency.Match(block);
            if (!frequency.Success && blocks.TryGetValue("stimLocations01", out block)) frequency = ModulationFrequency.Match(block);
            var problems = new List<string>();
            var hz = frequency.Success ? Finite(frequency.Groups[1].Value) : null;
            if (frequency.Success && !(hz > 0))
                problems.Add(string.Format(CultureInfo.InvariantCulture, "stimModulationFreqHz is '{0}', not a number above 0", frequency.Groups[1].Value.Trim()));
            var cycleMs = hz > 0 ? 1000.0 / hz.Value : 0;
            foreach (var pair in blocks)
            {
                if (!ConditionKey.IsMatch(pair.Key + ":")) continue;
                var ramp = RampDown.Match(pair.Value);
                if (!ramp.Success)
                {
                    problems.Add(pair.Key + " has no offRampDownDuration_ms");
                    continue;
                }
                var read = Finite(ramp.Groups[1].Value);
                if (read == null)
                {
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0}'s offRampDownDuration_ms is '{1}', not a finite number", pair.Key, ramp.Groups[1].Value.Trim()));
                    continue;
                }
                var rampMs = read.Value;
                maxMs = Math.Max(maxMs, rampMs);
                if (rampMs < MinimumRampDownSeconds * 1000)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} ramps down over {1} ms, less than {2} ms", pair.Key, rampMs, MinimumRampDownSeconds * 1000));
                else if (rampMs > MaximumRampDownSeconds * 1000)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} ramps down over {1} ms, more than {2} ms", pair.Key, rampMs, MaximumRampDownSeconds * 1000));
                else if (rampMs <= cycleMs)
                    problems.Add(string.Format(CultureInfo.InvariantCulture, "{0} ramps down over {1} ms, not longer than one {2} Hz cycle ({3} ms)", pair.Key, rampMs, hz, cycleMs));
            }
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The Zapit conditions file '{0}': {1}. Each condition needs offRampDownDuration_ms of {2} to {3} ms and longer than one stimulus cycle.",
                    path, string.Join("; ", problems), MinimumRampDownSeconds * 1000, MaximumRampDownSeconds * 1000));
            }
            return maxMs / 1000;
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
