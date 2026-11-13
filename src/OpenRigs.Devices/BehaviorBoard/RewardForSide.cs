using Bonsai;
using OpenRigs.Core;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>Pairs each (side, microlitres) reward with that side's reward line, for PlanReward.</summary>
    [Combinator]
    [Description("Pairs each (side, microlitres) reward with its side's line, from the (left, right) reward lines. Stops the workflow on a side that is neither.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class RewardForSide
    {
        /// <summary>Pairs each reward with its line.</summary>
        public IObservable<Tuple<double, RewardLine>> Process(IObservable<Tuple<Tuple<string, double>, Tuple<RewardLine, RewardLine>>> source)
        {
            return source.Select(value => Tuple.Create(value.Item1.Item2, Pick(value.Item1.Item1, value.Item2.Item1, value.Item2.Item2)));
        }

        /// <summary>The line whose side is <paramref name="side"/>, ignoring case.</summary>
        public static RewardLine Pick(string side, RewardLine left, RewardLine right)
        {
            if (string.Equals(side, left.Side, StringComparison.OrdinalIgnoreCase)) return left;
            if (string.Equals(side, right.Side, StringComparison.OrdinalIgnoreCase)) return right;
            throw new ArgumentException(string.Format("A reward for side '{0}': the reward lines are {1} and {2}.", side, left.Side, right.Side));
        }
    }
}
