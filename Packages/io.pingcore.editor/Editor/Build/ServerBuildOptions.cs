using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PingCore.Editor.Cli;

namespace PingCore.Editor.Build
{
    /// <summary>
    /// What one Linux Dedicated Server build is asked for: a build profile (the Editor's Ship section and
    /// the <c>-buildProfile</c> command line for CI and scripts) or the scene form of scripts that build from a scene
    /// list, the version folder, and the executable's path inside it. Validated by the command line's rules
    /// (<see cref="BuildServerArgs"/>), so the CLI and the Editor cannot drift apart.
    /// </summary>
    public sealed class ServerBuildOptions
    {
        // A path inside the build: segments of letters, digits, '.', '_' or '-', no '.' or '..' segment, at most four deep.
        private static readonly Regex SegmentPattern = new Regex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        private ServerBuildOptions(BuildServerArgs args, string executable, bool inProcess, IReadOnlyList<string> errors)
        {
            Args = args;
            Executable = executable;
            InProcess = inProcess;
            Errors = errors;
        }

        /// <summary>The validated arguments (version, scenes or build profile, product, extra defines, output root).</summary>
        public BuildServerArgs Args { get; }

        /// <summary>The executable's path inside the output folder, forward slashes (<c>BeaconRush.x86_64</c>, or <c>bin/Server.x86_64</c>).</summary>
        public string Executable { get; }

        /// <summary>
        /// True for a build started from the Editor's own UI: it refuses Play mode and a compile in
        /// progress, and its switch back leaves the marker <c>ActiveTargetRestoreCheck</c> reads after the
        /// domain reload. The headless CLI passes false; both put the active build profile and target back.
        /// </summary>
        public bool InProcess { get; }

        /// <summary>Every problem with the options; empty when they are usable.</summary>
        public IReadOnlyList<string> Errors { get; }

        public bool IsValid => Errors.Count == 0;

        /// <summary>The project-relative output folder (<c>Builds/Server/&lt;v&gt;</c>).</summary>
        public string OutputFolder => Args.OutputFolder;

        /// <summary>The project-relative executable path, the build's <c>locationPathName</c>.</summary>
        public string ExecutablePath => OutputFolder + "/" + Executable;

        /// <summary>The project-relative path of <c>version.txt</c>, at the top of the output folder.</summary>
        public string VersionFilePath => OutputFolder + "/" + BuildServerArgs.VersionFileName;

        /// <summary>
        /// The options of a headless <c>Cli.BuildServer</c> run. <paramref name="projectProduct"/> names the
        /// executable of a build profile run given no <c>-pingcoreProduct</c> (the project's product name, made
        /// safe by <see cref="ExecutableFromProduct"/>).
        /// </summary>
        public static ServerBuildOptions FromArgs(BuildServerArgs args, string projectProduct = null)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            string executable = args.Product != null ? args.Product + BuildServerArgs.ExecutableExtension : ExecutableFromProduct(projectProduct);
            return Make(args, executable, false);
        }

        /// <summary>
        /// The options of an in-Editor build through <paramref name="buildProfile"/>, always plain (extra defines and
        /// another output root are command-line only), writing <paramref name="executable"/> (a path inside the build, such as the one
        /// the game's startup command launches) under <c>Builds/Server/&lt;version&gt;/</c>.
        /// </summary>
        public static ServerBuildOptions ForProfile(string version, string buildProfile, string executable)
        {
            var argv = new List<string> { BuildServerArgs.BuildProfileFlag, buildProfile ?? string.Empty, BuildServerArgs.VersionFlag, version ?? string.Empty };
            return Make(BuildServerArgs.Parse(argv), executable, true);
        }

        /// <summary>
        /// Why <paramref name="executable"/> is not a path inside a build (forward slashes, segments of letters,
        /// digits, <c>.</c>, <c>_</c> or <c>-</c>, no <c>.</c> or <c>..</c> segment, at most four deep), or null. Pure.
        /// </summary>
        public static string ExecutableProblem(string executable)
        {
            if (string.IsNullOrEmpty(executable))
            {
                return "the executable's name is empty";
            }

            string[] segments = executable.Split('/');
            if (segments.Length > 4 || executable.Contains("\\"))
            {
                return "the executable " + executable + " must be a path inside the build with forward slashes, at most four deep";
            }

            foreach (string segment in segments)
            {
                if (segment == "." || segment == ".." || !SegmentPattern.IsMatch(segment))
                {
                    return "the executable " + executable + " must be letters, digits, '.', '_' or '-' in each part, with no . or .. part";
                }
            }

            return null;
        }

        /// <summary>
        /// The executable a build gets from the project's product name: its letters, digits, <c>.</c>, <c>_</c> and
        /// <c>-</c> (anything else dropped) plus <c>.x86_64</c>; <c>Server.x86_64</c> when nothing usable is left. Pure.
        /// </summary>
        public static string ExecutableFromProduct(string product)
        {
            string kept = Regex.Replace(product ?? string.Empty, "[^A-Za-z0-9._-]", string.Empty).TrimStart('.', '-');
            if (kept.Length > 64)
            {
                kept = kept.Substring(0, 64);
            }

            return (kept.Length == 0 ? "Server" : kept) + BuildServerArgs.ExecutableExtension;
        }

        private static ServerBuildOptions Make(BuildServerArgs args, string executable, bool inProcess)
        {
            var errors = new List<string>(args.Errors);
            string problem = ExecutableProblem(executable);
            if (problem != null)
            {
                errors.Add(problem);
            }

            return new ServerBuildOptions(args, executable, inProcess, errors);
        }
    }

    /// <summary>How one server build ended.</summary>
    public sealed class ServerBuildResult
    {
        internal ServerBuildResult(int exitCode, string problem, ServerBuildOptions options, string verdictText, ActiveTargetRestore restore)
        {
            ExitCode = exitCode;
            Problem = problem;
            Options = options;
            VerdictText = verdictText;
            Restore = restore;
        }

        /// <summary><see cref="ServerBuilder.ExitPass"/>, <see cref="ServerBuilder.ExitBuildFailed"/> or <see cref="ServerBuilder.ExitUsage"/>: the CLI's exit code.</summary>
        public int ExitCode { get; }

        public bool Passed => ExitCode == ServerBuilder.ExitPass;

        /// <summary>Why it did not pass, one sentence; null when it passed.</summary>
        public string Problem { get; }

        public ServerBuildOptions Options { get; }

        /// <summary>The project-relative output folder (<c>Builds/Server/&lt;v&gt;</c>).</summary>
        public string OutputFolder => Options.OutputFolder;

        /// <summary>The project-relative executable.</summary>
        public string ExecutablePath => Options.ExecutablePath;

        /// <summary>The build guard's verdict in one line, or null when the build never ran.</summary>
        public string VerdictText { get; }

        /// <summary>What the build did to the Editor's active build target, and whether it is being switched back.</summary>
        public ActiveTargetRestore Restore { get; }
    }
}
