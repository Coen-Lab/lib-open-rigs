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
        /// Returns the full path of a file relative to the given rig configuration directory,
        /// or throws if it does not exist. A rooted path is checked as given.
        /// </summary>
        public static string ResolveFile(string directory, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                throw new ArgumentException("The rig file path must be specified.", "relativePath");
            }

            string path;
            if (Path.IsPathRooted(relativePath))
            {
                path = Path.GetFullPath(relativePath);
            }
            else
            {
                if (string.IsNullOrEmpty(directory))
                {
                    throw new InvalidOperationException(
                        "No rig configuration directory was given to resolve '" + relativePath + "'.");
                }

                path = Path.GetFullPath(Path.Combine(directory, relativePath));
            }

            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    "No rig file '" + relativePath + "'. Expected: " + path);
            }

            return path;
        }
    }
}
