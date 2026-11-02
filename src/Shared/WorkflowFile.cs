using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OpenRigs.Shared
{
    /// <summary>
    /// The workflow file Bonsai was started with. OpenRigs.LiveView and OpenRigs.Manifest both
    /// compile this file in, so the live view and the session's manifest name the same workflow.
    /// </summary>
    internal static class WorkflowFile
    {
        /// <summary>
        /// The arguments Bonsai was started with. Bonsai's bootstrapper starts the editor again
        /// with those, then --no-boot, the workflow again and --@pipe with a pipe's name; what
        /// follows --no-boot is Bonsai's own.
        /// </summary>
        public static List<string> UserArguments(string[] args)
        {
            var user = new List<string>();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--no-boot") break;
                if (args[i] == "--@pipe") { i++; continue; }
                user.Add(args[i]);
            }
            return user;
        }

        /// <summary>The full path of the .bonsai file on the command line, or null when there is none (the editor's File, Open).</summary>
        public static string Find(string[] args, string workingDirectory)
        {
            var argument = UserArguments(args).FirstOrDefault(a => a.EndsWith(".bonsai", StringComparison.OrdinalIgnoreCase));
            return argument == null ? null : FullPath(argument, workingDirectory);
        }

        /// <summary>
        /// A workflow argument as a full path. A relative path was relative to where Bonsai was
        /// started, but by Start Bonsai works from the workflow's own folder, so when the path no
        /// longer resolves the file is looked for there by name. Joined to that folder instead,
        /// src\main.bonsai would read as src\src\main.bonsai.
        /// </summary>
        public static string FullPath(string argument, string workingDirectory)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(workingDirectory, argument));
                if (File.Exists(full) || Path.IsPathRooted(argument)) return full;
                var here = Path.Combine(workingDirectory, Path.GetFileName(argument));
                return File.Exists(here) ? here : full;
            }
            catch (Exception) { return argument; }
        }

        /// <summary>
        /// The folder the workflow runs from: the workflow file's own, or with none on the command
        /// line the working directory, which is where Bonsai finds relative includes.
        /// </summary>
        public static string Folder(string workflowFull, string workingDirectory)
        {
            return workflowFull != null ? Path.GetDirectoryName(workflowFull) : workingDirectory;
        }

        /// <summary>The workflow's path within its repository, or the full path when it is outside it.</summary>
        public static string Relative(string workflowFull, string root)
        {
            if (workflowFull != null && root != null && workflowFull.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return workflowFull.Substring(root.Length + 1);
            return workflowFull;
        }
    }

    /// <summary>Reads .git without running git, except for what changed since the commit, which is git's to answer.</summary>
    internal static class Git
    {
        /// <summary>The repository holding a folder: the nearest folder up from it with a .git, or null.</summary>
        public static string FindRoot(string start)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch (Exception) { }
            return null;
        }

        // The .git directory, following a worktree's "gitdir:" file.
        static string GitDir(string root)
        {
            var dotGit = Path.Combine(root, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (File.Exists(dotGit))
            {
                var text = File.ReadAllText(dotGit).Trim();
                if (text.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = text.Substring(7).Trim();
                    return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(root, target));
                }
            }
            return null;
        }

        // A worktree's gitdir is .git/worktrees/<name>; refs and config live in the common dir above it.
        static string CommonDir(string gitDir)
        {
            var commonFile = Path.Combine(gitDir, "commondir");
            return File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())) : gitDir;
        }

        /// <summary>The branch checked out (null when detached) and the commit, each null if it cannot be read.</summary>
        public static void Head(string root, out string branch, out string commit)
        {
            branch = null;
            commit = null;
            try
            {
                var gitDir = GitDir(root);
                if (gitDir == null) return;
                var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                if (head.StartsWith("ref:", StringComparison.Ordinal))
                {
                    var refName = head.Substring(4).Trim();
                    branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName.Substring(11) : refName;
                    var common = CommonDir(gitDir);
                    var refFile = Path.Combine(common, refName.Replace('/', Path.DirectorySeparatorChar));
                    commit = File.Exists(refFile) ? File.ReadAllText(refFile).Trim() : PackedRef(Path.Combine(common, "packed-refs"), refName);
                }
                else
                {
                    commit = head;
                }
            }
            catch (Exception) { }
        }

        static string PackedRef(string packedRefs, string refName)
        {
            if (!File.Exists(packedRefs)) return null;
            foreach (var line in File.ReadAllLines(packedRefs))
            {
                if (line.Length > 41 && line[40] == ' ' && line.Substring(41) == refName) return line.Substring(0, 40);
            }
            return null;
        }

        /// <summary>The origin remote's URL, or null.</summary>
        public static string OriginUrl(string root)
        {
            try
            {
                var gitDir = GitDir(root);
                if (gitDir == null) return null;
                var inOrigin = false;
                foreach (var raw in File.ReadAllLines(Path.Combine(CommonDir(gitDir), "config")))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal)) inOrigin = line == "[remote \"origin\"]";
                    else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal))
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0) return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>True or false from git status --porcelain; null when git is not on PATH or did not answer within 10 s.</summary>
        public static bool? IsDirty(string root)
        {
            var changed = Changed(root);
            return changed != null ? changed.Count > 0 : (bool?)null;
        }

        /// <summary>The tracked files git status --porcelain names, as paths from the root with '/' (a rename by its new path), untracked files left out as the dirty flag leaves them; null when git did not answer.</summary>
        public static List<string> Changed(string root)
        {
            var output = Run(root, "status --porcelain -z --untracked-files=no");
            if (output == null) return null;
            // -z: each entry is "XY path", NUL-ended and never quoted; a rename or copy is followed by its old path.
            var entries = System.Text.Encoding.UTF8.GetString(output).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            var changed = new List<string>();
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].Length < 4) continue;
                changed.Add(entries[i].Substring(3));
                if (entries[i][0] == 'R' || entries[i][0] == 'C') i++;
            }
            return changed;
        }

        /// <summary>The lowercase hex sha256 of git diff HEAD (staged and unstaged, binary files in full), so two dirty runs can be told apart or matched; null when git did not answer.</summary>
        public static string DiffSha256(string root)
        {
            var diff = Run(root, "-c core.quotePath=false diff HEAD --binary --no-color --no-ext-diff");
            if (diff == null) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(diff).Select(b => b.ToString("x2")));
            }
        }

        // git's output as bytes; null when git is not on PATH, failed, or did not answer within 10 s.
        static byte[] Run(string root, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                using (var output = new MemoryStream())
                {
                    var copy = p.StandardOutput.BaseStream.CopyToAsync(output);
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (Exception) { } return null; }
                    copy.Wait();
                    if (p.ExitCode != 0) return null;
                    return output.ToArray();
                }
            }
            catch (Exception) { return null; }
        }
    }
}
