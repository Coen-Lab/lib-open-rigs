using System;
using System.Globalization;

namespace OpenRigs.Devices.Zapit
{
    /// <summary>The commands the Zapit TCP server understands; the value is the command byte on the wire.</summary>
    public enum ZapitCommandType : byte
    {
        /// <summary>Stops the stimulus, ramping the laser down.</summary>
        StopStimulus = 0,
        /// <summary>Starts (or arms) a stimulus.</summary>
        StartStimulus = 1,
        /// <summary>Asks whether a conditions file is loaded.</summary>
        IsStimConfigLoaded = 2,
        /// <summary>Asks the pointer's state.</summary>
        GetState = 3,
        /// <summary>Asks how many conditions are loaded.</summary>
        GetNumConditions = 4
    }

    /// <summary>The read-only queries, apart so a query cannot start or stop a stimulus.</summary>
    public enum ZapitQuery : byte
    {
        /// <summary>Answers 1 when a conditions file is loaded, else 0.</summary>
        IsStimConfigLoaded = 2,
        /// <summary>Answers the pointer's state.</summary>
        GetState = 3,
        /// <summary>Answers the number of conditions loaded.</summary>
        GetNumConditions = 4
    }

    /// <summary>The pointer's state, as GetState answers it.</summary>
    public enum ZapitState : byte
    {
        /// <summary>Not stimulating.</summary>
        Idle = 0,
        /// <summary>Stimulating.</summary>
        Stimulating = 1,
        /// <summary>Ramping the laser down.</summary>
        RampDown = 2,
        /// <summary>Not a GetState reply, or a value Zapit does not define.</summary>
        Unknown = 255
    }

    /// <summary>
    /// One request to the Zapit TCP server. Only StartStimulus carries arguments; a null argument is
    /// not sent and Zapit uses its own default. Wire format, 16 bytes little-endian: [0] command,
    /// [1] argument-key bitmask, [2] boolean-value bitmask, [3] condition number, [4..7] stimulus
    /// duration (float32, s), [8..11] laser power (float32, mW), [12..15] start delay (float32, s).
    /// </summary>
    public class ZapitCommand
    {
        /// <summary>The request's length in bytes.</summary>
        public const int MessageLength = 16;

        const byte ConditionNumberBit = 1;
        const byte LaserOnBit = 2;
        const byte HardwareTriggeredBit = 4;
        const byte LoggingBit = 8;
        const byte VerboseBit = 16;
        const byte StimDurationBit = 32;
        const byte LaserPowerBit = 64;
        const byte StartDelayBit = 128;

        /// <summary>Gets or sets the command.</summary>
        public ZapitCommandType Command { get; set; }

        /// <summary>Gets or sets the condition, 1 to 254; null lets Zapit draw one.</summary>
        public int? ConditionNumber { get; set; }

        /// <summary>Gets or sets whether the laser fires; false moves the galvos with the laser off.</summary>
        public bool? LaserOn { get; set; }

        /// <summary>Gets or sets whether the DAQ waits for a TTL trigger; false stimulates on receipt.</summary>
        public bool? HardwareTriggered { get; set; }

        /// <summary>Gets or sets whether Zapit writes its own log of this stimulus.</summary>
        public bool? Logging { get; set; }

        /// <summary>Gets or sets Zapit's debug printing.</summary>
        public bool? Verbose { get; set; }

        /// <summary>Gets or sets the stimulus duration, in seconds; null or zero plays until StopStimulus.</summary>
        public float? StimDuration { get; set; }

        /// <summary>Gets or sets the laser power, in mW; null uses the conditions file's.</summary>
        public float? LaserPower { get; set; }

        /// <summary>Gets or sets the delay before onset, in seconds; Zapit honours it only with a duration.</summary>
        public float? StartDelay { get; set; }

        /// <summary>A StopStimulus command.</summary>
        public static ZapitCommand Stop()
        {
            return new ZapitCommand { Command = ZapitCommandType.StopStimulus };
        }

        /// <summary>A read-only query.</summary>
        public static ZapitCommand Query(ZapitQuery query)
        {
            return new ZapitCommand { Command = (ZapitCommandType)(byte)query };
        }

        /// <summary>The 16-byte request.</summary>
        public byte[] ToBytes()
        {
            var message = new byte[MessageLength];
            message[0] = (byte)Command;
            if (Command != ZapitCommandType.StartStimulus) return message;

            byte keys = 0, values = 0;
            if (ConditionNumber.HasValue)
            {
                var condition = ConditionNumber.Value;
                if (condition < 1 || condition > 254)
                {
                    throw new ArgumentOutOfRangeException(nameof(ConditionNumber),
                        string.Format("Zapit condition numbers run from 1 to 254; got {0}.", condition));
                }
                keys |= ConditionNumberBit;
                message[3] = (byte)condition;
            }
            Flag(LaserOn, LaserOnBit, ref keys, ref values);
            Flag(HardwareTriggered, HardwareTriggeredBit, ref keys, ref values);
            Flag(Logging, LoggingBit, ref keys, ref values);
            Flag(Verbose, VerboseBit, ref keys, ref values);
            Single(StimDuration, StimDurationBit, 4, message, ref keys);
            Single(LaserPower, LaserPowerBit, 8, message, ref keys);
            Single(StartDelay, StartDelayBit, 12, message, ref keys);
            message[1] = keys;
            message[2] = values;
            return message;
        }

        static void Flag(bool? value, byte bit, ref byte keys, ref byte values)
        {
            if (!value.HasValue) return;
            keys |= bit;
            if (value.Value) values |= bit;
        }

        static void Single(float? value, byte bit, int offset, byte[] message, ref byte keys)
        {
            if (!value.HasValue) return;
            keys |= bit;
            var bytes = BitConverter.GetBytes(value.Value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Array.Copy(bytes, 0, message, offset, bytes.Length);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            if (Command != ZapitCommandType.StartStimulus) return Command.ToString();
            return string.Format(CultureInfo.InvariantCulture,
                "StartStimulus(condition={0}, laserOn={1}, hardwareTriggered={2}, duration={3}, power={4}, delay={5})",
                Text(ConditionNumber), Text(LaserOn), Text(HardwareTriggered), Text(StimDuration), Text(LaserPower), Text(StartDelay));
        }

        static string Text(object value)
        {
            return value == null ? "default" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
