namespace OpenRigs.Core
{
    /// <summary>
    /// Specifies what a calibration lookup does with a value outside the calibrated range.
    /// </summary>
    public enum OutOfRangePolicy
    {
        /// <summary>
        /// Move the value to the nearest end of the calibrated range and flag the result as clamped.
        /// </summary>
        Clamp,

        /// <summary>
        /// Raise an error naming the value and the calibrated range.
        /// </summary>
        Throw
    }
}
