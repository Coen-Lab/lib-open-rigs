# Receive Stream

`ReceiveStream` subscribes to a single stream on a remote machine and unpacks each arriving message into its topic, header and payload. It is followed by an unpacker matching what the rig packed: `UnpackDataMessage` for a value from [Pack Data Message](pack-data-message.md), or [Unpack Video Message](unpack-video-message.md) for frames from [Pack Video Message](pack-video-message.md). One `ReceiveStream` per stream, each on its own subscription.

---

## Bonsai workflow

:::workflow
![ReceiveStream](~/assets/workflows/streaming/ReceiveStream.svg){data-bonsai="~/src/OpenRigs.Streaming/ReceiveStream.bonsai"}
:::

- **Subscriber** - connects to the publisher and receives only messages whose topic matches the prefix.
- **ParseStreamMessage** - splits the three frames into a `StreamMessage` carrying `Topic`, `Header`, `Payload` and `Valid`.

A message whose header cannot be parsed is marked `Valid = false` and passed through rather than terminating the sequence, so one malformed message cannot take down a long-running display. See [OpenRigs.Streaming.ParseStreamMessage](xref:OpenRigs.Streaming.ParseStreamMessage).

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `RigId` | `MyRig` | Identifier of the rig to subscribe to. Must match the publisher's `RigId` |
| `StreamName` | `data` | Stream to subscribe to. Must match the publisher's `StreamName` |
| `ConnectionString` | `>tcp://127.0.0.1:5556` | Publisher endpoint to connect to, for example `>tcp://rig-machine:5556`. Every stream from a rig is on this one port |

---

## Selecting the payload type

`ReceiveStream` alone gives raw bytes. Follow it with `UnpackDataMessage` and set `Type` to what the publisher sent, and the output reduces to three useful fields, `SessionKey`, `Index` and `Value`, with `Value` already the right type.

:::workflow
![StreamingReceive](~/workflows/StreamingReceive.bonsai)
:::

The type must match what was published, and the `type` header field records what that was. A mismatch stops the workflow with an error naming the stream and both types, for example `Stream MyRig/data/second/ carries Double but the receiver is set to Int32.` The selectable types are declared as attributes on the operator and the list is maintained by hand, so a type that is not yet listed cannot be chosen until it is added. See [OpenRigs.Streaming.UnpackDataMessage](xref:OpenRigs.Streaming.UnpackDataMessage).

`Index` increments per stream, so you can detect dropped messages from non-monotonic increments. Note that gaps are not neccessarily a fault. The publisher drops messages instead of blocking when a subscriber cannot keep up. So dropped frames at the remote monitor does not mean dropped frames at the rig.

---

## Usage

`StreamName` matches by prefix, so subscribing to `data` picks up both `data/first` and `data/second`. That is useful for a display that wants everything a rig emits, but note the streams then interleave in one sequence. Filter on `Topic` to separate them, or subscribe to the full names separately.