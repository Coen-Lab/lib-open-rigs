# Triggered Spinnaker

The Triggered Spinnaker is a Bonsai wrapper around a FLIR/Spinnaker SDK camera operating in hardware-trigger mode. Each external trigger pulse (typically from a Behavior Board camera trigger output) produces exactly one frame, so the resulting video is aligned to the rig's hardware clock rather than the PC system clock.

---

## .NET type

Unlike the other devices in this section, `TriggeredSpinnaker` is implemented as a C# Bonsai operator and is fully covered by the API reference. See [OpenRigs.Video.TriggeredSpinnaker](xref:OpenRigs.Video.TriggeredSpinnaker) for the property list, frame output type, and method signatures.

The `.bonsai` file in `src/OpenRigs.Devices/` is a thin reusable group around the operator, exposing the camera index and stream subject names as externalized properties so the same module can drive multiple cameras on the rig.

---

## Bonsai workflow

:::workflow
![TriggeredSpinnaker](~/assets/workflows-static/devices/TriggeredSpinnaker.svg){data-bonsai="~/assets/workflows-static/devices/TriggeredSpinnaker.bonsai"}
:::

The workflow passes the camera trigger events into the `TriggeredSpinnaker` operator, publishes the frame stream on a named subject, and forwards each frame's `VideoDataFrame` to downstream consumers (display, encoders, writers). Combine this module with a Behavior Board `CameraTriggerController` to produce a deterministic, Harp-timestamped video stream.

The operator pairs each frame with the next trigger event in arrival order. It copies the chunk data (`FrameID`, `Timestamp`, `ExposureTime`, `Gain`) inside the camera callback and fills dropped frames by `FrameID`, which keeps the pairing aligned for the rest of the session.

---

## Camera configuration

`Configure` stamps every acquisition-relevant setting onto the camera at the start of each session rather than trusting whatever the camera was last left in. Trigger mode and source, exposure with `ExposureAuto` off, gain with `GainAuto` off, binning, pixel format and the sensor region are all set from the operator's properties or to fixed values. A session is therefore reproducible from the workflow and the rig configuration alone, whatever SpinView or another program left behind.

Two of these deserve calling out, because a camera remembers them across programs until it is power cycled.

`PixelFormat` is applied before binning, because a format change can reset the size limits. It defaults to `Mono8`, which the 8-bit mono video writers expect. Leave it empty to apply no format at all and record in whatever the camera is already set to.

The sensor region is reset to the full sensor immediately after binning. Changing binning does not resize the region on a Spinnaker camera, and a camera whose stored user set carries a crop would otherwise record that crop at the requested binning with nothing to warn you. A region set deliberately in SpinView does not survive this reset.

---

## Start order

Every frame is paired with a trigger event in order. A trigger that fires before the camera has begun acquisition produces no frame, and every later frame would then be paired with a trigger from earlier in time. The camera triggers must therefore start only after every camera is ready.

Each camera reports once on the shared `CameraReady` subject, declared by the `CameraTriggerController`, when it is initialized and configured and about to begin acquisition. The controller holds any `StartCameras` request until `CameraCount` cameras have reported, then starts the triggers 0.1 s later. Nothing extra is wired in the rig workflow: set `camera_count` in the rig configuration to the number of triggered cameras and map it onto the controller's `CameraCount`.

If the cameras have not all reported within `CameraReadyTimeout` (30 s by default), the workflow stops with an error naming how many reported. A camera that is disconnected or held by another program such as SpinView is the usual cause. A `CameraCount` of zero starts the triggers without waiting, which keeps the old behaviour for rigs that have not set it.

This follows the start order used at AIND (cameras initialized, then triggers started) in front of the `StartCameras` gate used by Aeon.
