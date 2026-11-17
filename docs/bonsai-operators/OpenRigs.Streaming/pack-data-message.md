# Pack Data Message

`PackDataMessage` serializes a value to JSON, wraps it in a topic and header, and publishes it on
the socket held by [Stream Controller](stream-controller.md). It accepts any type, so a stream
can carry a number, a string, a tuple or a schema type without an operator per case. One instance is
needed per logical stream.

---

## Bonsai workflow

:::workflow
![PackDataMessage](~/assets/workflows/streaming/PackDataMessage.svg){data-bonsai="~/src/OpenRigs.Streaming/PackDataMessage.bonsai"}
:::

Each value passes through three steps:

- **SerializeStream** - serializes the value to JSON and records the name of its runtime type.
- **BuildMessage** - attaches the topic and header, with `Encoding` fixed to `json` and
  `PayloadType` taken from the serialized type name.
- **MulticastSubject** - publishes on `OutgoingMessage`, the subject the controller's socket subscribes
  to.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `StreamName` | `data` | The chosen stream name, used in both the topic and the header |

### Stream names

`StreamName` becomes the second segment of the topic, so a value packed as `trial` from rig `MyRig`
publishes on `MyRig/trial/`. Names may be hierarchical: publishing `data/first` and `data/second`
lets a subscriber take both by asking for `data`, or either one by asking for the full name. Give the
`StreamName` without a trailing slash — `ReceiveStream` appends one when it builds the topic, so
`data/` would produce `MyRig/data//` and match nothing.

---

## Usage

Connect any value to `PackDataMessage` and give it a stream name. Here two packers publish an `int` and a `double` on separate streams through the same socket owned by [Stream Controller](stream-controller.md):

:::workflow
![StreamingSend](~/workflows/StreamingSend.bonsai)
:::

One instance per stream. Two packers sharing a `StreamName` will interleave into one stream and corrupt its message index, so give each its own name.

Serialization happens on every message, so avoid pointing a packer at a high-rate source that a subscriber does not need at full rate — decimate with `Sample` or `Throttle` upstream, as [Pack Video Message](pack-video-message.md) does for frames.
