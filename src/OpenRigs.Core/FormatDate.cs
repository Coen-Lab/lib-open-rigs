using Bonsai;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using System.Xml.Serialization;

namespace OpenRigs.Core
{
    /// <summary>
    /// Formats a UTC instant as the time token in data names: the machine's local time and its UTC offset at that
    /// instant, yyyy-MM-ddTHH-mm-ss±HH-MM (ISO 8601 with : as -), such as 2026-07-20T15-32-56+01-00.
    /// </summary>
    [Combinator]
    [Description("Formats a UTC time as the time in data names: the machine's local time and its UTC offset at that instant, yyyy-MM-ddTHH-mm-ss±HH-MM.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class FormatDate
    {
        /// <summary>The local time part of the token, before the offset.</summary>
        public const string LocalFormat = "yyyy-MM-ddTHH-mm-ss";

        /// <summary>
        /// Gets or sets the time zone the token is in: the machine's (TimeZoneInfo.Local) unless a test sets another.
        /// </summary>
        [XmlIgnore]
        [Browsable(false)]
        public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

        /// <summary>
        /// Formats each instant, read as UTC unless its Kind is Local.
        /// </summary>
        public IObservable<string> Process(IObservable<DateTime> source)
        {
            return source.Select(value => Format(value, TimeZone));
        }

        /// <summary>
        /// The token for an instant (read as UTC unless its Kind is Local) in a time zone, cut to the whole second.
        /// </summary>
        public static string Format(DateTime instant, TimeZoneInfo zone)
        {
            var utc = instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : DateTime.SpecifyKind(instant, DateTimeKind.Utc);
            utc = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
            var offset = zone.GetUtcOffset(utc);
            var local = utc + offset;
            return local.ToString(LocalFormat, CultureInfo.InvariantCulture)
                + (offset < TimeSpan.Zero ? "-" : "+")
                + offset.Duration().ToString("hh\\-mm", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The UTC instant a token names: at its offset, or UTC when it has none (names written before the offset was added).
        /// Throws when the text is not a token.
        /// </summary>
        public static DateTime Parse(string token)
        {
            DateTime local;
            if (token == null || (token.Length != 19 && token.Length != 25)
                || !DateTime.TryParseExact(token.Substring(0, Math.Min(19, token.Length)), LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            {
                throw new FormatException("'" + token + "' is not a time in a data name (" + LocalFormat + ", then optionally ±HH-MM).");
            }

            if (token.Length == 19) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
            TimeSpan offset;
            var sign = token[19];
            if ((sign != '+' && sign != '-') || !TimeSpan.TryParseExact(token.Substring(20), "hh\\-mm", CultureInfo.InvariantCulture, out offset))
            {
                throw new FormatException("'" + token + "' is not a time in a data name: its offset is not ±HH-MM.");
            }

            return DateTime.SpecifyKind(sign == '-' ? local + offset : local - offset, DateTimeKind.Utc);
        }

        /// <summary>
        /// The experiment's start (UTC), the time in its folder's name, ses-&lt;SessionId&gt;_&lt;time&gt; (LogController's PathPrefix).
        /// Throws, naming the folder, when the name does not end in one.
        /// </summary>
        public static DateTime FolderStart(string pathPrefix)
        {
            var folder = System.IO.Path.GetFileName((pathPrefix ?? string.Empty).TrimEnd('\\', '/'));
            var at = folder.LastIndexOf('_');
            try
            {
                if (at < 0) throw new FormatException();
                return Parse(folder.Substring(at + 1));
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(string.Format(
                    "The experiment folder '{0}' does not end in _<start> ({1}±HH-MM), so its files cannot be named.", folder, LocalFormat));
            }
        }
    }
}
