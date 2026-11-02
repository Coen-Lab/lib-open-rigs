# Backlog

## Audit 2 Nov 2026 (rig.yml-driven Harp identity, dump and register map): not changed

Fixed in the same round (HarpDevices 0.1.0-local.2, Manifest 0.1.0-local.9): the Harp-device rule was written twice, and the two disagreed on a `portName: null` and on the register map, which the manifest expected for every Harp device although `RecordHarpDevices` writes it only when a loaded package has the board's WhoAmI, so every session of a rig with a Lickety Split or Pico board would have been held back by rig-sync; both now use `src\Shared\HarpRig.cs`, and the map is expected only when a loaded package decodes the kind. The record was written as soon as FirmwareVersionLow arrived, but a board's dump interleaves core and app registers, so the device name, Harp serial and unique id came from the dump at connection, or were null; it now waits until the dump has been quiet for 0.2 s, still bounded by `DumpTimeout`. A device with no `.Commands` said "No register dump came within 5 s" when none was asked for; it now writes at once and says so. A board of the wrong kind stopped the workflow only after Go; the dump at connection now stops it in ready mode. The port name came from WMI's display name, where rig-setup reads the registry's `Device Parameters\PortName`; the registry comes first now. A throw in `update-workflows.ps1` escaped the hourly task's redirect, so the log stopped after "Packages:" with no reason; it is written to the log, with exit 1. Dropped: stopping at Start when a rig.yml entry with a `portName` has no matching subject, which would also stop on a `spare:` board that is ignored on purpose.

- The WhoAmI table copied from open-rigs' `devices\harp.py`: there the values are Python ClassVars, in no schema C# can read; the numbers were checked equal.
- `RecordHarpDevices` not following `--property:ConfigRoot`: a generated group cannot take an ExternalizedMapping (`StreamDevices` has the same limit), and nothing passes it (rig-control does not).
- No BoardClockSet gate on the dump: ControlServer refuses Go until Ready (`ControlServer.cs:484`), and Ready needs a triggered frame, so the clock is set first.
- The `keep` subscription swallowing errors: the same error reaches the per-session path.
- `Thread.Sleep` in the HarpDevices tests: about 3x margins. The Manifest tests' stand-in HarpMessage, now the real one from Harp.Behavior: no clash.
- WMI listing unplugged ports: checked, it lists present ports only.

## Audit 1 Nov 2026 (round 7 of 7: deployment and recovery)

Changed (scripts only, no package): `update-workflows.ps1` took a branch as set up once `.bonsai\Packages` existed, so a setup killed halfway, or one that found no network after `main`'s packages were copied in, was never run again and the branch's check failed every hour; it now asks for Bonsai.exe and a folder for every package `Bonsai.config` names (NuGet's `2.0.7.0` for a config's `2.0.7` included). `push.ps1` pushed a raised `<Version>` that was never built and reported every workflow up to date (round 6, 0a90299); it refuses until each package's version is in `local_packages\`.

Checked and clean: a workflow's `.bonsai\Setup.ps1` waits for Bonsai's restore when its output is captured, as rig-setup's run captures it (a copy of coen-behaviour-box with two package folders removed: both back when it exited, 4 s).

## Audit 1 Nov 2026 (round 6 of 7: live acceptance, with a person at the rig)

