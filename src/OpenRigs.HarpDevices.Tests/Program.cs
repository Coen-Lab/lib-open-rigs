using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using Bonsai.Harp;
using OpenRigs.HarpDevices;

// Runs RecordHarpDevices' plan, its record and its dump request against fake device messages and a
// temporary experiment folder, and builds a workflow with it, without Bonsai's editor or hardware:
// exit 0 when every check passes. dotnet run --project src\OpenRigs.HarpDevices.Tests
namespace OpenRigs.HarpDevices.Tests
{
    static class Program
    {
        static int failed;

        static void Check(string what, bool ok, string why)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (ok ? "" : " - " + why));
            if (!ok) failed++;
        }

        // Polled, never a fixed sleep (audit-rigs set B, L2-L2): a record written on the Rx timer
        // thread is waited for up to a bound, so a slow machine passes and a fast one does not wait.
        static void WaitFor(Func<bool> done, int ms = 3000)
        {
            for (var w = Stopwatch.StartNew(); !done() && w.ElapsedMilliseconds < ms;) Thread.Sleep(10);
        }

        static string Error(Action action)
        {
            try { action(); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        static HarpMessage U8(int address, byte value)
        {
            return HarpMessage.FromByte(address, MessageType.Read, value);
        }

        // What GEEK-BBOX1's Behavior board reported on 2 Nov 2026: WhoAmI 1216, hardware 1.2,
        // core 1.11, firmware 3.1, Harp serial unset (65535), and no unique id (core before 1.12).
        static IEnumerable<HarpMessage> Board(int whoAmI)
        {
            yield return HarpMessage.FromUInt16(0, MessageType.Read, (ushort)whoAmI);
            yield return U8(1, 1);
            yield return U8(2, 2);
            yield return U8(3, 0);
            yield return U8(4, 1);
            yield return U8(5, 11);
            yield return U8(6, 3);
            yield return U8(7, 1);
            var name = new byte[25];
            Encoding.ASCII.GetBytes("Behavior").CopyTo(name, 0);
            yield return HarpMessage.FromByte(12, MessageType.Read, name);
            yield return HarpMessage.FromUInt16(13, MessageType.Read, 65535);
        }

        static Dictionary<string, string> Ports(params string[] pairs)
        {
            var ports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < pairs.Length; i += 2) ports[pairs[i]] = pairs[i + 1];
            return ports;
        }

        static RecordHarpDevice Recorder()
        {
            return new RecordHarpDevice
            {
                Name = "BehaviorBoard-Main", Entry = "rig.yml behaviorBoard: main:", PortName = "COM3",
                UsbSerial = "AB0M7PLB", ExpectedWhoAmI = 1216, DumpTimeout = TimeSpan.FromMilliseconds(600), DumpQuiet = TimeSpan.FromMilliseconds(100),
            };
        }

        // The dump as GEEK-BBOX1's board sent it on 2 Nov 2026 (ses-909's .bin): each core register
        // followed by the device's own at 32 on, so the device name, Harp serial and unique id
        // come after FirmwareVersionLow (7). Here with a unique id, as a core from 1.12 has.
        static IEnumerable<HarpMessage> Interleaved(int whoAmI)
        {
            var uid = new byte[16];
            for (var i = 0; i < uid.Length; i++) uid[i] = (byte)(i + 1);
            foreach (var core in Board(whoAmI).Concat(new[] { HarpMessage.FromByte(16, MessageType.Read, uid) }))
            {
                yield return core;
                yield return U8(32 + core.Address, 0);
            }
        }

        static int Main()
        {
            // Loaded before any record looks for a register map, as a workflow decoding the board has it.
            // The type, not its WhoAmI: a const is copied in at compile time and loads nothing.
            Console.WriteLine("Loaded " + typeof(Harp.Behavior.Device).Assembly.GetName().Name);
            Console.WriteLine("Loaded " + typeof(Harp.TimestampGeneratorGen3.Device).Assembly.GetName().Name);
            var root = Path.Combine(Path.GetTempPath(), "harpdevices-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Console.WriteLine("USB serials, as rig-setup's scan reads them:");
                Check("an FTDI chip's is its parent's id's last part", UsbSerials.FromIds(@"FTDIBUS\VID_0403+PID_6001+AB0M7PLBA\0000", @"USB\VID_0403&PID_6001\AB0M7PLB") == "AB0M7PLB", "wrong");
                Check("a Pico's, a composite device's interface, is its parent's", UsbSerials.FromIds(@"USB\VID_2E8A&PID_000A&MI_00\6&2B157701&0&0000", @"USB\VID_2E8A&PID_000A\E664C856C3913E37") == "E664C856C3913E37", "wrong");
                Check("a plain USB device's is its own", UsbSerials.FromIds(@"USB\VID_1234&PID_0001\SER123", @"USB\ROOT_HUB30\4&1&0&0") == "SER123", "wrong");
                Check("one Windows made up is none", UsbSerials.FromIds(@"USB\VID_1234&PID_0001\5&2A3B&0&2", null) == "", "not empty");
                Check("a motherboard COM1 has none", UsbSerials.FromIds(@"ACPI\PNP0501\0", @"ACPI\PNP0A08\0") == "", "not empty");
                var present = Error(() => Console.WriteLine("        this machine's ports: " + string.Join(", ", UsbSerials.Present().Select(p => p.Key + "=" + (p.Value.Length > 0 ? p.Value : "(none)")))));
                Check("this machine's ports read from Windows without error", present == null, present);
                Check("the port from a device's name",UsbSerials.PortOf("USB Serial Port (COM3)") == "COM3" && UsbSerials.PortOf("Communications Port (COM12)") == "COM12" && UsbSerials.PortOf("Printer Port (LPT1)") == null, "wrong");

                Console.WriteLine("Register maps:");
                var behavior = OpenRigs.Shared.RegisterMaps.Find(1216);
                Check("a package that carries its device.yml gives it", behavior != null && behavior.From == "Harp.Behavior 0.2.0" && behavior.Metadata.Contains("whoAmI: 1216"), behavior == null ? "none" : behavior.From);
                var tsg = OpenRigs.Shared.RegisterMaps.Find(1158);
                Check("Harp.TimestampGeneratorGen3 0.1.1, which carries none, gets the lab's copy of its release's, saying so", tsg != null && tsg.Metadata.Contains("whoAmI: 1158") && tsg.Metadata.Contains("firmwareVersion: \"1.2\"") && tsg.From.StartsWith("Harp.TimestampGeneratorGen3 0.1.1 (device.yml from harp-tech/device.timestampgeneratorgen3 a463d1d"), tsg == null ? "none" : tsg.From);
                Check("... and no other version does", OpenRigs.Shared.RegisterMaps.LabCopy("Harp.TimestampGeneratorGen3 0.1.2") == null, "found one");
                Check("every lab copy is embedded", OpenRigs.Shared.RegisterMaps.LabCopies.Keys.All(k => OpenRigs.Shared.RegisterMaps.LabCopy(k) != null), "one is missing");

                Console.WriteLine("The board at Start:");
                var r = Recorder();
                Check("the board rig.yml names, on its port, passes", Error(() => r.CheckBoard(Ports("COM3", "AB0M7PLB", "COM4", ""))) == null, "refused");
                Check("... its serial in any case", Error(() => r.CheckBoard(Ports("COM3", "ab0m7plb"))) == null, "refused");
                var swapped = Error(() => r.CheckBoard(Ports("COM3", "AQ03WRBO", "COM5", "AB0M7PLB")));
                Check("another board on the port stops it, saying where the right one is", swapped != null && swapped.Contains("AQ03WRBO") && swapped.Contains("on COM5"), swapped);
                var gone = Error(() => r.CheckBoard(Ports("COM3", "AQ03WRBO")));
                Check("... or that it is not plugged in, and the serial to give a replacement", gone != null && gone.Contains("not plugged in") && gone.Contains("usbSerial in rig.yml to 'AQ03WRBO'"), gone);
                var empty = Error(() => r.CheckBoard(Ports("COM4", "AB0M7PLB")));
                Check("nothing on the port stops it, saying where the board is", empty != null && empty.StartsWith("Nothing is plugged in on COM3") && empty.Contains("on COM4"), empty);
                var unnamed = Recorder();
                unnamed.UsbSerial = null;
                var missing = Error(() => unnamed.CheckBoard(Ports("COM3", "AB0M7PLB")));
                Check("no usbSerial in rig.yml stops it, giving the line to add", missing != null && missing.Contains("has no usbSerial") && missing.Contains("usbSerial: 'AB0M7PLB'"), missing);

                Console.WriteLine("The record:");
                RecordHarpDevice.PresentPorts = () => Ports("COM3", "AB0M7PLB");
                {
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    var written = new List<string>();
                    Exception error = null;
                    // A wide quiet, so a stalled test thread is not read as the dump having ended
                    // before the negative check below; the record lands on the timer's thread.
                    var waiting = Recorder();
                    waiting.DumpQuiet = TimeSpan.FromMilliseconds(500);
                    waiting.DumpTimeout = TimeSpan.FromMilliseconds(2000);
                    using (waiting.Process(messages, path).Subscribe(p => { lock (written) written.Add(p); }, ex => error = ex))
                    {
                        foreach (var m in Board(1216)) messages.OnNext(m);  // at connection
                        var experiment = Path.Combine(root, "ses-1");
                        path.OnNext(experiment);
                        foreach (var m in Board(1216)) messages.OnNext(m);  // the dump asked for
                        int early;
                        lock (written) early = written.Count;
                        Check("waits until the dump has finished", early == 0, early.ToString());
                        WaitFor(() => written.Count > 0 || error != null);
                        var json = File.ReadAllText(Path.Combine(experiment, "BehaviorBoard-Main", "BehaviorBoard-Main_device.json"));
                        Check("writes <Name>/<Name>_device.json when the dump has come", written.Count == 1 && error == null, written.Count + " " + error);
                        Check("... with the board's versions and serials", json.Contains("\"who_am_i\": 1216") && json.Contains("\"firmware\": \"3.1\"") && json.Contains("\"hardware\": \"1.2\"") && json.Contains("\"core\": \"1.11\"")
                            && json.Contains("\"harp_serial\": 65535") && json.Contains("\"usb_serial\": \"AB0M7PLB\"") && json.Contains("\"device_name\": \"Behavior\"") && json.Contains("\"uid\": null") && json.Contains("\"dumped\": true"), json);
                        Check("... with no note that the kind was checked late, as WhoAmI came at connection", !json.Contains("checked only after the experiment started"), json);
                        var map = Path.Combine(experiment, "BehaviorBoard-Main", "BehaviorBoard-Main.yml");
                        Check("... and the register map of the Harp package with its WhoAmI", File.Exists(map) && File.ReadAllText(map).Contains("whoAmI: 1216") && json.Contains("\"register_map_from\": \"Harp.Behavior 0.2.0\""), json);

                        var quiet = Path.Combine(root, "ses-2");
                        path.OnNext(quiet);
                        WaitFor(() => written.Count > 1 || error != null);
                        var late = File.ReadAllText(Path.Combine(quiet, "BehaviorBoard-Main", "BehaviorBoard-Main_device.json"));
                        Check("no dump within the timeout writes what the board reported at connection, and says so", late.Contains("\"dumped\": false") && late.Contains("\"who_am_i\": 1216") && late.Contains("No register dump came"), late);
                    }
                }
                {
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    Exception error = null;
                    using (Recorder().Process(messages, path).Subscribe(_ => { }, ex => error = ex))
                    {
                        var experiment = Path.Combine(root, "ses-3");
                        path.OnNext(experiment);
                        foreach (var m in Board(1158)) messages.OnNext(m);
                        WaitFor(() => error != null);
                        Check("a board of another kind stops the workflow, naming it", error != null && error.Message.Contains("WhoAmI 1158") && error.Message.Contains("reports 1216"), error == null ? "no error" : error.Message);
                        Check("... after recording it", File.Exists(Path.Combine(experiment, "BehaviorBoard-Main", "BehaviorBoard-Main_device.json")), "no record");
                    }
                }
                {
                    // M2 (audit, 2 Nov 2026): the record was written at register 7, before 12, 13 and 16 had come.
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    var written = new List<string>();
                    using (Recorder().Process(messages, path).Subscribe(written.Add))
                    {
                        var experiment = Path.Combine(root, "ses-4");
                        path.OnNext(experiment);
                        foreach (var m in Interleaved(1216)) messages.OnNext(m);  // no dump at connection
                        WaitFor(() => written.Count > 0);
                        var json = written.Count == 1 ? File.ReadAllText(written[0]) : "";
                        Check("a dump with the core's registers after 7 is recorded whole", json.Contains("\"device_name\": \"Behavior\"") && json.Contains("\"harp_serial\": 65535")
                            && json.Contains("\"uid\": \"0102030405060708090a0b0c0d0e0f10\"") && json.Contains("\"dumped\": true"), written.Count + " " + json);
                        // L2-I1: a wrong kind could stop the workflow before Go only from a WhoAmI at connection.
                        Check("... and says the kind was checked only after the experiment started, as nothing came at connection", json.Contains("checked only after the experiment started"), json);
                    }
                }
                {
                    // L1: no <Name>.Commands, so no dump to wait for.
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    var written = new List<string>();
                    var noCommands = Recorder();
                    noCommands.HasCommands = false;
                    using (noCommands.Process(messages, path).Subscribe(written.Add))
                    {
                        foreach (var m in Board(1216)) messages.OnNext(m);
                        path.OnNext(Path.Combine(root, "ses-5"));
                        var json = written.Count == 1 ? File.ReadAllText(written[0]) : "";
                        Check("with no dump asked for, writes at once what the board reported at connection, and says no dump was asked for", json.Contains("\"dumped\": false") && json.Contains("No register dump was asked for") && json.Contains("BehaviorBoard-Main.Commands"), written.Count + " " + json);
                    }
                }
                {
                    // 0.1.0-local.3: a board that reported no WhoAmI still leaves the map the manifest
                    // expects for its kind (audit, 3 Nov 2026: the experiment was held back every night).
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    Exception error = null;
                    using (Recorder().Process(messages, path).Subscribe(_ => { }, ex => error = ex))
                    {
                        foreach (var m in Board(1216).Where(m => m.Address != WhoAmI.Address)) messages.OnNext(m);
                        var silent = Path.Combine(root, "ses-silent");
                        path.OnNext(silent);
                        var silentRecord = Path.Combine(silent, "BehaviorBoard-Main", "BehaviorBoard-Main_device.json");
                        WaitFor(() => File.Exists(silentRecord) || error != null);
                        var json = File.ReadAllText(silentRecord);
                        var map = Path.Combine(silent, "BehaviorBoard-Main", "BehaviorBoard-Main.yml");
                        Check("a board that reported no WhoAmI gets the map of the kind rig.yml names, and the record says so",
                            error == null && json.Contains("\"who_am_i\": null") && File.Exists(map) && File.ReadAllText(map).Contains("whoAmI: 1216") && json.Contains("reported no WhoAmI"), error == null ? json : error.Message);
                    }
                }
                {
                    // The wiring check's refusal stops the workflow at Start, before the board is looked at.
                    var refused = Recorder();
                    refused.WiringRefusal = "BehaviorBoard-Main's wiring in rig.yml does not agree";
                    Exception error = null;
                    using (refused.Process(new Subject<HarpMessage>(), new Subject<string>()).Subscribe(_ => { }, ex => error = ex))
                        Check("a wiring refusal stops the workflow at Start with its message", error != null && error.Message == refused.WiringRefusal, error == null ? "no error" : error.Message);
                }
                {
                    // L2-L3: an error on the board's messages before any experiment was dropped.
                    var messages = new Subject<HarpMessage>();
                    Exception error = null;
                    using (Recorder().Process(messages, new Subject<string>()).Subscribe(_ => { }, ex => error = ex))
                    {
                        messages.OnError(new InvalidOperationException("port closed"));
                        Check("an error on the board's messages before any experiment stops the workflow", error != null && error.Message == "port closed", error == null ? "no error" : error.Message);
                    }
                }
                {
                    // L2: a board of the wrong kind is found from the dump at connection, before Go.
                    var messages = new Subject<HarpMessage>();
                    Exception error = null;
                    using (Recorder().Process(messages, new Subject<string>()).Subscribe(_ => { }, ex => error = ex))
                    {
                        foreach (var m in Board(1158)) messages.OnNext(m);
                        Check("a board of another kind stops the workflow before any experiment, from the dump at connection", error != null && error.Message.Contains("WhoAmI 1158") && error.Message.Contains("reports 1216"), error == null ? "no error" : error.Message);
                    }
                    error = null;
                    using (Recorder().Process(messages, new Subject<string>()).Subscribe(_ => { }, ex => error = ex))
                    {
                        foreach (var m in Board(1216)) messages.OnNext(m);
                        Check("... and the right kind does not", error == null, error == null ? "" : error.Message);
                    }
                }
                {
                    RecordHarpDevice.PresentPorts = () => Ports("COM3", "AQ03WRBO");
                    Exception error = null;
                    using (Recorder().Process(new Subject<HarpMessage>(), new Subject<string>()).Subscribe(_ => { }, ex => error = ex)) { }
                    Check("the wrong board stops it at Start", error != null && error.Message.Contains("AQ03WRBO"), error == null ? "no error" : error.Message);
                }

                Console.WriteLine("Wiring against the workflow's lines:");
                {
                    var rigText = "behaviorBoard:\n  main:\n    portName: COM6\n    usbSerial: X\n" +
                        "    cameraTriggerController: { trigger0Enabled: true, trigger0Frequency: 50, trigger1Enabled: false, trigger1Frequency: 50 }\n" +
                        "    wiring:\n" +
                        "      DIPort0: { device: Photodiode, note: sync square }\n" +
                        "      DO0: { device: CameraTrigger, targets: [Camera-Face, Camera-Pupil, Camera-Body] }\n" +
                        "      DO1: { device: RewardValve, side: left, part: '?' }\n" +
                        "      DO2: { device: RewardValve, side: right }\n" +
                        "      DO3: { device: ZapitTrigger }\n" +
                        "camera:\n  face: { serialNumber: '1' }\n  pupil: { serialNumber: '2' }\n  body: { serialNumber: '3' }\n" +
                        "rewardValves: { left: DO1, right: DO2 }\nzapit: { hardwareTriggered: true, triggerLine: 3 }\n";
                    var linesText = "BehaviorBoard-Main:\n" +
                        "  - { line: cameraTriggerController.trigger0, device: CameraTrigger, targets: [Camera-Face, Camera-Pupil, Camera-Body] }\n" +
                        "  - { line: cameraTriggerController.trigger1, device: CameraTrigger }\n" +
                        "  - { line: rewardValves.left, device: RewardValve, side: left }\n" +
                        "  - { line: rewardValves.right, device: RewardValve, side: right }\n" +
                        "  - { line: zapit.triggerLine, device: ZapitTrigger }\n" +
                        "  - { line: DIPort0, device: Photodiode }\n";
                    Func<string, string, WiringResult> wiringOf = (rigYml, lines) =>
                    {
                        var stream = new YamlDotNet.RepresentationModel.YamlStream();
                        stream.Load(new StringReader(rigYml));
                        var rigRoot = (YamlDotNet.RepresentationModel.YamlMappingNode)stream.Documents[0].RootNode;
                        var boardNode = (YamlDotNet.RepresentationModel.YamlMappingNode)((YamlDotNet.RepresentationModel.YamlMappingNode)rigRoot.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("behaviorBoard")]).Children[new YamlDotNet.RepresentationModel.YamlScalarNode("main")];
                        var device = new OpenRigs.Shared.RigDevice { Kind = "behaviorBoard", Instance = "main", Name = "BehaviorBoard-Main", Settings = boardNode };
                        return WiringCheck.Check(rigRoot, device, new[] { "BehaviorBoard-Main", "Camera-Face", "Camera-Pupil", "Camera-Body" }, lines, "av-dome");
                    };
                    var agree = wiringOf(rigText, linesText);
                    Check("the dome's wiring agrees with av-dome's lines", agree.Applies && agree.Problems.Count == 0, string.Join(" | ", agree.Problems));
                    Check("... resolving rewardValves.left to DO1, zapit.triggerLine to DO3 and the enabled trigger to DO0, not the disabled one",
                        agree.Summary.Contains("DO1 left RewardValve") && agree.Summary.Contains("DO3 ZapitTrigger") && agree.Summary.Contains("DO0 CameraTrigger") && !agree.Summary.Contains("DO1 CameraTrigger"), agree.Summary);
                    var sideSwapped = wiringOf(rigText.Replace("side: left, part", "side: right, part"), linesText);
                    Check("a valve on the other side refuses", sideSwapped.Problems.Any(p => p.StartsWith("DO1 (rewardValves.left) is used as the left RewardValve")), string.Join(" | ", sideSwapped.Problems));
                    var unknown = wiringOf(rigText.Replace("      DIPort0: { device: Photodiode, note: sync square }\n", ""), linesText);
                    Check("a line the workflow uses and the wiring leaves unknown refuses", unknown.Problems.Any(p => p.StartsWith("DIPort0 is used as Photodiode, but rig.yml's wiring does not say")), string.Join(" | ", unknown.Problems));
                    var unused = wiringOf(rigText + "", linesText.Replace("  - { line: DIPort0, device: Photodiode }\n", ""));
                    Check("a line the workflow does not use may be anything", unused.Problems.Count == 0, string.Join(" | ", unused.Problems));
                    var other = wiringOf(rigText.Replace("DO3: { device: ZapitTrigger }", "DO3: { device: AirPuff }"), linesText);
                    Check("another device on a used line refuses, naming both", other.Problems.Any(p => p.Contains("ZapitTrigger") && p.Contains("AirPuff")), string.Join(" | ", other.Problems));
                    var camera = wiringOf(rigText.Replace("targets: [Camera-Face, Camera-Pupil, Camera-Body] }\n      DO1", "targets: [Camera-Face, Camera-Side] }\n      DO1"), linesText);
                    Check("a target the workflow needs and the wiring lacks refuses, and so does one rig.yml does not have",
                        camera.Problems.Any(p => p.Contains("must drive or read Camera-Pupil")) && camera.Problems.Any(p => p.Contains("names Camera-Side, which is not a device")), string.Join(" | ", camera.Problems));
                    var missingKey = wiringOf(rigText.Replace("zapit: { hardwareTriggered: true, triggerLine: 3 }\n", "zapit: { hardwareTriggered: true }\n"), linesText);
                    Check("a line given by a rig.yml key the rig lacks refuses", missingKey.Problems.Any(p => p.Contains("this rig.yml has no zapit.triggerLine")), string.Join(" | ", missingKey.Problems));
                    var none = wiringOf(rigText, null);
                    Check("a workflow without board-lines.yml is not checked, and says so", !none.Applies && none.Summary.StartsWith("not checked: av-dome has no"), none.Summary);
                    var otherBoard = wiringOf(rigText, "BehaviorBoard-Second:\n  - { line: DO1, device: RewardValve }\n");
                    Check("a board the file does not list is not checked", !otherBoard.Applies, otherBoard.Summary);
                    Check("the refusal names the board, each problem and both files", sideSwapped.Refusal("BehaviorBoard-Main").StartsWith("BehaviorBoard-Main's wiring in rig.yml does not agree") && sideSwapped.Refusal("BehaviorBoard-Main").Contains(WiringCheck.LinesFile), sideSwapped.Refusal("BehaviorBoard-Main"));
                    Check("no refusal when they agree", agree.Refusal("BehaviorBoard-Main") == null, agree.Refusal("BehaviorBoard-Main"));
                    // Audit, 8 Nov 2026.
                    var twoProblems = wiringOf(rigText.Replace("side: left, part", "side: right, part").Replace("DO3: { device: ZapitTrigger }", "DO3: { device: AirPuff }"), linesText);
                    var refusal = twoProblems.Refusal("BehaviorBoard-Main");
                    Check("the refusal is one line, every disagreement in it, as rig-control keeps only a workflow error's first line",
                        twoProblems.Problems.Count == 2 && !refusal.Contains("\n") && twoProblems.Problems.All(refusal.Contains), refusal);
                    var notCabled = rigText.Replace("zapit: { hardwareTriggered: true, triggerLine: 3 }", "zapit: { hardwareTriggered: false, triggerLine: 3 }").Replace("      DO3: { device: ZapitTrigger }\n", "");
                    var zapitOff = wiringOf(notCabled, linesText);
                    Check("zapit.triggerLine is not used while zapit.hardwareTriggered is false, so its uncabled line may be unknown",
                        zapitOff.Problems.Count == 0 && !zapitOff.Summary.Contains("DO3"), string.Join(" | ", zapitOff.Problems) + " / " + zapitOff.Summary);
                    var zapitUnset = wiringOf(notCabled.Replace("hardwareTriggered: false, ", ""), linesText);
                    Check("... nor when hardwareTriggered is left out", zapitUnset.Problems.Count == 0, string.Join(" | ", zapitUnset.Problems));
                    var twice = wiringOf(rigText, linesText + "  - { line: DO0, device: CameraTrigger, targets: [Camera-Side] }\n");
                    Check("two entries for one line need every target either names",
                        twice.Problems.Any(p => p.Contains("must drive or read Camera-Side")), string.Join(" | ", twice.Problems));
                }

                Console.WriteLine("The dump request:");
                {
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    var sent = new List<HarpMessage>();
                    using (new RequestRegisterDump { Delay = TimeSpan.Zero }.Process(messages, path).Subscribe(sent.Add))
                    {
                        path.OnNext("x");
                        // L2-I3: polled, not a fixed 100 ms, so a slow machine does not fail it.
                        for (var waited = Stopwatch.StartNew(); sent.Count != 1 && waited.ElapsedMilliseconds < 2000; ) Thread.Sleep(10);
                        Check("reads OperationControl when the experiment folder appears", sent.Count == 1 && sent[0].Address == OperationControl.Address && sent[0].MessageType == MessageType.Read, sent.Count.ToString());
                        // Active, heartbeat on, operation LED on: what the device answers with.
                        messages.OnNext(OperationControl.FromPayload(MessageType.Read, new OperationControlPayload(OperationMode.Active, false, false, LedState.On, LedState.On, EnableFlag.Enabled)));
                        var write = sent.Count == 2 ? OperationControl.GetPayload(sent[1]) : default(OperationControlPayload);
                        Check("... then writes it back asking for the dump, the rest as the device had it", sent.Count == 2 && sent[1].MessageType == MessageType.Write && write.DumpRegisters && write.OperationMode == OperationMode.Active && write.Heartbeat == EnableFlag.Enabled, sent.Count.ToString());
                    }
                }

                Console.WriteLine("The plan from rig.yml:");
                var config = Path.Combine(root, "config");
                Directory.CreateDirectory(Path.Combine(config, Environment.MachineName));
                File.WriteAllText(Path.Combine(config, Environment.MachineName, "rig.yml"),
                    "behaviorBoard:\n  main:\n    portName: COM3\n    usbSerial: AB0M7PLB\n  spare:\n    portName: COM9\n  idle:\n    portName: null\n" +
                    "licketySplit:\n  left:\n    portName: COM4\n    usbSerial: E664C856C3913E37\n" +
                    "soundCard:\n  main:\n    portName: COM6\n" +
                    "camera:\n  box:\n    serialNumber: '1'\nrootPath: C:\\data\n");
                Func<string, Type> published = n =>
                    n == "PathPrefix" ? typeof(string)
                    : n == "BehaviorBoard-Main.Data" || n == "BehaviorBoard-Main.Commands" || n == "LicketySplit-Left.Data" || n == "SoundCard-Main.Data" || n == "BehaviorBoard-Idle.Data" ? typeof(HarpMessage)
                    : n == "Camera-Box.Data" ? typeof(object) : null;
                var plan = HarpDevicePlan.Create(config, published);
                var main = plan.Recorded.FirstOrDefault(d => d.Name == "BehaviorBoard-Main");
                Check("records each serial device the workflow publishes as HarpMessage", string.Join(",", plan.Recorded.Select(d => d.Name)) == "BehaviorBoard-Main,LicketySplit-Left,SoundCard-Main", string.Join(",", plan.Recorded.Select(d => d.Name)));
                Check("... with rig.yml's port and USB serial, its kind's WhoAmI, and its commands", main != null && main.PortName == "COM3" && main.UsbSerial == "AB0M7PLB" && main.ExpectedWhoAmI == 1216 && main.HasCommands, main == null ? "none" : main.ToString());
                var left = plan.Recorded.FirstOrDefault(d => d.Name == "LicketySplit-Left");
                Check("a Lickety Split is checked against WhoAmI 1400", left != null && left.ExpectedWhoAmI == 1400 && !plan.Notes.Any(n => n.Contains("licketySplit")), left == null ? "none" : left.ToString());
                var soundCard = plan.Recorded.FirstOrDefault(d => d.Name == "SoundCard-Main");
                Check("a kind with no known WhoAmI is recorded unchecked, and noted", soundCard != null && soundCard.ExpectedWhoAmI == 0 && !soundCard.HasCommands && plan.Notes.Any(n => n.Contains("no WhoAmI is known for soundCard")), string.Join(" | ", plan.Notes));
                Check("a device the workflow does not publish is noted", plan.Notes.Any(n => n.StartsWith("BehaviorBoard-Spare")), string.Join(" | ", plan.Notes));
                Check("a camera is not a serial device and is passed over", !plan.Notes.Any(n => n.StartsWith("Camera-Box")), string.Join(" | ", plan.Notes));
                // H1/M1: the same rule as OpenRigs.Manifest's (src\Shared\HarpRig.cs), whose tests hold the same entry.
                Check("... as is a portName: null, though its data is HarpMessage", !plan.Recorded.Any(d => d.Name == "BehaviorBoard-Idle") && !plan.Notes.Any(n => n.StartsWith("BehaviorBoard-Idle")), string.Join(" | ", plan.Notes));
                var wrongCommands = HarpDevicePlan.Create(config, n => n == "BehaviorBoard-Main.Commands" ? typeof(string) : published(n));
                Check("Commands of another type asks for no dump, and is noted", !wrongCommands.Recorded.First(d => d.Name == "BehaviorBoard-Main").HasCommands
                    && wrongCommands.Notes.Any(n => n.Contains("BehaviorBoard-Main.Commands is String, not HarpMessage")), string.Join(" | ", wrongCommands.Notes));
                var repo = Path.Combine(root, "workflow-repo");
                Directory.CreateDirectory(Path.Combine(repo, "schemas"));
                File.WriteAllText(Path.Combine(repo, "schemas", "board-lines.yml"), "BehaviorBoard-Main:\n  - { line: DO1, device: RewardValve, side: left }\n");
                var wired = HarpDevicePlan.Create(config, published, () => repo);
                var wiredBoard = wired.Recorded.First(d => d.Name == "BehaviorBoard-Main");
                Check("the plan reads the workflow's board-lines.yml and refuses a board whose rig.yml has no wiring for a line it uses",
                    wiredBoard.WiringRefusal != null && wiredBoard.WiringRefusal.Contains("DO1 is used as left RewardValve") && wired.Notes.Any(n => n.StartsWith("BehaviorBoard-Main: refused")),
                    wiredBoard.WiringRefusal + " | " + string.Join(" | ", wired.Notes));
                Check("... the refusal reaches the board's node, and the plan changes so the inner workflow is remade", wired.Signature() != plan.Signature(), "same signature");
                var unopted = HarpDevicePlan.Create(config, published, () => Path.Combine(root, "no-such-repo"));
                var unoptedBoard = unopted.Recorded.First(d => d.Name == "BehaviorBoard-Main");
                Check("a workflow without the file is recorded as not checked, and nothing refuses",
                    unoptedBoard.WiringRefusal == null && unoptedBoard.Wiring.StartsWith("not checked:"), unoptedBoard.Wiring);
                Check("only Behavior boards are wiring-checked", unopted.Recorded.Where(d => d.Kind != "behaviorBoard").All(d => d.Wiring == null), "another kind has a wiring result");
                var noFolder = HarpDevicePlan.Create(config, n => n == "PathPrefix" ? null : published(n));
                Check("no PathPrefix records nothing, and says so", noFolder.Recorded.Count == 0 && noFolder.Notes.Any(n => n.Contains("PathPrefix")), string.Join(" | ", noFolder.Notes));

                Console.WriteLine("In a workflow:");
                {
                    var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<WorkflowBuilder Version=\"2.9.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"" +
                        " xmlns:rx=\"clr-namespace:Bonsai.Reactive;assembly=Bonsai.Core\" xmlns:harp=\"clr-namespace:Bonsai.Harp;assembly=Bonsai.Harp\" xmlns:sys=\"clr-namespace:System;assembly=mscorlib\"" +
                        " xmlns:hd=\"clr-namespace:OpenRigs.HarpDevices;assembly=OpenRigs.HarpDevices\" xmlns=\"https://bonsai-rx.org/2018/workflow\">\n<Workflow><Nodes>" +
                        "<Expression xsi:type=\"GroupWorkflow\"><Name>Devices</Name><Workflow><Nodes>" +
                        "<Expression xsi:type=\"rx:BehaviorSubject\" TypeArguments=\"harp:HarpMessage\"><rx:Name>BehaviorBoard-Main.Data</rx:Name></Expression>" +
                        "<Expression xsi:type=\"rx:BehaviorSubject\" TypeArguments=\"harp:HarpMessage\"><rx:Name>BehaviorBoard-Main.Commands</rx:Name></Expression>" +
                        "</Nodes><Edges /></Workflow></Expression>" +
                        "<Expression xsi:type=\"rx:BehaviorSubject\" TypeArguments=\"sys:String\"><rx:Name>PathPrefix</rx:Name></Expression>" +
                        "<Expression xsi:type=\"hd:RecordHarpDevices\"><hd:ConfigRoot>" + SecurityEscape(config) + "</hd:ConfigRoot></Expression>" +
                        "<Expression xsi:type=\"SubscribeSubject\"><Name>BehaviorBoard-Main.Identity</Name></Expression>" +
                        "</Nodes><Edges /></Workflow>\n</WorkflowBuilder>\n";
                    WorkflowBuilder builder;
                    using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    var built = Error(() => builder.Workflow.Build());
                    Check("builds, a node after it subscribing to <Name>.Identity", built == null, built);
                    WorkflowBuilder misnamed;
                    using (var reader = XmlReader.Create(new StringReader(xml.Replace("BehaviorBoard-Main.Identity</Name>", "BehaviorBoard-Main.Nothing</Name>")))) misnamed = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    Check("... where a subject nobody publishes would not build, so that check means something", Error(() => misnamed.Workflow.Build()) != null, "built");
                    var node = builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<RecordHarpDevices>().Single();
                    var inner = node.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<GroupWorkflowBuilder>().SingleOrDefault();
                    var groups = inner == null ? "" : string.Join(",", inner.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<GroupWorkflowBuilder>().Select(g => g.Name));
                    Check("... with a group for the board, made from rig.yml", groups == "BehaviorBoard-Main", groups);

                    var copy = new WorkflowBuilder(builder.Workflow.ToInspectableGraph().FromInspectableGraph());
                    var saved = new StringWriter();
                    using (var writer = XmlWriter.Create(saved, new XmlWriterSettings { Indent = true })) WorkflowBuilder.Serializer.Serialize(writer, copy);
                    using (var reader = XmlReader.Create(new StringReader(saved.ToString()))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    node = builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<RecordHarpDevices>().Single();
                    Check("its ConfigRoot survives the editor's copy, a save and a reload", node.ConfigRoot == config, node.ConfigRoot);
                    var rebuilt = Error(() => builder.Workflow.Build());
                    Check("... and the reloaded workflow builds", rebuilt == null, rebuilt);

                    // A workflow property mapped onto it, as coen-behaviour-box's ConfigRoot: Bonsai's build
                    // refused one while the generated inner workflow externalized nothing (3 Nov 2026).
                    var mappedXml = xml.Replace("</Nodes><Edges /></Workflow>\n</WorkflowBuilder>",
                        "<Expression xsi:type=\"ExternalizedMapping\"><Property Name=\"ConfigRoot\" /></Expression>" +
                        "</Nodes><Edges><Edge From=\"4\" To=\"2\" Label=\"Source1\" /></Edges></Workflow>\n</WorkflowBuilder>");
                    WorkflowBuilder mapped;
                    using (var reader = XmlReader.Create(new StringReader(mappedXml))) mapped = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    var mappedBuilt = Error(() => mapped.Workflow.Build());
                    Check("a workflow property mapped onto it builds", mappedBuilt == null, mappedBuilt);
                    var moved = Path.Combine(root, "moved");
                    mapped.Workflow.SetWorkflowProperty("ConfigRoot", moved);
                    var mappedNode = mapped.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<RecordHarpDevices>().Single();
                    Check("... and setting it, as --property does, moves the node's ConfigRoot", mappedNode.ConfigRoot == moved, mappedNode.ConfigRoot);
                    var mappedCopy = new WorkflowBuilder(mapped.Workflow.ToInspectableGraph().FromInspectableGraph());
                    var mappedCopyNode = mappedCopy.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<RecordHarpDevices>().Single();
                    var innerMappings = mappedCopyNode.Workflow.Count(n => ExpressionBuilder.Unwrap(n.Value) is ExternalizedMappingBuilder);
                    Check("... and the editor's copy keeps one inner mapping, not two", innerMappings == 1, innerMappings + " mappings");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            Console.WriteLine(failed == 0 ? "All checks passed." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }

        static string SecurityEscape(string text)
        {
            return System.Security.SecurityElement.Escape(text);
        }
    }
}
