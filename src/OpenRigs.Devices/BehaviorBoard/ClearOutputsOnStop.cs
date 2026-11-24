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

/// <summary>
/// Passes a Behavior board's events through and, when the workflow stops, clears the output lines
/// it names. A stopped workflow is disposed, not completed, so a command sent to the board then is
/// dropped; this node sits on the board's output, so the board closes its port first, then this
/// node opens the port itself and writes OutputClear.
/// </summary>
[Combinator]
[Description("Passes the Behavior board's events through. When the workflow stops, however it stops, it lets the board close its port, then opens the port and writes OutputClear for Outputs. A port that cannot be opened is passed over.")]
[WorkflowElementCategory(ElementCategory.Sink)]
public class ClearOutputsOnStop
{
    const int OpenAttempts = 5;
    const int RetryMilliseconds = 100;

    [Description("The Behavior board's serial port.")]
    public string PortName { get; set; }

    [Description("The output lines cleared when the workflow stops.")]
    public DigitalOutputs Outputs { get; set; }

    public IObservable<HarpMessage> Process(IObservable<HarpMessage> source)
    {
        return Observable.Create<HarpMessage>(observer =>
        {
            var subscription = source.SubscribeSafe(observer);
            return Disposable.Create(() =>
            {
                subscription.Dispose();
                Clear(PortName, Outputs);
            });
        });
    }

    static void Clear(string portName, DigitalOutputs outputs)
    {
        if (string.IsNullOrEmpty(portName) || outputs == DigitalOutputs.None) return;
        var message = OutputClear.FromPayload(MessageType.Write, outputs).MessageBytes;
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
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
                    return;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The board's port is released a moment after its close returns.
                Thread.Sleep(RetryMilliseconds);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException || ex is ArgumentException)
            {
                return; // The board was unplugged, or the name is not a port.
            }
        }
    }
}
