using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;

namespace OpenRigs.Control
{
    /// <summary>
    /// Passes the Go event through, dropping commands. Placed after ControlEvents in the include.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Passes only the Go event through.")]
    public class OnGo
    {
        /// <summary>Passes the Go event through.</summary>
        /// <param name="source">ControlServer's events.</param>
        /// <returns>The Go event.</returns>
        public IObservable<ControlEvent> Process(IObservable<ControlEvent> source)
        {
            return source.Where(e => e.Kind == ControlEventKind.Go);
        }
    }

    /// <summary>
    /// Passes the arguments of one declared command through, dropping everything else.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Passes the arguments of the named command through, as sent by the controller.")]
    public class OnCommand
    {
        /// <summary>Gets or sets the command's name.</summary>
        [Description("The command's name, as declared on ControlServer.")]
        public string Name { get; set; }

        /// <summary>Passes the arguments of each sending of the named command through.</summary>
        /// <param name="source">ControlServer's events.</param>
        /// <returns>The command's arguments, each time it is sent.</returns>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            var name = Name ?? "";
            return source.Where(e => e.Kind == ControlEventKind.Command && e.Name == name).Select(e => e.Arguments ?? "");
        }
    }

    /// <summary>
    /// Passes each new value of one declared parameter through, as a controller sets it before
    /// Go, for a setting the hardware takes live. Emits nothing until the first set: start the
    /// hardware from its own setting, which ParameterDefault reports.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Passes each new value of the named parameter through, as a controller sets it before Go.")]
    public class OnParameterSet
    {
        /// <summary>Gets or sets the parameter's name.</summary>
        [Description("The parameter's name, as declared on ControlServer.")]
        public string Name { get; set; }

        /// <summary>Passes each new value of the named parameter through.</summary>
        /// <param name="source">ControlServer's events.</param>
        /// <returns>The parameter's value, each time a controller sets it.</returns>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            var name = Name ?? "";
            return source.Where(e => e.Kind == ControlEventKind.Set && e.Name == name).Select(e => e.Arguments ?? "");
        }
    }

    /// <summary>
    /// Sets a declared parameter's value from the workflow (a rig setting) before Go, unless a
    /// controller has set it, so the controller shows the value the hardware starts with.
    /// Passes every element through.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Sets the named parameter's value from each element before Go, unless a controller has set it, passing the element through.")]
    public class ParameterDefault
    {
        /// <summary>Gets or sets the parameter's name.</summary>
        [Description("The parameter's name, as declared on ControlServer.")]
        public string Name { get; set; }

        /// <summary>Sets the parameter's value from each element, passing the element through.</summary>
        /// <typeparam name="T">The type of the elements.</typeparam>
        /// <param name="source">The workflow's own values.</param>
        /// <returns>The same elements.</returns>
        public IObservable<T> Process<T>(IObservable<T> source)
        {
            var name = Name ?? "";
            return source.Do(value => ControlState.Require("ParameterDefault").SetDefault(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        }
    }

    /// <summary>
    /// Sets the highest value a controller may give a declared parameter, from the workflow at
    /// run time, so a value the hardware cannot take is refused at set with the reason rather
    /// than stopping the workflow. Passes every element through.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Sets the named parameter's maximum from each element, passing the element through.")]
    public class ParameterMaximum
    {
        /// <summary>Gets or sets the parameter's name.</summary>
        [Description("The parameter's name, as declared on ControlServer.")]
        public string Name { get; set; }

        /// <summary>Sets the parameter's maximum from each element, passing the element through.</summary>
        /// <param name="source">The highest values.</param>
        /// <returns>The same elements.</returns>
        public IObservable<double> Process(IObservable<double> source)
        {
            var name = Name ?? "";
            return source.Do(maximum => ControlState.Require("ParameterMaximum").SetMaximum(name, maximum));
        }
    }

    /// <summary>
    /// The value of one declared parameter as it stood at Go. Stops the workflow if the
    /// parameter was not declared, naming the ones that were.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("The value of the named parameter as it stood at Go.")]
    public class ParameterValue
    {
        /// <summary>Gets or sets the parameter's name.</summary>
        [Description("The parameter's name, as declared on ControlServer.")]
        public string Name { get; set; }

        /// <summary>Takes the named parameter's value from each event.</summary>
        /// <param name="source">The Go event (ExperimentGo).</param>
        /// <returns>The parameter's value at Go, as text.</returns>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            var name = Name ?? "";
            return source.Select(e =>
            {
                string value;
                if (e.Parameters == null || !e.Parameters.TryGetValue(name, out value))
                {
                    var declared = e.Parameters == null || e.Parameters.Count == 0 ? "none" : string.Join(", ", e.Parameters.Keys);
                    throw new ArgumentException("ParameterValue: no parameter named '" + name + "' was declared on ControlServer. Declared: " + declared + ".");
                }
                return value;
            });
        }
    }

    /// <summary>
    /// Who pressed Go, as the controller vouched for them (rig-control: from the Lab PIN or the
    /// launch's control token), else who launched the experiment, else empty: a name for
    /// provenance, such as a calibration's calibratedBy.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Who pressed Go, by name (else ID), from the controller; else who launched the experiment; else empty. Input is the Go event (ExperimentGo).")]
    public class GoByName
    {
        /// <summary>Names the person behind each Go event.</summary>
        /// <param name="source">The Go event (ExperimentGo).</param>
        /// <returns>The person's name, else their ID, else empty.</returns>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            return source.Select(go =>
            {
                var state = ControlState.Current;
                var person = state == null ? null : state.GoBy ?? state.LaunchedBy;
                if (person == null) return "";
                return !string.IsNullOrEmpty(person.Name) ? person.Name : (person.Id ?? "");
            });
        }
    }

    /// <summary>
    /// Reports the experiment's phase to the controller on each element, and passes every element
    /// through. Phases only move forward (Starting, Ready, Recording, Finished, Stopping): a
    /// report of an earlier phase than the current one is ignored. Put Ready on the first camera
    /// frame, Recording on the experiment folder, Finished on the end of the run.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Reports the experiment phase to the controller on each element, passing the element through. Phases only move forward.")]
    public class ReportPhase
    {
        /// <summary>Gets or sets the phase to report.</summary>
        [Description("The phase to report.")]
        public ExperimentPhase Phase { get; set; } = ExperimentPhase.Ready;

        /// <summary>Reports the phase on each element, passing the element through.</summary>
        /// <typeparam name="T">The type of the elements.</typeparam>
        /// <param name="source">The elements that mark the phase.</param>
        /// <returns>The same elements.</returns>
        public IObservable<T> Process<T>(IObservable<T> source)
        {
            var phase = Phase;
            return source.Do(_ => ControlState.Require("ReportPhase").SetPhase(phase));
        }
    }

    /// <summary>
    /// Reports a named value to the controller on each element (the experiment folder, water
    /// delivered, trials done), passing the element through. Status carries the latest value of each.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Reports each element as the latest value of the named metric, passing the element through.")]
    public class ReportMetric
    {
        /// <summary>Gets or sets the metric's name.</summary>
        [Description("The metric's name, as the controller will see it.")]
        public string Name { get; set; }

        /// <summary>Reports each element as the metric's latest value, passing the element through.</summary>
        /// <typeparam name="T">The type of the elements.</typeparam>
        /// <param name="source">The metric's values.</param>
        /// <returns>The same elements.</returns>
        public IObservable<T> Process<T>(IObservable<T> source)
        {
            var name = Name ?? "";
            return source.Do(value => ControlState.Require("ReportMetric").SetMetric(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        }
    }
}
