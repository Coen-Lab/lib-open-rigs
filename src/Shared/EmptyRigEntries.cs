using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Shared
{
    /// <summary>
    /// The device kinds RigDevices.Read passes over because one instance is left empty
    /// (behaviorBoard: spare: with nothing under it) beside instances with settings: by its shape
    /// rule the whole kind is then not devices, so every one of its devices would go unrecorded,
    /// unexpected and unstreamed without a word (audit L2-L1, 14 Nov 2026). Kept apart from
    /// RigDevices so the packages that compile only that file are unchanged.
    /// </summary>
    internal static class EmptyRigEntries
    {
        /// <summary>
        /// One phrase per such kind, naming the empty entries: "the rig.yml entry behaviorBoard: spare:
        /// has no settings, so every behaviorBoard is passed over". Empty when there is none or the file
        /// cannot be read (RigDevices.Read says why). A kind whose other values include a list or
        /// a scalar with a value (speakerArray's syncChannel) is settings, not devices, and is never named.
        /// </summary>
        public static List<string> Find(string path)
        {
            var found = new List<string>();
            YamlMappingNode root;
            try
            {
                if (!File.Exists(path)) return found;
                var yaml = new YamlStream();
                using (var reader = new StreamReader(path)) yaml.Load(reader);
                root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            }
            catch (Exception)
            {
                return found;
            }
            if (root == null) return found;
            foreach (var kind in root.Children)
            {
                var kindName = RigDevices.Scalar(kind.Key);
                var instances = kind.Value as YamlMappingNode;
                if (kindName == null || instances == null) continue;
                var empty = instances.Children.Where(i => !(i.Value is YamlMappingNode)).ToList();
                if (empty.Count == 0 || empty.Count == instances.Children.Count ||
                    empty.Any(i => !(i.Value is YamlScalarNode) || RigDevices.Value(i.Value) != null)) continue;
                found.Add("the rig.yml " + (empty.Count == 1 ? "entry " : "entries ") +
                    string.Join(", ", empty.Select(i => kindName + ": " + RigDevices.Scalar(i.Key) + ":")) +
                    (empty.Count == 1 ? " has" : " have") + " no settings, so every " + kindName + " is passed over");
            }
            return found;
        }
    }
}
