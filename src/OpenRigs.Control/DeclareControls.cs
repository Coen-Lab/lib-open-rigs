using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Control
{
    /// <summary>
    /// A device piece's own parameters and commands, declared to the workflow's ControlServer for as long
    /// as the piece runs, beside ExperimentControl's: a piece the workflow does not include, or that does
    /// not run on this rig, declares nothing. Status lists them with the piece's name. A name declared
    /// twice, by two pieces or a piece and ExperimentControl, stops the workflow at Start naming both.
    /// Emits nothing.
    /// </summary>
    [Combinator(MethodName = nameof(Generate))]
    [WorkflowElementCategory(ElementCategory.Source)]
    [Description("Declares a piece's own parameters and commands to the workflow's ControlServer while the piece runs.")]
    public class DeclareControls
    {
        /// <summary>Gets or sets the piece's name, as status and a clash name it.</summary>
        [Description("The piece's name, as status and a clash name it, such as LickSpoutStage.")]
        public string Piece { get; set; }

        /// <summary>Gets or sets the piece's parameters: a controller may change them until Go.</summary>
        [Description("The piece's settings a controller may change until Go, with their defaults.")]
        public Collection<Parameter> Parameters { get; set; } = new Collection<Parameter>();

        /// <summary>Gets or sets the piece's commands: a controller may send them while ready or recording.</summary>
        [Description("The piece's commands a controller may send while ready or recording.")]
        public Collection<Command> Commands { get; set; } = new Collection<Command>();

        /// <summary>Declares the parameters and commands from subscription to disposal.</summary>
        /// <returns>A sequence that emits nothing.</returns>
        public IObservable<Unit> Generate()
        {
            return Observable.Create<Unit>(observer =>
            {
                try { return ControlState.Declare(new PieceDeclaration(Piece ?? "", Parameters, Commands)); }
                catch (InvalidOperationException error)
                {
                    observer.OnError(error);
                    return System.Reactive.Disposables.Disposable.Empty;
                }
            });
        }
    }

    // One declarer's parameters and commands, copied so a run's values never reach the workflow's own.
    sealed class PieceDeclaration
    {
        internal readonly string Piece;
        internal readonly List<Parameter> Parameters;
        internal readonly List<Command> Commands;

        internal PieceDeclaration(string piece, IEnumerable<Parameter> parameters, IEnumerable<Command> commands)
        {
            Piece = piece;
            Parameters = parameters.Select(p => new Parameter { Name = p.Name ?? "", Value = p.Value ?? "", Description = p.Description ?? "", Minimum = p.Minimum, Maximum = p.Maximum, WholeNumber = p.WholeNumber }).ToList();
            Commands = commands.Select(c => new Command { Name = c.Name ?? "", Description = c.Description ?? "" }).ToList();
        }
    }
}
