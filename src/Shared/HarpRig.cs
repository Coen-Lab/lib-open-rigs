using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OpenRigs.Shared
{
    /// <summary>
    /// Which rig.yml entries are Harp devices, and what is known of each kind, decided one way for
    /// every package here: OpenRigs.HarpDevices records the devices, OpenRigs.Manifest expects
    /// their files, and the two must agree on every entry or rig-sync holds sessions back.
    /// </summary>
    internal static class HarpRig
    {
        /// <summary>The type a Harp device's data is published as, by name: not every package here references Bonsai.Harp.</summary>
        public const string MessageType = "Bonsai.Harp.HarpMessage";

        /// <summary>
        /// Each kind of Harp device rig.yml may hold, to the WhoAmI its boards report. The same
        /// numbers as open-rigs' devices\harp.py, where the kinds' settings are defined; the
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
        /// a board that reported none, the one for the WhoAmI its kind expects (0 for none), so
        /// the record meets what <see cref="RegisterMapFor"/> told the manifest to expect.
        /// </summary>
        public static RegisterMap MapToWrite(int? reported, int expected)
        {
            if (reported.HasValue) return RegisterMaps.Find(reported.Value);
            return expected != 0 ? RegisterMaps.Find(expected) : null;
        }

        /// <summary>A setting's text, null when rig.yml has none or leaves it empty (usbSerial: null).</summary>
        public static string Setting(RigDevice device, string key)
        {
            var value = device.Settings.Children
                .Where(pair => RigDevices.Scalar(pair.Key) == key)
                .Select(pair => RigDevices.Scalar(pair.Value))
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) || value == "null" || value == "~" ? null : value.Trim();
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
    /// so a package here need not reference Bonsai.Harp to ask.
    /// </summary>
    internal static class RegisterMaps
    {
        public static RegisterMap Find(int whoAmI)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var device = assembly.GetType(assembly.GetName().Name + ".Device", false);
                if (device == null || device.BaseType == null || device.BaseType.FullName != "Bonsai.Harp.Device") continue;
                var id = device.GetField("WhoAmI", BindingFlags.Public | BindingFlags.Static);
                var metadata = device.GetField("Metadata", BindingFlags.Public | BindingFlags.Static);
                if (id == null || metadata == null || !Equals(id.GetValue(null), whoAmI)) continue;
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return new RegisterMap
                {
                    Device = assembly.GetName().Name.Replace("Harp.", ""),
                    From = assembly.GetName().Name + " " + (version != null ? version.InformationalVersion.Split('+')[0] : assembly.GetName().Version.ToString()),
                    Metadata = (string)metadata.GetValue(null),
                };
            }
            return null;
        }
    }
}
