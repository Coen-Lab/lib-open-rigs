using System;
using System.ComponentModel;
using System.Globalization;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;

namespace OpenRigs.Logging
{
    /// <summary>
    /// Represents one event of an experiment that does not belong to a trial, such as a manual reward,
    /// a flush or a spout move, written as one row of BonsaiData_Async by LogAsync.
    /// </summary>
    public class AsyncEvent
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncEvent"/> class.
        /// </summary>
        /// <param name="eventName">What happened, for example RewardLeft. Commas and line breaks are replaced, so the row stays one CSV row.</param>
        /// <param name="detail">Any text that qualifies the event, for example Manual. Commas and line breaks are replaced.</param>
        /// <param name="value">The number the event carries, for example microlitres or steps, or null if it carries none.</param>
        public AsyncEvent(string eventName, string detail, double? value)
        {
            Event = Clean(eventName);
            Detail = Clean(detail);
            Value = value.HasValue && double.IsNaN(value.Value) ? null : value;
        }

        /// <summary>
        /// Gets what happened, for example RewardLeft or SpoutsIn.
        /// </summary>
        public string Event { get; private set; }

        /// <summary>
        /// Gets any text that qualifies the event, for example Manual or BeforeGo. Empty if none.
        /// </summary>
        public string Detail { get; private set; }

        /// <summary>
        /// Gets the number the event carries, for example microlitres or steps. Null, written as an
        /// empty cell, if the event carries none.
        /// </summary>
        public double? Value { get; private set; }

        /// <summary>
        /// Returns the event as Event,Detail,Value.
        /// </summary>
        /// <returns>The event as one comma-separated line.</returns>
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2}",
                Event,
                Detail,
                Value.HasValue ? Value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // The lab's CsvWriter does not quote fields, so a comma or a line break would split the row.
            return text.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }

    /// <summary>
    /// Represents an operator that makes an <see cref="AsyncEvent"/> from each element of the
    /// sequence, for LogAsync to write into BonsaiData_Async.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Makes an AsyncEvent from each element. A record with fields named Event, Detail or Value fills those (the properties fill any it lacks). Otherwise Event and Detail come from the properties, Value from the element when it is a number or a boolean (true is 1), and Detail from the element when it is text and Detail is empty. Stamp it with WithLatestTimestamp and send it to the AsyncEvents subject that LogAsync declares.")]
    public class CreateAsyncEvent
    {
        /// <summary>
        /// Gets or sets what happened, for example RewardLeft or SpoutsIn.
        /// </summary>
        [Description("What happened, for example RewardLeft or SpoutsIn.")]
        public string Event { get; set; } = "Event";

        /// <summary>
        /// Gets or sets any text that qualifies the event, for example Manual. When empty and the
        /// element is text, the element is the detail.
        /// </summary>
        [Description("Any text that qualifies the event, for example Manual. When empty and the element is text, the element is the detail.")]
        public string Detail { get; set; }

        /// <summary>
        /// Makes an <see cref="AsyncEvent"/> from each element of the sequence.
        /// </summary>
        /// <typeparam name="TSource">The type of the elements of the sequence.</typeparam>
        /// <param name="source">The sequence of elements, each one event.</param>
        /// <returns>A sequence of events, one for each element.</returns>
        public IObservable<AsyncEvent> Process<TSource>(IObservable<TSource> source)
        {
            // A record with fields named Event, Detail or Value (an ExpressionTransform's
            // new(... as Event, ... as Detail, ... as Value)) fills those from each element.
            var eventField = RecordField(typeof(TSource), nameof(AsyncEvent.Event));
            var detailField = RecordField(typeof(TSource), nameof(AsyncEvent.Detail));
            var valueField = RecordField(typeof(TSource), nameof(AsyncEvent.Value));
            var isRecord = eventField != null || detailField != null || valueField != null;

            return source.Select(element =>
            {
                var eventName = Event;
                var detail = Detail;
                double? value;
                if (isRecord)
                {
                    if (eventField != null) eventName = Convert.ToString(eventField.GetValue(element), CultureInfo.InvariantCulture);
                    if (detailField != null) detail = Convert.ToString(detailField.GetValue(element), CultureInfo.InvariantCulture);
                    value = valueField != null ? ToNumber(valueField.GetValue(element)) : null;
                }
                else
                {
                    var text = element as string;
                    if (string.IsNullOrEmpty(detail) && text != null)
                    {
                        detail = text;
                    }

                    value = ToNumber(element);
                }

                return new AsyncEvent(eventName, detail, value);
            });
        }

        static System.Reflection.PropertyInfo RecordField(Type type, string name)
        {
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
            {
                return null;
            }

            return type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        }

        static double? ToNumber(object element)
        {
            if (element == null)
            {
                return null;
            }

            if (element is bool flag)
            {
                return flag ? 1 : 0;
            }

            switch (Convert.GetTypeCode(element))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Convert.ToDouble(element, CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }
    }
}
