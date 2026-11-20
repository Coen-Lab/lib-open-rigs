using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;

namespace OpenRigs.Trials
{
    /// <summary>What a <see cref="TrialLoop{TTrial, TUpdate, TOutcome}"/> event is.</summary>
    public enum TrialEventKind
    {
        /// <summary>The trial was drawn and its inter-trial interval began; Seconds is the interval's start (itiStartTime), exactly.</summary>
        Iti,
        /// <summary>On a hardware-triggered rig, a laser trial's StartStimulus goes now, during its interval.</summary>
        Arm,
        /// <summary>The trial starts; Seconds is the start, exactly: the interval's end less the lead, or later until the quiet waits hold.</summary>
        Start,
        /// <summary>A laser trial's trigger: the trigger line rises; on a rig without hardware triggering its StartStimulus goes now.</summary>
        Trigger,
        /// <summary>The stimulus goes on, at the first tick at or after the trial start plus the lead.</summary>
        Stimulus,
        /// <summary>The task's own change during the trial, in Update (a pip, a moved grating).</summary>
        Update,
        /// <summary>One of the task's timed cues, named in Cue.</summary>
        Cue,
        /// <summary>The trial ended, its outcome in Outcome.</summary>
        Outcome,
        /// <summary>The laser has run to its cap (LaserTiming.LaserCapSeconds from the trigger) with no outcome yet: stop it.</summary>
        LaserCap,
        /// <summary>The trigger line falls.</summary>
        TriggerOff,
        /// <summary>The trial's stimulus goes off (StimulusTiming): once per trial whose stimulus came on and whose timing has a
        /// StimulusDurationSeconds or ResponseToStimulusOffSeconds, at the first tick at or after StimulusTiming.Time, or at the outcome itself when that is the time.</summary>
        StimulusOff
    }

    /// <summary>When a trial's laser ends, and so when the next trial's StartStimulus may go.</summary>
    public enum LaserEnd
    {
        /// <summary>The laser runs until the outcome (or LaserCap); the trigger line falls TriggerHoldSeconds after the outcome.</summary>
        AtOutcome,
        /// <summary>The laser runs the trial's LaserDurationSeconds (ILaserDurationTrial) from the trigger, outcome or not; the line falls then.</summary>
        AfterDuration
    }

    /// <summary>
    /// An experiment's laser timing: the triggering, when the laser and its trigger line end, and when the next trial
    /// may be armed (the line fallen, plus any ramp-down Zapit plays before it takes another StartStimulus).
    /// </summary>
    public sealed class LaserTiming
    {
        LaserTiming()
        {
        }

        /// <summary>Gets whether the Zapit DAQ waits for the trigger line: its StartStimulus goes during the interval (Arm), else at the trigger.</summary>
        public bool HardwareTriggered { get; private set; }

        /// <summary>Gets when the laser ends.</summary>
        public LaserEnd End { get; private set; }

        /// <summary>Gets how long the trigger line stays up past the outcome, in seconds (AtOutcome).</summary>
        public double TriggerHoldSeconds { get; private set; }

        /// <summary>Gets how long after the line falls Zapit takes the next StartStimulus, in seconds (AfterDuration).</summary>
        public double RampDownSeconds { get; private set; }

        /// <summary>Gets how long after the trigger LaserCap comes with no outcome yet, in seconds (AtOutcome); infinity for none.</summary>
        public double LaserCapSeconds { get; private set; }

        /// <summary>The laser on until the outcome, stopped by LaserCap laserCapSeconds after the trigger; the line held triggerHoldSeconds past the outcome (the conditions file's MaxRampDownSeconds, ZapitConditions.Check).</summary>
        public static LaserTiming UntilOutcome(bool hardwareTriggered, double triggerHoldSeconds, double laserCapSeconds)
        {
            Check("triggerHoldSeconds", triggerHoldSeconds, false);
            if (!(laserCapSeconds > 0)) throw new ArgumentException(string.Format("LaserTiming: laserCapSeconds is {0}; it must be above 0 (infinity for no cap).", laserCapSeconds));
            return new LaserTiming
            {
                HardwareTriggered = hardwareTriggered,
                End = LaserEnd.AtOutcome,
                TriggerHoldSeconds = triggerHoldSeconds,
                LaserCapSeconds = laserCapSeconds,
            };
        }

        /// <summary>The laser on for each trial's own duration from the trigger (every laser trial an ILaserDurationTrial); the next trial armed rampDownSeconds after the line falls.</summary>
        public static LaserTiming ForDuration(bool hardwareTriggered, double rampDownSeconds)
        {
            Check("rampDownSeconds", rampDownSeconds, false);
            return new LaserTiming
            {
                HardwareTriggered = hardwareTriggered,
                End = LaserEnd.AfterDuration,
                RampDownSeconds = rampDownSeconds,
                LaserCapSeconds = double.PositiveInfinity,
            };
        }

