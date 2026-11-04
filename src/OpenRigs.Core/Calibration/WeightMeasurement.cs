using System.Globalization;

namespace OpenRigs.Core
{
    /// <summary>
    /// One reading from a balance: when it was read, the weight, and how far the balance
    /// considers it settled.
    /// </summary>
    /// <remarks>
    /// The same shape as <c>Aeon.Environment.WeightMeasurement</c> in Project Aeon's acquisition
    /// library, kept here because that package pulls in the whole Aeon environment. A confidence
    /// of 1 means the balance reported the reading as stable, and 0 that it was still moving.
    /// </remarks>
    public struct WeightMeasurement
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WeightMeasurement"/> structure.
        /// </summary>
        /// <param name="timestamp">The time of the reading, in seconds.</param>
        /// <param name="value">The weight, in grams.</param>
        /// <param name="confidence">1 for a reading the balance reported as stable, 0 otherwise.</param>
        public WeightMeasurement(double timestamp, float value, float confidence)
        {
            Timestamp = timestamp;
            Value = value;
            Confidence = confidence;
        }

        /// <summary>
        /// Gets the time of the reading, in seconds.
        /// </summary>
        public double Timestamp { get; private set; }

        /// <summary>
        /// Gets the weight, in grams.
        /// </summary>
        public float Value { get; private set; }

        /// <summary>
        /// Gets the confidence in the reading: 1 when the balance reported it as stable, 0 while it was moving.
        /// </summary>
        public float Confidence { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the balance reported the reading as stable.
        /// </summary>
        public bool IsStable
        {
            get { return Confidence >= 1f; }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "WeightMeasurement(Seconds: {0}, Value: {1}, Confidence: {2})",
                Timestamp, Value, Confidence);
        }
    }
}
