using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Bonsai;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;

namespace OpenRigs.Control
{
    /// <summary>
    /// Where an experiment is, as the controller sees it.
    /// </summary>
    public enum ExperimentPhase
    {
        /// <summary>The workflow is up; its hardware is not yet confirmed.</summary>
        Starting,
        /// <summary>Hardware armed and streaming; nothing recorded; parameters still editable.</summary>
        Ready,
        /// <summary>Go received and the experiment folder exists.</summary>
        Recording,
        /// <summary>The run has ended; the workflow waits to be stopped.</summary>
        Finished,
        /// <summary>A stop was requested and the workflow is ending.</summary>
        Stopping
        // The order is the order an experiment moves through: phases only move forward.
    }

    /// <summary>
    /// A setting the controller may change until Go. Named for how it reads in a workflow
    /// file: the serializer names collection items after their type.
    /// </summary>
    public class Parameter
    {
        /// <summary>Gets or sets the parameter's name, as ParameterValue and a controller's set name it.</summary>
        public string Name { get; set; }

        /// <summary>Gets or sets the value, as text: the default until a controller sets it.</summary>
        public string Value { get; set; }

        /// <summary>Gets or sets what the parameter does, shown to the person setting it.</summary>
        public string Description { get; set; }

        /// <summary>Gets or sets the lowest value a set may give, if any; a set then needs a number.</summary>
        public double? Minimum { get; set; }

        /// <summary>Whether the workflow file records Minimum: only when one is set.</summary>
        public bool ShouldSerializeMinimum() { return Minimum.HasValue; }

        /// <summary>Gets or sets the highest value a set may give, if any; a set then needs a number.</summary>
        public double? Maximum { get; set; }

        /// <summary>Whether the workflow file records Maximum: only when one is set.</summary>
        public bool ShouldSerializeMaximum() { return Maximum.HasValue; }

        /// <summary>Gets or sets whether a set must give a whole number.</summary>
        public bool WholeNumber { get; set; }

        /// <summary>Whether the workflow file records WholeNumber: only when it is true.</summary>
        public bool ShouldSerializeWholeNumber() { return WholeNumber; }

        /// <summary>Why a value cannot be set, or null if it can. Checked when the controller sets
        /// it, so a bad value is refused while ready rather than stopping the workflow at Go.</summary>
        /// <param name="value">The value a controller asked for.</param>
        /// <returns>A sentence naming the limits, or null.</returns>
        public string Refusal(string value)
        {
            if (!WholeNumber && !Minimum.HasValue && !Maximum.HasValue) return null;
            var c = CultureInfo.InvariantCulture;
            var wanted = (WholeNumber ? "a whole number" : "a number")
                + (Minimum.HasValue && Maximum.HasValue ? string.Format(c, " from {0} to {1}", Minimum, Maximum)
                : Minimum.HasValue ? string.Format(c, " of at least {0}", Minimum)
                : Maximum.HasValue ? string.Format(c, " of at most {0}", Maximum) : "");
            bool parsed;
            double number;
            if (WholeNumber)
            {
                long whole;
                parsed = long.TryParse(value, NumberStyles.Integer, c, out whole);
                number = whole;
            }
            // NaN parses as a number but passes every comparison with a limit.
            else parsed = double.TryParse(value, NumberStyles.Float, c, out number) && !double.IsNaN(number);
            if (!parsed || (Minimum.HasValue && number < Minimum.Value) || (Maximum.HasValue && number > Maximum.Value))
                return Name + " must be " + wanted + "; '" + value + "' is not";
            return null;
        }
    }

    /// <summary>
    /// A command the workflow accepts while ready or recording.
    /// </summary>
    public class Command
    {
        /// <summary>Gets or sets the command's name, as OnCommand and a controller name it.</summary>
        public string Name { get; set; }

