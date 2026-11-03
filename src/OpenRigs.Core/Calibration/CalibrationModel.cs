namespace OpenRigs.Core
{
    /// <summary>
    /// Specifies how a calibration curve is derived from its measured points.
    /// </summary>
    public enum CalibrationModel
    {
        /// <summary>
        /// A straight line fitted by least squares through all points. Repeated measurements
        /// at the same x are averaged by the fit. The default for valve calibrations.
        /// </summary>
        LinearFit,

        /// <summary>
        /// A polynomial of the given order fitted by least squares through all points.
        /// </summary>
        PolynomialFit,

        /// <summary>
        /// Piecewise linear interpolation passing through every point.
        /// </summary>
        Linear,

        /// <summary>
        /// A natural cubic spline passing through every point.
        /// </summary>
        CubicSpline,

        /// <summary>
        /// A step function holding each point's value until the next point. Forward lookup only.
        /// </summary>
        Step
    }
}
