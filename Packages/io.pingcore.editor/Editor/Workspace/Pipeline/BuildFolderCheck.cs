using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PingCore.Editor.BuildGuard;
using PingCore.Editor.Cli;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>Why a folder may not be pushed, with the failure code the planner reports it under.</summary>
    public sealed class BuildFolderVerdict
    {
        public BuildFolderVerdict(IReadOnlyList<string> problems, string reason)
        {
            Problems = problems ?? Array.Empty<string>();
            Reason = reason;
        }

        /// <summary>Every reason, in order; empty when the folder may be pushed.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary><see cref="DeployFailure.StartupExecutableMissing"/> when a startup command's file is missing, else <see cref="DeployFailure.NoBuild"/>.</summary>
        public string Reason { get; }

        public bool Ok => Problems.Count == 0;

        public static BuildFolderVerdict Pass { get; } = new BuildFolderVerdict(Array.Empty<string>(), null);
    }

    /// <summary>
    /// A folder is pushed only when it holds the file or files the game's startup command launches (every member
    /// deployment's, or one of the game's template sets' when the fleet has no deployment) and the build guard
    /// passed it: the guard's last verdict reads <c>pass</c> at the postprocess stage, names an executable inside
    /// this folder, and is not older than any file in it (<c>version.txt</c> excepted: the build writes it after
    /// the verdict). A file's time is the latest of its write and creation times, so a file copied in after the
    /// guard ran counts as new. That holds for the Ship section's own build and for a folder the developer built
    /// with File &gt; Build Profiles in this project: every player build runs the guard.
    /// </summary>
    public static class BuildFolderCheck
    {
        /// <summary>
        /// The verdict for <paramref name="folder"/> (absolute, or relative to <paramref name="projectRoot"/>).
        /// <paramref name="startup"/> null skips the file check only (the startup command check was skipped).
        /// </summary>
        public static BuildFolderVerdict Check(string projectRoot, string folder, StartupFiles startup)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return new BuildFolderVerdict(new[] { "No folder is chosen to push; build first, or choose a folder." }, DeployFailure.NoBuild);
            }

            string root = Path.GetFullPath(Path.Combine(projectRoot, folder));
            if (!Directory.Exists(root))
            {
                return new BuildFolderVerdict(new[] { $"The folder {folder} does not exist; build first, or choose another folder." }, DeployFailure.NoBuild);
            }

            List<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'))
                .ToList();
            // Null only when the startup command check was skipped on purpose (DeployRequest.StartupCheckSkipped, which
            // DeployRequest.Problem requires): the guard's verdict below is checked all the same.
            string missing = startup == null || startup.Paths.Count == 0 ? null : StartupCommand.MissingProblem(startup, files);
            if (missing != null)
            {
                return new BuildFolderVerdict(new[] { missing }, DeployFailure.StartupExecutableMissing);
            }

            var problems = new List<string>();
            BuildGuardVerdict verdict = BuildGuardVerdictFile.Read(projectRoot);
            if (verdict == null)
            {
                problems.Add("The build guard wrote no verdict for this project; build the folder again in this project.");
                return new BuildFolderVerdict(problems, DeployFailure.NoBuild);
            }

            if (verdict.verdict != BuildGuardVerdict.Pass || verdict.stage != BuildGuardPostprocessor.Stage)
            {
                problems.Add($"The build guard's last verdict is {verdict.verdict} at {verdict.stage}, not pass at {BuildGuardPostprocessor.Stage}.");
            }

            string written = string.IsNullOrEmpty(verdict.outputPath) ? string.Empty : BuildGuardPolicy.NormalizeFullPath(Path.Combine(projectRoot, verdict.outputPath));
            string inside = BuildGuardPolicy.NormalizeFullPath(root).TrimEnd('/', '\\') + "/";
            if (!written.Replace('\\', '/').StartsWith(inside.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("The build guard's last verdict belongs to another build, not this folder; build it again in this project (Build, or File > Build Profiles).");
            }

            DateTime verdictAt = File.GetLastWriteTimeUtc(BuildGuardVerdictFile.GetPath(projectRoot));
            string versionFile = Path.Combine(root, BuildServerArgs.VersionFileName);
            bool newer = files
                .Select(f => Path.Combine(root, f))
                .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(versionFile), StringComparison.OrdinalIgnoreCase))
                .Any(f => FileTime(f) > verdictAt);
            if (newer)
            {
                problems.Add("A file in the folder changed after the build guard ran; build again.");
            }

            return problems.Count == 0 ? BuildFolderVerdict.Pass : new BuildFolderVerdict(problems, DeployFailure.NoBuild);
        }

        /// <summary>When a file last arrived or changed: the later of its write and creation times.</summary>
        public static DateTime FileTime(string path)
        {
            DateTime write = File.GetLastWriteTimeUtc(path);
            DateTime created = File.GetCreationTimeUtc(path);
            return write > created ? write : created;
        }
    }
}
