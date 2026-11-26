using OpenRigs.Devices.Zapit;
using OpenRigs.Trials;
using System;
using System.Reactive.Linq;

namespace OpenRigs.Opto.ZapitMatlab
{
    /// <summary>
    /// MATLAB Zapit (Zapit-MATLAB, its TCP client OpenRigs.Devices.Zapit) as the trial loop's opto system: its reply to a trial's
    /// StartStimulus must come <see cref="ZapitTrial.ReplyMarginSeconds"/> before the trigger, it never confirms a start, and after a
    /// laser played until stopped the next trial is armed only once it has answered that StopStimulus and
    /// <see cref="StopReplyMarginSeconds"/> more have passed. Goes when MATLAB Zapit is retired.
    /// </summary>
    public sealed class ZapitMatlabOptoSystem : OptoSystem
    {
        /// <summary>How long the next trial's StartStimulus waits past Zapit's reply to a StopStimulus, in seconds, from the first tick after
        /// it: MATLAB Zapit stops whatever is running 0.2 s after each stop (its delayStop timer), so a trial armed sooner plays no laser.</summary>
        public const double StopReplyMarginSeconds = 0.25;

        ZapitMatlabOptoSystem()
        {
        }

        /// <summary>The one MATLAB Zapit opto system.</summary>
        public static readonly ZapitMatlabOptoSystem Instance = new ZapitMatlabOptoSystem();

        /// <inheritdoc/>
        public override string Name { get { return "Zapit"; } }

        /// <inheritdoc/>
        public override double ArmLeadSeconds { get { return ZapitTrial.ReplyMarginSeconds; } }

        /// <inheritdoc/>
        public override bool ConfirmsStart { get { return false; } }

        /// <inheritdoc/>
        public override bool EndNeedsReply { get { return true; } }

        /// <inheritdoc/>
        public override double EndReplyMarginSeconds { get { return StopReplyMarginSeconds; } }

        /// <summary>Throws unless Zapit's reply armed the trial as it asked (<see cref="ZapitTrial.CheckArmed"/>, on the reply itself).</summary>
        public override void CheckArmed(OptoFeedback armed, IOptoTrial asked)
        {
            var reply = armed.Source as ZapitReply ?? new ZapitReply { Command = ZapitCommandType.StartStimulus, Value = armed.Condition, LaserOn = armed.On ? 1 : 0 };
            ZapitTrial.CheckArmed(reply, asked.TrialNumber, asked.LaserCondition, asked.LaserOn);
        }

        /// <inheritdoc/>
        public override Exception NotArmedByTrigger(int trialNumber, double triggerSeconds)
        {
            return ZapitTrial.NotArmedByTrigger(trialNumber, triggerSeconds);
        }

        /// <inheritdoc/>
        public override Exception ArmedLate(int trialNumber, double triggerSeconds, double sentSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Trial {0}'s StartStimulus went at {1} s, once the last trial's laser (and Zapit's reply to its StopStimulus) freed Zapit, only {2:0.###} s before its trigger at {3} s, less than " +
                "Zapit's {4} s reply margin, so the workflow stopped. The task's laser timing leaves too little time between trials: shorten the laser's " +
                "hold or duration, or lengthen the shortest interval.", trialNumber, sentSeconds, triggerSeconds - sentSeconds, triggerSeconds, ZapitTrial.ReplyMarginSeconds));
        }

        /// <inheritdoc/>
        public override Exception NeverArmed(int trialNumber)
        {
            return new InvalidOperationException(string.Format(
                "Zapit never answered trial {0}'s StartStimulus, so the workflow stopped before the next one was sent. Is Zapit running and answering?",
                trialNumber));
        }

