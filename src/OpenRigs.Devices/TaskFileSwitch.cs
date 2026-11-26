using Bonsai;
using OpenRigs.Core;
using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Devices
{
    /// <summary>
    /// A bool of the task file at a path, read from the file itself, for a choice the workflow makes at launch, before the
    /// task file is read at Go (TaskConfig): ConnectForTaskZapitDaq opens ZapitDaq only when its OpenedBy bool is true.
    /// </summary>
    [Combinator]
    [DefaultProperty("Path")]
    [Description("Reads a bool from the task file at each input path (TaskPath at launch): the value at Path under its taskParameters, true or false; false when the file leaves it out. Empty Path: EmptyPathValue, the file not read.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class TaskFileSwitch
    {
        /// <summary>Gets or sets the bool's dotted path under the task file's taskParameters, such as optogenetics.zapitBonsai; empty for none.</summary>
        [Description("The bool's dotted path under the task file's taskParameters, such as optogenetics.zapitBonsai. Empty: EmptyPathValue, the file not read.")]
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets the value for an empty Path, the file then not read: false by default.</summary>
        [Description("The value for an empty Path, the file then not read: false by default. ConnectForTaskZapitDaq sets it true, so a piece with no OpenedBy opens as before.")]
        public bool EmptyPathValue { get; set; }

        /// <summary>The bool at Path in the task file at each path.</summary>
        public IObservable<bool> Process(IObservable<string> source)
        {
            return source.Select(taskPath => Read(taskPath, Path, EmptyPathValue));
        }

        /// <summary>The bool at <paramref name="switchPath"/> under the taskParameters of the task file at <paramref name="taskPath"/>:
        /// false when the file leaves it out, and for an empty switchPath without reading the file. Throws, naming the file and
        /// the path, for a file that cannot be read or a value that is not true or false.</summary>
        public static bool Read(string taskPath, string switchPath)
        {
            return Read(taskPath, switchPath, false);
        }

        /// <summary>The bool at <paramref name="switchPath"/> under the taskParameters of the task file at <paramref name="taskPath"/>:
        /// false when the file leaves it out; <paramref name="emptyPathValue"/> for an empty switchPath, without reading the file.
        /// Throws, naming the file and the path, for a file that cannot be read or a value that is not true or false.</summary>
        public static bool Read(string taskPath, string switchPath, bool emptyPathValue)
        {
            if (string.IsNullOrWhiteSpace(switchPath)) return emptyPathValue;
            string text;
            try
            {
                text = File.ReadAllText(taskPath);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(string.Format("The task file {0} could not be read for {1}: {2}", taskPath, switchPath.Trim(), e.Message), e);
            }
            return Value(text, switchPath.Trim(), taskPath);
        }

        /// <summary>The bool at <paramref name="switchPath"/> under the taskParameters of a task file's text; false when it is
        /// left out. <paramref name="where"/> names the file in an error.</summary>
        public static bool Value(string text, string switchPath, string where)
        {
            YamlMappingNode parameters;
            try
            {
                parameters = TaskYaml.Parameters(TaskYaml.Load(text));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(string.Format("The task file {0} has no taskParameters map to read {1} from: {2}", where, switchPath, e.Message), e);
            }
            YamlNode node;
            try
            {
                node = TaskYaml.At(parameters.Children, switchPath);
            }
            catch (InvalidCastException)
            {
                node = null;
            }
            var scalar = node as YamlScalarNode;
            if (scalar == null) throw new InvalidOperationException(string.Format("The task file {0}'s {1} is not true or false.", where, switchPath));
            if (string.IsNullOrEmpty(scalar.Value) || scalar.Value == "null" || scalar.Value == "~") return false;
            bool value;
            if (bool.TryParse(scalar.Value, out value)) return value;
            throw new InvalidOperationException(string.Format("The task file {0}'s {1} is {2}, not true or false.", where, switchPath, scalar.Value));
        }
    }
}
