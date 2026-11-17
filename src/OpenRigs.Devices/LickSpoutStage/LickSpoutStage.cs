using Bonsai;
using System;
using System.ComponentModel;
using System.IO;
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
        /// <summary>Writes the mouse's in positions to its file.</summary>
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

    /// <summary>
    /// The lick spout stage's state: the mouse's in positions, loaded once from its file (or rig.yml's
    /// lickSpoutDefaultPosition when it has none), and the placement last asked for. Each placement
    /// command outputs the stage's new target; each nudge changes the in positions and outputs the
    /// current placement's target again; SpoutsDefault sets the in positions back to rig.yml's default
    /// and places the spouts in; SaveSpoutPositions writes the file and outputs nothing.
    /// </summary>
    [Combinator]
    [Description("Turns lick spout stage commands into the stage's targets, in steps: loads the mouse's in positions from PositionsFile (rig.yml's lickSpoutDefaultPosition if there is none), nudges them by StepSize, resets them to rig.yml's default (SpoutsDefault), saves them, and places the spouts home, in, out, left only or right only.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class LickSpoutStage
    {
        /// <summary>Gets or sets the path of rig.yml.</summary>
        [Description("The path of rig.yml, whose lickSpoutDefaultPosition is the in position of a mouse with no positions file.")]
        public string RigFile { get; set; }

        /// <summary>Gets or sets this mouse's positions file.</summary>
        [Description("This mouse's positions file: one line, baseTransverse,radialLeft,radialRight,elevationLeft,elevationRight.")]
        public string PositionsFile { get; set; }

        /// <summary>Gets or sets the steps each nudge moves, read at each nudge.</summary>
        [Description("Motor steps each nudge moves, read at each nudge.")]
        public int StepSize { get; set; } = 150;

        /// <summary>Gets or sets how far a spout is drawn back and down when out, in steps.</summary>
        [Description("How far a spout is drawn back and down when out, in steps on both its axes.")]
        public int RetractSteps { get; set; } = 1500;

        /// <summary>Turns each command into the stage's target, if it moves the stage.</summary>
        public IObservable<SpoutPositions> Process(IObservable<LickSpoutCommand> source)
        {
            return Observable.Defer(() =>
            {
                var state = Start(RigFile, PositionsFile);
                return source.Select(command => state.Apply(command, StepSize, RetractSteps)).Where(target => target != null);
            });
        }

        /// <summary>The stage's state at start: the file's positions, or rig.yml's defaults.</summary>
        public static StageState Start(string rigFile, string positionsFile)
        {
            if (string.IsNullOrEmpty(positionsFile)) throw new InvalidOperationException("LickSpoutStage has no PositionsFile.");
            var positions = File.Exists(positionsFile) ? SpoutPositions.Load(positionsFile) : SpoutPositions.FromRigFile(rigFile);
            return new StageState { In = positions, RigFile = rigFile, PositionsFile = positionsFile, Placement = LickSpoutCommand.SpoutsHome };
        }

        /// <summary>The stage's in positions and its current placement.</summary>
        public class StageState
        {
            /// <summary>Gets or sets the mouse's in positions.</summary>
            public SpoutPositions In { get; set; }

            /// <summary>Gets or sets the positions file.</summary>
            public string PositionsFile { get; set; }

            /// <summary>Gets or sets the path of rig.yml, whose lickSpoutDefaultPosition SpoutsDefault resets to.</summary>
            public string RigFile { get; set; }

            /// <summary>Gets or sets the placement last asked for.</summary>
            public LickSpoutCommand Placement { get; set; }

            /// <summary>Applies one command; the new target, or null when the stage does not move.</summary>
            public SpoutPositions Apply(LickSpoutCommand command, int stepSize, int retractSteps)
            {
                var values = In.ToArray();
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
                        In = SpoutPositions.FromRigFile(RigFile);
                        Placement = LickSpoutCommand.SpoutsIn;
                        return Target(retractSteps);
                    case LickSpoutCommand.SaveSpoutPositions:
                        In.Save(PositionsFile);
                        return null;
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
                In = SpoutPositions.FromArray(values);
                return Placement == LickSpoutCommand.SpoutsHome ? null : Target(retractSteps);
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
