using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Passes one kind's rig.yml entries on when there are exactly <see cref="Count"/> of them, and
    /// stops the workflow otherwise, naming the entries and where the rule is set.
    /// </summary>
    [Combinator]
    [Description("Passes one kind's rig.yml entries on if there are exactly Count; stops the workflow otherwise, naming them and Rule.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class RequireEntries
    {
        /// <summary>Gets or sets the kind, as rig.yml names it, for the message.</summary>
        [Description("The kind, as rig.yml names it, such as camera.")]
        public string Kind { get; set; } = "camera";

        /// <summary>Gets or sets how many entries there must be.</summary>
        [Description("How many entries there must be.")]
        public int Count { get; set; } = 3;

        /// <summary>Gets or sets where the rule is set, for the message.</summary>
        [Description("Where the rule is set, for the message.")]
        public string Rule { get; set; } = "dome rig model, open_rigs.rigs.dome";

        /// <summary>Checks each set of entries.</summary>
        public IObservable<Dictionary<string, TDevice>> Process<TDevice>(IObservable<Dictionary<string, TDevice>> source)
        {
            return source.Select(entries =>
            {
                Check(entries == null ? new string[0] : entries.Keys, Kind, Count, Rule);
                return entries;
            });
        }

        /// <summary>Throws unless there are exactly <paramref name="count"/> names.</summary>
        public static void Check(ICollection<string> names, string kind, int count, string rule)
        {
            if (names.Count == count) return;
            throw new InvalidOperationException(string.Format(
                "rig.yml lists {0} {1} entries ({2}); it needs exactly {3} ({4}).",
                names.Count, kind, names.Count == 0 ? "none" : string.Join(", ", names), count, rule));
        }
    }
}
