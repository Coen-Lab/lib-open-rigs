using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;

namespace OpenRigs.Core
{
    /// <summary>
    /// Looks each value up on a calibration curve, forward (x to y) or inverse (y to x),
    /// clamping to the calibrated range or failing outside it.
    /// </summary>
    [Combinator]
    [DefaultProperty("Direction")]
    [Description("Looks each value up on a calibration curve. Forward converts the quantity the rig controls to the measured one (pulse width to volume); Inverse converts the other way (requested volume to pulse width). Outside the calibrated range the value is clamped to the nearest end and the result says so, or an error is raised, per OutOfRange. Input is the value alone, with the curve mapped onto the Curve property, or a Tuple of the value and the curve, for example from WithLatestFrom.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class LookupCalibration
    {
        /// <summary>
        /// The fraction of the calibrated range within which a value just outside an end is
        /// treated as numerical noise: it snaps to the end and is not reported as clamped.
        /// </summary>
        public const double RangeTolerance = 1e-6;

        /// <summary>
        /// Gets or sets the direction of the lookup.
        /// </summary>
        [Description("Forward: x to y, the controlled quantity to the measured one. Inverse: y to x, the measured quantity to the controlled one.")]
        public LookupDirection Direction { get; set; } = LookupDirection.Forward;

        /// <summary>
        /// Gets or sets what happens to a value outside the calibrated range.
        /// </summary>
        [Description("Clamp moves an out-of-range value to the nearest end of the calibrated range and flags the result; Throw raises an error instead.")]
        public OutOfRangePolicy OutOfRange { get; set; } = OutOfRangePolicy.Clamp;

        /// <summary>
        /// Gets or sets the curve to look values up on. Set by property mapping from a fitted curve;
        /// ignored when the input pairs each value with its curve.
        /// </summary>
        [XmlIgnore]
        [Description("The calibration curve to look values up on, mapped from the output of FitCalibrationCurve. Ignored when the input is a Tuple of value and curve.")]
        public CalibrationCurveFit? Curve { get; set; }

        /// <summary>
        /// Looks each value up on the curve in the <see cref="Curve"/> property.
        /// </summary>
        public IObservable<CalibrationLookup> Process(IObservable<double> source)
        {
            return source.Select(value =>
            {
                var curve = Curve;
                if (curve == null)
                {
                    throw new InvalidOperationException(
                        "No calibration curve is set. Map the Curve property from FitCalibrationCurve, or pair each value with its curve.");
                }
                return Lookup(curve, value, Direction, OutOfRange);
            });
        }

        /// <summary>
        /// Looks each value up on the curve it is paired with, for example from WithLatestFrom or Zip.
        /// </summary>
        public IObservable<CalibrationLookup> Process(IObservable<Tuple<double, CalibrationCurveFit>> source)
        {
            return source.Select(pair => Lookup(pair.Item2, pair.Item1, Direction, OutOfRange));
        }

        /// <summary>
        /// Looks a value up on a curve.
        /// </summary>
        public static CalibrationLookup Lookup(CalibrationCurveFit curve, double value, LookupDirection direction, OutOfRangePolicy outOfRange)
        {
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            if (double.IsNaN(value)) throw new ArgumentException("The value to look up is NaN.", nameof(value));

            double min, max;
            string axis;
            if (direction == LookupDirection.Forward)
            {
                min = curve.MinX;
                max = curve.MaxX;
                axis = "x";
            }
            else
            {
                min = curve.MinY;
                max = curve.MaxY;
                axis = "y";
            }

            // Values within numerical noise of an end, for example from points stored as float,
            // snap to that end without counting as clamped.
            var tolerance = RangeTolerance * (max - min);
            var effective = value;
            var clamped = false;
            if (value < min && value >= min - tolerance)
            {
                effective = min;
            }
            else if (value > max && value <= max + tolerance)
            {
                effective = max;
            }
            else if (value < min || value > max)
            {
                if (outOfRange == OutOfRangePolicy.Throw)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, string.Format(
                        "The {0} value {1} lies outside the calibrated range [{2}, {3}].", axis, value, min, max));
                }
                effective = Math.Min(Math.Max(value, min), max);
                clamped = true;
            }

            var output = direction == LookupDirection.Forward ? curve.Evaluate(effective) : curve.Solve(effective);
            return new CalibrationLookup { Output = output, EffectiveInput = effective, Clamped = clamped };
        }
    }
}
