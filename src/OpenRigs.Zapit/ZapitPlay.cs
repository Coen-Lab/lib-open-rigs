using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// Play Condition, before Go: a condition's real trial from the loaded bank, armed as ZapitDaq arms a task's trial
    /// (ZapitDaq's own Arm, trial <see cref="SetupTrial"/>), for the agent to start on the ZapitTrigger line and clear
    /// (open_rigs.zapit.play, the sequence). A request is "condition,powerMw,seconds": one of the bank's laser-on powers
    /// for that condition, at most <see cref="CeilingMw"/>, for a duration whose trial fits <see cref="MaxTrialSeconds"/>
    /// with its ramp (ZapitBank.Cycles, as ZapitDaq checks it). Refused, as a step and never an error, with no bank
    /// loaded, a play already armed, a request that does not parse, the laser disabled (ceilingMw 0), or a condition,
    /// power or duration the bank cannot play. ZapitDaq's events for the play are reported as stages: Armed, Playing
    /// (started on the line), Ended (its own end, a stop, or a Zero such as LaserOff). When the requests end (Go) with a
    /// play armed or playing, the lines are zeroed (stage Off). Stages: Arming, Armed, Playing, Ended, Off, Refused.
    /// </summary>
    [Combinator]
    [Description("Play Condition before Go: each request (condition,powerMw,seconds) becomes ZapitDaq's Arm of that condition from the loaded bank (trial 0), or is refused; " +
        "ZapitDaq's events for it become the stages Armed, Playing and Ended; at Go (the requests end) a play still armed is zeroed. Inputs: requests, ZapitDaq's events. Output: steps (stage, detail, command).")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class ZapitPlay
    {
        /// <summary>Play Condition's trial number: a task's trials count from 1.</summary>
        public const int SetupTrial = 0;

        /// <summary>Gets or sets ZapitDaq's MaxTrialSeconds: a play whose trial with its ramp is longer is refused.</summary>
        [Description("ZapitDaq's MaxTrialSeconds (map it as ZapitDaq's): a play whose trial with its ramp is longer is refused.")]
        public double MaxTrialSeconds { get; set; }

        /// <summary>Gets or sets rig.yml's zapit.ceilingMw: at 0 (the laser disabled) every play is refused, and so is a power above it.</summary>
        [Description("rig.yml's zapit.ceilingMw: 0, the laser disabled, refuses every play; a power above it is refused.")]
        public double CeilingMw { get; set; }

        /// <summary>The steps for the requests, as ZapitDaq's events come.</summary>
        public IObservable<ZapitSetupStep> Process(IObservable<string> requests, IObservable<ZapitDaqEvent> events) => Observable.Create<ZapitSetupStep>(observer =>
        {
            var gate = new object();
            ZapitBank? bank = null;
            var playing = 0;   // the condition armed or playing; 0 for none
            void Emit(string stage, string detail, ZapitDaqCommand? command = null) =>
                observer.OnNext(new ZapitSetupStep { Stage = stage, Detail = detail, Command = command });

            void Request(string request)
            {
                void No(string why) => Emit("Refused", why);
                if (bank == null) { No("no Zapit bank is loaded"); return; }
                if (playing != 0) { No($"condition {playing} is still armed or playing"); return; }
                if (!(CeilingMw > 0)) { No("rig.yml's zapit.ceilingMw is 0 (laser disabled), so there is nothing to play"); return; }
                var parts = (request ?? "").Trim().Trim('"').Split(',');
                var numbers = new double[3];
                if (parts.Length != 3 || Enumerable.Range(0, 3).Any(i => !double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                    || numbers[0] != Math.Floor(numbers[0]))
                {
                    No("a play is condition,powerMw,seconds: " + request);
                    return;
                }
                int condition = (int)numbers[0];
                double power = numbers[1], seconds = numbers[2];
                if (!(power > 0) || power > CeilingMw) { No($"a play's power is above 0 mW and at most ceilingMw ({CeilingMw} mW), not {power} mW"); return; }
                if (!(seconds > 0)) { No($"a play lasts more than 0 s, not {seconds} s"); return; }
                try
                {
                    var variant = bank.Variant(condition, true, power);
                    bank.Cycles(condition, variant, seconds, MaxTrialSeconds);
                }
                catch (InvalidOperationException why) { No(why.Message); return; }
                playing = condition;
                Emit("Arming", request.Trim(), new ZapitDaqCommand
                {
                    Kind = ZapitDaqCommandKind.Arm, Trial = SetupTrial, Condition = condition, LaserOn = true, PowerMw = power, DurationSeconds = seconds,
                });
            }

            void OnEvent(ZapitDaqEvent e)
            {
                if (e.Kind == ZapitDaqEventKind.BankLoaded) bank = e.Bank;
                if (playing == 0 || e.Trial != SetupTrial) return;
                var condition = playing.ToString(CultureInfo.InvariantCulture);
                switch (e.Kind)
                {
                    case ZapitDaqEventKind.Armed: Emit("Armed", condition); break;
                    case ZapitDaqEventKind.Started: Emit("Playing", condition); break;
                    case ZapitDaqEventKind.Ended:
                    case ZapitDaqEventKind.Stopped:
                    case ZapitDaqEventKind.Underflowed:
                    case ZapitDaqEventKind.Zeroed:
                        playing = 0;
                        Emit("Ended", condition + " " + e.Kind);
                        break;
                }
            }

            void RequestsEnded()
            {
                if (playing == 0) return;
                playing = 0;
                Emit("Off", "Go", new ZapitDaqCommand { Kind = ZapitDaqCommandKind.Zero });
            }

            Action<T> Locked<T>(Action<T> action) => value =>
            {
                lock (gate)
                {
                    try { action(value); }
                    catch (Exception error) { observer.OnError(error); }
                }
            };
            return new CompositeDisposable(
                events.Subscribe(Locked<ZapitDaqEvent>(OnEvent), observer.OnError),
                requests.Subscribe(Locked<string>(Request), observer.OnError, () => Locked<int>(_ => RequestsEnded())(0)));
        });
    }
}
