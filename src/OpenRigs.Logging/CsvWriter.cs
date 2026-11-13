using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Xml.Serialization;
using Bonsai.Expressions;

namespace OpenRigs.Logging
{
    /// <summary>
    /// Represents an operator that writes each element of the sequence to a delimited text file,
    /// like the Bonsai CsvWriter, with a header that names every column by its member path.
    /// </summary>
    /// <remarks>
    /// The Bonsai CsvWriter names a column by cutting its expression text at the last member
    /// separator, which drops the field name of every string and nullable column
    /// (bonsai-rx/bonsai#2662). This operator keeps the writer Bonsai builds and replaces only the
    /// header. For a timestamped input the Value prefix is dropped, which leaves Seconds followed
    /// by the record's own field names. Remove it once the upstream fix ships.
    /// </remarks>
    [XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Writes each element of the sequence to a delimited text file, with a header naming every column by its member path. For a timestamped input the Value prefix is dropped from the column names.")]
    public class CsvWriter : Bonsai.IO.CsvWriter
    {
        const string ValuePrefix = "Value.";

        static readonly MethodInfo BuildCombinatorMethod = typeof(CombinatorExpressionBuilder)
            .GetMethod(nameof(BuildCombinator), BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly MethodInfo WriteRowMethod = typeof(LoggedRows).GetMethod(nameof(LoggedRows.WriteRow));

        /// <summary>
        /// Gets or sets a value indicating whether each row is flushed to disk as it is written.
        /// </summary>
        /// <remarks>
        /// The Bonsai CsvWriter writes through a StreamWriter that flushes only when its 4 KB buffer
        /// fills or the file closes. An experiment's trial log, a few rows a minute, then reached disk
        /// tens of trials late, and a Bonsai ended by force lost its unflushed tail (app-rig-control,
        /// 1 Nov 2026: 411 rows against 503 frames). A flush per row costs a write per row, so it
        /// is for slow logs only (LogTrial and LogAsync turn it on), never a Harp log at 1 kHz.
        /// </remarks>
        [Description("Whether each row is flushed to disk as it is written, so the file is never behind and a Bonsai ended by force loses no rows. For slow logs only (trials, events): a flush per row at a Harp log's rate costs a disk write per row.")]
        public bool AutoFlush { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether each row written is also published to
        /// <see cref="LoggedRows"/>, for a live view to stream.
        /// </summary>
        [Description("Whether each row written is also published to LoggedRows, with its experiment, log name and row number, for a live view (OpenRigs.LiveView's StreamDevices) to stream as it happens. For slow logs only, as AutoFlush.")]
        public bool PublishRows { get; set; }

        /// <inheritdoc/>
        protected override Expression BuildCombinator(IEnumerable<Expression> arguments)
        {
            // The Bonsai CsvWriter binds its private Process method by name on the instance type,
            // which a subclass cannot see. A stock writer with the same settings builds the
            // expression, and the call is then bound to this node through the method it found. That way
            // FileName and the other file settings are still read from this node at subscribe time.
#pragma warning disable CS0612 // Type or member is obsolete
            var builder = new Bonsai.IO.CsvWriter
            {
                Delimiter = Delimiter,
                ListSeparator = ListSeparator,
                IncludeHeader = IncludeHeader,
                Selector = Selector,
                CompatibilityMode = CompatibilityMode
            };
#pragma warning restore CS0612 // Type or member is obsolete

            Expression expression;
            try
            {
                expression = (Expression)BuildCombinatorMethod.Invoke(builder, new object[] { arguments });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            if (!(expression is MethodCallExpression process) ||
                process.Arguments.Count != 3 ||
                !(process.Arguments[1] is ConstantExpression header) ||
                !(process.Arguments[2] is LambdaExpression writeAction))
            {
                throw new InvalidOperationException(
                    "The Bonsai CsvWriter built an expression this operator does not recognise, and the header cannot be named. " +
                    "Check the Bonsai.System version against OpenRigs.Logging.");
            }

            var headerText = IncludeHeader ? BuildHeader(writeAction) : (string)header.Value;
            if (AutoFlush || PublishRows) writeAction = WithRowActions(writeAction, headerText, AutoFlush, PublishRows);
            return Expression.Call(
                Expression.Constant(this),
                process.Method,
                process.Arguments[0],
                Expression.Constant(headerText),
                writeAction);
        }

        /// <summary>
        /// Returns the column names this writer gives a sequence of the given element type, as its
        /// header would name them, without writing anything.
        /// </summary>
        /// <param name="elementType">The type of the elements the writer receives.</param>
        /// <param name="selector">The writer's Selector, or null or empty for every member.</param>
        /// <returns>The column names in order.</returns>
        /// <remarks>
        /// For checks that hold a log's columns to a list (lib-open-rigs' check-bonsai holds
        /// BonsaiData_Trial to lab_names.yml), so the names are the ones the file will have.
        /// </remarks>
        public static string[] ColumnNames(Type elementType, string selector = null)
        {
            var writer = new CsvWriter { IncludeHeader = true, Delimiter = "\t", Selector = selector };
            var source = Expression.Parameter(typeof(IObservable<>).MakeGenericType(elementType));
            var call = (MethodCallExpression)writer.BuildCombinator(new Expression[] { source });
            var header = (string)((ConstantExpression)call.Arguments[1]).Value;
            return header.Length == 0 ? new string[0] : header.Split('\t');
        }

        // The write Bonsai built is writer.WriteLine(line). With either option on, the same line
        // goes through LoggedRows.WriteRow instead, which writes it, then flushes and publishes it
        // as asked. With both off the lambda is left as Bonsai built it, so every log that does
        // not ask writes exactly as before.
        static LambdaExpression WithRowActions(LambdaExpression writeAction, string header, bool flush, bool publish)
        {
            if (!(writeAction.Body is MethodCallExpression writeLine) ||
                writeLine.Method.Name != nameof(System.IO.TextWriter.WriteLine) ||
                writeLine.Object == null ||
                writeLine.Arguments.Count != 1 ||
                writeLine.Arguments[0].Type != typeof(string))
            {
                throw new InvalidOperationException(
                    "The Bonsai CsvWriter built a write this operator does not recognise, so AutoFlush and PublishRows cannot be applied. " +
                    "Check the Bonsai.System version against OpenRigs.Logging.");
            }

            var body = Expression.Call(
                WriteRowMethod,
                Expression.Convert(writeLine.Object, typeof(System.IO.TextWriter)),
                writeLine.Arguments[0],
                Expression.Constant(header, typeof(string)),
                Expression.Constant(flush),
                Expression.Constant(publish));
            return Expression.Lambda(writeAction.Type, body, writeAction.Parameters);
        }

        static string BuildHeader(LambdaExpression writeAction)
        {
            var line = ((MethodCallExpression)writeAction.Body).Arguments.Single();
            var root = writeAction.Parameters[0];

            // A list input writes each item through a converter lambda, with columns relative to the item.
            if (line is MethodCallExpression listJoin && listJoin.Method.Name == "ListJoin")
            {
                var converter = (LambdaExpression)listJoin.Arguments[1];
                root = converter.Parameters[0];
                line = converter.Body;
            }

            if (!(line is MethodCallExpression join) ||
                join.Method.Name != nameof(string.Join) ||
                !(join.Arguments[0] is ConstantExpression delimiter) ||
                !(join.Arguments[1] is NewArrayExpression columns))
            {
                throw new InvalidOperationException(
                    "The Bonsai CsvWriter built a line expression this operator does not recognise, and the header cannot be named.");
            }

            var names = columns.Expressions
                .Select(column => GetMemberPath(column, root))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToArray();

            if (IsTimestamped(root.Type))
            {
                names = names.Select(name => name.StartsWith(ValuePrefix, StringComparison.Ordinal)
                    ? name.Substring(ValuePrefix.Length)
                    : name).ToArray();
            }

            var duplicate = names.GroupBy(name => name).FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidOperationException(
                    $"The column name '{duplicate.Key}' appears more than once in the header. " +
                    "A timestamped record must not have its own Seconds field. Rename the field or log the record without it.");
            }

            return string.Join((string)delimiter.Value, names).Trim();
        }

        static string GetMemberPath(Expression column, ParameterExpression root)
        {
            // Columns arrive as the member itself (string), its ToString call, or a HasValue conditional (nullable).
            if (column is MethodCallExpression call && call.Method.Name == nameof(ToString) && call.Object != null)
            {
                column = call.Object;
            }
            else if (column is ConditionalExpression conditional && conditional.Test is MemberExpression hasValue)
            {
                column = hasValue.Expression;
            }

            var members = new Stack<string>();
            while (column is MemberExpression member)
            {
                members.Push(member.Member.Name);
                column = member.Expression;
            }

            return column == root ? string.Join(".", members) : string.Empty;
        }

        static bool IsTimestamped(Type type)
        {
            return type.IsGenericType &&
                type.Name == "Timestamped`1" &&
                type.GetMember("Seconds").Length > 0 &&
                type.GetMember("Value").Length > 0;
        }
    }
}
