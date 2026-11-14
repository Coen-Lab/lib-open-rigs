using System;
using System.Collections.Generic;
using System.Globalization;
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

// Runs ExperimentManifest's writer and recorder against a temporary experiment folder, without Bonsai's
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

        // A running ControlServer's state, as ExperimentControl leaves it: ControlState's constructor
        // and setters are the server's own, so a test reaches them by reflection.
        static ControlState Controller()
        {
            var ctor = typeof(ControlState).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ControlServer) }, null);
            var state = (ControlState)ctor.Invoke(new object[] { new ControlServer { SubjectId = "TC002", SessionId = "003B1" } });
            Set(null, "Current", state);
            state.SetPhase(ExperimentPhase.Ready);
            // Go taken, as ControlServer's go leaves it: Recording and Finished are refused before it.
            Set(state, "GoRequested", true);
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
            state.SetPhase(ExperimentPhase.Stopping);
        }

        // One experiment with no run input: the folder appears, then whatever the controller does, then the workflow ends.
        static string RunExperiment(string root, string name, ControlState control, Action<ControlState> during)
        {
            // The folder as the patched Logging package names it (ses-<Session>_<DateTime>, badd9ca).
            var folder = Path.Combine(root, "sub-TC002", "ses-" + name + "_2026-10-30T19-57-12");
            var path = new Subject<string>();
            var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
            var subscription = recorder.Process(path).Subscribe(_ => { }, _ => { });
            if (control != null) control.SetPhase(ExperimentPhase.Recording);
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

        // Stage 3 (6 Nov 2026): the workflow's trial columns file, copied into the manifest at Start.
        static void TrialColumnsFile(string root, string blankManifest)
        {
            Console.WriteLine("Trial columns:");
            Check("a workflow with TrialColumns blank has trial_columns null", Field(blankManifest, "trial_columns") == "null", Field(blankManifest, "trial_columns"));
            var file = Path.Combine(root, "trial-columns.yml");
            File.WriteAllText(file, "trialType:\n  type: int\n  values: {0: Visual, 1: Audio}\nitiSeconds:\n  type: double\n  unit: s\n  formerly: [itiDuration]\n");
            string note;
            var read = Json.Serialize(TrialColumns.Read("trial-columns.yml", root, out note));
            Check("a file is copied with its hash, each column as written", note == null && Field(read, "columns.trialType.values.1") == "Audio"
                && Field(read, "columns.itiSeconds.unit") == "s" && read.Contains("\"itiDuration\"") && Regex.IsMatch(Field(read, "sha256") ?? "", "^[0-9a-f]{64}$"), read);
            File.WriteAllText(file, "trialType:\n  type: integer\n");
            read = Json.Serialize(TrialColumns.Read(file, root, out note));
            Check("a column whose type is not int, double, bool or string is not copied, and noted", Field(read, "missing") == "true" && note != null && note.Contains("trialType"), read);
            // L1-L4: YAML's nulls were copied as the text "~" or "null", and names differing only in case passed.
            File.WriteAllText(file, "a:\n  type: int\n  unit: ~\n  description: null\nb:\n  type: int\n  unit: Null\n  description: NULL\nc:\n  type: int\n  unit:\n  description: 'null'\n");
            read = Json.Serialize(TrialColumns.Read(file, root, out note));
            Check("~, null, Null, NULL and empty are JSON null; a quoted 'null' is text", note == null
                && new[] { "a.unit", "a.description", "b.unit", "b.description", "c.unit" }.All(k => Field(read, "columns." + k) == "null") && read.Contains("\"description\": \"null\""), read);
            File.WriteAllText(file, "trialType:\n  type: int\nTrialType:\n  type: int\n");
            read = Json.Serialize(TrialColumns.Read(file, root, out note));
            Check("names differing only in case are not copied, and noted", Field(read, "missing") == "true" && note != null && note.Contains("trialType and TrialType differ only in case"), read);
            read = Json.Serialize(TrialColumns.Read("nowhere.yml", root, out note));
            Check("a missing file is noted, not an error", Field(read, "missing") == "true" && note != null, read);
            Check("blank reads nothing", TrialColumns.Read(" ", root, out note) == null && note == null, "read");
            var snapshot = ManifestWriter.Snapshot("", true, file);
            Check("Snapshot carries the section and its note", snapshot.TrialColumns != null && snapshot.Notes.Cast<string>().Any(n => n.StartsWith("Trial columns not read")), "missing");
            File.Delete(file);
        }

        static DateTime Utc(string iso)
        {
            return DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        }

        static JsonObject Object(params object[] pairs)
        {
            var o = new JsonObject();
            for (var i = 0; i < pairs.Length; i += 2) o.Add((string)pairs[i], pairs[i + 1]);
            return o;
        }

        // The records' contract (claude-tools docs\architecture\records.md): fixtures\manifest\v3\*.json are what this
        // writer produces from these inputs, byte for byte. --write-fixtures writes them; otherwise each is compared.
        static void Fixtures(bool write)
        {
            Console.WriteLine("Fixtures:");
            var repo = AppDomain.CurrentDomain.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "OpenRigs.sln"))) repo = Path.GetDirectoryName(repo.TrimEnd('\\'));
            if (repo == null) { Check("the repository holding the fixtures is found", false, "no OpenRigs.sln above " + AppDomain.CurrentDomain.BaseDirectory); return; }
            var folder = Path.Combine(repo, "fixtures", "manifest", "v" + ManifestWriter.Version);
            Directory.CreateDirectory(folder);

            var writer = Object("package", "OpenRigs.Manifest", "version", "0.1.1-coen-lab.15");
            var rig = Object("machine", "GEEK-BBOX1", "user", "Coen lab", "os", "Microsoft Windows 10.0.26100");
            var workflow = @"C:\Github\Coen-Lab\coen-behaviour-box\src\main.bonsai";
            Func<bool, JsonObject> source = dirty => Object("workflow", @"src\main.bonsai", "repo", "https://github.com/ucl-open/coen-behaviour-box.git",
                "root", @"C:\Github\Coen-Lab\coen-behaviour-box", "branch", "main", "commit", "9b77733c1d0e8f6a4b2c9d7e5f3a1b8c6d4e2f0a",
                "dirty", dirty, "changed", dirty ? new List<object> { "src/main.bonsai" } : new List<object>(),
                "diff_sha256", dirty ? "5d41402abc4b2a76b9719d911017c5925d41402abc4b2a76b9719d911017c592" : null, "bonsai", "2.9.0");
            var rigYml = @"C:\Coen-Lab-Local\RigConfigs\GEEK-BBOX1\rig.yml";
            var start = new StartSnapshot
            {
                Source = source(false),
                Launch = ManifestWriter.LaunchSection(new[] { "Bonsai.exe", workflow, "--no-editor", "--property:SubjectId=PCO1-01503", "--property:SessionId=004B1", "--property:LaunchId=rig-PCO1-01503-004B1-20261112T101500Z" }),
                ConfigFiles = new List<object> { Object("path", rigYml, "sha256", "9b1c0f5e2d7a4c8b6e3f1a0d9c8b7a6f5e4d3c2b1a0f9e8d7c6b5a4f3e2d1c0b") },
            };
            var launchId = ManifestWriter.LaunchProperty(start.Launch, ManifestWriter.LaunchIdProperty);
            var tom = new KeyValuePair<string, string>("TomC", "Tom C");
            var session = ManifestWriter.SessionSection(@"C:\Coen-Lab-Local\Acquired-Data\sub-PCO1-01503\ses-004B1_2026-11-12T10-15-02");
            var plan = new ExpectedPlan();
            plan.Entries.Add(new ExpectedEntry { Path = "BehaviorBoard-Main/", Why = "rig.yml behaviorBoard: main:, published as BehaviorBoard-Main" });
            plan.Entries.Add(new ExpectedEntry { Path = "Settings/Settings_Experiment_", Why = "listed in ExpectedFiles" });
            var withCamera = new ExpectedPlan();
            withCamera.Entries.AddRange(plan.Entries);
            withCamera.Entries.Add(new ExpectedEntry { Path = "Camera-Box/", Why = "rig.yml camera: box:, published as Camera-Box" });
            var device = ManifestWriter.FileEntry("BehaviorBoard-Main/BehaviorBoard-Main_device.json", 412, Utc("2026-11-12T10:15:03Z"), true);
            Func<bool, List<object>> allFiles = closed => new List<object>
            {
                ManifestWriter.FileEntry("BehaviorBoard-Main/BehaviorBoard-Main_2026-11-12T10-00-00.bin", 1048576, Utc("2026-11-12T10:45:02Z"), closed),
                device,
                ManifestWriter.FileEntry("Settings/Settings_Experiment_2026-11-12T10-15-40.json", 2210, Utc("2026-11-12T10:15:40Z"), true),
            };
            var go = Utc("2026-11-12T10:15:40Z");
            var started = Utc("2026-11-12T10:15:41Z");
            var none = new string[0];

            var records = new Dictionary<string, JsonObject>();
            var runningFiles = new List<object> { device };
            records["running"] = ManifestWriter.Document(Utc("2026-11-12T10:15:03Z"), "coen-behaviour-box", writer, rig, session, start,
                ManifestWriter.ControlSection(launchId, "PCO1-01503", "004B1", null, tom, null, null),
                ManifestWriter.RunSection(new RunEnd(RunEnd.Running, null), false, Utc("2026-11-12T10:15:03Z"), null, null, null),
                ManifestWriter.ExpectedSection(plan, runningFiles), none, runningFiles);
            var files = allFiles(true);
            records["completed"] = ManifestWriter.Document(Utc("2026-11-12T10:45:04Z"), "coen-behaviour-box", writer, rig, session, start,
                ManifestWriter.ControlSection(launchId, "PCO1-01503", "004B1", go, tom, tom, null),
                ManifestWriter.RunSection(new RunEnd(RunEnd.Completed, null), true, started, Utc("2026-11-12T10:45:03Z"), 88100, null),
                ManifestWriter.ExpectedSection(plan, files), none, files);
            records["ended-early"] = ManifestWriter.Document(Utc("2026-11-12T10:45:04Z"), "coen-behaviour-box", writer, rig, session, start,
                ManifestWriter.ControlSection(launchId, "PCO1-01503", "004B1", go, tom, tom, null),
                ManifestWriter.RunSection(new RunEnd(RunEnd.EndedEarly, RunEnd.EndedEarlyError), true, started, Utc("2026-11-12T10:20:00Z"), 12950, null),
                ManifestWriter.ExpectedSection(withCamera, files), new[] { "Camera-Box left no file" }, files);
            var openFiles = allFiles(false);
            records["stopped-not-final"] = ManifestWriter.Document(Utc("2026-11-12T10:31:10Z"), "coen-behaviour-box", writer, rig, session, start,
                ManifestWriter.ControlSection(launchId, "PCO1-01503", "004B1", go, tom, tom, new KeyValuePair<string, string>("", "someone at AV-DOME-CONTROL (no Lab PIN)")),
                ManifestWriter.RunSection(new RunEnd(RunEnd.Stopped, null), false, started, Utc("2026-11-12T10:31:10Z"), null, "Stop from the window"),
                ManifestWriter.ExpectedSection(plan, openFiles), none, openFiles);
            var bare = new StartSnapshot
            {
                Source = source(true),
                Launch = ManifestWriter.LaunchSection(new[] { "Bonsai.exe", workflow, "--start" }),
                ConfigFiles = new List<object> { Object("path", rigYml, "missing", true, "why", "Could not find file '" + rigYml + "'.") },
            };
            records["no-controller"] = ManifestWriter.Document(Utc("2026-11-12T10:45:04Z"), null, writer, rig,
                ManifestWriter.SessionSection(@"C:\Coen-Lab-Local\Acquired-Data\sub-test\ses-XXXXX_2026-11-12T10-15-02"), bare,
                ManifestWriter.ControlSection(null, ManifestWriter.LaunchProperty(bare.Launch, ManifestWriter.LaunchIdProperty)),
                ManifestWriter.RunSection(new RunEnd(RunEnd.Completed, null), true, started, Utc("2026-11-12T10:45:03Z"), 88100, null),
                ManifestWriter.ExpectedSection(plan, files), new[] { "Config file not found at Start: " + rigYml + ".", ManifestWriter.NoControllerNote }, files);

            foreach (var record in records)
            {
                var path = Path.Combine(folder, record.Key + ".json");
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(Json.Serialize(record.Value));
                if (write) File.WriteAllBytes(path, bytes);
                var onDisk = File.Exists(path) ? File.ReadAllBytes(path) : null;
                Check("fixtures\\manifest\\v" + ManifestWriter.Version + "\\" + record.Key + ".json is what the writer produces, byte for byte", onDisk != null && onDisk.SequenceEqual(bytes),
                    onDisk == null ? "missing (dotnet run --project src\\OpenRigs.Manifest.Tests -- --write-fixtures)" : "differs");
            }
            var completed = Json.Serialize(records["completed"]);
            var topKeys = string.Join(",", records["completed"].Select(p => p.Key));
            var fileKeys = string.Join(",", ((JsonObject)((List<object>)records["completed"].First(p => p.Key == "files").Value)[0]).Select(p => p.Key));
            Check("version 3 has no status, completed or ingest, and a file no sha256 or expected", Field(completed, "version") == "3"
                && topKeys == "version,created,ingest_script,writer,rig,session,source,launch,control,config_files,trial_columns,run,expected,notes,files"
                && fileKeys == "path,size,mtime,closed", topKeys + " | " + fileKeys);
            Check("control.launch_id is the LaunchId the controller passed", Field(completed, "control.launch_id") == "rig-PCO1-01503-004B1-20261112T101500Z", Field(completed, "control.launch_id"));
            Check("... and null when none was passed", ManifestWriter.ControlSection(null, "a", "b", null, null, null, null).First(p => p.Key == "launch_id").Value == null, "not null");
            Check("a blank ingest script is null, not left out", Field(Json.Serialize(records["no-controller"]), "ingest_script") == "null", "left out");
            ExperimentFolder(repo);
            RetiredNames(repo);
        }

        // L1-L2: rig-sync retired on 13 Nov 2026; the overnight copy is jacuzzi-ingest's ingest-rig. The lab's
        // code and READMEs name it only in dated history: a sentence holding a date or "once".
        static void RetiredNames(string repo)
        {
            Console.WriteLine("Retired names:");
            var lab = new[] { "Control", "Manifest", "HarpDevices", "LiveView", "Vision" }
                .SelectMany(p => new[] { "OpenRigs." + p, "OpenRigs." + p + ".Tests" }).Concat(new[] { "Shared" })
                .Select(d => Path.Combine(repo, "src", d)).Where(Directory.Exists).ToList();
            var self = Path.Combine(repo, "src", "OpenRigs.Manifest.Tests", "Program.cs");
            var found = new List<string>();
            foreach (var file in lab.SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(d, "README.md", SearchOption.TopDirectoryOnly))))
            {
                if (file.Split(Path.DirectorySeparatorChar).Any(part => part == "bin" || part == "obj") || file == self) continue;
                var sentences = Regex.Split(File.ReadAllText(file), @"(?<=[.!?])\s+");
                found.AddRange(sentences.Where(s => s.Contains("rig-sync") && !Regex.IsMatch(s, @"\b\d{1,2} (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) \d{4}\b|\bonce\b"))
                    .Select(s => file.Substring(repo.Length).TrimStart('\\')));
            }
            Check("no lab .cs or README names rig-sync outside dated history", lab.Count >= 10 && found.Count == 0, found.Count == 0 ? lab.Count + " folders" : string.Join(", ", found.Distinct()));
        }

        // The one name two languages make (claude-tools docs\architecture\records.md, Experiment folder name):
        // fixtures\experiment-folder\v1\names.json holds this package's reader and LogController's format, as it
        // holds app-rig-control's rename.
        static void ExperimentFolder(string repo)
        {
            Console.WriteLine("The experiment folder's name:");
            var path = Path.Combine(repo, "fixtures", "experiment-folder", "v1", "names.json");
            Check(@"fixtures\experiment-folder\v1\names.json is there", File.Exists(path), path);
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            // Enough for this file as it is written (two-space indent, one string per value), not a parser.
            Func<string, string> list = key => Regex.Match(text, "\"" + key + @""": \[(.*?)\n  \]", RegexOptions.Singleline).Groups[1].Value;
            Func<string, string, string> value = (entry, key) => Regex.Unescape(Regex.Match(entry, "\"" + key + @""": ""((?:[^""\\]|\\.)*)""").Groups[1].Value);
            Func<string, List<string>> entries = key => Regex.Matches(list(key), @"\{[^}]*\}").Cast<Match>().Select(m => m.Value).ToList();
            Check("it is version 1", text.Contains("\"version\": 1,"), "another version");

            var logController = File.ReadAllText(Path.Combine(repo, "src", "OpenRigs.Logging", "LogController.bonsai"));
            var format = Regex.Match(logController, @"<Format>(\{0\}[^<]*)</Format>").Groups[1].Value;
            Check(@"LogController names the folder <root>\sub-<SubjectId>\ses-<SessionId>_<DateTime>", format == @"{0}\sub-{1}\ses-{2}_{3}", format);
            // As OpenRigs.Core's FormatDate, which LogController gives the folder's time to.
            Func<DateTime, string> formatDate = when => { var o = when.ToString("o").Replace(':', '-'); return o.Substring(0, o.IndexOf('.')); };

            var names = entries("names");
            Check("it has names to make and read", names.Count >= 4, names.Count.ToString());
            foreach (var name in names)
            {
                var started = DateTime.Parse(value(name, "started"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                var made = string.Format(CultureInfo.InvariantCulture, format, "", value(name, "subject"), value(name, "session"), formatDate(started)).TrimStart('\\').Replace('\\', '/');
                Check(value(name, "folder") + " is what the format makes", made == value(name, "folder"), made);
                var read = Json.Serialize(ManifestWriter.SessionSection(@"C:\Data\" + value(name, "folder").Replace('/', '\\')));
                Check("... and reads back to its subject, session and start", Field(read, "subject") == value(name, "subject") && Field(read, "id") == value(name, "session") && Field(read, "started") == value(name, "started"), read);
            }
            var refused = entries("refused");
            Check("it has IDs to refuse", refused.Count >= 5, refused.Count.ToString());
            foreach (var entry in refused)
            {
                Check("refused: " + value(entry, "why"), !ManifestWriter.IdValid(value(entry, "subject")) || !ManifestWriter.IdValid(value(entry, "session")), value(entry, "subject") + " " + value(entry, "session"));
            }
            var notNames = Regex.Matches(list("notNames"), @"""((?:[^""\\]|\\.)*)""").Cast<Match>().Select(m => Regex.Unescape(m.Groups[1].Value)).ToList();
            Check("it has names that are not one", notNames.Count >= 5, notNames.Count.ToString());
            foreach (var name in notNames)
            {
                var folder = name.StartsWith("sub-", StringComparison.Ordinal) ? @"C:\Data\" + name : @"C:\Data\sub-PCO1-01503\" + name;
                var read = Json.Serialize(ManifestWriter.SessionSection(folder));
                Check(name + " is not an experiment folder's name", Field(read, "id") == "null" && Field(read, "subject") == "null" && Field(read, "started") == "null", read);
            }
        }

        static int Main(string[] args)
        {
            var root = Path.Combine(Path.GetTempPath(), "openrigs-manifest-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Fixtures(args.Contains("--write-fixtures"));

                Console.WriteLine("How the run ended, with no run input:");
                var finishedThenStopped = RunExperiment(root, "a", Controller(), c => { c.SetPhase(ExperimentPhase.Finished); Stop(c, "run finished"); });
                Check("a run that finished, then was stopped by app-rig-control, is completed", Field(finishedThenStopped, "run.outcome") == "completed", Field(finishedThenStopped, "run.outcome"));
                Check("... in a folder named ses-<Session>_<DateTime>, its session read from the name", Field(finishedThenStopped, "session.id") == "a" && Field(finishedThenStopped, "session.subject") == "TC002" && Field(finishedThenStopped, "session.started") == "2026-10-30T19:57:12Z", Field(finishedThenStopped, "session.id") + " " + Field(finishedThenStopped, "session.started"));
                Check("... with no stop reason", Field(finishedThenStopped, "run.stop_reason") == "null", Field(finishedThenStopped, "run.stop_reason"));
                var finished = RunExperiment(root, "b", Controller(), c => c.SetPhase(ExperimentPhase.Finished));
                Check("a run that finished is completed", Field(finished, "run.outcome") == "completed", Field(finished, "run.outcome"));
                var stopped = RunExperiment(root, "c", Controller(), c => Stop(c, "mouse unwell"));
                Check("a controller stop before Finished is stopped, with its reason", Field(stopped, "run.outcome") == "stopped" && Field(stopped, "run.stop_reason") == "mouse unwell", Field(stopped, "run.outcome") + " " + Field(stopped, "run.stop_reason"));
                var early = RunExperiment(root, "d", Controller(), null);
                Check("a workflow that ends before Finished, unstopped, is ended-early", Field(early, "run.outcome") == "ended-early", Field(early, "run.outcome"));
                var stoppingUnasked = RunExperiment(root, "h", Controller(), c => c.SetPhase(ExperimentPhase.Stopping));
                Check("a Stopping phase with no stop from the controller is ended-early, not stopped", Field(stoppingUnasked, "run.outcome") == "ended-early", Field(stoppingUnasked, "run.outcome"));
                var alone = RunExperiment(root, "e", null, null);
                Check("without ExperimentControl it is stopped", Field(alone, "run.outcome") == "stopped" && Field(alone, "control") == "null", Field(alone, "run.outcome"));
                Check("the parameters at Go are not in the manifest (they are Settings_Experiment's)", !finished.Contains("\"parameters\"") && Field(finished, "control.subject_id") != null, Field(finished, "control"));
                // 0.1.0-local.5: who launched, released and stopped the experiment, as the controller named them.
                var named = RunExperiment(root, "h", Controller(), c =>
                {
                    Set(c, "LaunchedBy", new Person { Id = "PipC", Name = "Pip Coen" });
                    Set(c, "GoBy", new Person { Id = "JoB", Name = "Jo Bloggs" });
                    Set(c, "StoppedBy", new Person { Id = "", Name = "Jo Bloggs" });
                    Stop(c, "stopped by Jo Bloggs");
                });
                Check("control names who launched the experiment and who sent Go", Field(named, "control.launched_by.id") == "PipC" && Field(named, "control.launched_by.name") == "Pip Coen" && Field(named, "control.go_by.id") == "JoB", named);
                Check("... and who stopped it", Field(named, "control.stopped_by.name") == "Jo Bloggs" && Field(named, "control.stopped_by.id") == "", Field(named, "control.stopped_by.name"));
                {
                    // S3-L3: app-rig-control kills a workflow slow to stop, so the running write is the last.
                    var control = Controller();
                    var folder = Path.Combine(root, "sub-TC002", "ses-slow_2026-10-30T19-57-12");
                    var path = new Subject<string>();
                    var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                    using (recorder.Process(path).Subscribe(_ => { }, _ => { }))
                    {
                        control.SetPhase(ExperimentPhase.Recording);
                        path.OnNext(folder);
                        Set(control, "StoppedBy", new Person { Id = "JoB", Name = "Jo Bloggs" });
                        Stop(control, "mouse unwell");
                        var manifest = Path.Combine(folder, ManifestWriter.FileName);
                        var running = "";
                        for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.ElapsedMilliseconds < 2000; System.Threading.Thread.Sleep(50))
                        {
                            try { running = File.ReadAllText(manifest); } catch (IOException) { continue; }
                            if (Field(running, "run.stop_reason") != "null") break;
                        }
                        Check("a controller's stop is written while the run is still going, stopped and not final, with who sent it", Field(running, "run.outcome") == "stopped" && Field(running, "run.final") == "false" && Field(running, "run.stop_reason") == "mouse unwell"
                            && Field(running, "control.stopped_by.id") == "JoB", Field(running, "run.outcome") + " " + Field(running, "run.final") + " " + Field(running, "run.stop_reason") + " " + Field(running, "control.stopped_by.id"));
                    }
                    Set(null, "Current", null);
                }
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
                // 0.1.1-coen.6: a mouse's own file by {SubjectId}, and a folder as every file in it.
                Directory.CreateDirectory(Path.Combine(root, "Spouts"));
                File.WriteAllText(Path.Combine(root, "Spouts", "M7.csv"), "1,2");
                var mine = ManifestWriter.ConfigFiles(@"Spouts\{SubjectId}.csv", root, "M7");
                Check("{SubjectId} names the subject's own file", mine.Count == 1 && ((string)mine[0][0].Value).EndsWith(@"Spouts\M7.csv") && mine[0][1].Key == "sha256", string.Join(" | ", mine.Select(f => string.Join(",", f.Select(p => p.Key + "=" + p.Value)))));
                var nobody = ManifestWriter.ConfigFiles(@"Spouts\{SubjectId}.csv", root, null);
                Check("... and with no SubjectId on the command line it is missing, saying why", nobody.Count == 1 && nobody[0].Any(p => p.Key == "missing") && ((string)nobody[0].First(p => p.Key == "why").Value).Contains("SubjectId"), "found");
                Directory.CreateDirectory(Path.Combine(root, "Stimuli", "set"));
                File.WriteAllText(Path.Combine(root, "Stimuli", "b.wav"), "b");
                File.WriteAllText(Path.Combine(root, "Stimuli", "set", "a.wav"), "a");
                var stimuli = ManifestWriter.ConfigFiles("Stimuli", root, null);
                Check("a folder is every file in it, each with its sha256", stimuli.Count == 2 && stimuli.All(f => f[1].Key == "sha256"), string.Join(" | ", stimuli.Select(f => f[0].Value)));
                var map = new ExpectedEntry { Path = "Board/Board.yml" };
                var board = new ExpectedEntry { Path = "Board/" };
                var both = new[] { board, map };
                Check("a folder is not met by a file another entry names", ExpectedPlan.Matches(board, new[] { "Board/Board.yml" }, both) == 0
                    && ExpectedPlan.Matches(map, new[] { "Board/Board.yml" }, both) == 1, "the register map stood in for the board's data");
                Check("... and is by any other file in it", ExpectedPlan.Matches(board, new[] { "Board/Board.yml", "Board/Board_44_x.bin" }, both) == 1, "not met");
                // 0.1.1-coen.5: each settings file expected on its own, by a prefix entry (in Schemas/ until coen.7, Settings/ since).
                var schemas = ExpectedPlan.Create("Settings/Settings_Rig_;Settings/Settings_Task_;Settings/", null, n => null).Entries;
                var inSchemas = new[] { "Settings/Settings_Rig_2026-11-07T09-30-12.json" };
                Check("a prefix entry is met by a file whose path starts with it, and only by one", ExpectedPlan.Matches(schemas[0], inSchemas, schemas) == 1
                    && ExpectedPlan.Matches(schemas[1], inSchemas, schemas) == 0, "the rig's file met the task's entry, or not its own");
                Check("... and does not keep that file from meeting its folder", ExpectedPlan.Matches(schemas[2], inSchemas, schemas) == 1, "the folder was not met");
                // 0.1.0-local.10: the writer and its version, and what a dirty checkout changed.
                // The version the build stamped: open-rigs' build sets it (app-rig-setup's workflows\build-package.ps1
                // passes the .csproj's <Version>; a plain build is 42.42.42-dev0).
                var built = ((AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(ManifestWriter).Assembly, typeof(AssemblyInformationalVersionAttribute))).InformationalVersion.Split('+')[0];
                Check("writer names the package and its version as built", Field(finished, "writer.package") == "OpenRigs.Manifest" && Field(finished, "writer.version") == built, Field(finished, "writer.version") + " vs " + built);
                DirtySource(root);
                TrialColumnsFile(root, finished);

                // Round 5: the manifest reads OpenRigs.Control by name, so that it does not depend on it;
                // the checks above run through that read, and a renamed member fails here too.
                Check("every ControlState member the manifest reads is there", ControlExperiment.StateMembers.All(m => typeof(ControlState).GetProperty(m) != null),
                    string.Join(", ", ControlExperiment.StateMembers.Where(m => typeof(ControlState).GetProperty(m) == null)));
                Check("... and every Go event member", ControlExperiment.GoMembers.All(m => typeof(ControlEvent).GetProperty(m) != null),
                    string.Join(", ", ControlExperiment.GoMembers.Where(m => typeof(ControlEvent).GetProperty(m) == null)));
                Check("... and every Person member", ControlExperiment.PersonMembers.All(m => typeof(Person).GetProperty(m) != null),
                    string.Join(", ", ControlExperiment.PersonMembers.Where(m => typeof(Person).GetProperty(m) == null)));
                Check("the package does not reference OpenRigs.Control", !typeof(ManifestWriter).Assembly.GetReferencedAssemblies().Any(a => a.Name == "OpenRigs.Control"), "it does");

                Console.WriteLine("The run input:");
                {
                    var folder = Path.Combine(root, "sub-TC002", "ses-f_2026-10-30T19-57-12");
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
                    Check("... and its session is read from the folder's name", Field(text, "session.id") == "f" && Field(text, "session.started") == "2026-10-30T19:57:12Z", Field(text, "session.id") + " " + Field(text, "session.started"));
                    Check("... and the node completes", completed, "no OnCompleted");
                    // A run that fails before the folder exists: nothing to record, and the error goes
                    // on at once, so the workflow does not stand at Ready with a dead run (4 Nov 2026).
                    var failedFolder = Path.Combine(root, "sub-TC002", "ses-f2_2026-10-30T19-57-12");
                    var failedPath = new Subject<string>();
                    var failedRun = new Subject<int>();
                    Exception failedWith = null;
                    var failedRecorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                    var failedSubscription = failedRecorder.Process(failedPath, failedRun).Subscribe(_ => { }, e => failedWith = e, () => { });
                    failedRun.OnError(new InvalidOperationException("camera lost before Go"));
                    Check("a run that fails before the folder exists ends the node with its error at once", failedWith != null && failedWith.Message == "camera lost before Go", failedWith?.ToString() ?? "no OnError");
                    Check("... and writes nothing", !Directory.Exists(failedFolder), failedFolder);
                    failedSubscription.Dispose();
                    subscription.Dispose();
                }
                {
                    // L1-L1 (8 Nov 2026): the run's error after Finished or the controller's stop is how the workflow stopped.
                    foreach (var finishedFirst in new[] { true, false })
                    {
                        var control = Controller();
                        var folder = Path.Combine(root, "sub-TC002", "ses-err" + finishedFirst + "_2026-10-30T19-57-12");
                        var path = new Subject<string>();
                        var run = new Subject<int>();
                        var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                        var subscription = recorder.Process(path, run).Subscribe(_ => { }, _ => { });
                        control.SetPhase(ExperimentPhase.Recording);
                        path.OnNext(folder);
                        if (finishedFirst) control.SetPhase(ExperimentPhase.Finished); else Stop(control, "mouse unwell");
                        run.OnError(new InvalidOperationException("the session was stopped"));
                        subscription.Dispose();
                        Set(null, "Current", null);
                        var text = File.ReadAllText(Path.Combine(folder, ManifestWriter.FileName));
                        var want = finishedFirst ? "completed" : "stopped";
                        Check("a run's error after " + (finishedFirst ? "Finished is completed" : "the controller's stop is stopped") + ", not error", Field(text, "run.outcome") == want && Field(text, "run.error") == "null", Field(text, "run.outcome") + " " + Field(text, "run.error"));
                    }
                }
                {
                    // L1-L2 (8 Nov 2026): a workflow killed after its Finished lost its outcome. It is written
                    // as Finished comes, not final, and app-rig-control keeps an editor's Bonsai open until the
                    // end-of-run write sets run.final true.
                    var control = Controller();
                    var folder = Path.Combine(root, "sub-TC002", "ses-fin_2026-10-30T19-57-12");
                    var path = new Subject<string>();
                    var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                    var subscription = recorder.Process(path).Subscribe(_ => { }, _ => { });
                    control.SetPhase(ExperimentPhase.Recording);
                    path.OnNext(folder);
                    var manifest = Path.Combine(folder, ManifestWriter.FileName);
                    var first = File.ReadAllText(manifest);
                    Check("the first write is running and not final", Field(first, "run.outcome") == "running" && Field(first, "run.final") == "false", Field(first, "run.outcome") + " " + Field(first, "run.final"));
                    control.SetPhase(ExperimentPhase.Finished);
                    var provisional = "";
                    for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.ElapsedMilliseconds < 2000; System.Threading.Thread.Sleep(50))
                    {
                        try { provisional = File.ReadAllText(manifest); } catch (IOException) { continue; }
                        if (Field(provisional, "run.outcome") != "running") break;
                    }
                    Check("a workflow's Finished is written as it comes, completed and not final", Field(provisional, "run.outcome") == "completed" && Field(provisional, "run.final") == "false", Field(provisional, "run.outcome") + " " + Field(provisional, "run.final"));
                    subscription.Dispose();
                    Set(null, "Current", null);
                    var last = File.ReadAllText(manifest);
                    Check("... and the end-of-run write is completed and final", Field(last, "run.outcome") == "completed" && Field(last, "run.final") == "true", Field(last, "run.outcome") + " " + Field(last, "run.final"));
                }
                {
                    // S3-L1 (Run 1): another writer's .tmp is not a file of the experiment.
                    var folder = Path.Combine(root, "tmpfiles");
                    Directory.CreateDirectory(Path.Combine(folder, "Board"));
                    File.WriteAllText(Path.Combine(folder, "Board", "Board.yml.tmp"), "x");
                    File.WriteAllText(Path.Combine(folder, "Board", "Board.yml"), "x");
                    foreach (var record in ManifestWriter.RecordFiles) File.WriteAllText(Path.Combine(folder, record), "{}");
                    var listed = string.Join(",", ManifestWriter.FilesSection(folder).Cast<JsonObject>().Select(f => (string)f.First(p => p.Key == "path").Value));
                    Check("FilesSection leaves out another writer's .tmp and the three record files", listed == "Board/Board.yml", listed);
                }
                Check("ConfigFiles defaults under the shared ConfigRoot", new ExperimentManifestSettings().ConfigFiles.StartsWith(RigDevices.DefaultConfigRoot + @"\"), new ExperimentManifestSettings().ConfigFiles);

                Console.WriteLine("A write that fails:");
                {
                    // A file where the experiment folder should be, so Begin cannot make it.
                    var blocked = Path.Combine(root, "sub-TC002", "ses-g_2026-10-30T19-57-12");
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
                {
                    // L1-M1 (audit, 11 Nov 2026): an end-of-run write that failed, the file held by a
                    // reader without delete sharing, was never tried again: the manifest stayed running.
                    // The node's error disposes it at once (Observable.Create), so the reader lets go
                    // during the disposal's own attempt.
                    var folder = Path.Combine(root, "sub-TC002", "ses-held_2026-10-30T19-57-12");
                    var path = new Subject<string>();
                    var run = new Subject<int>();
                    var recorder = new ManifestRecorder(null, "", ExpectedPlan.Create("", null, n => null));
                    Exception error = null;
                    var errored = new System.Threading.ManualResetEventSlim();
                    var subscription = recorder.Process(path, run).Subscribe(_ => { }, e => { error = e; errored.Set(); }, () => { });
                    path.OnNext(folder);
                    run.OnNext(1);
                    var manifest = Path.Combine(folder, ManifestWriter.FileName);
                    var held = new System.Threading.ManualResetEventSlim();
                    var reader = new System.Threading.Thread(() =>
                    {
                        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            held.Set();
                            errored.Wait(5000);
                            System.Threading.Thread.Sleep(100);
                        }
                    });
                    reader.Start();
                    held.Wait();
                    run.OnCompleted();
                    reader.Join();
                    Check("an end-of-run write the reader blocks ends the node with its error", error != null, "no error");
                    subscription.Dispose();
                    var text = File.ReadAllText(manifest);
                    Check("... and is written at disposal, completed, final, with its items", Field(text, "run.outcome") == "completed" && Field(text, "run.final") == "true" && Field(text, "run.items") == "1",
                        Field(text, "run.outcome") + " " + Field(text, "run.final") + " " + Field(text, "run.items"));
                }

                Console.WriteLine("Launch and parameters:");
                var launch = Json.Serialize(ManifestWriter.LaunchSection(new[] { "Bonsai.exe", "w.bonsai", "--property:SubjectId=A", "-p:SubjectId=B", "--property:subjectid=C", "--no-boot", "w.bonsai", "--@pipe", "x" }));
                Check("a repeated --property is one key, the last value winning", Regex.Matches(launch, "\"subjectid\"", RegexOptions.IgnoreCase).Count == 1 && launch.Contains("\"C\""), launch);
                Check("Bonsai's own relaunch arguments are left out", !launch.Contains("--no-boot") && !launch.Contains("--@pipe"), launch);

                Console.WriteLine("Writing:");
                var file = Path.Combine(root, "atomic.json");
                ManifestWriter.WriteAtomically(file, "one");
                ManifestWriter.WriteAtomically(file, "two");
                Check("a second write replaces the first and leaves no .tmp", File.ReadAllText(file) == "two" && Directory.GetFiles(root, "atomic.json.*.tmp").Length == 0, File.ReadAllText(file));
                {
                    // A reader without delete sharing, as Python's open() is (app-rig-control polls manifest.json every second).
                    var opened = new System.Threading.ManualResetEventSlim();
                    var reader = new System.Threading.Thread(() =>
                    {
                        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            opened.Set();
                            System.Threading.Thread.Sleep(200);
                        }
                    });
                    reader.Start();
                    opened.Wait();
                    Exception held = null;
                    try { ManifestWriter.WriteAtomically(file, "three"); } catch (Exception e) { held = e; }
                    reader.Join();
                    Check("a write while a reader holds the file waits for it and succeeds", held == null && File.ReadAllText(file) == "three" && Directory.GetFiles(root, "atomic.json.*.tmp").Length == 0, held != null ? held.Message : File.ReadAllText(file));
                }

                Console.WriteLine("Expected files:");
                var rigYml = Path.Combine(root, "rig.yml");
                File.WriteAllText(rigYml, "behaviorBoard:\n  main:\n    portName: COM3\ncamera:\n  box:\n    serialNumber: '1'\n  face:\n    serialNumber: '2'\nrootPath: C:\\data\n");
                var plan = ExpectedPlan.Create("auto;events.csv;Camera-*/", rigYml, n => n == "BehaviorBoard-Main.Data" || n == "Camera-Box" ? typeof(object) : null);
                var paths = string.Join(", ", plan.Entries.Select(e => e.Path));
                Check("auto expects each published device's folder, and a listed file", paths == "BehaviorBoard-Main/, Camera-Box/, events.csv", paths);
                Check("a device in rig.yml the workflow does not publish is noted, not expected", plan.Notes.Any(n => n.StartsWith("Camera-Face")), string.Join(" | ", plan.Notes));
                Check("a wildcard entry is noted, not expected", plan.Notes.Any(n => n.Contains("Camera-*/")) && !paths.Contains("*"), string.Join(" | ", plan.Notes));
                var outside = ExpectedPlan.Create(@".\RigSchema\x.json;/abs.json;C:\data\x.json;../up.json;a/../b.json", null, n => null);
                var outsidePaths = string.Join(", ", outside.Entries.Select(e => e.Path));
                Check("a leading ./ is dropped, as the overnight copy names the file", outsidePaths == "RigSchema/x.json", outsidePaths);
                Check("... and a rooted entry or one with .. is noted, not expected", outside.Notes.Count == 4 && outside.Notes.All(n => n.EndsWith("not a path inside the experiment folder.")), string.Join(" | ", outside.Notes));
                // A Harp device's data is HarpMessage, told by name: the manifest does not reference Bonsai.Harp.
                // 0.1.0-local.9: which entries are Harp devices is src\Shared\HarpRig.cs, the rule RecordHarpDevices
                // uses (a portName: null is none), and the register map is expected only when a loaded package decodes the kind.
                var harpYml = Path.Combine(root, "harp-rig.yml");
                File.WriteAllText(harpYml, "behaviorBoard:\n  main:\n    portName: COM3\n  idle:\n    portName: null\n" +
                    "soundCard:\n  main:\n    portName: COM4\nclockSynchronizer:\n  sync:\n    portName: COM5\ncamera:\n  box:\n    serialNumber: '1'\n");
                var harpMessage = typeof(Bonsai.Harp.HarpMessage);
                Func<string, Type> harpPublished = n => n.EndsWith(".Data") && !n.StartsWith("Camera") ? harpMessage : n == "Camera-Box" || n == "Camera-Box.Data" ? typeof(object) : null;
                var unloaded = ExpectedPlan.Create("harp", harpYml, harpPublished);
                var unloadedPaths = string.Join(", ", unloaded.Entries.Select(e => e.Path));
                Check("harp expects each Harp device's record, and no register map no loaded package decodes", unloadedPaths == "BehaviorBoard-Main/BehaviorBoard-Main_device.json, SoundCard-Main/SoundCard-Main_device.json, ClockSynchronizer-Sync/ClockSynchronizer-Sync_device.json", unloadedPaths);
                Check("... noting why each map is not expected", unloaded.Notes.Any(n => n.StartsWith("BehaviorBoard-Main/BehaviorBoard-Main.yml") && n.Contains("decodes WhoAmI 1216")) && unloaded.Notes.Any(n => n.StartsWith("SoundCard-Main/SoundCard-Main.yml") && n.Contains("no WhoAmI is known for soundCard")), string.Join(" | ", unloaded.Notes));
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
                // L2-L1: one empty instance hid its whole kind, without a word.
                var emptyYml = Path.Combine(root, "empty-rig.yml");
                File.WriteAllText(emptyYml, "behaviorBoard:\n  main:\n    portName: COM3\n  spare:\ncamera:\n  box:\n    serialNumber: '1'\n" +
                    "speakerArray:\n  device: { bootDelay: 3 }\n  syncChannel: 7\n");
                var empty = ExpectedPlan.Create("auto", emptyYml, n => typeof(object));
                Check("an empty entry beside a board is noted, naming it, and settings with a value (speakerArray) are not",
                    empty.Notes.Count(n => n.Contains("has no settings")) == 1
                    && empty.Notes.Contains("The rig.yml entry behaviorBoard: spare: has no settings, so every behaviorBoard is passed over and none of its files is expected.")
                    && string.Join(", ", empty.Entries.Select(e => e.Path)) == "Camera-Box/", string.Join(" | ", empty.Notes));
                // L1-L1: auto;harp, as every lab workflow sets, wrote the note twice.
                var autoHarp = ExpectedPlan.Create("auto;harp", emptyYml, n => typeof(object));
                Check("auto;harp writes each note once", autoHarp.Notes.Count(n => n.Contains("has no settings")) == 1 && autoHarp.Notes.Distinct().Count() == autoHarp.Notes.Count, string.Join(" | ", autoHarp.Notes));

                Console.WriteLine("ConfigRoot:");
                var other = ManifestWriter.LaunchSection(new[] { "Bonsai.exe", "--property:ConfigRoot=D:\\Test\\config" });
                Check("a launch ConfigRoot that is not where rig.yml was read is noted", ManifestWriter.ConfigRootNote(other, @"C:\Coen-Lab-Local\RigConfigs\RIG\rig.yml") != null, "no note");
                var same = ManifestWriter.LaunchSection(new[] { "Bonsai.exe", "--property:ConfigRoot=C:\\Coen-Lab-Local\\RigConfigs" });
                Check("... and one that is, is not", ManifestWriter.ConfigRootNote(same, @"C:\Coen-Lab-Local\RigConfigs\RIG\rig.yml") == null, "noted");

                Console.WriteLine("The editor's copy:");
                {
                    var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<WorkflowBuilder Version=\"2.9.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:mf=\"clr-namespace:OpenRigs.Manifest;assembly=OpenRigs.Manifest\" xmlns=\"https://bonsai-rx.org/2018/workflow\">\n" +
                        "  <Workflow><Nodes><Expression xsi:type=\"mf:ExperimentManifest\"><mf:IngestScript>x</mf:IngestScript><mf:ConfigFiles>a.yml</mf:ConfigFiles><mf:ExpectedFiles>b/</mf:ExpectedFiles></Expression></Nodes><Edges /></Workflow>\n</WorkflowBuilder>\n";
                    WorkflowBuilder builder;
                    using (var reader = XmlReader.Create(new StringReader(xml))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    var copy = new WorkflowBuilder(builder.Workflow.ToInspectableGraph().FromInspectableGraph());
                    var saved = new StringWriter();
                    // Indented, as the editor saves: Bonsai's reader of a group's inner workflow expects it.
                    using (var writer = XmlWriter.Create(saved, new XmlWriterSettings { Indent = true })) WorkflowBuilder.Serializer.Serialize(writer, copy);
                    using (var reader = XmlReader.Create(new StringReader(saved.ToString()))) builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
                    var node = builder.Workflow.Select(n => ExpressionBuilder.Unwrap(n.Value)).OfType<ExperimentManifest>().Single();
                    Check("its settings survive the editor's copy, a save and a reload", node.IngestScript == "x" && node.ConfigFiles == "a.yml" && node.ExpectedFiles == "b/", node.IngestScript + " " + node.ConfigFiles + " " + node.ExpectedFiles);
                    Check("... in one settings node", node.Workflow.Count(n => ExpressionBuilder.Unwrap(n.Value) is ExperimentManifestSettings) == 1, "more than one");
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
