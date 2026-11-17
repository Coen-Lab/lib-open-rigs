using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Streaming
{
    /// <summary>
    /// Represents an operator that attaches the session key and message index of a received
    /// message to a value derived from it, producing the same shape as
    /// <see cref="UnpackDataMessage"/>. Use where the payload is decoded by a dedicated
    /// operator rather than deserialized, such as an image inside the UnpackVideoMessage
    /// workflow.
    /// </summary>
    [Combinator]
    [Description("Attaches the session key and message index of a received message to a value derived from it.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SelectStreamValue
    {
        /// <summary>
        /// Attaches message identity to each decoded value in an observable sequence.
        /// </summary>
        /// <typeparam name="TValue">The type of the decoded value.</typeparam>
        /// <param name="source">A sequence pairing each decoded value with the message it came from.</param>
        /// <returns>A sequence of payloads carrying the session key, message index and value.</returns>
        public IObservable<StreamPayload<TValue>> Process<TValue>(IObservable<Tuple<TValue, StreamMessage>> source)
        {
            return source.Select(pair => new StreamPayload<TValue>
            {
                SessionKey = pair.Item2.Header.SessionKey,
                Index = pair.Item2.Header.Index,
                Value = pair.Item1
            });
        }
    }
}
