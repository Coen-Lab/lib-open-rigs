using System;
using System.Collections.Generic;
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
// temporary session folder, and builds a workflow with it, without Bonsai's editor or hardware:
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
                    using (Recorder().Process(messages, path).Subscribe(written.Add, ex => error = ex))
                    {
                        foreach (var m in Board(1216)) messages.OnNext(m);  // at connection
                        var session = Path.Combine(root, "ses-1");
                        path.OnNext(session);
                        foreach (var m in Board(1216)) messages.OnNext(m);  // the dump asked for
                        Check("waits until the dump has finished", written.Count == 0, written.Count.ToString());
                        Thread.Sleep(400);
                        var json = File.ReadAllText(Path.Combine(session, "BehaviorBoard-Main", "BehaviorBoard-Main_device.json"));
                        Check("writes <Name>/<Name>_device.json when the dump has come", written.Count == 1 && error == null, written.Count + " " + error);
                        Check("... with the board's versions and serials", json.Contains("\"who_am_i\": 1216") && json.Contains("\"firmware\": \"3.1\"") && json.Contains("\"hardware\": \"1.2\"") && json.Contains("\"core\": \"1.11\"")
                            && json.Contains("\"harp_serial\": 65535") && json.Contains("\"usb_serial\": \"AB0M7PLB\"") && json.Contains("\"device_name\": \"Behavior\"") && json.Contains("\"uid\": null") && json.Contains("\"dumped\": true"), json);
                        var map = Path.Combine(session, "BehaviorBoard-Main", "BehaviorBoard-Main.yml");
                        Check("... and the register map of the Harp package with its WhoAmI", File.Exists(map) && File.ReadAllText(map).Contains("whoAmI: 1216") && json.Contains("\"register_map_from\": \"Harp.Behavior 0.2.0\""), json);

                        var quiet = Path.Combine(root, "ses-2");
                        path.OnNext(quiet);
                        Thread.Sleep(1800);
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
                        var session = Path.Combine(root, "ses-3");
                        path.OnNext(session);
                        foreach (var m in Board(1158)) messages.OnNext(m);
                        Thread.Sleep(400);
                        Check("a board of another kind stops the workflow, naming it", error != null && error.Message.Contains("WhoAmI 1158") && error.Message.Contains("reports 1216"), error == null ? "no error" : error.Message);
                        Check("... after recording it", File.Exists(Path.Combine(session, "BehaviorBoard-Main", "BehaviorBoard-Main_device.json")), "no record");
                    }
                }
                {
                    // M2 (audit, 2 Nov 2026): the record was written at register 7, before 12, 13 and 16 had come.
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    var written = new List<string>();
                    using (Recorder().Process(messages, path).Subscribe(written.Add))
                    {
                        var session = Path.Combine(root, "ses-4");
                        path.OnNext(session);
                        foreach (var m in Interleaved(1216)) messages.OnNext(m);  // no dump at connection
                        Thread.Sleep(400);
                        var json = written.Count == 1 ? File.ReadAllText(written[0]) : "";
                        Check("a dump with the core's registers after 7 is recorded whole", json.Contains("\"device_name\": \"Behavior\"") && json.Contains("\"harp_serial\": 65535")
                            && json.Contains("\"uid\": \"0102030405060708090a0b0c0d0e0f10\"") && json.Contains("\"dumped\": true"), written.Count + " " + json);
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
                    // L2: a board of the wrong kind is found from the dump at connection, before Go.
                    var messages = new Subject<HarpMessage>();
                    Exception error = null;
                    using (Recorder().Process(messages, new Subject<string>()).Subscribe(_ => { }, ex => error = ex))
                    {
                        foreach (var m in Board(1158)) messages.OnNext(m);
                        Check("a board of another kind stops the workflow before any session, from the dump at connection", error != null && error.Message.Contains("WhoAmI 1158") && error.Message.Contains("reports 1216"), error == null ? "no error" : error.Message);
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

                Console.WriteLine("The dump request:");
                {
                    var messages = new Subject<HarpMessage>();
                    var path = new Subject<string>();
                    var sent = new List<HarpMessage>();
                    using (new RequestRegisterDump { Delay = TimeSpan.Zero }.Process(messages, path).Subscribe(sent.Add))
                    {
                        path.OnNext("x");
                        Thread.Sleep(100);
                        Check("reads OperationControl when the session folder appears", sent.Count == 1 && sent[0].Address == OperationControl.Address && sent[0].MessageType == MessageType.Read, sent.Count.ToString());
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
                    "camera:\n  box:\n    serialNumber: '1'\nrootPath: C:\\data\n");
                Func<string, Type> published = n =>
                    n == "PathPrefix" ? typeof(string)
                    : n == "BehaviorBoard-Main.Data" || n == "BehaviorBoard-Main.Commands" || n == "LicketySplit-Left.Data" || n == "BehaviorBoard-Idle.Data" ? typeof(HarpMessage)
                    : n == "Camera-Box.Data" ? typeof(object) : null;
                var plan = HarpDevicePlan.Create(config, published);
                var main = plan.Recorded.FirstOrDefault(d => d.Name == "BehaviorBoard-Main");
                Check("records each serial device the workflow publishes as HarpMessage", string.Join(",", plan.Recorded.Select(d => d.Name)) == "BehaviorBoard-Main,LicketySplit-Left", string.Join(",", plan.Recorded.Select(d => d.Name)));
                Check("... with rig.yml's port and USB serial, its kind's WhoAmI, and its commands", main != null && main.PortName == "COM3" && main.UsbSerial == "AB0M7PLB" && main.ExpectedWhoAmI == 1216 && main.HasCommands, main == null ? "none" : main.ToString());
                var left = plan.Recorded.FirstOrDefault(d => d.Name == "LicketySplit-Left");
                Check("a kind with no known WhoAmI is recorded unchecked, and noted", left != null && left.ExpectedWhoAmI == 0 && !left.HasCommands && plan.Notes.Any(n => n.Contains("no WhoAmI is known for licketySplit")), string.Join(" | ", plan.Notes));
                Check("a device the workflow does not publish is noted", plan.Notes.Any(n => n.StartsWith("BehaviorBoard-Spare")), string.Join(" | ", plan.Notes));
                Check("a camera is not a serial device and is passed over", !plan.Notes.Any(n => n.StartsWith("Camera-Box")), string.Join(" | ", plan.Notes));
                // H1/M1: the same rule as OpenRigs.Manifest's (src\Shared\HarpRig.cs), whose tests hold the same entry.
                Check("... as is a portName: null, though its data is HarpMessage", !plan.Recorded.Any(d => d.Name == "BehaviorBoard-Idle") && !plan.Notes.Any(n => n.StartsWith("BehaviorBoard-Idle")), string.Join(" | ", plan.Notes));
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
