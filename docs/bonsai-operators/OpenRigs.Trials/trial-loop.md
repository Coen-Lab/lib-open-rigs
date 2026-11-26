# Trial Loop

`TrialLoop` runs a task's trials on the Harp clock: one state machine for what every task shares (the interval, the quiet waits, the trial start, the laser's arming, lead and trigger, the stimulus, the response window, the outcome and its timeout), with the task's own part (the draw, the stimulus, the response rule, the outcome) given as an `ITrialRules`. It is tested C#, driven in its tests tick by tick, so two tasks never time a trial two ways. Its types are the OpenRigs.Trials package's (namespace `OpenRigs.Trials`), which depends on OpenRigs.Devices for Zapit.

---

## A trial

| Phase | When | Event |
|---|---|---|
| Iti | the last outcome plus its timeout (penalised; a reward adds nothing), exactly; with `TrialTiming.ResponseWindowAlwaysFull`, the last window's end plus the timeout, rewarded or not (`TrialTimes.TrialEndTime`); trial 1 the first tick | `Iti` (the trial just drawn) |
| Arm | a laser trial on a hardware-triggered rig: the interval's start, or once the last laser is done (`LaserTiming`) and, after a StopStimulus, Zapit has answered it and `TrialLoop.StopReplyMarginSeconds` have passed | `Arm`: send the trial's StartStimulus |
| Start | the interval's end less the lead, `max(laserToStimulusSeconds, 0)`, or later until the quiet waits hold (`QuietWait`) | `Start` (exact time) |
| Trigger | `laserToStimulusSeconds` before the stimulus, if no outcome yet; the workflow stops if Zapit has not armed the trial, naming the last laser when it held Zapit past the trigger or freed it less than the reply margin before it | `Trigger`: raise the trigger line (and, without hardware triggering, send the StartStimulus) |
| Stimulus | the first tick at or after the start plus the lead | `Stimulus`, then the rules' `Update`s |
| Response | from `ResponseDelaySeconds` after the stimulus for `ResponseWindowSeconds` (infinity: no limit) | the rules' `Update`s; `LaserCap` once the laser has run `LaserCapSeconds` |
| Outcome | the tick (or lick or wheel sample, timed at the latest tick) the rules return an outcome, or the window's end | `Outcome`; `TriggerOff` when the line falls |
| Stimulus off | with `TrialTiming.StimulusDurationSeconds`, at `StimulusTiming.Time`: the earlier of the onset plus the duration and the outcome plus `ResponseToStimulusOffSeconds` | `StimulusOff`, once per trial whose stimulus came on; until then, after the outcome, an `IStimulusAfterOutcome`'s `AfterOutcome` updates (each tick and wheel input, never past the next trial's start) |
| Cues | the task's `TrialCue`s, from the stimulus or the next interval's start (`CueAnchor.Stimulus`, `NextIti`), outcome or not; a stimulus cue made with `droppedAtOutcome` never comes once the trial has its outcome (the spouts in after an early answer) | `Cue` |

`TrialTimes` gives the rules the trial's times as its row logs them (`ItiStartTime` for `itiStartTime`, `ItiSeconds` for `itiSeconds`, `quietWaitSeconds`, `trialStartTime`, `stimulusOnsetTime`, `StimulusOffTime` for `stimulusOffTime`, `laserTriggerTime`, and `Now` for `outcomeTime`); `LaserFired` is false on a trial answered before its trigger, which logs 0 mW. `SharedTrialColumns.Fill` fills a row's shared block from them and the outcome's reward and penalty (`rewardOfferedUl`, `rewarded`, `penalised`, `penaltyTimeoutSeconds`, `penaltyFlashBrightness`), so every task logs it one way; a task makes its reward command from the same `rewarded` it gives `Fill`.

## The laser

`LaserTiming.UntilOutcome(hardwareTriggered, triggerHoldSeconds, laserCapSeconds)`: the laser runs until the outcome (the workflow stops Zapit at `Outcome` or `LaserCap`), the trigger line falls `triggerHoldSeconds` after the outcome (the Zapit conditions file's max ramp-down, `ZapitConditions.Check(path).MaxRampDownSeconds`), and the next trial is armed then. `LaserTiming.ForDuration(hardwareTriggered, rampDownSeconds)`: each laser trial is an `ILaserDurationTrial`, and the line falls its own `LaserDurationSeconds` after the trigger, outcome or not, and the next trial is armed `rampDownSeconds` later (a trial answered before its trigger frees Zapit at its trigger's moment). `LaserTiming.UntilStimulusOff(hardwareTriggered, triggerHoldSeconds, laserCapSeconds)`: the laser runs until the trial's `StimulusOff`, even after the outcome, or its outcome when its stimulus never came on; the loop then emits `LaserOff` (the workflow stops Zapit at `LaserOff` or `LaserCap`, `laserCapSeconds` after the trigger or the trial's own `LaserDurationSeconds` when it is an `ILaserDurationTrial` and that is shorter), the line falls `triggerHoldSeconds` after the laser's end and the next trial is armed then; every laser trial's stimulus must have an off time.

