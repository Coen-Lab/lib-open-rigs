using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Resolves a path relative to a rig configuration directory to an absolute path,
    /// and fails if the file does not exist or the path is a full one.
    /// </summary>
    [Combinator]
    [DefaultProperty("Directory")]
    [Description("Turns a path relative to the rig configuration directory, such as calibration/valves.json taken from the rig configuration, into the absolute path of that file, and fails if it does not exist or is a full path. Input is either the relative path alone, resolved against the Directory property, or a Zip of the RigConfigDirectory subject with the relative path, in which case Directory is ignored.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ResolveRigFile
    {
        private string directory;
        private string setting;

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
        /// Gets or sets the rig.yml key the path is from, named when the path is refused.
        /// </summary>
        [Description("The rig.yml key the path is from, such as projector.meshPath, named when the path is refused. Empty: the refusal names the path alone.")]
        public string Setting
        {
            get { return setting; }
            set { setting = value; }
        }

        /// <summary>
        /// Resolves each relative path against the Directory property.
        /// </summary>
        public IObservable<string> Process(IObservable<string> source)
        {
            return source.Select(delegate (string relativePath)
            {
                return RigConfigDirectory.ResolveFile(directory, relativePath, setting);
            });
        }

        /// <summary>
        /// Resolves each (directory, relative path) pair, for example from a Zip of the directory and the path.
        /// </summary>
        public IObservable<string> Process(IObservable<Tuple<string, string>> source)
        {
            return source.Select(delegate (Tuple<string, string> pair)
            {
                return RigConfigDirectory.ResolveFile(pair.Item1, pair.Item2, setting);
            });
        }
    }
}
