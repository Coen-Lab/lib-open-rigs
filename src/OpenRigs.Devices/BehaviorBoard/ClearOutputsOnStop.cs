using Bonsai;
using Bonsai.Harp;
using Harp.Behavior;
using System;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;

namespace OpenRigs.Devices
{
    /// <summary>
    /// Passes a Behavior board's events through and, when the workflow stops however it stops, clears
    /// output lines the workflow holds high. A stopped workflow is disposed, not completed, so a
    /// message sent into the board's commands then is dropped; this node sits on the board's output,
    /// so disposing it closes the board first, then it opens the port itself and writes OutputClear.
    /// </summary>
    [Combinator]
    [Description("Passes the Behavior board's events through. When the workflow stops, however it stops, it lets the board close its port, then opens the port and writes OutputClear for Outputs. Best effort: a port that cannot be opened is passed over.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class ClearOutputsOnStop
    {
        const int OpenAttempts = 5;
        const int RetryMs = 100;

        /// <summary>Gets or sets the board's serial port.</summary>
        [Description("The Behavior board's serial port.")]
        public string PortName { get; set; }

        /// <summary>Gets or sets the lines cleared when the workflow stops.</summary>
        [Description("The lines cleared when the workflow stops.")]
        public DigitalOutputs Outputs { get; set; } = DigitalOutputs.DO3;

        /// <summary>Passes the events through and clears the lines when unsubscribed.</summary>
        public IObservable<HarpMessage> Process(IObservable<HarpMessage> source)
        {
            return Observable.Create<HarpMessage>(observer =>
            {
                var portName = PortName;
                var outputs = Outputs;
                var subscription = source.SubscribeSafe(observer);
                return Disposable.Create(() =>
                {
                    subscription.Dispose();
                    Clear(portName, outputs);
                });
            });
        }

        /// <summary>Writes OutputClear for the lines on a port nobody holds; true if it was written.</summary>
        public static bool Clear(string portName, DigitalOutputs outputs)
        {
            if (string.IsNullOrEmpty(portName)) return false;
            var message = OutputClear.FromPayload(MessageType.Write, outputs).MessageBytes;
            for (var attempt = 1; attempt <= OpenAttempts; attempt++)
            {
                try
                {
                    // Bonsai.Harp's serial settings.
                    using (var port = new SerialPort(portName, 1000000, Parity.None, 8, StopBits.One))
                    {
                        port.Handshake = Handshake.RequestToSend;
                        port.DtrEnable = true;
                        port.WriteTimeout = 500;
                        port.Open();
                        port.Write(message, 0, message.Length);
                        return true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(RetryMs); // the board's port is released a moment after its close returns
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException)
                {
                    return false; // no such port: nothing to clear
                }
            }
            return false;
        }
    }
}
