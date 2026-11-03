using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Emits the rig configuration directory for the current machine, ConfigRoot\MachineName,
    /// and fails at startup if it doesn't exist.
    /// </summary>
    [DefaultProperty("ConfigRoot")]
    [Description("Emits the rig configuration directory for the current machine (ConfigRoot\\MachineName). Fails at startup if the directory does not exist.")]
    public class ResolveRigConfigDirectory : Source<string>
    {
        private string configRoot = RigConfigDirectory.DefaultConfigRoot;
        [Description("The root under which each machine has its own configuration directory. A local folder or a network share.")]
        [Editor("Bonsai.Design.FolderNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        
        public string ConfigRoot
        {
            get { return configRoot; }
            set { configRoot = value; }
        }

        public override IObservable<string> Generate()
        {
            return Observable.Defer(delegate
            {
                return Observable.Return(RigConfigDirectory.Resolve(configRoot));
            });
        }
    }
}
