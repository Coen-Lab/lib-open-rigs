using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenRigs.Shared;

namespace OpenRigs.HarpDevices
{
    /// <summary>One Harp device RecordHarpDevices records, as rig.yml names it.</summary>
    class PlannedHarpDevice
    {
        public string Name;        // BehaviorBoard-Main
        public string Entry;       // rig.yml behaviorBoard: main:
        public string Kind;        // behaviorBoard
        public string PortName;    // COM3, from rig.yml
        public string UsbSerial;   // from rig.yml, null when it has none
        public int ExpectedWhoAmI; // 0 when the kind is not in the table
        public bool HasCommands;   // the workflow publishes <Name>.Commands
        public string Wiring;        // what the wiring check found (WiringCheck), for the record; null for a board it does not cover
        public string WiringRefusal; // every disagreement, which stops the workflow at Start; null when none

        public override string ToString()
        {
            return string.Join("|", Name, Kind, PortName, UsbSerial ?? "-", ExpectedWhoAmI, HasCommands, Wiring ?? "-", WiringRefusal ?? "-");
        }
    }

    /// <summary>
    /// The Harp devices RecordHarpDevices records, decided at build from rig.yml and the subjects
    /// the workflow publishes: every device entry with a portName whose &lt;Name&gt;.Data the
    /// workflow publishes as HarpMessage. Never throws: the editor builds a workflow on open, on
    /// any desk. What is wrong with an entry (no usbSerial) stops the workflow at Start, on the
    /// rig, where it can be put right.
    /// </summary>
    class HarpDevicePlan
    {
        /// <summary>The subject each device's commands go to, after its name: BehaviorBoard-Main.Commands.</summary>
        public const string CommandsSuffix = ".Commands";

        /// <summary>The subject each device's record's path is published as once written: BehaviorBoard-Main.Identity.</summary>
        public const string IdentitySuffix = ".Identity";

        public readonly List<PlannedHarpDevice> Recorded = new List<PlannedHarpDevice>();
        public readonly List<string> Notes = new List<string>();
        YamlDotNet.RepresentationModel.YamlMappingNode rig;
        List<string> deviceNames;
        string lines;
        Exception linesUnreadable;
        string workflow;

        /// <summary>Why the node records nothing and stops the workflow at Start, or null: no PathPrefix before it, which the manifest's harp files would wait for every night.</summary>
        public string StartRefusal;

        // The repository of the workflow running, found as OpenRigs.Manifest and LiveView find it.
        static string WorkflowRepository()
        {
            var full = WorkflowFile.Find(Environment.GetCommandLineArgs(), Environment.CurrentDirectory);
            return Git.FindRoot(WorkflowFile.Folder(full, Environment.CurrentDirectory));
        }

        public static HarpDevicePlan Create(string configRoot, Func<string, Type> publishedType, Func<string> repositoryRoot = null)
        {
            var plan = new HarpDevicePlan();
            if (publishedType("PathPrefix") != typeof(string))
            {
                plan.Notes.Add("the workflow publishes no PathPrefix (OpenRigs.Logging's LogController) before this node, so there is no experiment folder to record into");
                plan.StartRefusal = "RecordHarpDevices records nothing: " + plan.Notes[0] + ". Put it after LogController and the devices.";
                return plan;
            }
            if (string.IsNullOrWhiteSpace(configRoot))
            {
                plan.Notes.Add("ConfigRoot is not set, so no rig.yml was read");
                return plan;
            }
            string problem;
            var devices = RigDevices.Read(RigDevices.PathFor(configRoot), out problem);
            if (devices == null)
            {
                plan.Notes.Add(problem);
                return plan;
            }
            // The wiring check's inputs, read once: rig.yml's top level and the workflow's board-lines.yml.
            plan.rig = WiringCheck.ReadRig(RigDevices.PathFor(configRoot));
            plan.deviceNames = devices.Select(d => d.Name).ToList();
            // A failed read refuses the boards it would check, as a file that does not parse does (L2-L2).
            try
            {
                plan.lines = WiringCheck.ReadLines((repositoryRoot ?? WorkflowRepository)(), out plan.workflow);
            }
            catch (Exception ex)
            {
                plan.linesUnreadable = ex;
            }
            foreach (var device in devices)
            {
                plan.Consider(device, publishedType);
            }
            return plan;
        }

        // Which entries are Harp devices is HarpRig's rule, shared with OpenRigs.Manifest, so the
        // files it expects are the ones recorded here.
        void Consider(RigDevice device, Func<string, Type> publishedType)
        {
            var notHarp = HarpRig.NotHarp(device, publishedType);
            if (notHarp != null)
            {
                if (notHarp.Length > 0) Notes.Add(device.Name + ": " + notHarp);
                return;
            }
            var whoAmI = HarpRig.WhoAmI(device.Kind);
            var commands = publishedType(device.Name + CommandsSuffix);
            if (commands == null) Notes.Add(device.Name + ": the workflow publishes no " + device.Name + CommandsSuffix + ", so no register dump is asked for at the experiment's start");
            else if (commands.FullName != HarpRig.MessageType) Notes.Add(device.Name + ": " + device.Name + CommandsSuffix + " is " + commands.Name + ", not HarpMessage, so no register dump is asked for");
            if (whoAmI == 0) Notes.Add(device.Name + ": no WhoAmI is known for " + device.Kind + ", so the board's type is recorded, not checked");
            string wiring = null, refusal = null;
            if (device.Kind == "behaviorBoard")
            {
                var result = rig == null
                    ? new WiringResult { Summary = "not checked: rig.yml's top level could not be read" }
                    : linesUnreadable != null ? WiringCheck.Unreadable(linesUnreadable)
                    : WiringCheck.Check(rig, device, deviceNames, lines, workflow);
                wiring = result.Summary;
                refusal = result.Refusal(device.Name);
                if (refusal != null) Notes.Add(device.Name + ": " + result.Summary + " (the workflow stops at Start)");
            }
            Recorded.Add(new PlannedHarpDevice
            {
                Wiring = wiring,
                WiringRefusal = refusal,
                Name = device.Name,
                Entry = "rig.yml " + device.Kind + ": " + device.Instance + ":",
                Kind = device.Kind,
                PortName = HarpRig.Setting(device, "portName"),
                UsbSerial = HarpRig.Setting(device, "usbSerial"),
                ExpectedWhoAmI = whoAmI,
                HasCommands = commands != null && commands.FullName == HarpRig.MessageType,
            });
        }

        /// <summary>Everything the inner workflow is made from, so it is remade only when this changes.</summary>
        public string Signature()
        {
            var text = new StringBuilder();
            foreach (var device in Recorded) text.Append("\n+").Append(device);
            foreach (var note in Notes) text.Append("\n!").Append(note);
            return text.ToString();
        }
    }
}