        /// <summary>Gets or sets what the command does, shown on its button.</summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// A person a controller names on a request: who launched the experiment, sent Go or stopped it.
    /// The controller vouches for them (rig-control checks a PIN first); the workflow only records.
    /// </summary>
    public class Person
    {
        /// <summary>Gets or sets the person's ID as the controller knows them; empty if it gave none.</summary>
        public string Id { get; set; }

        /// <summary>Gets or sets the person's name, to show.</summary>
        public string Name { get; set; }

        /// <summary>The person a request names: <c>{"id": ..., "name": ...}</c>, or a name alone as a
        /// string; null when the request names nobody, so an older controller still works.</summary>
        /// <param name="token">The request's field.</param>
        /// <returns>The person, or null.</returns>
        public static Person From(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String) return Named("", (string)token);
            if (token.Type != JTokenType.Object) return null;
            return Named(Text(token["id"]), Text(token["name"]));
        }

        /// <summary>The person as a request or status names them, or a JSON null.</summary>
        /// <param name="person">The person, or null.</param>
        /// <returns>The JSON.</returns>
        public static JToken ToJson(Person person)
        {
            return person == null ? JValue.CreateNull() : new JObject { ["id"] = person.Id, ["name"] = person.Name };
        }

        static Person Named(string id, string name)
        {
            return id.Length == 0 && name.Length == 0 ? null : new Person { Id = id, Name = name };
        }

        static string Text(JToken value)
        {
            return value is JValue && value.Type != JTokenType.Null ? value.ToString() : "";
        }

        /// <summary>The name, and the ID after it when there is one.</summary>
        /// <returns>The line.</returns>
        public override string ToString()
        {
            return Id.Length == 0 ? Name : Name + " (" + Id + ")";
        }
    }

    /// <summary>
    /// What kind of event the controller sent.
    /// </summary>
    public enum ControlEventKind
    {
        /// <summary>The experiment was released, once.</summary>
        Go,
        /// <summary>A declared command, each time it is sent.</summary>
        Command,
        /// <summary>A parameter's new value, each time a controller sets one before Go.</summary>
        Set
    }

    /// <summary>
    /// An event from the controller: Go, with the parameters as they stood, a declared command,
    /// or a parameter set.
    /// </summary>
    public class ControlEvent
    {
        /// <summary>Gets or sets whether this is Go, a command or a set.</summary>
        public ControlEventKind Kind { get; set; }

        /// <summary>Gets or sets the command's or parameter's name; "go" for Go.</summary>
        public string Name { get; set; }

        /// <summary>Gets or sets the command's arguments or the parameter's new value as the
        /// controller sent them; empty for Go.</summary>
        public string Arguments { get; set; }

        /// <summary>Gets or sets the subject ID, from ControlServer.</summary>
        public string SubjectId { get; set; }

        /// <summary>Gets or sets the session ID, from ControlServer.</summary>
        public string SessionId { get; set; }

        /// <summary>Gets or sets every parameter's value as it stood when the event was sent.</summary>
        public Dictionary<string, string> Parameters { get; set; }

        /// <summary>Gets or sets when the event was sent, in UTC.</summary>
        public DateTime Time { get; set; }

        /// <summary>The event in one line: Go with its parameters, the command and its arguments,
        /// or the parameter and its new value.</summary>
        /// <returns>The line.</returns>
        public override string ToString()
        {
            return Kind == ControlEventKind.Go ? "Go " + string.Join(", ", Parameters.Select(p => p.Key + "=" + p.Value))
                : Kind == ControlEventKind.Set ? "Set " + Name + "=" + Arguments
                : "Command " + Name + " " + Arguments;
        }
    }

    /// <summary>
    /// The controller asked the workflow to stop. Ends the workflow through Rx's error path,
    /// as Watchdog does, so every subscription is disposed and every file closed.
    /// </summary>
    public class ExperimentStoppedException : Exception
    {
        /// <summary>Initializes a new instance with the controller's reason.</summary>
        /// <param name="message">Why the experiment was stopped.</param>
        public ExperimentStoppedException(string message) : base(message) { }
    }

