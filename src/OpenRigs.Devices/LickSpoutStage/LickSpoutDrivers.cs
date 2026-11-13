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
    /// <summary>The lick spout stage's two Harp StepperDriver boards.</summary>
    public enum LickSpoutDriver
    {
        /// <summary>StepperDriver-A: motor 0 base transverse, 1 left radial, 2 right radial.</summary>
        A,
        /// <summary>StepperDriver-B: motor 0 left elevation, 1 right elevation.</summary>
        B
    }

    /// <summary>The stage's fixed wiring and the messages that set up and move its drivers.</summary>
    public static class LickSpoutDrivers
    {
        /// <summary>The motors each driver uses.</summary>
        public static int MotorCount(LickSpoutDriver driver)
        {
            return driver == LickSpoutDriver.A ? 3 : 2;
        }

        /// <summary>The setup each driver needs once it is open: interlock open, motors and their home switches enabled, 0.5 A, 1/8 steps.</summary>
        public static IList<HarpMessage> Setup(LickSpoutDriver driver)
        {
            var motors = MotorCount(driver);
            var all = string.Join(", ", Enumerable.Range(0, motors).Select(m => "{0}" + m));
            var messages = new List<HarpMessage>
            {
                Named("InterlockEnabled", "Open"),
                Named("EnableDriver", string.Format(all, "Motor")),
                Named("EnableEncoders", string.Format(all, "Encoder")),
                Named("ResetEncoders", string.Format(all, "Encoder")),
                Named("EncoderMode", "Position"),
                Named("EncoderSamplingRate", "Rate100Hz"),
                Named("AccumulatedStepsSamplingRate", "Rate100Hz"),
                Named("EnableDigitalInputs", string.Format(all, "Input"))
            };
            for (var motor = 0; motor < motors; motor++)
            {
                messages.Add(Motor(motor, typeof(Motor0MaximumRunCurrent), 0.5f));
                messages.Add(Named("Motor" + motor + "MicrostepResolution", "Microstep8"));
                messages.Add(Motor(motor, typeof(Motor0StepInterval), 280));
                messages.Add(Named("Input" + motor + "OpMode", "StopMotor" + motor + "OnFalling"));
            }
            return messages;
        }

        /// <summary>The absolute moves that take one driver's motors to <paramref name="target"/>.</summary>
        public static IList<HarpMessage> Move(LickSpoutDriver driver, SpoutPositions target)
        {
            var steps = target.ToArray();
            var mine = driver == LickSpoutDriver.A ? steps.Take(3) : steps.Skip(3);
            return mine.Select((value, motor) => Motor(motor, typeof(Motor0MoveAbsolute), value)).ToList();
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
    [Description("Outputs one lick spout driver's setup messages for its commands when the first input arrives, then completes.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SetUpLickSpoutDriver
    {
        /// <summary>Gets or sets the driver.</summary>
        [Description("The driver to set up.")]
        public LickSpoutDriver Driver { get; set; }

        /// <summary>Outputs the setup when the first input arrives.</summary>
        public IObservable<HarpMessage> Process<TSource>(IObservable<TSource> source)
        {
            return source.Take(1).SelectMany(_ => LickSpoutDrivers.Setup(Driver));
        }
    }

    /// <summary>Turns each stage target into the absolute moves of one driver's motors.</summary>
    [Combinator]
    [Description("Turns each stage target into one driver's absolute moves.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class MoveLickSpoutDriver
    {
        /// <summary>Gets or sets the driver.</summary>
        [Description("The driver whose motors to move.")]
        public LickSpoutDriver Driver { get; set; }

        /// <summary>Outputs the moves for each target.</summary>
        public IObservable<HarpMessage> Process(IObservable<SpoutPositions> source)
        {
            return source.SelectMany(target => LickSpoutDrivers.Move(Driver, target));
        }
    }

    /// <summary>
    /// Homes one motor of a lick spout driver. The input is the driver's events; the output is the
    /// commands to send it. It reads the motor's home switch; if the motor is on it, steps away 1000
    /// at a time until it is off; then drives towards it (the driver stops the motor as the switch
    /// closes), sets the step count to 0 and completes. Stops the workflow if this takes longer
    /// than <see cref="TimeoutSeconds"/>.
    /// </summary>
    [Combinator]
    [Description("Homes one motor: the input is its driver's events, the output the commands to send it. Steps off the home switch if on it, drives back until the switch stops it, sets the step count to 0 and completes. Stops the workflow if it takes longer than TimeoutSeconds.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class HomeLickSpoutMotor
    {
        const int StepOff = 1000;
        const int DriveHome = -100000;
        static readonly TimeSpan StepOffInterval = TimeSpan.FromSeconds(0.3);
        static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.2);

        /// <summary>Gets or sets the driver, for the error message.</summary>
        [Description("The driver, for the error message.")]
        public LickSpoutDriver Driver { get; set; }

        /// <summary>Gets or sets the motor, 0 to 2.</summary>
        [Description("The motor to home, 0 to 2.")]
        public int Motor { get; set; }

        /// <summary>Gets or sets how long homing may take, in seconds.</summary>
        [Description("How long homing may take, in seconds.")]
        public double TimeoutSeconds { get; set; } = 60;

        /// <summary>Homes the motor.</summary>
        public IObservable<HarpMessage> Process(IObservable<HarpMessage> source)
        {
            return Home(source, Driver, Motor, TimeSpan.FromSeconds(TimeoutSeconds), DefaultScheduler.Instance);
        }

        /// <summary>Homes <paramref name="motor"/> from its driver's <paramref name="events"/>, timed on <paramref name="scheduler"/>.</summary>
        public static IObservable<HarpMessage> Home(IObservable<HarpMessage> events, LickSpoutDriver driver, int motor, TimeSpan timeout, IScheduler scheduler)
        {
            var homing = events.Publish(shared =>
            {
                var away = shared.Select(message => LickSpoutDrivers.AwayFromHome(message, motor)).Where(state => state.HasValue).Select(state => state.Value);
                var stepOff = away.Take(1).SelectMany(isAway => isAway
                    ? Observable.Empty<HarpMessage>()
                    : Observable.Timer(TimeSpan.Zero, StepOffInterval, scheduler)
                        .Select(_ => LickSpoutDrivers.MoveRelative(motor, StepOff))
                        .TakeUntil(away.Where(isOff => isOff)));
                var driveHome = Observable.Merge(
                    Observable.Timer(SettleTime, scheduler).Select(_ => LickSpoutDrivers.MoveRelative(motor, DriveHome)),
                    away.Where(isOff => !isOff).Take(1).Select(_ => LickSpoutDrivers.SetZero(motor)));
                return Observable.Return(LickSpoutDrivers.ReadSwitches()).Concat(stepOff).Concat(driveHome);
            });
            return homing.Timeout(timeout, Observable.Defer(() => Observable.Throw<HarpMessage>(new TimeoutException(string.Format(
                "StepperDriver-{0} motor {1} was not homed within {2} s: check its home switch and cable.", driver, motor, timeout.TotalSeconds)))), scheduler);
        }
    }
}
