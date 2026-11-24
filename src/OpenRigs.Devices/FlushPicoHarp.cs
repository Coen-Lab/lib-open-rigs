using Bonsai;
using OpenRigs.Core;
using OpenRigs.Devices.Rigs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Reactive.Linq;
using System.Threading;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Brings back a Lickety Split whose command parser is stuck before its device opens. The Harp Pico core
    /// (harp-tech/core.pico) has no resync or timeout: a frame cut short leaves it reading every later command as
    /// payload, so WhoAmI never answers and Bonsai.Harp's Device waits for ever while heartbeats still flow. Zero bytes
    /// complete the pending frame (at most 255 bytes) and are message type 0, which the core ignores.
    /// </summary>
    [Combinator]
    [Description("Before the Lickety Split's device opens, asks its board WhoAmI; a board that is silent gets zero bytes that complete a half-received frame in the Harp Pico core's parser, and must then answer or the workflow stops, saying to power-cycle it. Passes the rig.yml entry on once the port is closed again.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class FlushPicoHarp
    {
        const int OpenAttempts = 5;
        const int RetryMs = 100;
        const int AnswerMs = 250;
        const int SettleMs = 50;
        static readonly byte[] WhoAmI = { 0x01, 0x04, 0x00, 0xFF, 0x02, 0x06 };

        /// <summary>What a board did: answered WhoAmI at once, answered only after the flush, or never.</summary>
        public enum Outcome
        {
            /// <summary>Answered WhoAmI at once: nothing was written but WhoAmI.</summary>
            Answered,
            /// <summary>Answered only after the zeros.</summary>
            Flushed,
            /// <summary>Did not answer even after the zeros.</summary>
            Silent,
        }

        /// <summary>Flushes each entry's board if it is stuck, synchronously, so the device opens only after.</summary>
        public IObservable<NamedDevice<LicketySplit>> Process(IObservable<NamedDevice<LicketySplit>> source)
        {
            return source.Select(device =>
            {
                Flush(device.Name, device.Settings.PortName);
                return device;
            });
        }

        /// <summary>Opens the port with Bonsai.Harp's settings, recovers the board, and closes the port.</summary>
        public static void Flush(string name, string portName)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var port = new SerialPort(portName, 1000000, Parity.None, 8, StopBits.One))
                    {
                        port.Handshake = Handshake.RequestToSend;
                        port.DtrEnable = true;
                        port.WriteTimeout = 500;
                        port.ReadTimeout = 20;
                        port.Open();
                        var outcome = Recover(port.BaseStream);
                        if (outcome == Outcome.Silent)
                            throw new InvalidOperationException(name + " (" + portName + ") does not answer WhoAmI even after a flush: power-cycle it.");
                        if (outcome == Outcome.Flushed)
                            Console.Error.WriteLine("FlushPicoHarp: flushed " + name + " (" + portName + ")");
                        return;
                    }
                }
                catch (UnauthorizedAccessException) when (attempt < OpenAttempts)
                {
                    Thread.Sleep(RetryMs); // a port closed a moment ago is released a moment later
                }
            }
        }

        /// <summary>Asks WhoAmI; on silence writes 320 zeros, then 5 zeros twice, and asks again.</summary>
        public static Outcome Recover(Stream port)
        {
            var received = new List<byte>();
            var parsed = 0;
            if (Answers(port, received, ref parsed)) return Outcome.Answered;
            port.Write(new byte[320], 0, 320);
            for (var i = 0; i < 2; i++)
            {
                port.Write(new byte[5], 0, 5); // with fewer than a header's 5 bytes pending, the core drops what follows a frame
                Thread.Sleep(SettleMs);
            }
            return Answers(port, received, ref parsed) ? Outcome.Flushed : Outcome.Silent;
        }

        // Sends WhoAmI and waits for a Read reply (or its error) at address 0, frame by frame past heartbeats.
        static bool Answers(Stream port, List<byte> received, ref int parsed)
        {
            port.Write(WhoAmI, 0, WhoAmI.Length);
            var buffer = new byte[256];
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < AnswerMs)
            {
                int count;
                try { count = port.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { count = 0; }
                if (count == 0) { Thread.Sleep(5); continue; }
                for (var i = 0; i < count; i++) received.Add(buffer[i]);
                while (parsed + 2 <= received.Count && parsed + 2 + received[parsed + 1] <= received.Count)
                {
                    var type = received[parsed];
                    var length = received[parsed + 1];
                    var atZero = length > 0 && received[parsed + 2] == 0;
                    parsed += 2 + length;
                    if ((type == 0x01 || type == 0x09) && atZero) return true;
                }
            }
            return false;
        }
    }
}
