using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>A Behavior board write per input line that sets or clears it, such as the ZapitTrigger line.</summary>
    [Combinator]
    [Description("A Behavior board write that sets (Set true) or clears the input line, such as the ZapitTrigger line.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ZapitLineMessage
    {
        /// <summary>Gets or sets whether the line is set; false clears it.</summary>
        [Description("True sets the line high, false clears it.")]
        public bool Set { get; set; }

        /// <summary>The write for each line.</summary>
        public IObservable<HarpMessage> Process(IObservable<DigitalOutputs> source)
        {
            var set = Set;
            return source.Select(line => Message(line, set));
        }

        /// <summary>A Behavior board write that sets (<paramref name="set"/> true) or clears <paramref name="line"/>.</summary>
        public static HarpMessage Message(DigitalOutputs line, bool set)
        {
            return set
                ? OutputSet.FromPayload(MessageType.Write, line)
                : OutputClear.FromPayload(MessageType.Write, line);
        }
    }
}
