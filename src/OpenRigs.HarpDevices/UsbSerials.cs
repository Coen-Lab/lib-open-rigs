using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenRigs.HarpDevices
{
    /// <summary>
    /// Which board is on which serial port, by the USB serial number its chip was made with: an
    /// FTDI chip's (Harp boards, AB0M7PLB) or a Pico's flash id (E664C856C3913E37). Read the way
    /// rig-setup's machine record reads it (engine\machine.ps1, Get-UsbSerialFromIds), so the
    /// serial this checks is the one the rig's nightly scan lists.
    /// </summary>
    static class UsbSerials
    {
        /// <summary>
        /// Every serial port plugged in now, its name (COM3) to its USB serial, '' for a port with
        /// none (a motherboard COM1, a chip that makes one up). Ports that are not plugged in are
        /// left out: Windows remembers them, and only the board in the socket now matters here.
        /// </summary>
        public static Dictionary<string, string> Present()
        {
            var ports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var search = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports'"))
            using (var found = search.Get())
            {
                foreach (ManagementBaseObject device in found)
                {
                    var name = device["Name"] as string;
                    var id = device["PNPDeviceID"] as string;
                    if (id == null) continue;
                    var port = RegistryPortName(id) ?? PortOf(name);
                    if (port == null) continue;
                    ports[port] = FromIds(id, ParentId(id));
                }
            }
            return ports;
        }

        /// <summary>
        /// The port's name as its driver registered it (Device Parameters\PortName), as rig-setup
        /// reads it; null when there is none, and then the display name's is used.
        /// </summary>
        static string RegistryPortName(string instanceId)
        {
            try
            {
                using (var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters"))
                {
                    var value = parameters != null ? parameters.GetValue("PortName") as string : null;
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static readonly Regex PortName = new Regex(@"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase);

        /// <summary>COM3 from "USB Serial Port (COM3)", or null.</summary>
        public static string PortOf(string name)
        {
            var match = PortName.Match(name ?? string.Empty);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        /// <summary>
        /// The serial from a port's device id and its parent's: the last part of the USB device's
        /// id, which is the port's own id unless that is an interface of a composite device
        /// (&amp;MI_, a Pico) or a driver's bus (FTDIBUS\..., an FTDI chip), when it is the
        /// parent's. '' when that part was made up by Windows (it holds &amp;) or there is no USB
        /// device at all.
        /// </summary>
        public static string FromIds(string instanceId, string parentId)
        {
            var usb = instanceId != null && instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && instanceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0
                ? instanceId : parentId;
            if (usb == null || !usb.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var parts = usb.Split('\\');
            var last = parts[parts.Length - 1];
            return last.Length == 0 || last.Contains("&") ? string.Empty : last;
        }

        /// <summary>The parent device's id, or null. Windows keeps it for a device plugged in now.</summary>
        static string ParentId(string instanceId)
        {
            uint node, parent;
            if (CM_Locate_DevNodeW(out node, instanceId, 0) != 0) return null;
            if (CM_Get_Parent(out parent, node, 0) != 0) return null;
            var id = new StringBuilder(MaxDeviceIdLength + 1);
            return CM_Get_Device_IDW(parent, id, id.Capacity, 0) == 0 ? id.ToString() : null;
        }

        const int MaxDeviceIdLength = 200;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLength, int flags);
    }
}
