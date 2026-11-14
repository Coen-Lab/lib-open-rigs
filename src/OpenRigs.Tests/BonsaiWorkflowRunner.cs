using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace OpenRigs.Tests
{
    /// <summary>
    /// Launches Bonsai workflows headlessly against the repository Bonsai environment,
    /// in the same way the "Bonsai" profile in launchSettings.json does.
    /// </summary>
    public static class BonsaiWorkflowRunner
    {
        static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Runs a workflow from the test output directory and blocks until it terminates.
        /// </summary>
        /// <param name="workflowFileName">File name of the workflow, relative to the test output directory.</param>
        /// <param name="properties">Externalized workflow properties to assign, passed through as -p arguments.</param>
        /// <param name="timeout">How long to wait before killing the workflow. Defaults to two minutes.</param>
        /// <param name="workflowDirectory">
        /// Directory holding the workflow and the extensions to put on the Bonsai library path.
        /// Defaults to the output directory of the calling test assembly.
        /// </param>
        public static BonsaiWorkflowResult Run(
            string workflowFileName,
            IEnumerable<KeyValuePair<string, string>> properties = null,
            TimeSpan? timeout = null,
            string workflowDirectory = null)
        {
            var startInfo = CreateStartInfo(workflowFileName, properties, workflowDirectory);
            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (sender, e) => AppendLine(standardOutput, e.Data);
                process.ErrorDataReceived += (sender, e) => AppendLine(standardError, e.Data);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var effectiveTimeout = timeout ?? DefaultTimeout;
                if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
                {
                    TryKill(process);
                    throw new TimeoutException(
                        $"The workflow '{workflowFileName}' did not terminate within {effectiveTimeout}.");
                }

                // The parameterless overload waits for the asynchronous output handlers to drain.
                process.WaitForExit();
                return new BonsaiWorkflowResult(
                    process.ExitCode,
                    standardOutput.ToString(),
                    standardError.ToString(),
                    $"{startInfo.FileName} {startInfo.Arguments}");
            }
        }

        /// <summary>
        /// Starts a workflow as <see cref="Run"/> does and returns at once, for a test that kills
        /// it; its output is discarded. The caller disposes the process.
        /// </summary>
        public static Process Start(
            string workflowFileName,
            IEnumerable<KeyValuePair<string, string>> properties = null,
            string workflowDirectory = null)
        {
            var process = new Process { StartInfo = CreateStartInfo(workflowFileName, properties, workflowDirectory) };
            process.OutputDataReceived += (sender, e) => { };
            process.ErrorDataReceived += (sender, e) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        static ProcessStartInfo CreateStartInfo(
            string workflowFileName,
            IEnumerable<KeyValuePair<string, string>> properties,
            string workflowDirectory)
        {
            var executablePath = GetBonsaiExecutablePath();
            var outputDirectory = workflowDirectory ?? GetOutputDirectory();
            var workflowPath = Path.Combine(outputDirectory, workflowFileName);
            if (!File.Exists(workflowPath))
            {
                throw new FileNotFoundException(
                    $"The workflow '{workflowFileName}' was not found in '{outputDirectory}'. " +
                    "Ensure it is included as None with CopyToOutputDirectory in the project file.",
                    workflowPath);
            }

            var arguments = new StringBuilder();
            AppendArgument(arguments, workflowPath);
            AppendArgument(arguments, "--no-editor");

            // Mirrors the --lib argument in launchSettings.json, so that the OpenRigs extensions
            // built by this solution are discoverable by the environment.
            AppendArgument(arguments, "--lib");
            AppendArgument(arguments, outputDirectory);

            if (properties != null)
            {
                foreach (var property in properties)
                {
                    AppendArgument(arguments, "-p");
                    AppendArgument(arguments, $"{property.Key}={property.Value}");
                }
            }

            return new ProcessStartInfo(executablePath, arguments.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The bootstrapper resolves its environment relative to its own location, but
                // running from there keeps any relative paths in workflow output predictable.
                WorkingDirectory = Path.GetDirectoryName(executablePath)
            };
        }

        static string GetBonsaiExecutablePath()
        {
            var executablePath = GetAssemblyMetadata("BonsaiExecutablePath");
            if (string.IsNullOrEmpty(executablePath))
            {
                throw new InvalidOperationException(
                    "BonsaiExecutablePath was not baked into the test assembly. " +
                    "It is defined in build/Common.csproj.props.");
            }

            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    $"The Bonsai environment has not been bootstrapped at '{executablePath}'. " +
                    "Run .bonsai/Setup.cmd locally, or the bonsai-rx/setup-bonsai action in CI.",
                    executablePath);
            }

            return executablePath;
        }

        static string GetAssemblyMetadata(string key)
        {
            var assembly = typeof(BonsaiWorkflowRunner).Assembly;
            foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key == key)
                {
                    return attribute.Value;
                }
            }

            return null;
        }

        static string GetOutputDirectory()
        {
            return Path.GetDirectoryName(new Uri(typeof(BonsaiWorkflowRunner).Assembly.CodeBase).LocalPath);
        }

        static void AppendLine(StringBuilder builder, string data)
        {
            if (data == null)
            {
                return;
            }

            lock (builder)
            {
                builder.AppendLine(data);
            }
        }

        static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout expiring and the kill request.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The process is already terminating.
            }
        }

        static void AppendArgument(StringBuilder builder, string value)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(value));
        }

        /// <summary>
        /// Quotes a single argument following the rules used by CommandLineToArgvW. Notably this
        /// doubles any trailing backslashes, so that directory paths do not escape the closing quote.
        /// </summary>
        static string QuoteArgument(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var backslashes = 0;
                while (i < value.Length && value[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == value.Length)
                {
                    builder.Append('\\', backslashes * 2);
                    break;
                }

                if (value[i] == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    builder.Append('\\', backslashes);
                }

                builder.Append(value[i]);
            }

            builder.Append('"');
            return builder.ToString();
        }
    }

    /// <summary>
    /// The outcome of a headless Bonsai workflow run.
    /// </summary>
    public sealed class BonsaiWorkflowResult
    {
        public BonsaiWorkflowResult(int exitCode, string standardOutput, string standardError, string commandLine)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
            CommandLine = commandLine;
        }

        public int ExitCode { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }

        public string CommandLine { get; }

        /// <summary>
        /// Formats the command line and captured output for inclusion in assertion messages.
        /// The bootstrapper reports a zero exit code even when the workflow fails to build, so
        /// this output is the only reliable diagnostic when an assertion fails.
        /// </summary>
        public string Describe()
        {
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine($"Command line: {CommandLine}");
            builder.AppendLine($"Exit code: {ExitCode}");
            AppendSection(builder, "Standard output", StandardOutput);
            AppendSection(builder, "Standard error", StandardError);
            return builder.ToString();
        }

        static void AppendSection(StringBuilder builder, string heading, string content)
        {
            builder.AppendLine($"{heading}:");
            builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "  (empty)" : content.TrimEnd());
        }
    }
}
