using Bonsai;
using Bonsai.Harp;
using Harp.StepperDriver;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace OpenRigs.Devices
{
    /// <summary>The lick spout stage's two Harp StepperDriver boards, rig.yml's stepperDriver entries a and b; which axis each motor drives is their wiring (<see cref="LickSpoutWiring"/>).</summary>
    public enum LickSpoutDriver
    {
        /// <summary>StepperDriver-A, rig.yml's stepperDriver entry a.</summary>
        A,
        /// <summary>StepperDriver-B, rig.yml's stepperDriver entry b.</summary>
        B
    }

    /// <summary>The messages that set up and move the stage's drivers, by their wiring.</summary>
    public static class LickSpoutDrivers
    {
        /// <summary>
        /// The setup a driver needs once it is open: interlock open, its axes' motors, their encoders
        /// (Motor0-2 have one; Motor3 none) and their home switches enabled, 0.5 A, 1/8 steps, and each
        /// motor's limit cleared until it is homed (the driver keeps a limit while powered, and a limit
        /// from a run before would stop the homing).
        /// </summary>
        public static IList<HarpMessage> Setup(LickSpoutDriver driver, LickSpoutWiring wiring)
        {
            var motors = wiring.On(driver).Select(pair => pair.Value.Motor).OrderBy(m => m).ToList();
            var all = string.Join(", ", motors.Select(m => "{0}" + m));
            var encoders = string.Join(", ", motors.Where(m => m < 3).Select(m => "Encoder" + m));
            var messages = new List<HarpMessage>
            {
                Named("InterlockEnabled", "Open"),
                Named("EnableDriver", string.Format(all, "Motor"))
            };
            if (encoders.Length > 0)
            {
                messages.Add(Named("EnableEncoders", encoders));
                messages.Add(Named("ResetEncoders", encoders));
            }
            messages.AddRange(new[]
            {
                Named("EncoderMode", "Position"),
                Named("EncoderSamplingRate", "Rate100Hz"),
                Named("AccumulatedStepsSamplingRate", "Rate100Hz"),
                Named("EnableDigitalInputs", string.Format(all, "Input"))
            });
            foreach (var motor in motors)
            {
                messages.Add(Motor(motor, typeof(Motor0MaximumRunCurrent), 0.5f));
                messages.Add(Named("Motor" + motor + "MicrostepResolution", "Microstep8"));
                messages.Add(Motor(motor, typeof(Motor0StepInterval), 280));
                messages.Add(Named("Input" + motor + "OpMode", "StopMotor" + motor + "OnFalling"));
                messages.Add(Motor(motor, typeof(Motor0MaxPosition), 0));
            }
            return messages;
        }

        /// <summary>The absolute moves that take one driver's axes to <paramref name="target"/>.</summary>
        public static IList<HarpMessage> Move(LickSpoutDriver driver, LickSpoutWiring wiring, SpoutPositions target)
        {
            var steps = target.ToArray();
            return wiring.On(driver).Select(pair => Motor(pair.Value.Motor, typeof(Motor0MoveAbsolute), steps[pair.Key])).ToList();
        }

        /// <summary>Sets one motor's limit: the driver moves it no further than <paramref name="maxSteps"/> from home.</summary>
        public static HarpMessage SetMaxSteps(int motor, int maxSteps)
        {
            return Motor(motor, typeof(Motor0MaxPosition), maxSteps);
        }

        /// <summary>A relative move of one motor.</summary>
        public static HarpMessage MoveRelative(int motor, int steps)
        {
            return Motor(motor, typeof(Motor0MoveRelative), steps);
        }

        /// <summary>Sets one motor's step count to 0.</summary>
        public static HarpMessage SetZero(int motor)
        {
            return Motor(motor, typeof(Motor0AccumulatedSteps), 0);
        }

        /// <summary>Asks for the home switches' state.</summary>
        public static HarpMessage ReadSwitches()
        {
            return DigitalInputState.FromPayload(MessageType.Read, default(DigitalInputStates));
        }

        /// <summary>Whether a switch-state message has motor <paramref name="motor"/>'s input high (away from its home switch).</summary>
        public static bool? AwayFromHome(HarpMessage message, int motor)
        {
            if (message.Address != DigitalInputState.Address || message.Error) return null;
            return ((int)DigitalInputState.GetPayload(message) & (1 << motor)) != 0;
        }

        // Each per-motor register is named Motor<n><Register> with a static FromPayload(MessageType, value).
        static HarpMessage Motor(int motor, Type motor0Register, object value)
        {
            var name = motor0Register.FullName.Replace("Motor0", "Motor" + motor);
            var register = motor0Register.Assembly.GetType(name, true);
            var fromPayload = register.GetMethods().Single(m => m.Name == "FromPayload" && m.GetParameters().Length == 2);
            var payload = Convert.ChangeType(value, fromPayload.GetParameters()[1].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
            return (HarpMessage)fromPayload.Invoke(null, new[] { MessageType.Write, payload });
        }

        // A register whose payload is an enum, by the enum's text: "Motor0, Motor1" for flags.
        static HarpMessage Named(string registerName, string payload)
        {
            var register = typeof(DigitalInputState).Assembly.GetType("Harp.StepperDriver." + registerName, true);
            var fromPayload = register.GetMethods().Single(m => m.Name == "FromPayload" && m.GetParameters().Length == 2);
            var type = fromPayload.GetParameters()[1].ParameterType;
            return (HarpMessage)fromPayload.Invoke(null, new[] { MessageType.Write, Enum.Parse(type, payload) });
        }
    }

    /// <summary>
    /// Sets up one lick spout driver once it is open: outputs the setup messages for its commands and completes.
    /// </summary>
    [Combinator]
    [Description("Outputs one lick spout driver's setup messages for its commands when the first input arrives, then completes. Its motors are the ones RigFile's wiring gives the stage's axes.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SetUpLickSpoutDriver
    {
        /// <summary>Gets or sets the driver.</summary>
        [Description("The driver to set up.")]
        public LickSpoutDriver Driver { get; set; }

        /// <summary>Gets or sets the path of rig.yml, whose stepperDriver wiring names each axis's motor.</summary>
        [Description("The path of rig.yml, whose stepperDriver wiring names each axis's motor.")]
        public string RigFile { get; set; }

        /// <summary>Outputs the setup when the first input arrives.</summary>
        public IObservable<HarpMessage> Process<TSource>(IObservable<TSource> source)
        {
            return source.Take(1).SelectMany(_ => LickSpoutDrivers.Setup(Driver, LickSpoutWiring.FromRigFile(RigFile)));
        }
    }

    /// <summary>Turns each stage target into the absolute moves of one driver's motors.</summary>
    [Combinator]
    [Description("Turns each stage target into one driver's absolute moves, for the axes RigFile's wiring puts on it.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class MoveLickSpoutDriver
    {
        /// <summary>Gets or sets the driver.</summary>
        [Description("The driver whose motors to move.")]
        public LickSpoutDriver Driver { get; set; }

        /// <summary>Gets or sets the path of rig.yml, whose stepperDriver wiring names each axis's motor.</summary>
        [Description("The path of rig.yml, whose stepperDriver wiring names each axis's motor.")]
        public string RigFile { get; set; }

        /// <summary>Outputs the moves for each target.</summary>
        public IObservable<HarpMessage> Process(IObservable<SpoutPositions> source)
        {
            return Observable.Defer(() =>
            {
                var wiring = LickSpoutWiring.FromRigFile(RigFile);
                return source.SelectMany(target => LickSpoutDrivers.Move(Driver, wiring, target));
            });
        }
    }

    /// <summary>
    /// Homes the motors of a lick spout driver that RigFile's wiring gives the stage's axes, all at
    /// once. The input is the driver's events; the output is the commands to send it. For each motor
    /// it reads the home switch; if the motor is on it, steps away 1000 at a time until it is off;
    /// then drives towards it (the driver stops the motor as the switch closes), sets the step count
    /// to 0 and, where the wiring gives the motor a maxSteps, sets the driver's limit for it.
    /// Completes once every motor is homed; stops the workflow if one takes longer than
    /// <see cref="TimeoutSeconds"/>.
    /// </summary>
    [Combinator]
    [Description("Homes the driver's axis motors at once: the input is its events, the output the commands to send it. Each steps off its home switch if on it, drives back until the switch stops it, sets its step count to 0 and its limit to its wiring's maxSteps, if any. Completes once all are homed; stops the workflow if one takes longer than TimeoutSeconds.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class HomeLickSpoutDriver
    {
        const int StepOff = 1000;
        const int DriveHome = -100000;
        static readonly TimeSpan StepOffInterval = TimeSpan.FromSeconds(0.3);
        static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.2);

        /// <summary>Gets or sets the driver.</summary>
        [Description("The driver whose axis motors to home.")]
        public LickSpoutDriver Driver { get; set; }

        /// <summary>Gets or sets the path of rig.yml, whose stepperDriver wiring names each axis's motor and its limit.</summary>
        [Description("The path of rig.yml, whose stepperDriver wiring names each axis's motor and its limit.")]
        public string RigFile { get; set; }

        /// <summary>Gets or sets how long homing may take, in seconds.</summary>
        [Description("How long homing may take, in seconds.")]
        public double TimeoutSeconds { get; set; } = 60;

        /// <summary>Homes the motors.</summary>
        public IObservable<HarpMessage> Process(IObservable<HarpMessage> source)
        {
            return Observable.Defer(() =>
            {
                var motors = LickSpoutWiring.FromRigFile(RigFile).On(Driver).Select(pair => pair.Value).ToList();
                var timeout = TimeSpan.FromSeconds(TimeoutSeconds);
                return source.Publish(events => motors.Select(m => Home(events, Driver, m.Motor, m.MaxSteps, timeout, DefaultScheduler.Instance)).Merge());
            });
        }

        /// <summary>Homes <paramref name="motor"/> from its driver's <paramref name="events"/>, then sets its limit if it has one, timed on <paramref name="scheduler"/>.</summary>
        public static IObservable<HarpMessage> Home(IObservable<HarpMessage> events, LickSpoutDriver driver, int motor, int? maxSteps, TimeSpan timeout, IScheduler scheduler)
        {
            var homing = events.Publish(shared =>
            {
                var away = shared.Select(message => LickSpoutDrivers.AwayFromHome(message, motor)).Where(state => state.HasValue).Select(state => state.Value);
                var stepOff = away.Take(1).SelectMany(isAway => isAway
                    ? Observable.Empty<HarpMessage>()
                    : Observable.Timer(TimeSpan.Zero, StepOffInterval, scheduler)
                        .Select(_ => LickSpoutDrivers.MoveRelative(motor, StepOff))
                        .TakeUntil(away.Where(isOff => isOff)));
                var homed = maxSteps.HasValue
                    ? new[] { LickSpoutDrivers.SetZero(motor), LickSpoutDrivers.SetMaxSteps(motor, maxSteps.Value) }
                    : new[] { LickSpoutDrivers.SetZero(motor) };
                var driveHome = Observable.Merge(
                    Observable.Timer(SettleTime, scheduler).Select(_ => LickSpoutDrivers.MoveRelative(motor, DriveHome)),
                    away.Where(isOff => !isOff).Take(1).SelectMany(_ => homed));
                return Observable.Return(LickSpoutDrivers.ReadSwitches()).Concat(stepOff).Concat(driveHome);
            });
            return homing.Timeout(timeout, Observable.Defer(() => Observable.Throw<HarpMessage>(new TimeoutException(string.Format(
                "StepperDriver-{0} Motor{1} was not homed within {2} s: check its home switch and cable.", driver, motor, timeout.TotalSeconds)))), scheduler);
        }
    }
}
