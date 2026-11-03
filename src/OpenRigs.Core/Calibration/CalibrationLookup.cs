namespace OpenRigs.Core
{
    /// <summary>
    /// The result of looking a value up on a calibration curve.
    /// </summary>
    public class CalibrationLookup
    {
        /// <summary>
        /// Gets or sets the converted value, in the unit of the other axis. For an inverse lookup
        /// on a valve curve this is the pulse width to send to the board.
        /// </summary>
        public double Output { get; set; }

        /// <summary>
        /// Gets or sets the value that was actually looked up, in the unit of the request, after
        /// any clamping. This is the quantity to log as delivered; it differs from the request
        /// only when <see cref="Clamped"/> is true.
        /// </summary>
        public double EffectiveInput { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the request lay outside the calibrated range
        /// and was moved to its nearest end.
        /// </summary>
        public bool Clamped { get; set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format("Output={0}, EffectiveInput={1}, Clamped={2}", Output, EffectiveInput, Clamped);
        }
    }
}
