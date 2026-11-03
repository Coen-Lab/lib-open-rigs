using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class LookupCalibrationTests
    {
        // y = 0.05 x + 0.1 over x in [20, 60], so y in [1.1, 3.1].
        static CalibrationCurveFit Line()
        {
            return new CalibrationCurveFit(new double[] { 20, 40, 60 }, new double[] { 1.1, 2.1, 3.1 }, CalibrationModel.LinearFit, 2);
        }

        [TestMethod]
        public void Inverse_WithinRange_ReturnsWidth()
        {
            var result = LookupCalibration.Lookup(Line(), 2.1, LookupDirection.Inverse, OutOfRangePolicy.Clamp);
            Assert.AreEqual(40, result.Output, 1e-9);
            Assert.AreEqual(2.1, result.EffectiveInput);
            Assert.IsFalse(result.Clamped);
        }

        [TestMethod]
        public void Inverse_AboveRange_ClampsAndReportsEffectiveVolume()
        {
            var result = LookupCalibration.Lookup(Line(), 4.0, LookupDirection.Inverse, OutOfRangePolicy.Clamp);
            Assert.AreEqual(60, result.Output, 1e-9);
            Assert.AreEqual(3.1, result.EffectiveInput, 1e-12);
            Assert.IsTrue(result.Clamped);
        }

        [TestMethod]
        public void Forward_BelowRange_ClampsToMinX()
        {
            var result = LookupCalibration.Lookup(Line(), 5, LookupDirection.Forward, OutOfRangePolicy.Clamp);
            Assert.AreEqual(1.1, result.Output, 1e-12);
            Assert.AreEqual(20, result.EffectiveInput);
            Assert.IsTrue(result.Clamped);
        }

        [TestMethod]
        public void Forward_WithinRange_Evaluates()
        {
            var result = LookupCalibration.Lookup(Line(), 30, LookupDirection.Forward, OutOfRangePolicy.Throw);
            Assert.AreEqual(1.6, result.Output, 1e-12);
            Assert.IsFalse(result.Clamped);
        }

        [TestMethod]
        public void ThrowPolicy_RaisesOutsideRange()
        {
            var ex = Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                LookupCalibration.Lookup(Line(), 4.0, LookupDirection.Inverse, OutOfRangePolicy.Throw));
            StringAssert.Contains(ex.Message, "1.1");
            StringAssert.Contains(ex.Message, "3.1");
        }

        [TestMethod]
        public void NaN_IsRejected()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                LookupCalibration.Lookup(Line(), double.NaN, LookupDirection.Forward, OutOfRangePolicy.Clamp));
        }

        [TestMethod]
        public void Operator_PairedInput_UsesCurveFromPair()
        {
            var lookup = new LookupCalibration { Direction = LookupDirection.Inverse };
            var source = Observable.Return(Tuple.Create(1.6, Line()));
            var result = lookup.Process(source).Wait();
            Assert.AreEqual(30, result.Output, 1e-9);
        }

        [TestMethod]
        public void Operator_PropertyInput_UsesCurveProperty()
        {
            var lookup = new LookupCalibration { Direction = LookupDirection.Inverse, Curve = Line() };
            var results = lookup.Process(new[] { 1.1, 2.1, 4.0 }.ToObservable()).ToArray().Wait();
            CollectionAssert.AreEqual(new[] { 20.0, 40.0, 60.0 }, results.Select(r => Math.Round(r.Output, 9)).ToArray());
            CollectionAssert.AreEqual(new[] { false, false, true }, results.Select(r => r.Clamped).ToArray());
        }

        [TestMethod]
        public void Operator_WithoutCurve_Fails()
        {
            var lookup = new LookupCalibration();
            Assert.ThrowsException<InvalidOperationException>(() => lookup.Process(Observable.Return(1.0)).Wait());
        }

        [TestMethod]
        public void FitOperator_AcceptsPairsAndSeparateSequences()
        {
            var fit = new FitCalibrationCurve();
            IEnumerable<Tuple<double, double>> pairs = new[] { Tuple.Create(20.0, 1.1), Tuple.Create(40.0, 2.1), Tuple.Create(60.0, 3.1) };
            var fromPairs = fit.Process(Observable.Return(pairs)).Wait();
            Assert.AreEqual(0.05, fromPairs.Slope, 1e-12);

            var separate = Tuple.Create<IEnumerable<double>, IEnumerable<double>>(new double[] { 20, 40, 60 }, new double[] { 1.1, 2.1, 3.1 });
            var fromSeparate = fit.Process(Observable.Return(separate)).Wait();
            Assert.AreEqual(0.05, fromSeparate.Slope, 1e-12);
        }

        [TestMethod]
        public void FitOperator_HonoursModelAndOrder()
        {
            var fit = new FitCalibrationCurve { Model = CalibrationModel.PolynomialFit, Order = 3 };
            var pairs = Enumerable.Range(0, 6).Select(i => Tuple.Create((double)i, (double)i * i * i)).ToArray();
            var curve = fit.Process(Observable.Return<IEnumerable<Tuple<double, double>>>(pairs)).Wait();
            Assert.AreEqual(CalibrationModel.PolynomialFit, curve.Model);
            Assert.AreEqual(3, curve.Order);
            Assert.AreEqual(15.625, curve.Evaluate(2.5), 1e-8);
        }

        [TestMethod]
        public void ValueWithinNoiseOfAnEnd_SnapsWithoutClamping()
        {
            // Points read as float put the fitted end a few parts per billion off the request.
            var curve = new CalibrationCurveFit(new double[] { 20f, 40f, 60f }, new double[] { 1.1f, 2.1f, 3.1f }, CalibrationModel.LinearFit, 2);
            var low = LookupCalibration.Lookup(curve, 1.1, LookupDirection.Inverse, OutOfRangePolicy.Throw);
            Assert.IsFalse(low.Clamped);
            Assert.AreEqual(curve.MinY, low.EffectiveInput);
            Assert.AreEqual(20, low.Output, 1e-4);
            var high = LookupCalibration.Lookup(curve, 3.1, LookupDirection.Inverse, OutOfRangePolicy.Throw);
            Assert.IsFalse(high.Clamped);
            Assert.AreEqual(60, high.Output, 1e-4);
        }

        [TestMethod]
        public void ValueJustBeyondTolerance_IsClamped()
        {
            var curve = Line();
            var beyond = curve.MaxY + 1e-3;
            var result = LookupCalibration.Lookup(curve, beyond, LookupDirection.Inverse, OutOfRangePolicy.Clamp);
            Assert.IsTrue(result.Clamped);
            Assert.AreEqual(curve.MaxY, result.EffectiveInput);
        }
    }
}
