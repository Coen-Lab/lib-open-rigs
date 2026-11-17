using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using Bonsai.Harp;
using Harp.Behavior;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Control;
using OpenRigs.Core;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class RewardValveTests
    {
        static ushort Value(HarpMessage message)
        {
            return message.GetPayloadUInt16();
        }

        // A calibrated pulse on any line the board can pulse: its width register, then the line set.
        [DataTestMethod]
        [DataRow("DO0", 56, (ushort)0x400)]
        [DataRow("DO1", 57, (ushort)0x800)]
        [DataRow("DO3", 59, (ushort)0x2000)]
        [DataRow("DOPort0", 46, (ushort)1)]
        [DataRow("DOPort2", 48, (ushort)4)]
        public void APulseWritesItsWidthThenSetsItsLine(string line, int widthRegister, ushort mask)
        {
            var writes = CalibratedPulseWrites.Writes(line, 38);
            Assert.AreEqual(2, writes.Length);
            Assert.AreEqual(widthRegister, writes[0].Address);
            Assert.AreEqual((ushort)38, Value(writes[0]));
            Assert.AreEqual(34, writes[1].Address);
            Assert.AreEqual(mask, Value(writes[1]));
            Assert.AreEqual(mask, (ushort)(DigitalOutputs)Enum.Parse(typeof(DigitalOutputs), line), "the bit is Harp.Behavior's own");
        }

        [DataTestMethod]
        [DataRow("DI3", 10, "no line a Behavior board can pulse")]
        [DataRow("DO4", 10, "no line a Behavior board can pulse")]
        [DataRow("DO1", 0, "a pulse is 1 to 65535 ms")]
        public void APulseOffThePulsedLinesOrWidthsStops(string line, int width, string message)
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() => CalibratedPulseWrites.Writes(line, width));
            StringAssert.Contains(ex.Message, message);
        }

        [TestMethod]
        public void SetupClosesThenPulseEnablesTheLines()
        {
            var writes = new PulseSetupWrites().Process(Observable.Return(DigitalOutputs.DO1 | DigitalOutputs.DOPort1)).ToArray().Wait();
            CollectionAssert.AreEqual(new[] { 35, 45 }, writes.Select(w => w.Address).ToArray());
            Assert.AreEqual((ushort)0x3802, Value(writes[0]), "DO1 to DO3 closed as before, the ZapitTrigger's among them, and the port line");
            Assert.AreEqual((ushort)0x802, Value(writes[1]));
        }

        static RewardLine Line(string instance)
        {
            return new RewardLine { Piece = "RewardValve-" + instance, Instance = instance.ToLowerInvariant() };
        }

        [TestMethod]
        public void AValveTakesOnlyItsOwnRewards()
        {
            var left = Line("Left");
            var requests = new[] { Tuple.Create("left", 2.0), Tuple.Create("right", 3.0), Tuple.Create("Left", 4.0) };
            var taken = new RewardForValve().Process(requests.Select(r => Tuple.Create(r, left)).ToObservable()).ToArray().Wait();
            CollectionAssert.AreEqual(new[] { 2.0, 4.0 }, taken.Select(t => t.Item1).ToArray());
            Assert.IsTrue(taken.All(t => t.Item2 == left));
        }

        [TestMethod]
        public void ARewardForAValveNoPieceFiresStops()
        {
            var valves = new ReplaySubject<string>();
            valves.OnNext("left");
            valves.OnNext("right");
            var requests = new Subject<Tuple<string, double>>();
            var passed = 0;
            Exception error = null;
            using (new RequireRewardValve().Process(requests, valves).Subscribe(_ => passed++, e => error = e))
            {
                requests.OnNext(Tuple.Create("RIGHT", 1.0));
                requests.OnNext(Tuple.Create("centre", 1.0));
            }
            Assert.AreEqual(1, passed);
            StringAssert.Contains(error.Message, "A reward from the valve 'centre', which no RewardValve piece in this workflow fires; its valves are left, right.");
        }

        // The manual drop is the valve's own command, RewardValve-Left.Reward: declared with DeclareDeviceControls and
        // heard with OnDeviceCommand, by the one externalized Name.
        [TestMethod]
        public void TheValveDeclaresTheCommandItHears()
        {
            string text;
            using (var stream = typeof(LoadRewardLine).Assembly.GetManifestResourceStream("OpenRigs.Devices.RewardValve.bonsai")
                ?? typeof(RewardForValve).Assembly.GetManifestResourceStream("OpenRigs.Devices.RewardValve.bonsai"))
            using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(text))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            var elements = builder.Workflow.Select(n => ExpressionBuilder.GetWorkflowElement(n.Value)).ToList();
            var declared = elements.OfType<DeclareDeviceControls>().Single();
            var heard = elements.OfType<OnDeviceCommand>().Single();
            CollectionAssert.AreEqual(new[] { heard.Command }, declared.Commands.Select(c => c.Name).ToArray());
            Assert.IsTrue(declared.Commands.All(c => !string.IsNullOrEmpty(c.Description)));
            Assert.AreEqual(0, declared.Parameters.Count);
        }
    }
}
