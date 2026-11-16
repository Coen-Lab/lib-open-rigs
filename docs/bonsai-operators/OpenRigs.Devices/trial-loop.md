# Trial Loop

`TrialLoop` runs a task's trials on the Harp clock: one state machine for what every task shares (the interval, the quiet waits, the trial start, the laser's arming, lead and trigger, the stimulus, the response window, the outcome and its timeout), with the task's own part (the draw, the stimulus, the response rule, the outcome) given as an `ITrialRules`. It is tested C#, driven in its tests tick by tick, so two tasks never time a trial two ways.

---

## A trial

| Phase | When | Event |
|---|---|---|
| Iti | the last outcome plus its timeout, exactly; trial 1 the first tick | `Iti` (the trial just drawn) |
| Arm | a laser trial on a hardware-triggered rig: the interval's start, or once the last laser is done (`LaserTiming`) | `Arm`: send the trial's StartStimulus |
| Start | the interval's end less the lead, `max(laserToStimulusSeconds, 0)`, or later until the quiet waits hold (`QuietWait`) | `Start` (exact time) |
| Trigger | `laserToStimulusSeconds` before the stimulus, if no outcome yet; the workflow stops if Zapit has not armed the trial, naming the last laser when it held Zapit past the trigger or freed it less than the reply margin before it | `Trigger`: raise the trigger line (and, without hardware triggering, send the StartStimulus) |
| Stimulus | the first tick at or after the start plus the lead | `Stimulus`, then the rules' `Update`s |
| Response | from `ResponseDelaySeconds` after the stimulus for `ResponseWindowSeconds` (infinity: no limit) | the rules' `Update`s; `LaserCap` once the laser has run `LaserMaxSeconds` |
| Outcome | the tick (or lick or wheel sample, timed at the latest tick) the rules return an outcome, or the window's end | `Outcome`; `TriggerOff` when the line falls |
| Cues | the task's `TrialCue`s, from the stimulus or the next interval's start (`CueAnchor.Stimulus`, `NextIti`), outcome or not; a stimulus cue made with `droppedAtOutcome` never comes once the trial has its outcome (the spouts in after an early answer) | `Cue` |

`TrialTimes` gives the rules the trial's times as its row logs them (`ItiStartTime` for `itiStartTime`, `ItiSeconds` for `itiSeconds`, `quietWaitSeconds`, `trialStartTime`, `stimulusOnsetTime`, `laserTriggerTime`, and `Now` for `outcomeTime`); `LaserFired` is false on a trial answered before its trigger, which logs 0 mW. `SharedTrialColumns.Fill` fills a row's shared block from them and the outcome's reward and penalty (`rewardOfferedUl`, `rewarded`, `penalised`), so every task logs it one way; a task makes its reward command from the same `rewarded` it gives `Fill`.

## The laser

`LaserTiming.UntilOutcome(hardwareTriggered, triggerHoldSeconds, laserMaxSeconds)`: the laser runs until the outcome (the workflow stops Zapit at `Outcome` or `LaserCap`), the trigger line falls `triggerHoldSeconds` after the outcome, and the next trial is armed then. `LaserTiming.ForDuration(hardwareTriggered, laserDurationSeconds, rampDownSeconds)`: the line falls `laserDurationSeconds` after the trigger, outcome or not, and the next trial is armed `rampDownSeconds` later (a trial answered before its trigger frees Zapit at its trigger's moment). Zapit's reply to each StartStimulus is checked by `ZapitTrial.CheckArmed`; a StartStimulus still unanswered when the next is due stops the workflow. A trigger before the last laser frees Zapit stops it naming that laser and when it frees Zapit; `open_rigs.core.task` `Optogenetics.check_zapit_free_in_time` refuses such a task file first.

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

`TrialOutcome<TRow>` is built from the row `SharedTrialColumns.Fill` filled, with the task's `outcome` one of `TrialOutcomes` (`Correct`, `Incorrect`, `NoResponse`, `Probe`, open_rigs' `OUTCOMES`) and the rewarded valve's `rig.yml` wiring name: `Row` (stamped with `outcomeTime`, for `LogTrial`), `Rewarded`, `Reward` (valve and microlitres, null unless rewarded, for `RewardRequests`), `PenaltySound` and `PenaltyTimeoutSeconds`, so `TaskLogic` takes `Outcome.Row`, `Outcome.Reward` and `Outcome.PenaltySound` the same way in every task.

`TaskLogic` gives `RunTrials` the rules from `OnNextTick`, multicasts its events into `TrialEvents` (a `PublishSubject`), and picks each kind with `PickTrialEvents` (`Kind`, and `Cue` for one cue by name).
