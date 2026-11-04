using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Core
{
    /// <summary>
    /// Parses the text lines a serial balance prints into <see cref="WeightMeasurement"/> readings.
    /// </summary>
    [Combinator]
    [DefaultProperty("UnitSuffix")]
    [Description("Parses each line a serial balance prints, such as \"   12.345 g\" or \"   12.345 g?\", into a WeightMeasurement: the weight, and confidence 1 when stable or 0 while the unstable marker is present. Lines listed in Ignore, such as the balance's echo or error strings, are dropped. OpenRigs.Devices:SerialBalance.bonsai maps the properties from the balance block of the calibration settings.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ParseBalanceReading
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Gets or sets the text that follows the number in a reading.</summary>
        [Description("Text that follows the number in a reading, such as g. The value is parsed up to it.")]
        public string UnitSuffix { get; set; } = "g";

        /// <summary>Gets or sets the text present while the balance has not settled.</summary>
        [Description("Text present in a reading while the balance has not settled, such as ?.")]
        public string UnstableMarker { get; set; } = "?";

        /// <summary>Gets or sets the lines to drop, separated by commas.</summary>
        [Description("Lines to drop, separated by commas, such as the balance's echo or error strings.")]
        public string Ignore { get; set; } = "ES";

        /// <summary>Parses each printed line.</summary>
        public IObservable<WeightMeasurement> Process(IObservable<string> source)
        {
            return source
                .Where(line => !IsIgnored(line, Ignore))
                .Select(line => Parse(line, UnitSuffix, UnstableMarker, (DateTime.UtcNow - Epoch).TotalSeconds));
        }

        /// <summary>Returns whether a line is empty or listed in the ignore list.</summary>
        public static bool IsIgnored(string line, string ignore)
        {
            if (line == null) return true;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return true;
            foreach (string item in (ignore ?? "").Split(','))
            {
                if (item.Trim().Length > 0 && trimmed == item.Trim()) return true;
            }
            return false;
        }

        /// <summary>Parses one printed line.</summary>
        public static WeightMeasurement Parse(string line, string unitSuffix, string unstableMarker, double timestamp)
        {
            string text = line.Trim();
            bool stable = string.IsNullOrEmpty(unstableMarker) || !text.Contains(unstableMarker);
            if (!string.IsNullOrEmpty(unstableMarker)) text = text.Replace(unstableMarker, "");
            if (!string.IsNullOrEmpty(unitSuffix))
            {
                int cut = text.IndexOf(unitSuffix, StringComparison.Ordinal);
                if (cut >= 0) text = text.Substring(0, cut);
            }
            float weight;
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out weight))
            {
                throw new InvalidOperationException(
                    "Could not parse a weight from the balance line '" + line + "'. Check unitSuffix, unstableMarker and ignore in the balance settings.");
            }
            return new WeightMeasurement(timestamp, weight, stable ? 1f : 0f);
        }
    }
}
