using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class DispenseOperatorTests
    {
        static DispenseSample Sample(int size, int repeats = 4, int intervalMs = 2)
        {
            return new DispenseSample { Size = size, Repeats = repeats, InterPulseIntervalMs = intervalMs };
        }

        static IObservable<WeightMeasurement> Readings(double grams, bool stable = true, int periodMs = 5)
        {
            return Observable.Interval(TimeSpan.FromMilliseconds(periodMs)).Select(i => new WeightMeasurement(i, (float)grams, stable ? 1f : 0f));
        }

        [TestMethod]
        public void PulseBurst_FiresRepeatsRequestsOnTheChannel()
        {
            var requests = new PulseBurst { Channel = 2 }.Process(Observable.Return(Sample(20, repeats: 3))).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            Assert.AreEqual(3, requests.Count);
            Assert.IsTrue(requests.All(r => r.Item1 == 2 && r.Item2 == 20));
        }

        [TestMethod]
        public void PulseBurst_CompletesAfterTheLastPulseHasLanded()
        {
            var started = DateTime.UtcNow;
            PulseBurst.Burst(1, 20, 3, 30).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            // Three pulses at t = 0, 50, 100 ms, then one more period before completion.
            Assert.IsTrue((DateTime.UtcNow - started).TotalMilliseconds >= 140, "completed too early");
        }

        [TestMethod]
        public void PulseBurst_PrimesAtTheLargestSampleUnlessToldOtherwise()
        {
            var procedure = new DispenseProcedure { Samples = new List<DispenseSample> { Sample(20), Sample(60), Sample(40) }, PrimingPulses = 2 };
            var priming = new PulseBurst().Process(Observable.Return(procedure)).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            Assert.AreEqual(2, priming.Count);
            Assert.IsTrue(priming.All(r => r.Item2 == 60));

            procedure.PrimingSize = 30;
            Assert.IsTrue(new PulseBurst().Process(Observable.Return(procedure)).ToList().Wait().All(r => r.Item2 == 30));

            procedure.PrimingPulses = 0;
            Assert.AreEqual(0, new PulseBurst().Process(Observable.Return(procedure)).ToList().Wait().Count);
        }

        [TestMethod]
        public void WeighStable_WeighsAfterTheStepCompletesAndTheBalanceSettles()
        {
            var step = Observable.Timer(TimeSpan.FromMilliseconds(50)).Select(_ => "burst");
            var weight = new WeighStable { SettleWindow = 3, TimeoutSeconds = 2 }.Process(step, Readings(1.25)).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            Assert.AreEqual(1, weight.Count);
            Assert.AreEqual(1.25, weight[0], 1e-6);
        }

        [TestMethod]
        public void WeighStable_AnEmptyStepStillWeighs()
        {
            var weight = new WeighStable { SettleWindow = 2, TimeoutSeconds = 2 }.Process(Observable.Empty<int>(), Readings(0.5)).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
            Assert.AreEqual(0.5, weight.Single(), 1e-6);
        }

        [TestMethod]
        public void WeighStable_IgnoresTheReplayedReadingFromBeforeTheStep()
        {
            // A BehaviorSubject replays its last value; a window of 1 must not accept it.
            var readings = new BehaviorSubject<WeightMeasurement>(new WeightMeasurement(0, 99f, 1f));
            var step = Observable.Timer(TimeSpan.FromMilliseconds(30)).Select(_ => 0);
            var fresh = Observable.Timer(TimeSpan.FromMilliseconds(80)).Select(_ => new WeightMeasurement(1, 1f, 1f));
            using (fresh.Subscribe(readings))
            {
                var weight = new WeighStable { SettleWindow = 1, TimeoutSeconds = 2 }.Process(step, readings).ToList().Timeout(TimeSpan.FromSeconds(5)).Wait();
                Assert.AreEqual(1.0, weight.Single(), 1e-6);
            }
        }

        [TestMethod]
        public void WeighStable_FailsWhenTheBalanceNeverSettles()
        {
            try
            {
                new WeighStable { SettleWindow = 2, TimeoutSeconds = 0.2 }.Process(Observable.Empty<int>(), Readings(1, stable: false)).ToList().Wait();
                Assert.Fail("expected a timeout");
            }
            catch (TimeoutException ex)
            {
                StringAssert.Contains(ex.Message, "consecutive stable readings");
            }
        }

        [TestMethod]
        public void TheOperatorsComposeIntoTheProcedure()
        {
            // The same scenario the include wires: priming, baseline, three samples, against a simulated
            // balance that gains 0.05 ul per ms of pulse plus 0.1 ul per pulse and reports every 5 ms.
            var procedure = new DispenseProcedure
            {
                Samples = new List<DispenseSample> { Sample(20), Sample(40), Sample(60) },
                PrimingPulses = 2, SettleWindow = 2, WeighTimeoutS = 2,
            };
            var requests = new Subject<Tuple<int, int>>();
            var grams = new double[1];
            var readings = new BehaviorSubject<WeightMeasurement>(new WeightMeasurement(0, 0, 1));
            var burst = new PulseBurst();
            var weigh = new WeighStable { SettleWindow = 2, TimeoutSeconds = 2 };

            Func<IObservable<Tuple<int, int>>, IObservable<double>> fireThenWeigh = pulses =>
                weigh.Process(pulses.Do(requests.OnNext), readings);

            using (requests.Subscribe(request => grams[0] += (0.1 + 0.05 * request.Item2) / 1000.0))
            using (Observable.Interval(TimeSpan.FromMilliseconds(5)).Select(i => new WeightMeasurement(i, (float)grams[0], 1f)).Subscribe(readings))
            {
                var weights = fireThenWeigh(burst.Process(Observable.Return(procedure)))
                    .Concat(procedure.Samples.ToObservable().Select(sample => Observable.Defer(() => fireThenWeigh(burst.Process(Observable.Return(sample))))).Concat());
                // The include does the arithmetic with stock nodes: Skip(1) zipped with the weights gives the gain,
                // an ExpressionTransform divides by repeats and density.
                var gains = weights.Publish(shared => shared.Skip(1).Zip(shared, (current, previous) => current - previous));
                var points = gains.Zip(procedure.Samples.ToObservable(), (gain, sample) => Tuple.Create((double)sample.Size, gain / sample.Repeats / procedure.DensityGPerMl * 1000.0))
                    .ToList().Timeout(TimeSpan.FromSeconds(20)).Wait();

                CollectionAssert.AreEqual(new[] { 20.0, 40.0, 60.0 }, points.Select(p => p.Item1).ToArray());
                Assert.AreEqual(1.1, points[0].Item2, 1e-3);
                Assert.AreEqual(2.1, points[1].Item2, 1e-3);
                Assert.AreEqual(3.1, points[2].Item2, 1e-3);
            }
        }
    }
}
