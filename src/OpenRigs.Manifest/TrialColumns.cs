using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRigs.Shared;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// The workflow's trial columns file (TrialColumns, such as schemas\trial-columns.yml): what each
    /// BonsaiData_Trial column the workflow writes means, by name: type (int, double, bool or string),
    /// and where they apply values (the strings it may hold, or a code: meaning map), unit, formerly
    /// (names it was recorded under before, newest first) and description. Read once at Start and
    /// copied into the manifest, so each experiment carries the meaning of its own trial log.
    /// </summary>
    static class TrialColumns
    {
        static readonly string[] Types = { "int", "double", "bool", "string" };

        /// <summary>trial_columns: path, sha256 and columns; or path, missing and why. Null when TrialColumns is blank.</summary>
        public static JsonObject Read(string configured, string baseFolder, out string note)
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
                note = "Trial columns not read at Start from " + configured + ": " + ex.Message;
            }
            return section;
        }

        /// <summary>Each column by name, as written in the file; throws naming the column whose type is missing or not one of the four.</summary>
        public static JsonObject Columns(byte[] bytes)
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(new MemoryStream(bytes))) yaml.Load(reader);
            var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            if (root == null) throw new InvalidDataException("the file is empty or not a map of columns");
            var columns = new JsonObject();
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
                var obj = new JsonObject();
                foreach (var pair in map.Children) obj.Add(((YamlScalarNode)pair.Key).Value, Convert(pair.Value));
                return obj;
            }
            var seq = node as YamlSequenceNode;
            if (seq != null) return seq.Children.Select(Convert).ToList();
            return ((YamlScalarNode)node).Value;
        }
    }
}
