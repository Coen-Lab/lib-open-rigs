using OpenRigs.Trials;
using OpenRigs.Zapit;
using System;
using System.Reactive.Linq;

namespace OpenRigs.Opto.ZapitBonsai
{
    /// <summary>
    /// ZapitDaq (Zapit-Bonsai, OpenRigs.Zapit) as the trial loop's opto system: it arms every trial before its trigger
    /// (<see cref="ZapitDaqTrial.ArmSeconds"/>), confirms each start (Started), needs no reply to an early stop, and is free after a
    /// timed trial once its whole armed trial has played plus <see cref="ZapitDaqTrial.FreeMarginSeconds"/>.
    /// </summary>
    public sealed class ZapitBonsaiOptoSystem : OptoSystem
    {
        ZapitBonsaiOptoSystem()
        {
        }

        /// <summary>The one ZapitDaq opto system.</summary>
        public static readonly ZapitBonsaiOptoSystem Instance = new ZapitBonsaiOptoSystem();

        /// <inheritdoc/>
        public override string Name { get { return "ZapitDaq"; } }

        /// <inheritdoc/>
        public override double ArmLeadSeconds { get { return ZapitDaqTrial.ArmSeconds; } }

        /// <inheritdoc/>
        public override bool ConfirmsStart { get { return true; } }

        /// <summary>How long after the trigger ZapitDaq's Started may still be on its way (the board's trigger write, NI's start and
        /// ZapitDaq's polling), 0.05 s: an outcome sooner logs the armed power, and the Zapit event rows settle it.</summary>
        public override double StartedGraceSeconds { get { return 0.05; } }

        /// <inheritdoc/>
        public override bool EndNeedsReply { get { return false; } }

        /// <inheritdoc/>
        public override bool ArmCarriesLaserSeconds { get { return true; } }

        /// <summary>ZapitDaq arms every trial before its trigger, so the laser timing must be hardware triggered.</summary>
        public override string TimingProblem(LaserTiming laser)
        {
            if (laser.HardwareTriggered) return null;
            return "ZapitDaq arms every trial before its trigger: give the rules LaserTiming.UntilOutcome(triggerHoldSeconds, laserCapSeconds), UntilStimulusOff(triggerHoldSeconds, laserCapSeconds) or ForDuration(rampDownSeconds), not MATLAB Zapit's with hardwareTriggered false.";
        }

        /// <summary>ZapitDaq plays whole scan cycles and the ramp from the line's edge, after the trigger's tick: free once its whole
        /// trial has played (the longer of its Armed's TrialSeconds and the duration plus the ramp-down), plus a margin for the edge
        /// and its polling (<see cref="ZapitDaqTrial.FreeMarginSeconds"/>).</summary>
        public override double FreeAt(double triggerSeconds, double durationSeconds, double rampDownSeconds, double armedPlaySeconds)
        {
            return triggerSeconds + Math.Max(armedPlaySeconds, durationSeconds + rampDownSeconds) + ZapitDaqTrial.FreeMarginSeconds;
        }

        /// <summary>Throws unless an Armed is the trial's, as asked: its number, condition and laser (ZapitDaqTrial.CheckArmed).</summary>
        public override void CheckArmed(OptoFeedback armed, IOptoTrial asked)
        {
            ZapitDaqTrial.CheckArmed(armed.Trial, armed.Condition, armed.On, asked.TrialNumber, asked.LaserCondition, asked.LaserOn);
        }

        /// <inheritdoc/>
        public override Exception NotArmedByTrigger(int trialNumber, double triggerSeconds)
        {
            return ZapitDaqTrial.NotArmedByTrigger(trialNumber, triggerSeconds);
        }

        /// <inheritdoc/>
        public override Exception ArmedLate(int trialNumber, double triggerSeconds, double sentSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Trial {0}'s Arm went at {1} s, once the last trial's laser freed ZapitDaq, only {2:0.###} s before its trigger at {3} s, less than " +
                "ZapitDaq's {4} s arm time, so the workflow stopped. The task's laser timing leaves too little time between trials: shorten the laser's " +
                "hold or duration, or lengthen the shortest interval.", trialNumber, sentSeconds, triggerSeconds - sentSeconds, triggerSeconds, ZapitDaqTrial.ArmSeconds));
        }

        /// <inheritdoc/>
        public override Exception NeverArmed(int trialNumber)
        {
            return new InvalidOperationException(string.Format(
                "ZapitDaq never armed trial {0}, so the workflow stopped before the next trial was armed. Is the bank loaded and the NI DAQ answering?",
                trialNumber));
        }

