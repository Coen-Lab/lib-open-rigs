using Bonsai;
using OpenRigs.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenRigs.Trials
{
    /// <summary>
    /// A task's trials on the Harp clock, the same for every task: each trial drawn at its interval's start (the last
    /// outcome plus its timeout; trial 1 the first tick), a hardware-triggered opto system (<see cref="IOptoSystem"/>) armed
    /// during the interval once the last laser is done, the trial start at the interval's end less the lead (TrialLoop.Lead: max(laserToStimulusSeconds,
    /// the earliest Stimulus cue's lead, 0)) or later
    /// until the quiet waits hold (QuietWait), the trigger laserToStimulusSeconds before the stimulus (the workflow stops
    /// if the system has not armed the trial by then, naming the last laser when it held the system too long, and a trial answered
    /// first gets no laser; after a laser the workflow ends it early, UntilOutcome or UntilStimulusOff, and on a system whose end needs
    /// a reply (IOptoSystem.EndNeedsReply) the next is armed only once it has answered that end and its EndReplyMarginSeconds more have passed), the stimulus the lead after the start, the response window, the outcome and its timeout
    /// (from the window's end when the window always runs in full, TrialTiming.ResponseWindowAlwaysFull; cues still to come that are DroppedAtOutcome never come), and with a timed stimulus (TrialTiming.StimulusDurationSeconds)
    /// its StimulusOff, once, at StimulusTiming.Time (rules that are an IStimulusAfterOutcome update it after the outcome until then; a
    /// LaserTiming.UntilStimulusOff laser ends with it, LaserOff). What is the task's own (the draw, the stimulus, the
    /// response rule, the outcome) is its <see cref="ITrialRules{TTrial, TUpdate, TOutcome}"/>. Every time is on the Harp
    /// clock: inputs between ticks are timed at the latest tick. Not thread-safe: <see cref="TrialLoop.Observe"/> runs it
    /// under one lock.
    /// </summary>
    public class TrialLoop<TTrial, TUpdate, TOutcome>
        where TTrial : class, ITrial
        where TOutcome : class, ITrialOutcome
    {
        enum Phase { Waiting, Iti, Running, Timeout }

        sealed class Timer
        {
            public double Due;
            public TrialEventKind Kind;
            public TTrial Trial;
            public string Cue;
            public bool DroppedAtOutcome;
        }

        readonly ITrialRules<TTrial, TUpdate, TOutcome> rules;
        readonly LaserTiming laser;
        readonly IOptoSystem opto;   // the system that arms the laser trials; null for none
        readonly bool hasWheel;
        readonly List<bool> touched = new List<bool>();
        readonly List<Timer> timers = new List<Timer>();   // by due time, then as scheduled
        Phase phase = Phase.Waiting;
        TTrial trial;
        TrialTiming timing;
        QuietWait wait;
        TOutcome lastOutcome;
        double now = double.NaN, wheel = double.NaN;
        double itiStart, nextItiStart, start, quietWait, stimulusTime, triggerDue, onset, responseStart, responseEnd, triggerTime, capAt, armAt, armSentAt;
        double laserFreeAt = double.NegativeInfinity;   // the soonest the next trial may be armed
        bool armSent, triggered, stimulated, capped;
        TTrial awaitingReply;   // the trial whose Arm the system has not yet answered (Armed)
        TTrial armed;           // the last trial the system armed
        double armedPowerMw;    // the power it armed it with (its Armed's PowerMw)
        double armedSeconds;    // its whole length with the ramp (its Armed's PlaySeconds)
        int laserStarted;       // the last trial the system reported Started: its samples were generated
        readonly IStimulusAfterOutcome<TTrial, TUpdate> after;   // the rules' stimulus after the outcome, if they have one
        TTrial onTrial;         // the trial whose timed stimulus is on (StimulusDurationSeconds), until its StimulusOff
        TrialTimes onTimes;     // its times from its outcome
        double offDue;          // its off time: onset + duration, from the outcome the earlier of that and outcome + responseToStimulusOffSeconds
        bool afterOutcome, hookOpen;   // its outcome has come; AfterOutcome still called (until its off or the next trial's start)
        TTrial lasing;          // the trial whose LaserTiming.UntilStimulusOff laser is on, until its LaserOff or LaserCap
        double lasingCapAt;     // its LaserCap's time
        TTrial stopOwed;        // on a system whose end needs a reply, the triggered trial whose laser plays until stopped (LaserTiming.UntilStopped), until the event that ends it
        TTrial stopAwaited;     // the trial whose end the system has not yet answered
        TTrial stopped;         // the last trial ended
        bool stopAnswered;      // the system has answered an end, counted from the next tick
        double stopFreeAt = double.NegativeInfinity;   // the soonest the next trial may be armed after the last end's reply

        /// <summary>A loop running <paramref name="rules"/>' trials; the first is drawn at the first clock tick. Without
        /// a wheel (<paramref name="hasWheel"/> false) the wheel's distance stays NaN and a trial that waits for the wheel
        /// to be quiet is refused. With no opto system, a laser trial is refused.</summary>
        public TrialLoop(ITrialRules<TTrial, TUpdate, TOutcome> rules, bool hasWheel) : this(rules, hasWheel, null)
        {
        }

        /// <summary>A loop as the one above, its laser trials armed by <paramref name="opto"/> (null for none), whose messages
        /// come in through <see cref="OnOpto"/>; a laser timing the system cannot run (IOptoSystem.TimingProblem) is refused.</summary>
        public TrialLoop(ITrialRules<TTrial, TUpdate, TOutcome> rules, bool hasWheel, IOptoSystem opto)
        {
            if (rules == null) throw new ArgumentNullException("rules");
            this.rules = rules;
            this.hasWheel = hasWheel;
            laser = rules.Laser;
            this.opto = opto;
            var problem = opto != null && laser != null ? opto.TimingProblem(laser) : null;
            if (problem != null) throw new ArgumentException(problem);
            after = rules as IStimulusAfterOutcome<TTrial, TUpdate>;
        }

        /// <summary>Gets the opto system that arms the laser trials; null for none.</summary>
        public IOptoSystem Opto { get { return opto; } }

        /// <summary>Gets the trial drawn last: in its interval, running, or in its timeout; null before the first tick.</summary>
        public TTrial Trial { get { return trial; } }

        /// <summary>Gets whether the loop waits for the opto system's reply to the end the workflow sent at its last laser's end (IOptoSystem.EndNeedsReply).</summary>
        public bool AwaitingStopReply { get { return stopAwaited != null && !stopAnswered; } }

        /// <summary>A tick of the Harp clock, in seconds.</summary>
        public List<TrialEvent<TTrial, TUpdate, TOutcome>> OnClock(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) throw new InvalidOperationException(string.Format("The Harp clock gave {0} s.", seconds));
            if (!double.IsNaN(now) && seconds < now) throw new InvalidOperationException(string.Format("The Harp clock went backwards, from {0} s to {1} s.", now, seconds));
            now = seconds;
            var events = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
            if (stopAwaited != null && stopAnswered)
            {
                stopAwaited = null;
                stopAnswered = false;
                stopFreeAt = now + opto.EndReplyMarginSeconds;
            }
            if (phase == Phase.Waiting)
            {
                nextItiStart = now;
                NextTrial(events);
            }
            else if (phase == Phase.Timeout && now >= nextItiStart - TrialLoop.Tolerance)
            {
                NextTrial(events);
            }
            while (timers.Count > 0 && now >= timers[0].Due - TrialLoop.Tolerance)
            {
                var due = timers[0];
                timers.RemoveAt(0);
                events.Add(Event(due.Kind, now, due.Trial, due.Cue));
            }
            if (onTrial != null && now >= offDue - TrialLoop.Tolerance) Off(events);
            else if (onTrial != null && afterOutcome) AfterOutcome(events);
            if (lasing != null && now >= lasingCapAt - TrialLoop.Tolerance)
            {
                events.Add(Event(TrialEventKind.LaserCap, now, lasing, null));
                LaserEnded(events, false);
            }
            if (trial != null && timing.Laser && laser.HardwareTriggered && !armSent && (phase == Phase.Iti || phase == Phase.Running)
                && now >= armAt - TrialLoop.Tolerance && stopAwaited == null && now >= stopFreeAt - TrialLoop.Tolerance)
            {
                if (awaitingReply != null) throw opto.NeverArmed(awaitingReply.TrialNumber);
                armSent = true;
                armSentAt = now;
                awaitingReply = trial;
                var arm = Event(TrialEventKind.Arm, now, trial, null);
                if (opto.ArmCarriesLaserSeconds && laser.UntilStopped) arm.LaserSeconds = laser.ArmedSecondsOf(trial, timing);
                events.Add(arm);
            }
            if (phase == Phase.Iti && now >= wait.TrialStartTime - TrialLoop.Tolerance) Begin(events);
            else if (phase == Phase.Running) Run(events);
            return events;
        }

        /// <summary>A spout's lick detector changed (the spout's index in the loop's lick inputs): touched or released.</summary>
        public List<TrialEvent<TTrial, TUpdate, TOutcome>> OnLick(int spout, bool isTouched)
        {
            if (spout < 0) throw new ArgumentOutOfRangeException("spout", spout, "A spout is numbered from 0.");
            var events = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
            while (touched.Count <= spout) touched.Add(false);
            if (touched[spout] == isTouched) return events;
            touched[spout] = isTouched;
            if (phase == Phase.Iti && !double.IsNaN(now)) wait.Lick(AnyTouched(), now);
            if (!isTouched || phase != Phase.Running) return events;
            var updates = new List<TUpdate>();
            var outcome = rules.Lick(trial, spout, Times(), updates);
            Emit(updates, outcome, events);
            return events;
        }

        /// <summary>The wheel's distance in cm.</summary>
        public List<TrialEvent<TTrial, TUpdate, TOutcome>> OnWheel(double cm)
        {
            if (double.IsNaN(cm) || double.IsInfinity(cm)) throw new InvalidOperationException(string.Format("The wheel's distance is {0} cm.", cm));
            var events = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
            wheel = cm;
            if (phase == Phase.Iti && !double.IsNaN(now)) wait.Wheel(cm, now);
            if (onTrial != null && afterOutcome) AfterOutcome(events);
            if (phase != Phase.Running) return events;
            var updates = new List<TUpdate>();
            var outcome = rules.Wheel(trial, cm, Times(), updates);
            Emit(updates, outcome, events);
            return events;
        }

        /// <summary>
        /// A message from the opto system, mapped from its own by its adapter: an Armed while a trial's Arm is unanswered must arm
        /// that trial as it asked (IOptoSystem.CheckArmed); a Started marks its samples generated, so only then, on a system that
        /// confirms starts (IOptoSystem.ConfirmsStart), does the trial log its power (a missed trigger ends with none generated, and
        /// logs 0 mW); an outcome within the system's StartedGraceSeconds of the trigger logs the power before Started can come, and
        /// the system's own event rows settle it. A Started for the armed trial before its trigger stops the workflow
        /// (IOptoSystem.StartedBeforeTrigger). An Ended while a laser played until stopped is on or its end unanswered (a system whose
        /// end needs a reply) frees the system its EndReplyMarginSeconds from the next tick. Any other message is not the loop's; with
        /// no opto system, none is.
        /// </summary>
        public void OnOpto(OptoFeedback feedback)
        {
            if (feedback == null || opto == null) return;
            if (feedback.Kind == OptoFeedbackKind.Ended)
            {
                if (stopAwaited != null || stopOwed != null) stopAnswered = true;
                return;
            }
            if (feedback.Kind == OptoFeedbackKind.Started)
            {
                if (armed != null && feedback.Trial == armed.TrialNumber && armed == trial && !triggered) throw opto.StartedBeforeTrigger(feedback.Trial);
                laserStarted = feedback.Trial;
                return;
            }
            if (feedback.Kind != OptoFeedbackKind.Armed || awaitingReply == null) return;
            opto.CheckArmed(feedback, (IOptoTrial)awaitingReply);   // Check: every laser trial is one
            armed = awaitingReply;
            armedPowerMw = feedback.On ? feedback.PowerMw : 0;
            armedSeconds = feedback.PlaySeconds;
            awaitingReply = null;
        }

        void NextTrial(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            var next = rules.Draw(lastOutcome);
            if (next == null) throw new InvalidOperationException("The task's rules drew no trial.");
            var nextTiming = rules.Timing(next);
            Check(next, nextTiming);
            trial = next;
            timing = nextTiming;
            phase = Phase.Iti;
            itiStart = nextItiStart;
            var lead = TrialLoop.Lead(timing);
            wait = new QuietWait(itiStart, itiStart + timing.ItiSeconds - lead, timing.LickQuietSeconds, timing.WheelQuietSeconds, timing.WheelQuietCm, wheel, AnyTouched());
            armAt = Math.Max(itiStart, laserFreeAt);
            armSent = triggered = stimulated = capped = false;
            start = quietWait = stimulusTime = triggerDue = onset = responseStart = responseEnd = triggerTime = double.NaN;
            capAt = double.PositiveInfinity;
            events.Add(Event(TrialEventKind.Iti, itiStart, trial, null));
        }

        void Check(TTrial next, TrialTiming t)
        {
            var problems = new List<string>();
            if (t == null) throw new InvalidOperationException(string.Format("The task's rules gave trial {0} no timing.", next.TrialNumber));
            if (!(t.ItiSeconds >= 0) || double.IsInfinity(t.ItiSeconds)) problems.Add(string.Format("its interval is {0} s, not a finite number from 0", t.ItiSeconds));
            if (double.IsNaN(t.LaserToStimulusSeconds) || double.IsInfinity(t.LaserToStimulusSeconds)) problems.Add(string.Format("its laserToStimulusSeconds is {0}", t.LaserToStimulusSeconds));
            else if (TrialLoop.Lead(t) > t.ItiSeconds + TrialLoop.Tolerance) problems.Add(string.Format("its lead ({0} s) is longer than its interval ({1} s), so it would start before its interval", TrialLoop.Lead(t), t.ItiSeconds));
            if (!(t.ResponseDelaySeconds >= 0) || double.IsInfinity(t.ResponseDelaySeconds)) problems.Add(string.Format("its response delay is {0} s, not a finite number from 0", t.ResponseDelaySeconds));
            if (!(t.ResponseWindowSeconds > 0)) problems.Add(string.Format("its response window is {0} s, not above 0 (infinity for no limit)", t.ResponseWindowSeconds));
            if (t.Laser && laser == null) problems.Add("it is a laser trial, but the rules give no LaserTiming");
            if (t.Laser && laser != null && laser.End == LaserEnd.AtStimulusOff && double.IsNaN(t.StimulusDurationSeconds) && !t.ResponseToStimulusOffSeconds.HasValue)
                problems.Add("its laser ends at the stimulus off (LaserTiming.UntilStimulusOff), but its stimulus has no off time (StimulusDurationSeconds NaN and ResponseToStimulusOffSeconds null)");
            if (t.Laser && opto == null) problems.Add("it is a laser trial, but the loop has no opto system (IOptoSystem)");
            if (t.Laser && !(next is IOptoTrial)) problems.Add(string.Format("it is a laser trial, but its type {0} is not an IOptoTrial (its laser condition, laser on and power)", next.GetType().Name));
            problems.AddRange(QuietWait.LickProblems(t.LickQuietSeconds));
            problems.AddRange(QuietWait.WheelProblems(t.WheelQuietSeconds, t.WheelQuietCm));
            if (!hasWheel && t.WheelQuietSeconds > 0) problems.Add(string.Format("it waits {0} s for the wheel to be quiet, but the loop has no wheel", t.WheelQuietSeconds));
            if (!double.IsNaN(t.StimulusDurationSeconds) && (!(t.StimulusDurationSeconds > 0) || double.IsInfinity(t.StimulusDurationSeconds))) problems.Add(string.Format("its stimulus duration is {0} s, not a finite number above 0 (NaN for none)", t.StimulusDurationSeconds));
            if (t.ResponseToStimulusOffSeconds.HasValue && (!(t.ResponseToStimulusOffSeconds.Value >= 0) || double.IsInfinity(t.ResponseToStimulusOffSeconds.Value))) problems.Add(string.Format("its responseToStimulusOffSeconds is {0} s, not null or a finite number from 0", t.ResponseToStimulusOffSeconds.Value));
            if (t.ResponseWindowAlwaysFull && double.IsInfinity(t.ResponseWindowSeconds)) problems.Add("its response window always runs in full, but has no limit");
            if (problems.Count > 0) throw new InvalidOperationException(string.Format("Trial {0} cannot be run: {1}.", next.TrialNumber, string.Join("; ", problems)));
        }

        void Begin(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            phase = Phase.Running;
            start = wait.TrialStartTime;
            quietWait = start - wait.Earliest;
            stimulusTime = start + TrialLoop.Lead(timing);
            triggerDue = stimulusTime - timing.LaserToStimulusSeconds;
            responseStart = stimulusTime + timing.ResponseDelaySeconds;
            responseEnd = responseStart + timing.ResponseWindowSeconds;
            rules.Start(trial, Times(), wheel);
            events.Add(Event(TrialEventKind.Start, start, trial, null));
            if (timing.Cues != null)
            {
                // A cue before the stimulus (negative seconds, within the lead): at the start's tick if due by then.
                foreach (var cue in timing.Cues)
                {
                    if (cue.Anchor != CueAnchor.Stimulus || cue.Seconds >= 0) continue;
                    if (now >= stimulusTime + cue.Seconds - TrialLoop.Tolerance) events.Add(Event(TrialEventKind.Cue, now, trial, cue.Name));
                    else Schedule(stimulusTime + cue.Seconds, TrialEventKind.Cue, trial, cue.Name, cue.DroppedAtOutcome);
                }
            }
            Run(events);
        }

        void Run(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            if (timing.Laser && !triggered && now >= triggerDue - TrialLoop.Tolerance) Trigger(events);
            var updates = new List<TUpdate>();
            if (!stimulated && now >= stimulusTime - TrialLoop.Tolerance)
            {
                stimulated = true;
                onset = now;
                if (onTrial != null) throw new InvalidOperationException(string.Format("Trial {0}'s stimulus came at {1} s while trial {2}'s was still on until {3} s: the task's stimulus block must end it before the soonest next stimulus (StimulusTiming.Problems).", trial.TrialNumber, now, onTrial.TrialNumber, offDue));
                if (!double.IsNaN(timing.StimulusDurationSeconds) || timing.ResponseToStimulusOffSeconds.HasValue)
                {
                    onTrial = trial;
                    offDue = double.IsNaN(timing.StimulusDurationSeconds) ? double.PositiveInfinity : onset + timing.StimulusDurationSeconds;
                    afterOutcome = false;
                    hookOpen = true;
                }
                events.Add(Event(TrialEventKind.Stimulus, now, trial, null));
                if (timing.Cues != null)
                {
                    foreach (var cue in timing.Cues)
                    {
                        if (cue.Anchor == CueAnchor.Stimulus && cue.Seconds >= 0) Schedule(stimulusTime + cue.Seconds, TrialEventKind.Cue, trial, cue.Name, cue.DroppedAtOutcome);
                    }
                }
                rules.Stimulus(trial, Times(), updates);
            }
            if (stimulated)
            {
                var outcome = rules.Tick(trial, Times(), updates);
                Emit(updates, outcome, events);
                if (outcome != null) return;
            }
            if (now >= responseEnd - TrialLoop.Tolerance)
            {
                var outcome = rules.ResponseEnd(trial, Times());
                if (outcome == null) throw new InvalidOperationException(string.Format("The task's rules gave trial {0} no outcome when its response window closed.", trial.TrialNumber));
                End(outcome, events);
                return;
            }
            if (triggered && !capped && now >= capAt - TrialLoop.Tolerance)
            {
                capped = true;
                events.Add(Event(TrialEventKind.LaserCap, now, trial, null));
                StopSent();
            }
        }

        void Trigger(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            if (laser.HardwareTriggered)
            {
                if (!armSent) throw NotFree(trial.TrialNumber);
                if (armed != trial && now - armSentAt < opto.ArmLeadSeconds - TrialLoop.Tolerance) throw opto.ArmedLate(trial.TrialNumber, now, armSentAt);
                if (armed != trial) throw opto.NotArmedByTrigger(trial.TrialNumber, now);
            }
            else
            {
                if (now < laserFreeAt - TrialLoop.Tolerance || stopAwaited != null || now < stopFreeAt - TrialLoop.Tolerance) throw NotFree(trial.TrialNumber);
                if (awaitingReply != null) throw opto.NeverArmed(awaitingReply.TrialNumber);
                awaitingReply = trial;
            }
            triggered = true;
            triggerTime = now;
            // A system whose end needs a reply frees the next trial with that reply; another, by laserFreeAt alone.
            if (laser.UntilStopped && opto.EndNeedsReply) stopOwed = trial;
            events.Add(Event(TrialEventKind.Trigger, now, trial, null));
            if (laser.End == LaserEnd.AfterDuration)
            {
                var duration = LaserTiming.DurationOf(trial);
                Schedule(now + duration, TrialEventKind.TriggerOff, trial, null);
                laserFreeAt = opto.FreeAt(now, duration, laser.RampDownSeconds, armedSeconds);
            }
            else if (laser.End == LaserEnd.AtStimulusOff)
            {
                lasing = trial;
                lasingCapAt = now + laser.CapOf(trial);
                laserFreeAt = lasingCapAt + laser.TriggerHoldSeconds;   // the latest; LaserEnded brings it forward
                if (stimulated && onTrial != trial) LaserEnded(events, true);   // its stimulus already off
            }
            else
            {
                capAt = now + laser.LaserCapSeconds;
            }
        }

        void Emit(List<TUpdate> updates, TOutcome outcome, List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            foreach (var update in updates) events.Add(new TrialEvent<TTrial, TUpdate, TOutcome>(TrialEventKind.Update, now, trial, null, update, null));
            if (outcome != null) End(outcome, events);
        }

        void End(TOutcome outcome, List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            var timeout = outcome.PenaltyTimeoutSeconds;
            if (!(timeout >= 0) || double.IsInfinity(timeout)) throw new InvalidOperationException(string.Format("Trial {0}'s outcome has a timeout of {1} s, not a finite number from 0.", trial.TrialNumber, timeout));
            phase = Phase.Timeout;
            lastOutcome = outcome;
            if (timing.Laser)
            {
                if (laser.End == LaserEnd.AtOutcome || (laser.End == LaserEnd.AtStimulusOff && !triggered))
                {
                    Schedule(now + laser.TriggerHoldSeconds, TrialEventKind.TriggerOff, trial, null);
                    laserFreeAt = now + laser.TriggerHoldSeconds;
                    if (laser.End == LaserEnd.AtOutcome) StopSent();
                }
                else if (!triggered)
                {
                    laserFreeAt = Math.Max(laserFreeAt, triggerDue);   // never triggered: the system is free once its trigger would have come
                }
            }
            nextItiStart = timing.ResponseWindowAlwaysFull ? Times().TrialEndTime + timeout : now + timeout;
            timers.RemoveAll(t => t.Kind == TrialEventKind.Cue && t.Trial == trial && t.DroppedAtOutcome);
            events.Add(new TrialEvent<TTrial, TUpdate, TOutcome>(TrialEventKind.Outcome, now, trial, null, default(TUpdate), outcome));
            if (onTrial == trial)
            {
                onTimes = Times();
                offDue = onTimes.StimulusOffTime;
                afterOutcome = true;
                if (now >= offDue - TrialLoop.Tolerance) Off(events);
            }
            if (lasing == trial && onTrial != trial) LaserEnded(events, true);   // its stimulus never came on: the laser ends with the trial
            if (timing.Cues != null)
            {
                foreach (var cue in timing.Cues)
                {
                    if (cue.Anchor == CueAnchor.NextIti) Schedule(nextItiStart + cue.Seconds, TrialEventKind.Cue, trial, cue.Name);
                }
            }
            if (timing.ResponseWindowAlwaysFull ? now >= nextItiStart - TrialLoop.Tolerance : timeout <= 0) NextTrial(events);
        }

        void Off(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            events.Add(Event(TrialEventKind.StimulusOff, now, onTrial, null));
            var off = onTrial;
            onTrial = null;
            onTimes = null;
            if (lasing != null && lasing == off) LaserEnded(events, true);
        }

        // The UntilStimulusOff laser has ended (with LaserOff unless its LaserCap said so): the line falls TriggerHoldSeconds later,
        // the system free then, and a trial waiting in its interval to be armed is armed from then.
        void LaserEnded(List<TrialEvent<TTrial, TUpdate, TOutcome>> events, bool laserOff)
        {
            if (laserOff) events.Add(Event(TrialEventKind.LaserOff, now, lasing, null));
            StopSent();
            Schedule(now + laser.TriggerHoldSeconds, TrialEventKind.TriggerOff, lasing, null);
            laserFreeAt = now + laser.TriggerHoldSeconds;
            if (phase == Phase.Iti && trial != lasing && !armSent) armAt = Math.Max(itiStart, laserFreeAt);
            lasing = null;
        }

        // The event at which the workflow ends the laser has come (once per triggered trial whose laser plays until stopped, on a
        // system whose end needs a reply): the next trial waits for that reply, unless it is already in (the workflow's wall-clock backstop stopped it first).
        void StopSent()
        {
            if (stopOwed == null) return;
            stopped = stopOwed;
            stopOwed = null;
            if (stopAnswered)
            {
                stopAnswered = false;
                stopFreeAt = now + opto.EndReplyMarginSeconds;
            }
            else stopAwaited = stopped;
        }

        // The ended trial's stimulus while on, until the next trial starts.
        void AfterOutcome(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            if (after == null || !hookOpen) return;
            if (phase == Phase.Running || (phase == Phase.Iti && now >= wait.TrialStartTime - TrialLoop.Tolerance))
            {
                hookOpen = false;
                return;
            }
            var updates = new List<TUpdate>();
            onTimes.Now = now;
            after.AfterOutcome(onTrial, onTimes, wheel, updates);
            foreach (var update in updates) events.Add(new TrialEvent<TTrial, TUpdate, TOutcome>(TrialEventKind.Update, now, onTrial, null, update, null));
        }

        void Schedule(double due, TrialEventKind kind, TTrial owner, string cue, bool droppedAtOutcome = false)
        {
            var index = timers.FindLastIndex(t => t.Due <= due) + 1;
            timers.Insert(index, new Timer { Due = due, Kind = kind, Trial = owner, Cue = cue, DroppedAtOutcome = droppedAtOutcome });
        }

        TrialTimes Times()
        {
            return new TrialTimes
            {
                ItiStartTime = itiStart,
                ItiSeconds = timing.ItiSeconds,
                QuietWaitSeconds = quietWait,
                TrialStartTime = start,
                StimulusTime = stimulusTime,
                StimulusOnsetTime = onset,
                ResponseStartTime = responseStart,
                ResponseEndTime = responseEnd,
                LaserTriggerTime = triggerTime,
                LaserPowerMw = opto == null || !opto.ConfirmsStart ? double.NaN : triggered && armed == trial && (laserStarted == trial.TrialNumber || now - triggerTime < opto.StartedGraceSeconds) ? armedPowerMw : 0,
                LaserStarted = opto != null && opto.ConfirmsStart && triggered && armed == trial && laserStarted == trial.TrialNumber,
                StimulusOffTime = StimulusTiming.Time(onset, timing.StimulusDurationSeconds, now, timing.ResponseToStimulusOffSeconds),
                Now = now,
                ResponseWindowAlwaysFull = timing.ResponseWindowAlwaysFull,
            };
        }

        bool AnyTouched()
        {
            return touched.Contains(true);
        }

        TrialEvent<TTrial, TUpdate, TOutcome> Event(TrialEventKind kind, double seconds, TTrial owner, string cue)
        {
            return new TrialEvent<TTrial, TUpdate, TOutcome>(kind, seconds, owner, cue, default(TUpdate), null);
        }

        // The system not free at a trial's trigger: the task's laser timing (BusyAtTrigger), or the system's reply to the last end
        // not in, or in too late for the margin after it.
        Exception NotFree(int trialNumber)
        {
            if (now < laserFreeAt - TrialLoop.Tolerance) return opto.BusyAtTrigger(trialNumber, now, laserFreeAt);
            if (stopAwaited != null) return opto.EndNotAnswered(stopAwaited.TrialNumber, trialNumber, now);
            if (now < stopFreeAt - TrialLoop.Tolerance) return opto.EndAnsweredLate(stopped.TrialNumber, trialNumber, stopFreeAt, now);
            return opto.BusyAtTrigger(trialNumber, now, laserFreeAt);
        }
    }

    /// <summary>Runs a <see cref="TrialLoop{TTrial, TUpdate, TOutcome}"/> on a workflow's streams.</summary>
    public static class TrialLoop
    {
        /// <summary>A trial's lead, the trial start to the stimulus: the larger of its laserToStimulusSeconds, the most any of its
        /// Stimulus cues comes before the stimulus (a negative Seconds, such as a negative spouts.stimulusToSpoutsInSeconds on a trial whose spouts move, Spouts.Cues) and 0.</summary>
        public static double Lead(TrialTiming timing)
        {
            var lead = Math.Max(timing.LaserToStimulusSeconds, 0);
            if (timing.Cues != null)
            {
                foreach (var cue in timing.Cues) if (cue.Anchor == CueAnchor.Stimulus) lead = Math.Max(lead, -cue.Seconds);
            }
            return lead;
        }

        /// <summary>How close to a due time, in seconds, a tick counts as reaching it: sums of seconds are not exact doubles.</summary>
        public const double Tolerance = 1e-6;

        /// <summary>
        /// The trials of the first rules to come, as events: the loop is fed the Harp clock (seconds), each spout's lick detector's
        /// state (<paramref name="licks"/>, spout 0 the first: a two-spout rig's left, then its right), the opto system's messages
        /// (<paramref name="optoFeedback"/>, mapped from the system's own by its adapter) and, on a rig with one, the wheel (cm; null
        /// for none), under one lock; the events leave in order, outside it, one thread at a time (sending under the lock deadlocks a
        /// clock that ticks under its own). The first trial's interval starts at the first tick after the rules come. The clock ending
        /// ends the sequence; a lick detector or the wheel ending stops it naming the input, as its device has stopped sending; the opto
        /// messages ending is left to the laser trial that waits for one. An error from the loop, the rules or an input stops the sequence.
        /// </summary>
        public static IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Observe<TTrial, TUpdate, TOutcome>(
            IObservable<ITrialRules<TTrial, TUpdate, TOutcome>> rules, IObservable<double> clock, IObservable<bool>[] licks,
            IOptoSystem opto, IObservable<OptoFeedback> optoFeedback, IObservable<double> wheelCm = null)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            if (opto == null) throw new ArgumentNullException("opto", "The trial loop's opto system: a task with no opto takes the overload without one.");
            if (optoFeedback == null) throw new ArgumentNullException("optoFeedback");
            return Run(rules, clock, licks, wheelCm, opto, optoFeedback);
        }

        /// <summary>The trials as the overload above, with no opto system: a laser trial is refused.</summary>
        public static IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Observe<TTrial, TUpdate, TOutcome>(
            IObservable<ITrialRules<TTrial, TUpdate, TOutcome>> rules, IObservable<double> clock, IObservable<bool>[] licks, IObservable<double> wheelCm = null)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            return Run(rules, clock, licks, wheelCm, null, null);
        }

        static IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Run<TTrial, TUpdate, TOutcome>(
            IObservable<ITrialRules<TTrial, TUpdate, TOutcome>> rules, IObservable<double> clock, IObservable<bool>[] licks, IObservable<double> wheelCm,
            IOptoSystem opto, IObservable<OptoFeedback> optoFeedback)
            where TTrial : class, ITrial
            where TOutcome : class, ITrialOutcome
        {
            if (licks == null || licks.Length == 0 || Array.IndexOf(licks, null) >= 0) throw new ArgumentException("The trial loop needs a lick input for each spout, none null.", "licks");
            return rules.Take(1).SelectMany(r => Observable.Create<TrialEvent<TTrial, TUpdate, TOutcome>>(observer =>
            {
                var loop = new TrialLoop<TTrial, TUpdate, TOutcome>(r, wheelCm != null, opto);
                var gate = new object();
                var outbox = new Queue<Action>();
                bool sending = false, stopped = false;
                Action<Func<List<TrialEvent<TTrial, TUpdate, TOutcome>>>> run = step =>
                {
                    lock (gate)
                    {
                        if (stopped) return;
                        try
                        {
                            var events = step();
                            if (events == null)
                            {
                                stopped = true;
                                outbox.Enqueue(observer.OnCompleted);
                            }
                            else foreach (var e in events)
                            {
                                var value = e;
                                outbox.Enqueue(() => observer.OnNext(value));
                            }
                        }
                        catch (Exception error)
                        {
                            stopped = true;
                            outbox.Enqueue(() => observer.OnError(error));
                        }
                        if (sending || outbox.Count == 0) return;
                        sending = true;
                    }
                    while (true)
                    {
                        Action next;
                        lock (gate)
                        {
                            if (outbox.Count == 0)
                            {
                                sending = false;
                                return;
                            }
                            next = outbox.Dequeue();
                        }
                        try
                        {
                            next();
                        }
                        catch
                        {
                            lock (gate) sending = false;
                            throw;
                        }
                    }
                };
                Action<Exception> fail = error => run(() => { throw error; });
                Func<string, Action> ended = input => () => fail(new InvalidOperationException(string.Format(
                    "The trial loop's {0} ended: its device stopped sending, so the trials cannot go on.", input)));
                var none = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
                var subscriptions = new CompositeDisposable(Disposable.Create(() => { lock (gate) { stopped = true; outbox.Clear(); } }));
                if (wheelCm != null) subscriptions.Add(wheelCm.Subscribe(cm => run(() => loop.OnWheel(cm)), fail, ended("wheel")));
                for (var i = 0; i < licks.Length; i++)
                {
                    var spout = i;
                    subscriptions.Add(licks[i].Subscribe(lick => run(() => loop.OnLick(spout, lick)), fail, ended("lick input " + spout)));
                }
                if (optoFeedback != null) subscriptions.Add(optoFeedback.Subscribe(feedback => run(() => { loop.OnOpto(feedback); return none; }), fail));
                subscriptions.Add(clock.Subscribe(seconds => run(() => loop.OnClock(seconds)), fail, () => run(() => null)));
                return subscriptions;
            }));
        }
    }

    /// <summary>The trial loop's events of one kind, as a task's TaskLogic picks them from TrialEvents.</summary>
    [Combinator]
    [Description("The trial loop's events of one Kind (TrialEvents). With Kind Cue, Cue picks one cue by name; empty passes every cue.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class PickTrialEvents
    {
        /// <summary>Gets or sets the kind of event to pass.</summary>
        [Description("The kind of event to pass.")]
        public TrialEventKind Kind { get; set; }

        /// <summary>Gets or sets the cue to pass, with Kind Cue; empty for every cue.</summary>
        [Description("With Kind Cue, the cue's name to pass; empty for every cue.")]
        public string Cue { get; set; }

        /// <summary>The events of the kind (and cue); a cue named with another Kind stops the workflow.</summary>
        public IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Process<TTrial, TUpdate, TOutcome>(IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> source)
        {
            return Observable.Defer(() =>
            {
                var kind = Kind;
                var cue = Cue;
                if (!string.IsNullOrEmpty(cue) && kind != TrialEventKind.Cue)
                {
                    throw new InvalidOperationException(string.Format("PickTrialEvents names cue {0}, but its Kind is {1}, not Cue.", cue, kind));
                }
                return source.Where(e => e.Kind == kind && (string.IsNullOrEmpty(cue) || e.Cue == cue));
            });
        }
    }
}
