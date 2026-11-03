using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Resolves a path relative to a rig configuration directory to an absolute path,
    /// and fails if the file does not exist.
    /// </summary>
    [Combinator]
    [DefaultProperty("Directory")]
    [Description("Turns a path relative to the rig configuration directory, such as calibration/valves.json taken from the rig configuration, into the absolute path of that file, and fails if it does not exist. Input is either the relative path alone, resolved against the Directory property, or a Zip of the RigConfigDirectory subject with the relative path, in which case Directory is ignored.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ResolveRigFile
    {
        private string directory;

        /// <summary>
        /// Gets or sets the rig configuration directory that bare relative paths resolve against.
        /// </summary>
        [Description("The rig configuration directory to resolve against when the input is a relative path on its own. Leave empty when the input is a Zip of directory and path, which is the usual wiring.")]
        [Editor("Bonsai.Design.FolderNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string Directory
        {
            get { return directory; }
            set { directory = value; }
        }

        /// <summary>
        /// Resolves each relative path against the Directory property.
        /// </summary>
        public IObservable<string> Process(IObservable<string> source)
        {
            return source.Select(delegate (string relativePath)
            {
                return RigConfigDirectory.ResolveFile(directory, relativePath);
            });
        }

        /// <summary>
        /// Resolves each (directory, relative path) pair, for example from a Zip of the directory and the path.
        /// </summary>
        public IObservable<string> Process(IObservable<Tuple<string, string>> source)
        {
            return source.Select(delegate (Tuple<string, string> pair)
            {
                return RigConfigDirectory.ResolveFile(pair.Item1, pair.Item2);
            });
        }
    }
}
