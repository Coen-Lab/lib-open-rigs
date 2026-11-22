# Trial Loop

`TrialLoop` runs a task's trials on the Harp clock: one state machine for what every task shares (the interval, the quiet waits, the trial start, the laser's arming, lead and trigger, the stimulus, the response window, the outcome and its timeout), with the task's own part (the draw, the stimulus, the response rule, the outcome) given as an `ITrialRules`. It is tested C#, driven in its tests tick by tick, so two tasks never time a trial two ways. Its types are the OpenRigs.Trials package's (namespace `OpenRigs.Trials`), which depends on OpenRigs.Devices for Zapit.

---

## A trial

| Phase | When | Event |
|---|---|---|
| Iti | the last outcome plus its timeout (penalised; a reward adds nothing), exactly; with `TrialTiming.ResponseWindowAlwaysFull`, the last window's end plus the timeout, rewarded or not (`TrialTimes.TrialEndTime`); trial 1 the first tick | `Iti` (the trial just drawn) |
| Arm | a laser trial (ZapitDaq armed, waiting on the trigger line): the interval's start, or once the last laser is done (`LaserTiming`) | `Arm`: send the trial's StartStimulus |
| Start | the interval's end less the lead, `max(laserToStimulusSeconds, 0)`, or later until the quiet waits hold (`QuietWait`) | `Start` (exact time) |
| Trigger | `laserToStimulusSeconds` before the stimulus, if no outcome yet; the workflow stops if ZapitDaq has not armed the trial, naming the last laser when it held Zapit past the trigger or freed it less than the reply margin before it | `Trigger`: raise the trigger line (and, without hardware triggering, send the StartStimulus) |
| Stimulus | the first tick at or after the start plus the lead | `Stimulus`, then the rules' `Update`s |
| Response | from `ResponseDelaySeconds` after the stimulus for `ResponseWindowSeconds` (infinity: no limit) | the rules' `Update`s; `LaserCap` once the laser has run `LaserCapSeconds` |
| Outcome | the tick (or lick or wheel sample, timed at the latest tick) the rules return an outcome, or the window's end | `Outcome`; `TriggerOff` when the line falls |
| Stimulus off | with `TrialTiming.StimulusDurationSeconds`, at `StimulusTiming.Time`: the earlier of the onset plus the duration and the outcome plus `ResponseToStimulusOffSeconds` | `StimulusOff`, once per trial whose stimulus came on; until then, after the outcome, an `IStimulusAfterOutcome`'s `AfterOutcome` updates (each tick and wheel input, never past the next trial's start) |
| Cues | the task's `TrialCue`s, from the stimulus or the next interval's start (`CueAnchor.Stimulus`, `NextIti`), outcome or not; a stimulus cue made with `droppedAtOutcome` never comes once the trial has its outcome (the spouts in after an early answer) | `Cue` |

`TrialTimes` gives the rules the trial's times as its row logs them (`ItiStartTime` for `itiStartTime`, `ItiSeconds` for `itiSeconds`, `quietWaitSeconds`, `trialStartTime`, `stimulusOnsetTime`, `StimulusOffTime` for `stimulusOffTime`, `laserTriggerTime`, and `Now` for `outcomeTime`); `LaserFired` is false on a trial answered before its trigger, which logs 0 mW. `SharedTrialColumns.Fill` fills a row's shared block from them and the outcome's reward and penalty (`rewardOfferedUl`, `rewarded`, `penalised`, `penaltyTimeoutSeconds`, `penaltyFlashBrightness`), so every task logs it one way; a task makes its reward command from the same `rewarded` it gives `Fill`.

## The laser

`LaserTiming.UntilOutcome(triggerHoldSeconds, laserCapSeconds)`: the laser runs until the outcome (ZapitTrials sends ZapitDaq an early stop at `Outcome` or `LaserCap`: its ramp, then laser and LED at 0 V), the trigger line falls `triggerHoldSeconds` after the outcome (at least the bank's longest ramp plus rig.yml's `earlyStopLeadSeconds`: `ZapitBank.LongestRampSeconds`), and the next trial is armed then. `LaserTiming.ForDuration(rampDownSeconds)`: each laser trial is an `ILaserDurationTrial`, and the line falls its own `LaserDurationSeconds` after the trigger, outcome or not, and the next trial is armed `rampDownSeconds` later (a trial answered before its trigger frees ZapitDaq at its trigger's moment). Each trial is armed during its interval; ZapitDaq's `Armed` event is checked by `ZapitTrial.CheckArmed`, and a trial not armed by its trigger, or armed less than `ZapitTrial.ArmSeconds` before it, stops the workflow. A trigger before the last laser frees ZapitDaq stops it naming that laser and when it frees ZapitDaq; `open_rigs.core.task` `Optogenetics.check_zapit_free_in_time` refuses such a task file first.

## In a workflow

A task's `src\Extensions` implements `ITrialRules<Trial, TUpdate, TOutcome>` (its `Trial` an `ITrial`, and an `IZapitTrial` if its trials are laser trials; its outcome a `TrialOutcome<TrialRow>`, or a class derived from it that adds what else the outcome sets off, such as the spouts) and declares:

```csharp
[Combinator]
[Description("Runs the trials (lib-open-rigs TrialLoop). Inputs: the rules, the Harp clock, the wheel in cm, the left and right lick states, ZapitDaq's events (Zapit.Events).")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class RunTrials
{
    public IObservable<TrialEvent<Trial, TUpdate, TOutcome>> Process(IObservable<TaskRules> rules, IObservable<double> clock,
        IObservable<double> wheelCm, IObservable<bool> lickLeft, IObservable<bool> lickRight, IObservable<ZapitDaqEvent> zapitEvents)
    {
        return TrialLoop.Observe(rules, clock, new[] { lickLeft, lickRight }, zapitEvents, wheelCm);
    }
}
```

The lick inputs are one per spout, spout 0 the first (the `spout` the rules' `Lick` is given); the wheel is optional, left out (null) on a rig without one, where a trial with a wheel quiet wait is refused. The clock ending ends the trials; a lick input or the wheel ending stops the workflow naming it.

`TrialOutcome<TRow>` is built from the row `SharedTrialColumns.Fill` filled, with the task's `outcome` one of `TrialOutcomes` (`correct`, `incorrect`, `noResponse`, `probe`, `auto`, open_rigs' `OUTCOMES`), the rewarded valve's `rig.yml` wiring name, the trial's penalty level and its flash's rate (`Penalties.For`'s `Flash.RateHz`): `Row` (stamped with `outcomeTime`, for `LogTrials`), `Rewarded`, `Reward` (valve and microlitres, null unless rewarded, for `RewardRequests`), `Penalised`, `PenaltyLevelDbfs` (NaN when mute), `PenaltyTimeoutSeconds` and `PenaltyFlash` (the row's `penaltyFlashBrightness` at that rate, `PenaltyFlash.None` when none plays, for `OpenRigs.Vision:PlayPenaltyFlash`), so every task sends its outcomes into `TrialOutcomeEffects` the same way.

`TaskLogic` gives `RunTrials` the rules from `OnNextTick`, multicasts its events into `TrialEvents` (a `PublishSubject`), and picks each kind with `PickTrialEvents` (`Kind`, and `Cue` for one cue by name). `ZapitTrialCommand` makes each laser trial's ZapitDaq Arm from its `IZapitTrial`, from the `Arm` events.
