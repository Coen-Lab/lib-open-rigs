using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>
    /// Passes each value on, and stops the workflow with <see cref="Message"/> at the first null one:
    /// a rig.yml section a block needs and the rig lacks, such as zapit, named at start instead of a
    /// NullReferenceException where the block first reads it.
    /// </summary>
    [Combinator]
    [DefaultProperty("Message")]
    [Description("Passes each value on, and stops the workflow with Message at the first null one, such as a rig.yml section a block needs and the rig lacks.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class RequireValue
    {
        /// <summary>Gets or sets the error the workflow stops with: what is missing and what needs it.</summary>
        [Description("The error the workflow stops with: what is missing and what needs it, such as 'rig.yml has no zapit section, which ConnectZapit needs.'")]
        public string Message { get; set; } = string.Empty;

        /// <summary>Passes each value on, failing at a null one.</summary>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            var message = Message;
            if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("RequireValue has no Message: say what is missing and what needs it.");
            return source.Select(value =>
            {
                if (value == null) throw new InvalidOperationException(message);
                return value;
            });
        }
    }
}
