namespace OpenRigs.Core
{
    /// <summary>
    /// Specifies the direction of a calibration lookup.
    /// </summary>
    public enum LookupDirection
    {
        /// <summary>
        /// From the quantity the rig controls to the measured quantity, x to y.
        /// For a valve, pulse width to volume.
        /// </summary>
        Forward,

        /// <summary>
        /// From the measured quantity to the quantity the rig controls, y to x.
        /// For a valve, requested volume to pulse width.
        /// </summary>
        Inverse
    }
}
