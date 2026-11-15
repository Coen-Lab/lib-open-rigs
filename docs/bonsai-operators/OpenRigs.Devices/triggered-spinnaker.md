# Triggered Spinnaker

The Triggered Spinnaker is a Bonsai wrapper around a FLIR/Spinnaker SDK camera operating in hardware-trigger mode. Each external trigger pulse (typically from a Behavior Board camera trigger output) produces exactly one frame, so the resulting video is aligned to the rig's hardware clock rather than the PC system clock.

---

## .NET type

`TriggeredSpinnaker` is a C# Bonsai operator, covered by the API reference: see [OpenRigs.Video.TriggeredSpinnaker](xref:OpenRigs.Video.TriggeredSpinnaker). Its `Name` identifies the camera: once the camera is streaming it announces `Name` to `CamerasOpen`, and `CameraLiveSetting` finds a running camera by it. A blank `PixelFormat` keeps the format stored on the camera, so a rig states it. Its triggered output pairs each frame with its trigger pulse's Harp time in order and leaves a dropped frame as a gap in `frameId`, the later times kept.

---

## Bonsai workflow

`OpenRigs.Devices:SpinnakerCamera.bonsai` is the camera's device piece (see [Building Blocks](../../articles/building-blocks.md)). Its `Name` (`Camera-<Entry>`) picks the camera's entry under `camera` in `rig.yml` (`serialNumber`, `exposureTime`, `gain`, `binning`, `pixelFormat`). Its trigger is the Behavior board `Board`'s camera trigger whose `wiring` line (device `CameraTrigger`, DO0 or DO1) names it in `targets`; the workflow stops at Start, naming the camera, unless exactly one enabled trigger names it and its exposure fits the trigger period with 1 ms to spare (`CameraTrigger`). The board's `CameraTriggers` core include starts the triggers once every camera has announced itself, so no camera misses the first trigger. Frames are paired with the board's trigger Event messages only (`CameraTriggerPulses`) and published on `<Name>.Data`; the first frame announces `<Name>` into `ReadyStages`. No frame for 15 s stops the workflow, naming the camera (`GapWatchdog`). The piece logs its own video with `LogVideoFfmpeg` as `<Name>`, every frame from the first, at the trigger's frequency. `CameraLiveControls` adds a camera's exposure and gain as live experiment parameters.

The include needs the Spinnaker SDK to load, so it has no rendered image here.
