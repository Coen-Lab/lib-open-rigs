using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using Bonsai;
using Bonsai.Expressions;
using OpenRigs.Control;
using OpenRigs.Manifest;
using OpenRigs.Shared;

// Runs SessionManifest's writer and recorder against a temporary session folder, without Bonsai's
// editor or hardware: exit 0 when every check passes. dotnet run --project src\OpenRigs.Manifest.Tests
namespace OpenRigs.Manifest.Tests
{
    static class Program
    {
        static int failed;

        static void Check(string what, bool ok, string why)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what + (ok ? "" : " - " + why));
            if (!ok) failed++;
        }

        static string Field(string json, string path)
        {
            // "a.b.c" into a pretty-printed manifest: enough for these checks, not a parser.
            var pos = 0;
            foreach (var part in path.Split('.'))
            {
                var m = new Regex("\"" + Regex.Escape(part) + "\": ").Match(json, pos);
                if (!m.Success) return null;
                pos = m.Index + m.Length;
            }
            var value = new Regex("^(\"(?:[^\"\\\\]|\\\\.)*\"|[^,\\n]+)").Match(json.Substring(pos));
            if (!value.Success) return null;
            var v = value.Groups[1].Value;
            return v.StartsWith("\"") ? v.Substring(1, v.Length - 2).Replace("\\\\", "\\") : v;
        }

        // A running ControlServer's state, as SessionControl leaves it: ControlState's constructor
        // and setters are the server's own, so a test reaches them by reflection.
        static ControlState Controller()
        {
            var ctor = typeof(ControlState).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ControlServer) }, null);
            var state = (ControlState)ctor.Invoke(new object[] { new ControlServer { SubjectId = "TC002", SessionId = "003B1" } });
            Set(null, "Current", state);
            state.SetPhase(SessionPhase.Ready);
            Set(state, "Go", new ControlEvent
            {
                Kind = ControlEventKind.Go, SubjectId = "TC002", SessionId = "003B1", Time = DateTime.UtcNow,
                Parameters = new Dictionary<string, string> { { "RunLengthMins", "30" } }
            });
            return state;
        }

        static void Set(ControlState state, string property, object value)
        {
            typeof(ControlState).GetProperty(property).SetValue(state, value);
        }

        // The controller's stop, as ControlServer handles {"cmd": "stop", "why": ...}.
        static void Stop(ControlState state, string why)
        {
            Set(state, "StopReason", why);
            state.SetPhase(SessionPhase.Stopping);
        }

        // One session with no run input: the folder appears, then whatever the controller does, then the workflow ends.
        static string Session(string root, string name, ControlState control, Action<ControlState> during)
        {
            var folder = Path.Combine(root, "sub-TC002", "ses-" + name + "_date-2026-10-30T19-57-12");
            var path = new Subject<string>();
            var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
            var subscription = recorder.Process(path).Subscribe(_ => { }, _ => { });
            if (control != null) control.SetPhase(SessionPhase.Recording);
            path.OnNext(folder);
            if (during != null) during(control);
            subscription.Dispose();
            Set(null, "Current", null);
            return File.ReadAllText(Path.Combine(folder, ManifestWriter.FileName));
        }

        static void RunGit(string folder, string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", arguments) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException("git " + arguments + " exited " + p.ExitCode);
            }
        }

        // A throwaway repository: clean, then an edit, a rename and an untracked file.
        static void DirtySource(string root)
        {
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            File.WriteAllText(Path.Combine(repo, "src", "main.bonsai"), "<a/>\n");
            File.WriteAllText(Path.Combine(repo, "old name.txt"), "x\n");
            RunGit(repo, "init -q");
            RunGit(repo, "add -A");
            RunGit(repo, "-c user.name=t -c user.email=t@t commit -q -m one");
            var workflow = Path.Combine(repo, "src", "main.bonsai");
            var clean = Json.Serialize(ManifestWriter.SourceSection(workflow, repo));
            Check("a clean checkout is not dirty, changes nothing and has no diff hash", Field(clean, "dirty") == "false" && clean.Contains("\"changed\": []") && Field(clean, "diff_sha256") == "null", clean);
            File.WriteAllText(workflow, "<b/>\n");
            RunGit(repo, "mv \"old name.txt\" \"new name.txt\"");
            File.WriteAllText(Path.Combine(repo, "scratch.txt"), "untracked\n");
            var dirty = Json.Serialize(ManifestWriter.SourceSection(workflow, repo));
            var changed = Regex.Match(dirty, "\"changed\": \\[([^\\]]*)\\]").Groups[1].Value;
            var names = Regex.Matches(changed, "\"([^\"]*)\"").Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n).ToList();
            Check("a dirty checkout lists each changed tracked file, a rename by its new name, untracked files left out", Field(dirty, "dirty") == "true" && string.Join(",", names) == "new name.txt,src/main.bonsai", changed);
            var hash = Field(dirty, "diff_sha256");
            Check("... and hashes the diff", hash != null && Regex.IsMatch(hash, "^[0-9a-f]{64}$"), hash);
            Check("... the same diff giving the same hash", Field(Json.Serialize(ManifestWriter.SourceSection(workflow, repo)), "diff_sha256") == hash, "changed");
            File.WriteAllText(workflow, "<c/>\n");
            Check("... and another diff another", Field(Json.Serialize(ManifestWriter.SourceSection(workflow, repo)), "diff_sha256") != hash, "the same");
            // git writes its objects read-only, which would stop the test folder being deleted.
            foreach (var file in Directory.GetFiles(repo, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(repo, true);
        }

        static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "openrigs-manifest-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Console.WriteLine("How the run ended, with no run input:");
                var finishedThenStopped = Session(root, "a", Controller(), c => { c.SetPhase(SessionPhase.Finished); Stop(c, "run finished"); });
                Check("a run that finished, then was stopped by rig-control, is completed", Field(finishedThenStopped, "run.outcome") == "completed", Field(finishedThenStopped, "run.outcome"));
                Check("... with no stop reason", Field(finishedThenStopped, "run.stop_reason") == "null", Field(finishedThenStopped, "run.stop_reason"));
                var finished = Session(root, "b", Controller(), c => c.SetPhase(SessionPhase.Finished));
                Check("a run that finished is completed", Field(finished, "run.outcome") == "completed", Field(finished, "run.outcome"));
                var stopped = Session(root, "c", Controller(), c => Stop(c, "mouse unwell"));
                Check("a controller stop before Finished is stopped, with its reason", Field(stopped, "run.outcome") == "stopped" && Field(stopped, "run.stop_reason") == "mouse unwell", Field(stopped, "run.outcome") + " " + Field(stopped, "run.stop_reason"));
                var early = Session(root, "d", Controller(), null);
                Check("a workflow that ends before Finished, unstopped, is ended-early", Field(early, "run.outcome") == "ended-early", Field(early, "run.outcome"));
                var alone = Session(root, "e", null, null);
                Check("without SessionControl it is stopped", Field(alone, "run.outcome") == "stopped" && Field(alone, "control") == "null", Field(alone, "run.outcome"));
                Check("the settings at Go are recorded", Field(finished, "control.parameters.RunLengthMins") == "30", Field(finished, "control.parameters.RunLengthMins"));
                // 0.1.0-local.5: who launched, released and stopped the session, as the controller named them.
                var named = Session(root, "h", Controller(), c =>
                {
                    Set(c, "LaunchedBy", new Person { Id = "PipC", Name = "Pip Coen" });
                    Set(c, "GoBy", new Person { Id = "JoB", Name = "Jo Bloggs" });
                    Set(c, "StoppedBy", new Person { Id = "", Name = "Jo Bloggs" });
                    Stop(c, "stopped by Jo Bloggs");
                });
                Check("control names who launched the session and who sent Go", Field(named, "control.launched_by.id") == "PipC" && Field(named, "control.launched_by.name") == "Pip Coen" && Field(named, "control.go_by.id") == "JoB", named);
                Check("... and who stopped it", Field(named, "control.stopped_by.name") == "Jo Bloggs" && Field(named, "control.stopped_by.id") == "", Field(named, "control.stopped_by.name"));
                Check("a controller that names nobody leaves them null", Field(finished, "control.launched_by") == "null" && Field(finished, "control.go_by") == "null" && Field(finished, "control.stopped_by") == "null", Field(finished, "control.launched_by"));
                Check("rig.os is the real Windows version, not the 6.2 Windows tells an undeclared program", !Field(finished, "rig.os").Contains("6.2.9200"), Field(finished, "rig.os"));
                // 0.1.0-local.6: rig.yml is in config_files only, not copied again into rig.
                var rigKeys = string.Join(",", ManifestWriter.RigSection().Select(p => p.Key));
                Check("rig is the machine alone", rigKeys == "machine,user,os", rigKeys);
                // 0.1.0-local.7: a config file by path and hash; its settings are the workflow's to log.
                var cfgPath = Path.Combine(root, "cfg.yml");
                File.WriteAllText(cfgPath, "a: 1\n");
                var cfgKeys = string.Join(",", ManifestWriter.ConfigFile(cfgPath, root).Select(p => p.Key));
                Check("a config file is its path and sha256, not its text", cfgKeys == "path,sha256", cfgKeys);
                var map = new ExpectedEntry { Path = "Board/Board.yml" };
                var board = new ExpectedEntry { Path = "Board/" };
                var both = new[] { board, map };
                Check("a folder is not met by a file another entry names", ExpectedPlan.Matches(board, new[] { "Board/Board.yml" }, both) == 0
                    && ExpectedPlan.Matches(map, new[] { "Board/Board.yml" }, both) == 1, "the register map stood in for the board's data");
                Check("... and is by any other file in it", ExpectedPlan.Matches(board, new[] { "Board/Board.yml", "Board/Board_44_x.bin" }, both) == 1, "not met");
                // 0.1.0-local.10: the writer and its version, and what a dirty checkout changed.
                // The version the build stamped: open-rigs' build sets it (scripts\build-package.ps1
                // passes the .csproj's <Version>; a plain build is 42.42.42-dev0).
                var built = ((AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(ManifestWriter).Assembly, typeof(AssemblyInformationalVersionAttribute))).InformationalVersion.Split('+')[0];
                Check("writer names the package and its version as built", Field(finished, "writer.package") == "OpenRigs.Manifest" && Field(finished, "writer.version") == built, Field(finished, "writer.version") + " vs " + built);
                DirtySource(root);

                // Round 5: the manifest reads OpenRigs.Control by name, so that it does not depend on it;
                // the checks above run through that read, and a renamed member fails here too.
                Check("every ControlState member the manifest reads is there", ControlSession.StateMembers.All(m => typeof(ControlState).GetProperty(m) != null),
                    string.Join(", ", ControlSession.StateMembers.Where(m => typeof(ControlState).GetProperty(m) == null)));
                Check("... and every Go event member", ControlSession.GoMembers.All(m => typeof(ControlEvent).GetProperty(m) != null),
                    string.Join(", ", ControlSession.GoMembers.Where(m => typeof(ControlEvent).GetProperty(m) == null)));
                Check("... and every Person member", ControlSession.PersonMembers.All(m => typeof(Person).GetProperty(m) != null),
                    string.Join(", ", ControlSession.PersonMembers.Where(m => typeof(Person).GetProperty(m) == null)));
                Check("... and the Stopping phase", Enum.IsDefined(typeof(SessionPhase), "Stopping"), "no Stopping");
                Check("the package does not reference OpenRigs.Control", !typeof(ManifestWriter).Assembly.GetReferencedAssemblies().Any(a => a.Name == "OpenRigs.Control"), "it does");

                Console.WriteLine("The run input:");
                {
                    var folder = Path.Combine(root, "sub-TC002", "ses-f_date-2026-10-30T19-57-12");
                    var path = new Subject<string>();
                    var run = new Subject<int>();
                    var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                    var completed = false;
                    var subscription = recorder.Process(path, run).Subscribe(_ => { }, _ => { }, () => completed = true);
                    run.OnNext(1); run.OnNext(2); run.OnCompleted();
                    Check("a run that ends before the folder exists writes nothing yet", !Directory.Exists(folder), folder);
                    path.OnNext(folder);
                    var text = File.ReadAllText(Path.Combine(folder, ManifestWriter.FileName));
                    Check("... and is written completed, with its items, when the folder appears", Field(text, "run.outcome") == "completed" && Field(text, "run.items") == "2", Field(text, "run.outcome") + " " + Field(text, "run.items"));
                    Check("... and the node completes", completed, "no OnCompleted");
                    subscription.Dispose();
                }

                Console.WriteLine("A write that fails:");
                {
                    // A file where the session folder should be, so Begin cannot make it.
                    var blocked = Path.Combine(root, "sub-TC002", "ses-g_date-2026-10-30T19-57-12");
                    Directory.CreateDirectory(Path.GetDirectoryName(blocked));
                    File.WriteAllText(blocked, "not a folder");
                    var path = new Subject<string>();
                    var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                    Exception error = null;
                    var subscription = recorder.Process(path).Subscribe(_ => { }, e => error = e);
                    Exception thrown = null;
                    try { path.OnNext(blocked); } catch (Exception e) { thrown = e; }
                    // Thrown, it went back up the thread that delivered the path, ControlServer's from Go,
                    // and killed Bonsai (round 2 audit, 1 Nov 2026).
                    Check("ends the node with its error rather than throwing to whoever named the folder", thrown == null && error is IOException, thrown != null ? "threw " + thrown.GetType().Name : "error " + error);
                    subscription.Dispose();
                }

                Console.WriteLine("Launch and parameters:");
                var launch = Json.Serialize(ManifestWriter.LaunchSection(new[] { "Bonsai.exe", "w.bonsai", "--property:SubjectId=A", "-p:SubjectId=B", "--property:subjectid=C", "--no-boot", "w.bonsai", "--@pipe", "x" }));
                Check("a repeated --property is one key, the last value winning", Regex.Matches(launch, "\"subjectid\"", RegexOptions.IgnoreCase).Count == 1 && launch.Contains("\"C\""), launch);
                Check("Bonsai's own relaunch arguments are left out", !launch.Contains("--no-boot") && !launch.Contains("--@pipe"), launch);

                Console.WriteLine("Writing:");
                var file = Path.Combine(root, "atomic.json");
                ManifestWriter.WriteAtomically(file, "one");
                ManifestWriter.WriteAtomically(file, "two");
                Check("a second write replaces the first and leaves no .tmp", File.ReadAllText(file) == "two" && !File.Exists(file + ".tmp"), File.ReadAllText(file));

                Console.WriteLine("Expected files:");
                var rigYml = Path.Combine(root, "rig.yml");
                File.WriteAllText(rigYml, "behaviorBoard:\n  main:\n    portName: COM3\ncamera:\n  box:\n    serialNumber: '1'\n  face:\n    serialNumber: '2'\nrootPath: C:\\data\n");
                var plan = ExpectedPlan.Create("auto;events.csv;Camera-*/", rigYml, n => n == "BehaviorBoard-Main.Data" || n == "Camera-Box" ? typeof(object) : null);
                var paths = string.Join(", ", plan.Entries.Select(e => e.Path));
                Check("auto expects each published device's folder, and a listed file", paths == "BehaviorBoard-Main/, Camera-Box/, events.csv", paths);
                Check("a device in rig.yml the workflow does not publish is noted, not expected", plan.Notes.Any(n => n.StartsWith("Camera-Face")), string.Join(" | ", plan.Notes));
                Check("a wildcard entry is noted, not expected", plan.Notes.Any(n => n.Contains("Camera-*/")) && !paths.Contains("*"), string.Join(" | ", plan.Notes));
                // A Harp device's data is HarpMessage, told by name: the manifest does not reference Bonsai.Harp.
                // 0.1.0-local.9: which entries are Harp devices is src\Shared\HarpRig.cs, the rule RecordHarpDevices
                // uses (a portName: null is none), and the register map is expected only when a loaded package decodes the kind.
                var harpYml = Path.Combine(root, "harp-rig.yml");
                File.WriteAllText(harpYml, "behaviorBoard:\n  main:\n    portName: COM3\n  idle:\n    portName: null\n" +
                    "licketySplit:\n  left:\n    portName: COM4\nclockSynchronizer:\n  sync:\n    portName: COM5\ncamera:\n  box:\n    serialNumber: '1'\n");
                var harpMessage = typeof(Bonsai.Harp.HarpMessage);
                Func<string, Type> harpPublished = n => n.EndsWith(".Data") && !n.StartsWith("Camera") ? harpMessage : n == "Camera-Box" || n == "Camera-Box.Data" ? typeof(object) : null;
                var unloaded = ExpectedPlan.Create("harp", harpYml, harpPublished);
                var unloadedPaths = string.Join(", ", unloaded.Entries.Select(e => e.Path));
                Check("harp expects each Harp device's record, and no register map no loaded package decodes", unloadedPaths == "BehaviorBoard-Main/BehaviorBoard-Main_device.json, LicketySplit-Left/LicketySplit-Left_device.json, ClockSynchronizer-Sync/ClockSynchronizer-Sync_device.json", unloadedPaths);
                Check("... noting why each map is not expected", unloaded.Notes.Any(n => n.StartsWith("BehaviorBoard-Main/BehaviorBoard-Main.yml") && n.Contains("decodes WhoAmI 1216")) && unloaded.Notes.Any(n => n.StartsWith("LicketySplit-Left/LicketySplit-Left.yml") && n.Contains("no WhoAmI is known for licketySplit")), string.Join(" | ", unloaded.Notes));
                Check("a portName: null is not a Harp device, and not noted", !unloadedPaths.Contains("Idle") && !unloaded.Notes.Any(n => n.Contains("Idle")), unloadedPaths + " | " + string.Join(" | ", unloaded.Notes));
                Console.WriteLine("        loaded " + LoadBehavior());
                var harp = ExpectedPlan.Create("auto;harp", harpYml, harpPublished);
                var harpPaths = string.Join(", ", harp.Entries.Where(e => e.Path.StartsWith("BehaviorBoard") || e.Path.StartsWith("Camera")).Select(e => e.Path));
                Check("with Harp.Behavior loaded, the behaviorBoard's register map is expected too", harpPaths == "BehaviorBoard-Main/, BehaviorBoard-Idle/, Camera-Box/, BehaviorBoard-Main/BehaviorBoard-Main_device.json, BehaviorBoard-Main/BehaviorBoard-Main.yml", harpPaths);
                Check("... and still not a clockSynchronizer's", harp.Entries.All(e => e.Path != "ClockSynchronizer-Sync/ClockSynchronizer-Sync.yml"), "expected");
                Check("... which do not count as the device's data", ExpectedPlan.Matches(harp.Entries[0], new[] { "BehaviorBoard-Main/BehaviorBoard-Main_device.json", "BehaviorBoard-Main/BehaviorBoard-Main.yml" }, harp.Entries) == 0, "counted");
                var notHarp = ExpectedPlan.Create("harp", rigYml, n => n == "BehaviorBoard-Main.Data" ? typeof(object) : null);
                Check("... and nothing for a device whose data is not HarpMessage", notHarp.Entries.Count == 0, string.Join(", ", notHarp.Entries.Select(e => e.Path)));
                // Round 5: rig.yml's devices are read by src\Shared\RigDevices.cs, as the live view reads them.
                var none = ExpectedPlan.Create("auto", Path.Combine(root, "absent", "rig.yml"), n => typeof(object));
                Check("no rig.yml expects nothing, and says so", none.Entries.Count == 0 && none.Notes.Any(n => n.StartsWith("No rig.yml at") && n.EndsWith("so no device's files are expected.")), string.Join(" | ", none.Notes));

                Console.WriteLine("ConfigRoot:");
                var other = ManifestWriter.LaunchSection(new[] { "Bonsai.exe", "--property:ConfigRoot=D:\\Test\\config" });
                Check("a launch ConfigRoot that is not where rig.yml was read is noted", ManifestWriter.ConfigRootNote(other, @"C:\Coen-Lab-Local\RigConfigs\RIG\rig.yml") != null, "no note");
                var same = ManifestWriter.LaunchSection(new[] { "Bonsai.exe", "--property:ConfigRoot=C:\\Coen-Lab-Local\\RigConfigs" });
                Check("... and one that is, is not", ManifestWriter.ConfigRootNote(same, @"C:\Coen-Lab-Local\RigConfigs\RIG\rig.yml") == null, "noted");

                Console.WriteLine("The editor's copy:");
                {
                    var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<WorkflowBuilder Version=\"2.9.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:mf=\"clr-namespace:OpenRigs.Manifest;assembly=OpenRigs.Manifest\" xmlns=\"https://bonsai-rx.org/2018/workflow\">\n" +
                        "  <Workflow><Nodes><Expression xsi:type=\"mf:SessionManifest\"><mf:IngestScript>x</mf:IngestScript><mf:ConfigFiles>a.yml</mf:ConfigFiles><mf:ExpectedFiles>b/</mf:ExpectedFiles></Expression></Nodes><Edges /></Workflow>\n</WorkflowBuilder>\n";
                    WorkflowBuilder builder;
                    using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    var copy = new WorkflowBuilder(builder.Workflow.ToInspectableGraph().FromInspectableGraph());
                    var saved = new StringWriter();
                    // Indented, as the editor saves: Bonsai's reader of a group's inner workflow expects it.
                    using (var writer = XmlWriter.Create(saved, new XmlWriterSettings { Indent = true })) WorkflowBuilder.Serializer.Serialize(writer, copy);
                    using (var reader = XmlReader.Create(new StringReader(saved.ToString()))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    var node = builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<SessionManifest>().Single();
                    Check("its settings survive the editor's copy, a save and a reload", node.IngestScript == "x" && node.ConfigFiles == "a.yml" && node.ExpectedFiles == "b/", node.IngestScript + " " + node.ConfigFiles + " " + node.ExpectedFiles);
                    Check("... in one settings node", node.Workflow.Count(n => ExpressionBuilder.Unwrap(n.Value) is SessionManifestSettings) == 1, "more than one");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
            Console.WriteLine(failed == 0 ? "All checks passed." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }

        // Loads Harp.Behavior only when called: in Main, the JIT could load it before the checks
        // that need it absent. The type, not its WhoAmI: a const is copied in and loads nothing.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static string LoadBehavior()
        {
            return typeof(Harp.Behavior.Device).Assembly.GetName().Name;
        }
    }
}
