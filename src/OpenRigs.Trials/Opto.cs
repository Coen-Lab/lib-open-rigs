using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Trials
{
    /// <summary>What an opto system (an inactivation system, such as Zapit) reads from a task's trial: its number (ITrial) and its laser.</summary>
    public interface IOptoTrial : ITrial
    {
        /// <summary>Gets the trial's condition in the system's conditions, from 1.</summary>
        int LaserCondition { get; }

        /// <summary>Gets where the condition aims, in the system's own notation, as the trial log's laserPointsMm (Zapit: "AP ML;AP ML" in mm).</summary>
        string LaserPointsMm { get; }

        /// <summary>Gets whether the light is on on this trial (else a control trial: the same targeting and masking, no light).</summary>
        bool LaserOn { get; }

        /// <summary>Gets the power on a light-on trial, in mW.</summary>
        double LaserPowerMw { get; }
    }

    /// <summary>An opto trial with its own duration (LaserTiming.ForDuration: each trial's, from its combination).</summary>
    public interface IOptoDurationTrial : IOptoTrial
    {
        /// <summary>Gets how long the light runs from the trigger, in seconds, above 0.</summary>
        double LaserDurationSeconds { get; }
    }

    /// <summary>What an opto system tells the loop (<see cref="OptoFeedback.Kind"/>).</summary>
    public enum OptoFeedbackKind
    {
        /// <summary>The system armed a trial (its answer to the loop's Arm): checked against what the trial asked.</summary>
        Armed,
        /// <summary>The system started the trial it armed (its samples began after the trigger); only a system that confirms starts sends it.</summary>
        Started,
        /// <summary>The system ended or stopped a trial (or answered an end-now); a system whose end needs a reply frees the next Arm with it.</summary>
        Ended
    }

    /// <summary>
    /// One message from an opto system to the trial loop, mapped from the system's own message by its adapter
    /// (OpenRigs.Opto.ZapitBonsai ZapitBonsaiOpto.ToOptoFeedback, OpenRigs.Opto.ZapitMatlab ZapitMatlabOpto.ToOptoFeedback).
    /// </summary>
    public sealed class OptoFeedback
    {
        /// <summary>Gets or sets what the message is.</summary>
        public OptoFeedbackKind Kind { get; set; }

        /// <summary>Gets or sets the trial it is about, from 1; 0 when the system does not say (Armed: the trial the loop is waiting on).</summary>
        public int Trial { get; set; }

        /// <summary>Gets or sets the condition armed (Armed).</summary>
        public int Condition { get; set; }

        /// <summary>Gets or sets whether the light is armed on (Armed).</summary>
        public bool On { get; set; }

        /// <summary>Gets or sets the power the system will deliver, in mW (Armed; 0 when the system does not say): what the trial row logs once started.</summary>
        public double PowerMw { get; set; }

        /// <summary>Gets or sets the whole play the system armed, ramps included, in seconds (Armed; 0 when the system does not say): IOptoSystem.FreeAt reads it.</summary>
        public double PlaySeconds { get; set; }

        /// <summary>Gets or sets the system's own message this was mapped from, for the system's own checks and words (IOptoSystem.CheckArmed); null if none.</summary>
        public object Source { get; set; }
    }

    /// <summary>
    /// An opto system's policy, which <see cref="TrialLoop{TTrial, TUpdate, TOutcome}"/> consults and never hard-codes: how long an Arm
    /// takes, whether the system confirms its start, whether an end-now needs its reply before the next Arm, when it is free after a timed
    /// trial, and the words of each stop. Each system's adapter package implements it (OpenRigs.Opto.ZapitBonsai, OpenRigs.Opto.ZapitMatlab);
    /// adding a system adds an adapter, never a change to the loop. Every adapter's include must also drive its outputs to zero when the
    /// workflow stops or errors, and stop the light at <see cref="OptoBackstop"/> should the Harp clock stall.
    /// </summary>
    public interface IOptoSystem
    {
        /// <summary>Gets the system's name, as the loop's stops say it.</summary>
        string Name { get; }

        /// <summary>Gets the least time from a trial's Arm to its trigger, in seconds: an Arm sent later than this before the trigger stops the workflow.</summary>
        double ArmLeadSeconds { get; }

        /// <summary>Gets whether the system confirms each trial's start (OptoFeedbackKind.Started): only then does the trial log the armed power and laserStarted.</summary>
        bool ConfirmsStart { get; }

        /// <summary>Gets how long after the trigger a Started may still be on its way, in seconds: an outcome sooner logs the armed power (with ConfirmsStart).</summary>
        double StartedGraceSeconds { get; }

        /// <summary>Gets whether an end-now (a light played until stopped) needs the system's reply (OptoFeedbackKind.Ended) before the next Arm.</summary>
        bool EndNeedsReply { get; }

        /// <summary>Gets how long the next Arm waits past that reply, in seconds, from the first tick after it (with EndNeedsReply).</summary>
        double EndReplyMarginSeconds { get; }

        /// <summary>Gets whether an Arm of a light played until stopped carries the most it plays (TrialEvent.LaserSeconds, LaserTiming.ArmedSecondsOf); else 0.</summary>
        bool ArmCarriesLaserSeconds { get; }

        /// <summary>Why the system cannot run the task's laser timing, or null (such as a system that arms every trial before its trigger and a timing that is not hardware triggered).</summary>
        string TimingProblem(LaserTiming laser);

        /// <summary>When the system is free for the next Arm after a timed trial (LaserTiming.ForDuration) triggered at triggerSeconds, playing durationSeconds,
        /// with the timing's rampDownSeconds, armed for armedPlaySeconds (its Armed's PlaySeconds; 0 when the system does not say).</summary>
        double FreeAt(double triggerSeconds, double durationSeconds, double rampDownSeconds, double armedPlaySeconds);

        /// <summary>Throws, in the system's words, unless an Armed is the trial's, as it asked: its condition and light (and its number when the system gives one).</summary>
        void CheckArmed(OptoFeedback armed, IOptoTrial asked);

        /// <summary>The stop for a trial whose trigger came before the system armed it: the task stops rather than move the trigger.</summary>
        Exception NotArmedByTrigger(int trialNumber, double triggerSeconds);

        /// <summary>The stop for a trial whose Arm went less than ArmLeadSeconds before its trigger, once the last trial's light freed the system.</summary>
        Exception ArmedLate(int trialNumber, double triggerSeconds, double sentSeconds);

        /// <summary>The stop for a trial the system never armed, before the next Arm.</summary>
        Exception NeverArmed(int trialNumber);

        /// <summary>The stop for a trial whose trigger came while the last trial's light still held the system (the task's laser timing).</summary>
        Exception BusyAtTrigger(int trialNumber, double triggerSeconds, double freeAtSeconds);

        /// <summary>The stop for a Started that came before the trial's trigger.</summary>
        Exception StartedBeforeTrigger(int trialNumber);

        /// <summary>The stop for a trial whose trigger came before the system answered the last end-now (EndNeedsReply).</summary>
        Exception EndNotAnswered(int stoppedTrialNumber, int trialNumber, double triggerSeconds);

        /// <summary>The stop for a trial whose trigger came before EndReplyMarginSeconds had passed since the system answered the last end-now.</summary>
        Exception EndAnsweredLate(int stoppedTrialNumber, int trialNumber, double freeAtSeconds, double triggerSeconds);
    }

    /// <summary>
    /// A base for an <see cref="IOptoSystem"/>: the stops worded from its Name, and CheckArmed from the Armed's condition, light and trial.
    /// An adapter overrides what its system words or checks differently.
    /// </summary>
    public abstract class OptoSystem : IOptoSystem
    {
        /// <inheritdoc/>
        public abstract string Name { get; }

        /// <inheritdoc/>
        public abstract double ArmLeadSeconds { get; }

        /// <inheritdoc/>
        public abstract bool ConfirmsStart { get; }

        /// <inheritdoc/>
        public virtual double StartedGraceSeconds { get { return 0; } }

        /// <inheritdoc/>
        public abstract bool EndNeedsReply { get; }

        /// <inheritdoc/>
        public virtual double EndReplyMarginSeconds { get { return 0; } }

        /// <inheritdoc/>
        public virtual bool ArmCarriesLaserSeconds { get { return false; } }

        /// <inheritdoc/>
        public virtual string TimingProblem(LaserTiming laser)
        {
            return null;
        }

        /// <inheritdoc/>
        public virtual double FreeAt(double triggerSeconds, double durationSeconds, double rampDownSeconds, double armedPlaySeconds)
        {
            return triggerSeconds + durationSeconds + rampDownSeconds;
        }

        /// <inheritdoc/>
        public virtual void CheckArmed(OptoFeedback armed, IOptoTrial asked)
        {
            if ((armed.Trial != 0 && armed.Trial != asked.TrialNumber) || armed.Condition != asked.LaserCondition || armed.On != asked.LaserOn)
            {
                throw new InvalidOperationException(string.Format(
                    "{0} armed trial {1} (condition {2}, light {3}) when trial {4} asked for condition {5}, light {6}, so the workflow stopped.",
                    Name, armed.Trial, armed.Condition, armed.On ? "on" : "off", asked.TrialNumber, asked.LaserCondition, asked.LaserOn ? "on" : "off"));
            }
        }

        /// <inheritdoc/>
        public virtual Exception NotArmedByTrigger(int trialNumber, double triggerSeconds)
        {
            return new InvalidOperationException(string.Format(
                "{0} had not armed trial {1} by its trigger at {2} s on the Harp clock, so the workflow stopped rather than move the trigger.", Name, trialNumber, triggerSeconds));
        }

        /// <inheritdoc/>
        public virtual Exception ArmedLate(int trialNumber, double triggerSeconds, double sentSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Trial {0}'s Arm went at {1} s, once the last trial's light freed {2}, only {3:0.###} s before its trigger at {4} s, less than " +
                "{2}'s {5} s arm time, so the workflow stopped. The task's laser timing leaves too little time between trials: shorten the light's " +
                "hold or duration, or lengthen the shortest interval.", trialNumber, sentSeconds, Name, triggerSeconds - sentSeconds, triggerSeconds, ArmLeadSeconds));
        }

        /// <inheritdoc/>
        public virtual Exception NeverArmed(int trialNumber)
        {
            return new InvalidOperationException(string.Format("{0} never armed trial {1}, so the workflow stopped before the next trial was armed.", Name, trialNumber));
        }

        /// <inheritdoc/>
        public virtual Exception BusyAtTrigger(int trialNumber, double triggerSeconds, double freeAtSeconds)
        {
            return new InvalidOperationException(string.Format(
                "Trial {0}'s trigger came at {1} s on the Harp clock, but the last trial's light frees {2} only at {3} s, so the trial could not " +
                "be armed in time and the workflow stopped. The task's laser timing leaves too little time between trials: shorten the light's hold " +
                "or duration, or lengthen the shortest interval.", trialNumber, triggerSeconds, Name, freeAtSeconds));
        }

        /// <inheritdoc/>
        public virtual Exception StartedBeforeTrigger(int trialNumber)
        {
            return new InvalidOperationException(string.Format(
                "{0} started trial {1} before the workflow raised its trigger, so the light played outside the trial and the workflow stopped. Check the trigger line's wiring for a stray edge.",
                Name, trialNumber));
        }

        /// <inheritdoc/>
        public virtual Exception EndNotAnswered(int stoppedTrialNumber, int trialNumber, double triggerSeconds)
        {
            return new InvalidOperationException(string.Format(
                "{0} had not answered trial {1}'s stop by trial {2}'s trigger at {3} s on the Harp clock, so trial {2} could not be armed and the workflow stopped.",
                Name, stoppedTrialNumber, trialNumber, triggerSeconds));
        }

        /// <inheritdoc/>
        public virtual Exception EndAnsweredLate(int stoppedTrialNumber, int trialNumber, double freeAtSeconds, double triggerSeconds)
        {
            return new InvalidOperationException(string.Format(
                "{0} answered trial {1}'s stop too late for trial {2}: its Arm waits {3} s past that reply, to {4} s on the Harp clock, after its trigger at {5} s, " +
                "so the workflow stopped. Lengthen the shortest interval.", Name, stoppedTrialNumber, trialNumber, EndReplyMarginSeconds, freeAtSeconds, triggerSeconds));
        }
    }

    /// <summary>
    /// An opto system's Go limits for a session, from its adapter: the least time from Arm to trigger and how long the system stays busy
    /// after a light's end. A task's Go check is one rule for every system: the lead plus ArmLeadSeconds fits in the shortest interval
    /// (<see cref="LeadProblem"/>), and each light's duration leaves OverheadSeconds before the next trigger.
    /// </summary>
    public sealed class OptoLimits
    {
        /// <summary>Limits of the system named <paramref name="who"/>: its Arm needs <paramref name="armLeadSeconds"/> before the trigger,
        /// called <paramref name="armLeadName"/> in its stops (such as "arm time"), and it stays busy <paramref name="busyAfterEndSeconds"/> after a light's end.</summary>
        public OptoLimits(string who, double armLeadSeconds, string armLeadName, double busyAfterEndSeconds)
        {
            Who = who;
            ArmLeadSeconds = armLeadSeconds;
            ArmLeadName = armLeadName;
            BusyAfterEndSeconds = busyAfterEndSeconds;
        }

        /// <summary>Gets who the limits are, as Go's problems name it (such as "Zapit").</summary>
        public string Who { get; private set; }

        /// <summary>Gets the least time from Arm to trigger, in seconds.</summary>
        public double ArmLeadSeconds { get; private set; }

        /// <summary>Gets what the system calls ArmLeadSeconds in Go's problems (such as "arm time" or "reply margin").</summary>
        public string ArmLeadName { get; private set; }

        /// <summary>Gets how long the system stays busy after a light's end, before the next Arm, in seconds.</summary>
        public double BusyAfterEndSeconds { get; private set; }

        /// <summary>Gets BusyAfterEndSeconds plus ArmLeadSeconds: what a light's end must leave before the next trigger.</summary>
        public double OverheadSeconds { get { return BusyAfterEndSeconds + ArmLeadSeconds; } }

        /// <summary>
        /// Why the task file's lead is refused, or null: the trigger comes laserToStimulusSeconds before the stimulus, and the system needs
        /// ArmLeadSeconds before the trigger, all inside the shortest interval (open_rigs.core.task Optogenetics.check_lead_fits, the same rule).
        /// </summary>
        public string LeadProblem(double laserToStimulusSeconds, double itiMinSeconds)
        {
            if (laserToStimulusSeconds + ArmLeadSeconds <= itiMinSeconds + 1e-9) return null;
            return string.Format(
                "optogenetics.laserToStimulusSeconds ({0} s) plus {1}'s {2} s {3} must be at most trialStructure.itiMinSeconds ({4} s), the shortest interval",
                laserToStimulusSeconds, Who, ArmLeadSeconds, ArmLeadName, itiMinSeconds);
        }
    }

    /// <summary>Each triggered trial again its rules' LaserCapSeconds later on the computer's clock (never with no cap): the backstop
    /// with which an opto adapter's include stops the light should the Harp board stall so that LaserCap never comes.</summary>
    [Combinator]
    [Description("Each input trial (with the rules' laser timing, at its trigger) again LaserCapSeconds later on the computer's clock, never with no cap: the backstop should the Harp board stall.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class OptoBackstop
    {
        /// <summary>Each trial at its cap.</summary>
        public IObservable<TTrial> Process<TTrial>(IObservable<Tuple<TTrial, LaserTiming>> source)
        {
            return source.SelectMany(t => double.IsInfinity(t.Item2.LaserCapSeconds) ? Observable.Never<TTrial>()
                : Observable.Timer(TimeSpan.FromSeconds(t.Item2.LaserCapSeconds)).Select(_ => t.Item1));
        }
    }
}
