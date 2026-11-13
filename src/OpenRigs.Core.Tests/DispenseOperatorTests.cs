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
            return new DispenseSample { Size = size, Repeats = repeats, PulseGapMs = intervalMs };
        }

        static IObservable<WeightMeasurement> Readings(double grams, bool stable = true, int periodMs = 5)
        {
            return Observable.Interval(TimeSpan.FromMilliseconds(periodMs)).Select(i => new WeightMeasurement(i, (float)grams, stable ? 1f : 0f));
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
            // Priming, baseline and three samples, fired by FirePulses on a stand-in clock against a
            // simulated balance that gains 0.05 ul per ms of pulse plus 0.1 ul per pulse and reports every 5 ms.
            var procedure = new DispenseProcedure
            {
                Samples = new List<DispenseSample> { Sample(20), Sample(40), Sample(60) },
                PrimingPulses = 2, SettleWindow = 2, WeighTimeoutS = 2,
            };
            var requests = new Subject<Tuple<int, int>>();
            var grams = new double[1];
            var readings = new BehaviorSubject<WeightMeasurement>(new WeightMeasurement(0, 0, 1));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var clock = Observable.Interval(TimeSpan.FromMilliseconds(1)).Select(_ => stopwatch.Elapsed.TotalSeconds).Publish().RefCount();
            var weigh = new WeighStable { SettleWindow = 2, TimeoutSeconds = 2 };

            Func<int, int, int, IObservable<double>> fireThenWeigh = (width, count, gap) => Observable.Defer(() =>
                weigh.Process(new FirePulses { WidthMs = width, Count = count, PulseGapMs = gap }
                    .Process(Observable.Return(0), clock).Do(requests.OnNext), readings));

            using (requests.Subscribe(request => grams[0] += (0.1 + 0.05 * request.Item2) / 1000.0))
            using (Observable.Interval(TimeSpan.FromMilliseconds(5)).Select(i => new WeightMeasurement(i, (float)grams[0], 1f)).Subscribe(readings))
            {
                var weights = fireThenWeigh(60, procedure.PrimingPulses, 2)
                    .Concat(procedure.Samples.Select(sample => fireThenWeigh(sample.Size, sample.Repeats, sample.PulseGapMs)).Concat());
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
