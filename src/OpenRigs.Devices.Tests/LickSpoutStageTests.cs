using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Bonsai.Harp;
using Harp.StepperDriver;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Devices.Tests
{
    [TestClass]
    public class LickSpoutStageTests
    {
        static string TempFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            return folder;
        }

        static string RigFile(string folder)
        {
            var path = Path.Combine(folder, "rig.yml");
            File.WriteAllText(path,
                "rootPath: x\nlickSpoutDefaultPosition:\n  baseTransverse: 10\n  leftRadial: 2000\n  rightRadial: 2100\n  leftElevation: 3000\n  rightElevation: 3100\n");
            return path;
        }

        [TestMethod]
        public void MouseWithNoFileStartsFromRigYmlAndSavesItsOwn()
        {
            var folder = TempFolder();
            var file = Path.Combine(folder, "LickSpoutPositions", "M1.csv");
            var state = LickSpoutStage.Start(RigFile(folder), file);
            Assert.AreEqual("10,2000,2100,3000,3100", state.In.ToString());

            Assert.IsNull(state.Apply(LickSpoutCommand.LeftUp, 150, 1500), "nudges do not move a stage at home");
            Assert.IsNull(state.Apply(LickSpoutCommand.SaveSpoutPositions, 150, 1500));
            Assert.AreEqual("10,2000,2100,3150,3100", File.ReadAllText(file).Trim());
            Assert.AreEqual("10,2000,2100,3150,3100", LickSpoutStage.Start(null, file).In.ToString(), "the file wins over rig.yml");
        }

        [TestMethod]
        public void PlacementsRetractTheOtherSpout()
        {
            var state = LickSpoutStage.Start(RigFile(TempFolder()), Path.Combine(TempFolder(), "none.csv"));
            Assert.AreEqual("0,0,0,0,0", state.Apply(LickSpoutCommand.SpoutsHome, 150, 1500).ToString());
            Assert.AreEqual("10,2000,2100,3000,3100", state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500).ToString());
            Assert.AreEqual("10,500,600,1500,1600", state.Apply(LickSpoutCommand.SpoutsOut, 150, 1500).ToString());
            Assert.AreEqual("10,2000,600,3000,1600", state.Apply(LickSpoutCommand.LeftIn, 150, 1500).ToString());
            Assert.AreEqual("10,500,2100,1500,3100", state.Apply(LickSpoutCommand.RightIn, 150, 1500).ToString());
        }

        [TestMethod]
        public void NudgesMoveTheCurrentPlacement()
        {
            var state = LickSpoutStage.Start(RigFile(TempFolder()), Path.Combine(TempFolder(), "none.csv"));
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            var moves = new[]
            {
                LickSpoutCommand.BaseLeft, LickSpoutCommand.LeftFront, LickSpoutCommand.RightBack,
                LickSpoutCommand.LeftDown, LickSpoutCommand.RightUp
            }.Select(command => state.Apply(command, 10, 1500).ToString()).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "20,2000,2100,3000,3100", "20,2010,2100,3000,3100", "20,2010,2090,3000,3100",
                "20,2010,2090,2990,3100", "20,2010,2090,2990,3110"
            }, moves);
        }

        [TestMethod]
        public void OperatorReadsStepSizeAtEachNudgeAndSkipsSaves()
        {
            var folder = TempFolder();
            var stage = new LickSpoutStage { RigFile = RigFile(folder), PositionsFile = Path.Combine(folder, "M2.csv"), StepSize = 5 };
            var targets = stage.Process(new[] { LickSpoutCommand.SpoutsIn, LickSpoutCommand.SaveSpoutPositions, LickSpoutCommand.BaseRight }.ToObservable())
                .Select(t => t.ToString()).ToList().Wait();
            CollectionAssert.AreEqual(new[] { "10,2000,2100,3000,3100", "5,2000,2100,3000,3100" }, targets.ToArray());
        }

        [DataTestMethod]
        [DataRow("1,2,3,4\n")]
        [DataRow("1,2,3,4,x\n")]
        [DataRow("1,2,3,4,5\n6,7,8,9,10\n")]
        public void MalformedPositionsFileIsRefused(string text)
        {
            var file = Path.Combine(TempFolder(), "bad.csv");
            File.WriteAllText(file, text);
            var ex = Assert.ThrowsException<InvalidDataException>(() => LickSpoutStage.Start(null, file));
            StringAssert.Contains(ex.Message, "must be one line of five whole numbers");
        }

        [TestMethod]
        public void RigYmlWithoutDefaultsIsRefused()
        {
            var folder = TempFolder();
            var rig = Path.Combine(folder, "rig.yml");
            File.WriteAllText(rig, "lickSpoutDefaultPosition:\n  baseTransverse: 1\n");
            var ex = Assert.ThrowsException<InvalidDataException>(() => LickSpoutStage.Start(rig, Path.Combine(folder, "none.csv")));
            StringAssert.Contains(ex.Message, "lickSpoutDefaultPosition.leftRadial must be a whole number");
        }

        [TestMethod]
        public void SetupAndMovesAddressEachDriversMotors()
        {
            var setupA = LickSpoutDrivers.Setup(LickSpoutDriver.A);
            var setupB = LickSpoutDrivers.Setup(LickSpoutDriver.B);
            Assert.AreEqual(8 + 4 * 3, setupA.Count);
            Assert.AreEqual(8 + 4 * 2, setupB.Count);
            Assert.AreEqual(StepperMotors.Motor0 | StepperMotors.Motor1, EnableDriver.GetPayload(setupB[1]));
            Assert.AreEqual(0.5f, Motor2MaximumRunCurrent.GetPayload(setupA.Single(m => m.Address == Motor2MaximumRunCurrent.Address)));
            Assert.AreEqual(280, (int)Motor1StepInterval.GetPayload(setupB.Single(m => m.Address == Motor1StepInterval.Address)));
            Assert.AreEqual("StopMotor1OnFalling", Input1OpMode.GetPayload(setupB.Single(m => m.Address == Input1OpMode.Address)).ToString());

            var target = SpoutPositions.FromArray(new[] { 1, 2, 3, 4, 5 });
            var a = LickSpoutDrivers.Move(LickSpoutDriver.A, target);
            var b = LickSpoutDrivers.Move(LickSpoutDriver.B, target);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, new[] { Motor0MoveAbsolute.GetPayload(a[0]), Motor1MoveAbsolute.GetPayload(a[1]), Motor2MoveAbsolute.GetPayload(a[2]) });
            CollectionAssert.AreEqual(new[] { 4, 5 }, new[] { Motor0MoveAbsolute.GetPayload(b[0]), Motor1MoveAbsolute.GetPayload(b[1]) });
        }

        static HarpMessage Switches(params int[] away)
        {
            return DigitalInputState.FromPayload(MessageType.Event, (DigitalInputStates)away.Sum(m => 1 << m));
        }

        [TestMethod]
        public void HomingStepsOffTheSwitchThenDrivesBackAndZeroes()
        {
            var scheduler = new HistoricalScheduler();
            var events = new Subject<HarpMessage>();
            var sent = new List<HarpMessage>();
            var completed = false;
            HomeLickSpoutMotor.Home(events, LickSpoutDriver.A, 1, TimeSpan.FromSeconds(60), scheduler)
                .Subscribe(sent.Add, () => completed = true);

            Assert.AreEqual(DigitalInputState.Address, sent.Single().Address, "first it reads the switches");
            events.OnNext(Switches());                       // motor 1 on its switch
            scheduler.AdvanceBy(TimeSpan.FromSeconds(0.65)); // three steps off
            Assert.AreEqual(3, sent.Skip(1).Count(m => m.Address == Motor1MoveRelative.Address && Motor1MoveRelative.GetPayload(m) == 1000));
            events.OnNext(Switches(1));                      // off it
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
            Assert.AreEqual(-100000, Motor1MoveRelative.GetPayload(sent.Last()));
            Assert.IsFalse(completed);
            events.OnNext(Switches(0, 1));                   // another motor's switch: not ours
            events.OnNext(Switches(0));                      // the driver stopped it on its switch
            Assert.AreEqual(0, Motor1AccumulatedSteps.GetPayload(sent.Last()));
            Assert.IsTrue(completed);
        }

        [TestMethod]
        public void HomingAMotorAlreadyOffItsSwitchDrivesStraightBack()
        {
            var scheduler = new HistoricalScheduler();
            var events = new Subject<HarpMessage>();
            var sent = new List<HarpMessage>();
            HomeLickSpoutMotor.Home(events, LickSpoutDriver.B, 0, TimeSpan.FromSeconds(60), scheduler).Subscribe(sent.Add);
            events.OnNext(Switches(0));
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
            Assert.AreEqual(2, sent.Count);
            Assert.AreEqual(-100000, Motor0MoveRelative.GetPayload(sent[1]));
        }

        [TestMethod]
        public void HomingThatNeverEndsNamesTheMotor()
        {
            var scheduler = new HistoricalScheduler();
            Exception error = null;
            HomeLickSpoutMotor.Home(new Subject<HarpMessage>(), LickSpoutDriver.B, 1, TimeSpan.FromSeconds(60), scheduler)
                .Subscribe(_ => { }, e => error = e);
            scheduler.AdvanceBy(TimeSpan.FromSeconds(61));
            Assert.IsInstanceOfType(error, typeof(TimeoutException));
            StringAssert.Contains(error.Message, "StepperDriver-B motor 1 was not homed within 60 s");
        }
    }
}
