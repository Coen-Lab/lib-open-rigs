using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive;
using System.Reactive.Linq;
using System.Security;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using OpenRigs.Shared;

namespace OpenRigs.LiveView
{
    /// <summary>
    /// The live view of every device on a rig, in one node. At build it reads this machine's
    /// rig.yml and, for each device entry (kind: instance:) whose data the workflow publishes as
    /// a subject named &lt;Kind-Instance&gt;.Data, adds its kind's recipe to its own inner workflow,
    /// beside a StreamController that opens OpenRigs.Streaming's two ports. Only subjects built
    /// before this node can be found, so it goes last in the workflow.
    /// </summary>
    [DefaultProperty(nameof(ConfigRoot))]
    [TypeDescriptionProvider(typeof(StreamDevicesTypeDescriptionProvider))]
    [Description("Streams every device in this machine's rig.yml that the workflow publishes as <Kind-Instance>.Data, at its kind's rate, over OpenRigs.Streaming. Put it last in the workflow: only subjects built before it are found.")]
    public class StreamDevices : GroupWorkflowBuilder
    {
        // The plan the inner workflow was last made from. The editor builds on every change, so
        // the inner workflow is only remade when the plan differs.
        string generatedFrom;

        /// <summary>Initializes a new instance of the <see cref="StreamDevices"/> class.</summary>
        public StreamDevices()
        {
            GroupNode.Externalize(Workflow, Settings, "ConfigRoot");
        }

        /// <summary>
        /// Initializes a copy with the given inner workflow. Bonsai copies a group by this
        /// constructor (ExpressionBuilderGraphExtensions.Clone): the editor's working copy of every
        /// workflow it opens, the player's when it shows a layout, and every save. The copy gets
        /// only the Name, Description and the converted inner workflow, so the settings live in
        /// that workflow (StreamDevicesSettings), whose leaf nodes the copy shares.
        /// </summary>
        /// <param name="workflow">The inner workflow to copy.</param>
        public StreamDevices(ExpressionBuilderGraph workflow)
            : base(workflow)
        {
            GroupNode.Externalize(Workflow, Settings, "ConfigRoot");
        }

        /// <summary>Gets or sets the root under which each machine has its own configuration directory.</summary>
        [Category("Config")]
        [Description("The root under which each machine has its own configuration directory, <ConfigRoot>\\<MACHINE-NAME>\\rig.yml. Read when the workflow is built; a missing file streams no devices.")]
        public string ConfigRoot
        {
            get { return Settings.ConfigRoot; }
            set { Settings.ConfigRoot = value; }
        }

        /// <summary>Gets or sets the TCP port of the data publisher.</summary>
        [Category("Ports")]
        [Description("TCP port of the data publisher, on every interface.")]
        public int DataPort
        {
            get { return Settings.DataPort; }
            set { Settings.DataPort = value; }
        }

        /// <summary>Gets or sets the TCP port of the video publisher.</summary>
        [Category("Ports")]
        [Description("TCP port of the video publisher, on every interface.")]
        public int VideoPort
        {
            get { return Settings.VideoPort; }
            set { Settings.VideoPort = value; }
        }

        // The settings node in the inner workflow, added on first use. A workflow saved before
        // the node existed gets it when its ConfigRoot is read back, after the inner workflow.
        StreamDevicesSettings Settings
        {
            get { return GroupNode.Settings<StreamDevicesSettings>(Workflow); }
        }

        /// <summary>Remakes the inner workflow if rig.yml or the subjects published have changed, then builds it.</summary>
        /// <param name="arguments">None: the node has no inputs.</param>
        /// <returns>The inner workflow's expression.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var plan = LiveViewPlan.Create(ConfigRoot, name => GroupNode.PublishedType(this, name));
            var signature = plan.Signature(DataPort, VideoPort);
            if (signature != generatedFrom)
            {
                Generate(plan);
                generatedFrom = signature;
            }
            return base.Build(arguments);
        }

