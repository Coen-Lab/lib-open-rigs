using Bonsai;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>What the lick spout stage is told to do: a placement, a nudge of one axis, or saving the mouse's positions.</summary>
    public enum LickSpoutCommand
    {
        /// <summary>Every axis to its home switch (0).</summary>
        SpoutsHome,
        /// <summary>Both spouts to the mouse's in position.</summary>
        SpoutsIn,
        /// <summary>The in position reset to rig.yml's lickSpoutDefaultPosition, both spouts there (saved only by SaveSpoutPositions).</summary>
        SpoutsDefault,
        /// <summary>Both spouts drawn back and down.</summary>
        SpoutsOut,
        /// <summary>The left spout in, the right drawn back and down.</summary>
        LeftOnly,
        /// <summary>The right spout in, the left drawn back and down.</summary>
        RightOnly,
        /// <summary>Writes the mouse's in positions as its row of the rig's LickSpoutPositions.csv.</summary>
        SaveSpoutPositions,
        /// <summary>Left elevation up one step size.</summary>
        LeftUp,
        /// <summary>Left elevation down one step size.</summary>
        LeftDown,
        /// <summary>Right elevation up one step size.</summary>
        RightUp,
        /// <summary>Right elevation down one step size.</summary>
        RightDown,
        /// <summary>Left radial in (towards the mouse) one step size.</summary>
        LeftIn,
        /// <summary>Left radial out one step size.</summary>
        LeftOut,
        /// <summary>Right radial in (towards the mouse) one step size.</summary>
        RightIn,
        /// <summary>Right radial out one step size.</summary>
        RightOut,
        /// <summary>Base transverse one step size one way.</summary>
        BaseLeft,
        /// <summary>Base transverse one step size the other way.</summary>
        BaseRight
    }

    /// <summary>The lick spout stage's five axes, in <see cref="SpoutPositions.AxisNames"/> order.</summary>
    public enum SpoutAxis
    {
        /// <summary>The base's transverse axis.</summary>
        BaseTransverse,
        /// <summary>The left spout's radial axis.</summary>
        RadialLeft,
        /// <summary>The right spout's radial axis.</summary>
        RadialRight,
        /// <summary>The left spout's elevation axis.</summary>
        ElevationLeft,
        /// <summary>The right spout's elevation axis.</summary>
        ElevationRight
    }

    /// <summary>
    /// A move of one axis's in position, relative or absolute. Relative (<see cref="Steps"/>): + is the way BaseLeft,
    /// LeftIn, RightIn, LeftUp and RightUp move, flipped like them on an axis wired <c>reversed: true</c>.
    /// Absolute (<see cref="Position"/>): the raw motor step count from home, not flipped. Set exactly one of the two.
    /// </summary>
    public class SpoutMove
    {
        /// <summary>Gets or sets the axis moved.</summary>
        public SpoutAxis Axis { get; set; }

        /// <summary>Gets or sets the motor steps moved, signed; 0 when <see cref="Position"/> is set.</summary>
        public int Steps { get; set; }

        /// <summary>Gets or sets the in position the axis moves to, in raw motor steps from home (not flipped by <c>reversed</c>), or null for a relative move.</summary>
        public int? Position { get; set; }
    }

    /// <summary>
    /// The lick spout stage's state: the mouse's in positions, loaded once from its row of the rig's
    /// LickSpoutPositions.csv (or rig.yml's lickSpoutDefaultPosition when it has none), and the placement
    /// last asked for. Each placement command outputs the stage's new target; each nudge or <see cref="SpoutMove"/>
    /// changes the in positions and outputs the current placement's target again (a move from the second input
    /// also a SpoutPositionsMoved event); SpoutsDefault sets the in positions back to rig.yml's default and places the
    /// spouts in; SaveSpoutPositions writes the mouse's row and outputs the positions saved. The first output is
    /// the positions the stage started from.
    /// </summary>
    [Combinator]
    [Description("Turns lick spout stage commands into the stage's targets, in steps: loads the mouse's in positions from its row of PositionsFile (rig.yml's lickSpoutDefaultPosition if it has none), nudges them by StepSize or moves them by (or to) the second input's SpoutMove, resets them to rig.yml's default (SpoutsDefault), saves them as its row, and places the spouts home, in, out, left only or right only.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class LickSpoutStage
    {
        /// <summary>Gets or sets the path of rig.yml.</summary>
        [Description("The path of rig.yml, whose lickSpoutDefaultPosition is the in position of a mouse with no positions file.")]
        public string RigFile { get; set; }

        /// <summary>Gets or sets the rig's spout positions list.</summary>
        [Description("The rig's spout positions list, LickSpoutPositions.csv beside rig.yml: a header, mouse,baseTransverse,radialLeft,radialRight,elevationLeft,elevationRight, then one row per mouse.")]
        public string PositionsFile { get; set; }

        /// <summary>Gets or sets the mouse whose row the stage reads and saves.</summary>
        [Description("The mouse whose row of PositionsFile the stage reads and saves. NoMouse, a workflow's with no experiment, has no row: the stage starts from rig.yml's default and saves nothing.")]
        public string SubjectId { get; set; }

        /// <summary>The subject of a workflow that makes no experiment (OpenRigs.Logging NoExperimentFolder), no mouse's.</summary>
        public const string NoMouse = "_no-experiment_";

        /// <summary>Gets or sets the steps each nudge moves, read at each nudge.</summary>
        [Description("Motor steps each nudge moves, read at each nudge.")]
        public int StepSize { get; set; } = 150;

        /// <summary>Gets or sets how far a spout is drawn back and down when out, in steps.</summary>
        [Description("How far a spout is drawn back and down when out, in steps on both its axes.")]
        public int RetractSteps { get; set; } = 1500;

        /// <summary>The positions the stage started from, then each command's step: a new target, or the positions saved.</summary>
        public IObservable<LickSpoutStep> Process(IObservable<LickSpoutCommand> source)
        {
            return Process(source, Observable.Empty<SpoutMove>());
        }

        /// <summary>The positions the stage started from, then each command's or move's step, one at a time.</summary>
        public IObservable<LickSpoutStep> Process(IObservable<LickSpoutCommand> source, IObservable<SpoutMove> moves)
        {
            return Observable.Defer(() =>
            {
                var state = Start(RigFile, PositionsFile, SubjectId);
                var started = new LickSpoutStep { Event = state.StartedFrom, Positions = state.In, SubjectId = SubjectId };
                var steps = Observable.Merge(
                    source.Select(command => (Func<LickSpoutStep>)(() => state.Step(command, StepSize, RetractSteps))),
                    moves.Select(move => (Func<LickSpoutStep>)(() => state.Step(move, RetractSteps))));
                return Observable.Return(started).Concat(steps.Select(step => step()).Where(step => step != null));
            });
        }

        /// <summary>The stage's state at start: the mouse's row, or rig.yml's defaults.</summary>
        public static StageState Start(string rigFile, string positionsFile, string subjectId)
        {
            if (string.IsNullOrEmpty(positionsFile)) throw new InvalidOperationException("LickSpoutStage has no PositionsFile.");
            var row = subjectId == NoMouse ? null : SpoutPositions.Load(positionsFile, subjectId);
            var positions = row ?? SpoutPositions.FromRigFile(rigFile);
            var wiring = LickSpoutWiring.FromRigFile(rigFile);
            return new StageState
            {
                Wiring = wiring,
                In = wiring.Limit(positions),
                StartedFrom = row != null ? LickSpoutStep.FromList : LickSpoutStep.FromDefault,
                RigFile = rigFile,
                PositionsFile = positionsFile,
                SubjectId = subjectId,
                Placement = LickSpoutCommand.SpoutsHome
            };
        }

        /// <summary>The stage's in positions and its current placement.</summary>
        public class StageState
        {
            /// <summary>Gets or sets the mouse's in positions, each held to its axis's limit.</summary>
            public SpoutPositions In { get; set; }

            /// <summary>Gets or sets the stage's wiring, whose limits hold every position.</summary>
            public LickSpoutWiring Wiring { get; set; }

            /// <summary>Gets or sets the rig's positions list.</summary>
            public string PositionsFile { get; set; }

            /// <summary>Gets or sets the mouse whose row is read and saved.</summary>
            public string SubjectId { get; set; }

            /// <summary>Gets or sets where the in positions came from at start, <see cref="LickSpoutStep.FromList"/> or <see cref="LickSpoutStep.FromDefault"/>.</summary>
            public string StartedFrom { get; set; }

            /// <summary>Gets or sets the path of rig.yml, whose lickSpoutDefaultPosition SpoutsDefault resets to.</summary>
            public string RigFile { get; set; }

            /// <summary>Gets or sets the placement last asked for.</summary>
            public LickSpoutCommand Placement { get; set; }

            /// <summary>Applies one command: a step with the new target, a step with the positions saved, or null.</summary>
            public LickSpoutStep Step(LickSpoutCommand command, int stepSize, int retractSteps)
            {
                if (command == LickSpoutCommand.SaveSpoutPositions)
                {
                    if (SubjectId == NoMouse) return null;
                    In.Save(PositionsFile, SubjectId);
                    return new LickSpoutStep { Event = LickSpoutStep.Saved, Positions = In, SubjectId = SubjectId };
                }
                var target = Apply(command, stepSize, retractSteps);
                return target == null ? null : new LickSpoutStep { Target = target };
            }

            /// <summary>Applies a task's move: a <see cref="LickSpoutStep.Moved"/> step, with the new target unless the spouts are home; throws when the move sets both Steps and Position, or neither.</summary>
            public LickSpoutStep Step(SpoutMove move, int retractSteps)
            {
                var name = SpoutPositions.AxisNames[(int)move.Axis];
                if (move.Position.HasValue && move.Steps != 0) throw new InvalidOperationException("SpoutMove for " + name + " sets both Steps and Position; set one.");
                if (!move.Position.HasValue && move.Steps == 0) throw new InvalidOperationException("SpoutMove for " + name + " sets neither Steps nor Position.");
                var target = Move(move, retractSteps);
                return new LickSpoutStep { Target = target, Event = LickSpoutStep.Moved, Positions = In, SubjectId = SubjectId, Move = move };
            }

            /// <summary>Applies one command other than a save; the new target, or null when the stage does not move.</summary>
            public SpoutPositions Apply(LickSpoutCommand command, int stepSize, int retractSteps)
            {
                switch (command)
                {
                    case LickSpoutCommand.SpoutsHome:
                    case LickSpoutCommand.SpoutsIn:
                    case LickSpoutCommand.SpoutsOut:
                    case LickSpoutCommand.LeftOnly:
                    case LickSpoutCommand.RightOnly:
                        Placement = command;
                        return Target(retractSteps);
                    case LickSpoutCommand.SpoutsDefault:
                        In = Wiring.Limit(SpoutPositions.FromRigFile(RigFile));
                        Placement = LickSpoutCommand.SpoutsIn;
                        return Target(retractSteps);
                    default: return Move(Nudge(command, stepSize), retractSteps);
                }
            }

            /// <summary>Moves one axis's in position, relative or absolute and held to its limit; the current placement's new target, or null when home.</summary>
            public SpoutPositions Move(SpoutMove move, int retractSteps)
            {
                var axis = (int)move.Axis;
                var values = In.ToArray();
                if (move.Position.HasValue) values[axis] = move.Position.Value;
                else values[axis] += Wiring.Axes[axis].Reversed ? -move.Steps : move.Steps;
                In = Wiring.Limit(SpoutPositions.FromArray(values));
                return Placement == LickSpoutCommand.SpoutsHome ? null : Target(retractSteps);
            }

            // A nudge as a move of one step size.
            static SpoutMove Nudge(LickSpoutCommand command, int stepSize)
            {
                switch (command)
                {
                    case LickSpoutCommand.BaseLeft: return new SpoutMove { Axis = SpoutAxis.BaseTransverse, Steps = stepSize };
                    case LickSpoutCommand.BaseRight: return new SpoutMove { Axis = SpoutAxis.BaseTransverse, Steps = -stepSize };
                    case LickSpoutCommand.LeftIn: return new SpoutMove { Axis = SpoutAxis.RadialLeft, Steps = stepSize };
                    case LickSpoutCommand.LeftOut: return new SpoutMove { Axis = SpoutAxis.RadialLeft, Steps = -stepSize };
                    case LickSpoutCommand.RightIn: return new SpoutMove { Axis = SpoutAxis.RadialRight, Steps = stepSize };
                    case LickSpoutCommand.RightOut: return new SpoutMove { Axis = SpoutAxis.RadialRight, Steps = -stepSize };
                    case LickSpoutCommand.LeftUp: return new SpoutMove { Axis = SpoutAxis.ElevationLeft, Steps = stepSize };
                    case LickSpoutCommand.LeftDown: return new SpoutMove { Axis = SpoutAxis.ElevationLeft, Steps = -stepSize };
                    case LickSpoutCommand.RightUp: return new SpoutMove { Axis = SpoutAxis.ElevationRight, Steps = stepSize };
                    case LickSpoutCommand.RightDown: return new SpoutMove { Axis = SpoutAxis.ElevationRight, Steps = -stepSize };
                    default: throw new ArgumentOutOfRangeException(nameof(command), command, null);
                }
            }

            SpoutPositions Target(int retractSteps)
            {
                switch (Placement)
                {
                    case LickSpoutCommand.SpoutsHome: return new SpoutPositions();
                    case LickSpoutCommand.SpoutsIn: return In;
                    case LickSpoutCommand.SpoutsOut: return In.Retract(true, true, retractSteps);
                    case LickSpoutCommand.LeftOnly: return In.Retract(false, true, retractSteps);
                    default: return In.Retract(true, false, retractSteps);
                }
            }
        }
    }

    /// <summary>One output of <see cref="LickSpoutStage"/>: a target to move to, or the mouse's in positions with what happened to them.</summary>
    public class LickSpoutStep
    {
        /// <summary>The event of the positions the stage started from, the mouse's row.</summary>
        public const string FromList = "SpoutPositionsLoaded";

        /// <summary>The event of the positions the stage started from, rig.yml's lickSpoutDefaultPosition (the mouse has no row).</summary>
        public const string FromDefault = "SpoutPositionsDefault";

        /// <summary>The event of the positions SaveSpoutPositions wrote as the mouse's row.</summary>
        public const string Saved = "SpoutPositionsSaved";

        /// <summary>The event of a task's move (LickSpoutStage.Moves): the new in positions after <see cref="Move"/>.</summary>
        public const string Moved = "SpoutPositionsMoved";

        /// <summary>Gets or sets the stage's new target, or null when the step is an event.</summary>
        public SpoutPositions Target { get; set; }

        /// <summary>Gets or sets the event, or null when the step is a move.</summary>
        public string Event { get; set; }

        /// <summary>Gets or sets the mouse's in positions the event is about.</summary>
        public SpoutPositions Positions { get; set; }

        /// <summary>Gets or sets the mouse.</summary>
        public string SubjectId { get; set; }

        /// <summary>Gets or sets the task's move of a <see cref="Moved"/> event, or null.</summary>
        public SpoutMove Move { get; set; }

        /// <summary>The event's detail for the experiment's events log: the mouse, the move if any (baseTransverse+50, or baseTransverse=60 when absolute), and each axis's steps.</summary>
        public string Detail
        {
            get
            {
                if (Positions == null) return null;
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                var values = Positions.ToArray();
                var move = Move == null ? "" : " " + SpoutPositions.AxisNames[(int)Move.Axis]
                    + (Move.Position.HasValue ? "=" + Move.Position.Value.ToString(invariant) : Move.Steps.ToString("+0;-0;0", invariant));
                return SubjectId + move + " " + string.Join(" ", SpoutPositions.AxisNames.Select((axis, i) => axis + "=" + values[i].ToString(invariant)));
            }
        }
    }

    /// <summary>Outputs one lick spout stage command for each input, such as a controller's command of that name.</summary>
    [Combinator]
    [Description("Outputs Command for each input.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ToLickSpoutCommand
    {
        /// <summary>Gets or sets the command to output.</summary>
        [Description("The command to output.")]
        public LickSpoutCommand Command { get; set; }

        /// <summary>Outputs the command for each input.</summary>
        public IObservable<LickSpoutCommand> Process<TSource>(IObservable<TSource> source)
        {
            var command = Command;
            return source.Select(_ => command);
        }
    }
}
