using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Core
{
    /// <summary>
    /// Derives a calibration curve from measured (x, y) points, for example the pulse width and
    /// delivered volume pairs in a valve calibration artefact.
    /// </summary>
    [Combinator]
    [DefaultProperty("Model")]
    [Description("Derives a calibration curve from measured (x, y) points, where x is the quantity the rig controls and y the quantity measured. Input is a sequence of (x, y) pairs, for example from SelectCalibrationPoints, or a Tuple of the x values and the y values. The curve keeps the calibrated range so LookupCalibration can clamp to it.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class FitCalibrationCurve
    {
        /// <summary>
        /// Gets or sets how the curve is derived from the points.
        /// </summary>
        [Description("How the curve is derived from the points. LinearFit, a least-squares line, suits repeated noisy measurements such as valve weighings.")]
        public CalibrationModel Model { get; set; } = CalibrationModel.LinearFit;

        /// <summary>
        /// Gets or sets the polynomial order used by <see cref="CalibrationModel.PolynomialFit"/>.
        /// </summary>
        [Description("The polynomial order. Used by PolynomialFit only; there must be more points than the order.")]
        public int Order { get; set; } = 2;

        /// <summary>
        /// Derives a curve from each sequence of (x, y) pairs.
        /// </summary>
        public IObservable<CalibrationCurveFit> Process(IObservable<IEnumerable<Tuple<double, double>>> source)
        {
            return source.Select(points =>
            {
                var pairs = points.ToArray();
                return new CalibrationCurveFit(
                    pairs.Select(p => p.Item1),
                    pairs.Select(p => p.Item2),
                    Model,
                    Order);
            });
        }

        /// <summary>
        /// Derives a curve from each pair of x values and y values.
        /// </summary>
        public IObservable<CalibrationCurveFit> Process(IObservable<Tuple<IEnumerable<double>, IEnumerable<double>>> source)
        {
            return source.Select(pair => new CalibrationCurveFit(pair.Item1, pair.Item2, Model, Order));
        }

        /// <summary>
        /// Derives a curve from the points of each calibration artefact.
        /// </summary>
        public IObservable<CalibrationCurveFit> Process(IObservable<Calibration.CalibrationCurve> source)
        {
            return source.Select(curve =>
            {
                if (curve == null || curve.Points == null || curve.Points.Count < 2)
                {
                    throw new InvalidOperationException("The calibration curve needs at least two points to fit.");
                }
                return new CalibrationCurveFit(
                    curve.Points.Select(point => point.X),
                    curve.Points.Select(point => point.Y),
                    Model,
                    Order);
            });
        }
    }
}
