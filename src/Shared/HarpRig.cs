using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OpenRigs.Shared
{
    /// <summary>
    /// Which rig.yml entries are Harp devices, and what is known of each kind, decided one way for
    /// every package here: OpenRigs.HarpDevices records the devices, OpenRigs.Manifest expects
    /// their files, and the two must agree on every entry or the overnight copy (jacuzzi-ingest's ingest-rig) holds experiments back.
    /// </summary>
    internal static class HarpRig
    {
        /// <summary>The type a Harp device's data is published as, by name: not every package here references Bonsai.Harp.</summary>
        public const string MessageType = "Bonsai.Harp.HarpMessage";

        /// <summary>
        /// Each kind of Harp device rig.yml may hold, to the WhoAmI its boards report. The same
        /// numbers as open-rigs' devices\harp.py and devices\lickety_split.py, where the kinds' settings are defined; the
        /// kinds are named as the rig schemas name their fields (behavior_board is behaviorBoard).
        /// A kind not here is recorded with its WhoAmI unchecked.
        /// </summary>
        public static readonly Dictionary<string, int> WhoAmIByKind = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "behaviorBoard", 1216 },
            { "timestampGeneratorGen3", 1158 },
            { "cameraControllerGen2", 1170 },
            { "clockSynchronizer", 1152 },
            { "stepperDriver", 1130 },
            { "hobgoblin", 123 },
            { "licketySplit", 1400 },
        };

        /// <summary>
        /// Null when the entry is a Harp device: a portName (not empty, not null) and its
        /// &lt;Name&gt;.Data published as HarpMessage. Otherwise why not; empty when it has no
        /// portName, which is not a serial device at all (a camera) and not worth a note.
        /// </summary>
        public static string NotHarp(RigDevice device, Func<string, Type> publishedType)
        {
            if (Setting(device, "portName") == null) return string.Empty;
            var data = publishedType(device.Name + ".Data");
            if (data == null) return "the workflow publishes no " + device.Name + ".Data before this node";
            if (data.FullName != MessageType) return "its data (" + data.Name + ") is not HarpMessage, so it is not a Harp device";
            return null;
        }

        /// <summary>The WhoAmI a kind's boards report, 0 when the kind is not in the table.</summary>
        public static int WhoAmI(string kind)
        {
            int whoAmI;
            return WhoAmIByKind.TryGetValue(kind ?? string.Empty, out whoAmI) ? whoAmI : 0;
        }

        /// <summary>
        /// The register map a device of this kind gets: null when the kind's WhoAmI is not known,
        /// or no Harp package loaded decodes it, and then why in <paramref name="why"/>. The
        /// manifest expects &lt;Name&gt;.yml exactly when this is not null; a record of a board of
        /// the right kind writes it then (<see cref="MapToWrite"/>).
        /// </summary>
        public static RegisterMap RegisterMapFor(string kind, out string why)
        {
            var whoAmI = WhoAmI(kind);
            if (whoAmI == 0)
            {
                why = "no WhoAmI is known for " + kind;
                return null;
            }
            var map = RegisterMaps.Find(whoAmI);
            why = map == null ? "no Harp package loaded in this workflow decodes WhoAmI " + whoAmI + " (" + kind + ")" : null;
            return map;
        }

        /// <summary>
        /// The register map a record writes: the one for the WhoAmI the board reported, else, for
        /// a board that reported none, the one for the WhoAmI its kind expects; none for a kind
        /// whose WhoAmI is not known (expected 0), so the record meets what
        /// <see cref="RegisterMapFor"/> told the manifest to expect, and an unexpected
        /// &lt;Name&gt;.yml never stands against the device's folder entry (S3-I1).
        /// </summary>
        public static RegisterMap MapToWrite(int? reported, int expected)
        {
            if (expected == 0) return null;
            return RegisterMaps.Find(reported ?? expected);
        }

        /// <summary>A setting's text, null when rig.yml has none or leaves it empty (usbSerial: null).</summary>
        public static string Setting(RigDevice device, string key)
        {
            return device.Settings.Children
                .Where(pair => RigDevices.Scalar(pair.Key) == key)
                .Select(pair => RigDevices.Value(pair.Value))
                .FirstOrDefault();
        }
    }

    /// <summary>A Harp package's register map: its device.yml, and the package it came from.</summary>
    internal class RegisterMap
    {
        public string Device;    // Behavior
        public string From;      // Harp.Behavior 0.2.0
        public string Metadata;  // the device.yml
    }

    /// <summary>
    /// The register maps of the Harp packages loaded in this workflow, by WhoAmI. Each generated
    /// Harp package's Device class carries its WhoAmI and its device.yml (Metadata), so a board's
    /// map is the one of the package that decodes it, and cannot drift from it. Read by reflection,
    /// so a package here need not reference Bonsai.Harp to ask. A package released before its
    /// generator embedded device.yml gets the lab's copy of that release's file
    /// (<see cref="LabCopies"/>), for that exact version only.
    /// </summary>
    internal static class RegisterMaps
    {
        /// <summary>
        /// device.yml for released Harp packages that carry none, by "&lt;package&gt; &lt;version&gt;",
        /// to the commit it was taken from. Each is src\Shared\RegisterMaps\&lt;package&gt;-&lt;version&gt;.device.yml,
        /// embedded as OpenRigs.Shared.RegisterMaps.&lt;file name&gt;.
        /// </summary>
        public static readonly Dictionary<string, string> LabCopies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Released 12 Jun 2024, two minutes after a463d1d ("Bump interface version"); upstream
            // embeds device.yml since c0e29e9 (9 Nov 2024), not yet released.
            { "Harp.TimestampGeneratorGen3 0.1.1", "harp-tech/device.timestampgeneratorgen3 a463d1d" },
        };

        public static RegisterMap Find(int whoAmI)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var device = assembly.GetType(assembly.GetName().Name + ".Device", false);
                if (device == null || device.BaseType == null || device.BaseType.FullName != "Bonsai.Harp.Device") continue;
                var id = device.GetField("WhoAmI", BindingFlags.Public | BindingFlags.Static);
                if (id == null || !Equals(id.GetValue(null), whoAmI)) continue;
                var name = assembly.GetName().Name;
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                var package = name + " " + (version != null ? version.InformationalVersion.Split('+')[0] : assembly.GetName().Version.ToString());
                var metadata = device.GetField("Metadata", BindingFlags.Public | BindingFlags.Static);
                if (metadata != null) return new RegisterMap { Device = name.Replace("Harp.", ""), From = package, Metadata = (string)metadata.GetValue(null) };
                var copy = LabCopy(package);
                if (copy != null) return new RegisterMap { Device = name.Replace("Harp.", ""), From = package + " (device.yml from " + LabCopies[package] + ", kept in lib-open-rigs: the package carries none)", Metadata = copy };
            }
            return null;
        }

        /// <summary>The lab's copy of a package's device.yml, null when there is none.</summary>
        public static string LabCopy(string package)
        {
            if (!LabCopies.ContainsKey(package)) return null;
            var resource = "OpenRigs.Shared.RegisterMaps." + package.Replace(' ', '-') + ".device.yml";
            using (var stream = typeof(RegisterMaps).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) return null;
                using (var reader = new System.IO.StreamReader(stream)) return reader.ReadToEnd();
            }
        }
    }
}
