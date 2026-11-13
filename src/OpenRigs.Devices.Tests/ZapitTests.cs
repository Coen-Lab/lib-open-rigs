using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Devices.Zapit;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class ZapitTests
    {
        const string Conditions =
            "stimModulationFreqHz: 40\n" +
            "stimLocations01:\n  offRampDownDuration_ms: 250\n" +
            "stimLocations02:\n  offRampDownDuration_ms: 250\n";

        static string WriteConditions(string text)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, text);
            return path;
        }

        static ZapitReply Reply(ZapitCommandType command, int value, double datenum = 739000)
        {
            var bytes = new byte[ZapitReply.MessageLength];
            BitConverter.GetBytes(datenum).CopyTo(bytes, 0);
            bytes[8] = (byte)command;
            bytes[9] = (byte)value;
            return ZapitReply.Parse(bytes);
        }

        [TestMethod]
        public void ATrialsCommandSendsItsPowerOnlyWithTheLaserOn()
        {
            var on = ZapitTrial.Command(7, 3, true, 4.5, true, 1.5);
            Assert.AreEqual("StartStimulus(condition=3, laserOn=True, hardwareTriggered=True, duration=1.5, power=4.5, delay=0)", on.ToString());
            Assert.AreEqual(true, on.Logging);
            var off = ZapitTrial.Command(8, 2, false, 4.5, false, 0);
            Assert.AreEqual("StartStimulus(condition=2, laserOn=False, hardwareTriggered=False, duration=0, power=0, delay=0)", off.ToString());
            Assert.AreEqual(0f, BitConverter.ToSingle(off.ToBytes(), 8));
            Assert.AreEqual(64, off.ToBytes()[1] & 64);  // the power is sent, not left to the conditions file
        }

        [DataTestMethod]
        [DataRow(0, 1.0, "Trial 5 has Zapit condition 0")]
        [DataRow(255, 1.0, "Trial 5 has Zapit condition 255")]
        [DataRow(1, -1.0, "Trial 5's Zapit stimulus lasts -1 s")]
        public void ATrialsCommandRefusesWhatZapitCannotPlay(int condition, double duration, string message)
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => ZapitTrial.Command(5, condition, true, 1, false, duration));
            StringAssert.Contains(ex.Message, message);
        }

        [TestMethod]
        public void LineMessagesSetAndClearTheLine()
        {
            var set = ZapitTrial.LineMessage(Harp.Behavior.DigitalOutputs.DO3, true);
            Assert.AreEqual(Harp.Behavior.OutputSet.Address, set.Address);
            Assert.AreEqual(Harp.Behavior.DigitalOutputs.DO3, Harp.Behavior.OutputSet.GetPayload(set));
            var clear = new ZapitLineMessage().Process(Observable.Return(Harp.Behavior.DigitalOutputs.DO3)).Wait();
            Assert.AreEqual(Harp.Behavior.OutputClear.Address, clear.Address);
            Assert.AreEqual(Bonsai.Harp.MessageType.Write, clear.MessageType);
        }

        [TestMethod]
        public void StartStimulusSetsKeysValuesAndFloats()
        {
            var bytes = new ZapitCommand
            {
                Command = ZapitCommandType.StartStimulus,
                ConditionNumber = 3,
                LaserOn = false,
                HardwareTriggered = true,
                LaserPower = 2.5f
            }.ToBytes();
            Assert.AreEqual(16, bytes.Length);
            Assert.AreEqual(1, bytes[0]);
            Assert.AreEqual(1 | 2 | 4 | 64, bytes[1], "keys: condition, laserOn, hardwareTriggered, power");
            Assert.AreEqual(4, bytes[2], "values: only hardwareTriggered is true");
            Assert.AreEqual(3, bytes[3]);
            Assert.AreEqual(2.5f, BitConverter.ToSingle(bytes, 8));
        }

        [TestMethod]
        public void QueriesCarryNoArguments()
        {
            CollectionAssert.AreEqual(new byte[] { 4 }.Concat(new byte[15]).ToArray(), ZapitCommand.Query(ZapitQuery.GetNumConditions).ToBytes());
            CollectionAssert.AreEqual(new byte[16], ZapitCommand.Stop().ToBytes());
        }

        [TestMethod]
        public void ConditionOutsideOneTo254IsRefused()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                new ZapitCommand { Command = ZapitCommandType.StartStimulus, ConditionNumber = 255 }.ToBytes());
        }

        [TestMethod]
        public void ReplyParsesStatusCodesAndErrors()
        {
            var reply = Reply(ZapitCommandType.GetState, 1);
            Assert.AreEqual(ZapitState.Stimulating, reply.State);
            Assert.IsTrue(reply.ServerTime.HasValue);
            Assert.IsFalse(reply.IsError);
            Assert.IsTrue(Reply(ZapitCommandType.GetState, 0, datenum: -1).IsError);
            Assert.IsTrue(Reply(ZapitCommandType.StartStimulus, 255).IsError);
            Assert.IsFalse(Reply(ZapitCommandType.StopStimulus, 1, datenum: 1).ServerTime.HasValue);
        }

        [TestMethod]
        public void ConditionCountMatchesTheFile()
        {
            var path = WriteConditions(Conditions);
            CheckZapitConditionCount.Check(Reply(ZapitCommandType.GetNumConditions, 2), path);
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                CheckZapitConditionCount.Check(Reply(ZapitCommandType.GetNumConditions, 3), path));
            StringAssert.Contains(ex.Message, "Zapit has 3 conditions loaded but the conditions file");
            Assert.ThrowsException<InvalidOperationException>(() =>
                CheckZapitConditionCount.Check(Reply(ZapitCommandType.GetState, 2), path));
            Assert.ThrowsException<InvalidOperationException>(() =>
                CheckZapitConditionCount.Check(Reply(ZapitCommandType.GetNumConditions, 255), path));
        }

        [DataTestMethod]
        [DataRow("stimLocations01:\n  offRampDownDuration_ms: 250\nstimLocations03:\n  offRampDownDuration_ms: 250\n", "exactly 1 to 2")]
        [DataRow("stimLocations01:\n  offRampDownDuration_ms: 50\n", "less than 100 ms")]
        [DataRow("stimLocations01:\n  offRampDownDuration_ms: 600\n", "more than 500 ms")]
        [DataRow("stimModulationFreqHz: 5\nstimLocations01:\n  offRampDownDuration_ms: 200\n", "not longer than one 5 Hz cycle")]
        [DataRow("stimLocations01:\n  laserPowerInMW: 2\n", "has no offRampDownDuration_ms")]
        [DataRow("laserPowerInMW: 2\n", "has no conditions")]
        public void ConditionsFileRefused(string text, string message)
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => ZapitConditions.Check(WriteConditions(text)));
            StringAssert.Contains(ex.Message, message);
        }

        [TestMethod]
        public void MissingConditionsFileNamesIt()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => ZapitConditions.Check(@"Z:\no\such.yml"));
            StringAssert.Contains(ex.Message, "no\\such.yml");
        }

        [TestMethod]
        public void SnapshotKeepsTheText()
        {
            var path = WriteConditions(Conditions);
            StringAssert.Contains(ZapitConditionsSnapshot.Snapshot(path), "stimLocations02:\\n");
        }

        [TestMethod]
        public void ClientSendsEachCommandAndStopsTheStimulusWhenDisposed()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var received = new List<byte>();
            var server = Task.Run(() =>
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    var request = new byte[ZapitCommand.MessageLength];
                    while (true)
                    {
                        var read = 0;
                        while (read < request.Length)
                        {
                            var count = stream.Read(request, read, request.Length - read);
                            if (count <= 0) return;
                            read += count;
                        }
                        lock (received) received.Add(request[0]);
                        var reply = new byte[ZapitReply.MessageLength];
                        BitConverter.GetBytes(739000.0).CopyTo(reply, 0);
                        reply[8] = request[0];
                        reply[9] = 2;
                        stream.Write(reply, 0, reply.Length);
                    }
                }
            });

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var replies = new ZapitClient { Port = port }
                .Process(new[] { ZapitCommand.Query(ZapitQuery.GetNumConditions), ZapitCommand.Query(ZapitQuery.GetState) }.ToObservable())
                .ToList().Wait();
            Assert.IsTrue(server.Wait(5000));
            listener.Stop();

            Assert.AreEqual(2, replies.Count);
            Assert.AreEqual(ZapitCommandType.GetNumConditions, replies[0].Command);
            CollectionAssert.AreEqual(new byte[] { 4, 3, 0 }, received, "the queries, then StopStimulus on disconnect");
        }

        [TestMethod]
        public void ClientWithNoServerSaysSo()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                new ZapitClient { Port = 1 }.Process(Observable.Return(ZapitCommand.Stop())).Wait());
            StringAssert.Contains(ex.Message, "Could not connect to Zapit at 127.0.0.1:1");
        }
    }
}