        /// <summary>A trial's laser duration under ForDuration: its ILaserDurationTrial's, a finite number above 0; throws, naming the trial, otherwise.</summary>
        public static double DurationOf(ITrial trial)
        {
            var timed = trial as ILaserDurationTrial;
            if (timed == null) throw new InvalidOperationException(string.Format("Trial {0} is a laser trial of a LaserTiming.ForDuration task, but has no laser duration of its own (ILaserDurationTrial).", trial.TrialNumber));
            Check("trial " + trial.TrialNumber + "'s laserDurationSeconds", timed.LaserDurationSeconds, true);
            return timed.LaserDurationSeconds;
        }

        static void Check(string name, double value, bool aboveZero)
        {
            if (!(aboveZero ? value > 0 : value >= 0) || double.IsInfinity(value))
            {
                throw new ArgumentException(string.Format("LaserTiming: {0} is {1}; it must be a finite number {2}.", name, value, aboveZero ? "above 0" : "from 0"));
            }
        }
    }

    /// <summary>What a task's cue is timed from.</summary>
    public enum CueAnchor
    {
        /// <summary>The stimulus as it was due (the trial start plus the lead); a trial whose stimulus never came has none of these after
        /// it. Negative seconds come before the stimulus, within the trial's lead, which grows to hold them (TrialLoop.Lead).</summary>
        Stimulus,
        /// <summary>The start of the interval after the trial's outcome (the outcome plus its timeout and reward consumption, ITrialOutcome).</summary>
        NextIti
    }

    /// <summary>
    /// A moment of a trial the task names, such as the grating off or the spouts in: a Cue event at the first tick at or
    /// past it, outcome or not, unless it is dropped at the outcome (DroppedAtOutcome).
    /// </summary>
    public sealed class TrialCue
    {
        /// <summary>A cue named <paramref name="name"/>, <paramref name="seconds"/> after its anchor, outcome or not.</summary>
        public TrialCue(string name, CueAnchor anchor, double seconds) : this(name, anchor, seconds, false)
        {
        }