        // Loads every include in the generated workflow, however deep, before it is built. A
        // PropertyMapping onto an include sees the include's properties only once its workflow is
        // loaded, and an include loads when its own turn to build comes, after the mapping onto
        // it. Bonsai loads includes ahead of time in a pass before building (groups and includes
        // only), which the workflow made here missed; and PackDataMessage maps PayloadType through
        // BuildMessage into CreateEnvelope inside a SelectMany, which no pass reaches. An unloaded
        // include's ArgumentRange (public) loads it.
        static void LoadIncludes(ExpressionBuilderGraph workflow)
        {
            foreach (var node in workflow)
            {
                var builder = ExpressionBuilder.Unwrap(node.Value);
                var include = builder as IncludeWorkflowBuilder;
                if (include != null && include.Workflow == null)
                {
                    var range = include.ArgumentRange;
                    if (include.Workflow == null)
                        throw new InvalidOperationException("OpenRigs.LiveView could not load " + include.Path + ": is its package installed?");
                }
                var nested = builder as IWorkflowExpressionBuilder;
                if (nested != null && nested.Workflow != null) LoadIncludes(nested.Workflow);
            }
        }

        // The inner workflow is one group, LiveView, holding the rest, and a new plan swaps that
        // group for a new one rather than emptying it. Before a build Bonsai adds an edge from
        // each subject's publisher to its subscribers (DataMessage from StreamController to every
        // stream) in the workflow holding both, and takes them out again after: in LiveView's
        // own workflow, which stays whole when it is swapped out. Emptying it mid-build stopped
        // the build ("The specified edge source does not belong to the graph"). A saved workflow
        // carries the last LiveView, which the first build replaces.
        //
        // In the editor, and in the player when it shows a layout, this is Bonsai's inspectable
        // copy, where every node, however deep, is wrapped in an InspectBuilder (the visualizer
        // windows are made from them, and fail on a bare node). The new group is wrapped the same
        // way there, with Bonsai's own conversion.
        void Generate(LiveViewPlan plan)
        {
            var content = new ExpressionBuilderGraph();
            Fill(content, plan);
            LoadIncludes(content);
            var workflow = Workflow;
            var settings = Settings;
            var inspectable = workflow.Any(node => node.Value is InspectBuilder);
            foreach (var node in workflow.ToList())
            {
                var kept = GroupNode.Unwrap(node.Value);
                if (kept != settings && !(kept is ExternalizedMappingBuilder)) workflow.Remove(node);
            }
            var group = new ExpressionBuilderGraph();
            group.Add(Group("LiveView", "Made from this machine's rig.yml each time the workflow is built; edits here are lost.", content));
            if (inspectable) group = group.ToInspectableGraph();
            workflow.Add(group.Single().Value);
        }

        void Fill(ExpressionBuilderGraph workflow, LiveViewPlan plan)
        {
            var controller = Load("StreamController", new Dictionary<string, string>
            {
                { "RigId", plan.RigId },
                { "DataPort", DataPort.ToString() },
                { "VideoPort", VideoPort.ToString() },
            });
            if (!plan.HasSessionFolder) RemoveSessionKey(controller);
            workflow.Add(Group("StreamController", plan.HasSessionFolder
                ? "Opens the data and video publishers and declares DataMessage, VideoMessage and StreamIdentity (RigId, session key). The key is \"ready\" until PathPrefix names the session folder, then that folder's last two names."
                : "Opens the data and video publishers and declares DataMessage, VideoMessage and StreamIdentity (RigId, session key). The workflow publishes no PathPrefix, so the key stays \"ready\".",
                controller));

            if (plan.HasSessionFolder)
            {
                workflow.Add(Group("elapsed", "Seconds since the session folder (PathPrefix) appeared, once a second.",
                    Load("Elapsed", new Dictionary<string, string>())));
            }

            if (plan.StreamsLoggedRows)
            {
                workflow.Add(Group(LiveViewPlan.LoggedRowsStream,
                    "Every row a log with PublishRows writes (OpenRigs.Logging's LogTrial and LogAsync), as it is written: its session, log name, row number, header and line, as the logs stream.",
                    Load("LoggedRows", new Dictionary<string, string> { { "Stream", LiveViewPlan.LoggedRowsStream } })));
            }

            foreach (var device in plan.Streamed)
            {
                workflow.Add(Group(device.Stream, device.Summary, Load(device.Recipe.Template, device.TemplateValues())));
            }

            workflow.Add(Group("liveview", "Every 5 s, the devices streamed and skipped and why, as the liveview stream.",
                Load("Report", new Dictionary<string, string>
                {
                    { "Streamed", string.Join("|", (plan.StreamsLoggedRows ? new[] { LiveViewPlan.LoggedRowsStream } : new string[0]).Concat(plan.Streamed.Select(d => d.Stream))) },
                    { "Skipped", string.Join("|", plan.Skipped) },
                    { "Notes", string.Join("|", plan.Notes) },
                })));
        }

