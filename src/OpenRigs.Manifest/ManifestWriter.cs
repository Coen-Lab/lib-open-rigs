using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bonsai;
using OpenRigs.Shared;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// What is fixed at Start: the launch, the workflow's commit and the configuration files as
    /// loaded, with notes on what was not found. Read once, written into both manifests.
    /// </summary>
    class StartSnapshot
    {
        public JsonObject Source;
        public JsonObject Launch;
        public List<object> ConfigFiles = new List<object>();
        public JsonObject TrialColumns;
        public List<object> Notes = new List<object>();
    }

    /// <summary>
    /// The manifest's contents and its writes, with no Rx, so a test can drive it on a temp
    /// folder. The format is FORMAT.md beside this file, version 3.
    /// </summary>
    class ManifestWriter
    {
        public const string FileName = "manifest.json";
        public const int Version = 3;

        /// <summary>The experiment folder's three records, one writer each: this file, the overnight copy's and the server's. None is a file of the experiment.</summary>
        public static readonly string[] RecordFiles = { FileName, "sync.json", "ingest.json" };

        /// <summary>The launch property a controller passes its id for the launch in, recorded as control.launch_id.</summary>
        public const string LaunchIdProperty = "LaunchId";

        public const string NoControllerNote = "No ExperimentControl running when the experiment folder appeared: no control section, and a workflow that stops before its run ends is recorded as stopped, whatever stopped it.";

        readonly string ingestScript;
        readonly StartSnapshot start;
        readonly ExpectedPlan expected;
        string folder;
        string manifestPath;
        DateTime started;

        public ManifestWriter(string ingestScript, StartSnapshot start, ExpectedPlan expected)
        {
            this.ingestScript = ingestScript;
            this.start = start;
            this.expected = expected ?? new ExpectedPlan();
        }

        public string ManifestPath { get { return manifestPath; } }

        /// <summary>Reads what the manifest takes from Start: the workflow, its repository and commit, the command line, each configuration file and the trial columns file.</summary>
        public static StartSnapshot Snapshot(string configFiles, bool hasRunInput, string trialColumns = null)
        {
            var snapshot = new StartSnapshot();
            var args = Environment.GetCommandLineArgs();
            var workflowFull = WorkflowFile.Find(args, Environment.CurrentDirectory);
            var workflowFolder = WorkflowFile.Folder(workflowFull, Environment.CurrentDirectory);
            if (workflowFull == null) snapshot.Notes.Add("No workflow on Bonsai's command line (opened from the editor): source is the repository of the working directory, " + workflowFolder + ".");
            var root = Git.FindRoot(workflowFolder);
            snapshot.Source = SourceSection(workflowFull, root);
            snapshot.Launch = LaunchSection(args);
            var subject = LaunchProperty(snapshot.Launch, "SubjectId");
            foreach (var configured in ConfigEntries(configFiles))
            {
                foreach (var file in ConfigFiles(configured, root ?? workflowFolder, subject))
                {
                    snapshot.ConfigFiles.Add(file);
                    var missing = file.FirstOrDefault(p => p.Key == "missing");
                    if (missing.Key != null) snapshot.Notes.Add("Config file not found at Start: " + configured + (configured.Contains(SubjectToken) ? " (" + SubjectToken + " " + (subject ?? "not on the command line") + ")" : "") + ".");
                }
            }
            string trialNote;
            snapshot.TrialColumns = Manifest.TrialColumns.Read(trialColumns, root ?? workflowFolder, out trialNote);
            if (trialNote != null) snapshot.Notes.Add(trialNote);
            if (!hasRunInput) snapshot.Notes.Add("No run input: the outcome is read from OpenRigs.Control's phase when the workflow stops.");
            var note = ConfigRootNote(snapshot.Launch, RigConfigPath(configFiles));
            if (note != null) snapshot.Notes.Add(note);
            return snapshot;
        }

        // OpenRigs' RigConfig reads <ConfigRoot>\<MACHINE>\rig.yml, and ConfigRoot is a workflow
        // property a launch may set; ConfigFiles cannot follow it, so say when the two disagree.
        public static string ConfigRootNote(JsonObject launch, string rigConfig)
        {
            var properties = (JsonObject)launch.First(p => p.Key == "properties").Value;
            var configRoot = properties.FirstOrDefault(p => string.Equals(p.Key, "ConfigRoot", StringComparison.OrdinalIgnoreCase)).Value as string;
            if (configRoot == null || rigConfig == null) return null;
            var root = ResolvePath(configRoot, Environment.CurrentDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (rigConfig.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return "The launch set ConfigRoot to " + configRoot + ", but ConfigFiles read " + rigConfig + ": the rig.yml recorded may not be the one the workflow ran with. Set ConfigFiles to match.";
        }

        static IEnumerable<string> ConfigEntries(string configFiles)
        {
            return (configFiles ?? "").Split(';').Select(e => e.Trim()).Where(e => e.Length > 0);
        }

        /// <summary>The first of ConfigFiles named rig.yml, its path resolved as at Start; null if none is.</summary>
        public static string RigConfigPath(string configFiles)
        {
            var configured = ConfigEntries(configFiles)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e.Replace('/', '\\')), "rig.yml", StringComparison.OrdinalIgnoreCase));
            if (configured == null) return null;
            var folder = WorkflowFile.Folder(WorkflowFile.Find(Environment.GetCommandLineArgs(), Environment.CurrentDirectory), Environment.CurrentDirectory);
            return ResolvePath(configured, Git.FindRoot(folder) ?? folder);
        }

        /// <summary>The part of a ConfigFiles entry that is the subject, from the launch's --property:SubjectId
        /// (the mouse's spout positions, calibration\LickSpoutPositions\{SubjectId}.csv, 0.1.1-coen.6).</summary>
        public const string SubjectToken = "{SubjectId}";

        public static string LaunchProperty(JsonObject launch, string name)
        {
            var properties = launch.FirstOrDefault(p => p.Key == "properties").Value as JsonObject;
            var found = properties != null ? properties.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) : default(KeyValuePair<string, object>);
            return found.Key != null ? found.Value as string : null;
        }

        /// <summary>One ConfigFiles entry's files: the file, or every file in a folder (calibration\AuditoryStimuli),
        /// in order, each with its sha256 (0.1.1-coen.6). {SubjectId} is the launch's; with none, the entry is missing.</summary>
        public static List<JsonObject> ConfigFiles(string configured, string baseFolder, string subject)
        {
            if (configured.Contains(SubjectToken))
            {
                if (string.IsNullOrEmpty(subject))
                {
                    var none = new JsonObject();
                    none.Add("path", configured);
                    none.Add("missing", true);
                    none.Add("why", "no --property:SubjectId on the command line to name the subject's file");
                    return new List<JsonObject> { none };
                }
                configured = configured.Replace(SubjectToken, subject);
            }
            var full = ResolvePath(configured, baseFolder);
            if (Directory.Exists(full))
            {
                return Directory.GetFiles(full, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => ConfigFile(f, baseFolder)).ToList();
            }
            return new List<JsonObject> { ConfigFile(configured, baseFolder) };
        }

        /// <summary>A configured path with environment variables expanded, a relative one from the base folder.</summary>
        public static string ResolvePath(string configured, string baseFolder)
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            try { return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseFolder, expanded)); }
            catch (Exception) { return expanded; }
        }

        /// <summary>The first write, outcome running. Makes the folder if the loggers have not yet: PathPrefix names it before any of them writes and they make the same folder. Throws if the file cannot be written: nothing recorded is the thing to stop for.</summary>
        public string Begin(string experimentFolder, ControlExperiment control)
        {
            if (string.IsNullOrEmpty(experimentFolder)) throw new ArgumentException("ExperimentManifest was given no experiment folder.");
            var full = Path.GetFullPath(experimentFolder.TrimEnd('\\', '/'));
            Directory.CreateDirectory(full);
            var path = Path.Combine(full, FileName);
            folder = full;
            started = DateTime.UtcNow;
            if (control == null) start.Notes.Add(NoControllerNote);
            try
            {
                manifestPath = path;
                Write(new RunEnd(RunEnd.Running, null), null, control, null, false);
            }
            catch
            {
                folder = null;
                manifestPath = null;
                throw;
            }
            return manifestPath;
        }

        /// <summary>The outcome so far, with run.final false, once the workflow's Finished or the controller's stop has come: the outcome, the stop's reason and who sent it are on disk even if the workflow is killed before its end. A Begin that never happened, or failed, writes nothing.</summary>
        public void Provisional(RunEnd end, long? items, ControlExperiment control)
        {
            if (manifestPath == null) return;
            Write(end, items, control, DateTime.UtcNow, false);
        }

        /// <summary>The end-of-run write, with the outcome and run.final true. A Begin that never happened, or failed, writes nothing.</summary>
        public void Finish(RunEnd end, long? items, ControlExperiment control)
        {
            if (manifestPath == null) return;
            Write(end, items, control, DateTime.UtcNow, true);
        }

        void Write(RunEnd end, long? items, ControlExperiment control, DateTime? ended, bool final)
        {
            // A running write carries the stop too (a workflow killed before the stop's own write, which then is the last).
            var stopReason = (end.Outcome == RunEnd.Stopped || end.Outcome == RunEnd.Running) && control != null ? control.StopReason : null;
            var files = FilesSection(folder);
            var doc = Document(DateTime.UtcNow, ingestScript, WriterSection(), RigSection(), SessionSection(folder), start,
                ControlSection(control, LaunchProperty(start.Launch, LaunchIdProperty)),
                RunSection(end, final, started, ended, items, stopReason),
                ExpectedSection(expected, files), expected.Notes.Concat(start.Notes.Cast<string>()), files);
            WriteAtomically(manifestPath, Json.Serialize(doc));
        }

        /// <summary>The manifest from what one write knows, reading no clock, machine or disk, so a test builds the fixtures in fixtures\manifest\v3\ from their inputs.</summary>
        public static JsonObject Document(DateTime created, string ingestScript, JsonObject writer, JsonObject rig, JsonObject session, StartSnapshot start,
            JsonObject control, JsonObject run, List<object> expected, IEnumerable<string> notes, List<object> files)
        {
            var doc = new JsonObject();
            doc.Add("version", (long)Version);
            doc.Add("created", Iso(created));
            doc.Add("ingest_script", string.IsNullOrEmpty(ingestScript) ? null : ingestScript);
            doc.Add("writer", writer);
            doc.Add("rig", rig);
            doc.Add("session", session);
            doc.Add("source", start.Source);
            doc.Add("launch", start.Launch);
            doc.Add("control", control);
            doc.Add("config_files", start.ConfigFiles);
            doc.Add("trial_columns", start.TrialColumns);
            doc.Add("run", run);
            doc.Add("expected", expected);
            doc.Add("notes", notes.Cast<object>().ToList());
            doc.Add("files", files);
            return doc;
        }

        /// <summary>How the run went at this write. Only the end-of-run write is final.</summary>
        public static JsonObject RunSection(RunEnd end, bool final, DateTime started, DateTime? ended, long? items, string stopReason)
        {
            var run = new JsonObject();
            run.Add("outcome", end.Outcome);
            run.Add("final", final);
            run.Add("started", Iso(started));
            run.Add("ended", ended.HasValue ? Iso(ended.Value) : null);
            run.Add("seconds", ended.HasValue ? (object)Math.Round((ended.Value - started).TotalSeconds, 1) : null);
            run.Add("items", items);
            run.Add("error", end.Error);
            run.Add("stop_reason", stopReason);
            return run;
        }

        /// <summary>Each file or folder the experiment must leave, why, and how many files in the folder match it now.</summary>
        public static List<object> ExpectedSection(ExpectedPlan plan, List<object> files)
        {
            var paths = files.Cast<JsonObject>().Select(f => (string)f.First(p => p.Key == "path").Value).ToList();
            var section = new List<object>();
            foreach (var entry in plan.Entries)
            {
                var item = new JsonObject();
                item.Add("path", entry.Path);
                item.Add("why", entry.Why);
                item.Add("found", (long)ExpectedPlan.Matches(entry, paths, plan.Entries));
                section.Add(item);
            }
            return section;
        }

        /// <summary>manifest.json.&lt;pid&gt;.tmp, then the rename, so no reader sees half a file, nor a moment with no manifest. UTF-8 without a BOM. A write that fails takes its .tmp with it.</summary>
        public static void WriteAtomically(string path, string text)
        {
            var tmp = path + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                // Windows refuses the rename while a reader holds the file without delete sharing
                // (Python's open(); rig-control polls manifest.json every second): wait it out, about 0.5 s.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(tmp, path, null);
                        else File.Move(tmp, path);
                        break;
                    }
                    catch (IOException) when (attempt < 10)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>The machine. Its rig.yml is named in config_files only (0.1.0-local.6); version 1 also copied it here.</summary>
        public static JsonObject RigSection()
        {
            var rig = new JsonObject();
            rig.Add("machine", Environment.MachineName);
            rig.Add("user", Environment.UserName);
            // Not Environment.OSVersion: Bonsai.exe declares no Windows version, so that says 6.2.
            rig.Add("os", RuntimeInformation.OSDescription.Trim());
            return rig;
        }

        /// <summary>
        /// One configuration file as it was at Start: path and sha256, or missing. Not its text (0.1.0-local.7):
        /// the workflow logs the settings it parsed from it into the experiment (the Open Rigs logging spec's
        /// settings JSON), and the manifest names which file that was.
        /// </summary>
        public static JsonObject ConfigFile(string configured, string baseFolder)
        {
            var file = new JsonObject();
            var full = ResolvePath(configured, baseFolder);
            file.Add("path", full);
            try
            {
                file.Add("sha256", Sha256Hex(File.ReadAllBytes(full)));
            }
            catch (Exception ex)
            {
                file.Add("missing", true);
                file.Add("why", ex.Message);
            }
            return file;
        }

        static readonly Regex SessionName = new Regex(@"^ses-(?<id>.+?)_(?:date-)?(?<dt>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2})$", RegexOptions.CultureInvariant);

        public static JsonObject SessionSection(string experimentFolder)
        {
            var session = new JsonObject();
            var name = Path.GetFileName(experimentFolder);
            var parent = Path.GetFileName(Path.GetDirectoryName(experimentFolder) ?? string.Empty);
            session.Add("subject", parent.StartsWith("sub-", StringComparison.Ordinal) ? parent.Substring(4) : null);
            var m = SessionName.Match(name);
            session.Add("id", m.Success ? m.Groups["id"].Value : null);
            string started = null;
            if (m.Success)
            {
                DateTime dt;
                if (DateTime.TryParseExact(m.Groups["dt"].Value, "yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                {
                    started = Iso(dt);
                }
            }
            session.Add("started", started);
            session.Add("folder", experimentFolder);
            return session;
        }

        /// <summary>The package that wrote the manifest and its version, so a reader knows which of the format's rules it followed: the package's version, its build suffix (+commit) left out.</summary>
        public static JsonObject WriterSection()
        {
            var assembly = typeof(ManifestWriter).Assembly;
            var informational = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
            var version = informational != null ? informational.InformationalVersion : assembly.GetName().Version.ToString();
            var plus = version.IndexOf('+');
            var writer = new JsonObject();
            writer.Add("package", assembly.GetName().Name);
            writer.Add("version", plus >= 0 ? version.Substring(0, plus) : version);
            return writer;
        }

        /// <summary>The workflow file and the repository holding it: origin, branch, commit, dirty, and when dirty which files and a hash of the diff. Found from the workflow's folder, as OpenRigs.LiveView finds it, so both name the same commit.</summary>
        public static JsonObject SourceSection(string workflowFull, string root)
        {
            var source = new JsonObject();
            source.Add("workflow", WorkflowFile.Relative(workflowFull, root));
            source.Add("repo", root != null ? Git.OriginUrl(root) : null);
            source.Add("root", root);
            string branch = null, commit = null;
            if (root != null) Git.Head(root, out branch, out commit);
            source.Add("branch", branch);
            source.Add("commit", commit);
            var changed = root != null ? Git.Changed(root) : null;
            source.Add("dirty", changed != null ? changed.Count > 0 : (bool?)null);
            source.Add("changed", changed != null ? changed.Cast<object>().ToList() : null);
            source.Add("diff_sha256", changed != null && changed.Count > 0 ? Git.DiffSha256(root) : null);
            var bonsai = typeof(CombinatorAttribute).Assembly.GetName().Version;
            source.Add("bonsai", string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", bonsai.Major, bonsai.Minor, bonsai.Build));
            return source;
        }

        static readonly Regex PropertyArgument = new Regex(@"^(?:--property|-p)[:=](?<name>[^=]+)=(?<value>.*)$", RegexOptions.CultureInvariant);

        /// <summary>Bonsai's command line: every argument, and the --property:Name=Value (or -p:) values by name.</summary>
        public static JsonObject LaunchSection(string[] args)
        {
            var launch = new JsonObject();
            var arguments = new List<object>();
            var properties = new JsonObject();
            foreach (var arg in WorkflowFile.UserArguments(args))
            {
                arguments.Add(arg);
                var m = PropertyArgument.Match(arg);
                if (m.Success) properties.Set(m.Groups["name"].Value, m.Groups["value"].Value);
            }
            launch.Add("arguments", arguments);
            launch.Add("properties", properties);
            return launch;
        }

        /// <summary>The experiment controller's record: the id the controller passed for this launch (the
        /// LaunchId property, null when none was), subject, session, when Go was taken, and who
        /// launched it, sent Go and stopped it. Null without ExperimentControl. The parameters at Go are
        /// in the experiment's Settings_Experiment file (OpenRigs.Control's ExperimentSettings, since
        /// 0.1.1-coen.5; Schemas_Experiment until coen.7), so the settings live once, in the experiment folder.</summary>
        public static JsonObject ControlSection(ControlExperiment control, string launchId)
        {
            if (control == null) return null;
            return ControlSection(launchId, control.GoSubjectId, control.GoSessionId, control.GoTime, control.LaunchedBy, control.GoBy, control.StoppedBy);
        }

        public static JsonObject ControlSection(string launchId, string subjectId, string sessionId, DateTime? go,
            KeyValuePair<string, string>? launchedBy, KeyValuePair<string, string>? goBy, KeyValuePair<string, string>? stoppedBy)
        {
            var section = new JsonObject();
            section.Add("launch_id", string.IsNullOrEmpty(launchId) ? null : launchId);
            section.Add("subject_id", subjectId);
            section.Add("session_id", sessionId);
            section.Add("go", go.HasValue ? Iso(go.Value) : null);
            section.Add("launched_by", PersonSection(launchedBy));
            section.Add("go_by", PersonSection(goBy));
            section.Add("stopped_by", PersonSection(stoppedBy));
            return section;
        }

        static JsonObject PersonSection(KeyValuePair<string, string>? person)
        {
            if (!person.HasValue) return null;
            var section = new JsonObject();
            section.Add("id", person.Value.Key);
            section.Add("name", person.Value.Value);
            return section;
        }

        public static List<object> FilesSection(string experimentFolder)
        {
            var files = new List<object>();
            var prefix = experimentFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var paths = Directory.GetFiles(experimentFolder, "*", SearchOption.AllDirectories);
            Array.Sort(paths, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var rel = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
                rel = rel.Replace('\\', '/');
                // Another writer's half-written file (RecordHarpDevice's .yml.tmp) is renamed within seconds: never a file of the experiment.
                if (RecordFiles.Contains(rel) || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                long size;
                DateTime mtime;
                try
                {
                    size = info.Length;
                    mtime = info.LastWriteTimeUtc;
                }
                catch (FileNotFoundException) { continue; }
                files.Add(FileEntry(rel, size, mtime, IsClosed(path)));
            }
            return files;
        }

        public static JsonObject FileEntry(string path, long size, DateTime mtime, bool closed)
        {
            var entry = new JsonObject();
            entry.Add("path", path);
            entry.Add("size", size);
            entry.Add("mtime", Iso(mtime));
            entry.Add("closed", closed);
            return entry;
        }

        /// <summary>Whether no other process holds the file: an exclusive open succeeds. A file we cannot open for another reason (an ACL) reads as closed, since that is not a write in progress.</summary>
        public static bool IsClosed(string path)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static string Iso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
