using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using Bonsai;
using Bonsai.Harp;
using OpenRigs.Shared;

namespace OpenRigs.HarpDevices
{
    /// <summary>
    /// One Harp device's identity, checked at Start and recorded into each experiment. At Start: the
    /// board on the port rig.yml gives must be the one rig.yml names by its USB serial, or the
    /// workflow stops. When the experiment folder appears: the registers the device reports (WhoAmI,
    /// hardware, firmware and core versions, its Harp serial number) are written to
    /// &lt;Name&gt;\&lt;Name&gt;_device.json, and the register map of the Harp package that
    /// decodes it to &lt;Name&gt;\&lt;Name&gt;.yml. A board of the wrong kind stops the workflow.
    /// RecordHarpDevices makes one for each Harp device in rig.yml.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Checks a Harp device's USB serial at Start against rig.yml, then writes <Name>\\<Name>_device.json (WhoAmI, versions, serials) and <Name>\\<Name>.yml (its register map) into each experiment folder. Inputs: the device's messages and the experiment folder (PathPrefix). Output: the device.json's path.")]
    public class RecordHarpDevice
    {
        /// <summary>The port's serial ports and USB serials, read at Start. The tests' own in place of Windows'.</summary>
        internal static Func<Dictionary<string, string>> PresentPorts = UsbSerials.Present;

        /// <summary>Gets or sets the device's name in the data, Kind-Instance.</summary>
        [Description("The device's name in the data, Kind-Instance (BehaviorBoard-Main): its folder in the experiment and the start of its files' names.")]
        public string Name { get; set; }

        /// <summary>Gets or sets where rig.yml has the device, for messages.</summary>
        [Description("Where rig.yml has the device (rig.yml behaviorBoard: main:), for messages.")]
        public string Entry { get; set; }

        /// <summary>Gets or sets the serial port rig.yml gives.</summary>
        [Description("The serial port rig.yml gives the device (portName).")]
        public string PortName { get; set; }

        /// <summary>Gets or sets the USB serial rig.yml gives, or null if it gives none.</summary>
        [Description("The board's USB serial as rig.yml gives it (usbSerial). The board on PortName must have it, or the workflow stops at Start.")]
        public string UsbSerial { get; set; }

        /// <summary>Gets or sets what the wiring check found, for the record.</summary>
        [Description("What the wiring check found (rig.yml's wiring against the workflow's schemas\\board-lines.yml), written into the record; empty for a device it does not cover.")]
        public string Wiring { get; set; }

        /// <summary>Gets or sets every disagreement between rig.yml's wiring and the lines the workflow uses; the workflow stops at Start when set.</summary>
        [Description("Every disagreement between rig.yml's wiring and the lines the workflow uses. When set, the workflow stops at Start with it.")]
        public string WiringRefusal { get; set; }

        /// <summary>Gets or sets the WhoAmI the device must report, or 0 to record it unchecked.</summary>
        [Description("The WhoAmI a device of this kind reports. Another stops the workflow; 0 records it unchecked.")]
        public int ExpectedWhoAmI { get; set; }

        /// <summary>Gets or sets whether a register dump is asked for when the experiment folder appears.</summary>
        [Description("Whether a register dump is asked for on <Name>.Commands when the experiment folder appears. Without one, the record is written at once from what the device reported when its port opened.")]
        public bool HasCommands { get; set; } = true;

