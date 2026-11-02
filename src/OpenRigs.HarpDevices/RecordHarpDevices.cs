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

namespace OpenRigs.HarpDevices
{
    /// <summary>
    /// Every Harp device on a rig checked and recorded, in one node, with nothing typed per
    /// device. At build it reads this machine's rig.yml and, for each device entry with a
    /// portName whose data the workflow publishes as HarpMessage under &lt;Kind-Instance&gt;.Data,
    /// adds to its own inner workflow a RecordHarpDevice (the board's USB serial checked at Start;
    /// its registers and register map written into each session) and, when the workflow publishes
    /// &lt;Kind-Instance&gt;.Commands, a RequestRegisterDump on it. Each device's record's path is
    /// published as &lt;Kind-Instance&gt;.Identity. Only subjects built before this node can be
    /// found, so it goes after the devices and LogController. SessionManifest expects the two files
    /// with harp in its ExpectedFiles.
    /// </summary>
    [DefaultProperty(nameof(ConfigRoot))]
    [TypeDescriptionProvider(typeof(RecordHarpDevicesTypeDescriptionProvider))]
    [Description("Checks every Harp device in this machine's rig.yml at Start (its USB serial, usbSerial in rig.yml) and writes its registers and register map into each session folder, as <Name>/<Name>_device.json and <Name>/<Name>.yml. Finds each device as <Kind-Instance>.Data and asks for a register dump on <Kind-Instance>.Commands. Put it after the devices and LogController; give SessionManifest's ExpectedFiles harp to expect the files.")]
    public class RecordHarpDevices : GroupWorkflowBuilder
    {
        // The plan the inner workflow was last made from. The editor builds on every change, so
        // the inner workflow is only remade when the plan differs.
        string generatedFrom;

        /// <summary>Initializes a new instance of the <see cref="RecordHarpDevices"/> class.</summary>
        public RecordHarpDevices()
        {
        }

        /// <summary>
        /// Initializes a copy with the given inner workflow. Bonsai copies a group by this
        /// constructor (the editor's working copy, the player's when it shows a layout, every
        /// save), passing only the Name, Description and inner workflow, so the settings live in
        /// that workflow (RecordHarpDevicesSettings), as StreamDevices' do.
        /// </summary>
        /// <param name="workflow">The inner workflow to copy.</param>
        public RecordHarpDevices(ExpressionBuilderGraph workflow)
            : base(workflow)
        {
        }

        /// <summary>Gets or sets the root under which each machine has its own configuration directory.</summary>
        [Category("Config")]
        [Description("The root under which each machine has its own configuration directory, <ConfigRoot>\\<MACHINE-NAME>\\rig.yml. Read when the workflow is built; a missing file records no devices.")]
        public string ConfigRoot
        {
            get { return Settings.ConfigRoot; }
            set { Settings.ConfigRoot = value; }
        }

        RecordHarpDevicesSettings Settings
        {
            get { return GroupNode.Settings<RecordHarpDevicesSettings>(Workflow); }
        }

        /// <summary>Remakes the inner workflow if rig.yml or the subjects published have changed, then builds it.</summary>
        /// <param name="arguments">None: the node has no inputs.</param>
        /// <returns>The inner workflow's expression.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var plan = HarpDevicePlan.Create(ConfigRoot, name => GroupNode.PublishedType(this, name));
            var signature = plan.Signature();
            if (signature != generatedFrom)
            {
                Generate(plan);
                generatedFrom = signature;
            }
            return base.Build(arguments);
        }

        // As StreamDevices: the inner workflow is one group, HarpDevices, swapped whole for a new
        // one when the plan changes (emptying it mid-build stops the build), and made inspectable
        // in the editor's copy, where every node is wrapped in an InspectBuilder.
        void Generate(HarpDevicePlan plan)
        {
            var content = new ExpressionBuilderGraph();
            foreach (var device in plan.Recorded)
            {
                content.Add(Group(device.Name, Summary(device), Device(device)));
            }
            var workflow = Workflow;
            var settings = Settings;
            var inspectable = workflow.Any(node => node.Value is InspectBuilder);
            foreach (var node in workflow.ToList())
            {
                if (GroupNode.Unwrap(node.Value) != settings) workflow.Remove(node);
            }
            var description = "Made from this machine's rig.yml each time the workflow is built; edits here are lost." +
                (plan.Notes.Count > 0 ? " Notes: " + string.Join("; ", plan.Notes) + "." : "");
            var group = new ExpressionBuilderGraph();
            group.Add(Group("HarpDevices", description, content));
            if (inspectable) group = group.ToInspectableGraph();
            workflow.Add(group.Single().Value);
        }

