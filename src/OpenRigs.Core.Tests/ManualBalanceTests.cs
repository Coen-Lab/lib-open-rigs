using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class ManualBalanceTests
    {
        static DispenseCalibration Settings(int priming = 2)
        {
            return new DispenseCalibration
            {
                Procedure = new DispenseProcedure
                {
                    Samples = new List<DispenseSample>
                    {
                        new DispenseSample { Size = 10, Repeats = 2, InterPulseIntervalMs = 1 },
                        new DispenseSample { Size = 20, Repeats = 2, InterPulseIntervalMs = 1 }
                    },
                    PrimingPulses = priming,
                    XUnit = "ms",
                    SettleWindow = 10,
                    WeighTimeoutS = 60
                },
                Balance = new SerialBalance()
            };
        }

        static DispenseCalibration Apply(string port)
        {
            return new ApplyBalancePort().Process(Observable.Return(Tuple.Create(Settings(), port))).Wait();
        }

        [TestMethod]
        public void ApplyBalancePort_SetsASerialPort()
        {
            var settings = Apply(" COM7 ");
            Assert.AreEqual("COM7", settings.Balance.PortName);
            Assert.AreEqual(10, settings.Procedure.SettleWindow);
            Assert.AreEqual(60, settings.Procedure.WeighTimeoutS);
        }

        [TestMethod]
        public void ApplyBalancePort_ManualTakesOneTypedWeightAndWaits()
        {
            var settings = Apply("Manual");
            Assert.AreEqual(BalancePort.Manual, settings.Balance.PortName);
            Assert.AreEqual(1, settings.Procedure.SettleWindow);
            Assert.AreEqual(3600, settings.Procedure.WeighTimeoutS);
        }

        [TestMethod]
        public void ApplyBalancePort_RefusesAnEmptyPort()
        {
            Assert.ThrowsException<InvalidOperationException>(() => Apply(" "));
        }

        [TestMethod]
        public void ManualWeighing_AsksAfterEachBurstAndTakesOnlyDueWeights()
        {
            var start = new Subject<DispenseCalibration>();
            var pulses = new Subject<Tuple<int, int>>();
            var typed = new Subject<string>();
            var events = new List<ManualWeighingEvent>();
            var due = new AutoResetEvent(false);
            using (new ManualWeighing().Process(start, pulses, typed).Subscribe(e =>
            {
                lock (events) events.Add(e);
                if (e.Prompt.StartsWith("Weigh Now")) due.Set();
            }))
            {
                typed.OnNext("5");
                start.OnNext(Settings());
                pulses.OnNext(Tuple.Create(1, 20));
                typed.OnNext("1.0");
                pulses.OnNext(Tuple.Create(1, 20));
                Assert.IsTrue(due.WaitOne(5000), "the baseline was never asked for");
                typed.OnNext("heavy");
                typed.OnNext("20.5 g");
                due.Reset(); // the refusal above asked again
                for (int sample = 0; sample < 2; sample++)
                {
                    pulses.OnNext(Tuple.Create(1, 10));
                    pulses.OnNext(Tuple.Create(1, 10));
                    Assert.IsTrue(due.WaitOne(5000), "sample " + (sample + 1) + " was never asked for");
                    typed.OnNext(sample == 0 ? "20.6" : "20.8");
                }
            }

            List<ManualWeighingEvent> seen;
            lock (events) seen = events.ToList();
            var readings = seen.Where(e => e.Reading.HasValue).Select(e => (double)e.Reading.Value.Value).ToList();
            CollectionAssert.AreEqual(new[] { 20.5, 20.6, 20.8 }, readings.Select(r => Math.Round(r, 3)).ToArray());
            Assert.IsTrue(seen.Any(e => e.Prompt.Contains("5 g refused")), "a weight before the run was taken");
            Assert.IsTrue(seen.Any(e => e.Prompt.Contains("1.0 g refused")), "a weight mid-burst was taken");
            Assert.IsTrue(seen.Any(e => e.Prompt.Contains("'heavy' is not a weight")), "text was taken as a weight");
            Assert.IsTrue(seen.Last().Prompt.StartsWith("Every weight taken"));
            Assert.IsTrue(seen.Where(e => e.Reading.HasValue).All(e => e.Reading.Value.IsStable));
        }

        [TestMethod]
        public void ManualReadings_PassesOnlyTakenWeights()
        {
            var events = new[]
            {
                new ManualWeighingEvent { Prompt = "wait" },
                new ManualWeighingEvent { Prompt = "taken", Reading = new WeightMeasurement(0, 1.5f, 1) }
            };
            var readings = new ManualReadings().Process(events.ToObservable()).ToList().Wait();
            Assert.AreEqual(1, readings.Count);
            Assert.AreEqual(1.5f, readings[0].Value);
        }

        [TestMethod]
        public void DescribeCalibrationCurve_ListsEachPoint()
        {
            var curve = new CalibrationCurve
            {
                XUnit = "ms",
                YUnit = "ul",
                Points = new List<CalibrationPoint> { new CalibrationPoint { X = 10, Y = 0.62 }, new CalibrationPoint { X = 20, Y = 1.19 } }
            };
            Assert.AreEqual("10 ms 0.620 ul, 20 ms 1.190 ul", new DescribeCalibrationCurve().Process(Observable.Return(curve)).Wait());
        }
    }
}