        /// <summary>
        /// A cue named <paramref name="name"/>, <paramref name="seconds"/> after its anchor; with
        /// <paramref name="droppedAtOutcome"/>, a cue still to come at the trial's outcome never comes (a Stimulus cue only).
        /// </summary>
        public TrialCue(string name, CueAnchor anchor, double seconds, bool droppedAtOutcome)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("TrialCue: a cue needs a name.");
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || (seconds < 0 && anchor != CueAnchor.Stimulus)) throw new ArgumentException(string.Format("TrialCue: cue {0} is {1} s after its anchor; it must be a finite number, from 0 unless timed from the stimulus.", name, seconds));
            if (droppedAtOutcome && anchor != CueAnchor.Stimulus) throw new ArgumentException(string.Format("TrialCue: cue {0} is timed from the next interval, which starts after the outcome, so it cannot be dropped at the outcome.", name));
            Name = name;
            Anchor = anchor;
            Seconds = seconds;
            DroppedAtOutcome = droppedAtOutcome;
        }

        /// <summary>Gets whether the cue, if still to come at the trial's outcome, never comes (a cue that belongs to the response, such as the spouts in).</summary>
        public bool DroppedAtOutcome { get; private set; }

        /// <summary>Gets the cue's name, as a Cue event carries it.</summary>
        public string Name { get; private set; }

        /// <summary>Gets what the cue is timed from.</summary>
        public CueAnchor Anchor { get; private set; }

        /// <summary>Gets the seconds from the anchor.</summary>
        public double Seconds { get; private set; }
    }

    /// <summary>What one trial asks of the loop, given by the task's rules as the trial is drawn.</summary>
    public sealed class TrialTiming
    {
        /// <summary>A trial with no interval, lead, quiet waits, laser or cues and no response limit.</summary>
        public TrialTiming()
        {
            ResponseWindowSeconds = double.PositiveInfinity;
            StimulusDurationSeconds = double.NaN;
        }

        /// <summary>Gets or sets the longest the stimulus lasts from its onset (stimulus.durationSeconds); NaN (the default): no
        /// cap, the off then only ResponseToStimulusOffSeconds after the outcome; both unset, no off is timed and no StimulusOff comes.</summary>
        public double StimulusDurationSeconds { get; set; }

        /// <summary>Gets or sets the outcome to the stimulus off (stimulus.responseToStimulusOffSeconds): null, the response does not end
        /// it; 0, off at the outcome; X, off X s after it, whichever of that and the duration comes first.</summary>
        public double? ResponseToStimulusOffSeconds { get; set; }

        /// <summary>Gets or sets the inter-trial interval drawn for the trial (itiSeconds): its start to the stimulus with a quiet mouse.</summary>
        public double ItiSeconds { get; set; }

        /// <summary>Gets or sets the signed lead (laserToStimulusSeconds): the trigger this long before the stimulus, after it if negative. The trial starts TrialLoop.Lead (max of this, the earliest Stimulus cue's lead and 0) before the stimulus in every experiment.</summary>
        public double LaserToStimulusSeconds { get; set; }

        /// <summary>Gets or sets how long every spout must be released before the trial starts; 0 for none (QuietWait).</summary>
        public double LickQuietSeconds { get; set; }

        /// <summary>Gets or sets the window the wheel's travel is counted over before the trial starts; 0 for none (QuietWait).</summary>
        public double WheelQuietSeconds { get; set; }

        /// <summary>Gets or sets the most wheel travel, in cm, that window may hold (QuietWait).</summary>
        public double WheelQuietCm { get; set; }

        /// <summary>Gets or sets whether the trial is armed and triggered (every trial of an optogenetics experiment, laser on or off).</summary>
        public bool Laser { get; set; }

        /// <summary>Gets or sets the stimulus to the response window's opening, in seconds (an open loop).</summary>
        public double ResponseDelaySeconds { get; set; }

        /// <summary>Gets or sets how long the response window stays open, in seconds; at its end the rules' ResponseEnd gives the outcome. Infinity for no limit.</summary>
        public double ResponseWindowSeconds { get; set; }

        /// <summary>Gets or sets the trial's cues; null or empty for none.</summary>
        public IList<TrialCue> Cues { get; set; }
    }

    /// <summary>
    /// The Harp times of the trial so far, as its row logs them (the lab's shared block of trial columns): each a time
    /// on the Harp clock, NaN until it comes. Given to the rules with each input, so an outcome built from it is timed at Now.
    /// </summary>
    public sealed class TrialTimes
    {
        /// <summary>Gets or sets when the inter-trial interval before the trial began (itiStartTime).</summary>
        public double ItiStartTime { get; set; }

        /// <summary>Gets or sets the inter-trial interval drawn for the trial (itiSeconds).</summary>
        public double ItiSeconds { get; set; }

        /// <summary>Gets or sets how long the quiet waits held the start past the interval's end less the lead (quietWaitSeconds).</summary>
        public double QuietWaitSeconds { get; set; }

        /// <summary>Gets or sets the trial start, exactly (trialStartTime).</summary>
        public double TrialStartTime { get; set; }

        /// <summary>Gets or sets when the stimulus was due: the trial start plus the lead.</summary>
        public double StimulusTime { get; set; }

        /// <summary>Gets or sets the tick the stimulus was commanded on (stimulusOnsetTime); NaN before.</summary>
        public double StimulusOnsetTime { get; set; }

        /// <summary>Gets or sets when the response window opens: StimulusTime plus the response delay.</summary>
        public double ResponseStartTime { get; set; }

        /// <summary>Gets or sets when it closes; infinity for no limit.</summary>
        public double ResponseEndTime { get; set; }

        /// <summary>Gets or sets when the stimulus goes off were the outcome at Now (stimulusOffTime, StimulusTiming.Time): at the
        /// outcome, the trial's off time. NaN with no onset, or neither StimulusDurationSeconds nor ResponseToStimulusOffSeconds.</summary>
        public double StimulusOffTime { get; set; }

        /// <summary>Gets or sets the tick the trigger line rose (laserTriggerTime); NaN if it has not.</summary>
        public double LaserTriggerTime { get; set; }

        /// <summary>Gets or sets the Harp clock's latest tick: an outcome given now is timed then (outcomeTime).</summary>
        public double Now { get; set; }

        /// <summary>Gets whether the laser was triggered: an outcome given now logs its power, else 0 mW.</summary>
        public bool LaserFired { get { return !double.IsNaN(LaserTriggerTime); } }

        /// <summary>Gets whether the response window is open at Now.</summary>
        public bool Responding { get { return Now >= ResponseStartTime - TrialLoop.Tolerance; } }
    }

    /// <summary>A trial as the loop needs it: its number. A laser trial's is also an <see cref="IZapitTrial"/>.</summary>
    public interface ITrial
    {
        /// <summary>Gets the trial's number, from 1.</summary>
        int TrialNumber { get; }
    }

    /// <summary>A trial's outcome as a task's rules give it: the loop needs only what comes before the next interval, which
    /// starts at the outcome plus PenaltyTimeoutSeconds plus RewardConsumptionSeconds (one of them 0 on any trial the lab runs).</summary>
    public interface ITrialOutcome
    {
        /// <summary>Gets the timeout after the trial, in seconds (penaltyTimeoutSeconds): 0 unless penalised.</summary>
        double PenaltyTimeoutSeconds { get; }

        /// <summary>Gets the time to drink the reward after the trial, in seconds (rewardConsumptionSeconds): 0 unless rewarded.</summary>
        double RewardConsumptionSeconds { get; }
    }

    /// <summary>
    /// A task's own part of its trials, which <see cref="TrialLoop{TTrial, TUpdate, TOutcome}"/> runs: the draw, each
    /// trial's timing, and the response rule, called with each input from the trial's start to its outcome. A method
    /// returns the outcome when the trial ends, else null; changes the task shows (TUpdate) go into updates, in order.
    /// Called under the loop's lock, one call at a time. A laser trial (TrialTiming.Laser) must be an IZapitTrial.
    /// </summary>
    public interface ITrialRules<TTrial, TUpdate, TOutcome>
        where TTrial : class, ITrial
        where TOutcome : class, ITrialOutcome
    {
        /// <summary>Gets the experiment's laser timing; null in an experiment without a laser.</summary>
        LaserTiming Laser { get; }

        /// <summary>The next trial, at the start of its interval; previous is the last trial's outcome, null before the first.</summary>
        TTrial Draw(TOutcome previous);

        /// <summary>What the trial asks of the loop.</summary>
        TrialTiming Timing(TTrial trial);

        /// <summary>The trial starts; wheelCm is the wheel's latest distance, NaN if none has come.</summary>
        void Start(TTrial trial, TrialTimes times, double wheelCm);

        /// <summary>The stimulus goes on (the Stimulus event is just before these updates).</summary>
        void Stimulus(TTrial trial, TrialTimes times, ICollection<TUpdate> updates);

        /// <summary>Each Harp clock tick from the stimulus to the outcome, the stimulus's own tick included.</summary>
        TOutcome Tick(TTrial trial, TrialTimes times, ICollection<TUpdate> updates);

        /// <summary>A spout touched (spout 0 left, 1 right) from the trial's start to its outcome; times.Responding says whether the window is open.</summary>
        TOutcome Lick(TTrial trial, int spout, TrialTimes times, ICollection<TUpdate> updates);

        /// <summary>The wheel's distance in cm, from the trial's start to its outcome.</summary>
        TOutcome Wheel(TTrial trial, double cm, TrialTimes times, ICollection<TUpdate> updates);

        /// <summary>The response window closed with no outcome: the outcome then, never null.</summary>
        TOutcome ResponseEnd(TTrial trial, TrialTimes times);
    }

    /// <summary>
    /// Rules whose stimulus goes on changing after the outcome while it is still on (the outcome decided; licks and the wheel
    /// no longer change it): the loop calls AfterOutcome on each Harp clock tick and each wheel input from the outcome until
    /// the trial's StimulusOff event or the next trial's start, whichever is first, and emits its updates as Update events
    /// of that trial. Rules without it show nothing new after the outcome.
    /// </summary>
    public interface IStimulusAfterOutcome<TTrial, TUpdate>
    {
        /// <summary>The trial's stimulus after its outcome; times are its own, Now the latest tick; wheelCm the wheel's latest distance (NaN for none).</summary>
        void AfterOutcome(TTrial trial, TrialTimes times, double wheelCm, ICollection<TUpdate> updates);
    }

    /// <summary>One thing a trial loop asks the workflow to do, at a Harp time.</summary>
    public sealed class TrialEvent<TTrial, TUpdate, TOutcome>
    {
        internal TrialEvent(TrialEventKind kind, double seconds, TTrial trial, string cue, TUpdate update, TOutcome outcome)
        {
            Kind = kind;
            Seconds = seconds;
            Trial = trial;
            Cue = cue;
            Update = update;
            Outcome = outcome;
        }

        /// <summary>Gets what the event is.</summary>
        public TrialEventKind Kind { get; private set; }

        /// <summary>Gets its Harp time: the tick it came on, or the exact moment for Interval and Start.</summary>
        public double Seconds { get; private set; }

        /// <summary>Gets the trial it belongs to.</summary>
        public TTrial Trial { get; private set; }

        /// <summary>Gets the cue's name (Cue).</summary>
        public string Cue { get; private set; }

        /// <summary>Gets the task's change (Update).</summary>
        public TUpdate Update { get; private set; }

        /// <summary>Gets the outcome (Outcome).</summary>
        public TOutcome Outcome { get; private set; }
    }
}
