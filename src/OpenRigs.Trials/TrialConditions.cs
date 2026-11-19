#nullable disable
using OpenRigs.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Trials
{
    /// <summary>One of a task's trialConditions (open_rigs.rigs.conditioned_task TrialCondition): a task's generated
    /// TrialCondition implements it in a small partial class.</summary>
    public interface ITrialCondition
    {
        /// <summary>Gets the condition's trialType, the task's enum.</summary>
        Enum TrialType { get; }
        /// <summary>Gets its relative draw weight.</summary>
        int Weight { get; }
        /// <summary>Gets the groups of paths whose lists share one picked index.</summary>
        List<List<string>> Pairs { get; }
        /// <summary>Gets its set: condition-level values by dotted path, as read.</summary>
        object Set { get; }
    }

    /// <summary>A task whose trials are drawn from weighted trial conditions: a task's generated parameters implement it in a
    /// small partial class.</summary>
    public interface IConditionedTask
    {
        /// <summary>Gets the trial mix.</summary>
        IList<ITrialCondition> Conditions { get; }
        /// <summary>Gets the curriculum stage these values are (null when empty).</summary>
        ICurriculum Stage { get; }
        /// <summary>Gets the seed of the trials' random stream (null: one is drawn).</summary>
        double? RngSeed { get; }
        /// <summary>Gets whether this is an optogenetics experiment.</summary>
        bool OptogeneticsEnabled { get; }
    }

    /// <summary>What a task's C# reads from its schema (its generated TaskSchema): x-session-fields, the list fields, x-max-combinations.</summary>
    public sealed class ConditionsSchema
    {
        /// <summary>Creates the schema's facts.</summary>
        public ConditionsSchema(IEnumerable<string> sessionFields, IEnumerable<string> listFields, int maxCombinations)
        {
            SessionFields = sessionFields.ToArray();
            ListFields = listFields.ToArray();
            MaxCombinations = maxCombinations;
        }

        /// <summary>Gets what a condition may not set (the path or any path under it).</summary>
        public string[] SessionFields { get; private set; }
        /// <summary>Gets the list fields one entry of which each trial draws.</summary>
        public string[] ListFields { get; private set; }
        /// <summary>Gets the most combinations one condition may expand into.</summary>
        public int MaxCombinations { get; private set; }
    }

    /// <summary>One of the task's trialConditions as built at Go (TrialConditions.Build).</summary>
    public class TrialConditionDraw<TTask>
    {
        /// <summary>The condition's trialType, its trials'.</summary>
        public string TrialType;
        /// <summary>Its relative draw weight.</summary>
        public int Weight;
        /// <summary>Where it is, for a message: trialConditions[index] (trialType).</summary>
        public string Where;
        /// <summary>The task with its set over it, one per combination of its list cells, picked uniformly per trial.</summary>
        public List<TrialConditionCombination<TTask>> Combinations;
    }

    /// <summary>One combination of a condition's list cells: the task with the condition's set over it.</summary>
    public class TrialConditionCombination<TTask>
    {
        /// <summary>The task as this combination's trials draw it.</summary>
        public TTask Task;
        /// <summary>The picks that make it, for a message ("" with no list cell).</summary>
        public string Picks;
        /// <summary>Its 0-based row in Settings_Combinations: build order across the session, conditions in turn (the trialCombinationId column).</summary>
        public int Id;
    }

    /// <summary>The loudest level a sound plays at in a task's weighted conditions, and where (a condition and its picks): what
    /// a sound's buffer is peak-checked at, at Go, so no trial's level can clip.</summary>
    public class LoudestSound
    {
        /// <summary>The level in dBFS RMS; NaN when every combination is mute.</summary>
        public double LevelDbfs;
        /// <summary>The condition that draws it, for a message.</summary>
        public string Where;

        /// <summary>The loudest of the weighted conditions' combinations, by the task's level (a number or mute, SoundLevels.Parse).</summary>
        public static LoudestSound Of<TTask>(IEnumerable<TrialConditionDraw<TTask>> conditions, Func<TTask, object> levelDbfs)
        {
            var loudest = new LoudestSound { LevelDbfs = double.NaN, Where = "no condition" };
            foreach (var condition in conditions.Where(c => c.Weight > 0))
            {
                foreach (var combination in condition.Combinations)
                {
                    var value = SoundLevels.Parse(levelDbfs(combination.Task));
                    if (value > loudest.LevelDbfs || (double.IsNaN(loudest.LevelDbfs) && !double.IsNaN(value)))
                    {
                        loudest = new LoudestSound { LevelDbfs = value, Where = condition.Where + (combination.Picks.Length == 0 ? "" : " with " + combination.Picks) };
                    }
                }
            }
            return loudest;
        }
    }

    /// <summary>A task's trial conditions built from its file, the Python build's rule (open_rigs.rigs.conditioned_task).</summary>
    public static class TrialConditions
    {
        /// <summary>The most a condition's weight may be (conditioned_task.MAX_WEIGHT), so the weights' sum stays an int.</summary>
        public const int MaxWeight = 1000000;

        /// <summary>Each trial condition as drawn: its set and pairs written over a copy of the task file's text (without its
        /// trialConditions) at their dotted paths and read back, once for every combination: each pairs group's lists (the
        /// set's, else the task's) zipped, equally long; then by path each other list cell of its set and every list field one
        /// pick (a scalar field's list one value, a list field's entry a one-entry list); all combined as a product, the first
        /// cell's pick changing fastest (each combination's Id its row, across the session), at most MaxCombinations. Refused,
        /// each named: a session field, a path that is not a task field, an optogenetics path with optogenetics off, a missing
        /// weight, a YAML merge key, a set that is not a map or a key that is not a path, a bad pairs group. Adds a problem for
        /// each condition that cannot be built, and returns null then.</summary>
        public static List<TrialConditionDraw<TTask>> Build<TTask>(string taskText, IConditionedTask task, Func<string, TTask> read, ConditionsSchema schema, List<string> problems)
        {
            var typed = task.Conditions ?? new List<ITrialCondition>();
            if (typed.Count == 0) { problems.Add("trialConditions needs a condition"); return null; }
            if (typed.Sum(c => (long)c.Weight) > int.MaxValue)
            {
                problems.Add(string.Format("trialConditions' weights add to more than {0}, the most the draw can sum", int.MaxValue));
                return null;
            }
            var nodes = TaskYaml.Parameters(TaskYaml.Load(taskText)).Children;
            // Every field of the typed task, defaults included, for whether a path is a task field.
            var fields = ((YamlMappingNode)TaskYaml.Load(TaskYaml.Text(task)).Documents[0].RootNode).Children;
            YamlNode sequence;
            var conditionNodes = nodes.TryGetValue(new YamlScalarNode("trialConditions"), out sequence) && sequence is YamlSequenceNode
                ? ((YamlSequenceNode)sequence).Children : new List<YamlNode>();
            // Each combination is read from the task without its conditions, which it does not draw from.
            var stripped = TaskYaml.Load(taskText);
            TaskYaml.Parameters(stripped).Children[new YamlScalarNode("trialConditions")] = new YamlSequenceNode();
            var writer = new StringWriter();
            stripped.Save(writer, false);
            var baseText = writer.ToString();
            var built = new List<TrialConditionDraw<TTask>>();
            int id = 0;
            for (int i = 0; i < typed.Count; i++)
            {
                var c = typed[i];
                var trialType = EnumNames.Name(c.TrialType);
                var where = string.Format("trialConditions[{0}] ({1})", i, trialType);
                var before = problems.Count;
                if (c.Weight < 0) problems.Add(where + ": its weight is below 0");
                if (c.Weight > MaxWeight) problems.Add(string.Format("{0}: its weight is above {1}", where, MaxWeight));
                var conditionNode = i < conditionNodes.Count ? conditionNodes[i] as YamlMappingNode : null;
                if (conditionNode == null) { problems.Add(where + ": it is not a map in the task file"); continue; }
                // The typed read fills a missing weight with 0 and merges a << key, which the node tree does not: refuse both.
                if (!conditionNode.Children.ContainsKey(new YamlScalarNode("weight"))) problems.Add(where + ": it has no weight");
                var values = new Dictionary<string, YamlNode>();
                YamlNode set;
                var hasSet = conditionNode.Children.TryGetValue(new YamlScalarNode("set"), out set);
                if (conditionNode.Children.ContainsKey(new YamlScalarNode("<<")) || (set is YamlMappingNode && ((YamlMappingNode)set).Children.ContainsKey(new YamlScalarNode("<<"))))
                    problems.Add(where + ": a YAML merge key (<<) is not read by the draw; write the condition out in full");
                if (hasSet && !(set is YamlMappingNode) || c.Set != null && !(c.Set is System.Collections.IDictionary))
                    problems.Add(where + ": its set is not a map");
                else if (set is YamlMappingNode)
                {
                    foreach (var cell in ((YamlMappingNode)set).Children)
                    {
                        if (!(cell.Key is YamlScalarNode)) { problems.Add(where + ": a set key is not a dotted path"); continue; }
                        values[((YamlScalarNode)cell.Key).Value] = cell.Value;
                    }
                }
                var pairs = c.Pairs ?? new List<List<string>>();
                var paired = pairs.SelectMany(g => g ?? new List<string>()).ToList();
                foreach (var path in values.Keys.Concat(paired))
                {
                    var session = TaskYaml.SessionField(schema.SessionFields, path);
                    if (session != null) problems.Add(string.Format("{0} sets or pairs {1}, a session field (x-session-fields: {2})", where, path, session));
                    else if (!TaskYaml.Known(fields, path)) problems.Add(string.Format("{0} sets or pairs {1}, which is not a task field", where, path));
                    else if (path.StartsWith("optogenetics.") && !task.OptogeneticsEnabled) problems.Add(string.Format("{0} sets {1} with optogenetics off", where, path));
                }
                if (paired.Distinct().Count() < paired.Count || pairs.Any(g => g == null || g.Count < 2)) problems.Add(where + ": each pairs group needs two paths or more, each path in one group");
                if (problems.Count > before) continue;
                var cells = new List<KeyValuePair<string[], List<YamlNode[]>>>();
                foreach (var group in pairs)
                {
                    var lists = group.Select(path => Options(nodes, values, path, schema.ListFields)).ToList();
                    if (lists.Select(l => l.Count).Distinct().Count() > 1)
                    {
                        problems.Add(string.Format("{0}: pairs [{1}] have lists of lengths {2}; paired lists must be equally long", where, string.Join(", ", group), string.Join(", ", lists.Select(l => l.Count))));
                        continue;
                    }
                    cells.Add(new KeyValuePair<string[], List<YamlNode[]>>(group.ToArray(), Enumerable.Range(0, lists[0].Count).Select(k => lists.Select(l => l[k]).ToArray()).ToList()));
                }
                foreach (var path in values.Keys.Concat(schema.ListFields).Distinct().Where(p => !paired.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
                {
                    cells.Add(new KeyValuePair<string[], List<YamlNode[]>>(new[] { path }, Options(nodes, values, path, schema.ListFields).Select(o => new[] { o }).ToList()));
                }
                foreach (var cell in cells.Where(cell => cell.Value.Count == 0)) problems.Add(string.Format("{0}: {1} is an empty list", where, string.Join(", ", cell.Key)));
                var count = cells.Aggregate(1L, (n, cell) => n * cell.Value.Count);
                if (count > schema.MaxCombinations) problems.Add(string.Format("{0} expands into {1} combinations, more than {2} (x-max-combinations)", where, count, schema.MaxCombinations));
                if (problems.Count > before) continue;
                var draw = new TrialConditionDraw<TTask> { TrialType = trialType, Weight = c.Weight, Where = where, Combinations = new List<TrialConditionCombination<TTask>>() };
                for (int n = 0; n < count; n++)
                {
                    var stream = TaskYaml.Load(baseText);
                    var parameters = TaskYaml.Parameters(stream).Children;
                    var named = new List<string>();
                    int rest = n;
                    foreach (var cell in cells)
                    {
                        var picked = cell.Value[rest % cell.Value.Count];
                        rest /= cell.Value.Count;
                        for (int k = 0; k < cell.Key.Length; k++)
                        {
                            TaskYaml.Put(parameters, cell.Key[k], picked[k]);
                            if (cell.Value.Count > 1) named.Add(cell.Key[k] + " " + TaskYaml.Show(picked[k]));
                        }
                    }
                    writer = new StringWriter();
                    stream.Save(writer, false);
                    try
                    {
                        draw.Combinations.Add(new TrialConditionCombination<TTask> { Task = read(writer.ToString()), Picks = string.Join(", ", named), Id = id++ });
                    }
                    catch (YamlDotNet.Core.YamlException e)
                    {
                        problems.Add(string.Format("{0}{1}: its values do not read as the task's fields ({2})", where, named.Count == 0 ? "" : " with " + string.Join(", ", named), e.InnerException != null ? e.InnerException.Message : e.Message));
                        break;
                    }
                }
                built.Add(draw);
            }
            if (problems.Count == 0 && built.Sum(c => (long)c.Weight) <= 0) problems.Add("trialConditions needs a condition with a weight above 0");
            return problems.Count == 0 ? built : null;
        }

        /// <summary>Settings_Combinations: one row per combination of every condition, in build order (its Id, the trial log's
        /// trialCombinationId), with its trialConditionId, trialType, conditionWeight and values. Each task is serialised as
        /// SerializeToJson serialises Settings_Task and flattened to dotted paths, a one-entry list unwrapped only at a list
        /// field (an x-whole list stays a list); a path is in values when it differs between rows or from the task's own, so a
        /// set every condition shares is kept; session fields are left out. NaN is written as JSON NaN.</summary>
        public static string CombinationsJson<TTask>(TTask task, IList<TrialConditionDraw<TTask>> conditions, ConditionsSchema schema)
        {
            var own = TaskYaml.Flatten(task, schema.ListFields);
            var all = conditions.SelectMany(c => c.Combinations.Select(m => TaskYaml.Flatten(m.Task, schema.ListFields))).ToList();
            var kept = all.SelectMany(r => r.Keys).Distinct()
                .Where(path => !schema.SessionFields.Any(f => path == f || path.StartsWith(f + ".")))
                .Where(path => all.Any(r => !TaskYaml.Same(r, own, path) || !TaskYaml.Same(r, all[0], path)))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var rows = new Newtonsoft.Json.Linq.JArray();
            int n = 0;
            for (int i = 0; i < conditions.Count; i++)
            {
                foreach (var combination in conditions[i].Combinations)
                {
                    var values = new Newtonsoft.Json.Linq.JObject();
                    foreach (var path in kept) values[path] = all[n][path];
                    rows.Add(new Newtonsoft.Json.Linq.JObject(
                        new Newtonsoft.Json.Linq.JProperty("trialCombinationId", combination.Id),
                        new Newtonsoft.Json.Linq.JProperty("trialConditionId", i),
                        new Newtonsoft.Json.Linq.JProperty("trialType", conditions[i].TrialType),
                        new Newtonsoft.Json.Linq.JProperty("conditionWeight", conditions[i].Weight),
                        new Newtonsoft.Json.Linq.JProperty("values", values)));
                    n++;
                }
            }
            var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var json = new Newtonsoft.Json.JsonTextWriter(text) { Formatting = Newtonsoft.Json.Formatting.Indented, FloatFormatHandling = Newtonsoft.Json.FloatFormatHandling.Symbol };
            new Newtonsoft.Json.Linq.JObject(new Newtonsoft.Json.Linq.JProperty("rows", rows)).WriteTo(json);
            json.Flush();
            return text.ToString();
        }

        // A cell's picks (conditioned_task._options): a list field's entries, each a one-entry list; a scalar field's list,
        // else its one value (a whole list, x-whole, is one value). The value is the set's, else the task's.
        static List<YamlNode> Options(IDictionary<YamlNode, YamlNode> nodes, Dictionary<string, YamlNode> values, string path, string[] listFields)
        {
            YamlNode value;
            var own = TaskYaml.At(nodes, path);
            if (!values.TryGetValue(path, out value)) value = own;
            var list = value as YamlSequenceNode;
            var listField = listFields.Contains(path);
            if (list == null || own is YamlSequenceNode && !listField) return new List<YamlNode> { value };
            return listField ? list.Children.Select(entry => (YamlNode)new YamlSequenceNode(entry)).ToList() : list.Children.ToList();
        }
    }
}
