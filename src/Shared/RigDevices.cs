using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace OpenRigs.Shared
{
    /// <summary>One device entry in rig.yml: kind: instance: settings, named &lt;Kind&gt;-&lt;Instance&gt;.</summary>
    internal class RigDevice
    {
        /// <summary>The key in rig.yml, such as camera.</summary>
        public string Kind;

        /// <summary>The instance's key, such as box.</summary>
        public string Instance;

        /// <summary>The device's name, Camera-Box: the subject its data is published as is Camera-Box.Data.</summary>
        public string Name;

        /// <summary>The instance's settings.</summary>
        public YamlMappingNode Settings;
    }

    /// <summary>The devices a rig.yml lists, read the same way by every package here.</summary>
    internal static class RigDevices
    {
        /// <summary>The folder rig-setup keeps each machine's rig.yml under, the ConfigRoot every package here defaults to.</summary>
        public const string DefaultConfigRoot = @"C:\Coen-Lab-Local\RigConfigs";

        /// <summary>This machine's rig.yml under a ConfigRoot: &lt;ConfigRoot&gt;\&lt;MACHINE-NAME&gt;\rig.yml, with environment variables expanded.</summary>
        public static string PathFor(string configRoot)
        {
            return Path.Combine(Environment.ExpandEnvironmentVariables(configRoot), Environment.MachineName, "rig.yml");
        }

        /// <summary>
        /// Every device entry in the rig.yml at this path, in file order; null with the reason
        /// when there is no such file, or it cannot be read, or holds no settings. Never throws:
        /// the editor builds a workflow on open, on any desk.
        /// </summary>
        public static List<RigDevice> Read(string path, out string problem)
        {
            problem = null;
            YamlMappingNode root;
            try
            {
                if (!File.Exists(path))
                {
                    problem = "no rig.yml at " + path;
                    return null;
                }
                var yaml = new YamlStream();
                using (var reader = new StreamReader(path)) yaml.Load(reader);
                root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
            }
            catch (Exception ex)
            {
                problem = path + " could not be read: " + ex.Message;
                return null;
            }
            if (root == null)
            {
                problem = path + " is empty or not a set of settings";
                return null;
            }

            var devices = new List<RigDevice>();
            foreach (var kind in root.Children)
            {
                var kindName = Scalar(kind.Key);
                var instances = kind.Value as YamlMappingNode;
                // Device entries are kind: instance: settings. Anything else (rootPath, version) is not a device.
                if (kindName == null || instances == null || instances.Children.Count == 0 ||
                    !instances.Children.Values.All(v => v is YamlMappingNode)) continue;
                foreach (var instance in instances.Children)
                {
                    var instanceName = Scalar(instance.Key) ?? string.Empty;
                    devices.Add(new RigDevice
                    {
                        Kind = kindName,
                        Instance = instanceName,
                        Name = Pascal(kindName) + "-" + Pascal(instanceName),
                        Settings = (YamlMappingNode)instance.Value,
                    });
                }
            }
            return devices;
        }

        /// <summary>A scalar node's text, or null for anything else.</summary>
        public static string Scalar(YamlNode node)
        {
            var scalar = node as YamlScalarNode;
            return scalar != null ? scalar.Value : null;
        }

        /// <summary>A scalar's text, trimmed; null when there is none or it is left empty (null, ~, empty or blank), as a generated rig.yml writes an unset value.</summary>
        public static string Value(YamlNode node)
        {
            var value = Scalar(node);
            return string.IsNullOrWhiteSpace(value) || value == "null" || value == "~" ? null : value.Trim();
        }

        static string Pascal(string name)
        {
            return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }
}
