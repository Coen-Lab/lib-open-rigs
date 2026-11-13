using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;

namespace OpenRigs.Core
{
    /// <summary>
    /// Takes one device's settings out of one kind's rig.yml entries by the device's name in the data,
    /// Kind-Instance: Camera-Box is the <c>box</c> entry under <c>camera</c>. The input is that kind's
    /// entries (a <c>MemberSelector</c> on the workflow's rig configuration), so one operator serves
    /// every kind and every rig model. A name rig.yml does not have, or a malformed or unset name,
    /// stops the workflow at start, naming the entries rig.yml has.
    /// </summary>
    [Combinator]
    [DefaultProperty("Name")]
    [Description("Takes one device's settings from one kind's rig.yml entries (the input) by its name, Kind-Instance: Camera-Box is camera: box:. Outputs the name with the settings, for naming the device's files. Stops the workflow at start, naming the entries rig.yml has, if it has none by that name or the name is unset.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class RigDevice
    {
        static readonly Regex NamePart = new Regex("^[A-Z][A-Za-z0-9]*$");

        /// <summary>Gets or sets the device's name in the data, Kind-Instance.</summary>
        [Description("The device's name in the data: its kind and its rig.yml entry, each starting with a capital, joined by a hyphen, such as BehaviorBoard-Main.")]
        public string Name { get; set; }

        /// <summary>Takes the device named <see cref="Name"/> from each set of entries.</summary>
        // Dictionary, not IDictionary: it is the generated rig classes' own type, which Bonsai matches to infer TDevice.
        public IObservable<NamedDevice<TDevice>> Process<TDevice>(IObservable<Dictionary<string, TDevice>> source)
        {
            return source.Select(entries =>
            {
                var name = Name;
                return new NamedDevice<TDevice>(name, Find(entries, name));
            });
        }

        /// <summary>The entry <paramref name="name"/> names, or an exception saying what rig.yml has instead.</summary>
        public static TDevice Find<TDevice>(IDictionary<string, TDevice> entries, string name)
        {
            // Each part PascalCase, letters and digits only: the name is the device's folder and file
            // names, and a logged file's device is read up to its first underscore.
            var parts = (name ?? string.Empty).Split('-');
            if (parts.Length != 2 || !NamePart.IsMatch(parts[0]) || !NamePart.IsMatch(parts[1]))
            {
                throw new ArgumentException(string.Format(
                    "RigDevice's Name is '{0}'; set it to Kind-Instance, each part starting with a capital and holding only letters and digits, such as Camera-Box.",
                    name));
            }

            var kind = LowerFirst(parts[0]);
            var entry = LowerFirst(parts[1]);
            TDevice device;
            if (entries != null && entries.TryGetValue(entry, out device)) return device;

            var has = entries == null || entries.Count == 0
                ? string.Format("Its {0} section has no entries.", kind)
                : string.Format("Its {0} entries: {1}.", kind, string.Join(", ", entries.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            throw new KeyNotFoundException(string.Format(
                "rig.yml has no {0} named '{1}', which this workflow records as {2}. {3} Add {1} under {0}: in this rig's rig.yml, or correct the name there.",
                kind, entry, name, has));
        }

        static string LowerFirst(string text)
        {
            return char.ToLowerInvariant(text[0]) + text.Substring(1);
        }
    }

    /// <summary>A device's name in the data, Kind-Instance, and its settings from rig.yml.</summary>
    public class NamedDevice<TDevice>
    {
        /// <summary>Creates a named device.</summary>
        public NamedDevice(string name, TDevice settings)
        {
            Name = name;
            Settings = settings;
        }

        /// <summary>Gets the device's name in the data, such as Camera-Box.</summary>
        [Description("The device's name in the data, such as Camera-Box: its folder and the start of its files' names.")]
        public string Name { get; private set; }

        /// <summary>Gets the device's entry in rig.yml.</summary>
        [Description("The device's entry in rig.yml.")]
        public TDevice Settings { get; private set; }
    }
}