Changed (Control 0.1.0-local.9, Manifest 0.1.0-local.5): the manifest is the session's record, and it did not say who ran the session. rig-control kept a record of its own in gui-pyrat's `session_log.json` on the share, which the first real run could not write ("No lab server configured": the lab adapter never set up gui-pyrat's store), and which nothing read. On review it was dropped for the manifest, which travels with the data and is written however the session was started: `go` and `stop` may name a person (`by`, and on `go` `launched_by`, each `{"id", "name"}` or a name alone), ControlState keeps them as `LaunchedBy`, `GoBy` and `StoppedBy`, `status` reports them, and the manifest's `control` section writes them (session-manifest SPEC, version 2, same day). A controller that names nobody leaves them null; an older ControlServer ignores the fields.

## Audit 1 Nov 2026 (round 5 of 5: simplicity, unity, open-rigs compatibility and style): not changed

Fixed in the same round (Control 0.1.0-local.8, LiveView 0.1.0-local.5, Manifest 0.1.0-local.4, no behaviour changed): a package's folder could not move into open-rigs, since its `.csproj` set a licence expression beside open-rigs' licence file (`dotnet pack` there: NU5033, reproduced against open-rigs 77094a4); the shared settings are in `Directory.Build.props` now, as open-rigs keeps them in `build\`, and the Control tests check every `.csproj` holds only what an open-rigs one does. LiveView and Manifest each had their own copy of the git reader, the workflow file, the build-context lookup, the settings node and its property filter, and the rig.yml walk (one copy read Bonsai's relaunch arguments, the other not); they compile `src\Shared\` in. Manifest no longer depends on Control: it reads `ControlState.Current` by name, checked against the real one by its tests. Manifest's helpers (`Git`, `Json`, `ManifestWriter`...) were public; internal now. Every public member has a summary (Control had 40 without, LiveView 22, Manifest 83), and a missing one stops the build. A package's id is its folder's name. `OnGo` was missing from Control's README.

- Console test harnesses where open-rigs uses MSTest (`src\OpenRigs.Logging.Tests`): moving a package upstream moves its tests into open-rigs' test projects and their helpers (`OpenRigs.Tests`) anyway; converting here first would be done twice.
- Manifest's own JSON writer (80 lines) rather than Newtonsoft.Json: Manifest no longer loads Newtonsoft at all, and the overnight copy reads the files byte for byte as written now.
- `Nullable` disabled here where open-rigs enables it: the code has no annotations, and under open-rigs' build `OpenRigs.Control` packed with no warnings (77094a4).
- Stream names `<Kind-Instance>.video` and `.encoder` rather than open-rigs' examples' `video/face`: they name the `<Kind-Instance>.Data` subjects they come from, and rig-setup's live view and rig-control's window read them; rig-setup cannot be changed from a rig.
- The control protocol's keys in snake_case (`subject_id`) where OpenRigs.Streaming's header is camelCase (`rigId`): the protocol is read by Python and written to Python's records, and changing it now is a break in two repositories for no reader's gain.

## Audit 1 Nov 2026 (round 2 of 5: the whole repository): not changed

Raised by the independent audit of every package, script and document here and kept as they are; do not raise again without new information. Fixed in the same round: SessionManifest's failed write threw back up ControlServer's thread and closed Bonsai with nothing finished (Manifest 0.1.0-local.3 ends the node with the error instead), a stop or bind error waited 2 s on the server's own thread (Control 0.1.0-local.7), `set` took `NaN` for a parameter with limits, the live view named `src\src\main.bonsai` for a relative launch path where the manifest names `src\main.bonsai` (LiveView 0.1.0-local.4), a second update-workflows run exited 0 claiming the running one covered its push (it now waits), and Control and LiveView had no tests here (`tests\OpenRigs.Control.Tests`, `tests\OpenRigs.LiveView.Tests`).

- ControlServer does not catch an exception thrown back into its emit of Go or a command. Once `OnNext` has thrown, Rx detaches the observer and swallows any later `OnError` (probed), so the server cannot end the workflow cleanly from there; the fix belongs in the node that threw, as SessionManifest's was.
- `status` carries no parameter limits (`Minimum`, `Maximum`, `WholeNumber`): nothing reads them yet, and a refusal names the range. Round 4 (rig-control's window) decides.
- `ControlState.Go`, `StopReason` and `FinishedAt` are written once and read without the lock: each is one reference or flag, and a stale read cannot give a wrong outcome.
- `ConfigRootNote` looks only for a property named `ConfigRoot`: that is OpenRigs' RigConfig convention.
- A workflow written by an un-indented `XmlWriter` does not reload (Bonsai's group reader); the editor indents, and no lab tool writes workflows.
- The hourly update-workflows meeting the Lab PIN hook: PIPOFFICERIG is a `desk-personal` machine, which rig-setup leaves without the hooks. A run on a rig (push.ps1 there) asks for the PIN, as any commit there does.
- Two ControlServers in one workflow: the second fails to bind and stops the workflow, naming the port.
- The rig.yml device walk and the build-context lookup copied in LiveView and Manifest, two readers of the workflow's commit, and console test harnesses where open-rigs uses MSTest (`src\OpenRigs.Logging.Tests`): round 5's lens, simplicity and open-rigs style.
- Rebuilding a package to compare its DLL with the committed one: builds embed the source path, so the hashes differ whatever the code.
