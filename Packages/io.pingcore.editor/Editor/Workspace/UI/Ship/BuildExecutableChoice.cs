using System;
using System.Collections.Generic;
using System.Linq;
using PingCore.Editor.Build;
using PingCore.Editor.Workspace.Pipeline;

namespace PingCore.Editor.Workspace.UI.Ship
{
    /// <summary>
    /// The file Ship's Build row writes, decided from the SAME startup command read Push checks against
    /// (<see cref="StartupCommandReader"/>: the fleet's deployments' template sets, else the game's), never from a branch's
    /// default deployment spec. Pure. A process name <c>./BeaconRushServer.x86_64</c> makes the build write
    /// <c>BeaconRushServer.x86_64</c> (product <c>BeaconRushServer</c>), so the game servers start it.
    /// <list type="bullet">
    /// <item>One file: that file.</item>
    /// <item>Several files: the one the developer picked in Ship (kept as <see cref="Settings.EditorProjectSettings.ProcessName"/>);
    /// with no pick, or a pick the game no longer launches, Build waits for one (<see cref="NeedsPick"/>).</item>
    /// <item>None (no startup command found, or one the plugin cannot check): the project's product name, said plainly.</item>
    /// </list>
    /// </summary>
    public sealed class BuildExecutableChoice
    {
        private BuildExecutableChoice(string executable, string line, IReadOnlyList<StartupCandidate> choices)
        {
            Executable = executable;
            Line = line;
            Choices = choices ?? Array.Empty<StartupCandidate>();
        }

        /// <summary>The path inside the build the build writes (<c>BeaconRushServer.x86_64</c>), or null while a pick is needed.</summary>
        public string Executable { get; }

        /// <summary>The Build row's line: what the build writes and why, or what to pick.</summary>
        public string Line { get; }

        /// <summary>The files to pick from when the game launches several; empty otherwise.</summary>
        public IReadOnlyList<StartupCandidate> Choices { get; }

        /// <summary>The game launches several files and none is picked: Build does not run.</summary>
        public bool NeedsPick => Executable == null;

        /// <summary>The Build row's line when nothing names the file: the project's product name, and what to check.</summary>
        public static string NoStartupCommandLine(string fallback) => $"No startup command found for this game; the executable is named {fallback}. Make sure your game's startup command launches that file.";

        /// <summary>
        /// The decision for <paramref name="check"/> (null: the startup command was not read, for example signed out), the
        /// picked process name (<paramref name="picked"/>, as the panel writes it, or null) and the project's product name.
        /// </summary>
        public static BuildExecutableChoice Decide(StartupCheck check, string picked, string productName)
        {
            string fallback = ServerBuildOptions.ExecutableFromProduct(productName);
            if (check?.Problem != null)
            {
                return new BuildExecutableChoice(fallback, $"The build writes {fallback} (the project's product name): {check.Problem}", null);
            }

            if (check == null || check.Candidates.Count == 0)
            {
                return new BuildExecutableChoice(fallback, NoStartupCommandLine(fallback) + (string.IsNullOrEmpty(check?.Skipped) ? string.Empty : " " + check.Skipped), null);
            }

            if (check.Candidates.Count == 1)
            {
                StartupExecutable only = check.Candidates[0].Executable;
                return Writes(check.Candidates[0], fallback, path => $"The build writes {path}, the file your game launches ({only.Written}).", null);
            }

            string pickedPath = PickedPath(picked);
            StartupCandidate chosen = pickedPath == null ? null : check.Candidates.FirstOrDefault(c => string.Equals(c.Executable.RelativePath, pickedPath, StringComparison.Ordinal));
            string list = string.Join(", ", check.Candidates.Select(c => c.Label()));
            if (chosen == null)
            {
                string again = pickedPath == null ? string.Empty : $" The file you picked, ./{pickedPath}, is no longer one of them.";
                return new BuildExecutableChoice(null, $"Your game launches different files ({list}).{again} Pick the one this build writes under Server executable, then press Build.", check.Candidates);
            }

            string push = check.AnyOf ? " Push accepts a build holding any one of them." : " Push needs every one of them in the build.";
            return Writes(chosen, fallback, path => $"The build writes {path}, the file you picked of the ones your game launches ({list}).{push}", check.Candidates);
        }

        /// <summary>
        /// The process name to keep for a picked file: its written form, quoted when it holds a space or tab, so
        /// <see cref="PickedPath"/> reads it back as the same file (<c>"./My Game.x86_64"</c>, not <c>./My</c>).
        /// </summary>
        public static string ProcessNameFor(StartupExecutable executable)
        {
            string written = executable?.Written ?? string.Empty;
            return written.IndexOfAny(new[] { ' ', '\t' }) >= 0 ? "\"" + written + "\"" : written;
        }

        /// <summary>The path inside the build a picked process name launches, or null for no pick or a name the plugin cannot check.</summary>
        public static string PickedPath(string picked)
        {
            return string.IsNullOrWhiteSpace(picked) ? null : StartupCommand.Parse(picked, out _)?.RelativePath;
        }

        // A file the build can write as named (a plain path inside the build), else the product name and why.
        private static BuildExecutableChoice Writes(StartupCandidate candidate, string fallback, Func<string, string> line, IReadOnlyList<StartupCandidate> choices)
        {
            StartupExecutable exe = candidate.Executable;
            if (ServerBuildOptions.ExecutableProblem(exe.RelativePath) != null)
            {
                return new BuildExecutableChoice(fallback, $"The build writes {fallback} (the project's product name): {exe.Written} is not a plain path inside a build. Make sure your game's startup command launches {fallback}.", choices);
            }

            return new BuildExecutableChoice(exe.RelativePath, line(exe.RelativePath), choices);
        }
    }
}