Under `UntilOutcome` and `UntilStimulusOff` the workflow sends StopStimulus once per triggered trial, so the next trial also waits for Zapit's reply to it and then `TrialLoop.StopReplyMarginSeconds` (0.25 s, counted from the first tick after the reply), as well as the hold. MATLAB Zapit answers a StopStimulus only once its ramp-down has played (about 1 s in a rig test, 25 Nov 2026), and it stops whatever is running 0.2 s after each stop (its `delayStop` timer). A trial armed at the hold alone was stopped by that timer and played no laser, every other trial. A StopStimulus still unanswered at the next trial's trigger stops the workflow and names that trial. So does a reply that comes too late for the margin to pass before the trigger. A reply that comes before the loop's own stop event counts (the workflow's wall-clock backstop can stop Zapit first). `ForDuration` sends no StopStimulus: Zapit ends each stimulus itself, so it waits for no reply.

Zapit's reply to each StartStimulus is checked by `ZapitTrial.CheckArmed`; a StartStimulus still unanswered when the next is due stops the workflow. A trigger before the last laser frees Zapit stops it naming that laser and when it frees Zapit; `open_rigs.core.task` `Optogenetics.check_zapit_free_in_time` refuses such a task file first.

## In a workflow

A task's `src\Extensions` implements `ITrialRules<Trial, TUpdate, TOutcome>` (its `Trial` an `ITrial`, and an `IZapitTrial` if its trials are laser trials; its outcome a `TrialOutcome<TrialRow>`, or a class derived from it that adds what else the outcome sets off, such as the spouts) and declares:

```csharp
[Combinator]
[Description("Runs the trials (lib-open-rigs TrialLoop). Inputs: the rules, the Harp clock, the wheel in cm, the left and right lick states, Zapit's replies.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, TUpdate, TOutcome>> Process(IObservable<TaskRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitReply> zapitReplies)
    {
        return TrialLoop.Observe(rules, clock, new[] { lickLeft, lickRight }, zapitReplies, wheelCm);
    }
}
```

The lick inputs are one per spout, spout 0 the first (the `spout` the rules' `Lick` is given); the wheel is optional, left out (null) on a rig without one, where a trial with a wheel quiet wait is refused. The clock ending ends the trials; a lick input or the wheel ending stops the workflow naming it.

`TrialOutcome<TRow>` is built from the row `SharedTrialColumns.Fill` filled, with the task's `outcome` one of `TrialOutcomes` (`correct`, `incorrect`, `noResponse`, `probe`, `auto`, open_rigs' `OUTCOMES`), the rewarded valve's `rig.yml` wiring name, the trial's penalty level and its flash's rate (`Penalties.For`'s `Flash.RateHz`): `Row` (stamped with `outcomeTime`, for `LogTrials`), `Rewarded`, `Reward` (valve and microlitres, null unless rewarded, for `RewardRequests`), `Penalised`, `PenaltyLevelDbfs` (NaN when mute), `PenaltyTimeoutSeconds` and `PenaltyFlash` (the row's `penaltyFlashBrightness` at that rate, `PenaltyFlash.None` when none plays, for `OpenRigs.Vision:PlayPenaltyFlash`), so every task sends its outcomes into `TrialOutcomeEffects` the same way.

`TaskLogic` gives `RunTrials` the rules from `OnNextTick`, multicasts its events into `TrialEvents` (a `PublishSubject`), and picks each kind with `PickTrialEvents` (`Kind`, and `Cue` for one cue by name). `ZapitTrialCommand` makes each laser trial's StartStimulus from its `IZapitTrial`, from the `Arm` events on a hardware-triggered rig, else from the `Trigger` events: under `ForDuration` with the trial's own `LaserDurationSeconds`, and under `UntilOutcome` and `UntilStimulusOff` (`LaserTiming.UntilStopped`, mapped to `ZapitTrialCommand.UntilStopped`) with no duration, even for an `ILaserDurationTrial`, so Zapit plays until StopStimulus (from Trials .23; .22 sent the trial's duration, so Zapit could stop itself).

On ZapitDaq (OpenRigs.Zapit, in place of MATLAB Zapit): `TrialLoop.Observe(rules, clock, licks, zapitEvents, wheelCm)` takes ZapitDaq's events (`Zapit.Events`) in place of Zapit's replies, checks each `Armed` against the trial (`ZapitDaqTrial.CheckArmed`) and holds `ZapitDaqTrial.ArmSeconds` between arm and trigger; `LaserTiming.UntilOutcome(triggerHoldSeconds, laserCapSeconds)`, `UntilStimulusOff(triggerHoldSeconds, laserCapSeconds)` and `ForDuration(rampDownSeconds)` arm every trial during its interval; `ZapitDaqTrials` sends the Arms (under `UntilStopped`, `ZapitDaqTrialCommand.UntilStopped`, for the `Arm` event's `LaserSeconds`, `LaserTiming.ArmedSecondsOf`: the trial's stimulus's own off under `UntilStimulusOff` with a timed stimulus, else its cap, 0 for none) and early stops (at the outcome, or at `LaserOff` under `UntilStimulusOff`). The samples end at `LaserSeconds` with their own ramp, so a laser never outlasts its cap or its stimulus's own off should an early stop not land: on the rig (25 Nov 2026) NI-DAQmx did not rewrite a running buffer, and laserEnd stimulusOff trials armed with no duration played the whole `maxTrialSeconds` buffer. Since then ZapitDaq streams a trial armed with `UntilStopped` (or no duration), so its early stop ramps within `ZapitDaq.EarlyStopLeadSeconds` (0.1 s; 63 to 90 ms on B2); the trial row's `laserPowerMw` is the `Armed` event's capped power.
