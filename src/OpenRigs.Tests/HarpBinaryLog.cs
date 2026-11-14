using System;
using System.Collections.Generic;
using System.IO;
using Bonsai.Harp;

namespace OpenRigs.Tests
{
    /// <summary>
    /// Reads raw Harp binary logs of the kind written by LogHarpDevice.
    /// </summary>
    public static class HarpBinaryLog
    {
        // A Harp frame is laid out as [MessageType][Length][Address][Port][PayloadType], optionally
        // followed by a timestamp, then the payload and a trailing checksum. The length byte counts
        // everything after itself, so the frame occupies Length + 2 bytes.
        const int LengthOffset = 1;
        const int LengthOverhead = 2;
        const int HeaderLength = 5;

        // A timestamp is a UInt32 of whole seconds followed by a UInt16 counting 32 microsecond ticks.
        const int TimestampLength = 6;
        const double MicrosecondTick = 0.000032;

        /// <summary>
        /// Counts the messages in a Harp binary log. Messages are variable length, so the log is
        /// walked frame by frame rather than dividing by a fixed record size.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The log does not divide cleanly into frames, which means it is truncated or corrupt.
        /// </exception>
        public static int CountMessages(string fileName)
        {
            var count = 0;
            foreach (var message in ReadMessages(fileName))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Decodes every message in a Harp binary log.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The log does not divide cleanly into frames, which means it is truncated or corrupt.
        /// </exception>
        public static IEnumerable<HarpLogMessage> ReadMessages(string fileName)
        {
            var data = File.ReadAllBytes(fileName);
            var offset = 0;
            var frameIndex = 0;
            while (offset < data.Length)
            {
                if (offset + LengthOffset >= data.Length)
                {
                    throw new InvalidDataException(
                        $"'{fileName}' ends with a partial Harp frame header at offset {offset}.");
                }

                var frameLength = data[offset + LengthOffset] + LengthOverhead;
                if (frameLength < HeaderLength + 1 || offset + frameLength > data.Length)
                {
                    throw new InvalidDataException(
                        $"'{fileName}' declares a {frameLength} byte frame at offset {offset}, " +
                        $"but only {data.Length - offset} bytes remain.");
                }

                yield return ReadFrame(data, offset, frameLength, frameIndex);
                offset += frameLength;
                frameIndex++;
            }
        }

        static HarpLogMessage ReadFrame(byte[] data, int offset, int frameLength, int frameIndex)
        {
            var rawPayloadType = data[offset + 4];
            var isTimestamped = (rawPayloadType & (int)PayloadType.Timestamp) != 0;

            var payloadStart = offset + HeaderLength;
            double? timestamp = null;
            if (isTimestamped)
            {
                var seconds = BitConverter.ToUInt32(data, payloadStart);
                var ticks = BitConverter.ToUInt16(data, payloadStart + sizeof(uint));
                timestamp = seconds + (ticks * MicrosecondTick);
                payloadStart += TimestampLength;
            }

            // The final byte is the checksum, the truncated sum of every byte before it.
            var checksumOffset = offset + frameLength - 1;
            var sum = 0;
            for (var i = offset; i < checksumOffset; i++)
            {
                sum += data[i];
            }

            var payload = new byte[Math.Max(0, checksumOffset - payloadStart)];
            Array.Copy(data, payloadStart, payload, 0, payload.Length);

            return new HarpLogMessage(
                frameIndex,
                data[offset + 2],
                data[offset + 3],
                (MessageType)data[offset],
                (PayloadType)(rawPayloadType & ~(int)PayloadType.Timestamp),
                isTimestamped,
                timestamp,
                payload,
                frameLength,
                (byte)sum == data[checksumOffset]);
        }
    }

    /// <summary>
    /// A single message decoded from a Harp binary log.
    /// </summary>
    public sealed class HarpLogMessage
    {
        public HarpLogMessage(
            int frameIndex,
            int address,
            int port,
            MessageType messageType,
            PayloadType payloadType,
            bool isTimestamped,
            double? timestamp,
            byte[] payload,
            int frameLength,
            bool checksumValid)
        {
            FrameIndex = frameIndex;
            Address = address;
            Port = port;
            MessageType = messageType;
            PayloadType = payloadType;
            IsTimestamped = isTimestamped;
            Timestamp = timestamp;
            Payload = payload;
            FrameLength = frameLength;
            ChecksumValid = checksumValid;
        }

        /// <summary>
        /// The position of the message within its log file.
        /// </summary>
        public int FrameIndex { get; }

        /// <summary>
        /// The register address the message was published on.
        /// </summary>
        public int Address { get; }

        public int Port { get; }

        public MessageType MessageType { get; }

        /// <summary>
        /// The payload type, with the timestamp flag removed.
        /// </summary>
        public PayloadType PayloadType { get; }

        public bool IsTimestamped { get; }

        /// <summary>
        /// Seconds since the Harp epoch, or null when the message carries no timestamp.
        /// </summary>
        public double? Timestamp { get; }

        public byte[] Payload { get; }

        public int FrameLength { get; }

        /// <summary>
        /// Whether the trailing checksum matches the rest of the frame.
        /// </summary>
        public bool ChecksumValid { get; }

        public string GetPayloadHex()
        {
            return BitConverter.ToString(Payload).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>
        /// The payload read as a little endian integer, using at most the first eight bytes. When the
        /// source was a simulator running with sequence payloads, this is the index of the message.
        /// </summary>
        public long GetPayloadValue()
        {
            var value = 0L;
            for (var i = 0; i < Payload.Length && i < sizeof(long); i++)
            {
                value |= (long)Payload[i] << (i * 8);
            }

            return value;
        }
    }
}
