using System;
using System.Collections.Generic;
using System.Linq;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// The Push check's startup command: the files a build must hold (all of them, or any one), or a skip with its
    /// reason (Push still runs, without the file check), or a refusal (Push does not run). Exactly one of
    /// <see cref="Candidates"/> (non-empty), <see cref="Skipped"/> and <see cref="Problem"/> is set.
    /// </summary>
    public sealed class StartupCheck
    {
        private StartupCheck(IReadOnlyList<StartupCandidate> candidates, bool anyOf, string skipped, string problem, string note)
        {
            Candidates = candidates ?? Array.Empty<StartupCandidate>();
            AnyOf = anyOf;
            Skipped = skipped;
            Problem = problem;
            Note = note;
        }

        /// <summary>Each distinct file the game launches, with who launches it; empty unless the check was found.</summary>
        public IReadOnlyList<StartupCandidate> Candidates { get; }

        /// <summary>
        /// True when the build needs only ONE of <see cref="Candidates"/> (the game's template sets, with no deployment to
        /// say which is used); false when it needs every one (the fleet's deployments all run the build Push uploads).
        /// </summary>
        public bool AnyOf { get; }

        /// <summary>The one file the game launches, or null (none found, or several).</summary>
        public StartupExecutable Executable => Candidates.Count == 1 ? Candidates[0].Executable : null;

        /// <summary>What the push check needs, or null when nothing was found.</summary>
        public StartupFiles Files => Candidates.Count == 0 ? null : new StartupFiles(Candidates.Select(c => c.Executable.RelativePath), AnyOf);

        /// <summary>Why the check was skipped ("The startup-command check was skipped: ..."), or null.</summary>
        public string Skipped { get; }

        /// <summary>Why Push must not run, or null.</summary>
        public string Problem { get; }

        /// <summary>What a found check did not cover ("Not checked: ..."), or null.</summary>
        public string Note { get; }

        /// <summary>A check of exactly one file (no source named).</summary>
        public static StartupCheck Found(StartupExecutable executable) => Found(new[] { new StartupCandidate(executable ?? throw new ArgumentNullException(nameof(executable)), Array.Empty<string>()) }, false, null);

        /// <summary>A check of <paramref name="candidates"/> (at least one): all of them, or with <paramref name="anyOf"/> any one.</summary>
        public static StartupCheck Found(IReadOnlyList<StartupCandidate> candidates, bool anyOf, string note)
        {
            if (candidates == null || candidates.Count == 0)
            {
                throw new ArgumentException("A found check names at least one file.", nameof(candidates));
            }

            return new StartupCheck(candidates, anyOf, null, null, note);
        }

        /// <summary>A skip; <paramref name="why"/> completes "The startup-command check was skipped: ".</summary>
        public static StartupCheck Skip(string why) => new StartupCheck(null, false, SkippedPrefix + why, null, null);

        public static StartupCheck Refused(string problem) => new StartupCheck(null, false, null, problem, null);

        /// <summary>How every skip sentence starts.</summary>
        public const string SkippedPrefix = "The startup-command check was skipped: ";

        /// <summary>How a found check's note starts.</summary>
        public const string NotCheckedPrefix = "Not checked: ";

        /// <summary>
        /// The line the Push row adds before it runs: which file or files the build must hold and who launches each
        /// ("Your fleet's deployments launch different files: ./A (deployment eu-1), ./B (deployment us-1); the build must
        /// hold each."), plus <see cref="Note"/>; null for a single file with no note, or when nothing was found.
        /// </summary>
        public string Describe()
        {
            string line = null;
            if (Candidates.Count > 1)
            {
                string list = string.Join(", ", Candidates.Select(c => c.Label()));
                line = AnyOf
                    ? $"Your game's template sets launch different files: {list}; the build must hold one of them."
                    : $"Your fleet's deployments launch different files: {list}; the build must hold each.";
            }

            if (Note == null)
            {
                return line;
            }

            return line == null ? Note : line + " " + Note;
        }

        /// <summary>
        /// For a check of any one of several files: "The build holds ./B, which template set Linux launches." for the
        /// first candidate <paramref name="files"/> (paths inside the build) hold, compared as Linux does; else null.
        /// </summary>
        public string MatchedLine(IEnumerable<string> files)
        {
            if (!AnyOf || Candidates.Count < 2)
            {
                return null;
            }

            HashSet<string> present = StartupCommand.Normalized(files);
            StartupCandidate hit = Candidates.FirstOrDefault(c => present.Contains(c.Executable.RelativePath));
            return hit == null ? null : $"The build holds {hit.Executable.Written}, which {string.Join(" and ", hit.Sources)} launches.";
        }
    }

    /// <summary>One file the game launches and who launches it (<c>deployment eu-1</c>, <c>template set Default</c>).</summary>
    public sealed class StartupCandidate
    {
        public StartupCandidate(StartupExecutable executable, IReadOnlyList<string> sources)
        {
            Executable = executable ?? throw new ArgumentNullException(nameof(executable));
            Sources = sources ?? Array.Empty<string>();
        }

        public StartupExecutable Executable { get; }

        /// <summary>Who launches it, in reading order.</summary>
        public IReadOnlyList<string> Sources { get; }

        /// <summary><c>./A (deployment eu-1, deployment us-1)</c>, or the file alone when no source is named.</summary>
        public string Label() => Sources.Count == 0 ? Executable.Written : $"{Executable.Written} ({string.Join(", ", Sources)})";
    }

    /// <summary>
    /// The files the push check requires inside the build (paths inside it, forward slashes): every one, or with
    /// <see cref="AnyOf"/> at least one. Paths only, so a run's saved state can carry it.
    /// </summary>
    public sealed class StartupFiles
    {
        public StartupFiles(IEnumerable<string> paths, bool anyOf)
        {
            Paths = (paths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
            AnyOf = anyOf;
        }

        public IReadOnlyList<string> Paths { get; }

        public bool AnyOf { get; }

        /// <summary>The build must hold <paramref name="path"/>.</summary>
        public static StartupFiles One(string path) => new StartupFiles(new[] { path }, false);
    }

    /// <summary>The file a game's startup command launches.</summary>
    public sealed class StartupExecutable
    {
        public StartupExecutable(string written, string relativePath)
        {
            Written = written;
            RelativePath = relativePath;
        }

        /// <summary>The command's first word as the panel holds it, quotes removed (<c>./BeaconRush.x86_64</c>).</summary>
        public string Written { get; }

        /// <summary>The same file as a path inside the build, forward slashes, no leading <c>./</c> (<c>BeaconRush.x86_64</c>).</summary>
        public string RelativePath { get; }

        public override string ToString() => Written;
    }

    /// <summary>
    /// Where a startup command comes from, as the reads answered: a member deployment (<c>deployment eu-1</c>) or one of
    /// the game's template sets (<c>template set Default</c>), with its template set, or why it has none.
    /// </summary>
    public sealed class StartupSource
    {
        /// <summary>Who this is in a sentence: <c>deployment eu-1</c>, <c>template set Default</c>.</summary>
        public string Label { get; set; }

        /// <summary>Its template set, or null when it has none or it could not be read.</summary>
        public Api.Wire.TemplateSetResponse TemplateSet { get; set; }

        /// <summary>
        /// Why <see cref="TemplateSet"/> is null, or null: for a deployment a phrase after its label ("its deployment spec
        /// #5801 names no template set"), for one of the game's template sets a whole phrase ("template set Default no
        /// longer exists").
        /// </summary>
        public string Unresolved { get; set; }
    }
}
