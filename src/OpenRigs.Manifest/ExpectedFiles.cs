using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRigs.Shared;

namespace OpenRigs.Manifest
{
    /// <summary>One file or folder the experiment must leave: a path in the experiment folder, a folder ending in /,
    /// or a file name's start ending in _ (Settings/Settings_Task_), met by a file whose path starts with it.</summary>
    class ExpectedEntry
    {
        public string Path;
        public string Why;
    }

    /// <summary>
    /// What the experiment must leave, decided at build as StreamDevices decides what to stream: for
    /// each device entry in rig.yml (kind: instance:, named &lt;Kind&gt;-&lt;Instance&gt;) whose subject
    /// the workflow publishes before this node, as &lt;Name&gt; or &lt;Name&gt;.Data, a folder of that
    /// name holding at least one file, which is where OpenRigs.Logging logs a device. Never
    /// throws: the editor builds a workflow on open, on any desk.
    /// </summary>
    class ExpectedPlan
    {
        public const string Auto = "auto";

        /// <summary>
        /// ExpectedFiles' word for the Harp devices' records: for each Harp device in rig.yml (HarpRig:
        /// a portName, its &lt;Name&gt;.Data published as HarpMessage), the files OpenRigs.HarpDevices'
        /// RecordHarpDevices writes beside its data (0.1.0-local.8): the record, and the register map
        /// when a loaded Harp package decodes the kind's WhoAmI (0.1.0-local.9). Decided by the same
        /// shared rule as RecordHarpDevices, from rig.yml and the devices' subjects, not from
        /// RecordHarpDevices itself: Bonsai may build this node before that one, whatever their
        /// order in the workflow (a node fed from one built earlier is built sooner).
        /// </summary>
        public const string Harp = "harp";

        public readonly List<ExpectedEntry> Entries = new List<ExpectedEntry>();
        public readonly List<string> Notes = new List<string>();

        /// <param name="expectedFiles">ExpectedFiles: entries separated by ;, auto for the devices'.</param>
        /// <param name="rigConfig">The rig.yml among ConfigFiles, its path resolved; null if there is none.</param>
        /// <param name="publishedType">The type a subject of this name is published as, or null.</param>
        public static ExpectedPlan Create(string expectedFiles, string rigConfig, Func<string, Type> publishedType)
        {
            var plan = new ExpectedPlan();
            foreach (var raw in (expectedFiles ?? "").Split(';'))
            {
                var entry = raw.Trim().Replace('\\', '/');
                // rig-sync matches paths relative to the experiment folder (RigSchema/x.json), so ./ is dropped.
                while (entry.StartsWith("./")) entry = entry.Substring(2);
                if (entry.Length == 0) continue;
                if (string.Equals(entry, Auto, StringComparison.OrdinalIgnoreCase)) plan.AddDevices(rigConfig, publishedType, false);
                else if (string.Equals(entry, Harp, StringComparison.OrdinalIgnoreCase)) plan.AddDevices(rigConfig, publishedType, true);
                // rig-sync matches a path or a folder, never a pattern: a wildcard would match nothing and hold the experiment back.
                else if (entry.IndexOfAny(new[] { '*', '?' }) >= 0) plan.Notes.Add("ExpectedFiles entry " + entry + " is not expected: a wildcard is not a path. Name the folder (Camera-Box/) instead.");
                else if (entry.StartsWith("/") || entry.Contains(":") || entry.Split('/').Contains(".."))
                    plan.Notes.Add("ExpectedFiles entry " + entry + " is not expected: it is not a path inside the experiment folder.");
                else plan.Add(entry, "listed in ExpectedFiles");
            }
            return plan;
        }

        void Add(string path, string why)
        {
            if (Entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            Entries.Add(new ExpectedEntry { Path = path, Why = why });
        }

        // A folder for each device (auto), or each Harp device's record and register map (harp).
        void AddDevices(string rigConfig, Func<string, Type> publishedType, bool harp)
        {
            if (rigConfig == null)
            {
                Notes.Add("ExpectedFiles is " + (harp ? Harp : Auto) + " but ConfigFiles names no rig.yml, so no device's files are expected.");
                return;
            }
            string problem;
            var devices = RigDevices.Read(rigConfig, out problem);
            if (devices == null)
            {
                Notes.Add(char.ToUpperInvariant(problem[0]) + problem.Substring(1) + " when the workflow was built, so no device's files are expected.");
                return;
            }
            foreach (var device in devices)
            {
                var name = device.Name;
                var entry = "rig.yml " + device.Kind + ": " + device.Instance + ":";
                if (harp)
                {
                    // HarpRig's rule, as RecordHarpDevices decides it (0.1.0-local.9): a portName
                    // that is not null, and the register map only when a loaded package decodes the kind.
                    if (HarpRig.NotHarp(device, publishedType) != null) continue;
                    var why = entry + " is a Harp device (" + name + ".Data is HarpMessage): RecordHarpDevices' ";
                    Add(name + "/" + name + "_device.json", why + "record");
                    string noMap;
                    if (HarpRig.RegisterMapFor(device.Kind, out noMap) != null) Add(name + "/" + name + ".yml", why + "register map");
                    else Notes.Add(name + "/" + name + ".yml (" + entry + ") is not expected: " + noMap + ", so RecordHarpDevices writes no register map.");
                    continue;
                }
                var published = publishedType(name) != null ? name : publishedType(name + ".Data") != null ? name + ".Data" : null;
                if (published == null)
                {
                    Notes.Add(name + " (" + entry + ") is not expected: the workflow publishes no " + name + " or " + name + ".Data before ExperimentManifest.");
                    continue;
                }
                Add(name + "/", entry + ", published as " + published);
            }
        }

        /// <summary>
        /// The files under the experiment folder an entry matches: the file itself, every file whose path starts
        /// with a prefix entry (ending in _, 0.1.1-coen.5: each of the three settings files, in Schemas/ until coen.7
        /// and Settings/ since, expected on its own, as one folder entry was met by any one of them), or every file in the folder that no other
        /// entry names (0.1.0-local.7), so a device's register map, expected by name in the device's folder,
        /// never stands in for the device's data.
        /// </summary>
        public static int Matches(ExpectedEntry entry, IEnumerable<string> relativePaths, IEnumerable<ExpectedEntry> entries)
        {
            if (entry.Path.EndsWith("_", StringComparison.Ordinal))
            {
                return relativePaths.Count(p => p.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase));
            }
            if (!entry.Path.EndsWith("/", StringComparison.Ordinal))
            {
                return relativePaths.Count(p => string.Equals(p, entry.Path, StringComparison.OrdinalIgnoreCase));
            }
            var named = entries.Where(e => !e.Path.EndsWith("/", StringComparison.Ordinal) && !e.Path.EndsWith("_", StringComparison.Ordinal)).Select(e => e.Path).ToList();
            return relativePaths.Count(p => p.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase)
                && !named.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

    }
}
