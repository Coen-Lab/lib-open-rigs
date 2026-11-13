using System;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>
    /// One 15-byte reply from the Zapit TCP server. Wire format: [0..7] MATLAB datenum of the reply
    /// as a double (1 or -1 are connection status codes, not times), [8] the command byte echoed,
    /// [9] the result (the condition presented for StartStimulus, else the query's answer), [10] the
    /// laser state for StartStimulus, [11..14] unused. 255 in a result byte means an error.
    /// </summary>
    public class ZapitReply
    {
        /// <summary>The reply's length in bytes.</summary>
        public const int MessageLength = 15;

        const byte ErrorSentinel = 255;
        const double OleAutomationEpochDatenum = 693960.0; // datenum(1899,12,30)

        /// <summary>Gets or sets the raw MATLAB datenum, kept for logging.</summary>
        public double ServerDatenum { get; set; }

        /// <summary>Gets or sets the Zapit PC's clock at the reply; null when the datenum was a status code.</summary>
        public DateTime? ServerTime { get; set; }

        /// <summary>Gets or sets the command this reply answers.</summary>
        public ZapitCommandType Command { get; set; }

        /// <summary>Gets the command's wire byte, for logging without enum names.</summary>
        public int CommandCode { get { return (int)Command; } }

        /// <summary>Gets or sets the result: the condition presented, or the query's answer; 255 on error.</summary>
        public int Value { get; set; }

        /// <summary>Gets or sets the laser state for StartStimulus: 1 on, 0 off, 255 otherwise.</summary>
        public int LaserOn { get; set; }

        /// <summary>Gets or sets whether Zapit flagged the request as failed.</summary>
        public bool IsError { get; set; }

        /// <summary>Gets the state for a GetState reply; Unknown for anything else.</summary>
        public ZapitState State
        {
            get
            {
                if (Command != ZapitCommandType.GetState || Value > 2) return ZapitState.Unknown;
                return (ZapitState)(byte)Value;
            }
        }

        /// <summary>Parses a 15-byte reply.</summary>
        public static ZapitReply Parse(byte[] buffer)
        {
            if (buffer == null || buffer.Length < MessageLength)
            {
                throw new ArgumentException(string.Format("A Zapit reply is {0} bytes.", MessageLength), nameof(buffer));
            }
            var datenum = new byte[8];
            Array.Copy(buffer, 0, datenum, 0, 8);
            if (!BitConverter.IsLittleEndian) Array.Reverse(datenum);
            var reply = new ZapitReply
            {
                ServerDatenum = BitConverter.ToDouble(datenum, 0),
                Command = (ZapitCommandType)buffer[8],
                Value = buffer[9],
                LaserOn = buffer[10]
            };
            if (reply.ServerDatenum > OleAutomationEpochDatenum)
            {
                reply.ServerTime = DateTime.FromOADate(reply.ServerDatenum - OleAutomationEpochDatenum);
            }
            reply.IsError = reply.ServerDatenum < 0 || reply.Value == ErrorSentinel;
            return reply;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return string.Format("ZapitReply({0}, value={1}, laserOn={2}, error={3}, serverTime={4})",
                Command, Value, LaserOn, IsError,
                ServerTime.HasValue ? ServerTime.Value.ToString("HH:mm:ss.fff") : "n/a");
        }
    }
}