    /// <summary>
    /// The one experiment's state, shared by ControlServer and the report operators in the same process.
    /// </summary>
    public sealed class ControlState
    {
        /// <summary>The running ControlServer's state, or null when none is running. Other
        /// packages read it (OpenRigs.Manifest records the settings at Go and the stop's reason).</summary>
        public static ControlState Current { get; internal set; }
        readonly object gate = new object();

        /// <summary>Gets the experiment's phase.</summary>
        public ExperimentPhase Phase { get; private set; } = ExperimentPhase.Starting;

        /// <summary>Gets when the phase last moved, in UTC.</summary>
        public DateTime PhaseSince { get; private set; } = DateTime.UtcNow;

        /// <summary>Gets whether Go has been taken; parameters are fixed from then on.</summary>
        public bool GoRequested { get; internal set; }

        /// <summary>Gets the subject ID.</summary>
        public string SubjectId { get; }

        /// <summary>Gets the session ID.</summary>
        public string SessionId { get; }

        /// <summary>Gets the declared parameters, with their values as they stand.</summary>
        public List<Parameter> Parameters { get; }

        /// <summary>Gets the declared commands.</summary>
        public List<Command> Commands { get; }

        /// <summary>Gets the latest value of each metric reported.</summary>
        public Dictionary<string, string> Metrics { get; } = new Dictionary<string, string>();

        /// <summary>The Go event, with every parameter as it stood; null before Go.</summary>
        public ControlEvent Go { get; internal set; }

        /// <summary>The why the controller's stop gave; null unless the controller stopped the experiment.</summary>
        public string StopReason { get; internal set; }

        /// <summary>Who launched the experiment, as the controller's Go named them; null if it named nobody.</summary>
        public Person LaunchedBy { get; internal set; }

        /// <summary>Who sent Go, as the controller named them; null if it named nobody.</summary>
        public Person GoBy { get; internal set; }

        /// <summary>Who sent the stop, as the controller named them; null if it named nobody (a
        /// controller's own stop after Finished, or no stop).</summary>
        public Person StoppedBy { get; internal set; }

        /// <summary>When the workflow reported Finished; null if it never did. Kept because the
        /// phase moves on to Stopping when the controller stops a finished experiment, as rig-control
        /// does after every run, and a run that finished is still one that finished.</summary>
        public DateTime? FinishedAt { get; private set; }

        internal ControlState(ControlServer server)
        {
            SubjectId = server.SubjectId ?? "";
            SessionId = server.SessionId ?? "";
            Parameters = server.Parameters.Select(p => new Parameter { Name = p.Name, Value = p.Value ?? "", Description = p.Description ?? "", Minimum = p.Minimum, Maximum = p.Maximum, WholeNumber = p.WholeNumber }).ToList();
            Commands = server.Commands.Select(c => new Command { Name = c.Name, Description = c.Description ?? "" }).ToList();
        }

        /// <summary>The state of the running ControlServer, or an error naming what is missing.</summary>
        /// <param name="operatorName">The operator asking, named in the error.</param>
        /// <returns>The running ControlServer's state.</returns>
        public static ControlState Require(string operatorName)
        {
            var state = Current;
            if (state == null)
            {
                throw new InvalidOperationException(operatorName + " needs a running ControlServer (the ExperimentControl include) in this workflow.");
            }
            return state;
        }