        /// <summary>The task's laser timing, not ZapitDaq: the last laser held it past this trial's trigger (open_rigs.core.task
        /// Optogenetics.check_zapit_free_in_time refuses such a task file).</summary>
        public override Exception BusyAtTrigger(int trialNumber, double triggerSeconds, double freeAtSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Trial {0}'s trigger came at {1} s on the Harp clock, but the last trial's laser frees ZapitDaq only at {2} s, so the trial could not " +
                "be armed in time and the workflow stopped. The task's laser timing leaves too little time between trials: shorten the laser's hold " +
                "or duration, or lengthen the shortest interval.", trialNumber, triggerSeconds, freeAtSeconds));
        }

        /// <inheritdoc/>
        public override Exception StartedBeforeTrigger(int trialNumber)
        {
            return ZapitDaqTrial.StartedBeforeTrigger(trialNumber);
        }

        /// <summary>ZapitDaq's Go limits for a session: its <see cref="ZapitDaqTrial.ArmSeconds"/> arm time before each trigger, and
        /// <paramref name="busyAfterEndSeconds"/> after a laser's end (<see cref="BusyAfterEndSeconds"/>).</summary>
        public static OptoLimits Limits(double busyAfterEndSeconds)
        {
            return new OptoLimits("Zapit", ZapitDaqTrial.ArmSeconds, "arm time", busyAfterEndSeconds);
        }

        /// <summary>ZapitDaq's Go limits for a session on <paramref name="bank"/>: as <see cref="Limits(double)"/> with
        /// <see cref="BusyAfterEndSeconds"/>.</summary>
        public static OptoLimits Limits(ZapitBank bank, bool earlyStop)
        {
            return Limits(BusyAfterEndSeconds(bank, earlyStop));
        }

        /// <summary>How long ZapitDaq stays busy after a laser's end, in seconds: <see cref="ZapitDaqTrial.FreeAfterLaserSeconds"/> (up to a
        /// scan cycle, the bank's longest ramp and the free margin), plus ZapitDaq.EarlyStopLeadSeconds when an early stop ends the laser
        /// (<paramref name="earlyStop"/>: LaserTiming.UntilOutcome or UntilStimulusOff).</summary>
        public static double BusyAfterEndSeconds(ZapitBank bank, bool earlyStop)
        {
            return ZapitDaqTrial.FreeAfterLaserSeconds(bank) + (earlyStop ? OpenRigs.Zapit.ZapitDaq.EarlyStopLeadSeconds : 0);
        }
    }

    /// <summary>ZapitDaq's messages for the trial loop, and the loop run on them.</summary>
    public static class ZapitBonsaiOpto
    {
        /// <summary>A ZapitDaq event as the loop's opto feedback: Armed (its trial, condition, laser, capped power and whole trial
        /// length), Started (its trial), Ended, Stopped and Zeroed (Ended); null for any other event, which is not the loop's.</summary>
        public static OptoFeedback ToOptoFeedback(ZapitDaqEvent zapit)
        {
            if (zapit == null) return null;
            switch (zapit.Kind)
            {
                case ZapitDaqEventKind.Armed:
                    return new OptoFeedback
                    {
                        Kind = OptoFeedbackKind.Armed,
                        Trial = zapit.Trial,
                        Condition = zapit.Condition,
                        On = zapit.LaserOn,
                        PowerMw = zapit.CappedPowerMw,
                        PlaySeconds = zapit.TrialSeconds,
                        Source = zapit,
                    };
                case ZapitDaqEventKind.Started:
                    return new OptoFeedback { Kind = OptoFeedbackKind.Started, Trial = zapit.Trial, Source = zapit };
                case ZapitDaqEventKind.Ended:
                case ZapitDaqEventKind.Stopped:
                case ZapitDaqEventKind.Zeroed:
                    return new OptoFeedback { Kind = OptoFeedbackKind.Ended, Trial = zapit.Trial, Source = zapit };
                default:
                    return null;
            }
        }

        /// <summary>The trials as <see cref="TrialLoop.Observe{TTrial, TUpdate, TOutcome}(IObservable{ITrialRules{TTrial, TUpdate, TOutcome}}, IObservable{double}, IObservable{bool}[], IOptoSystem, IObservable{OptoFeedback}, IObservable{double})"/>,
        /// their laser trials armed by ZapitDaq: its events (Zapit.Events) as the opto feedback.</summary>
        public static IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Observe<TTrial, TUpdate, TOutcome>(
            IObservable<ITrialRules<TTrial, TUpdate, TOutcome>> rules, IObservable<double> clock, IObservable<bool>[] licks,
            IObservable<ZapitDaqEvent> zapitEvents, IObservable<double> wheelCm = null)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            if (zapitEvents == null) throw new ArgumentNullException("zapitEvents");
            return TrialLoop.Observe(rules, clock, licks, ZapitBonsaiOptoSystem.Instance, zapitEvents.Select(ToOptoFeedback).Where(f => f != null), wheelCm);
        }

        /// <summary>An event from ZapitDaq into a loop whose opto system is ZapitDaq's (<see cref="TrialLoop{TTrial, TUpdate, TOutcome}.OnOpto"/>).</summary>
        public static void OnZapitEvent<TTrial, TUpdate, TOutcome>(this TrialLoop<TTrial, TUpdate, TOutcome> loop, ZapitDaqEvent zapit)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            loop.OnOpto(ToOptoFeedback(zapit));
        }
    }
}
