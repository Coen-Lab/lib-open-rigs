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

        // The rig template's wiring, with {0} added to the left radial motor's line.
        const string Wiring = "stepperDriver:\n" +
            "  a:\n    wiring:\n" +
            "      Motor0: { device: BaseTransverse }\n      Motor1: { device: Radial, side: left{0} }\n      Motor2: { device: Radial, side: right }\n" +
            "      Input0: { device: BaseTransverseHome }\n      Input1: { device: RadialHome, side: left }\n      Input2: { device: RadialHome, side: right }\n" +
            "  b:\n    wiring:\n" +
            "      Motor0: { device: Elevation, side: left }\n      Motor1: { device: Elevation, side: right }\n" +
            "      Input0: { device: ElevationHome, side: left }\n      Input1: { device: ElevationHome, side: right }\n";

        static string RigFile(string folder, string wiring = null)
        {
            var path = Path.Combine(folder, "rig.yml");
            File.WriteAllText(path,
                "rootPath: x\nlickSpoutDefaultPosition:\n  baseTransverse: 10\n  radialLeft: 2000\n  radialRight: 2100\n  elevationLeft: 3000\n  elevationRight: 3100\n" +
                (wiring ?? Wiring.Replace("{0}", "")));
            return path;
        }

        const string Header = "mouse,baseTransverse,radialLeft,radialRight,elevationLeft,elevationRight";

        [TestMethod]
        public void MouseWithNoRowStartsFromRigYmlAndSavesItsOwnRow()
        {
            var folder = TempFolder();
            var list = Path.Combine(folder, "LickSpoutPositions.csv");
            var state = LickSpoutStage.Start(RigFile(folder), list, "M1");
            Assert.AreEqual("10,2000,2100,3000,3100", state.In.ToString());
            Assert.AreEqual(LickSpoutStep.FromDefault, state.StartedFrom);

            Assert.IsNull(state.Step(LickSpoutCommand.LeftUp, 150, 1500), "nudges do not move a stage at home");
            var saved = state.Step(LickSpoutCommand.SaveSpoutPositions, 150, 1500);
            Assert.AreEqual(LickSpoutStep.Saved, saved.Event);
            Assert.AreEqual("M1 baseTransverse=10 radialLeft=2000 radialRight=2100 elevationLeft=3150 elevationRight=3100", saved.Detail);
            Assert.IsNull(saved.Target);
            CollectionAssert.AreEqual(new[] { Header, "M1,10,2000,2100,3150,3100" }, File.ReadAllLines(list));
            var again = LickSpoutStage.Start(RigFile(folder), list, "M1");
            Assert.AreEqual("10,2000,2100,3150,3100", again.In.ToString(), "the row wins over rig.yml");
            Assert.AreEqual(LickSpoutStep.FromList, again.StartedFrom);
        }

        [TestMethod]
        public void SavingReplacesOnlyTheMousesRow()
        {
            var folder = TempFolder();
            var list = Path.Combine(folder, "LickSpoutPositions.csv");
            File.WriteAllText(list, Header + "\nM2,1,2,3,4,5\nM1,9,9,9,9,9\n");
            var state = LickSpoutStage.Start(RigFile(folder), list, "M1");
            Assert.AreEqual("9,9,9,9,9", state.In.ToString());
            state.Apply(LickSpoutCommand.BaseLeft, 1, 1500);
            state.Step(LickSpoutCommand.SaveSpoutPositions, 1, 1500);
            CollectionAssert.AreEqual(new[] { Header, "M1,10,9,9,9,9", "M2,1,2,3,4,5" }, File.ReadAllLines(list));
            Assert.IsFalse(File.Exists(list + ".tmp"), "the temporary file took the list's place");
            LickSpoutStage.Start(RigFile(folder), list, "M3").Step(LickSpoutCommand.SaveSpoutPositions, 1, 1500);
            Assert.AreEqual(4, File.ReadAllLines(list).Length, "a new mouse adds its row");
        }

        [TestMethod]
        public void NoMouseNeitherReadsNorSaves()
        {
            var folder = TempFolder();
            var list = Path.Combine(folder, "LickSpoutPositions.csv");
            File.WriteAllText(list, Header + "\n" + LickSpoutStage.NoMouse + ",1,2,3,4,5\n");
            var state = LickSpoutStage.Start(RigFile(folder), list, LickSpoutStage.NoMouse);
            Assert.AreEqual("10,2000,2100,3000,3100", state.In.ToString());
            Assert.IsNull(state.Step(LickSpoutCommand.SaveSpoutPositions, 150, 1500));
            Assert.AreEqual(2, File.ReadAllLines(list).Length);
        }

        [TestMethod]
        public void PlacementsRetractTheOtherSpout()
        {
            var state = LickSpoutStage.Start(RigFile(TempFolder()), Path.Combine(TempFolder(), "none.csv"), "M1");
            Assert.AreEqual("0,0,0,0,0", state.Apply(LickSpoutCommand.SpoutsHome, 150, 1500).ToString());
            Assert.AreEqual("10,2000,2100,3000,3100", state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500).ToString());
            Assert.AreEqual("10,500,600,1500,1600", state.Apply(LickSpoutCommand.SpoutsOut, 150, 1500).ToString());
            Assert.AreEqual("10,2000,600,3000,1600", state.Apply(LickSpoutCommand.LeftOnly, 150, 1500).ToString());
            Assert.AreEqual("10,500,2100,1500,3100", state.Apply(LickSpoutCommand.RightOnly, 150, 1500).ToString());
        }

        [TestMethod]
        public void OnlyPlacementsHoldThroughInAndOutNudges()
        {
            var state = LickSpoutStage.Start(RigFile(TempFolder()), Path.Combine(TempFolder(), "none.csv"), "M1");
            state.Apply(LickSpoutCommand.LeftOnly, 150, 1500);
            Assert.AreEqual("10,2010,600,3000,1600", state.Apply(LickSpoutCommand.LeftIn, 10, 1500).ToString(), "left in: towards the mouse, the right stays out");
            Assert.AreEqual("10,2010,590,3000,1600", state.Apply(LickSpoutCommand.RightOut, 10, 1500).ToString(), "right out: away from the mouse, still retracted");
            state.Apply(LickSpoutCommand.RightOnly, 150, 1500);
            Assert.AreEqual(LickSpoutCommand.RightOnly, state.Placement);
            Assert.AreEqual("10,510,2090,1500,3100", state.Apply(LickSpoutCommand.RightIn, 0, 1500).ToString());
        }

        [TestMethod]
        public void SpoutsDefaultResetsTheMousesPositionsToRigYmls()
        {
            var folder = TempFolder();
            var list = Path.Combine(folder, "LickSpoutPositions.csv");
            File.WriteAllText(list, Header + "\nM1,10,2000,2100,3150,3100\n");
            var state = LickSpoutStage.Start(RigFile(folder), list, "M1");
            Assert.AreEqual("10,2000,2100,3150,3100", state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500).ToString(), "in: the mouse's own");
            Assert.AreEqual("10,2000,2100,3000,3100", state.Apply(LickSpoutCommand.SpoutsDefault, 150, 1500).ToString(), "default: rig.yml's");
            Assert.AreEqual(LickSpoutCommand.SpoutsIn, state.Placement);
            Assert.AreEqual("M1,10,2000,2100,3150,3100", File.ReadAllLines(list)[1], "the row kept until saved");
            state.Step(LickSpoutCommand.SaveSpoutPositions, 150, 1500);
            Assert.AreEqual("M1,10,2000,2100,3000,3100", File.ReadAllLines(list)[1]);
        }

        [TestMethod]
        public void NudgesMoveTheCurrentPlacement()
        {
            var state = LickSpoutStage.Start(RigFile(TempFolder()), Path.Combine(TempFolder(), "none.csv"), "M1");
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            var moves = new[]
            {
                LickSpoutCommand.BaseLeft, LickSpoutCommand.LeftIn, LickSpoutCommand.RightOut,
                LickSpoutCommand.LeftDown, LickSpoutCommand.RightUp
            }.Select(command => state.Apply(command, 10, 1500).ToString()).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "20,2000,2100,3000,3100", "20,2010,2100,3000,3100", "20,2010,2090,3000,3100",
                "20,2010,2090,2990,3100", "20,2010,2090,2990,3110"
            }, moves);
        }

        [TestMethod]
        public void OperatorStartsWithItsPositionsReadsStepSizeAtEachNudgeAndOutputsSaves()
        {
            var folder = TempFolder();
            var stage = new LickSpoutStage { RigFile = RigFile(folder), PositionsFile = Path.Combine(folder, "LickSpoutPositions.csv"), SubjectId = "M2", StepSize = 5 };
            var steps = stage.Process(new[] { LickSpoutCommand.SpoutsIn, LickSpoutCommand.SaveSpoutPositions, LickSpoutCommand.BaseRight }.ToObservable())
                .Select(t => t.Target != null ? t.Target.ToString() : t.Event).ToList().Wait();
            CollectionAssert.AreEqual(new[] { LickSpoutStep.FromDefault, "10,2000,2100,3000,3100", LickSpoutStep.Saved, "5,2000,2100,3000,3100" }, steps.ToArray());
        }

        [DataTestMethod]
        [DataRow("1,2,3,4,5\n")]
        [DataRow(Header + "\nM1,1,2,3,4\n")]
        [DataRow(Header + "\nM1,1,2,3,4,x\n")]
        [DataRow(Header + "\nM1,1,2,3,4,5\nM1,6,7,8,9,10\n")]
        public void MalformedPositionsListIsRefused(string text)
        {
            var file = Path.Combine(TempFolder(), "LickSpoutPositions.csv");
            File.WriteAllText(file, text);
            var ex = Assert.ThrowsException<InvalidDataException>(() => LickSpoutStage.Start(null, file, "M1"));
            StringAssert.Contains(ex.Message, "spout positions list");
        }

        [TestMethod]
        public void RigYmlWithoutDefaultsIsRefused()
        {
            var folder = TempFolder();
            var rig = Path.Combine(folder, "rig.yml");
            File.WriteAllText(rig, "lickSpoutDefaultPosition:\n  baseTransverse: 1\n");
            var ex = Assert.ThrowsException<InvalidDataException>(() => LickSpoutStage.Start(rig, Path.Combine(folder, "none.csv"), "M1"));
            StringAssert.Contains(ex.Message, "lickSpoutDefaultPosition.radialLeft must be a whole number");
        }

        [TestMethod]
        public void SetupAndMovesAddressEachDriversMotors()
        {
            var wiring = LickSpoutWiring.FromRigFile(RigFile(TempFolder()));
            var setupA = LickSpoutDrivers.Setup(LickSpoutDriver.A, wiring);
            var setupB = LickSpoutDrivers.Setup(LickSpoutDriver.B, wiring);
            Assert.AreEqual(8 + 5 * 3, setupA.Count);
            Assert.AreEqual(8 + 5 * 2, setupB.Count);
            Assert.AreEqual(StepperMotors.Motor0 | StepperMotors.Motor1, EnableDriver.GetPayload(setupB[1]));
            Assert.AreEqual(0.5f, Motor2MaximumRunCurrent.GetPayload(setupA.Single(m => m.Address == Motor2MaximumRunCurrent.Address)));
            Assert.AreEqual(280, (int)Motor1StepInterval.GetPayload(setupB.Single(m => m.Address == Motor1StepInterval.Address)));
            Assert.AreEqual("StopMotor1OnFalling", Input1OpMode.GetPayload(setupB.Single(m => m.Address == Input1OpMode.Address)).ToString());
            Assert.AreEqual(0, Motor2MaxPosition.GetPayload(setupA.Single(m => m.Address == Motor2MaxPosition.Address)), "a limit from a run before is cleared until homing");

            var target = SpoutPositions.FromArray(new[] { 1, 2, 3, 4, 5 });
            var a = LickSpoutDrivers.Move(LickSpoutDriver.A, wiring, target);
            var b = LickSpoutDrivers.Move(LickSpoutDriver.B, wiring, target);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, new[] { Motor0MoveAbsolute.GetPayload(a[0]), Motor1MoveAbsolute.GetPayload(a[1]), Motor2MoveAbsolute.GetPayload(a[2]) });
            CollectionAssert.AreEqual(new[] { 4, 5 }, new[] { Motor0MoveAbsolute.GetPayload(b[0]), Motor1MoveAbsolute.GetPayload(b[1]) });
        }

        [TestMethod]
        public void AnAxisMovedToASpareMotorIsDrivenThereAndTheOldMotorIsLeftAlone()
        {
            var moved = Wiring.Replace("{0}", "")
                .Replace("Motor1: { device: Radial, side: left }", "Motor1: { device: Shutter }\n      Motor3: { device: Radial, side: left }")
                .Replace("Input1: { device: RadialHome, side: left }", "Input3: { device: RadialHome, side: left }");
            var wiring = LickSpoutWiring.FromRigFile(RigFile(TempFolder(), moved));
            Assert.AreEqual(3, wiring.Axes[1].Motor);
            var setupA = LickSpoutDrivers.Setup(LickSpoutDriver.A, wiring);
            Assert.AreEqual(StepperMotors.Motor0 | StepperMotors.Motor2 | StepperMotors.Motor3, EnableDriver.GetPayload(setupA[1]));
            Assert.AreEqual(QuadratureEncoders.Encoder0 | QuadratureEncoders.Encoder2, EnableEncoders.GetPayload(setupA[2]), "Motor3 has no encoder");
            Assert.IsFalse(setupA.Any(m => m.Address == Motor1MaximumRunCurrent.Address), "the shutter's motor is not the stage's");
            var a = LickSpoutDrivers.Move(LickSpoutDriver.A, wiring, SpoutPositions.FromArray(new[] { 1, 2, 3, 4, 5 }));
            Assert.AreEqual(2, Motor3MoveAbsolute.GetPayload(a.Single(m => m.Address == Motor3MoveAbsolute.Address)));
            Assert.IsFalse(a.Any(m => m.Address == Motor1MoveAbsolute.Address));
        }

        [DataTestMethod]
        [DataRow("Motor2: { device: Radial, side: right }", "Motor2: { device: Radial, side: left }", "both drive the radialLeft axis")]
        [DataRow("Motor2: { device: Radial, side: right }", "Motor2: { device: Shutter }", "no stepperDriver motor is wired to radialRight ({ device: Radial, side: right })")]
        [DataRow("Input2: { device: RadialHome, side: right }", "Input2: { device: RadialHome, side: left }", "stepperDriver.a.wiring.Motor2 drives the radialRight axis, so its home switch must be Input2: { device: RadialHome, side: right }")]
        [DataRow("side: right }\n      Input0", "side: right, maxSteps: 0 }\n      Input0", "stepperDriver.a.wiring.Motor2.maxSteps must be a whole number of steps, 1 or more")]
        public void WiringThatDoesNotGiveEachAxisOneMotorIsRefused(string line, string wrong, string message)
        {
            var text = Wiring.Replace("{0}", "");
            Assert.IsTrue(text.Contains(line), line);
            var rig = RigFile(TempFolder(), text.Replace(line, wrong));
            var ex = Assert.ThrowsException<InvalidDataException>(() => LickSpoutWiring.FromRigFile(rig));
            StringAssert.Contains(ex.Message, message);
        }

        [TestMethod]
        public void NoWiringIsRefusedNamingTheEntry()
        {
            var ex = Assert.ThrowsException<InvalidDataException>(() => LickSpoutWiring.FromRigFile(RigFile(TempFolder(), "stepperDriver:\n  a: {}\n")));
            StringAssert.Contains(ex.Message, "stepperDriver.a has no wiring");
        }

        [TestMethod]
        public void PositionsAreHeldToTheAxisLimit()
        {
            var folder = TempFolder();
            var list = Path.Combine(folder, "LickSpoutPositions.csv");
            File.WriteAllText(list, Header + "\nM1,10,9500,2100,3000,3100\n");
            var state = LickSpoutStage.Start(RigFile(folder, Wiring.Replace("{0}", ", maxSteps: 9000")), list, "M1");
            Assert.AreEqual("10,9000,2100,3000,3100", state.In.ToString(), "a saved position past the limit starts at it");
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            Assert.AreEqual(9000, state.Apply(LickSpoutCommand.LeftIn, 150, 1500).RadialLeft, "a nudge stops at the limit");
            Assert.AreEqual(8850, state.Apply(LickSpoutCommand.LeftOut, 150, 1500).RadialLeft);
            Assert.AreEqual(9100, state.Apply(LickSpoutCommand.RightIn, 7000, 1500).RadialRight, "an axis with no maxSteps has no limit");
        }

        [TestMethod]
        public void AReversedAxisNudgesTheOtherWay()
        {
            var folder = TempFolder();
            var state = LickSpoutStage.Start(RigFile(folder, Wiring.Replace("{0}", ", reversed: true")), Path.Combine(folder, "LickSpoutPositions.csv"), "M1");
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            Assert.AreEqual(1850, state.Apply(LickSpoutCommand.LeftIn, 150, 1500).RadialLeft, "In takes steps off a reversed axis");
            Assert.AreEqual(2000, state.Apply(LickSpoutCommand.LeftOut, 150, 1500).RadialLeft);
            Assert.AreEqual(2250, state.Apply(LickSpoutCommand.RightIn, 150, 1500).RadialRight, "the others are as wired");
            var wrong = RigFile(TempFolder(), Wiring.Replace("{0}", ", reversed: yes"));
            StringAssert.Contains(Assert.ThrowsException<InvalidDataException>(() => LickSpoutWiring.FromRigFile(wrong)).Message, "Motor1.reversed must be true or false");
        }

        static LickSpoutStage.StageState Biased(string wiring = null, int stepSteps = 100, int maxSteps = 250)
        {
            var folder = TempFolder();
            var state = LickSpoutStage.Start(RigFile(folder, wiring), Path.Combine(folder, "LickSpoutPositions.csv"), "M1");
            state.BiasSettings = new SpoutBiasSettings { StepSteps = stepSteps, MaxSteps = maxSteps };
            return state;
        }

        [TestMethod]
        public void ABiasMoveShiftsTheBaseOfThePlacementAtOnceAndIsAnEvent()
        {
            var state = Biased();
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            var step = state.Step(LickSpoutCommand.BiasLeft, 150, 1500);
            Assert.AreEqual("110,2000,2100,3000,3100", step.Target.ToString(), "the in position, the base a bias step toward the mouse's left");
            Assert.AreEqual(LickSpoutStep.BiasMoved, step.Event);
            Assert.AreEqual("M1 biasSteps=100", step.Detail);
            Assert.AreEqual("10,2000,2100,3000,3100", state.In.ToString(), "the in position is the person's, without the offset");
            Assert.AreEqual("M1 biasSteps=0", state.Step(LickSpoutCommand.BiasRight, 150, 1500).Detail);
            Assert.AreEqual("-90,2000,2100,3000,3100", state.Step(LickSpoutCommand.BiasRight, 150, 1500).Target.ToString());
            Assert.IsNull(state.Step(LickSpoutCommand.SpoutsIn, 150, 1500).Event, "a placement is no bias event");
        }

        [TestMethod]
        public void EveryPlacementButHomeCarriesTheOffsetSoTheOutPositionFollows()
        {
            var state = Biased();
            state.Apply(LickSpoutCommand.SpoutsOut, 150, 1500);
            Assert.AreEqual("110,500,600,1500,1600", state.Step(LickSpoutCommand.BiasLeft, 150, 1500).Target.ToString(), "drawn out: the retracted position shifted");
            Assert.AreEqual("110,2000,2100,3000,3100", state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500).ToString());
            Assert.AreEqual("110,2000,600,3000,1600", state.Apply(LickSpoutCommand.LeftOnly, 150, 1500).ToString());
            Assert.AreEqual("110,500,2100,1500,3100", state.Apply(LickSpoutCommand.RightOnly, 150, 1500).ToString());
            Assert.AreEqual("0,0,0,0,0", state.Apply(LickSpoutCommand.SpoutsHome, 150, 1500).ToString(), "home ignores the offset");
            var atHome = state.Step(LickSpoutCommand.BiasLeft, 150, 1500);
            Assert.IsNull(atHome.Target, "a bias move at home moves nothing");
            Assert.AreEqual("M1 biasSteps=200", atHome.Detail);
            Assert.AreEqual("210,2000,2100,3000,3100", state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500).ToString());
        }

        [TestMethod]
        public void TheOffsetIsHeldToTheTasksLimitEachWay()
        {
            var state = Biased(stepSteps: 100, maxSteps: 250);
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            var lefts = Enumerable.Range(0, 4).Select(_ => state.Step(LickSpoutCommand.BiasLeft, 150, 1500)).ToArray();
            CollectionAssert.AreEqual(new[] { "M1 biasSteps=100", "M1 biasSteps=200", "M1 biasSteps=250" }, lefts.Take(3).Select(s => s.Detail).ToArray());
            Assert.IsNull(lefts[3], "at the limit a move that way changes nothing: no move, no event");
            for (var i = 0; i < 6; i++) state.Step(LickSpoutCommand.BiasRight, 150, 1500);
            Assert.AreEqual(-250, state.BiasSteps);
            Assert.AreEqual(-240, state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500).BaseTransverse);
        }

        [TestMethod]
        public void WithoutSettingsABiasMoveDoesNothing()
        {
            var folder = TempFolder();
            var state = LickSpoutStage.Start(RigFile(folder), Path.Combine(folder, "none.csv"), "M1");
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            Assert.IsNull(state.Step(LickSpoutCommand.BiasLeft, 150, 1500));
            Assert.AreEqual(0, state.BiasSteps);
        }

        [TestMethod]
        public void SpoutsDefaultAndBiasResetSetTheOffsetBackTo0()
        {
            var state = Biased();
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            state.Step(LickSpoutCommand.BiasLeft, 150, 1500);
            var reset = state.Step(LickSpoutCommand.BiasReset, 150, 1500);
            Assert.AreEqual("10,2000,2100,3000,3100", reset.Target.ToString());
            Assert.AreEqual("M1 biasSteps=0", reset.Detail);
            Assert.IsNull(state.Step(LickSpoutCommand.BiasReset, 150, 1500), "already 0");
            state.Step(LickSpoutCommand.BiasRight, 150, 1500);
            var spoutsDefault = state.Step(LickSpoutCommand.SpoutsDefault, 150, 1500);
            Assert.AreEqual("10,2000,2100,3000,3100", spoutsDefault.Target.ToString());
            Assert.AreEqual(LickSpoutStep.BiasMoved, spoutsDefault.Event, "the change to 0 is logged");
            Assert.IsNull(state.Step(LickSpoutCommand.SpoutsDefault, 150, 1500).Event, "no change, no event");
        }

        [TestMethod]
        public void NudgesMoveTheInPositionAndTheOffsetRidesOnTopNeverSaved()
        {
            var folder = TempFolder();
            var list = Path.Combine(folder, "LickSpoutPositions.csv");
            var state = LickSpoutStage.Start(RigFile(folder), list, "M1");
            state.BiasSettings = new SpoutBiasSettings { StepSteps = 100, MaxSteps = 500 };
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            state.Step(LickSpoutCommand.BiasRight, 150, 1500);
            Assert.AreEqual("-140,2000,2100,3000,3100", state.Apply(LickSpoutCommand.BaseRight, 50, 1500).ToString());
            Assert.AreEqual("-40,2000,2100,3000,3100", state.In.ToString());
            var saved = state.Step(LickSpoutCommand.SaveSpoutPositions, 150, 1500);
            Assert.AreEqual("M1 baseTransverse=-40 radialLeft=2000 radialRight=2100 elevationLeft=3000 elevationRight=3100", saved.Detail);
            Assert.AreEqual("M1,-40,2000,2100,3000,3100", File.ReadAllLines(list)[1], "the person's position, not the correction");
        }

        [TestMethod]
        public void AReversedBaseTakesABiasMoveTheOtherWayAsItsNudges()
        {
            var reversed = Wiring.Replace("{0}", "").Replace("Motor0: { device: BaseTransverse }", "Motor0: { device: BaseTransverse, reversed: true }");
            var state = Biased(reversed);
            state.Apply(LickSpoutCommand.SpoutsIn, 1000, 1500);
            var nudged = state.Apply(LickSpoutCommand.BaseLeft, 1000, 1500).BaseTransverse;
            var step = state.Step(LickSpoutCommand.BiasLeft, 150, 1500);
            Assert.AreEqual(-990, nudged, "BaseLeft takes steps off a reversed base");
            Assert.AreEqual(-1090, step.Target.BaseTransverse, "and so does BiasLeft: the mouse's left on every rig");
            Assert.AreEqual("M1 biasSteps=100", step.Detail, "the offset is logged the mouse's way, the same on every rig");
        }

        [TestMethod]
        public void TheCorrectedPositionIsHeldToTheBaseLimit()
        {
            var limited = Wiring.Replace("{0}", "").Replace("Motor0: { device: BaseTransverse }", "Motor0: { device: BaseTransverse, maxSteps: 150 }");
            var state = Biased(limited);
            state.Apply(LickSpoutCommand.SpoutsIn, 150, 1500);
            state.Step(LickSpoutCommand.BiasLeft, 150, 1500);
            Assert.AreEqual(150, state.Step(LickSpoutCommand.BiasLeft, 150, 1500).Target.BaseTransverse);
            Assert.AreEqual(200, state.BiasSteps, "the offset is the task's; the axis limit holds the target");
        }

        [TestMethod]
        public void OperatorTakesTheBiasSettingsOnItsSecondInput()
        {
            var folder = TempFolder();
            var stage = new LickSpoutStage { RigFile = RigFile(folder), PositionsFile = Path.Combine(folder, "LickSpoutPositions.csv"), SubjectId = "M2" };
            var commands = new Subject<LickSpoutCommand>();
            var settings = new Subject<SpoutBiasSettings>();
            var steps = new List<string>();
            using (stage.Process(commands, settings).Subscribe(t => steps.Add(t.Target != null ? t.Target.ToString() : t.Event)))
            {
                commands.OnNext(LickSpoutCommand.SpoutsIn);
                commands.OnNext(LickSpoutCommand.BiasLeft);
                settings.OnNext(new SpoutBiasSettings { StepSteps = 50, MaxSteps = 500 });
                commands.OnNext(LickSpoutCommand.BiasLeft);
            }
            CollectionAssert.AreEqual(new[] { LickSpoutStep.FromDefault, "10,2000,2100,3000,3100", "60,2000,2100,3000,3100" }, steps.ToArray(), "no move before the settings came");
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
            HomeLickSpoutDriver.Home(events, LickSpoutDriver.A, 1, null, TimeSpan.FromSeconds(60), scheduler)
                .Subscribe(sent.Add, () => completed = true);

            Assert.AreEqual(DigitalInputState.Address, sent.Single().Address, "first it reads the switches");
            events.OnNext(Switches());                       // Motor1 on its switch
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
            HomeLickSpoutDriver.Home(events, LickSpoutDriver.B, 0, null, TimeSpan.FromSeconds(60), scheduler).Subscribe(sent.Add);
            events.OnNext(Switches(0));
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
            Assert.AreEqual(2, sent.Count);
            Assert.AreEqual(-100000, Motor0MoveRelative.GetPayload(sent[1]));
        }

        [TestMethod]
        public void HomingSetsTheLimitOnlyOnceTheMotorIsZeroed()
        {
            var scheduler = new HistoricalScheduler();
            var events = new Subject<HarpMessage>();
            var sent = new List<HarpMessage>();
            var completed = false;
            HomeLickSpoutDriver.Home(events, LickSpoutDriver.A, 1, 9000, TimeSpan.FromSeconds(60), scheduler).Subscribe(sent.Add, () => completed = true);
            events.OnNext(Switches(1));
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
            Assert.IsFalse(sent.Any(m => m.Address == Motor1MaxPosition.Address), "no limit while it drives home");
            events.OnNext(Switches());
            CollectionAssert.AreEqual(new[] { Motor1AccumulatedSteps.Address, Motor1MaxPosition.Address }, sent.Skip(sent.Count - 2).Select(m => m.Address).ToArray());
            Assert.AreEqual(9000, Motor1MaxPosition.GetPayload(sent.Last()));
            Assert.IsTrue(completed);
        }

        [TestMethod]
        public void HomingThatNeverEndsNamesTheMotor()
        {
            var scheduler = new HistoricalScheduler();
            Exception error = null;
            HomeLickSpoutDriver.Home(new Subject<HarpMessage>(), LickSpoutDriver.B, 1, null, TimeSpan.FromSeconds(60), scheduler)
                .Subscribe(_ => { }, e => error = e);
            scheduler.AdvanceBy(TimeSpan.FromSeconds(61));
            Assert.IsInstanceOfType(error, typeof(TimeoutException));
            StringAssert.Contains(error.Message, "StepperDriver-B Motor1 was not homed within 60 s");
        }
    }
}
