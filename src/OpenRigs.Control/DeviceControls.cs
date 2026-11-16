using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using Bonsai;

namespace OpenRigs.Control
{
    /// <summary>
    /// A device's own controls, named from the device so a piece included once per device (CameraLiveControls) declares
    /// each device's: Name is the device (Camera-Overhead), each parameter and command is declared as Name.X
    /// (Camera-Overhead.ExposureUs), and the piece is the device. Every operator here takes the device as Name, so one
    /// externalized Name sets the device, its subjects and its controls together.
    /// </summary>
    public static class DeviceControls
    {
        /// <summary>A device's control <paramref name="control"/> as declared: the device's name, a dot, the control's.</summary>
        public static string FullName(string device, string control)
        {
            if (string.IsNullOrEmpty(device)) throw new InvalidOperationException(string.Format("A device control ({0}) has no device Name.", control));
            return device + "." + (control ?? "");
        }
    }

    /// <summary>A device's own parameters and commands (DeclareControls), each named Name.X, the piece the device.</summary>
    [Combinator(MethodName = nameof(Generate))]
    [WorkflowElementCategory(ElementCategory.Source)]
    [Description("Declares a device's own parameters and commands, each named <Name>.<its name>, the piece the device, while the piece runs.")]
    public class DeclareDeviceControls
    {
        /// <summary>Gets or sets the device, such as Camera-Overhead: the piece's name and each control's prefix.</summary>
        [Description("The device, such as Camera-Overhead: the piece's name and each control's prefix.")]
        public string Name { get; set; }

        /// <summary>Gets or sets the parameters, each named without the device.</summary>
        [Description("The device's settings a controller may change until Go, named without the device (ExposureUs).")]
        public Collection<Parameter> Parameters { get; set; } = new Collection<Parameter>();

        /// <summary>Gets or sets the commands, each named without the device.</summary>
        [Description("The device's commands a controller may send while ready or recording, named without the device.")]
        public Collection<Command> Commands { get; set; } = new Collection<Command>();

        /// <summary>Declares Name.X for each, from subscription to disposal.</summary>
        /// <returns>A sequence that emits nothing.</returns>
        public IObservable<Unit> Generate()
        {
            var device = Name;
            return new DeclareControls
            {
                Piece = device,
                Parameters = new Collection<Parameter>(Parameters.Select(p => new Parameter { Name = DeviceControls.FullName(device, p.Name), Value = p.Value, Description = p.Description, Minimum = p.Minimum, Maximum = p.Maximum, WholeNumber = p.WholeNumber }).ToList()),
                Commands = new Collection<Command>(Commands.Select(c => new Command { Name = DeviceControls.FullName(device, c.Name), Description = c.Description }).ToList()),
            }.Generate();
        }
    }

    /// <summary>OnCommand for a device's command Name.Command.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Passes the arguments of the device's command <Name>.<Command> through, each time a controller sends it.")]
    public class OnDeviceCommand
    {
        /// <summary>Gets or sets the device.</summary>
        [Description("The device, such as RewardValve-Left.")]
        public string Name { get; set; }

        /// <summary>Gets or sets the command, named without the device.</summary>
        [Description("The command, named without the device (Reward).")]
        public string Command { get; set; }

        /// <summary>Passes the arguments of each sending of Name.Command through.</summary>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            return new OnCommand { Name = DeviceControls.FullName(Name, Command) }.Process(source);
        }
    }

    /// <summary>OnParameterSet for a device's parameter Name.Parameter.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Passes each new value of the device's parameter <Name>.<Parameter> through, as a controller sets it before Go.")]
    public class OnDeviceParameterSet
    {
        /// <summary>Gets or sets the device.</summary>
        [Description("The device, such as Camera-Overhead.")]
        public string Name { get; set; }

        /// <summary>Gets or sets the parameter, named without the device.</summary>
        [Description("The parameter, named without the device (ExposureUs).")]
        public string Parameter { get; set; }

        /// <summary>Passes each new value of Name.Parameter through.</summary>
        public IObservable<string> Process(IObservable<ControlEvent> source)
        {
            return new OnParameterSet { Name = DeviceControls.FullName(Name, Parameter) }.Process(source);
        }
    }

    /// <summary>ParameterDefault for a device's parameter Name.Parameter.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Sets the device's parameter <Name>.<Parameter> from each element before Go, unless a controller has set it, passing the element through.")]
    public class DeviceParameterDefault
    {
        /// <summary>Gets or sets the device.</summary>
        [Description("The device, such as Camera-Overhead.")]
        public string Name { get; set; }

        /// <summary>Gets or sets the parameter, named without the device.</summary>
        [Description("The parameter, named without the device (ExposureUs).")]
        public string Parameter { get; set; }

        /// <summary>Sets Name.Parameter's value from each element, passing the element through.</summary>
        public IObservable<T> Process<T>(IObservable<T> source)
        {
            return new ParameterDefault { Name = DeviceControls.FullName(Name, Parameter) }.Process(source);
        }
    }

    /// <summary>ParameterMaximum for a device's parameter Name.Parameter.</summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Sets the device's parameter <Name>.<Parameter>'s maximum from each element, passing the element through.")]
    public class DeviceParameterMaximum
    {
        /// <summary>Gets or sets the device.</summary>
        [Description("The device, such as Camera-Overhead.")]
        public string Name { get; set; }

        /// <summary>Gets or sets the parameter, named without the device.</summary>
        [Description("The parameter, named without the device (ExposureUs).")]
        public string Parameter { get; set; }

        /// <summary>Sets Name.Parameter's maximum from each element, passing the element through.</summary>
        public IObservable<double> Process(IObservable<double> source)
        {
            return new ParameterMaximum { Name = DeviceControls.FullName(Name, Parameter) }.Process(source);
        }
    }
}
