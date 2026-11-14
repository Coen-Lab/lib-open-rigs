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

        public override string ToString()
        {
            return string.Join("|", Name, Kind, PortName, UsbSerial ?? "-", ExpectedWhoAmI, HasCommands);
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

        /// <summary>Why the node stops the workflow at Start, or null: no PathPrefix before it, which the manifest's harp files would wait for every night, or an empty rig.yml entry that hides its kind's devices.</summary>
        public string StartRefusal;

        public static HarpDevicePlan Create(string configRoot, Func<string, Type> publishedType)
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
            // An empty entry hides its whole kind from RigDevices.Read: refused at Start, as a missing usbSerial is.
            var empty = EmptyRigEntries.Find(RigDevices.PathFor(configRoot));
            if (empty.Count > 0)
            {
                plan.Notes.AddRange(empty);
                plan.StartRefusal = "RecordHarpDevices stops the workflow: " + string.Join("; ", empty) +
                    " and none is checked or recorded. Give each empty entry its settings in rig.yml, or delete it.";
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
            Recorded.Add(new PlannedHarpDevice
            {
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
