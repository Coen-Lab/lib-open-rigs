using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenRigs.Devices.Tests
{
    /// <summary>FlushPicoHarp against a board whose parser is core.pico's process_cdc_input: no resync, no timeout.</summary>
    [TestClass]
    public class FlushPicoHarpTests
    {
        static readonly byte[] Heartbeat = { 0x03, 0x0E, 0x08, 0xFF, 0x14, 0x4D, 0x8C, 0x2A, 0xE7, 0x00, 0x73, 0x4D, 0x8C, 0x2A, 0xE7, 0x73 };

        // Each write arrives as one read of the core's: with fewer than a header's 5 bytes buffered it takes all of it
        // and drops what follows a completed frame; with a header in, it takes only the rest of that frame.
        // The frame size is a byte, as msg_size() is uint8_t. Replies to a WhoAmI read; never, when dead.
        sealed class PicoBoard : Stream
        {
            readonly List<byte> frame = new List<byte>();
            readonly Queue<byte> output = new Queue<byte>();
            public bool Dead;
            public int ZerosWritten;

            public PicoBoard(params byte[] pending) { Feed(pending); }
            public void Send(byte[] bytes) { foreach (var b in bytes) output.Enqueue(b); }

            void Feed(byte[] data)
            {
                var i = 0;
                while (i < data.Length)
                {
                    if (frame.Count < 5) { frame.AddRange(data.Skip(i)); i = data.Length; }
                    else { var take = Math.Min(data.Length - i, (byte)(frame[1] + 2) - frame.Count); frame.AddRange(data.Skip(i).Take(take)); i += take; }
                    if (frame.Count >= 5 && frame.Count >= (byte)(frame[1] + 2))
                    {
                        if (!Dead && frame[0] == 0x01 && frame[2] == 0x00)
                            Send(new byte[] { 0x01, 0x0C, 0x00, 0xFF, 0x12, 0xA1, 0x8E, 0x2A, 0xE7, 0x2B, 0x5B, 0x78, 0x05, 0x61 });
                        frame.Clear();
                    }
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                var data = buffer.Skip(offset).Take(count).ToArray();
                if (data.All(b => b == 0)) ZerosWritten += count;
                Feed(data);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = 0;
                while (n < count && output.Count > 0) buffer[offset + n++] = output.Dequeue();
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        [TestMethod]
        public void AHealthyBoardAnswersAndGetsNoZeros()
        {
            var board = new PicoBoard();
            Assert.AreEqual(FlushPicoHarp.Outcome.Answered, FlushPicoHarp.Recover(board));
            Assert.AreEqual(0, board.ZerosWritten);
        }

        [TestMethod]
        public void AStuckBoardAnswersOnlyAfterTheFlush()
        {
            var board = new PicoBoard(0x00, 0xC8, 0x00, 0x00, 0x00); // type 0, length 200: the incident's reproduction
            Assert.AreEqual(FlushPicoHarp.Outcome.Flushed, FlushPicoHarp.Recover(board));
        }

        [TestMethod]
        public void ABoardStuckOnTheLongestFrameAnswersAfterTheFlush()
        {
            var board = new PicoBoard(0x00, 0xFD, 0x00, 0x00, 0x00); // length 253: 255 bytes, the most msg_size() allows
            Assert.AreEqual(FlushPicoHarp.Outcome.Flushed, FlushPicoHarp.Recover(board));
        }

        [TestMethod]
        public void HeartbeatsAreNotAnAnswer()
        {
            var board = new PicoBoard { Dead = true };
            board.Send(Heartbeat);
            board.Send(Heartbeat);
            Assert.AreEqual(FlushPicoHarp.Outcome.Silent, FlushPicoHarp.Recover(board));
        }

        [TestMethod]
        public void AHeartbeatBeforeTheReplyIsSkipped()
        {
            var board = new PicoBoard();
            board.Send(Heartbeat);
            Assert.AreEqual(FlushPicoHarp.Outcome.Answered, FlushPicoHarp.Recover(board));
        }

        [TestMethod]
        public void AMissingPortStopsTheWorkflowAsTheDeviceWould()
        {
            var error = Assert.ThrowsException<IOException>(() => FlushPicoHarp.Flush("LicketySplit-Left", "COM97"));
            StringAssert.Contains(error.Message, "COM97");
        }
    }
}
