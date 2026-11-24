using Bonsai;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>What the lick spout stage is told to do: a placement, a nudge of one axis, saving the mouse's positions, or a move of the task's bias offset.</summary>
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
        BaseRight,
        /// <summary>The bias offset one bias step toward the mouse's left (the way BaseLeft moves the base: the right spout toward the midline), held to the bias limit (<see cref="SpoutBiasSettings"/>); the stage moves to its placement's target at once.</summary>
        BiasLeft,
        /// <summary>The bias offset one bias step toward the mouse's right (the way BaseRight moves the base), held to the bias limit; the stage moves at once.</summary>
        BiasRight,
        /// <summary>The bias offset back to 0; the stage moves at once.</summary>
        BiasReset
    }

    /// <summary>
    /// How far the task's bias moves (BiasLeft, BiasRight) shift the base transverse axis, sent by the task to the piece's
    /// LickSpoutStage.BiasSettings: each move <see cref="StepSteps"/>, the offset held within <see cref="MaxSteps"/> either
    /// way. Until one comes, both are 0 and a bias move does nothing.
    /// </summary>
    public class SpoutBiasSettings
    {
        /// <summary>Gets or sets the motor steps one bias move shifts the base, toward the mouse's left or right.</summary>
        public int StepSteps { get; set; }

        /// <summary>Gets or sets the furthest the bias offset may go from the mouse's in position, in steps, each way.</summary>
        public int MaxSteps { get; set; }
    }

    /// <summary>
    /// The lick spout stage's state: the mouse's in positions, loaded once from its row of the rig's
    /// LickSpoutPositions.csv (or rig.yml's lickSpoutDefaultPosition when it has none), the task's bias offset
    /// on the base transverse axis (0 at start, never saved), and the placement last asked for. Each placement
    /// command outputs the stage's new target, made from the in positions with the bias offset added; each nudge
    /// changes the in positions and outputs the current placement's target again; each bias move changes the
    /// offset and outputs the target again with a SpoutBiasMoved event; SpoutsDefault sets the in positions back
    /// to rig.yml's default, keeping the offset (only BiasReset, or a new run, sets it to 0), and places the spouts in; SaveSpoutPositions writes the mouse's row
    /// (without the offset) and outputs the positions saved. The first output is the positions the stage started from.
    /// </summary>
    [Combinator]
    [Description("Turns lick spout stage commands into the stage's targets, in steps: loads the mouse's in positions from its row of PositionsFile (rig.yml's lickSpoutDefaultPosition if it has none), nudges them by StepSize, resets them to rig.yml's default (SpoutsDefault), saves them as its row, and places the spouts home, in, out, left only or right only. Every placement but home carries the task's bias offset on the base transverse axis, which BiasLeft, BiasRight and BiasReset move by the second input's SpoutBiasSettings.")]
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

        /// <summary>The positions the stage started from, then each command's step: a new target, or the positions saved. With no bias settings, a bias move does nothing.</summary>
        public IObservable<LickSpoutStep> Process(IObservable<LickSpoutCommand> source)
        {
            return Process(source, Observable.Empty<SpoutBiasSettings>());
        }

        /// <summary>The positions the stage started from, then each command's step: a new target, the positions saved, or a
        /// bias move. Each of <paramref name="biasSettings"/> sets the bias moves' step and limit from the next command on.</summary>
        public IObservable<LickSpoutStep> Process(IObservable<LickSpoutCommand> source, IObservable<SpoutBiasSettings> biasSettings)
        {
            return Observable.Defer(() =>
            {
                var state = Start(RigFile, PositionsFile, SubjectId);
                var started = new LickSpoutStep { Event = state.StartedFrom, Positions = state.In, SubjectId = SubjectId };
                var settings = biasSettings.Do(value => state.BiasSettings = value).IgnoreElements().Select(_ => (LickSpoutStep)null);
                var steps = source.Select(command => state.Step(command, StepSize, RetractSteps));
                return Observable.Return(started).Concat(Observable.Merge(steps, settings).Where(step => step != null));
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

            /// <summary>Gets or sets the task's bias offset on the base transverse axis, in steps, positive toward the mouse's left
            /// (the way BaseLeft moves the base, so a reversed axis's motor takes it off); added to every target but home, never
            /// to <see cref="In"/>.</summary>
            public int BiasSteps { get; set; }

            /// <summary>Gets or sets the bias moves' step and limit; null, as at start, leaves the offset where it is.</summary>
            public SpoutBiasSettings BiasSettings { get; set; }

            /// <summary>Applies one command: a step with the new target, a step with the positions saved, or null. A step
            /// that changed the bias offset is also a <see cref="LickSpoutStep.BiasMoved"/> event, with a target unless the
            /// spouts are home.</summary>
            public LickSpoutStep Step(LickSpoutCommand command, int stepSize, int retractSteps)
            {
                if (command == LickSpoutCommand.SaveSpoutPositions)
                {
                    if (SubjectId == NoMouse) return null;
                    In.Save(PositionsFile, SubjectId);
                    return new LickSpoutStep { Event = LickSpoutStep.Saved, Positions = In, SubjectId = SubjectId, BiasSteps = BiasSteps };
                }
                var biasBefore = BiasSteps;
                var target = Apply(command, stepSize, retractSteps);
                var biasMoved = BiasSteps != biasBefore;
                if (target == null && !biasMoved) return null;
                return new LickSpoutStep { Target = target, Event = biasMoved ? LickSpoutStep.BiasMoved : null, SubjectId = SubjectId, BiasSteps = BiasSteps };
            }

            /// <summary>Applies one command other than a save; the new target, or null when the stage does not move.</summary>
            public SpoutPositions Apply(LickSpoutCommand command, int stepSize, int retractSteps)
            {
                var values = In.ToArray();
                var nudged = Nudged(command);
                if (nudged >= 0 && Wiring.Axes[nudged].Reversed) stepSize = -stepSize;
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
                    case LickSpoutCommand.BiasLeft:
                    case LickSpoutCommand.BiasRight:
                    case LickSpoutCommand.BiasReset:
                        var before = BiasSteps;
                        BiasSteps = Biased(command);
                        return BiasSteps == before || Placement == LickSpoutCommand.SpoutsHome ? null : Target(retractSteps);
                    case LickSpoutCommand.LeftUp: values[3] += stepSize; break;
                    case LickSpoutCommand.LeftDown: values[3] -= stepSize; break;
                    case LickSpoutCommand.RightUp: values[4] += stepSize; break;
                    case LickSpoutCommand.RightDown: values[4] -= stepSize; break;
                    case LickSpoutCommand.LeftIn: values[1] += stepSize; break;
                    case LickSpoutCommand.LeftOut: values[1] -= stepSize; break;
                    case LickSpoutCommand.RightIn: values[2] += stepSize; break;
                    case LickSpoutCommand.RightOut: values[2] -= stepSize; break;
                    case LickSpoutCommand.BaseLeft: values[0] += stepSize; break;
                    case LickSpoutCommand.BaseRight: values[0] -= stepSize; break;
                    default: throw new ArgumentOutOfRangeException(nameof(command), command, null);
                }
                In = Wiring.Limit(SpoutPositions.FromArray(values));
                return Placement == LickSpoutCommand.SpoutsHome ? null : Target(retractSteps);
            }

            // The axis a nudge moves, in SpoutPositions.AxisNames order, or -1 for a placement.
            static int Nudged(LickSpoutCommand command)
            {
                switch (command)
                {
                    case LickSpoutCommand.BaseLeft: case LickSpoutCommand.BaseRight: return 0;
                    case LickSpoutCommand.LeftIn: case LickSpoutCommand.LeftOut: return 1;
                    case LickSpoutCommand.RightIn: case LickSpoutCommand.RightOut: return 2;
                    case LickSpoutCommand.LeftUp: case LickSpoutCommand.LeftDown: return 3;
                    case LickSpoutCommand.RightUp: case LickSpoutCommand.RightDown: return 4;
                    default: return -1;
                }
            }

            // The offset after a bias command: one step toward the mouse's left or right, held within the limit either way, or 0.
            int Biased(LickSpoutCommand command)
            {
                if (command == LickSpoutCommand.BiasReset) return 0;
                var settings = BiasSettings ?? new SpoutBiasSettings();
                var max = Math.Max(settings.MaxSteps, 0);
                var step = Math.Max(settings.StepSteps, 0);
                var moved = BiasSteps + (command == LickSpoutCommand.BiasLeft ? step : -step);
                return Math.Max(-max, Math.Min(max, moved));
            }

            // The in positions with the bias offset on the base transverse axis, flipped on a reversed axis as its nudges are.
            SpoutPositions Corrected()
            {
                var values = In.ToArray();
                values[0] += Wiring.Axes[0].Reversed ? -BiasSteps : BiasSteps;
                return Wiring.Limit(SpoutPositions.FromArray(values));
            }

            SpoutPositions Target(int retractSteps)
            {
                if (Placement == LickSpoutCommand.SpoutsHome) return new SpoutPositions();
                var corrected = Corrected();
                switch (Placement)
                {
                    case LickSpoutCommand.SpoutsIn: return corrected;
                    case LickSpoutCommand.SpoutsOut: return corrected.Retract(true, true, retractSteps);
                    case LickSpoutCommand.LeftOnly: return corrected.Retract(false, true, retractSteps);
                    default: return corrected.Retract(true, false, retractSteps);
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

        /// <summary>The event of a change of the bias offset: BiasLeft, BiasRight or BiasReset.</summary>
        public const string BiasMoved = "SpoutBiasMoved";

        /// <summary>Gets or sets the stage's new target, or null when the step is an event.</summary>
        public SpoutPositions Target { get; set; }

        /// <summary>Gets or sets the event, or null when the step is a move.</summary>
        public string Event { get; set; }

        /// <summary>Gets or sets the mouse's in positions the event is about.</summary>
        public SpoutPositions Positions { get; set; }

        /// <summary>Gets or sets the mouse.</summary>
        public string SubjectId { get; set; }

        /// <summary>Gets or sets the stage's bias offset after the step, in steps, positive toward the mouse's left.</summary>
        public int BiasSteps { get; set; }

        /// <summary>The event's detail for the experiment's events log: the mouse and each axis's steps, or for
        /// <see cref="BiasMoved"/> the mouse and the new offset (M1 biasSteps=-200).</summary>
        public string Detail
        {
            get
            {
                if (Event == BiasMoved) return SubjectId + " biasSteps=" + BiasSteps.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (Positions == null) return null;
                var values = Positions.ToArray();
                return SubjectId + " " + string.Join(" ", SpoutPositions.AxisNames.Select((axis, i) => axis + "=" + values[i].ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
