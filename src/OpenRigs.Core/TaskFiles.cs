#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Core
{
    /// <summary>A task enum value as the task file writes it.</summary>
    public static class EnumNames
    {
        /// <summary>The value's EnumMember name (visualSide, discriminationGoNoGo), else its C# name.</summary>
        public static string Name(Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var member = field == null ? null : (System.Runtime.Serialization.EnumMemberAttribute)Attribute.GetCustomAttribute(field, typeof(System.Runtime.Serialization.EnumMemberAttribute));
            return member != null ? member.Value : value.ToString();
        }
    }

    /// <summary>A task file's curriculum block (open_rigs.rigs.conditioned_task Curriculum): a task's generated Curriculum
    /// implements it in a one-line partial class.</summary>
    public interface ICurriculum
    {
        /// <summary>Gets the stage's id.</summary>
        int Id { get; }
        /// <summary>Gets the stage's code (S03A).</summary>
        string Code { get; }
        /// <summary>Gets the stage's version (k7f2).</summary>
        string Version { get; }
        /// <summary>Gets the stage's path (A).</summary>
        string Path { get; }
        /// <summary>Gets the stage's name.</summary>
        string Label { get; }
        /// <summary>Gets the SHA-256 of the curriculum file's text.</summary>
        string Sha256 { get; }
    }

    /// <summary>The curriculum block's rules, Python's (conditioned_task.Curriculum) again for Bonsai.</summary>
    public static class CurriculumCheck
    {
        /// <summary>What is wrong with a curriculum (null when present and well formed: an id of 0 or more, a code like S03A, a
        /// 4-character version, a path of one capital letter, a label and a sha256), else the reason.</summary>
        public static string Problem(ICurriculum curriculum)
        {
            if (curriculum == null) return "is empty";
            var bad = new List<string>();
            if (curriculum.Id < 0) bad.Add("id " + curriculum.Id + " is below 0");
            if (curriculum.Code == null || !Regex.IsMatch(curriculum.Code, @"^S\d{2}[A-Z]$")) bad.Add("code '" + curriculum.Code + "' is not like S03A");
            if (curriculum.Version == null || !Regex.IsMatch(curriculum.Version, "^[abcdefghjkmnpqrstuvwxyz23456789]{4}$")) bad.Add("version '" + curriculum.Version + "' is not 4 characters of abcdefghjkmnpqrstuvwxyz23456789");
            if (curriculum.Path == null || !Regex.IsMatch(curriculum.Path, "^[A-Z]$")) bad.Add("path '" + curriculum.Path + "' is not one capital letter");
            if (string.IsNullOrEmpty(curriculum.Label)) bad.Add("label is empty");
            if (string.IsNullOrEmpty(curriculum.Sha256)) bad.Add("sha256 is empty");
            return bad.Count == 0 ? null : "is malformed (" + string.Join("; ", bad) + ")";
        }
    }

    /// <summary>A task file's text as YAML nodes and typed values, by dotted path under taskParameters: what a task's draw
    /// builds its trial conditions with (OpenRigs.Trials TrialConditions).</summary>
    public static class TaskYaml
    {
        /// <summary>The task file's text read as Bonsai's DeserializeFromYaml reads it, except that a typed object (a trial
        /// condition's set) keeps its YAML types, numbers and bools, where DeserializeFromYaml reads them as text.</summary>
        public static T Read<T>(string text)
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithTypeConverter(new YamlDotNet.Serialization.Converters.DateTimeOffsetConverter())
                .WithAttemptingUnquotedStringTypeDeserialization()
                .Build();
            return deserializer.Deserialize<T>(new YamlDotNet.Core.MergingParser(new YamlDotNet.Core.Parser(new StringReader(text))));
        }

        /// <summary>A task logic as a task file's text (as SerializeToYaml writes it), which Read reads back.</summary>
        public static string Text(object taskLogic)
        {
            var serializer = new YamlDotNet.Serialization.SerializerBuilder()
                .WithTypeConverter(new YamlDotNet.Serialization.Converters.DateTimeOffsetConverter())
                .DisableAliases()
                .Build();
            return serializer.Serialize(taskLogic);
        }

        /// <summary>The text's YAML stream.</summary>
        public static YamlStream Load(string text)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            return stream;
        }

        /// <summary>The stream's taskParameters map.</summary>
        public static YamlMappingNode Parameters(YamlStream stream)
        {
            return (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("taskParameters")];
        }

        /// <summary>The node at a dotted path; a key the file leaves out is read as null, an empty scalar.</summary>
        public static YamlNode At(IDictionary<YamlNode, YamlNode> nodes, string path)
        {
            var parts = path.Split('.');
            for (int i = 0; i < parts.Length - 1; i++) nodes = ((YamlMappingNode)nodes[new YamlScalarNode(parts[i])]).Children;
            YamlNode node;
            return nodes.TryGetValue(new YamlScalarNode(parts[parts.Length - 1]), out node) ? node : new YamlScalarNode();
        }

        /// <summary>Writes the node at a dotted path whose parents are maps.</summary>
        public static void Put(IDictionary<YamlNode, YamlNode> nodes, string path, YamlNode value)
        {
            var parts = path.Split('.');
            for (int i = 0; i < parts.Length - 1; i++) nodes = ((YamlMappingNode)nodes[new YamlScalarNode(parts[i])]).Children;
            nodes[new YamlScalarNode(parts[parts.Length - 1])] = value;
        }

        /// <summary>Whether a dotted path is a leaf (not a map) of the task's nodes; give it the typed task as Text writes it,
        /// every field present, so a leaf the file leaves out (a default) is known.</summary>
        public static bool Known(IDictionary<YamlNode, YamlNode> nodes, string path)
        {
            YamlNode node = null;
            foreach (var part in path.Split('.'))
            {
                if (nodes == null || !nodes.TryGetValue(new YamlScalarNode(part), out node)) return false;
                nodes = node is YamlMappingNode ? ((YamlMappingNode)node).Children : null;
            }
            return !(node is YamlMappingNode);
        }

        /// <summary>A node for a message: a list as [a, b], else its text.</summary>
        public static string Show(YamlNode node)
        {
            var list = node as YamlSequenceNode;
            return list != null ? "[" + string.Join(", ", list.Children) + "]" : node.ToString();
        }

        /// <summary>A typed task as dotted paths to its leaves (Settings_Combinations' normalisation): serialised as
        /// SerializeToJson does, a one-entry list unwrapped only at a list field.</summary>
        public static Dictionary<string, Newtonsoft.Json.Linq.JToken> Flatten(object task, ICollection<string> listFields)
        {
            var leaves = new Dictionary<string, Newtonsoft.Json.Linq.JToken>();
            Flatten(Newtonsoft.Json.Linq.JToken.FromObject(task), "", leaves, listFields);
            return leaves;
        }

        static void Flatten(Newtonsoft.Json.Linq.JToken token, string path, Dictionary<string, Newtonsoft.Json.Linq.JToken> leaves, ICollection<string> listFields)
        {
            var map = token as Newtonsoft.Json.Linq.JObject;
            if (map != null)
            {
                foreach (var property in map.Properties()) Flatten(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, leaves, listFields);
                return;
            }
            var list = token as Newtonsoft.Json.Linq.JArray;
            leaves[path] = list != null && list.Count == 1 && listFields.Contains(path) ? list[0] : token;
        }

        /// <summary>Whether two flattened tasks hold the same value, or both none, at a path.</summary>
        public static bool Same(Dictionary<string, Newtonsoft.Json.Linq.JToken> a, Dictionary<string, Newtonsoft.Json.Linq.JToken> b, string path)
        {
            Newtonsoft.Json.Linq.JToken x, y;
            return a.TryGetValue(path, out x) == b.TryGetValue(path, out y) && Newtonsoft.Json.Linq.JToken.DeepEquals(x, y);
        }

        /// <summary>Whether a dotted path is a session field or lies under one, or one lies under it: the field, else null.</summary>
        public static string SessionField(IEnumerable<string> sessionFields, string path)
        {
            return sessionFields.FirstOrDefault(f => path == f || path.StartsWith(f + ".") || f.StartsWith(path + "."));
        }
    }
}
