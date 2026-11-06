using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenRigs.Shared;

namespace OpenRigs.Control
{
    /// <summary>
    /// The experiment's settings at Go, as JSON text for OpenRigs.Logging's LogDataSchema named
    /// Settings_Experiment, so the experiment folder holds them beside the rig's and the task's
    /// (Settings\Settings_Experiment_&lt;DateTime&gt;.json): the subject and session, the rig, the
    /// workflow with its repository and commit as OpenRigs.Manifest reads them, who launched it and
    /// who sent Go, and every declared parameter as it stood at Go, ParameterDefault's included.
    /// The repository is read once at Start, so the Go reply never waits on it.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("The experiment's settings at Go as JSON text (subject, session, rig, workflow, commit, who launched and sent Go, every parameter at Go), for LogDataSchema named Settings_Experiment. Its input is ExperimentGo.")]
    public class ExperimentSettings
    {
        /// <summary>The experiment's settings as JSON text, once for each Go event.</summary>
        /// <param name="source">The Go event (ExperimentGo).</param>
        /// <returns>The settings, indented JSON.</returns>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            return Observable.Defer(() =>
            {
                var workflow = Workflow();
                return source.Where(e => e.Kind == ControlEventKind.Go)
                    .Select(go => Settings(go, ControlState.Current, workflow).ToString(Formatting.Indented));
            });
        }

        /// <summary>The workflow Bonsai was started with and its repository: the workflow's path in
        /// the repository, the repository's origin, branch and commit, and whether it had changes.</summary>
        /// <returns>The workflow's fields, in the order the settings give them.</returns>
        public static JObject Workflow()
        {
            var args = Environment.GetCommandLineArgs();
            var full = WorkflowFile.Find(args, Environment.CurrentDirectory);
            var root = Git.FindRoot(WorkflowFile.Folder(full, Environment.CurrentDirectory));
            string branch = null, commit = null;
            if (root != null) Git.Head(root, out branch, out commit);
            var changed = root != null ? Git.Changed(root) : null;
            return new JObject
            {
                ["workflow"] = WorkflowFile.Relative(full, root),
                ["repo"] = root != null ? Git.OriginUrl(root) : null,
                ["branch"] = branch,
                ["commit"] = commit,
                ["dirty"] = changed != null ? (JToken)(changed.Count > 0) : JValue.CreateNull(),
            };
        }

        /// <summary>The settings for one Go event.</summary>
        /// <param name="go">The Go event, with every parameter as it stood.</param>
        /// <param name="state">The running ControlServer's state (who launched and sent Go), or null.</param>
        /// <param name="workflow">The workflow's fields, from <see cref="Workflow"/>.</param>
        /// <returns>The settings.</returns>
        public static JObject Settings(ControlEvent go, ControlState state, JObject workflow)
        {
            var settings = new JObject
            {
                ["subject_id"] = go.SubjectId,
                ["session_id"] = go.SessionId,
                ["rig"] = Environment.MachineName,
            };
            foreach (var field in (workflow ?? new JObject()).Properties()) settings[field.Name] = field.Value;
            settings["launched_by"] = Person.ToJson(state != null ? state.LaunchedBy : null);
            settings["go_by"] = Person.ToJson(state != null ? state.GoBy : null);
            settings["go"] = go.Time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var parameters = new JObject();
            foreach (var p in go.Parameters ?? Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, string>>()) parameters[p.Key] = p.Value;
            settings["parameters"] = parameters;
            return settings;
        }
    }
}