        static string Summary(PlannedHarpDevice device)
        {
            return device.Entry + " on " + device.PortName + ", USB serial " + (device.UsbSerial ?? "not in rig.yml (the workflow stops at Start)") +
                (device.ExpectedWhoAmI != 0 ? ", WhoAmI " + device.ExpectedWhoAmI : ", WhoAmI unchecked") +
                (device.HasCommands ? "; a register dump asked for on " + device.Name + HarpDevicePlan.CommandsSuffix + "." : "; no " + device.Name + HarpDevicePlan.CommandsSuffix + ", so no dump is asked for.");
        }

        // <Name>.Data and PathPrefix into RecordHarpDevice, published as <Name>.Identity; and into
        // RequestRegisterDump, sent on <Name>.Commands.
        static ExpressionBuilderGraph Device(PlannedHarpDevice device)
        {
            var graph = new ExpressionBuilderGraph();
            var data = graph.Add(new SubscribeSubject { Name = device.Name + ".Data" });
            var path = graph.Add(new SubscribeSubject { Name = "PathPrefix" });
            var record = graph.Add(new CombinatorBuilder
            {
                Combinator = new RecordHarpDevice
                {
                    Name = device.Name,
                    Entry = device.Entry,
                    PortName = device.PortName,
                    UsbSerial = device.UsbSerial,
                    ExpectedWhoAmI = device.ExpectedWhoAmI,
                    HasCommands = device.HasCommands,
                },
            });
            var identity = graph.Add(new Bonsai.Reactive.PublishSubject { Name = device.Name + HarpDevicePlan.IdentitySuffix });
            graph.AddEdge(data, record, new ExpressionBuilderArgument(0));
            graph.AddEdge(path, record, new ExpressionBuilderArgument(1));
            graph.AddEdge(record, identity, new ExpressionBuilderArgument(0));
            if (device.HasCommands)
            {
                var dump = graph.Add(new CombinatorBuilder { Combinator = new RequestRegisterDump() });
                var commands = graph.Add(new MulticastSubject { Name = device.Name + HarpDevicePlan.CommandsSuffix });
                graph.AddEdge(data, dump, new ExpressionBuilderArgument(0));
                graph.AddEdge(path, dump, new ExpressionBuilderArgument(1));
                graph.AddEdge(dump, commands, new ExpressionBuilderArgument(0));
            }
            return graph;
        }

        static GroupWorkflowBuilder Group(string name, string description, ExpressionBuilderGraph workflow)
        {
            return new GroupWorkflowBuilder(workflow) { Name = name, Description = description };
        }
    }

    /// <summary>
    /// RecordHarpDevices' settings, kept as a node in its inner workflow so that they survive
    /// Bonsai's copies of the group. Set them on RecordHarpDevices; this node builds to nothing.
    /// </summary>
    [Description("RecordHarpDevices' settings (ConfigRoot), kept here so Bonsai's copies of the group keep them. Set them on RecordHarpDevices.")]
    public class RecordHarpDevicesSettings : ZeroArgumentExpressionBuilder
    {
        /// <summary>Gets or sets RecordHarpDevices' ConfigRoot.</summary>
        public string ConfigRoot { get; set; } = @"C:\Coen-Lab-Local\RigConfigs";

        /// <summary>Builds to an empty sequence: the node only holds the settings.</summary>
        /// <param name="arguments">None.</param>
        /// <returns>An empty sequence.</returns>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            return Expression.Constant(Observable.Empty<Unit>(), typeof(IObservable<Unit>));
        }
    }

    // RecordHarpDevices' own settings in the Properties panel: its inner workflow is generated and externalizes nothing.
    class RecordHarpDevicesTypeDescriptionProvider : ShownPropertiesProvider
    {
        public RecordHarpDevicesTypeDescriptionProvider()
            : base("ConfigRoot")
        {
        }
    }
}