        /// <summary>
        /// Moves the phase forward. A report of an earlier phase is ignored, so a reporter
        /// left on a stream that keeps running (Ready on every frame) cannot undo Recording.
        /// Recording or Finished before Go is refused, as status would then say it of an
        /// experiment nobody has released (S2-I1).
        /// </summary>
        /// <param name="phase">The phase reported.</param>
        /// <exception cref="InvalidOperationException">Recording or Finished reported before Go.</exception>
        public void SetPhase(ExperimentPhase phase)
        {
            lock (gate)
            {
                if (phase <= Phase) return;
                if ((phase == ExperimentPhase.Recording || phase == ExperimentPhase.Finished) && !GoRequested)
                    throw new InvalidOperationException("The workflow reported " + phase + " before Go: report Recording on the experiment folder and Finished on the end of the run, both of which come after Go (ExperimentGo).");
                Phase = phase;
                PhaseSince = DateTime.UtcNow;
                if (phase == ExperimentPhase.Finished) FinishedAt = PhaseSince;
            }
        }

        /// <summary>Records the latest value of a metric.</summary>
        /// <param name="name">The metric's name.</param>
        /// <param name="value">Its value, as text.</param>
        public void SetMetric(string name, string value)
        {
            lock (gate) { Metrics[name] = value; }
        }

        internal T Locked<T>(Func<T> read)
        {
            lock (gate) { return read(); }
        }

        // The parameters a controller has set: the workflow's own default no longer replaces them.
        internal readonly HashSet<string> SetByController = new HashSet<string>();

        // How many times each command was taken, for status: after a command whose reply was lost,
        // a controller compares the count to tell whether it ran (S2-L2).
        internal readonly Dictionary<string, int> Taken = new Dictionary<string, int>();

        /// <summary>The named declared parameter; an error naming the declared ones if there is none.</summary>
        /// <param name="name">The parameter's name.</param>
        /// <param name="operatorName">The operator asking, named in the error.</param>
        /// <returns>The parameter.</returns>
        internal Parameter RequireParameter(string name, string operatorName)
        {
            var parameter = Parameters.FirstOrDefault(p => p.Name == name);
            if (parameter == null)
            {
                var declared = Parameters.Count == 0 ? "none" : string.Join(", ", Parameters.Select(p => p.Name));
                throw new ArgumentException(operatorName + ": no parameter named '" + name + "' was declared on ControlServer. Declared: " + declared + ".");
            }
            return parameter;
        }

        /// <summary>Sets a parameter's value from the workflow (a rig setting), unless a controller
        /// has set it or Go has been taken, so status shows the value the hardware uses.</summary>
        /// <param name="name">The parameter's name.</param>
        /// <param name="value">The value, as text.</param>
        public void SetDefault(string name, string value)
        {
            var parameter = RequireParameter(name, "ParameterDefault");
            lock (gate)
            {
                if (!GoRequested && !SetByController.Contains(name)) parameter.Value = value;
            }
        }

        /// <summary>Sets the highest value a controller's set may give a parameter, as the
        /// workflow learns it (an exposure that must fit the trigger period).</summary>
        /// <param name="name">The parameter's name.</param>
        /// <param name="maximum">The highest value.</param>
        public void SetMaximum(string name, double maximum)
        {
            var parameter = RequireParameter(name, "ParameterMaximum");
            lock (gate) { parameter.Maximum = maximum; }
        }

        internal JObject Status()
        {
            lock (gate)
            {
                return new JObject
                {
                    ["ok"] = true,
                    ["phase"] = Phase.ToString(),
                    ["since"] = PhaseSince.ToString("o"),
                    ["subject_id"] = SubjectId,
                    ["session_id"] = SessionId,
                    ["go_requested"] = GoRequested,
                    ["launched_by"] = Person.ToJson(LaunchedBy),
                    ["go_by"] = Person.ToJson(GoBy),
                    ["stopped_by"] = Person.ToJson(StoppedBy),
                    ["parameters"] = new JArray(Parameters.Select(p => new JObject { ["name"] = p.Name, ["value"] = p.Value, ["description"] = p.Description })),
                    ["commands"] = new JArray(Commands.Select(c => new JObject { ["name"] = c.Name, ["description"] = c.Description, ["taken"] = Taken.TryGetValue(c.Name ?? "", out var n) ? n : 0 })),
                    ["metrics"] = JObject.FromObject(Metrics)
                };
            }
        }
    }

