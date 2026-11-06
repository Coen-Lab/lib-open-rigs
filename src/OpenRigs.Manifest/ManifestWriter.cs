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
    /// The manifest's contents and its two writes, with no Rx, so a test can drive it on a temp
    /// folder. The format is FORMAT.md beside this file, version 2.
    /// </summary>
    class ManifestWriter
    {
        public const string FileName = "manifest.json";
        public const int Version = 2;

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
            foreach (var configured in ConfigEntries(configFiles))
            {
                var file = ConfigFile(configured, root ?? workflowFolder);
                snapshot.ConfigFiles.Add(file);
                var missing = file.FirstOrDefault(p => p.Key == "missing");
                if (missing.Key != null) snapshot.Notes.Add("Config file not found at Start: " + configured + ".");
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

        /// <summary>A configured path with environment variables expanded, a relative one from the base folder.</summary>
        public static string ResolvePath(string configured, string baseFolder)
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            try { return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseFolder, expanded)); }
            catch (Exception) { return expanded; }
        }

        /// <summary>The first write, status partial and outcome running. Makes the folder if the loggers have not yet: PathPrefix names it before any of them writes and they make the same folder. Throws if the file cannot be written: nothing recorded is the thing to stop for.</summary>
        public string Begin(string experimentFolder, ControlExperiment control)
        {
            if (string.IsNullOrEmpty(experimentFolder)) throw new ArgumentException("ExperimentManifest was given no experiment folder.");
            var full = Path.GetFullPath(experimentFolder.TrimEnd('\\', '/'));
            Directory.CreateDirectory(full);
            var path = Path.Combine(full, FileName);
            folder = full;
            started = DateTime.UtcNow;
            if (control == null) start.Notes.Add("No ExperimentControl running when the experiment folder appeared: no control section, and a workflow that stops before its run ends is recorded as stopped, whatever stopped it.");
            try
            {
                manifestPath = path;
                Write(new RunEnd(RunEnd.Running, null), null, control, null);
            }
            catch
            {
                folder = null;
                manifestPath = null;
                throw;
            }
            return manifestPath;
        }

        /// <summary>A running write again, once the controller's stop has come: the stop's reason and who sent it are on disk even if the workflow is killed before its end. A Begin that never happened, or failed, writes nothing.</summary>
        public void Stopping(ControlExperiment control)
        {
            if (manifestPath == null) return;
            Write(new RunEnd(RunEnd.Running, null), null, control, null);
        }

        /// <summary>The second write, with the outcome. A Begin that never happened, or failed, writes nothing.</summary>
        public void Finish(RunEnd end, long? items, ControlExperiment control)
        {
            if (manifestPath == null) return;
            Write(end, items, control, DateTime.UtcNow);
        }

        void Write(RunEnd end, long? items, ControlExperiment control, DateTime? ended)
        {
            var doc = new JsonObject();
            doc.Add("version", (long)Version);
            doc.Add("created", Iso(DateTime.UtcNow));
            doc.Add("status", "partial");
            if (!string.IsNullOrEmpty(ingestScript)) doc.Add("ingest_script", ingestScript);
            doc.Add("writer", WriterSection());
            doc.Add("rig", RigSection());
            doc.Add("session", SessionSection(folder));
            doc.Add("source", start.Source);
            doc.Add("launch", start.Launch);
            doc.Add("control", ControlSection(control));
            doc.Add("config_files", start.ConfigFiles);
            doc.Add("trial_columns", start.TrialColumns);
            var run = new JsonObject();
            run.Add("outcome", end.Outcome);
            run.Add("started", Iso(started));
            run.Add("ended", ended.HasValue ? Iso(ended.Value) : null);
            run.Add("seconds", ended.HasValue ? (object)Math.Round((ended.Value - started).TotalSeconds, 1) : null);
            run.Add("items", items);
            run.Add("error", end.Error);
            // A running write carries the stop too: rig-control kills a workflow slow to stop, and that write is then the last.
            run.Add("stop_reason", (end.Outcome == RunEnd.Stopped || end.Outcome == RunEnd.Running) && control != null ? control.StopReason : null);
            doc.Add("run", run);
            var files = FilesSection(folder);
            doc.Add("expected", ExpectedSection(expected, files));
            doc.Add("notes", expected.Notes.Concat(start.Notes.Cast<string>()).Cast<object>().ToList());
            doc.Add("files", files);
            WriteAtomically(manifestPath, Json.Serialize(doc));
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

        /// <summary>manifest.json.tmp, then the rename, so no reader sees half a file, nor a moment with no manifest (rig-sync would copy the experiment as one without). A write that fails takes its .tmp with it.</summary>
        public static void WriteAtomically(string path, string text)
        {
            AtomicFile.Write(path, text);
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

        /// <summary>The experiment controller's settings at Go: subject, session, who launched it, sent
        /// Go and stopped it, and every parameter. Null without ExperimentControl.</summary>
        public static JsonObject ControlSection(ControlExperiment control)
        {
            if (control == null) return null;
            var section = new JsonObject();
            section.Add("subject_id", control.GoSubjectId);
            section.Add("session_id", control.GoSessionId);
            var go = control.GoTime;
            section.Add("go", go.HasValue ? Iso(go.Value) : null);
            section.Add("launched_by", PersonSection(control.LaunchedBy));
            section.Add("go_by", PersonSection(control.GoBy));
            section.Add("stopped_by", PersonSection(control.StoppedBy));
            var parameters = new JsonObject();
            foreach (var p in control.GoParameters) parameters.Set(p.Key, p.Value);
            section.Add("parameters", parameters);
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
                if (rel == FileName || rel == FileName + ".tmp") continue;
                var info = new FileInfo(path);
                var entry = new JsonObject();
                entry.Add("path", rel);
                entry.Add("size", info.Length);
                entry.Add("mtime", Iso(info.LastWriteTimeUtc));
                entry.Add("closed", IsClosed(path));
                files.Add(entry);
            }
            return files;
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
