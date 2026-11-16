using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;

namespace OpenRigs.Audio.Tests
{
    [TestClass]
    public class WaveTests
    {
        // A WAV of the given chunks, each (four-byte id, body), padded to even length as RIFF does.
        static MemoryStream Riff(params Tuple<byte[], byte[]>[] chunks)
        {
            var body = new MemoryStream();
            var writer = new BinaryWriter(body);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            foreach (var chunk in chunks)
            {
                writer.Write(chunk.Item1);
                writer.Write(chunk.Item2.Length);
                writer.Write(chunk.Item2);
                if (chunk.Item2.Length % 2 == 1) writer.Write((byte)0);
            }
            var file = new MemoryStream();
            var head = new BinaryWriter(file);
            head.Write(Encoding.ASCII.GetBytes("RIFF"));
            head.Write((int)body.Length);
            head.Write(body.ToArray());
            file.Position = 0;
            return file;
        }

        static Tuple<byte[], byte[]> Chunk(string id, byte[] body) => Tuple.Create(Encoding.ASCII.GetBytes(id), body);

        static byte[] Fmt(int size = 16)
        {
            var fmt = new MemoryStream();
            var writer = new BinaryWriter(fmt);
            writer.Write((short)1);       // PCM
            writer.Write((short)1);       // mono
            writer.Write(48000);
            writer.Write(96000);
            writer.Write((short)2);
            writer.Write((short)16);
            return fmt.ToArray().Take(size).Concat(new byte[Math.Max(0, size - 16)]).ToArray();
        }

        static byte[] Samples(params short[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

        static float[] Values(Mat sound)
        {
            var values = new float[sound.Cols];
            System.Runtime.InteropServices.Marshal.Copy(sound.Data, values, 0, values.Length);
            return values;
        }

        [TestMethod]
        public void ReadsMonoPcm()
        {
            var sound = Wave.Read(Riff(Chunk("fmt ", Fmt()), Chunk("data", Samples(-32768, 32767))), "test", 48000);
            CollectionAssert.AreEqual(new[] { -1f, 1f }, Values(sound));
        }

        // A chunk id is four bytes, not four UTF-8 characters: bytes that are one UTF-8 character
        // (C3 A9, é) once made the reader take more than four and lose its place.
        [TestMethod]
        public void SkipsAChunkWhoseIdIsNotAscii()
        {
            var odd = Tuple.Create(new byte[] { 0xC3, 0xA9, 0xC3, 0xA9 }, new byte[] { 1, 2, 3 });
            var sound = Wave.Read(Riff(Chunk("fmt ", Fmt()), odd, Chunk("data", Samples(0, 32767))), "test", 48000);
            Assert.AreEqual(2, sound.Cols);
        }

        // A fmt chunk shorter than PCM's 16 bytes has no bit depth: refused saying so, not read
        // from the next chunk's bytes.
        [TestMethod]
        public void AShortFmtChunkIsRefusedSayingSo()
        {
            var error = Assert.ThrowsException<InvalidDataException>(() => Wave.Read(Riff(Chunk("fmt ", Fmt(14)), Chunk("data", Samples(0))), "short.wav", 48000));
            StringAssert.Contains(error.Message, "fmt chunk is 14 bytes");
        }

        // A WAVEFORMATEXTENSIBLE-sized fmt (40 bytes) is skipped to its end.
        [TestMethod]
        public void ALongFmtChunkIsSkippedToItsEnd()
        {
            var sound = Wave.Read(Riff(Chunk("fmt ", Fmt(40)), Chunk("data", Samples(0, 0, 0))), "test", 48000);
            Assert.AreEqual(3, sound.Cols);
        }

        static byte[] FloatFmt()
        {
            var fmt = new MemoryStream();
            var writer = new BinaryWriter(fmt);
            writer.Write((short)3);       // IEEE float
            writer.Write((short)1);
            writer.Write(48000);
            writer.Write(192000);
            writer.Write((short)4);
            writer.Write((short)32);
            return fmt.ToArray();
        }

        // A 32-bit float WAV is read as stored, sample for sample (a task's noise kept as float32).
        [TestMethod]
        public void ReadsMonoFloatAsStored()
        {
            var stored = new[] { -0.75f, 0.000123f, 1.5f };
            var sound = Wave.Read(Riff(Chunk("fmt ", FloatFmt()), Chunk("data", stored.SelectMany(BitConverter.GetBytes).ToArray())), "test", 48000);
            CollectionAssert.AreEqual(stored, Values(sound));
        }
    }
}
