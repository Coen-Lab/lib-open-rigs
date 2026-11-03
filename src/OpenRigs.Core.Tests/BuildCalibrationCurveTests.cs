using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class BuildCalibrationCurveTests
    {
        static readonly DateTimeOffset When = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        static CalibrationCurve Build(IList<Tuple<double, double>> points, CalibrationModel model = CalibrationModel.LinearFit, bool requireMonotone = true, bool requirePositive = true)
        {
            return BuildCalibrationCurve.Build(points, "Gerion", "Ohaus PX223", "pulse_width", "ms", "volume", "ul", model, 2, requireMonotone, requirePositive, When);
        }

        [TestMethod]
        public void RefusesAPointWhereNothingWasMeasured()
        {
            try
            {
                Build(Points(20, 0.0, 40, 2.0, 60, 3.0));
                Assert.Fail("expected a refusal");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "Nothing was measured at pulse_width 20 ms");
            }
            Assert.AreEqual(3, Build(Points(20, 0.0, 40, 2.0, 60, 3.0), CalibrationModel.Linear, requirePositive: false).Points.Count);
        }

        static IList<Tuple<double, double>> Points(params double[] xy)
        {
            var list = new List<Tuple<double, double>>();
            for (int i = 0; i < xy.Length; i += 2) list.Add(Tuple.Create(xy[i], xy[i + 1]));
            return list;
        }

        [TestMethod]
        public void CarriesTheProvenanceAxesAndPoints()
        {
            var curve = Build(Points(20, 1.1, 40, 2.1, 60, 3.1));
            Assert.AreEqual(When, curve.CalibratedAt);
            Assert.AreEqual("Gerion", curve.CalibratedBy);
            Assert.AreEqual(Environment.MachineName, curve.MachineName);
            Assert.AreEqual("Ohaus PX223", curve.Notes);
            Assert.AreEqual("pulse_width", curve.XName);
            Assert.AreEqual("ul", curve.YUnit);
            Assert.AreEqual(3, curve.Points.Count);
            Assert.AreEqual(2.1, curve.Points[1].Y);
            Assert.IsNull(curve.PayloadPath);
        }

        [TestMethod]
        public void ADecreasingCurveIsMonotoneToo()
        {
            var curve = Build(Points(20, 3.0, 40, 2.0, 60, 1.0));
            Assert.AreEqual(3, curve.Points.Count);
        }

        [TestMethod]
        public void AnyModelCanDoTheAcceptanceCheck()
        {
            var points = Points(20, 1.1, 40, 2.1, 60, 3.1, 80, 4.0);
            foreach (var model in new[] { CalibrationModel.Linear, CalibrationModel.CubicSpline, CalibrationModel.PolynomialFit })
            {
                Assert.AreEqual(4, Build(points, model).Points.Count, model.ToString());
            }
        }

        [TestMethod]
        public void RefusesANonMonotoneCurveAndListsThePoints()
        {
            try
            {
                Build(Points(20, 1.0, 40, 3.0, 60, 2.0), CalibrationModel.Linear);
                Assert.Fail("expected a refusal");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "not give a monotone");
                StringAssert.Contains(ex.Message, "(40, 3)");
                StringAssert.Contains(ex.Message, "Nothing was written");
            }
        }

        [TestMethod]
        public void TheMonotoneCheckCanBeSwitchedOff()
        {
            var curve = Build(Points(20, 1.0, 40, 3.0, 60, 2.0), CalibrationModel.Linear, requireMonotone: false);
            Assert.AreEqual(3, curve.Points.Count);
        }

        [TestMethod]
        public void NeedsTwoPoints()
        {
            try
            {
                Build(Points(20, 1.0));
                Assert.Fail("expected a refusal");
            }
            catch (InvalidOperationException ex)
            {
                StringAssert.Contains(ex.Message, "at least two");
            }
        }

        [TestMethod]
        public void FitCalibrationCurveTakesTheArtefactDirectly()
        {
            var curve = Build(Points(20, 1.1, 40, 2.1, 60, 3.1));
            var fit = new FitCalibrationCurve().Process(Observable.Return(curve)).Wait();
            Assert.AreEqual(3, fit.PointCount);
            Assert.AreEqual(0.05, fit.Slope, 1e-9);
            StringAssert.Contains(fit.ToString(), "LinearFit through 3 points");
            StringAssert.Contains(fit.ToString(), "monotone");
        }
    }
}
