using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Bonsai.Expressions;
using Newtonsoft.Json;
using OpenRigs.Core.DataTypes;

namespace OpenRigs.Streaming
{
    /// <summary>
    /// Represents a received payload, reduced to the fields that vary from message to message.
    /// </summary>
    /// <typeparam name="T">The type the payload was serialized from.</typeparam>
    public class StreamPayload<T>
    {
        /// <summary>
        /// Gets or sets the session the message belongs to. It changes when a new session starts.
        /// </summary>
        public string SessionKey { get; set; }

        /// <summary>
        /// Gets or sets the per-stream message index. A gap indicates dropped messages.
        /// </summary>
        public long Index { get; set; }

        /// <summary>
        /// Gets or sets the deserialized payload.
        /// </summary>
        public T Value { get; set; }
    }

    /// <summary>
    /// Represents an operator that deserializes the payload of each received message into the
    /// specified type, keeping only the session key and message index. It is the mirror of
    /// the PackDataMessage workflow. Messages whose header could not be parsed are dropped.
    /// A message whose header names a different type from the one selected fails with an
    /// error naming the stream and both types; a header with no type is deserialized as is.
    /// </summary>
    /// <remarks>
    /// The selectable types are the XmlInclude attributes below, and the list is maintained by
    /// hand. Add an entry to stream a type that is not yet listed; without one the workflow
    /// cannot store the choice. Vector2 and Vector3 show the pattern for schema types from
    /// OpenRigs.Core.
    /// </remarks>
    [DefaultProperty("Type")]
    [Description("Deserializes the payload of each received message into the specified type.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [XmlInclude(typeof(TypeMapping<string>))]
    [XmlInclude(typeof(TypeMapping<bool>))]
    [XmlInclude(typeof(TypeMapping<int>))]
    [XmlInclude(typeof(TypeMapping<long>))]
    [XmlInclude(typeof(TypeMapping<float>))]
    [XmlInclude(typeof(TypeMapping<double>))]
    [XmlInclude(typeof(TypeMapping<Vector2>))]
    [XmlInclude(typeof(TypeMapping<Vector3>))]
    public class UnpackDataMessage : SingleArgumentExpressionBuilder
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnpackDataMessage"/> class.
        /// </summary>
        public UnpackDataMessage()
        {
            Type = new TypeMapping<string>();
        }

        /// <summary>
        /// Gets or sets the type the payload will be deserialized into. It must match the type
        /// the publisher serialized, which is reported in the message header.
        /// </summary>
        public TypeMapping Type { get; set; }

        /// <inheritdoc/>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var typeMapping = (TypeMapping)Type;
            var returnType = typeMapping.GetType().GetGenericArguments()[0];
            return Expression.Call(
                typeof(UnpackDataMessage),
                "Process",
                new[] { returnType },
                Enumerable.Single(arguments));
        }

        private static IObservable<StreamPayload<T>> Process<T>(IObservable<StreamMessage> source)
        {
            var expectedType = typeof(T).Name;
            return source.Where(message => message.Valid).Select(message =>
            {
                var header = message.Header;
                if (!string.IsNullOrEmpty(header.Type) && header.Type != expectedType)
                {
                    throw new InvalidOperationException(string.Format(
                        "Stream {0} carries {1} but the receiver is set to {2}.",
                        message.Topic, header.Type, expectedType));
                }

                return new StreamPayload<T>
                {
                    SessionKey = header.SessionKey,
                    Index = header.Index,
                    Value = JsonConvert.DeserializeObject<T>(message.Text)
                };
            });
        }
    }
}