    /// <summary>
    /// Answers a controller over a ZeroMQ reply socket: status, parameter changes while ready,
    /// declared commands, Go and Stop. Emits Go (with the parameters as they stood) and each
    /// command as a ControlEvent; Stop ends the workflow with ExperimentStoppedException.
    /// One request is one JSON object with "cmd"; one reply is {"ok": true, ...} or
    /// {"ok": false, "why": "..."}.
    /// </summary>
    [Combinator(MethodName = nameof(Generate))]
    [WorkflowElementCategory(ElementCategory.Source)]
    [Description("Answers an experiment controller over ZeroMQ: status, parameters while ready, declared commands, Go and Stop.")]
    public class ControlServer
    {
        /// <summary>
        /// The reply socket's endpoint, in Bonsai.ZeroMQ's form: a leading @ binds, &gt; connects.
        /// </summary>
        [Description("The reply socket endpoint. @ binds, > connects, as in Bonsai.ZeroMQ. Local only by default: the rig agent relays, and checks the Lab PIN.")]
        public string ConnectionString { get; set; } = "@tcp://127.0.0.1:5561";

        /// <summary>Gets or sets the subject (mouse) ID.</summary>
        [Description("The subject (mouse) ID, reported in status and carried on the Go event.")]
        public string SubjectId { get; set; }

        /// <summary>Gets or sets the session ID.</summary>
        [Description("The session ID, reported in status and carried on the Go event.")]
        public string SessionId { get; set; }

        /// <summary>
        /// The settings a controller may change until Go. Each is read in the workflow with ParameterValue.
        /// </summary>
        [Description("The settings a controller may change until Go, with their defaults.")]
        public Collection<Parameter> Parameters { get; set; } = new Collection<Parameter>();

        /// <summary>
        /// The commands a controller may send while ready or recording. Each is taken in the workflow with OnCommand.
        /// </summary>
        [Description("The commands a controller may send while ready or recording.")]
        public Collection<Command> Commands { get; set; } = new Collection<Command>();

        /// <summary>Opens the reply socket and answers the controller until the workflow stops.</summary>
        /// <returns>Go, once, and each declared command as the controller sends it.</returns>
        public IObservable<ControlEvent> Generate()
        {
            return Observable.Create<ControlEvent>(observer =>
            {
                // The second of two with one name was shadowed by the first in set and command (S2-L2).
                var twice = Twice("parameters", Parameters.Select(p => p.Name)) ?? Twice("commands", Commands.Select(c => c.Name));
                if (twice != null)
                {
                    observer.OnError(new InvalidOperationException(twice));
                    return Disposable.Empty;
                }
                var state = new ControlState(this);
                ControlState.Current = state;
                var cancel = new CancellationTokenSource();
                var endpoint = ConnectionString ?? "";
                var thread = new Thread(() => Serve(endpoint, state, observer, cancel.Token))
                {
                    IsBackground = true,
                    Name = "OpenRigs.Control"
                };
                thread.Start();
                return Disposable.Create(() =>
                {
                    cancel.Cancel();
                    // A stop, or an error opening the socket, ends the sequence on the server's
                    // own thread, and Rx disposes it there: a Join would only wait out its timeout.
                    if (Thread.CurrentThread != thread) thread.Join(2000);
                    if (ControlState.Current == state) ControlState.Current = null;
                });
            });
        }

