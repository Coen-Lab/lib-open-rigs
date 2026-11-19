using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRigs.Shared;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// A WorkflowData log's columns file (TrialColumns, such as schemas\trial-columns.yml, and each of
    /// StreamColumns): what each column the log holds means, by name: type (int, double, bool or string),
    /// and where they apply values (the strings it may hold, or a code: meaning map), unit and
    /// description. Read once at Start and copied into the manifest, so each experiment carries the
    /// meaning of its own logs.
    /// </summary>
    static class TrialColumns
    {
        static readonly string[] Types = { "int", "double", "bool", "string" };

        /// <summary>StreamColumns' Log=path entries, separated by ;, in order; throws on an entry not Log=path or two logs whose names differ only in case.</summary>
        public static List<KeyValuePair<string, string>> Streams(string configured)
        {
            var streams = new List<KeyValuePair<string, string>>();
            foreach (var entry in (configured ?? "").Split(';').Select(e => e.Trim()).Where(e => e.Length > 0))
            {
                var equals = entry.IndexOf('=');
                var log = equals > 0 ? entry.Substring(0, equals).Trim() : "";
                var path = equals > 0 ? entry.Substring(equals + 1).Trim() : "";
                if (log.Length == 0 || path.Length == 0) throw new InvalidOperationException("ExperimentManifest's StreamColumns entry '" + entry + "' is not Log=path (WorkflowData_StimulusState=schemas\\stimulus-state-columns.yml).");
                var clash = streams.FirstOrDefault(s => string.Equals(s.Key, log, StringComparison.OrdinalIgnoreCase));
                if (clash.Key != null) throw new InvalidOperationException("ExperimentManifest's StreamColumns names " + clash.Key + " and " + log + ", which differ only in case or not at all.");
                streams.Add(new KeyValuePair<string, string>(log, path));
            }
            return streams;
        }

        /// <summary>A columns file's section: path, sha256 and columns; or path, missing and why. Null when blank.</summary>
        public static JsonObject Read(string configured, string baseFolder, out string note, string what = "Trial columns")
        {
            note = null;
            if (string.IsNullOrWhiteSpace(configured)) return null;
            var full = ManifestWriter.ResolvePath(configured.Trim(), baseFolder);
            var section = new JsonObject();
            section.Add("path", full);
            try
            {
                var bytes = File.ReadAllBytes(full);
                var columns = Columns(bytes);
                section.Add("sha256", ManifestWriter.Sha256Hex(bytes));
                section.Add("columns", columns);
            }
            catch (Exception ex)
            {
                section.Add("missing", true);
                section.Add("why", ex.Message);
                note = what + " not read at Start from " + configured + ": " + ex.Message;
            }
            return section;
        }

        /// <summary>Each column by name, as written in the file; throws naming the column whose type is missing or not one of the four, or two keys differing only in case.</summary>
        public static JsonObject Columns(byte[] bytes)
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(new MemoryStream(bytes))) yaml.Load(reader);
            var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            if (root == null) throw new InvalidDataException("the file is empty or not a map of columns");
            var columns = new JsonObject();
            RefuseCaseOnly(root, "columns");
            foreach (var pair in root.Children)
            {
                var name = ((YamlScalarNode)pair.Key).Value;
                var column = pair.Value as YamlMappingNode;
                var type = column == null ? null : column.Children
                    .Where(p => ((YamlScalarNode)p.Key).Value == "type")
                    .Select(p => (p.Value as YamlScalarNode)?.Value).FirstOrDefault();
                if (!Types.Contains(type)) throw new InvalidDataException("column " + name + " has no type of " + string.Join(", ", Types));
                columns.Add(name, Convert(column));
            }
            return columns;
        }

        static object Convert(YamlNode node)
        {
            var map = node as YamlMappingNode;
            if (map != null)
            {
                RefuseCaseOnly(map, "keys");
                var obj = new JsonObject();
                foreach (var pair in map.Children) obj.Add(((YamlScalarNode)pair.Key).Value, Convert(pair.Value));
                return obj;
            }
            var seq = node as YamlSequenceNode;
            if (seq != null) return seq.Children.Select(Convert).ToList();
            // YAML's null, unquoted, is JSON's null, as YamlDotNet and PyYAML read it; a quoted 'null' is text.
            var scalar = (YamlScalarNode)node;
            if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain &&
                (scalar.Value == "" || scalar.Value == "~" || scalar.Value == "null" || scalar.Value == "Null" || scalar.Value == "NULL")) return null;
            return scalar.Value;
        }

        // FORMAT.md: no object has two keys differing only in case, as Windows PowerShell 5.1 refuses to read one.
        static void RefuseCaseOnly(YamlMappingNode map, string what)
        {
            var clash = map.Children.Keys.Select(k => ((YamlScalarNode)k).Value)
                .GroupBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (clash != null) throw new InvalidDataException(what + " " + string.Join(" and ", clash) + " differ only in case");
        }
    }
}