        /// <summary>The task's laser timing, not Zapit: the last laser held Zapit past this trial's trigger (open_rigs.core.task
        /// Optogenetics.check_zapit_free_in_time refuses such a task file).</summary>
        public override Exception BusyAtTrigger(int trialNumber, double triggerSeconds, double freeAtSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Trial {0}'s trigger came at {1} s on the Harp clock, but the last trial's laser frees Zapit only at {2} s, so the trial's StartStimulus " +
                "could not go in time and the workflow stopped. The task's laser timing leaves too little time between trials: shorten the laser's hold " +
                "or duration, or lengthen the shortest interval.", trialNumber, triggerSeconds, freeAtSeconds));
        }

        /// <inheritdoc/>
        public override Exception EndNotAnswered(int stoppedTrialNumber, int trialNumber, double triggerSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Zapit had not answered trial {0}'s StopStimulus by trial {1}'s trigger at {2} s on the Harp clock, so trial {1}'s StartStimulus could not " +
                "go and the workflow stopped (a StartStimulus sent while Zapit is still stopping is stopped too). Is Zapit running and answering?",
                stoppedTrialNumber, trialNumber, triggerSeconds));
        }

        /// <inheritdoc/>
        public override Exception EndAnsweredLate(int stoppedTrialNumber, int trialNumber, double freeAtSeconds, double triggerSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Zapit answered trial {0}'s StopStimulus too late for trial {1}: its StartStimulus waits {2} s past that reply, to {3} s on the Harp clock, " +
                "after its trigger at {4} s, so the workflow stopped. Zapit takes longer to stop than the task leaves between trials: lengthen the shortest interval.",
                stoppedTrialNumber, trialNumber, StopReplyMarginSeconds, freeAtSeconds, triggerSeconds));
        }

        /// <summary>MATLAB Zapit's Go limits for a session whose conditions file ramps down for <paramref name="rampDownSeconds"/> at most
        /// (ZapitConditionsFile.MaxRampDownSeconds): its <see cref="ZapitTrial.ReplyMarginSeconds"/> reply margin before each trigger, and
        /// the ramp-down after a laser's end.</summary>
        public static OptoLimits Limits(double rampDownSeconds)
        {
            return new OptoLimits("Zapit", ZapitTrial.ReplyMarginSeconds, "reply margin", rampDownSeconds);
        }
    }

    /// <summary>MATLAB Zapit's replies for the trial loop, and the loop run on them. Goes when MATLAB Zapit is retired.</summary>
    public static class ZapitMatlabOpto
    {
        /// <summary>A reply from Zapit as the loop's opto feedback: one to StartStimulus is Armed (its condition and laser; the reply itself
        /// for <see cref="ZapitMatlabOptoSystem.CheckArmed"/>), one to StopStimulus Ended; null for any other reply, which is not the loop's.</summary>
        public static OptoFeedback ToOptoFeedback(ZapitReply reply)
        {
            if (reply == null) return null;
            if (reply.Command == ZapitCommandType.StartStimulus)
                return new OptoFeedback { Kind = OptoFeedbackKind.Armed, Condition = reply.Value, On = reply.LaserOn == 1, Source = reply };
            if (reply.Command == ZapitCommandType.StopStimulus)
                return new OptoFeedback { Kind = OptoFeedbackKind.Ended, Source = reply };
            return null;
        }

        /// <summary>The trials as <see cref="TrialLoop.Observe{TTrial, TUpdate, TOutcome}(IObservable{ITrialRules{TTrial, TUpdate, TOutcome}}, IObservable{double}, IObservable{bool}[], IOptoSystem, IObservable{OptoFeedback}, IObservable{double})"/>,
        /// their laser trials armed by MATLAB Zapit: its replies (ZapitReplies) as the opto feedback.</summary>
        public static IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Observe<TTrial, TUpdate, TOutcome>(
            IObservable<ITrialRules<TTrial, TUpdate, TOutcome>> rules, IObservable<double> clock, IObservable<bool>[] licks,
            IObservable<ZapitReply> zapitReplies, IObservable<double> wheelCm = null)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            if (zapitReplies == null) throw new ArgumentNullException("zapitReplies");
            return TrialLoop.Observe(rules, clock, licks, ZapitMatlabOptoSystem.Instance, zapitReplies.Select(ToOptoFeedback).Where(f => f != null), wheelCm);
        }

        /// <summary>A reply from Zapit into a loop whose opto system is MATLAB Zapit's (<see cref="TrialLoop{TTrial, TUpdate, TOutcome}.OnOpto"/>).</summary>
        public static void OnZapitReply<TTrial, TUpdate, TOutcome>(this TrialLoop<TTrial, TUpdate, TOutcome> loop, ZapitReply reply)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            loop.OnOpto(ToOptoFeedback(reply));
        }
    }
}