        static void Serve(string endpoint, ControlState state, IObserver<ControlEvent> observer, CancellationToken cancel)
        {
            ResponseSocket socket;
            try
            {
                socket = new ResponseSocket();
                if (endpoint.StartsWith("@")) socket.Bind(endpoint.Substring(1));
                else if (endpoint.StartsWith(">")) socket.Connect(endpoint.Substring(1));
                else throw new ArgumentException("ConnectionString must start with @ (bind) or > (connect): " + endpoint);
            }
            catch (Exception error)
            {
                observer.OnError(error);
                return;
            }

            using (socket)
            {
                while (!cancel.IsCancellationRequested)
                {
                    List<string> frames = null;
                    Exception stop = null;
                    ControlEvent emit = null;
                    JObject reply;
                    // The socket's own errors end the sequence through the observer, as the
                    // bind does: unhandled on this thread they would end Bonsai with no file
                    // closed. The OnNext below stays outside, so a throw from a downstream
                    // node is fixed in that node.
                    try
                    {
                        // Every frame of the request is taken: a reply sent with one left unread throws.
                        if (!socket.TryReceiveMultipartStrings(TimeSpan.FromMilliseconds(100), ref frames)) continue;
                        if (frames.Count != 1) reply = Refuse("one frame per request, got " + frames.Count);
                        else
                        {
                            try { reply = Handle(frames[0], state, out emit, out stop); }
                            catch (Exception error) { reply = Refuse(error.Message); }
                        }
                        socket.SendFrame(reply.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    catch (Exception error)
                    {
                        observer.OnError(error);
                        return;
                    }
                    // After the reply: everything downstream of Go (the experiment folder, file
                    // writers, anything that waits) runs on this thread, and a reply held
                    // behind it reads to the controller as a workflow that did not answer.
                    if (emit != null) observer.OnNext(emit);
                    if (stop != null)
                    {
                        observer.OnError(stop);
                        return;
                    }
                }
            }
        }

        static JObject Handle(string text, ControlState state, out ControlEvent emit, out Exception stop)
        {
            emit = null;
            stop = null;
            JObject request;
            try { request = JObject.Parse(text); }
            catch (Exception) { return Refuse("not a JSON object: " + text); }
            var cmd = (string)request["cmd"] ?? "";
            switch (cmd)
            {
                case "status":
                    return state.Status();

                case "set":
                {
                    var name = (string)request["name"] ?? "";
                    var value = (string)request["value"] ?? "";
                    var phase = state.Locked(() => state.Phase);
                    if (phase != ExperimentPhase.Starting && phase != ExperimentPhase.Ready)
                        return Refuse("parameters can only change before Go (phase " + phase + ")");
                    if (state.Locked(() => state.GoRequested))
                        return Refuse("Go has already been requested");
                    var parameter = state.Parameters.FirstOrDefault(p => p.Name == name);
                    if (parameter == null)
                        return Refuse("no parameter named '" + name + "'; this workflow declares: " + Names(state.Parameters.Select(p => p.Name)));
                    // Under the lock: ParameterMaximum may move the limit from the workflow's thread.
                    var refusal = state.Locked(() => parameter.Refusal(value));
                    if (refusal != null) return Refuse(refusal);
                    state.Locked(() =>
                    {
                        parameter.Value = value;
                        return state.SetByController.Add(name);
                    });
                    // Emitted so a setting the hardware takes live (exposure) follows it while ready.
                    emit = new ControlEvent
                    {
                        Kind = ControlEventKind.Set,
                        Name = name,
                        Arguments = value,
                        SubjectId = state.SubjectId,
                        SessionId = state.SessionId,
                        Parameters = state.Locked(() => state.Parameters.ToDictionary(p => p.Name, p => p.Value)),
                        Time = DateTime.UtcNow
                    };
                    return Ok();
                }

                case "go":
                {
                    var phase = state.Locked(() => state.Phase);
                    if (phase != ExperimentPhase.Ready)
                        return Refuse("not ready (phase " + phase + ")");
                    // One lock: the snapshot, the check and GoRequested, so a ParameterDefault
                    // or ParameterMaximum landing between them cannot show Go a value it did not
                    // take. A value set before the workflow learned the limit (ParameterMaximum
                    // from the open camera) is refused here, naming it, rather than recorded.
                    string outOfRange = null;
                    Dictionary<string, string> parameters = null;
                    var refused = state.Locked(() =>
                    {
                        if (state.GoRequested) return "Go has already been requested";
                        foreach (var p in state.Parameters)
                        {
                            // An empty value is one nothing set (coen-behaviour-box's ExposureTime and
                            // Gain until rig.yml fills them): the workflow's own default, as before.
                            if (string.IsNullOrEmpty(p.Value)) continue;
                            outOfRange = p.Refusal(p.Value);
                            if (outOfRange != null) return outOfRange + "; set it again";
                        }
                        parameters = state.Parameters.ToDictionary(p => p.Name, p => p.Value);
                        state.GoRequested = true;
                        return null;
                    });
                    if (refused != null) return Refuse(refused);
                    // Named on Go, the moment the experiment becomes a record: the workflow was
                    // started by whoever launched it, and is released by whoever sends this.
                    state.LaunchedBy = Person.From(request["launched_by"]);
                    state.GoBy = Person.From(request["by"]);
                    emit = new ControlEvent
                    {
                        Kind = ControlEventKind.Go,
                        Name = "go",
                        Arguments = "",
                        SubjectId = state.SubjectId,
                        SessionId = state.SessionId,
                        Parameters = parameters,
                        Time = DateTime.UtcNow
                    };
                    state.Go = emit;
                    return Ok();
                }

                case "command":
                {
                    var name = (string)request["name"] ?? "";
                    var args = (string)request["args"] ?? "";
                    var phase = state.Locked(() => state.Phase);
                    if (phase != ExperimentPhase.Ready && phase != ExperimentPhase.Recording)
                        return Refuse("commands are taken while ready or recording (phase " + phase + ")");
                    if (!state.Commands.Any(c => c.Name == name))
                        return Refuse("no command named '" + name + "'; this workflow declares: " + Names(state.Commands.Select(c => c.Name)));
                    state.Locked(() => state.Taken[name] = (state.Taken.TryGetValue(name, out var taken) ? taken : 0) + 1);
                    emit = new ControlEvent
                    {
                        Kind = ControlEventKind.Command,
                        Name = name,
                        Arguments = args,
                        SubjectId = state.SubjectId,
                        SessionId = state.SessionId,
                        Parameters = state.Locked(() => state.Parameters.ToDictionary(p => p.Name, p => p.Value)),
                        Time = DateTime.UtcNow
                    };
                    return Ok();
                }

                case "stop":
                {
                    var why = (string)request["why"] ?? "stopped by the controller";
                    // The person first: the Manifest's stop poll (local.14) fires on StopReason and
                    // then reads StoppedBy, so the other order could write the stop without them.
                    state.StoppedBy = Person.From(request["by"]);
                    state.StopReason = why;
                    state.SetPhase(ExperimentPhase.Stopping);
                    stop = new ExperimentStoppedException("Experiment stopped: " + why);
                    return Ok();
                }

                default:
                    return Refuse("unknown cmd '" + cmd + "'; one of status, set, go, command, stop");
            }
        }

        // Why two of ControlServer's parameters or commands share a name, naming both by position; null when none do.
        static string Twice(string what, IEnumerable<string> names)
        {
            var list = names.Select(n => n ?? "").ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var j = list.IndexOf(list[i], i + 1);
                if (j >= 0)
                    return "ControlServer's " + what + " " + (i + 1) + " and " + (j + 1) + " are both named '" + list[i] + "', so the second could never be reached: rename or remove one.";
            }
            return null;
        }

        static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count == 0 ? "none" : string.Join(", ", list);
        }

        static JObject Ok() { return new JObject { ["ok"] = true }; }

        static JObject Refuse(string why) { return new JObject { ["ok"] = false, ["why"] = why }; }
    }
}