        /// <summary>Gets or sets how long to wait for the register dump after the experiment folder appears.</summary>
        [Description("How long to wait for the device's registers after the experiment folder appears before writing what it reported at connection instead.")]
        public TimeSpan DumpTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets how long the device must be quiet after FirmwareVersionLow for the dump to have finished.</summary>
        [Description("How long after FirmwareVersionLow (register 7) no register may come for the dump to have finished. The dump interleaves the core's registers with the device's own, so the device name, Harp serial and unique id (12, 13, 16) come after 7.")]
        public TimeSpan DumpQuiet { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>Checks the board at Start, then records it into each experiment folder.</summary>
        /// <param name="messages">The device's messages (&lt;Name&gt;.Data).</param>
        /// <param name="pathPrefix">The experiment folder, each time one appears.</param>
        /// <returns>The path of each device.json written.</returns>
        public IObservable<string> Process(IObservable<HarpMessage> messages, IObservable<string> pathPrefix)
        {
            return Observable.Create<string>(source =>
            {
                var observer = Observer.Synchronize(source);
                string usbSerial;
                try
                {
                    if (!string.IsNullOrEmpty(WiringRefusal)) throw new InvalidOperationException(WiringRefusal);
                    usbSerial = CheckBoard(PresentPorts());
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                    return Disposable.Empty;
                }

                // What the device reported at connection (Bonsai.Harp asks for a dump when it opens
                // the port), kept in case the dump asked for at the experiment's start never comes.
                // A board of the wrong kind stops the workflow here, before Go; once an experiment has
                // started, Write records it first.
                var gate = new object();
                var atConnection = new Dictionary<int, HarpMessage>();
                var started = false;
                var checkedBeforeStart = false;
                var keep = messages.Subscribe(message =>
                {
                    if (!IsReport(message)) return;
                    lock (gate) atConnection[message.Address] = message;
                    if (message.Address != WhoAmI.Address || Volatile.Read(ref started) || message.GetPayload().Count < 2) return;
                    Volatile.Write(ref checkedBeforeStart, true);
                    var wrong = WrongKind(message.GetPayloadUInt16());
                    if (wrong != null) observer.OnError(new InvalidOperationException(wrong));
                }, observer.OnError);

                var records = pathPrefix.SelectMany(path =>
                {
                    Volatile.Write(ref started, true);
                    var fresh = new Dictionary<int, HarpMessage>();
                    var reports = messages.Where(IsReport).Do(message => { lock (gate) fresh[message.Address] = message; });
                    // The dump has finished once FirmwareVersionLow has come and then nothing for DumpQuiet.
                    var dump = !HasCommands ? Observable.Return(false) : reports
                        .SkipWhile(message => message.Address != FirmwareVersionLow.Address)
                        .Throttle(DumpQuiet)
                        .FirstAsync()
                        .Select(message => true)
                        .Timeout(DumpTimeout, Observable.Defer(() => { lock (gate) return Observable.Return(fresh.ContainsKey(FirmwareVersionLow.Address)); }));
                    return dump.Select(dumped =>
                    {
                        Dictionary<int, HarpMessage> registers;
                        lock (gate)
                        {
                            registers = new Dictionary<int, HarpMessage>(atConnection);
                            foreach (var pair in fresh) registers[pair.Key] = pair.Value;
                        }
                        return Write(path, usbSerial, registers, dumped, Volatile.Read(ref checkedBeforeStart));
                    });
                }).Subscribe(observer);
                return new CompositeDisposable(keep, records);
            });
        }

        // Why the board is of the wrong kind for its rig.yml entry, or null when it is not (or its kind's WhoAmI is not known).
        string WrongKind(int whoAmI)
        {
            if (ExpectedWhoAmI == 0 || whoAmI == ExpectedWhoAmI) return null;
            var known = RegisterMaps.Find(whoAmI);
            return "The board on " + PortName + " reports WhoAmI " + whoAmI +
                (known != null ? " (" + known.Device + ")" : "") + ", but " + (Entry ?? Name) + " is a kind of board that reports " + ExpectedWhoAmI +
                ". The board rig.yml names by its USB serial is not the kind rig.yml files it under.";
        }

        static bool IsReport(HarpMessage message)
        {
            return message.MessageType == MessageType.Read && !message.Error;
        }

        /// <summary>
        /// The USB serial of the board on PortName, if it is the one rig.yml names; otherwise an
        /// exception saying what is plugged in where, and what to change.
        /// </summary>
        public string CheckBoard(IDictionary<string, string> ports)
        {
            var who = (Entry ?? Name) + " (" + Name + ")";
            string onPort;
            var plugged = ports.TryGetValue(PortName ?? string.Empty, out onPort);
            if (string.IsNullOrEmpty(UsbSerial))
            {
                throw new InvalidOperationException(who + " has no usbSerial, so the workflow cannot tell which board is on " + PortName + ". " +
                    (!plugged ? "Nothing is plugged in on " + PortName + " now."
                    : onPort.Length == 0 ? PortName + " has no USB serial to give it."
                    : "The board on " + PortName + " now is " + onPort + ": if it is the right one, add usbSerial: '" + onPort + "' under it in rig.yml."));
            }
            var elsewhere = ports.Where(p => string.Equals(p.Value, UsbSerial, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).FirstOrDefault();
            if (!plugged)
            {
                throw new InvalidOperationException("Nothing is plugged in on " + PortName + ", where " + who + " should be, the board " + UsbSerial +
                    (elsewhere != null ? ". That board is on " + elsewhere + ": correct portName in rig.yml." : ", which is not plugged in at all."));
            }
            if (!string.Equals(onPort, UsbSerial, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(PortName + " holds the board " + (onPort.Length == 0 ? "with no USB serial" : onPort) + ", but " + who + " is the board " + UsbSerial +
                    (elsewhere != null ? ", which is on " + elsewhere + ": correct portName in rig.yml, or put the cables back."
                    : ", which is not plugged in. If the board was replaced on purpose, change usbSerial in rig.yml to '" + onPort + "'."));
            }
            return onPort;
        }

        // Writes the record and the register map, then stops the workflow if the board is of the wrong kind.
        string Write(string pathPrefix, string usbSerial, Dictionary<int, HarpMessage> registers, bool dumped, bool checkedAtConnection)
        {
            var notes = new List<object>();
            if (!dumped) notes.Add(registers.Count == 0 ? "The device reported no registers, at connection or after the experiment folder appeared."
                : !HasCommands ? "No register dump was asked for at the experiment's start (the workflow publishes no " + Name + HarpDevicePlan.CommandsSuffix + "): these are the registers the device reported when its port opened."
                : "No register dump came within " + DumpTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s of the experiment folder appearing: these are the registers the device reported when its port opened.");

            var whoAmI = U16(registers, WhoAmI.Address);
            // A board that reported no WhoAmI gets its kind's map, said to be so, so the experiment is
            // not held back every night. A board of another kind would have stopped the workflow.
            var map = HarpRig.MapToWrite(whoAmI, ExpectedWhoAmI);
            if (whoAmI.HasValue && map == null) notes.Add("No Harp package loaded in this workflow decodes WhoAmI " + whoAmI.Value + ", so its register map is not written.");
            else if (!whoAmI.HasValue && map != null) notes.Add("The device reported no WhoAmI, so the register map written is the one for the kind rig.yml names (WhoAmI " + ExpectedWhoAmI + ").");
            else if (!whoAmI.HasValue) notes.Add("The device reported no WhoAmI, and no Harp package loaded in this workflow decodes its kind's (" + ExpectedWhoAmI + "), so no register map is written.");
            // A wrong kind stops the workflow before Go only when the WhoAmI came at connection.
            if (whoAmI.HasValue && !checkedAtConnection) notes.Add("The board reported no WhoAmI at connection, so its kind was checked only after the experiment started.");

            var folder = Path.Combine(pathPrefix, Name);
            Directory.CreateDirectory(folder);
            if (map != null) AtomicFile.Write(Path.Combine(folder, Name + ".yml"), map.Metadata);

            var record = new JsonObject();
            record.Add("name", Name);
            record.Add("rig_entry", Entry);
            record.Add("port", PortName);
            record.Add("usb_serial", usbSerial);
            record.Add("wiring", string.IsNullOrEmpty(Wiring) ? null : Wiring);
            record.Add("who_am_i", Number(whoAmI));
            record.Add("expected_who_am_i", ExpectedWhoAmI != 0 ? (object)ExpectedWhoAmI : null);
            record.Add("device_name", DeviceNameOf(registers));
            record.Add("hardware", Version(registers, HardwareVersionHigh.Address, HardwareVersionLow.Address));
            record.Add("assembly", Number(U8(registers, AssemblyVersion.Address)));
            record.Add("core", Version(registers, CoreVersionHigh.Address, CoreVersionLow.Address));
            record.Add("firmware", Version(registers, FirmwareVersionHigh.Address, FirmwareVersionLow.Address));
            record.Add("harp_serial", Number(U16(registers, SerialNumber.Address)));
            record.Add("uid", Uid(registers));
            record.Add("register_map", map != null ? Name + ".yml" : null);
            record.Add("register_map_from", map != null ? map.From : null);
            record.Add("dumped", dumped);
            record.Add("written", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            record.Add("notes", notes);
            var path = Path.Combine(folder, Name + "_device.json");
            AtomicFile.Write(path, Json.Serialize(record));

            var wrong = whoAmI.HasValue ? WrongKind(whoAmI.Value) : null;
            if (wrong != null) throw new InvalidOperationException(wrong);
            return path;
        }

        static object Number(int? value)
        {
            return value.HasValue ? (object)value.Value : null;
        }

        static int? U8(Dictionary<int, HarpMessage> registers, int address)
        {
            HarpMessage message;
            if (!registers.TryGetValue(address, out message) || message.GetPayload().Count < 1) return null;
            return message.GetPayloadByte();
        }

        static int? U16(Dictionary<int, HarpMessage> registers, int address)
        {
            HarpMessage message;
            if (!registers.TryGetValue(address, out message) || message.GetPayload().Count < 2) return null;
            return message.GetPayloadUInt16();
        }

        static string Version(Dictionary<int, HarpMessage> registers, int high, int low)
        {
            var h = U8(registers, high);
            var l = U8(registers, low);
            return h.HasValue && l.HasValue ? h.Value + "." + l.Value : null;
        }

        static string DeviceNameOf(Dictionary<int, HarpMessage> registers)
        {
            HarpMessage message;
            if (!registers.TryGetValue(DeviceName.Address, out message)) return null;
            var bytes = message.GetPayload();
            var text = Encoding.ASCII.GetString(bytes.Array, bytes.Offset, bytes.Count);
            var end = text.IndexOf('\0');
            return (end >= 0 ? text.Substring(0, end) : text).Trim();
        }

        // Register 16 is the 16-byte unique id from Harp core 1.12; older cores (GEEK-BBOX1's board, 1.11) have none.
        const int UidAddress = 16;

        static string Uid(Dictionary<int, HarpMessage> registers)
        {
            HarpMessage message;
            if (!registers.TryGetValue(UidAddress, out message)) return null;
            var bytes = message.GetPayload();
            if (bytes.Count != 16) return null;
            var hex = new StringBuilder(32);
            for (var i = 0; i < bytes.Count; i++) hex.Append(bytes.Array[bytes.Offset + i].ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }
    }
}
