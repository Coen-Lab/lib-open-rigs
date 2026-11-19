using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>The rewards for one valve, each with the valve's line, for PlanReward.</summary>
    [Combinator]
    [Description("Each (valve name, microlitres) reward, with its valve's RewardLine, passed on as (microlitres, line) when the name is the line's own (rig.yml's wiring name, such as left, ignoring case); a reward for another valve is dropped.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class RewardForValve
    {
        /// <summary>Passes on the rewards for the line's valve.</summary>
        public IObservable<Tuple<double, RewardLine>> Process(IObservable<Tuple<Tuple<string, double>, RewardLine>> source)
        {
            return source
                .Where(value => string.Equals(value.Item1.Item1, value.Item2.Instance, StringComparison.OrdinalIgnoreCase))
                .Select(value => Tuple.Create(value.Item1.Item2, value.Item2));
        }
    }

    /// <summary>
    /// A RewardValve piece's announcement into RewardValves: its valve's rig.yml name (left), at its line, and then never
    /// completes, so the multicast after it leaves RewardValves open for every other valve (Bonsai 2.9 passes a
    /// multicast source's completion on to the subject, which would end the other valves' announcements with it).
    /// </summary>
    [Combinator]
    [Description("Emits the valve's rig.yml name (its RewardLine's Instance, such as left) for each line, and never completes, so a multicast into RewardValves after it leaves the subject open for every other valve.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class AnnounceRewardValve
    {
        /// <summary>The valve's name for each line, never completing.</summary>
        public IObservable<string> Process(IObservable<RewardLine> source)
        {
            return source.Select(line => line.Instance).Concat(Observable.Never<string>());
        }
    }

    /// <summary>
    /// Every reward request checked against the valves the workflow's RewardValve pieces announced: a request for a
    /// valve no piece fires would give no water and say nothing, so it stops the workflow, naming the valves there are.
    /// </summary>
    [Combinator]
    [Description("Passes each (valve name, microlitres) reward through, and stops the workflow on one whose valve no RewardValve piece announced (second input, each piece's rig.yml name), naming the valves there are.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class RequireRewardValve
    {
        /// <summary>Passes each request for an announced valve through.</summary>
        public IObservable<Tuple<string, double>> Process(IObservable<Tuple<string, double>> requests, IObservable<string> valves)
        {
            return Observable.Defer(() =>
            {
                var gate = new object();
                var known = new List<string>();
                var announced = valves.Do(name => { lock (gate) known.Add(name); }).IgnoreElements().Select(_ => default(Tuple<string, double>));
                var checkedRequests = requests.Select(request =>
                {
                    lock (gate)
                    {
                        if (!known.Any(name => string.Equals(name, request.Item1, StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidOperationException(string.Format(
                                "A reward from the valve '{0}', which no RewardValve piece in this workflow fires; its valves are {1}. " +
                                "Ask for a valve by its name in rig.yml's wiring, or include its piece (RewardValve-<Name>).",
                                request.Item1, known.Count == 0 ? "none" : string.Join(", ", known)));
                        }
                    }
                    return request;
                });
                return announced.Merge(checkedRequests);
            });
        }
    }

    /// <summary>
    /// Each reward plan once its board has answered the write that opens its valve: the board's Write reply to OutputSet
    /// with the plan's line set, for the first of the plan's pulses. The plan alone is not a delivery: a stop at the
    /// outcome can leave the valve's chain planning while the board's path is already gone (F12), and then no answer
    /// comes and neither does the plan. Plans are answered in order; a plan of no pulses opens nothing and never comes.
    /// The board answers over its serial link, after the plan has reached this operator.
    /// </summary>
    [Combinator]
    [Description("Each reward plan (RewardDelivered's), passed on when its board's messages (second input, Board.Data) carry the Write reply to OutputSet with its line set: the valve opened. A plan the board never answers, as at a stop, never comes.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class RewardsFired
    {
        /// <summary>The plans of <paramref name="source"/>, each as the board answers its first pulse.</summary>
        public IObservable<RewardPlan> Process(IObservable<RewardPlan> source, IObservable<HarpMessage> board)
        {
            return Observable.Create<RewardPlan>(observer =>
            {
                var gate = new object();
                var waiting = new Queue<RewardPlan>();
                var answered = 0;  // the head plan's pulses the board has answered
                var sourceDone = false;
                Action completeWhenIdle = () => { if (sourceDone && waiting.Count == 0) observer.OnCompleted(); };
                var answers = board.Subscribe(message =>
                {
                    lock (gate)
                    {
                        if (waiting.Count == 0 || message.Address != PulseLines.OutputSet || message.MessageType != MessageType.Write || message.Error) return;
                        var plan = waiting.Peek();
                        if ((message.GetPayloadUInt16() & PulseLines.Mask(plan.Line)) == 0) return;
                        if (answered++ == 0) observer.OnNext(plan);
                        if (answered >= plan.Pulses)
                        {
                            waiting.Dequeue();
                            answered = 0;
                            completeWhenIdle();
                        }
                    }
                }, error => { lock (gate) observer.OnError(error); });
                var plans = source.Subscribe(plan =>
                {
                    lock (gate) if (plan != null && plan.Pulses > 0) waiting.Enqueue(plan);
                }, error => { lock (gate) observer.OnError(error); }, () => { lock (gate) { sourceDone = true; completeWhenIdle(); } });
                return new System.Reactive.Disposables.CompositeDisposable(plans, answers);
            });
        }
    }
}
