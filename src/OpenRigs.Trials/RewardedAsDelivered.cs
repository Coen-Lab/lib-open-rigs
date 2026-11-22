using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;

namespace OpenRigs.Trials
{
    /// <summary>
    /// Each outcome's row, in order, with rewarded true only when its valve's RewardDelivered (same valve, same microlitres)
    /// came for it. RewardDelivered is the board's answer to the write that opened the valve (RewardValve's RewardsFired), a
    /// few ms after the outcome, so a rewarded outcome's row waits for it: it goes out rewarded when it comes, rewarded
    /// false when the next outcome or the end of the outcomes comes first, and not at all when a stop tears the workflow
    /// down first (F12: a row written in the teardown cannot know whether the valve opened). A RewardDelivered that came
    /// since the previous outcome, before this one, also counts; a manual drop (RewardPlan.Manual) never does. The outcome's own Rewarded (reward tone, spouts) is untouched.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("Each outcome's row, in order, rewarded only when its valve's RewardDelivered (the board's answer) came for it: a rewarded row waits for it, and goes out not rewarded if the next outcome comes first.")]
    public class RewardedAsDelivered
    {
        class Pending<TRow> where TRow : class
        {
            public TrialOutcome<TRow> Outcome;
            public bool Waiting;
        }

        /// <summary>The rows of <paramref name="source"/>'s outcomes, rewarded as <paramref name="delivered"/> says; completes with <paramref name="source"/>.</summary>
        public IObservable<Timestamped<TRow>> Process<TRow>(IObservable<TrialOutcome<TRow>> source, IObservable<RewardPlan> delivered) where TRow : class
        {
            return Observable.Create<Timestamped<TRow>>(observer =>
            {
                var gate = new object();
                var rows = new Queue<Pending<TRow>>();
                RewardPlan early = null;  // a delivery since the previous outcome, before this one's
                var done = false;
                Action flush = () =>
                {
                    while (rows.Count > 0 && !rows.Peek().Waiting) observer.OnNext(rows.Dequeue().Outcome.Row);
                    if (done && rows.Count == 0) observer.OnCompleted();
                };
                Action<bool> giveUp = last =>
                {
                    foreach (var row in rows) if (row.Waiting) { SetRewarded(row.Outcome.Row.Value, false); row.Waiting = false; }
                    done = last;
                    flush();
                };
                var plans = delivered.Where(plan => !plan.Manual).Subscribe(plan =>
                {
                    lock (gate)
                    {
                        foreach (var row in rows)
                        {
                            if (row.Waiting && Matches(plan, row.Outcome.Reward)) { row.Waiting = false; flush(); return; }
                        }
                        early = plan;
                    }
                }, error => { lock (gate) observer.OnError(error); });
                var outcomes = source.Subscribe(outcome =>
                {
                    lock (gate)
                    {
                        giveUp(false);
                        var row = new Pending<TRow> { Outcome = outcome, Waiting = outcome.Rewarded && !Matches(early, outcome.Reward) };
                        early = null;
                        rows.Enqueue(row);
                        flush();
                    }
                }, error => { lock (gate) observer.OnError(error); }, () => { lock (gate) giveUp(true); });
                return new System.Reactive.Disposables.CompositeDisposable(outcomes, plans);
            });
        }

        static bool Matches(RewardPlan plan, Tuple<string, double> reward)
        {
            return plan != null && plan.RewardLine != null && reward != null
                && string.Equals(plan.RewardLine.Instance, reward.Item1, StringComparison.OrdinalIgnoreCase)
                && plan.RequestedUl == reward.Item2;
        }

        static void SetRewarded(object row, bool value)
        {
            row.GetType().GetField("rewarded", BindingFlags.Public | BindingFlags.Instance).SetValue(row, value);
        }
    }
}
