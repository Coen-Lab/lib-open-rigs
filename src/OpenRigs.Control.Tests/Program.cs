using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json.Linq;
using OpenRigs.Control;

// Runs ControlServer on a local port and talks to it as a controller does, over a ZeroMQ request
// socket, without Bonsai's editor or hardware: exit 0 when every check passes.
// dotnet run --project src\OpenRigs.Control.Tests
namespace OpenRigs.Control.Tests
{
    static class Program
    {
        // Not 5561, so a rig agent's workflow running on this machine is left alone.
        const string Endpoint = "tcp://127.0.0.1:5641";
        static int failed;

        static void Check(string what, bool ok, string why)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (ok ? "" : " - " + why));
            if (!ok) failed++;
        }

        static JObject Ask(RequestSocket socket, object request)
        {
            socket.SendFrame(JObject.FromObject(request).ToString(Newtonsoft.Json.Formatting.None));
            string reply;
            if (!socket.TryReceiveFrameString(TimeSpan.FromSeconds(5), out reply)) return new JObject { ["ok"] = false, ["why"] = "no reply in 5 s" };
            return JObject.Parse(reply);
        }

        static bool Ok(JObject reply) { return (bool)reply["ok"]; }

        static Exception Catch(Action action)
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }

        static ControlServer Server()
        {
            return new ControlServer
            {
                ConnectionString = "@" + Endpoint,
                SubjectId = "TC002",
                SessionId = "003B1",
                Parameters = new Collection<Parameter>
                {
                    new Parameter { Name = "RunLengthMins", Value = "30", Minimum = 1, Maximum = 9999, WholeNumber = true },
                    new Parameter { Name = "RewardUl", Value = "2.5", Minimum = 0, Maximum = 10 },
                    new Parameter { Name = "Note", Value = "" },
                },
                Commands = new Collection<Command> { new Command { Name = "reward" } },
            };
        }

        static int Main()
        {
            Console.WriteLine("Parameters before Go:");
            var events = new System.Collections.Generic.List<ControlEvent>();
            Exception ended = null;
            var stoppedAt = new ManualResetEvent(false);
            var subscription = Server().Generate().Subscribe(e => { lock (events) events.Add(e); }, e => { ended = e; stoppedAt.Set(); });
            using (var socket = new RequestSocket(">" + Endpoint))
            {
                var state = ControlState.Current;
                Check("the running server's state is ControlState.Current", state != null, "null");
                Check("a whole number in range is taken", Ok(Ask(socket, new { cmd = "set", name = "RunLengthMins", value = "45" })), "refused");
                var half = Ask(socket, new { cmd = "set", name = "RunLengthMins", value = "2.5" });
                Check("a fraction for a whole-number parameter is refused, with the range", !Ok(half) && ((string)half["why"]).Contains("whole number from 1 to 9999"), half.ToString());
                Check("a number above the maximum is refused", !Ok(Ask(socket, new { cmd = "set", name = "RewardUl", value = "11" })), "taken");
                Check("NaN is refused for a parameter with limits", !Ok(Ask(socket, new { cmd = "set", name = "RewardUl", value = "NaN" })), "taken");
                Check("a parameter without limits takes any text", Ok(Ask(socket, new { cmd = "set", name = "Note", value = "left eye shut" })), "refused");
                Check("an undeclared parameter is refused", !Ok(Ask(socket, new { cmd = "set", name = "Nope", value = "1" })), "taken");

                // 0.1.0-local.10: a set is an event, for settings the hardware takes live while ready.
                Console.WriteLine("Live parameters before Go:");
                Thread.Sleep(200);
                ControlEvent[] sets;
                lock (events) sets = events.ToArray();
                var lengths = new OnParameterSet { Name = "RunLengthMins" }.Process(sets.ToObservable()).ToList().Wait();
                Check("each accepted set is an event, and only accepted ones", sets.Length == 2 && sets.All(e => e.Kind == ControlEventKind.Set), string.Join("; ", sets.Select(e => e.ToString())));
                Check("OnParameterSet passes the named parameter's new values", lengths.SequenceEqual(new[] { "45" }), string.Join(",", lengths));
                Check("OnGo drops sets", !new OnGo().Process(sets.ToObservable()).ToList().Wait().Any(), "passed one");
                new ParameterDefault { Name = "RewardUl" }.Process(Observable.Return(3.5)).Wait();
                new ParameterDefault { Name = "RunLengthMins" }.Process(Observable.Return(60)).Wait();
                var defaults = Ask(socket, new { cmd = "status" })["parameters"];
                Check("ParameterDefault sets a parameter no controller has set, shown in status", (string)defaults[1]["value"] == "3.5", defaults.ToString());
                Check("... but leaves one a controller set", (string)defaults[0]["value"] == "45", defaults.ToString());
                new ParameterMaximum { Name = "RewardUl" }.Process(Observable.Return(5.0)).Wait();
                var over = Ask(socket, new { cmd = "set", name = "RewardUl", value = "6" });
                Check("ParameterMaximum lowers the limit a set is checked against", !Ok(over) && ((string)over["why"]).Contains("from 0 to 5"), over.ToString());
                var unknown = Catch(() => new ParameterDefault { Name = "Nope" }.Process(Observable.Return(1)).Wait());
                Check("ParameterDefault of an undeclared parameter stops, naming the declared ones", unknown is ArgumentException && unknown.Message.Contains("RunLengthMins, RewardUl, Note"), unknown == null ? "no error" : unknown.Message);

                Console.WriteLine("Go:");
                Check("Go is refused before Ready", !Ok(Ask(socket, new { cmd = "go" })), "taken");
                state.SetPhase(SessionPhase.Ready);
                // 0.1.0-local.11: a value taken before the workflow learned the limit is refused at Go, not recorded.
                Check("a set within the limit as it stands is taken", Ok(Ask(socket, new { cmd = "set", name = "RewardUl", value = "4" })), "refused");
                new ParameterMaximum { Name = "RewardUl" }.Process(Observable.Return(3.0)).Wait();
                var stale = Ask(socket, new { cmd = "go" });
                Check("Go is refused while a value is above a maximum learned after it, naming it", !Ok(stale) && ((string)stale["why"]).Contains("RewardUl must be") && ((string)stale["why"]).Contains("from 0 to 3") && ((string)stale["why"]).Contains("'4' is not"), stale.ToString());
                Check("... and not requested", !state.GoRequested, "requested");
                // A parameter with limits that nothing has set (coen-behaviour-box's ExposureTime until rig.yml fills it) does not block Go.
                state.Parameters.Add(new Parameter { Name = "Blank", Value = "", Description = "", Minimum = 1, Maximum = 2 });
                Check("an empty value is refused as a set", new Parameter { Name = "Blank", Minimum = 1, Maximum = 2 }.Refusal("") != null, "taken");
                Check("set again within it, Go goes on", Ok(Ask(socket, new { cmd = "set", name = "RewardUl", value = "2" })), "refused");
                Check("Go is taken once Ready", Ok(Ask(socket, new { cmd = "go", by = new { id = "JoB", name = "Jo Bloggs" }, launched_by = new { id = "PipC", name = "Pip Coen" } })), "refused");
                // 0.1.0-local.9: who launched and released the session, for the manifest.
                Check("Go keeps who launched the session and who sent it", state.LaunchedBy?.ToString() == "Pip Coen (PipC)" && state.GoBy?.ToString() == "Jo Bloggs (JoB)", state.LaunchedBy + " / " + state.GoBy);
                var named = Ask(socket, new { cmd = "status" });
                Check("status names them", (string)named["launched_by"]["id"] == "PipC" && (string)named["go_by"]["name"] == "Jo Bloggs" && named["stopped_by"].Type == JTokenType.Null, named.ToString());
                Check("... and only once", !Ok(Ask(socket, new { cmd = "go" })), "taken twice");
                Check("parameters cannot change after Go", !Ok(Ask(socket, new { cmd = "set", name = "Note", value = "x" })), "taken");
                Thread.Sleep(200);
                ControlEvent go;
                lock (events) go = events.Find(e => e.Kind == ControlEventKind.Go);
                Check("the Go event carries the parameters as they stood", go != null && go.Parameters["RunLengthMins"] == "45" && go.Parameters["Note"] == "left eye shut", go == null ? "no Go event" : go.ToString());
                Check("ControlState.Go is that event", state.Go == go, "differs");

                Console.WriteLine("Commands and status:");
                state.SetPhase(SessionPhase.Recording);
                Check("a declared command is taken while recording", Ok(Ask(socket, new { cmd = "command", name = "reward", args = "5" })), "refused");
                Check("an undeclared one is refused", !Ok(Ask(socket, new { cmd = "command", name = "flash" })), "taken");
                state.SetPhase(SessionPhase.Ready);
                var culture = Thread.CurrentThread.CurrentCulture;
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                try { new ReportMetric { Name = "water_ml" }.Process(Observable.Return(0.25)).Wait(); }
                finally { Thread.CurrentThread.CurrentCulture = culture; }
                var status = Ask(socket, new { cmd = "status" });
                Check("status reports the phase, which only moves forward", (string)status["phase"] == "Recording", (string)status["phase"]);
                Check("a metric is reported in the invariant culture, whatever the rig's", (string)status["metrics"]["water_ml"] == "0.25", status["metrics"]?.ToString());

                Console.WriteLine("Stop:");
                var watch = Stopwatch.StartNew();
                Check("stop is answered", Ok(Ask(socket, new { cmd = "stop", why = "mouse unwell", by = "Jo Bloggs" })), "refused");
                stoppedAt.WaitOne(5000);
                Check("... then ends the sequence with SessionStoppedException", ended is SessionStoppedException, ended == null ? "no error" : ended.GetType().Name);
                while (ControlState.Current != null && watch.ElapsedMilliseconds < 5000) Thread.Sleep(5);
                // Before 0.1.0-local.7 the server joined its own thread here and waited out the 2 s timeout.
                Check("... and the server is gone within a second, not after a 2 s wait on itself", watch.ElapsedMilliseconds < 1000, watch.ElapsedMilliseconds + " ms");
                Check("the stop's reason is kept", state.StopReason == "mouse unwell", state.StopReason);
                Check("... and who sent it, a name alone taken as a name", state.StoppedBy?.Name == "Jo Bloggs" && state.StoppedBy.Id == "", state.StoppedBy?.ToString());
            }

            Console.WriteLine("A controller that names nobody:");
            Check("no field, a JSON null, an empty object or a list is nobody", new JToken[] { null, JValue.CreateNull(), new JObject(), new JArray("x"), new JObject { ["id"] = new JObject() } }.All(t => Person.From(t) == null), "a person");
            Check("an ID alone is kept", Person.From(new JObject { ["id"] = "PipC" })?.Id == "PipC", "lost");
            subscription.Dispose();

            Console.WriteLine("Two servers on one port:");
            {
                var first = Server().Generate().Subscribe(_ => { }, _ => { });
                Exception second = null;
                var failedAt = new ManualResetEvent(false);
                var other = Server().Generate().Subscribe(_ => { }, e => { second = e; failedAt.Set(); });
                failedAt.WaitOne(5000);
                Check("the second ends with an error naming the port in use", second is NetMQ.AddressAlreadyInUseException, second == null ? "no error" : second.GetType().Name);
                other.Dispose();
                first.Dispose();
            }

            NetMQConfig.Cleanup(false);

            Console.WriteLine(failed == 0 ? "All checks passed." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }
    }
}
