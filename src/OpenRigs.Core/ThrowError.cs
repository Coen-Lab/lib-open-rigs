using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Core
{
    /// <summary>Stops the workflow with an error carrying each input string as its message.</summary>
    [Combinator]
    [Description("Stops the workflow with an InvalidOperationException carrying the input string as its message, so a check fails loudly instead of stalling.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class ThrowError
    {
        /// <summary>Throws on each input.</summary>
        public IObservable<string> Process(IObservable<string> source)
        {
            return source.Do(message => { throw new InvalidOperationException(message); });
        }
    }
}
