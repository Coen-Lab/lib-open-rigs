using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;
using OpenRigs.Core.Calibration;

namespace OpenRigs.Core
{
    /// <summary>
    /// Assembles a calibration artefact from measured (x, y) points: a <see cref="CalibrationCurve"/>
    /// with provenance, ready to serialize. Refuses points that cannot be used by a rig.
    /// </summary>
    /// <remarks>
    /// The artefact stores the points, never a fit. A rig fits them at startup with the model
    /// in its own configuration. The fit here is only the acceptance check: with
    /// <see cref="RequireMonotone"/> set, points whose fitted curve is not monotone in either
    /// direction are refused.
    /// </remarks>
    [Combinator]
    [DefaultProperty("CalibratedBy")]
    [Description("Assembles a calibration artefact from measured (x, y) points: a CalibrationCurve with provenance (time in UTC, operator, machine name, notes), the platform type every rig reads. Input is the full list of points for one channel, for example from DispenseCalibrationProcedure's Done step. Refuses a curve that is not monotone unless RequireMonotone is off. Write it with WriteCalibrationArtefact.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class BuildCalibrationCurve
    {
        /// <summary>Gets or sets who ran the calibration, written into the artefact.</summary>
        [Description("Who ran the calibration, written into the artefact's provenance.")]
        public string CalibratedBy { get; set; } = "";

        /// <summary>Gets or sets free text written into the artefact, such as the balance model.</summary>
        [Description("Free text written into the artefact's provenance, such as the balance model.")]
        public string Notes { get; set; } = "";

        /// <summary>Gets or sets the name of the quantity the rig controls.</summary>
        [Description("Name of the quantity the rig controls, such as pulse_width.")]
        public string XName { get; set; } = "pulse_width";

        /// <summary>Gets or sets the unit of the x values.</summary>
        [Description("Unit of the x values, such as ms.")]
        public string XUnit { get; set; } = "ms";

        /// <summary>Gets or sets the name of the measured quantity.</summary>
        [Description("Name of the measured quantity, such as volume.")]
        public string YName { get; set; } = "volume";

        /// <summary>Gets or sets the unit of the y values.</summary>
        [Description("Unit of the y values, such as ul.")]
        public string YUnit { get; set; } = "ul";

        /// <summary>Gets or sets the model used for the acceptance check.</summary>
        [Description("The model fitted for the acceptance check only. The artefact stores the points. Any model works, the check is that the curve is monotone.")]
        public CalibrationModel Model { get; set; } = CalibrationModel.LinearFit;

        /// <summary>Gets or sets the polynomial order used by <see cref="CalibrationModel.PolynomialFit"/>.</summary>
        [Description("The polynomial order, used by PolynomialFit only.")]
        public int Order { get; set; } = 2;

        /// <summary>Gets or sets a value indicating whether a curve that is not monotone is refused.</summary>
        [Description("Refuse points whose fitted curve is not monotone in either direction, since a rig inverts the curve to look a quantity up. Switch off only for a calibration that is never inverted.")]
        public bool RequireMonotone { get; set; } = true;

        /// <summary>Gets or sets a value indicating whether a point whose measured value is not positive is refused.</summary>
        [Description("Refuse a point whose measured value is zero or negative: a dispense sample that gained nothing means the line, the tubing or the container is wrong. Switch off for a quantity that can legitimately be zero.")]
        public bool RequirePositive { get; set; } = true;

        /// <summary>Builds one artefact per sequence of measured points, for example from SelectCalibrationPoints.</summary>
        public IObservable<CalibrationCurve> Process(IObservable<IEnumerable<Tuple<double, double>>> source)
        {
            return source.Select(points => Build(points.ToList()));
        }

        /// <summary>Builds one artefact per list of measured points.</summary>
        public IObservable<CalibrationCurve> Process(IObservable<IList<Tuple<double, double>>> source)
        {
            return source.Select(points => Build(points));
        }

        /// <summary>Builds one artefact per array of measured points.</summary>
        public IObservable<CalibrationCurve> Process(IObservable<Tuple<double, double>[]> source)
        {
            return source.Select(points => Build(points));
        }

        CalibrationCurve Build(IList<Tuple<double, double>> points)
        {
            return Build(points, CalibratedBy, Notes, XName, XUnit, YName, YUnit, Model, Order, RequireMonotone, RequirePositive, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Builds the artefact, checking the points first.
        /// </summary>
        public static CalibrationCurve Build(
            IList<Tuple<double, double>> points, string calibratedBy, string notes,
            string xName, string xUnit, string yName, string yUnit,
            CalibrationModel model, int order, bool requireMonotone, bool requirePositive, DateTimeOffset calibratedAt)
        {
            if (points == null || points.Count < 2)
            {
                throw new InvalidOperationException("A calibration curve needs at least two measured points; got " + (points == null ? 0 : points.Count) + ".");
            }

            if (requirePositive)
            {
                var empty = points.FirstOrDefault(p => double.IsNaN(p.Item2) || p.Item2 <= 0);
                if (empty != null)
                {
                    throw new InvalidOperationException(
                        "Nothing was measured at " + xName + " " + empty.Item1 + " " + xUnit + " (" + yName + " " + empty.Item2.ToString("0.###") + " " + yUnit +
                        "). For a dispenser, check the line, the tubing and that the container is on the balance. Nothing was written.");
                }
            }

            if (requireMonotone)
            {
                var fit = new CalibrationCurveFit(points.Select(p => p.Item1), points.Select(p => p.Item2), model, order);
                if (!fit.IsMonotone)
                {
                    throw new InvalidOperationException(
                        "The measured points do not give a monotone " + model + " curve. A rig could not turn a " + yName +
                        " into a " + xName + " with it. Nothing was written. Points (" + xName + " " + xUnit + ", " + yName + " " + yUnit + "): " +
                        Describe(points) + ". Check the dispenser and repeat the run.");
                }
            }

            var curve = new CalibrationCurve
            {
                CalibratedAt = calibratedAt,
                CalibratedBy = calibratedBy ?? "",
                MachineName = Environment.MachineName,
                Notes = notes ?? "",
                XName = xName,
                XUnit = xUnit,
                YName = yName,
                YUnit = yUnit,
                Points = points.Select(p => new CalibrationPoint { X = p.Item1, Y = p.Item2 }).ToList()
            };
            return curve;
        }

        static string Describe(IEnumerable<Tuple<double, double>> points)
        {
            return string.Join(", ", points.Select(p => string.Format(CultureInfo.InvariantCulture, "({0}, {1:0.###})", p.Item1, p.Item2)));
        }
    }
}
