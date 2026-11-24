using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reactive;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Expressions;
using OpenRigs.Shared;

namespace OpenRigs.Core
{
    /// <summary>
    /// The workflow's ConfigRoot, once per workflow (in OpenRigs.Devices' ReadLabRig): emits it once, and makes it
    /// known when the workflow is built to every node after it that reads rig.yml then (OpenRigs.LiveView's
    /// StreamDevices, OpenRigs.HarpDevices' RecordHarpDevices, OpenRigs.Manifest's ExperimentManifest), their only root:
    /// with none built before them, they stop the build. So a launch's ConfigRoot reaches every reader of rig.yml with no
    /// edge to any of them.
    /// </summary>
    [DefaultProperty(nameof(ConfigRoot))]
    [TypeDescriptionProvider(typeof(WorkflowConfigRootTypeDescriptionProvider))]
    [Description("Emits the workflow's ConfigRoot once, and gives it at build to every node after it that reads rig.yml when the workflow is built (StreamDevices, RecordHarpDevices, ExperimentManifest), their only root. Once per workflow, in ReadLabRig.")]
    public class WorkflowConfigRoot : GroupWorkflowBuilder
    {
        /// <summary>Initializes a new instance of the <see cref="WorkflowConfigRoot"/> class.</summary>
        public WorkflowConfigRoot()
        {
            GroupNode.Externalize(Workflow, Settings, "ConfigRoot");
        }

        /// <summary>Initializes a copy with the given inner workflow, which holds the settings (Bonsai's copies of a group).</summary>
        /// <param name="workflow">The inner workflow to copy.</param>
        public WorkflowConfigRoot(ExpressionBuilderGraph workflow)
            : base(workflow)
        {
            GroupNode.Externalize(Workflow, Settings, "ConfigRoot");
        }

        /// <summary>Gets or sets the root under which each machine has its own configuration directory.</summary>
        [Description("The root under which each machine has its own configuration directory, <ConfigRoot>\\<MACHINE-NAME>\\rig.yml.")]
        [Editor("Bonsai.Design.FolderNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string ConfigRoot
        {
            get { return Settings.ConfigRoot; }
            set { Settings.ConfigRoot = value; }
        }

        WorkflowConfigRootSettings Settings
        {
            get { return GroupNode.Settings<WorkflowConfigRootSettings>(Workflow); }
        }

        /// <summary>Makes ConfigRoot known to the nodes built after this one, and emits it once.</summary>
        /// <param name="arguments">None: the node has no inputs.</param>
        /// <returns>A sequence of the one ConfigRoot.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var configRoot = ConfigRoot ?? "";
            GroupNode.PublishConfigRoot(this, configRoot);
            return Expression.Constant(Observable.Return(configRoot), typeof(IObservable<string>));
        }
    }

    /// <summary>
    /// WorkflowConfigRoot's settings, kept as a node in its inner workflow so that they survive Bonsai's copies of the
    /// group. Set them on WorkflowConfigRoot; this node builds to nothing.
    /// </summary>
    [Description("WorkflowConfigRoot's settings (ConfigRoot), kept here so Bonsai's copies of the group keep them. Set them on WorkflowConfigRoot.")]
    public class WorkflowConfigRootSettings : ZeroArgumentExpressionBuilder
    {
        /// <summary>Gets or sets WorkflowConfigRoot's ConfigRoot.</summary>
        public string ConfigRoot { get; set; }

        /// <summary>Builds to an empty sequence: the node only holds the settings.</summary>
        /// <param name="arguments">None.</param>
        /// <returns>An empty sequence.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            return Expression.Constant(Observable.Empty<Unit>(), typeof(IObservable<Unit>));
        }
    }

    // WorkflowConfigRoot's own ConfigRoot in the Properties panel and to an ExternalizedMapping (ReadLabRig's).
    class WorkflowConfigRootTypeDescriptionProvider : ShownPropertiesProvider
    {
        public WorkflowConfigRootTypeDescriptionProvider()
            : base("ConfigRoot")
        {
        }
    }
}
