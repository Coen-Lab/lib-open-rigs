using Bonsai;
using OpenRigs.Core;
using OpenRigs.Devices.Zapit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>
    /// A task's trials on the Harp clock, the same for every task: each trial drawn at its interval's start (the last
    /// outcome plus its timeout; trial 1 the first tick), a hardware-triggered Zapit armed during the interval once the
    /// last laser is done, the trial start at the interval's end less the lead (max(laserToStimulusSeconds, 0)) or later
    /// until the quiet waits hold (QuietWait), the trigger laserToStimulusSeconds before the stimulus (the workflow stops
    /// if Zapit has not armed the trial by then, and a trial answered first gets no laser), the stimulus the lead after the
    /// start, the response window, the outcome and its timeout. What is the task's own (the draw, the stimulus, the
    /// response rule, the outcome) is its <see cref="ITrialRules{TTrial, TUpdate, TOutcome}"/>. Every time is on the Harp
    /// clock: inputs between ticks are timed at the latest tick. Not thread-safe: <see cref="TrialLoop.Observe"/> runs it
    /// under one lock.
    /// </summary>
    public class TrialLoop<TTrial, TUpdate, TOutcome>
        where TTrial : class, IZapitTrial
        where TOutcome : class, ITrialOutcome
    {
        enum Phase { Waiting, Interval, Running, Timeout }

        sealed class Timer
        {
            public double Due;
            public TrialEventKind Kind;
            public TTrial Trial;
            public string Cue;
        }

        readonly ITrialRules<TTrial, TUpdate, TOutcome> rules;
        readonly LaserTiming laser;
        readonly List<bool> touched = new List<bool>();
        readonly List<Timer> timers = new List<Timer>();   // by due time, then as scheduled
        Phase phase = Phase.Waiting;
        TTrial trial;
        TrialTiming timing;
        QuietWait wait;
        TOutcome lastOutcome;
        double now = double.NaN, wheel = double.NaN;
        double intervalStart, nextIntervalStart, start, quietWait, stimulusTime, triggerDue, onset, responseStart, responseEnd, triggerTime, capAt, armAt;
        double laserFreeAt = double.NegativeInfinity;   // the soonest the next trial may be armed
        bool armSent, triggered, stimulated, capped;
        TTrial awaitingReply;   // the trial whose StartStimulus Zapit has not yet answered
        TTrial armed;           // the last trial Zapit armed

        /// <summary>A loop running <paramref name="rules"/>' trials; the first is drawn at the first clock tick.</summary>
        public TrialLoop(ITrialRules<TTrial, TUpdate, TOutcome> rules)
        {
            if (rules == null) throw new ArgumentNullException("rules");
            this.rules = rules;
            laser = rules.Laser;
        }

        /// <summary>Gets the trial drawn last: in its interval, running, or in its timeout; null before the first tick.</summary>
        public TTrial Trial { get { return trial; } }

        /// <summary>A tick of the Harp clock, in seconds.</summary>
        public List<TrialEvent<TTrial, TUpdate, TOutcome>> OnClock(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) throw new InvalidOperationException(string.Format("The Harp clock gave {0} s.", seconds));
            if (!double.IsNaN(now) && seconds < now) throw new InvalidOperationException(string.Format("The Harp clock went backwards, from {0} s to {1} s.", now, seconds));
            now = seconds;
            var events = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
            if (phase == Phase.Waiting)
            {
                nextIntervalStart = now;
                NextTrial(events);
            }
            else if (phase == Phase.Timeout && now >= nextIntervalStart - TrialLoop.Tolerance)
            {
                NextTrial(events);
            }
            while (timers.Count > 0 && now >= timers[0].Due - TrialLoop.Tolerance)
            {
                var due = timers[0];
                timers.RemoveAt(0);
                events.Add(Event(due.Kind, now, due.Trial, due.Cue));
            }
            if (trial != null && timing.Laser && laser.HardwareTriggered && !armSent && (phase == Phase.Interval || phase == Phase.Running)
                && now >= armAt - TrialLoop.Tolerance)
            {
                if (awaitingReply != null) throw NeverAnswered(awaitingReply);
                armSent = true;
                awaitingReply = trial;
                events.Add(Event(TrialEventKind.Arm, now, trial, null));
            }
            if (phase == Phase.Interval && now >= wait.TrialStartTime - TrialLoop.Tolerance) Begin(events);
            else if (phase == Phase.Running) Run(events);
            return events;
        }

        /// <summary>A spout's lick detector changed (spout 0 left, 1 right): touched or released.</summary>
        public List<TrialEvent<TTrial, TUpdate, TOutcome>> OnLick(int spout, bool isTouched)
        {
            if (spout < 0) throw new ArgumentOutOfRangeException("spout", spout, "A spout is numbered from 0.");
            var events = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
            while (touched.Count <= spout) touched.Add(false);
            if (touched[spout] == isTouched) return events;
            touched[spout] = isTouched;
            if (phase == Phase.Interval && !double.IsNaN(now)) wait.Lick(AnyTouched(), now);
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
            if (phase == Phase.Interval && !double.IsNaN(now)) wait.Wheel(cm, now);
            if (phase != Phase.Running) return events;
            var updates = new List<TUpdate>();
            var outcome = rules.Wheel(trial, cm, Times(), updates);
            Emit(updates, outcome, events);
            return events;
        }

        /// <summary>
        /// A reply from Zapit: one to StartStimulus while a trial's is unanswered must arm that trial as it asked
        /// (ZapitTrial.CheckArmed); any other reply is not the loop's.
        /// </summary>
        public void OnZapitReply(ZapitReply reply)
        {
            if (reply == null || reply.Command != ZapitCommandType.StartStimulus || awaitingReply == null) return;
            ZapitTrial.CheckArmed(reply, awaitingReply.TrialNumber, awaitingReply.LaserCondition, awaitingReply.LaserOn);
            armed = awaitingReply;
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
            phase = Phase.Interval;
            intervalStart = nextIntervalStart;
            var lead = Math.Max(timing.LaserToStimulusSeconds, 0);
            wait = new QuietWait(intervalStart, intervalStart + timing.IntervalSeconds - lead, timing.LickQuietSeconds, timing.WheelQuietSeconds, timing.WheelQuietCm, wheel, AnyTouched());
            armAt = Math.Max(intervalStart, laserFreeAt);
            armSent = triggered = stimulated = capped = false;
            start = quietWait = stimulusTime = triggerDue = onset = responseStart = responseEnd = triggerTime = double.NaN;
            capAt = double.PositiveInfinity;
            events.Add(Event(TrialEventKind.Interval, intervalStart, trial, null));
        }

        void Check(TTrial next, TrialTiming t)
        {
            var problems = new List<string>();
            if (t == null) throw new InvalidOperationException(string.Format("The task's rules gave trial {0} no timing.", next.TrialNumber));
            if (!(t.IntervalSeconds >= 0) || double.IsInfinity(t.IntervalSeconds)) problems.Add(string.Format("its interval is {0} s, not a finite number from 0", t.IntervalSeconds));
            if (double.IsNaN(t.LaserToStimulusSeconds) || double.IsInfinity(t.LaserToStimulusSeconds)) problems.Add(string.Format("its laserToStimulusSeconds is {0}", t.LaserToStimulusSeconds));
            else if (t.LaserToStimulusSeconds > t.IntervalSeconds + TrialLoop.Tolerance) problems.Add(string.Format("its lead ({0} s) is longer than its interval ({1} s), so it would start before its interval", t.LaserToStimulusSeconds, t.IntervalSeconds));
            if (!(t.ResponseDelaySeconds >= 0) || double.IsInfinity(t.ResponseDelaySeconds)) problems.Add(string.Format("its response delay is {0} s, not a finite number from 0", t.ResponseDelaySeconds));
            if (!(t.ResponseSeconds > 0)) problems.Add(string.Format("its response window is {0} s, not above 0 (infinity for no limit)", t.ResponseSeconds));
            if (t.Laser && laser == null) problems.Add("it is a laser trial, but the rules give no LaserTiming");
            problems.AddRange(QuietWait.SettingsProblems(t.LickQuietSeconds, t.WheelQuietSeconds, t.WheelQuietCm));
            if (problems.Count > 0) throw new InvalidOperationException(string.Format("Trial {0} cannot be run: {1}.", next.TrialNumber, string.Join("; ", problems)));
        }

        void Begin(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            phase = Phase.Running;
            start = wait.TrialStartTime;
            quietWait = start - wait.Earliest;
            stimulusTime = start + Math.Max(timing.LaserToStimulusSeconds, 0);
            triggerDue = start + Math.Max(-timing.LaserToStimulusSeconds, 0);
            responseStart = stimulusTime + timing.ResponseDelaySeconds;
            responseEnd = responseStart + timing.ResponseSeconds;
            rules.Start(trial, Times(), wheel);
            events.Add(Event(TrialEventKind.Start, start, trial, null));
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
                events.Add(Event(TrialEventKind.Stimulus, now, trial, null));
                if (timing.Cues != null)
                {
                    foreach (var cue in timing.Cues)
                    {
                        if (cue.Anchor == CueAnchor.Stimulus) Schedule(stimulusTime + cue.Seconds, TrialEventKind.Cue, trial, cue.Name);
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
            }
        }

        void Trigger(List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            if (laser.HardwareTriggered)
            {
                if (armed != trial) throw ZapitTrial.NotArmedByTrigger(trial.TrialNumber, now);
            }
            else
            {
                if (awaitingReply != null) throw NeverAnswered(awaitingReply);
                awaitingReply = trial;
            }
            triggered = true;
            triggerTime = now;
            events.Add(Event(TrialEventKind.Trigger, now, trial, null));
            if (laser.End == LaserEnd.AfterDuration)
            {
                Schedule(now + laser.LaserDurationSeconds, TrialEventKind.TriggerOff, trial, null);
                laserFreeAt = now + laser.LaserDurationSeconds + laser.RampDownSeconds;
            }
            else
            {
                capAt = now + laser.LaserMaxSeconds;
            }
        }

        void Emit(List<TUpdate> updates, TOutcome outcome, List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            foreach (var update in updates) events.Add(new TrialEvent<TTrial, TUpdate, TOutcome>(TrialEventKind.Update, now, trial, null, update, null));
            if (outcome != null) End(outcome, events);
        }

        void End(TOutcome outcome, List<TrialEvent<TTrial, TUpdate, TOutcome>> events)
        {
            var timeout = outcome.TimeoutSeconds;
            if (!(timeout >= 0) || double.IsInfinity(timeout)) throw new InvalidOperationException(string.Format("Trial {0}'s outcome has a timeout of {1} s, not a finite number from 0.", trial.TrialNumber, timeout));
            phase = Phase.Timeout;
            lastOutcome = outcome;
            if (timing.Laser)
            {
                if (laser.End == LaserEnd.AtOutcome)
                {
                    Schedule(now + laser.TriggerHoldSeconds, TrialEventKind.TriggerOff, trial, null);
                    laserFreeAt = now + laser.TriggerHoldSeconds;
                }
                else if (!triggered)
                {
                    laserFreeAt = Math.Max(laserFreeAt, triggerDue);   // never triggered: Zapit is free once its trigger would have come
                }
            }
            nextIntervalStart = now + timeout;
            events.Add(new TrialEvent<TTrial, TUpdate, TOutcome>(TrialEventKind.Outcome, now, trial, null, default(TUpdate), outcome));
            if (timing.Cues != null)
            {
                foreach (var cue in timing.Cues)
                {
                    if (cue.Anchor == CueAnchor.NextInterval) Schedule(nextIntervalStart + cue.Seconds, TrialEventKind.Cue, trial, cue.Name);
                }
            }
            if (timeout <= 0) NextTrial(events);
        }

        void Schedule(double due, TrialEventKind kind, TTrial owner, string cue)
        {
            var index = timers.FindLastIndex(t => t.Due <= due) + 1;
            timers.Insert(index, new Timer { Due = due, Kind = kind, Trial = owner, Cue = cue });
        }

        TrialTimes Times()
        {
            return new TrialTimes
            {
                IntervalStartTime = intervalStart,
                IntervalSeconds = timing.IntervalSeconds,
                QuietWaitSeconds = quietWait,
                TrialStartTime = start,
                StimulusTime = stimulusTime,
                StimulusOnsetTime = onset,
                ResponseStartTime = responseStart,
                ResponseEndTime = responseEnd,
                LaserTriggerTime = triggerTime,
                Now = now,
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

        static InvalidOperationException NeverAnswered(TTrial unanswered)
        {
            return new InvalidOperationException(string.Format(
                "Zapit never answered trial {0}'s StartStimulus, so the workflow stopped before the next one was sent. Is Zapit running and answering?",
                unanswered.TrialNumber));
        }
    }

    /// <summary>Runs a <see cref="TrialLoop{TTrial, TUpdate, TOutcome}"/> on a workflow's streams.</summary>
    public static class TrialLoop
    {
        /// <summary>How close to a due time, in seconds, a tick counts as reaching it: sums of seconds are not exact doubles.</summary>
        public const double Tolerance = 1e-9;

        /// <summary>
        /// The trials of the first rules to come, as events: the loop is fed the Harp clock (seconds), the wheel (cm),
        /// the left (spout 0) and right (spout 1) lick detectors' states and Zapit's replies, under one lock; the events
        /// leave in order, outside it, one thread at a time (sending under the lock deadlocks a clock that ticks under its
        /// own). The first trial's interval starts at the first tick after the rules come. An error from the loop or the
        /// rules stops the sequence with it.
        /// </summary>
        public static IObservable<TrialEvent<TTrial, TUpdate, TOutcome>> Observe<TTrial, TUpdate, TOutcome>(
            IObservable<ITrialRules<TTrial, TUpdate, TOutcome>> rules, IObservable<double> clock, IObservable<double> wheelCm,
            IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitReply> zapitReplies)
            where TTrial : class, IZapitTrial
            where TOutcome : class, ITrialOutcome
        {
            return rules.Take(1).SelectMany(r => Observable.Create<TrialEvent<TTrial, TUpdate, TOutcome>>(observer =>
            {
                var loop = new TrialLoop<TTrial, TUpdate, TOutcome>(r);
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
                            foreach (var e in step())
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
                var none = new List<TrialEvent<TTrial, TUpdate, TOutcome>>();
                return new CompositeDisposable(
                    Disposable.Create(() => { lock (gate) { stopped = true; outbox.Clear(); } }),
                    wheelCm.Subscribe(cm => run(() => loop.OnWheel(cm)), fail),
                    lickLeft.Subscribe(lick => run(() => loop.OnLick(0, lick)), fail),
                    lickRight.Subscribe(lick => run(() => loop.OnLick(1, lick)), fail),
                    zapitReplies.Subscribe(reply => run(() => { loop.OnZapitReply(reply); return none; }), fail),
                    clock.Subscribe(seconds => run(() => loop.OnClock(seconds)), fail));
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
