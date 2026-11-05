using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Harp;

namespace OpenRigs.HarpDevices
{
    /// <summary>
    /// Asks a Harp device for a register dump each time an experiment folder appears, so the experiment
    /// logs every register's value at its start. The dump Bonsai.Harp asks for when it opens the
    /// port arrives before the experiment folder exists, so no experiment would otherwise have one
    /// (open-rigs/open-rigs#9, which would do this in LogHarpDevice for every workflow; this node
    /// goes when that is released). The dump request is a bit of OperationControl, which also
    /// holds the operation mode, LEDs, heartbeat and reply muting, so the register is read first
    /// and written back with only DumpRegisters set.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Asks a Harp device for a register dump a moment after each experiment folder appears, keeping its other OperationControl settings. Inputs: the device's messages and the experiment folder (PathPrefix). Output: the commands to send it.")]
    public class RequestRegisterDump
    {
        /// <summary>Gets or sets how long after the experiment folder appears to ask, so the device's loggers are writing into it.</summary>
        [Description("How long after the experiment folder appears to ask, so the device's loggers are already writing into it.")]
        public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(0.5);

        /// <summary>For each experiment folder, a read of OperationControl, then the write that asks for the dump when the reply arrives.</summary>
        /// <param name="messages">The device's messages.</param>
        /// <param name="pathPrefix">The experiment folder, each time one appears.</param>
        /// <returns>The commands to send the device.</returns>
        public IObservable<HarpMessage> Process(IObservable<HarpMessage> messages, IObservable<string> pathPrefix)
        {
            return pathPrefix.Delay(Delay).SelectMany(path => Observable.Create<HarpMessage>(observer =>
            {
                // Listening for the reply before the read goes out, so a fast reply is not missed.
                var reply = messages
                    .Where(message => message.Address == OperationControl.Address && message.MessageType == MessageType.Read && !message.Error)
                    .Take(1)
                    .Select(WithDump)
                    .Subscribe(observer);
                observer.OnNext(HarpCommand.ReadByte(OperationControl.Address));
                return reply;
            }));
        }

        /// <summary>The write that sets DumpRegisters and leaves every other setting as the device reported it.</summary>
        public static HarpMessage WithDump(HarpMessage operationControl)
        {
            var payload = OperationControl.GetPayload(operationControl);
            payload.DumpRegisters = true;
            return OperationControl.FromPayload(MessageType.Write, payload);
        }
    }
}
