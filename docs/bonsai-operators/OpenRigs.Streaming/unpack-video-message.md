# Unpack Video Message

`UnpackVideoMessage` decodes each received video message back into an image. It is the mirror of [Pack Video Message](pack-video-message.md) and follows [Receive Stream](receive-stream.md) in the same way `UnpackDataMessage` does, differing only in what it does with the payload: JPEG frames are decoded rather than deserialized from JSON.

---

## Bonsai workflow

:::workflow
![UnpackVideoMessage](~/assets/workflows/streaming/UnpackVideoMessage.svg){data-bonsai="~/src/OpenRigs.Streaming/UnpackVideoMessage.bonsai"}
:::

- **DecodeImage** - decodes the JPEG payload into an image.
- **SelectStreamValue** - zips the decoded image back with the message it came from and attaches `SessionKey` and `Index`.

### Externalized properties

| Property | Default | Description |
|----------|---------|-------------|
| `Mode` | `Unchanged` | Optional conversion applied to the decoded image, for example grayscale |

---

## Usage

Subscribe to the camera's stream with `ReceiveStream`, then decode:

```
ReceiveStream -> UnpackVideoMessage -> Value
  RigId = MyRig
  StreamName = video
  ConnectionString = >tcp://rig-machine:5556
```

Connect `Value` to a visualizer to watch the remote camera. Frames arrive at the publisher's `SampleInterval` rate, resized and JPEG compressed. The stream is for monitoring: frames have been through a lossy round trip and should not be analysed.
