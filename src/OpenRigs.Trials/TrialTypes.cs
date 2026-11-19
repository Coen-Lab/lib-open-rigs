#nullable disable
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRigs.Trials
{
    /// <summary>A condition's trialType derived from its values by the task schema's x-trial-types (open_rigs.rigs.conditions
    /// derive_trial_type, the same rule): never stored, derived at Go and logged as each trial's trialType.</summary>
    public static class TrialTypes
    {
        static readonly HashSet<string> EntryKeys = new HashSet<string> { "trialType", "if", "mirrored" };

        /// <summary>The trialType of a condition: task the task's values by dotted path (a list a leaf), set the condition's set by
        /// dotted path, pairs its pairs; every value read as a list (a scalar a one-entry list). The first entry whose if matches
        /// names it; a mirrored entry [a, b] also needs a and b in one pairs group and b's numbers a's negated. Throws on a
        /// malformed entry, an if naming a path not in the values, a JSON Schema keyword it does not evaluate, or no match.</summary>
        public static string Derive(JArray trialTypes, IDictionary<string, JToken> task, IDictionary<string, JToken> set, IList<List<string>> pairs)
        {
            var flat = new JObject();
            foreach (var value in task.Concat(set)) flat[value.Key] = value.Value is JArray ? value.Value : new JArray(value.Value);
            for (int i = 0; i < trialTypes.Count; i++)
            {
                var entry = trialTypes[i] as JObject;
                var last = i == trialTypes.Count - 1;
                if (entry == null || entry.Properties().Any(p => !EntryKeys.Contains(p.Name)) || entry["trialType"] == null || (entry["if"] != null) == last)
                {
                    throw new InvalidOperationException("x-trial-types entry " + Show(trialTypes[i]) + " is malformed: needs trialType, if on every entry but the last, and only if, mirrored, trialType");
                }
                var condition = entry["if"] as JObject;
                if (condition != null)
                {
                    var named = (condition["properties"] as JObject ?? new JObject()).Properties().Select(p => p.Name)
                        .Concat((condition["required"] as JArray ?? new JArray()).Select(p => (string)p));
                    var missing = named.Where(path => flat[path] == null).Distinct().ToList();
                    if (missing.Count > 0) throw new InvalidOperationException("x-trial-types entry " + Show(entry) + " names " + string.Join(", ", missing) + ", not in the values");
                    if (!Matches(condition, flat)) continue;
                }
                var mirrored = entry["mirrored"] as JArray;
                if (mirrored != null)
                {
                    string a = (string)mirrored[0], b = (string)mirrored[1];
                    var aList = (JArray)flat[a];
                    var bList = (JArray)flat[b];
                    var together = (pairs ?? new List<List<string>>()).Any(g => g != null && g.Contains(a) && g.Contains(b));
                    if (!together || !aList.All(IsNumber) || aList.Count != bList.Count || !aList.Zip(bList, (x, y) => IsNumber(y) && -(double)x == (double)y).All(same => same)) continue;
                }
                return (string)entry["trialType"];
            }
            throw new InvalidOperationException("no x-trial-types entry matches the condition's values");
        }

        /// <summary>Whether a value matches a JSON Schema, in the subset x-trial-types uses: type, const, enum, not, allOf,
        /// anyOf, oneOf, properties, required, items, contains, minItems, maxItems, minimum, maximum, exclusiveMinimum,
        /// exclusiveMaximum. Throws on any other keyword, so a rule is never passed by a keyword left unread.</summary>
        public static bool Matches(JToken schema, JToken value)
        {
            if (schema.Type == JTokenType.Boolean) return (bool)schema;
            foreach (var keyword in ((JObject)schema).Properties())
            {
                var s = keyword.Value;
                var array = value as JArray;
                var map = value as JObject;
                bool ok;
                switch (keyword.Name)
                {
                    case "type": ok = (s is JArray ? s.Select(t => (string)t) : new[] { (string)s }).Any(t => IsType(value, t)); break;
                    case "const": ok = Same(s, value); break;
                    case "enum": ok = s.Any(option => Same(option, value)); break;
                    case "not": ok = !Matches(s, value); break;
                    case "allOf": ok = s.All(option => Matches(option, value)); break;
                    case "anyOf": ok = s.Any(option => Matches(option, value)); break;
                    case "oneOf": ok = s.Count(option => Matches(option, value)) == 1; break;
                    case "properties": ok = map == null || ((JObject)s).Properties().All(p => map[p.Name] == null || Matches(p.Value, map[p.Name])); break;
                    case "required": ok = map == null || s.All(p => map[(string)p] != null); break;
                    case "items": ok = array == null || array.All(item => Matches(s, item)); break;
                    case "contains": ok = array == null || array.Any(item => Matches(s, item)); break;
                    case "minItems": ok = array == null || array.Count >= (int)s; break;
                    case "maxItems": ok = array == null || array.Count <= (int)s; break;
                    case "minimum": ok = !IsNumber(value) || (double)value >= (double)s; break;
                    case "maximum": ok = !IsNumber(value) || (double)value <= (double)s; break;
                    case "exclusiveMinimum": ok = !IsNumber(value) || (double)value > (double)s; break;
                    case "exclusiveMaximum": ok = !IsNumber(value) || (double)value < (double)s; break;
                    case "description": case "title": case "$comment": ok = true; break;
                    default: throw new InvalidOperationException("x-trial-types uses the JSON Schema keyword " + keyword.Name + ", which OpenRigs.Trials TrialTypes does not evaluate");
                }
                if (!ok) return false;
            }
            return true;
        }

        static bool IsNumber(JToken value) => value.Type == JTokenType.Integer || value.Type == JTokenType.Float;

        static bool IsType(JToken value, string type)
        {
            switch (type)
            {
                case "number": return IsNumber(value);
                case "integer": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float && Math.Floor((double)value) == (double)value;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "array": return value is JArray;
                case "object": return value is JObject;
                case "null": return value.Type == JTokenType.Null;
                default: throw new InvalidOperationException("x-trial-types uses the JSON Schema type " + type + ", which is not one");
            }
        }

        // JSON Schema equality: numbers by value (1 equals 1.0), never a bool; arrays and objects member by member.
        static bool Same(JToken a, JToken b)
        {
            if (IsNumber(a) && IsNumber(b)) return (double)a == (double)b;
            if (a is JArray && b is JArray) return a.Count() == b.Count() && a.Zip(b, Same).All(same => same);
            if (a is JObject && b is JObject)
            {
                var x = (JObject)a;
                var y = (JObject)b;
                return x.Count == y.Count && x.Properties().All(p => y[p.Name] != null && Same(p.Value, y[p.Name]));
            }
            return a.Type == b.Type && JToken.DeepEquals(a, b);
        }

        static string Show(JToken token) => token.ToString(Newtonsoft.Json.Formatting.None);
    }
}
