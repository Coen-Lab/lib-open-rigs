# OpenRigs.Streaming

Bonsai operators to stream live experiment telemetry over ZeroMQ, and receive it on a remote machine. A rig publishes values, images and events as they happen; a machine elsewhere on the same network subscribes to the
named stream and returns them in their original types.

## Operators

| Operator | Role |
|----------|------|
| `StreamController` | Opens the publisher socket, declares the `OutgoingMessage` subject and defines and publishes rig/session identity. |
| `PackDataMessage` | Serializes a value and packs it into a message with Topic, JSON header and payload. |
| `PackVideoMessage` | Decimates, resizes, encodes and packs video frames into ZeroMQ messages. |
| `ReceiveStream` | Subscribes to a named stream and unpacks to a `StreamMessage`. |
| `UnpackDataMessage` | Deserializes the payload into `SessionKey`, `Index` and `Value`. |
| `UnpackVideoMessage` | Decodes a JPEG payload into an image with `SessionKey` and `Index`. |

## Getting started

Put a `StreamController` at the top of a workflow and set its `RigId`. Send any value over the network by adding a `PackDataMessage` with a `StreamName` property.

On the receiving machine, subscribe to a stream by adding a `ReceiveStream` node with the correct name. Every stream from a rig, data or video, arrives on the same port.

`UnpackDataMessage` parses each message to `SessionKey`, `Index` and `Value`, where `Value` has the type the publisher sent rather than a JSON string. `Index` increments per stream, so gaps can be used to determine if messages were dropped.

Message format is ZeroMQ and JSON, so a consumer need not be Bonsai. Any language with a ZeroMQ binding — Python, for example — can subscribe by topic prefix.

## Dependencies

Installed with the package: `Bonsai.Core`, `Bonsai.Dsp`, `Bonsai.Scripting.Expressions`, `Bonsai.Vision`, `Bonsai.ZeroMQ`, `NetMQ`, `Newtonsoft.Json`, `OpenRigs.Core`.

## Documentation

<https://open-rigs.github.io/open-rigs/bonsai-operators/OpenRigs.Streaming/streaming.html>
