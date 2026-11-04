using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class CalibrationCurveFitTests
    {
        // y = 0.05 x + 0.1: a valve delivering 0.05 ul per ms with a 0.1 ul offset.
        static readonly double[] LineX = { 20, 40, 60 };
        static readonly double[] LineY = { 1.1, 2.1, 3.1 };

        static CalibrationCurveFit Line(CalibrationModel model = CalibrationModel.LinearFit, int order = 2)
        {
            return new CalibrationCurveFit(LineX, LineY, model, order);
        }

        [TestMethod]
        public void LinearFit_RecoversSlopeAndInterceptOfExactLine()
        {
            var curve = Line();
            Assert.AreEqual(0.05, curve.Slope, 1e-12);
            Assert.AreEqual(0.1, curve.Intercept, 1e-12);
            Assert.AreEqual(20, curve.MinX);
            Assert.AreEqual(60, curve.MaxX);
            Assert.AreEqual(1.1, curve.MinY, 1e-12);
            Assert.AreEqual(3.1, curve.MaxY, 1e-12);
            Assert.IsTrue(curve.IsMonotone);
        }

        [TestMethod]
        public void LinearFit_AveragesRepeatedMeasurements()
        {
            // Three repeats per width with symmetric noise; the least-squares line is the noiseless one.
            var x = new double[] { 20, 20, 20, 40, 40, 40, 60, 60, 60 };
            var y = new double[] { 1.0, 1.1, 1.2, 2.0, 2.1, 2.2, 3.0, 3.1, 3.2 };
            var curve = new CalibrationCurveFit(x, y, CalibrationModel.LinearFit, 2);
            Assert.AreEqual(0.05, curve.Slope, 1e-12);
            Assert.AreEqual(0.1, curve.Intercept, 1e-12);
            Assert.AreEqual(9, curve.PointCount);
        }

        [TestMethod]
        public void LinearFit_SolveInvertsEvaluate()
        {
            var curve = Line();
            foreach (var x in new[] { 20.0, 33.3, 47.9, 60.0 })
            {
                Assert.AreEqual(x, curve.Solve(curve.Evaluate(x)), 1e-9);
            }
        }

        [TestMethod]
        public void PolynomialFit_FitsParabolaExactly()
        {
            var x = new double[] { 0, 1, 2, 3, 4 };
            var y = x.Select(v => 2 * v * v + 3 * v + 1).ToArray();
            var curve = new CalibrationCurveFit(x, y, CalibrationModel.PolynomialFit, 2);
            Assert.AreEqual(2 * 2.5 * 2.5 + 3 * 2.5 + 1, curve.Evaluate(2.5), 1e-9);
            Assert.IsTrue(curve.IsMonotone);
            Assert.AreEqual(2.5, curve.Solve(curve.Evaluate(2.5)), 1e-8);
        }

        [TestMethod]
        public void PolynomialFit_NeedsMorePointsThanOrder()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new CalibrationCurveFit(new double[] { 1, 2 }, new double[] { 1, 4 }, CalibrationModel.PolynomialFit, 2));
        }

        [TestMethod]
        public void Linear_ReproducesPointsAndInverts()
        {
            var curve = Line(CalibrationModel.Linear);
            Assert.AreEqual(2.1, curve.Evaluate(40), 1e-12);
            Assert.AreEqual(1.6, curve.Evaluate(30), 1e-12);
            Assert.AreEqual(30, curve.Solve(1.6), 1e-9);
        }

        [TestMethod]
        public void Linear_AveragesDuplicateXBeforeInterpolating()
        {
            var x = new double[] { 20, 20, 40 };
            var y = new double[] { 1.0, 1.2, 2.1 };
            var curve = new CalibrationCurveFit(x, y, CalibrationModel.Linear, 2);
            Assert.AreEqual(1.1, curve.Evaluate(20), 1e-12);
        }

        [TestMethod]
        public void CubicSpline_InvertsWithinRange()
        {
            var x = new double[] { 10, 20, 30, 40, 50 };
            var y = new double[] { 0.5, 1.2, 2.1, 3.3, 4.8 };
            var curve = new CalibrationCurveFit(x, y, CalibrationModel.CubicSpline, 2);
            Assert.IsTrue(curve.IsMonotone);
            foreach (var target in new[] { 0.5, 1.0, 2.5, 4.8 })
            {
                Assert.AreEqual(target, curve.Evaluate(curve.Solve(target)), 1e-8);
            }
        }

        [TestMethod]
        public void Step_SupportsForwardOnly()
        {
            var curve = Line(CalibrationModel.Step);
            Assert.AreEqual(1.1, curve.Evaluate(39.9), 1e-12);
            Assert.AreEqual(2.1, curve.Evaluate(40), 1e-12);
            Assert.ThrowsException<InvalidOperationException>(() => curve.Solve(2.1));
        }

        [TestMethod]
        public void NonMonotoneCurve_CannotBeInverted()
        {
            var x = new double[] { -2, -1, 0, 1, 2 };
            var y = x.Select(v => v * v).ToArray();
            var curve = new CalibrationCurveFit(x, y, CalibrationModel.PolynomialFit, 2);
            Assert.IsFalse(curve.IsMonotone);
            Assert.ThrowsException<InvalidOperationException>(() => curve.Solve(1));
        }

        [TestMethod]
        public void Solve_RejectsValueOutsideRange()
        {
            var curve = Line();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => curve.Solve(5));
        }

        [TestMethod]
        public void Constructor_RejectsBadInput()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new CalibrationCurveFit(new double[] { 1 }, new double[] { 1 }, CalibrationModel.LinearFit, 2));
            Assert.ThrowsException<ArgumentException>(() =>
                new CalibrationCurveFit(new double[] { 1, 2 }, new double[] { 1 }, CalibrationModel.LinearFit, 2));
            Assert.ThrowsException<ArgumentException>(() =>
                new CalibrationCurveFit(new double[] { 3, 3 }, new double[] { 1, 2 }, CalibrationModel.LinearFit, 2));
            Assert.ThrowsException<ArgumentException>(() =>
                new CalibrationCurveFit(new double[] { 1, double.NaN }, new double[] { 1, 2 }, CalibrationModel.LinearFit, 2));
        }

        [TestMethod]
        public void Points_AreSortedByX()
        {
            var curve = new CalibrationCurveFit(new double[] { 60, 20, 40 }, new double[] { 3.1, 1.1, 2.1 }, CalibrationModel.Linear, 2);
            Assert.AreEqual(20, curve.MinX);
            Assert.AreEqual(60, curve.MaxX);
            Assert.AreEqual(1.6, curve.Evaluate(30), 1e-12);
        }
    }
}
