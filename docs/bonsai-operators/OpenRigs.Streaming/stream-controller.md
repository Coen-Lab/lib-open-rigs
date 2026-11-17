# Stream Controller

The `StreamController` operator owns the ZeroMQ publisher socket and the identity carried by every streamed message. It assembles a `StreamIdentity` subject from the rig ID, subject ID and session ID, which all packing operators consume to build their topic and header. There should be a `StreamController` at the top of any workflow that streams. It must exist so that the `StreamIdentity` subject is available, and so that something binds the socket. Without it the packing operators have nowhere to publish.

---

## Bonsai workflow

:::workflow
![StreamController](~/assets/workflows/streaming/StreamController.svg){data-bonsai="~/src/OpenRigs.Streaming/StreamController.bonsai"}
:::

The operator publishes the rig identity once, then opens one publisher socket bound to `@tcp://0.0.0.0:5556`. It is fed by the `OutgoingMessage` subject, which carries everything from [Pack Data Message](pack-data-message.md) and [Pack Video Message](pack-video-message.md) alike. Any number of streams share the socket, separated by topic, and a payload's encoding is recorded in its header rather than implied by the port it arrives on.

Packing operators reach the socket through a `MulticastSubject` rather than a direct connection, so a `StreamController` anywhere in the workflow serves every packer.

One socket is enough because ZeroMQ filters subscriptions at the publisher and queues per subscription. A viewer that subscribes to `data` never receives a video frame, and a slow video viewer fills only its own queue.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `RigId` | `MyRig` | The rig identifier included in every published topic |
| `SubjectId` | `Algernon` | The subject identifier, matching the logging session directory |
| `SessionId` | `001` | The session identifier, matching the logging session directory |

`SubjectId` and `SessionId` combine into the `sessionKey` header field. Setting them to match the values given to `LogController` is what lets a streamed record be traced back to the data on disk.

---

## Usage

One `StreamController` per workflow. Its `StreamIdentity` and `OutgoingMessage` subjects are named and workflow global, exactly as `LogController`'s `PathPrefix` is, and a second controller would redeclare them. `RigId` must match the `RigId` set on any [Receive Stream](receive-stream.md).

The default `0.0.0.0` binds on all interfaces, which is what cross-machine streaming needs. See the [overview](streaming.md#cross-machine-setup).
