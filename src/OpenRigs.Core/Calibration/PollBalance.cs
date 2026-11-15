using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core
{
    /// <summary>
    /// Produces the commands that drive a serial balance from its settings: a tare after the
    /// boot delay, then a print request every print period.
    /// </summary>
    [Combinator]
    [Description("Produces the serial commands that drive a balance from its settings: the tare command after bootDelayS, then the print command every printPeriodS from bootDelayS plus tareDelayS on. An empty command is not sent. Send the output to the balance's serial commands subject, as OpenRigs.Devices:SerialBalance.bonsai does.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class PollBalance
    {
        /// <summary>Drives the balance from the first settings received.</summary>
        public IObservable<string> Process(IObservable<SerialBalance> source)
        {
            return source.Take(1).SelectMany(settings => Commands(settings));
        }

        /// <summary>The command sequence for the given settings.</summary>
        public static IObservable<string> Commands(SerialBalance settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            var boot = TimeSpan.FromSeconds(Math.Max(0, settings.BootDelayS));
            var settle = TimeSpan.FromSeconds(Math.Max(0, settings.TareDelayS));
            var period = TimeSpan.FromSeconds(settings.PrintPeriodS > 0 ? settings.PrintPeriodS : 0.5);
            var noTare = string.IsNullOrEmpty(settings.TareCommand);

            // The prints' schedule starts once the tare is sent (Concat), not on a second timer
            // from the start: two timers on a busy thread pool could send a print first.
            var tare = noTare
                ? Observable.Empty<string>()
                : Observable.Timer(boot).Select(_ => settings.TareCommand);
            var print = string.IsNullOrEmpty(settings.PrintCommand)
                ? Observable.Empty<string>()
                : Observable.Timer(noTare ? boot + settle : settle, period).Select(_ => settings.PrintCommand);
            return tare.Concat(print);
        }
    }
}
