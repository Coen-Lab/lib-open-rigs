using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;

namespace OpenRigs.Trials
{
    /// <summary>
    /// Each outcome's row, with rewarded true only when its valve's RewardDelivered (same valve, same microlitres) came since
    /// the previous outcome: TrialOutcomeEffects requests the reward first, and the valve plans it in the same call, so a
    /// reward the stop cut off is logged rewarded false. The outcome's own Rewarded (reward tone, spouts) is untouched.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [Description("Each outcome's row, rewarded only when its valve's RewardDelivered came for it.")]
    public class RewardedAsDelivered
    {
        /// <summary>The rows of <paramref name="source"/>'s outcomes, rewarded as <paramref name="delivered"/> says; completes with <paramref name="source"/>.</summary>
        public IObservable<Timestamped<TRow>> Process<TRow>(IObservable<TrialOutcome<TRow>> source, IObservable<RewardPlan> delivered) where TRow : class
        {
            return source.Publish(outcomes => Observable.Defer(() =>
            {
                var gate = new object();
                RewardPlan last = null;
                var plans = delivered.Do(p => { lock (gate) last = p; }).IgnoreElements().Select(_ => default(Timestamped<TRow>))
                    .TakeUntil(outcomes.LastOrDefaultAsync());
                return plans.Merge(outcomes.Select(o =>
                {
                    RewardPlan p;
                    lock (gate) { p = last; last = null; }
                    if (o.Rewarded && !Matches(p, o.Reward)) SetRewarded(o.Row.Value, false);
                    return o.Row;
                }));
            }));
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
