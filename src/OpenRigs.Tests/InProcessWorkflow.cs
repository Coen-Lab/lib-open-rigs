using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Threading;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;

namespace OpenRigs.Tests
{
    /// <summary>
    /// Builds a test workflow in this process, as Bonsai builds it at Start, and runs it for a while: a piece's test
    /// whose workflow ends in text outputs, one per thing the piece did. Every assembly the workflow names must be
    /// loaded or referenced by the test project.
    /// </summary>
    public static class InProcessWorkflow
    {
        /// <summary>
        /// Runs <paramref name="xml"/> until it has given <paramref name="count"/> outputs, stopped, or
        /// <paramref name="seconds"/> passed; the outputs in order and the error that stopped it, if any.
        /// The workflow's output must be text.
        /// </summary>
        public static Tuple<List<string>, Exception> Run(string xml, double seconds, int count = int.MaxValue)
        {
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            var run = (IObservable<string>)Expression.Lambda(builder.Workflow.Build()).Compile().DynamicInvoke();
            var outputs = new List<string>();
            Exception error = null;
            using (var done = new ManualResetEventSlim())
            using (run.Subscribe(
                output => { lock (outputs) { outputs.Add(output); if (outputs.Count >= count) done.Set(); } },
                e => { error = e; done.Set(); },
                done.Set))
            {
                done.Wait(TimeSpan.FromSeconds(seconds));
            }
            lock (outputs) return Tuple.Create(new List<string>(outputs), error);
        }

        /// <summary>The innermost message of <paramref name="error"/>, or null.</summary>
        public static string Innermost(Exception error)
        {
            while (error != null && error.InnerException != null) error = error.InnerException;
            return error == null ? null : error.Message;
        }
    }
}
