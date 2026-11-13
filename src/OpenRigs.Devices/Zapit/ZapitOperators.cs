using Bonsai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>Emits one read-only Zapit query per input.</summary>
    [Combinator]
    [Description("Emits a read-only Zapit query per input: IsStimConfigLoaded answers 1 or 0, GetState 0 idle, 1 stimulating, 2 ramp-down, GetNumConditions the count loaded.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CreateZapitQuery
    {
        /// <summary>Gets or sets the query to send.</summary>
        [Description("The query to send.")]
        public ZapitQuery Query { get; set; } = ZapitQuery.IsStimConfigLoaded;

        /// <summary>Emits the query for each input.</summary>
        public IObservable<ZapitCommand> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(input => ZapitCommand.Query(Query));
        }
    }

    /// <summary>
    /// Stops the workflow at start unless Zapit has loaded as many conditions as the conditions file
    /// the task names, and that file passes <see cref="ZapitConditions.Check"/>: Zapit presents
    /// whatever is at a condition's position in its own file, so two files would stimulate the wrong
    /// sites with no error.
    /// </summary>
    [Combinator]
    [Description("Checks Zapit's GetNumConditions reply against the conditions file (input: the reply and the file's path) and stops the workflow if they differ, the reply is an error or the file fails its checks. Passes the reply on.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CheckZapitConditionCount
    {
        /// <summary>Checks each (reply, path) pair.</summary>
        public IObservable<ZapitReply> Process(IObservable<Tuple<ZapitReply, string>> source)
        {
            return source.Select(input =>
            {
                Check(input.Item1, input.Item2);
                return input.Item1;
            });
        }

        /// <summary>Throws unless <paramref name="reply"/> counts the conditions in the file at <paramref name="path"/>.</summary>
        public static void Check(ZapitReply reply, string path)
        {
            if (reply == null || reply.Command != ZapitCommandType.GetNumConditions)
            {
                throw new InvalidOperationException(string.Format("CheckZapitConditionCount takes Zapit's GetNumConditions reply, not {0}.", reply));
            }
            var count = ZapitConditions.Check(path);
            if (reply.IsError)
            {
                throw new InvalidOperationException(string.Format(
                    "Zapit answered GetNumConditions with an error ({0}). The conditions file {1} has {2}: load it in Zapit and start again.",
                    reply, path, count));
            }
            if (reply.Value != count)
            {
                throw new InvalidOperationException(string.Format(
                    "Zapit has {0} conditions loaded but the conditions file {1} has {2}: load it in Zapit and start again.",
                    reply.Value, path, count));
            }
        }
    }

    /// <summary>The Zapit conditions file as JSON, for LogDataSchema: its full path and its text unchanged.</summary>
    [Combinator]
    [Description("Reads the Zapit conditions file the input names and returns its full path and text as JSON, for LogDataSchema.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitConditionsSnapshot
    {
        /// <summary>Reads each named file.</summary>
        public IObservable<string> Process(IObservable<string> source)
        {
            return source.Select(Snapshot);
        }

        /// <summary>The JSON for the file at <paramref name="path"/>.</summary>
        public static string Snapshot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var snapshot = new JObject
            {
                ["zapitConfigPath"] = fullPath,
                ["contents"] = File.ReadAllText(fullPath)
            };
            return snapshot.ToString(Formatting.Indented);
        }
    }
}
