# Pack Video Message

`PackVideoMessage` turns a camera stream into a stream of JPEG frames small enough to watch over a network. It samples the incoming frames down to a viewing rate, resizes them, encodes each as JPEG, and publishes on the socket held by [Stream Controller](stream-controller.md), alongside every data stream. One instance is needed per camera.

---

## Bonsai workflow

:::workflow
![PackVideoMessage](~/assets/workflows/streaming/PackVideoMessage.svg){data-bonsai="~/src/OpenRigs.Streaming/PackVideoMessage.bonsai"}
:::

Frames pass through four steps before packing:

- **SampleInterval** - downsampling in time, taking the most recent frame at each tick.
- **Resize** - downsampling in space, to `FrameSize`.
- **EncodeImage** - JPEG encoding.
- **BuildMessage** - attaches the topic and header, with `Encoding` set to `jpeg` and `PayloadType`
  to `Image`.
- **MulticastSubject** - publishes on `OutgoingMessage`, the same subject [Pack Data Message](pack-data-message.md) uses.

`SampleInterval` samples rather than counting: it emits the *most recent* frame at each tick, so the stream rate is independent of the acquisition rate. 

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `SampleInterval` | `PT0.2S` | How often to take a frame. Downsampling in time, applied before any other work |
| `FrameSize` | `320 x 240` | Size of the streamed frames. Downsampling in space |
| `Interpolation` | `Linear` | Interpolation used when resizing |
| `StreamName` | `video` | The chosen stream name, used in both the topic and the header |

---

## Usage

Connect a camera stream to `PackVideoMessage`:

```
CameraCapture -> PackVideoMessage   (StreamName = video)
```

One instance per camera, each with its own `StreamName`. Hierarchical names group them: cameras published as `video/face` and `video/body` can both be picked up by a subscription to `video`.

On the viewing machine, [Receive Stream](receive-stream.md) subscribes to the stream and [Unpack Video Message](unpack-video-message.md) decodes it.

The defaults are chosen for monitoring, not for analysis or logging. The stream is deliberately lossy and re-encoded. Logging should be done directly at the publishing, acquisition machine.