        static GroupWorkflowBuilder Group(string name, string description, ExpressionBuilderGraph workflow)
        {
            return new GroupWorkflowBuilder(workflow) { Name = name, Description = description };
        }

        // Without a session folder the key is only "ready": drop PathPrefix and SessionKey, which
        // would otherwise stop the build looking for a subject the workflow does not have, and the
        // Merge after them. A Merge left with one string input takes Rx's flattening overload and
        // sends the key one character at a time ("y" last), so "ready" goes straight on instead.
        static void RemoveSessionKey(ExpressionBuilderGraph controller)
        {
            var subscribe = controller.First(n => n.Value is SubscribeSubject s && s.Name == "PathPrefix");
            var key = subscribe.Successors.Single().Target;
            var merge = key.Successors.Single().Target;
            var ready = controller.Single(n => n != key && n.Successors.Any(e => e.Target == merge));
            var onward = merge.Successors.Single();
            controller.Remove(subscribe);
            controller.Remove(key);
            controller.Remove(merge);
            controller.AddEdge(ready, onward.Target, new ExpressionBuilderArgument(onward.Label.Index));
        }

        static ExpressionBuilderGraph Load(string template, IDictionary<string, string> values)
        {
            string xml;
            var resource = "OpenRigs.LiveView.Templates." + template + ".xml";
            using (var stream = typeof(StreamDevices).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) throw new InvalidOperationException("OpenRigs.LiveView has no template " + resource + ".");
                using (var reader = new StreamReader(stream)) xml = reader.ReadToEnd();
            }
            foreach (var pair in values)
            {
                xml = xml.Replace("{{" + pair.Key + "}}", SecurityElement.Escape(pair.Value ?? string.Empty));
            }
            if (xml.Contains("{{")) throw new InvalidOperationException("Template " + template + " has a placeholder with no value.");
            using (var reader = XmlReader.Create(new StringReader(xml)))
            {
                return ((WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader)).Workflow;
            }
        }
    }

    /// <summary>
    /// StreamDevices' settings, kept as a node in its inner workflow so that they survive Bonsai's
    /// copies of the group. Set them on StreamDevices; this node builds to nothing.
    /// </summary>
    [Description("StreamDevices' settings (ConfigRoot, DataPort, VideoPort), kept here so Bonsai's copies of the group keep them. Set them on StreamDevices.")]
    public class StreamDevicesSettings : ZeroArgumentExpressionBuilder
    {
        /// <summary>Gets or sets StreamDevices' ConfigRoot.</summary>
        public string ConfigRoot { get; set; } = RigDevices.DefaultConfigRoot;

        /// <summary>Gets or sets StreamDevices' DataPort.</summary>
        public int DataPort { get; set; } = 5556;

        /// <summary>Gets or sets StreamDevices' VideoPort.</summary>
        public int VideoPort { get; set; } = 5558;

        /// <summary>Builds to an empty sequence: the node only holds the settings.</summary>
        /// <param name="arguments">None.</param>
        /// <returns>An empty sequence.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            return Expression.Constant(Observable.Empty<Unit>(), typeof(IObservable<Unit>));
        }
    }

    // StreamDevices' own settings in the Properties panel and to an ExternalizedMapping: its inner
    // workflow is generated and externalizes only ConfigRoot, for a workflow property to be mapped onto it.
    class StreamDevicesTypeDescriptionProvider : ShownPropertiesProvider
    {
        public StreamDevicesTypeDescriptionProvider()
            : base("ConfigRoot", "DataPort", "VideoPort")
        {
        }
    }
}
