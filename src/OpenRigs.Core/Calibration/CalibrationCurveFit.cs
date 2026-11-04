using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics;
using MathNet.Numerics.Interpolation;
using MathNet.Numerics.RootFinding;

namespace OpenRigs.Core
{
    /// <summary>
    /// A calibration curve derived from measured (x, y) points, keeping the calibrated range so
    /// lookups can be clamped to it. x is the quantity the rig controls, y the quantity measured.
    /// </summary>
    public sealed class CalibrationCurveFit
    {
        const double RootAccuracy = 1e-10;
        const int RootIterations = 200;
        const int MonotoneSamples = 65;

        readonly Func<double, double> evaluate;
        readonly IInterpolation? interpolation;
        readonly double slope;
        readonly double intercept;

        /// <summary>
        /// Initializes a new calibration curve from measured points.
        /// </summary>
        /// <param name="x">The controlled values, one per point.</param>
        /// <param name="y">The measured values, one per point, in the same order.</param>
        /// <param name="model">How the curve is derived from the points.</param>
        /// <param name="order">The polynomial order; used by <see cref="CalibrationModel.PolynomialFit"/> only.</param>
        public CalibrationCurveFit(IEnumerable<double> x, IEnumerable<double> y, CalibrationModel model, int order)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));
            var xs = x.ToArray();
            var ys = y.ToArray();
            if (xs.Length != ys.Length)
            {
                throw new ArgumentException("A calibration curve needs the same number of x and y values.");
            }
            if (xs.Length < 2)
            {
                throw new ArgumentException("A calibration curve needs at least two points.");
            }
            if (xs.Any(double.IsNaN) || ys.Any(double.IsNaN))
            {
                throw new ArgumentException("Calibration points must not contain NaN.");
            }

            Array.Sort(xs, ys);
            Model = model;
            Order = order;
            PointCount = xs.Length;
            MinX = xs[0];
            MaxX = xs[xs.Length - 1];
            if (MinX == MaxX)
            {
                throw new ArgumentException("Calibration points must span a range of x values.");
            }

            switch (model)
            {
                case CalibrationModel.LinearFit:
                    var line = Fit.Line(xs, ys);
                    intercept = line.Item1;
                    slope = line.Item2;
                    var a = intercept;
                    var b = slope;
                    evaluate = t => a + b * t;
                    break;
                case CalibrationModel.PolynomialFit:
                    if (order < 1)
                    {
                        throw new ArgumentOutOfRangeException(nameof(order), order, "The polynomial order must be at least 1.");
                    }
                    if (xs.Length <= order)
                    {
                        throw new ArgumentException(string.Format(
                            "A polynomial fit of order {0} needs more than {0} points; {1} given.", order, xs.Length));
                    }
                    var coefficients = Fit.Polynomial(xs, ys, order);
                    evaluate = t => Polynomial.Evaluate(t, coefficients);
                    break;
                case CalibrationModel.Linear:
                    interpolation = Interpolate.Linear(Distinct(xs, ys, out var linearY), linearY);
                    evaluate = interpolation.Interpolate;
                    break;
                case CalibrationModel.CubicSpline:
                    interpolation = Interpolate.CubicSpline(Distinct(xs, ys, out var splineY), splineY);
                    evaluate = interpolation.Interpolate;
                    break;
                case CalibrationModel.Step:
                    interpolation = Interpolate.Step(Distinct(xs, ys, out var stepY), stepY);
                    evaluate = interpolation.Interpolate;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown calibration model.");
            }

            var yStart = evaluate(MinX);
            var yEnd = evaluate(MaxX);
            MinY = Math.Min(yStart, yEnd);
            MaxY = Math.Max(yStart, yEnd);
            IsMonotone = CheckMonotone(yStart, yEnd);
        }

        /// <summary>
        /// Gets how the curve was derived from the points.
        /// </summary>
        public CalibrationModel Model { get; }

        /// <summary>
        /// Gets the polynomial order given at construction. Meaningful for <see cref="CalibrationModel.PolynomialFit"/> only.
        /// </summary>
        public int Order { get; }

        /// <summary>
        /// Gets the number of measured points the curve was derived from.
        /// </summary>
        public int PointCount { get; }

        /// <summary>
        /// Gets the smallest measured x.
        /// </summary>
        public double MinX { get; }

        /// <summary>
        /// Gets the largest measured x.
        /// </summary>
        public double MaxX { get; }

        /// <summary>
        /// Gets the smaller of the curve's values at the two ends of the x range.
        /// For a fit this is the fitted value, not the smallest measured y.
        /// </summary>
        public double MinY { get; }

        /// <summary>
        /// Gets the larger of the curve's values at the two ends of the x range.
        /// </summary>
        public double MaxY { get; }

        /// <summary>
        /// Gets a value indicating whether the curve is monotone over the calibrated range,
        /// which inverse lookup requires.
        /// </summary>
        public bool IsMonotone { get; }

        /// <summary>
        /// Gets the fitted slope, in y per x. Meaningful for <see cref="CalibrationModel.LinearFit"/> only.
        /// </summary>
        public double Slope { get { return slope; } }

        /// <summary>
        /// Gets the fitted intercept, in y. Meaningful for <see cref="CalibrationModel.LinearFit"/> only.
        /// </summary>
        public double Intercept { get { return intercept; } }

        /// <summary>
        /// Evaluates the curve at the given x. No range check; see <see cref="LookupCalibration"/> for clamping.
        /// </summary>
        public double Evaluate(double x)
        {
            return evaluate(x);
        }

        /// <summary>
        /// Finds the x at which the curve takes the given y. The curve must be monotone and y
        /// must lie within [<see cref="MinY"/>, <see cref="MaxY"/>]; see <see cref="LookupCalibration"/> for clamping.
        /// </summary>
        public double Solve(double y)
        {
            if (!IsMonotone)
            {
                throw new InvalidOperationException(string.Format(
                    "The calibration curve is not monotone over [{0}, {1}], so it cannot be inverted.", MinX, MaxX));
            }
            if (Model == CalibrationModel.Step)
            {
                throw new InvalidOperationException("A step curve supports forward lookup only.");
            }
            if (y < MinY || y > MaxY)
            {
                throw new ArgumentOutOfRangeException(nameof(y), y, string.Format(
                    "The value lies outside the calibrated range [{0}, {1}].", MinY, MaxY));
            }
            if (Model == CalibrationModel.LinearFit)
            {
                return (y - intercept) / slope;
            }

            var target = y;
            if (Brent.TryFindRoot(t => evaluate(t) - target, MinX, MaxX, RootAccuracy, RootIterations, out var root))
            {
                return root;
            }
            if (y == evaluate(MinX)) return MinX;
            if (y == evaluate(MaxX)) return MaxX;
            throw new InvalidOperationException(string.Format(
                "No x in [{0}, {1}] gives y = {2} on the calibration curve.", MinX, MaxX, y));
        }

        bool CheckMonotone(double yStart, double yEnd)
        {
            if (yStart == yEnd) return false;
            var tolerance = 1e-9 * Math.Max(1.0, Math.Abs(yEnd - yStart));
            var increasing = yEnd > yStart;
            var previous = yStart;
            for (int i = 1; i <= MonotoneSamples; i++)
            {
                var t = MinX + (MaxX - MinX) * i / MonotoneSamples;
                var value = evaluate(t);
                var step = value - previous;
                if (increasing ? step < -tolerance : step > tolerance) return false;
                previous = value;
            }
            return true;
        }

        static double[] Distinct(double[] xs, double[] ys, out double[] values)
        {
            // Interpolators need strictly increasing x; average repeated measurements at the same x.
            var points = new List<double>();
            var result = new List<double>();
            var i = 0;
            while (i < xs.Length)
            {
                var j = i;
                var sum = 0.0;
                while (j < xs.Length && xs[j] == xs[i])
                {
                    sum += ys[j];
                    j++;
                }
                points.Add(xs[i]);
                result.Add(sum / (j - i));
                i = j;
            }
            values = result.ToArray();
            return points.ToArray();
        }

        /// <summary>
        /// Describes the fit without assuming its model: the model, the point count, the x range
        /// and the y range it maps to, whether it is monotone, and the line's slope and intercept
        /// when the model is <see cref="CalibrationModel.LinearFit"/>.
        /// </summary>
        public override string ToString()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var text = string.Format(culture,
                "{0} through {1} points: x {2:0.###} to {3:0.###} maps to y {4:0.###} to {5:0.###}, {6}",
                Model, PointCount, MinX, MaxX, MinY, MaxY, IsMonotone ? "monotone" : "NOT monotone");
            if (Model == CalibrationModel.LinearFit)
            {
                text += string.Format(culture, "; slope {0:0.####}, intercept {1:0.####}", Slope, Intercept);
            }
            return text;
        }

    }
}
