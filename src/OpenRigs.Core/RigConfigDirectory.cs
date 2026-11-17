using System;
using System.IO;

namespace OpenRigs.Core
{
    /// <summary>
    /// Resolves the per-machine rig configuration directory and files within it.
    /// The layout is ConfigRoot\MachineName\rig.yml and ConfigRoot\MachineName\calibration\*.
    /// </summary>
    internal static class RigConfigDirectory
    {
        /// <summary>
        /// Returns the full path of the configuration directory for the current machine,
        /// or throws if no root is given or the directory does not exist.
        /// </summary>
        public static string Resolve(string configRoot)
        {
            if (string.IsNullOrWhiteSpace(configRoot))
            {
                throw new InvalidOperationException(
                    "ConfigRoot is not set: give ReadLabRig's ConfigRoot, the folder holding each machine's <MACHINE-NAME>\\rig.yml.");
            }

            var machineName = Environment.MachineName;
            var path = Path.GetFullPath(Path.Combine(configRoot, machineName));
            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException(
                    "No rig configuration directory for machine '" + machineName +
                    "'. Expected: " + path);
            }

            return path;
        }

        /// <summary>
        /// Returns the full path of a file relative to the given rig configuration directory, or throws if it does
        /// not exist or is given as a full path. <paramref name="setting"/>, the rig.yml key the path is from
        /// (such as projector.meshPath), is named in each refusal when given.
        /// </summary>
        public static string ResolveFile(string directory, string relativePath, string setting = null)
        {
            var path = FilePath(directory, relativePath, setting);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(setting)
                    ? "No rig file '" + relativePath + "'. Expected: " + path
                    : "rig.yml's " + setting + " names " + relativePath + ", and there is no such file: " + path);
            }

            return path;
        }

        /// <summary>
        /// The full path of a file rig.yml names relative to its folder (or <paramref name="directory"/>), whether or
        /// not it exists: every rig.yml file path goes through here, so each is relative. Throws if it is empty or a full
        /// path, naming <paramref name="setting"/> when given.
        /// </summary>
        public static string FilePath(string directory, string relativePath, string setting = null)
        {
            var named = string.IsNullOrEmpty(setting) ? "The rig file path" : "rig.yml's " + setting;
            if (string.IsNullOrEmpty(relativePath))
            {
                throw new InvalidOperationException(named + " is empty: give it relative to rig.yml's folder.");
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException(string.Format(
                    "{0} is a full path, {1}: give it relative to rig.yml's folder ({2}), as calibration/{3}.",
                    named, relativePath, string.IsNullOrEmpty(directory) ? "<ConfigRoot>\\<MACHINE-NAME>" : directory,
                    Path.GetFileName(relativePath)));
            }

            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException(
                    "No rig configuration directory was given to resolve '" + relativePath + "'.");
            }

            return Path.GetFullPath(Path.Combine(directory, relativePath));
        }
    }
}
