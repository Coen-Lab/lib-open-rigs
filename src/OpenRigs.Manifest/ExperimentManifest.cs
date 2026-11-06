using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Expressions;
using OpenRigs.Shared;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// Writes manifest.json into each experiment folder, with nothing wired to it. At build it finds
    /// the PathPrefix subject (OpenRigs.Logging's LogController), so it goes after LogController.
    /// At Start it reads the configuration files and the launch; when PathPrefix names the folder
    /// it writes the manifest (status partial, outcome running), and again when the run ends.
    /// One input may be connected, the run: its completion means completed, its error is the
    /// run's error, and its elements are counted. Without it the end is read from
    /// OpenRigs.Control's phase when ExperimentControl is in the workflow.
    /// </summary>
    [DefaultProperty(nameof(ConfigFiles))]
    [TypeDescriptionProvider(typeof(ExperimentManifestTypeDescriptionProvider))]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Writes manifest.json into the experiment folder (PathPrefix) when it appears and when the run ends: rig, workflow commit, launch, who launched and sent Go, config files as loaded at Start, outcome and every file. Put it after LogController. An input, if connected, is the run: its completion, error and element count.")]
    public class ExperimentManifest : GroupWorkflowBuilder
    {
        /// <summary>The subject LogController publishes the experiment folder's path under.</summary>
        public const string PathSubject = "PathPrefix";

        /// <summary>Initializes a new instance of the <see cref="ExperimentManifest"/> class.</summary>
        public ExperimentManifest()
        {
        }

        /// <summary>
        /// Initializes a copy with the given inner workflow. Bonsai copies a group by this
        /// constructor (the editor's working copy, the player's when it shows a layout, every
        /// save), passing only the Name, Description and inner workflow, so the settings live in
        /// that workflow (ExperimentManifestSettings), as StreamDevices' do.
        /// </summary>
        /// <param name="workflow">The inner workflow to copy.</param>
        public ExperimentManifest(ExpressionBuilderGraph workflow)
            : base(workflow)
        {
        }

        /// <summary>Gets or sets the name of the server's ingest script for this kind of data.</summary>
        [Description("The name of the server's ingest script for this kind of data, as the server's registry has it. Blank: the server runs nothing but the copy.")]
        public string IngestScript
        {
            get { return Settings.IngestScript; }
            set { Settings.IngestScript = value; }
        }

        /// <summary>Gets or sets the configuration files the workflow runs with, separated by ;.</summary>
        [Description("The configuration files the workflow runs with, separated by ;. Each is read once at Start and recorded in the manifest with its sha256, so the manifest has them as loaded. Environment variables are expanded (%COMPUTERNAME%), and {SubjectId} is the launch's --property:SubjectId (a mouse's own file); a folder is every file in it; a relative path is from the workflow's repository. A missing file is noted, not an error.")]
        public string ConfigFiles
        {
            get { return Settings.ConfigFiles; }
            set { Settings.ConfigFiles = value; }
        }

        /// <summary>Gets or sets what the experiment must leave, separated by ;, or auto.</summary>
        [Description("What the experiment must leave, separated by ;: a path in the experiment folder, a folder ending in /, or the start of a file's path ending in _ (Schemas/Schemas_Task_), met by any file whose path starts with it. auto: a folder for each device in rig.yml (from ConfigFiles) whose subject the workflow publishes before this node, as <Kind>-<Instance> or <Kind>-<Instance>.Data, which is where OpenRigs.Logging logs it. harp: for each Harp device in rig.yml (a portName, <Kind>-<Instance>.Data published as HarpMessage), the <Name>_device.json and <Name>.yml OpenRigs.HarpDevices' RecordHarpDevices writes in that folder. rig-sync alarms on an entry that left no file. Blank: nothing expected.")]
        public string ExpectedFiles
        {
            get { return Settings.ExpectedFiles; }
            set { Settings.ExpectedFiles = value; }
        }

        /// <summary>Gets or sets the workflow's trial columns file, copied into the manifest at Start.</summary>
        [Description("The workflow's trial columns file (schemas\\trial-columns.yml): what each BonsaiData_Trial column means, its type, and where they apply its values or codes, unit and former names. Read once at Start and copied into the manifest as trial_columns, so the experiment carries the meaning of its trial log. A relative path is from the workflow's repository. Blank: the workflow logs no trials. A missing or unreadable file is noted, not an error.")]
        public string TrialColumns
        {
            get { return Settings.TrialColumns; }
            set { Settings.TrialColumns = value; }
        }

        ExperimentManifestSettings Settings
        {
            get { return GroupNode.Settings<ExperimentManifestSettings>(Workflow); }
        }

        /// <summary>Gets the range of inputs: none, or one, the run.</summary>
        public override Range<int> ArgumentRange
        {
            get { return Range.Create(0, 1); }
        }

        /// <summary>
        /// Builds the recorder on PathPrefix and the run. The inner workflow holds only the
        /// settings and is not built: the node subscribes to PathPrefix itself, as
        /// SubscribeSubject does, through the enclosing build context.
        /// </summary>
        /// <param name="arguments">The run, if one is connected.</param>
        /// <returns>The manifest's path, once it is written.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var path = GroupNode.Subject(this, PathSubject);
            if (path == null)
            {
                throw new InvalidOperationException("ExperimentManifest found no " + PathSubject + " subject. It names the experiment folder and comes from OpenRigs.Logging's LogController: put ExperimentManifest after LogController, at the same level or below it.");
            }
            var pathType = GroupNode.ObservedType(path.Type);
            if (pathType != typeof(string))
            {
                throw new InvalidOperationException("ExperimentManifest needs " + PathSubject + " to be a string, the experiment folder's path; this workflow's is " + pathType + ".");
            }
            var expected = ExpectedPlan.Create(ExpectedFiles, ManifestWriter.RigConfigPath(ConfigFiles), name => GroupNode.PublishedType(this, name));
            var recorder = Expression.Constant(new ManifestRecorder(IngestScript, ConfigFiles, expected, TrialColumns));
            var observablePath = Expression.Convert(path, typeof(IObservable<string>));
            var run = arguments.FirstOrDefault();
            if (run == null)
            {
                return Expression.Call(recorder, nameof(ManifestRecorder.Process), null, observablePath);
            }
            var runType = GroupNode.ObservedType(run.Type);
            return Expression.Call(recorder, nameof(ManifestRecorder.Process), new[] { runType }, observablePath, run);
        }

    }

    /// <summary>
    /// ExperimentManifest's settings, kept as a node in its inner workflow so that they survive
    /// Bonsai's copies of the group. Set them on ExperimentManifest; this node builds to nothing.
    /// </summary>
    [Description("ExperimentManifest's settings (IngestScript, ConfigFiles, ExpectedFiles, TrialColumns), kept here so Bonsai's copies of the group keep them. Set them on ExperimentManifest.")]
    public class ExperimentManifestSettings : ZeroArgumentExpressionBuilder
    {
        /// <summary>Gets or sets ExperimentManifest's IngestScript.</summary>
        public string IngestScript { get; set; }

        /// <summary>Gets or sets ExperimentManifest's ConfigFiles.</summary>
        public string ConfigFiles { get; set; } = @"C:\Coen-Lab-Local\RigConfigs\%COMPUTERNAME%\rig.yml";

        /// <summary>Gets or sets ExperimentManifest's ExpectedFiles.</summary>
        public string ExpectedFiles { get; set; } = ExpectedPlan.Auto;

        /// <summary>Gets or sets ExperimentManifest's TrialColumns.</summary>
        public string TrialColumns { get; set; }

        /// <summary>Builds to an empty sequence: the node only holds the settings.</summary>
        /// <param name="arguments">None.</param>
        /// <returns>An empty sequence.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            return Expression.Constant(Observable.Empty<Unit>(), typeof(IObservable<Unit>));
        }
    }

    // ExperimentManifest's own settings in the Properties panel: its inner workflow externalizes nothing.
    class ExperimentManifestTypeDescriptionProvider : ShownPropertiesProvider
    {
        public ExperimentManifestTypeDescriptionProvider()
            : base("IngestScript", "ConfigFiles", "ExpectedFiles", "TrialColumns")
        {
        }
    }
}
