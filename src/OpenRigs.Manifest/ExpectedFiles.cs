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
        /// <summary>The bool subject the entry is required by (path?Subject), or null when it is always required.</summary>
        public string Condition;
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

        // Each condition's last value from its subject; absent until the subject has sent one.
        readonly Dictionary<string, bool> conditionValues = new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>The conditions the entries name, each once.</summary>
        public List<string> Conditions
        {
            get { return Entries.Where(e => e.Condition != null).Select(e => e.Condition).Distinct(StringComparer.Ordinal).ToList(); }
        }

        public void ClearConditions()
        {
            lock (conditionValues) conditionValues.Clear();
        }

        public void SetCondition(string name, bool value)
        {
            lock (conditionValues) conditionValues[name] = value;
        }

        /// <summary>Whether the entry must leave a file now: always without a condition, else while its subject's last value is true.</summary>
        public bool Required(ExpectedEntry entry)
        {
            if (entry.Condition == null) return true;
            bool value;
            lock (conditionValues) return conditionValues.TryGetValue(entry.Condition, out value) && value;
        }

        /// <summary>A condition the workflow does not publish as a bool subject: its entries are required always, which the overnight copy then holds them to.</summary>
        public void Unconditional(string condition, string why)
        {
            var paths = Entries.Where(e => e.Condition == condition).Select(e => e.Path).ToList();
            foreach (var entry in Entries.Where(e => e.Condition == condition)) entry.Condition = null;
            Note("Condition " + condition + " " + why + ", so " + string.Join(", ", paths) + " " + (paths.Count == 1 ? "is" : "are") + " expected always.");
        }

        /// <summary>At each write: the conditions whose subject has sent no value yet, so their entries are not required.</summary>
        public List<string> ConditionNotes()
        {
            var notes = new List<string>();
            foreach (var condition in Conditions)
            {
                bool value;
                bool known;
                lock (conditionValues) known = conditionValues.TryGetValue(condition, out value);
                if (!known) notes.Add("Condition " + condition + " has sent no value, so " + string.Join(", ", Entries.Where(e => e.Condition == condition).Select(e => e.Path)) + " not required.");
            }
            return notes;
        }

        /// <param name="expectedFiles">ExpectedFiles: entries separated by ;, auto for the devices'; path?Subject is required only while the bool subject Subject is true.</param>
        /// <param name="rigConfig">The rig.yml among ConfigFiles, its path resolved; null if there is none.</param>
        /// <param name="publishedType">The type a subject of this name is published as, or null.</param>
        public static ExpectedPlan Create(string expectedFiles, string rigConfig, Func<string, Type> publishedType)
        {
            var plan = new ExpectedPlan();
            foreach (var raw in (expectedFiles ?? "").Split(';'))
            {
                var entry = raw.Trim().Replace('\\', '/');
                // The overnight copy matches paths relative to the experiment folder, so ./ is dropped.
                while (entry.StartsWith("./")) entry = entry.Substring(2);
                if (entry.Length == 0) continue;
                // path?Subject: required only while the bool subject is true (the workflow decides, the manifest records required).
                string condition = null;
                var mark = entry.LastIndexOf('?');
                if (mark >= 0)
                {
                    condition = entry.Substring(mark + 1).Trim();
                    entry = entry.Substring(0, mark).Trim();
                    if (condition.Length == 0 || entry.Length == 0 || entry.IndexOfAny(new[] { '*', '?' }) >= 0
                        || string.Equals(entry, Auto, StringComparison.OrdinalIgnoreCase) || string.Equals(entry, Harp, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.Note("ExpectedFiles entry " + raw.Trim() + " is not expected: a condition is a path, ?, then the name of a bool subject (Settings/Settings_Laser_?Optogenetics).");
                        continue;
                    }
                }
                if (string.Equals(entry, Auto, StringComparison.OrdinalIgnoreCase)) plan.AddDevices(rigConfig, publishedType, false);
                else if (string.Equals(entry, Harp, StringComparison.OrdinalIgnoreCase)) plan.AddDevices(rigConfig, publishedType, true);
                // The overnight copy matches a path or a folder, never a pattern: a wildcard would match nothing and hold the experiment back.
                else if (entry.IndexOfAny(new[] { '*', '?' }) >= 0) plan.Note("ExpectedFiles entry " + entry + " is not expected: a wildcard is not a path. Name the folder (Camera-Overhead/) instead.");
                else if (entry.StartsWith("/") || entry.Contains(":") || entry.Split('/').Contains(".."))
                    plan.Note("ExpectedFiles entry " + entry + " is not expected: it is not a path inside the experiment folder.");
                else plan.Add(entry, "listed in ExpectedFiles", condition);
            }
            return plan;
        }

        // Each note once: auto;harp, as every lab workflow sets, reads rig.yml twice (audit L1-L1).
        void Note(string note)
        {
            if (!Notes.Contains(note)) Notes.Add(note);
        }

        void Add(string path, string why, string condition = null)
        {
            if (Entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            Entries.Add(new ExpectedEntry { Path = path, Why = why, Condition = condition });
        }

        // A folder for each device (auto), or each Harp device's record and register map (harp).
        void AddDevices(string rigConfig, Func<string, Type> publishedType, bool harp)
        {
            if (rigConfig == null)
            {
                Note("ExpectedFiles is " + (harp ? Harp : Auto) + " but ConfigFiles names no rig.yml, so no device's files are expected.");
                return;
            }
            string problem;
            var devices = RigDevices.Read(rigConfig, out problem);
            if (devices == null)
            {
                Note(char.ToUpperInvariant(problem[0]) + problem.Substring(1) + " when the workflow was built, so no device's files are expected.");
                return;
            }
            // An empty entry hides its whole kind from RigDevices.Read (audit L2-L1).
            foreach (var empty in EmptyRigEntries.Find(rigConfig))
                Note(char.ToUpperInvariant(empty[0]) + empty.Substring(1) + " and none of its files is expected.");
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
                    else Note(name + "/" + name + ".yml (" + entry + ") is not expected: " + noMap + ", so RecordHarpDevices writes no register map.");
                    continue;
                }
                var published = publishedType(name) != null ? name : publishedType(name + ".Data") != null ? name + ".Data" : null;
                if (published == null)
                {
                    Note(name + " (" + entry + ") is not expected: the workflow publishes no " + name + " or " + name + ".Data before ExperimentManifest.");
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
